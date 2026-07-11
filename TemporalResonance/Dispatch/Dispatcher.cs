using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace TemporalResonance;

/// <summary>
/// The arbitration core, ported from the Darktide mod's ARCHITECTURE.md.
/// Single entry point Dispatch(hookId, scale): scale null = event (full
/// strength, once); scale 0..1 = poll sample. All arbitration lives here
/// because the hardware can't mediate between competing sources, doesn't
/// resume interrupted commands, and always replaces running ones.
/// The gate ordering and the poll/event asymmetries (gap exemption, hold
/// immunity) are each load-bearing — see comments at each step.
/// </summary>
public class Dispatcher
{
    const long GlobalMinGapMs = 100;
    const float ChangeEpsilon = 0.03f;
    const long PanicSuppressMs = 20_000;

    readonly ICoreClientAPI capi;
    readonly HookRegistry hooks;
    readonly ButtplugManager devices;
    readonly PatternPlayer player;
    readonly PresetStore store;

    readonly Dictionary<string, long> lastFireMs = new();
    /// Last scale actually SENT (not last observed) — the epsilon gate compares against this.
    readonly Dictionary<string, float> lastScale = new();
    /// Last scale OBSERVED per poll hook, even if gated — used to proactively
    /// re-assert a poll the moment an event hold expires.
    readonly Dictionary<string, float> lastObserved = new();
    readonly Dictionary<string, long> holdUntilMs = new();   // deviceKey -> claimed until
    readonly HashSet<string> pendingReassert = new();        // hookIds owed a re-send after a hold
    long lastAnyFireMs;
    long suppressUntilMs;                                    // panic window: no dispatches at all

    public Dispatcher(ICoreClientAPI capi, HookRegistry hooks, ButtplugManager devices,
                      PatternPlayer player, PresetStore store)
    {
        this.capi = capi;
        this.hooks = hooks;
        this.devices = devices;
        this.player = player;
        this.store = store;
    }

    public void Dispatch(string hookId, float? scale = null)
    {
        var hook = hooks.Get(hookId);
        if (hook == null) return;

        // Poll/event is decided by the hook's registered kind, not by whether
        // a scale was passed — events may carry a scale too (e.g. footstep
        // loudness) and it must not buy them poll semantics.
        var isPoll = hook.Kind == HookKind.Poll;
        if (isPoll && scale == null) return;
        var now = Now();

        // Panic window: drop EVERYTHING, polls and events alike — the user hit
        // panic because they want silence, and a poll's next sample would
        // otherwise undo the stop within one interval.
        if (now < suppressUntilMs) return;

        if (isPoll) lastObserved[hookId] = scale!.Value;

        // 1. Per-hook cooldown.
        if (lastFireMs.TryGetValue(hookId, out var last) && (now - last) < hook.CooldownSec * 1000) return;

        // 2. Global minimum gap — POLLS ONLY. Events are rare, cooldown-protected,
        //    and dropping one loses it forever
        if (isPoll && now - lastAnyFireMs < GlobalMinGapMs) return;

        // 3. Change epsilon — POLLS ONLY; bypassed while this hook owes a
        //    reassert (hardware doesn't resume, so the level must be re-sent
        //    after a hold even if it hasn't moved).
        if (isPoll && !pendingReassert.Contains(hookId)
            && lastScale.TryGetValue(hookId, out var prev) && Math.Abs(scale!.Value - prev) < ChangeEpsilon) return;

        // 4. Resolve targets: connected devices with an existing preset only —
        //    stale device entries error when commanded.
        var targets = new List<(TrDevice dev, Preset preset, bool inverted)>();
        var assignedConnected = 0;
        foreach (var (deviceKey, presetId) in store.AssignmentsFor(hookId))
        {
            var dev = devices.FindByKey(deviceKey);
            var preset = store.GetPreset(presetId);
            if (dev == null || preset == null) continue;
            assignedConnected++;

            // 5. Hold check — POLLS ONLY: a timed event burst owns the device.
            //    Events ignore holds entirely (a burst may interrupt anything;
            //    it just also claims the device for itself in step 9).
            if (isPoll && holdUntilMs.TryGetValue(deviceKey, out var hold) && hold > now)
                continue;

            targets.Add((dev, preset, isPoll && store.IsInverted(hookId, deviceKey)));
        }

        if (isPoll)
        {
            if (targets.Count < assignedConnected) pendingReassert.Add(hookId);
            else pendingReassert.Remove(hookId);
        }

        if (targets.Count == 0) return;

        // 6. Group by (preset, inverted) — with Buttplug sends being per-device
        //    this only computes the effective scale once per group.
        // 7. Send. The Lua design's interrupt-flag asymmetry has no Buttplug
        //    analogue: PatternPlayer's generation replacement gives replace
        //    semantics unconditionally.
        // 8. Batch fallback — N/A: Buttplug commands are per-device already.
        foreach (var group in targets.GroupBy(t => (t.preset.Id, t.inverted)))
        {
            var inverted = group.Key.inverted;
            // Events: optional scale multiplies intensity (default full).
            // Polls: scale is the level, inversion may flip it.
            var effScale = scale == null ? 1.0 : (inverted ? 1 - scale.Value : scale.Value);
            foreach (var (dev, preset, _) in group)
            {
                player.Play(dev, preset, effScale);

                // 9. Claim holds: timed event bursts own their devices. Plain
                //    assignment, never Max() — the newest burst physically
                //    replaced the previous one, so its window must too.
                //    The reassert debt is charged HERE for EVERY event send
                //    (not only at the hold check) — that's load-bearing twice
                //    over: (a) an unchanged poll is dropped at the epsilon
                //    gate before it ever discovers a hold, so it would stay
                //    silent until its value drifted past the epsilon; and
                //    (b) an infinite-duration event claims no hold, so the
                //    debt is what lets the poll's very next sample displace it.
                if (!isPoll)
                {
                    if (preset.DurationSec > 0)
                    {
                        var holdUntil = now + (long)(preset.DurationSec * 1000);
                        holdUntilMs[dev.Key] = holdUntil;
                        ScheduleHoldExpiryReassert(dev.Key, holdUntil);
                    }

                    foreach (var pollHook in hooks.All)
                    {
                        if (pollHook.Kind == HookKind.Poll && pollHook.Id != hookId
                            && store.AssignmentsFor(pollHook.Id).ContainsKey(dev.Key))
                        {
                            pendingReassert.Add(pollHook.Id);
                        }
                    }
                }
            }
        }

        // 10. Bookkeeping on successful send.
        lastFireMs[hookId] = now;
        lastAnyFireMs = now;
        if (isPoll) lastScale[hookId] = scale!.Value;
    }

