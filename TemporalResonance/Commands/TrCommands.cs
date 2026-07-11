using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TemporalResonance;

/// <summary>
/// The whole .tr chat command surface: connection management, plus commands
/// that exercise each layer (presets, direct playback, assignments, test
/// fires) until the real UI exists.
/// </summary>
public class TrCommands
{
    readonly ICoreClientAPI capi;
    readonly ButtplugManager devices;
    readonly PatternPlayer player;
    readonly PresetStore store;
    readonly HookRegistry hooks;
    readonly Dispatcher dispatcher;

    /// Value fed to the debug-poll sampler; null = source unavailable.
    public float? DebugPollValue { get; private set; }

    /// Set by the ModSystem; opens/toggles the settings dialog.
    public Action? OpenGui { get; init; }

    public TrCommands(ICoreClientAPI capi, ButtplugManager devices, PatternPlayer player,
                      PresetStore store, HookRegistry hooks, Dispatcher dispatcher)
    {
        this.capi = capi;
        this.devices = devices;
        this.player = player;
        this.store = store;
        this.hooks = hooks;
        this.dispatcher = dispatcher;
    }

    public void Register()
    {
        var parsers = capi.ChatCommands.Parsers;

        var root = capi.ChatCommands.Create("tr")
            .WithDescription("Temporal Resonance device commands");

        // ---- Connection ----

        root.BeginSubCommand("connect")
            .WithDescription($"Connect to Intiface Central (default {ButtplugManager.DefaultUrl})")
            .WithArgs(parsers.OptionalWord("url"))
            .HandleWith(args =>
            {
                var url = args.Parsers[0].IsMissing ? store.Config.ServerUrl : (string)args[0];
                devices.RunAsync(() => devices.ConnectAsync(url), "connect");
                return TextCommandResult.Success($"Connecting to {url}...");
            })
        .EndSubCommand()
        .BeginSubCommand("disconnect")
            .WithDescription("Disconnect from Intiface Central")
            .HandleWith(_ =>
            {
                if (!devices.Connected) return TextCommandResult.Error("Not connected.");
                devices.RunAsync(() => devices.DisconnectAsync(), "disconnect");
                return TextCommandResult.Success("Disconnecting...");
            })
        .EndSubCommand()
        .BeginSubCommand("scan")
            .WithDescription("Start scanning for devices")
            .HandleWith(_ => WhenConnected(() =>
            {
                devices.RunAsync(() => devices.StartScanningAsync(), "scan");
                return TextCommandResult.Success("Scanning for devices...");
            }))
        .EndSubCommand()
        .BeginSubCommand("stopscan")
            .WithDescription("Stop scanning for devices")
            .HandleWith(_ => WhenConnected(() =>
            {
                devices.RunAsync(() => devices.StopScanningAsync(), "stopscan");
                return TextCommandResult.Success("Stopping scan...");
            }))
        .EndSubCommand()
        .BeginSubCommand("devices")
            .WithDescription("List connected devices")
            .HandleWith(_ => WhenConnected(() =>
            {
                if (devices.Devices.Count == 0) return TextCommandResult.Success("No devices connected. Try .tr scan");
                var sb = new StringBuilder("Devices:");
                foreach (var d in devices.Devices)
                    sb.Append($"\n{d.Name} — key: {d.Key}, vibrate: {(d.CanVibrate ? "yes" : "no")}");
                return TextCommandResult.Success(sb.ToString());
            }))
        .EndSubCommand()
        .BeginSubCommand("vibrate")
            .WithDescription("Set vibration directly: .tr vibrate <devicekey> <percent 0-100>")
            .WithArgs(parsers.Word("devicekey"), parsers.Int("percent"))
            .HandleWith(args => WhenConnected(() =>
            {
                var dev = FindDevice((string)args[0], out var err);
                if (dev == null) return err!;
                if (!dev.CanVibrate) return TextCommandResult.Error($"{dev.Name} cannot vibrate.");
                var percent = Math.Clamp((int)args[1], 0, 100);
                devices.RunAsync(() => devices.SetVibrateAsync(dev, percent / 100.0), "vibrate");
                return TextCommandResult.Success($"{dev.Key}: vibrate {percent}%");
            }))
        .EndSubCommand()
        .BeginSubCommand("stop")
            .WithDescription("Stop all devices")
            .HandleWith(_ => WhenConnected(() =>
            {
                player.StopAll();
                return TextCommandResult.Success("Stopping all devices.");
            }))
        .EndSubCommand()
        .BeginSubCommand("panic")
            .WithDescription("Stop everything, reset dispatcher state, suppress triggers for 20s")
            .HandleWith(_ =>
            {
                dispatcher.Panic();
                return TextCommandResult.Success("Panic: all devices stopped, triggers suppressed for 20s.");
            })
        .EndSubCommand()
        .BeginSubCommand("gui")
            .WithDescription("Open the settings dialog (also bound to a hotkey, default O)")
            .HandleWith(_ =>
            {
                OpenGui?.Invoke();
                return TextCommandResult.Success();
            })
        .EndSubCommand()
        .BeginSubCommand("status")
            .WithDescription("Show connection, holds and assignment summary")
            .HandleWith(_ =>
            {
                var sb = new StringBuilder();
                sb.Append(devices.Connected
                    ? $"Connected, {devices.Devices.Count} device(s)"
                    : "Not connected");
                sb.Append($"\nPresets: {store.Config.Presets.Count}, hooks: {hooks.All.Count}");
                var now = dispatcher.NowMs;
                foreach (var (key, until) in dispatcher.HoldsUntilMs.Where(kv => kv.Value > now))
                    sb.Append($"\nHold: {key} for another {(until - now) / 1000.0:0.0}s");
                foreach (var (hookId, perDevice) in store.Config.Assignments.Where(kv => kv.Value.Count > 0))
                    sb.Append($"\n{hookId}: " + string.Join(", ", perDevice.Select(kv => $"{kv.Key} -> {kv.Value}")));
                return TextCommandResult.Success(sb.ToString());
            })
        .EndSubCommand();

        // ---- Presets ----

        root.BeginSubCommand("preset")
            .WithDescription("Manage presets")
            .BeginSubCommand("create")
                .WithDescription(".tr preset create <name> <intensity 0-100> [durationSec] [loopOnSec] [loopOffSec]")
                .WithArgs(parsers.Word("name"), parsers.Int("intensity"),
                          parsers.OptionalDouble("durationsec"), parsers.OptionalDouble("looponsec"), parsers.OptionalDouble("loopoffsec"))
                .HandleWith(args =>
                {
                    var preset = store.CreatePreset((string)args[0]);
                    store.UpdatePreset(preset.Id, p =>
                    {
                        p.Actions["vibrate"] = Math.Clamp((int)args[1], 0, 100) / 100.0;
                        p.DurationSec = args.Parsers[2].IsMissing ? 0 : Math.Max(0, (double)args[2]);
                        p.LoopOnSec = args.Parsers[3].IsMissing ? 0 : Math.Max(0, (double)args[3]);
                        p.LoopOffSec = args.Parsers[4].IsMissing ? 0 : Math.Max(0, (double)args[4]);
                    });
                    return TextCommandResult.Success($"Created preset '{preset.Id}': {Describe(preset)}");
                })
            .EndSubCommand()
            .BeginSubCommand("list")
                .WithDescription("List presets")
                .HandleWith(_ =>
                {
                    if (store.Config.Presets.Count == 0) return TextCommandResult.Success("No presets. Use .tr preset create");
                    var sb = new StringBuilder("Presets:");
                    foreach (var p in store.Config.Presets.Values) sb.Append($"\n{p.Id}: {Describe(p)}");
                    return TextCommandResult.Success(sb.ToString());
                })
            .EndSubCommand()
            .BeginSubCommand("delete")
                .WithDescription(".tr preset delete <id>")
                .WithArgs(parsers.Word("id"))
                .HandleWith(args => store.DeletePreset((string)args[0])
                    ? TextCommandResult.Success($"Deleted preset '{args[0]}' and its assignments.")
                    : TextCommandResult.Error($"No preset '{args[0]}'."))
            .EndSubCommand()
            .BeginSubCommand("edit")
                .WithDescription(".tr preset edit <id> <intensity|duration|loopon|loopoff> <value>")
                .WithArgs(parsers.Word("id"), parsers.Word("field"), parsers.Double("value"))
                .HandleWith(args =>
                {
                    var field = ((string)args[1]).ToLowerInvariant();
                    var value = (double)args[2];
                    var ok = store.UpdatePreset((string)args[0], p =>
                    {
                        switch (field)
                        {
                            case "intensity": p.Actions["vibrate"] = Math.Clamp(value, 0, 100) / 100.0; break;
                            case "duration": p.DurationSec = Math.Max(0, value); break;
                            case "loopon": p.LoopOnSec = Math.Max(0, value); break;
                            case "loopoff": p.LoopOffSec = Math.Max(0, value); break;
                        }
                    });
                    if (!ok) return TextCommandResult.Error($"No preset '{args[0]}'.");
                    if (field is not ("intensity" or "duration" or "loopon" or "loopoff"))
                        return TextCommandResult.Error("Field must be intensity, duration, loopon or loopoff.");
                    return TextCommandResult.Success($"Updated: {Describe(store.GetPreset((string)args[0])!)}");
                })
            .EndSubCommand()
        .EndSubCommand();

        // ---- Playback / assignments / hooks ----

        root.BeginSubCommand("play")
            .WithDescription("Play a preset directly: .tr play <devicekey> <presetid>")
            .WithArgs(parsers.Word("devicekey"), parsers.Word("presetid"))
            .HandleWith(args => WhenConnected(() =>
            {
                var dev = FindDevice((string)args[0], out var err);
                if (dev == null) return err!;
                var preset = store.GetPreset((string)args[1]);
                if (preset == null) return TextCommandResult.Error($"No preset '{args[1]}'.");
                player.Play(dev, preset);
                return TextCommandResult.Success($"Playing '{preset.Id}' on {dev.Key}: {Describe(preset)}");
            }))
        .EndSubCommand()
        .BeginSubCommand("assign")
            .WithDescription(".tr assign <hookid> <devicekey> <presetid>")
            .WithArgs(parsers.Word("hookid"), parsers.Word("devicekey"), parsers.Word("presetid"))
            .HandleWith(args =>
            {
                if (hooks.Get((string)args[0]) == null) return TextCommandResult.Error($"No hook '{args[0]}'. Use .tr hooks");
                if (store.GetPreset((string)args[2]) == null) return TextCommandResult.Error($"No preset '{args[2]}'.");
                store.Assign((string)args[0], (string)args[1], (string)args[2]);
                return TextCommandResult.Success($"{args[0]} + {args[1]} -> {args[2]}");
            })
        .EndSubCommand()
        .BeginSubCommand("unassign")
            .WithDescription(".tr unassign <hookid> <devicekey>")
            .WithArgs(parsers.Word("hookid"), parsers.Word("devicekey"))
            .HandleWith(args =>
            {
                var hookId = (string)args[0];
                var deviceKey = (string)args[1];
                // Silence what this assignment last sent before removing it
                if (store.AssignmentsFor(hookId).TryGetValue(deviceKey, out var oldPresetId)
                    && store.GetPreset(oldPresetId) is { } oldPreset
                    && devices.FindByKey(deviceKey) is { } dev)
                {
                    player.Play(dev, oldPreset, 0);
                }
                return store.Unassign(hookId, deviceKey)
                    ? TextCommandResult.Success($"Unassigned {deviceKey} from {hookId}.")
                    : TextCommandResult.Error("No such assignment.");
            })
        .EndSubCommand()
        .BeginSubCommand("invert")
            .WithDescription(".tr invert <hookid> <devicekey> <on|off> — poll hooks send 1-scale to this device")
            .WithArgs(parsers.Word("hookid"), parsers.Word("devicekey"), parsers.WordRange("state", "on", "off"))
            .HandleWith(args =>
            {
                store.SetInverted((string)args[0], (string)args[1], (string)args[2] == "on");
                return TextCommandResult.Success($"Inversion {(string)args[2]} for {args[1]} on {args[0]}.");
            })
        .EndSubCommand()
        .BeginSubCommand("hooks")
            .WithDescription("List registered hooks and their assignments")
            .HandleWith(_ =>
            {
                var sb = new StringBuilder("Hooks:");
                foreach (var h in hooks.All)
                {
                    sb.Append($"\n{h.Id} ({h.Kind}, cooldown {h.CooldownSec}s{(h.Kind == HookKind.Poll ? $", every {h.PollIntervalSec}s" : "")}) — {h.DisplayName}");
                    foreach (var (deviceKey, presetId) in store.AssignmentsFor(h.Id))
                        sb.Append($"\n    {deviceKey} -> {presetId}{(store.IsInverted(h.Id, deviceKey) ? " (inverted)" : "")}");
                }
                return TextCommandResult.Success(sb.ToString());
            })
        .EndSubCommand()
        .BeginSubCommand("fire")
            .WithDescription("Test-fire a hook through the dispatcher: .tr fire <hookid> [scale 0-100]")
            .WithArgs(parsers.Word("hookid"), parsers.OptionalInt("scale"))
            .HandleWith(args =>
            {
                var hook = hooks.Get((string)args[0]);
                if (hook == null) return TextCommandResult.Error($"No hook '{args[0]}'. Use .tr hooks");
                float? scale = args.Parsers[1].IsMissing ? null : Math.Clamp((int)args[1], 0, 100) / 100f;
                dispatcher.Dispatch(hook.Id, scale);
                return TextCommandResult.Success($"Dispatched {hook.Id}{(scale != null ? $" at {scale:0.00}" : " (event)")}.");
            })
        .EndSubCommand()
        .BeginSubCommand("debugpoll")
            .WithDescription("Set the debug-poll sampler value: .tr debugpoll <0-100|nil>")
            .WithArgs(parsers.Word("value"))
            .HandleWith(args =>
            {
                var raw = (string)args[0];
                if (raw.Equals("nil", StringComparison.OrdinalIgnoreCase))
                {
                    DebugPollValue = null;
                    return TextCommandResult.Success("debug-poll source now unavailable (nil).");
                }
                if (!int.TryParse(raw, out var v)) return TextCommandResult.Error("Value must be 0-100 or nil.");
                DebugPollValue = Math.Clamp(v, 0, 100) / 100f;
                return TextCommandResult.Success($"debug-poll now sampling {DebugPollValue:0.00}.");
            })
        .EndSubCommand();
    }

    static string Describe(Preset p)
    {
        var intensity = p.Actions.TryGetValue("vibrate", out var i) ? $"{i * 100:0}%" : "no actions";
        var duration = p.DurationSec > 0 ? $"{p.DurationSec}s" : "infinite";
        var loop = p.LoopOnSec > 0 && p.LoopOffSec > 0 ? $", loop {p.LoopOnSec}s on / {p.LoopOffSec}s off" : "";
        return $"{intensity} for {duration}{loop}";
    }

    TrDevice? FindDevice(string key, out TextCommandResult? error)
    {
        var dev = devices.FindByKey(key);
        error = dev == null ? TextCommandResult.Error($"No connected device '{key}'. Use .tr devices") : null;
        return dev;
    }

    TextCommandResult WhenConnected(Func<TextCommandResult> handler)
        => devices.Connected ? handler() : TextCommandResult.Error("Not connected. Use .tr connect first.");
}
