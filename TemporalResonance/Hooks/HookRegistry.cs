using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace TemporalResonance;

/// <summary>
/// Registry of all hooks plus the poll driver. One game tick listener
/// accumulates elapsed time per poll hook and fires each sampler at its own
/// interval. A sampler returning null 3 consecutive times triggers a one-shot
/// StopHook so the device doesn't hold its last level forever after the
/// source disappears (death, leaving world, ...).
/// </summary>
public class HookRegistry
{
    public const int NilStreakLimit = 3;

    readonly Dictionary<string, HookDescriptor> hooks = new();
    readonly Dictionary<string, double> elapsed = new();   // poll hookId -> seconds since last sample
    readonly Dictionary<string, int> nilStreak = new();

    ICoreClientAPI? capi;
    Dispatcher? dispatcher;
    long tickListenerId;

    public void Register(HookDescriptor d) => hooks[d.Id] = d;

    public HookDescriptor? Get(string id) => hooks.TryGetValue(id, out var d) ? d : null;

    public IReadOnlyCollection<HookDescriptor> All => hooks.Values;

    public void StartPolling(ICoreClientAPI capi, Dispatcher dispatcher)
    {
        this.capi = capi;
        this.dispatcher = dispatcher;
        tickListenerId = capi.Event.RegisterGameTickListener(OnTick, 50);
    }

    public void StopPolling()
    {
        if (capi != null) capi.Event.UnregisterGameTickListener(tickListenerId);
        elapsed.Clear();
        nilStreak.Clear();
    }

    void OnTick(float dt)
    {
        foreach (var hook in hooks.Values.Where(h => h.Kind == HookKind.Poll && h.Sampler != null))
        {
            var t = elapsed.GetValueOrDefault(hook.Id) + dt;
            if (t < hook.PollIntervalSec)
            {
                elapsed[hook.Id] = t;
                continue;
            }
            elapsed[hook.Id] = 0;

            var value = hook.Sampler!();
            if (value == null)
            {
                var streak = nilStreak.GetValueOrDefault(hook.Id) + 1;
                nilStreak[hook.Id] = streak;
                if (streak == NilStreakLimit) dispatcher?.StopHook(hook.Id);
            }
            else
            {
                nilStreak[hook.Id] = 0;
                dispatcher?.Dispatch(hook.Id, value);
            }
        }
    }
}