    /// <summary>
    /// Re-assert affected polls the moment a hold expires instead of waiting
    /// for their next natural sample (which lags by up to the poll interval).
    /// The callback no-ops if a newer burst replaced this hold in the meantime
    /// — that burst scheduled its own expiry callback.
    /// </summary>
    void ScheduleHoldExpiryReassert(string deviceKey, long holdUntil)
    {
        capi.Event.RegisterCallback(_ =>
        {
            if (!holdUntilMs.TryGetValue(deviceKey, out var current) || current != holdUntil) return;
            holdUntilMs.Remove(deviceKey);

            foreach (var pollHook in hooks.All)
            {
                if (pollHook.Kind != HookKind.Poll) continue;
                if (!store.AssignmentsFor(pollHook.Id).ContainsKey(deviceKey)) continue;
                if (!lastObserved.TryGetValue(pollHook.Id, out var value)) continue;

                pendingReassert.Add(pollHook.Id);
                Dispatch(pollHook.Id, value);
            }
        }, (int)Math.Max(1, holdUntil - Now()) + 50);
    }

    /// <summary>
    /// Zero only this hook's output: re-play its assigned presets at scale 0 on
    /// its connected assigned devices. Deliberately not a device-wide stop —
    /// other hooks driving the same device are untouched. No-op unless the
    /// hook has previously sent.
    /// </summary>
    public void StopHook(string hookId)
    {
        if (!lastFireMs.ContainsKey(hookId)) return;

        foreach (var (deviceKey, presetId) in store.AssignmentsFor(hookId))
        {
            var dev = devices.FindByKey(deviceKey);
            var preset = store.GetPreset(presetId);
            if (dev == null || preset == null) continue;
            player.Play(dev, preset, 0);
        }
        lastScale.Remove(hookId);
        lastObserved.Remove(hookId); // don't resurrect a stopped source at hold expiry
        pendingReassert.Remove(hookId);
    }

    /// User-facing panic: full reset plus a suppression window so polls can't
    /// re-assert themselves one sample later and undo the stop.
    public void Panic()
    {
        Reset();
        suppressUntilMs = Now() + PanicSuppressMs;
    }

    /// Clear all state (so the next session's first dispatch always sends) and
    /// stop everything. Session end teardown — no suppression window, so
    /// joining the next world starts clean and responsive.
    public void Reset()
    {
        suppressUntilMs = 0;
        lastFireMs.Clear();
        lastScale.Clear();
        lastObserved.Clear();
        holdUntilMs.Clear();
        pendingReassert.Clear();
        lastAnyFireMs = 0;
        player.StopAll();
        devices.RunAsync(() => devices.StopAllAsync(), "stop all");
    }

    public IReadOnlyDictionary<string, long> HoldsUntilMs => holdUntilMs;
    public long NowMs => Now();

    long Now() => capi.ElapsedMilliseconds;
}
