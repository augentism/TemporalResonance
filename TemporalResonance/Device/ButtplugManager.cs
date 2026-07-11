using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Buttplug.Client;
using Buttplug.Core.Messages;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TemporalResonance;

/// <summary>
/// A connected device with a stable key. Buttplug's Index is a per-session
/// handle, so persisted config (assignments) is keyed by Key: the device name,
/// disambiguated as "Name#N" when several same-name devices are connected.
/// </summary>
public record TrDevice(string Key, string Name, ButtplugClientDevice Raw, bool CanVibrate);

/// <summary>
/// Owns the Buttplug client: connection lifecycle, a connected-only device
/// cache with stable keys, and raw fire-and-forget sends. All events are
/// marshalled to the client main thread before touching game API or state.
/// </summary>
public class ButtplugManager
{
    public const string DefaultUrl = "ws://localhost:12345";

    readonly ICoreClientAPI capi;
    readonly ILogger logger;
    ButtplugClient? client;
    List<TrDevice> devices = new();

    /// Raised on the main thread whenever the connected-device cache changes.
    public event Action? DevicesChanged;
    /// Raised on the main thread for user-facing status messages.
    public event Action<string>? Message;

    public ButtplugManager(ICoreClientAPI capi, ILogger logger)
    {
        this.capi = capi;
        this.logger = logger;
    }

    public bool Connected => client is { Connected: true };

    /// Connected devices only — this cache is the "filter stale devices" rule:
    /// anything not in here must never be commanded.
    public IReadOnlyList<TrDevice> Devices => devices;

    public TrDevice? FindByKey(string deviceKey) => devices.FirstOrDefault(d => d.Key == deviceKey);

    public async Task ConnectAsync(string url)
    {
        // ButtplugClient instances are safest treated as single-use: fresh client per connection
        if (client != null)
        {
            if (client.Connected) await client.DisconnectAsync();
            client.Dispose();
        }

        client = CreateClient();
        await client.ConnectAsync(new ButtplugWebsocketConnector(new Uri(url)));

        OnMainThread(() =>
        {
            RebuildCache();
            Message?.Invoke($"Connected to Intiface. {devices.Count} device(s) already known. Use .tr scan to find more.");
        });
    }

    public Task DisconnectAsync() => client is { Connected: true } c ? c.DisconnectAsync() : Task.CompletedTask;
    public Task StartScanningAsync() => Client().StartScanningAsync();
    public Task StopScanningAsync() => Client().StopScanningAsync();
    public Task StopAllAsync() => client is { Connected: true } c ? c.StopAllDevicesAsync() : Task.CompletedTask;

    public Task SetVibrateAsync(TrDevice dev, double intensity)
        => dev.Raw.RunOutputAsync(DeviceOutput.Vibrate.Percent(Math.Clamp(intensity, 0, 1)), default);

    /// Fire-and-forget with error reporting into chat/log instead of a swallowed task exception
    public async void RunAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            OnMainThread(() =>
            {
                Message?.Invoke($"{what} failed: {ex.Message}");
                logger.Error("Buttplug {0} failed: {1}", what, ex);
            });
        }
    }

    ButtplugClient Client() => client is { Connected: true } c
        ? c
        : throw new InvalidOperationException("Not connected. Use .tr connect first.");

    ButtplugClient CreateClient()
    {
        var c = new ButtplugClient("TemporalResonance");

        // Library events fire on background threads; hop to the main thread before touching the API
        c.DeviceAdded += (_, e) => OnMainThread(() =>
        {
            RebuildCache();
            Message?.Invoke($"Device added: {e.Device.Name}");
        });
        c.DeviceRemoved += (_, e) => OnMainThread(() =>
        {
            RebuildCache();
            Message?.Invoke($"Device removed: {e.Device.Name}");
        });
        c.ScanningFinished += (_, _) => OnMainThread(() => Message?.Invoke("Scanning finished."));
        c.ServerDisconnect += (_, _) => OnMainThread(() =>
        {
            RebuildCache();
            Message?.Invoke("Intiface server disconnected.");
        });
        c.ErrorReceived += (_, e) => OnMainThread(() =>
        {
            Message?.Invoke($"Error from server: {e.Exception.Message}");
            logger.Error("Buttplug error: {0}", e.Exception);
        });

        return c;
    }

    void RebuildCache()
    {
        var fresh = new List<TrDevice>();
        if (client is { Connected: true } c)
        {
            var nameCounts = new Dictionary<string, int>();
            foreach (var raw in c.Devices.OrderBy(d => d.Index))
            {
                var slug = Slugify(raw.Name);
                var n = nameCounts.TryGetValue(slug, out var seen) ? seen : 0;
                nameCounts[slug] = n + 1;
                var key = n == 0 ? slug : $"{slug}#{n}";
                fresh.Add(new TrDevice(key, raw.Name, raw, raw.HasOutput(OutputType.Vibrate)));
            }
        }
        devices = fresh;
        DevicesChanged?.Invoke();
    }

    /// Device names contain spaces ("Lovense Solace Pro"), which chat command
    /// word parsers can't accept — keys are lowercase-dashed instead.
    static string Slugify(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return slug.Length == 0 ? "device" : slug;
    }

    void OnMainThread(Action action) => capi.Event.EnqueueMainThreadTask(action, "temporalresonance");

    public void Dispose()
    {
        client?.Dispose();
        client = null;
        devices = new List<TrDevice>();
    }
}
