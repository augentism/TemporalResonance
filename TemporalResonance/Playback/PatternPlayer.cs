using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace TemporalResonance;

/// <summary>
/// The preset interpreter. Buttplug's RunOutputAsync is a bare set-scalar with
/// no duration or loop semantics, so this layer synthesizes them with
/// main-thread timer callbacks. One playback session per (deviceKey, action);
/// a new Play on the same slot replaces the running one.
///
/// Cancellation uses generation counters instead of callback unregistration:
/// every scheduled callback captures the session generation at schedule time
/// and no-ops if the session has since been replaced. All state is
/// main-thread-only; no locks.
/// </summary>
public class PatternPlayer
{
    class Session
    {
        public int Generation;
        public double Level;      // effective intensity of the on-phase
        public long EndAtMs;      // 0 = infinite
        public bool OnPhase;
    }

    readonly ICoreClientAPI capi;
    readonly ButtplugManager devices;
    readonly Dictionary<(string deviceKey, string action), Session> sessions = new();

    public PatternPlayer(ICoreClientAPI capi, ButtplugManager devices)
    {
        this.capi = capi;
        this.devices = devices;
    }

    /// <summary>
    /// Start (or replace) playback of a preset on a device. scale multiplies
    /// each action's stored intensity. A zero effective intensity is legal —
    /// the dispatcher uses it to silence a hook's output.
    /// </summary>
    public void Play(TrDevice dev, Preset preset, double scale = 1.0)
    {
        foreach (var (action, intensity) in preset.Actions)
        {
            var level = Math.Clamp(intensity * scale, 0, 1);
            var session = GetOrCreate(dev.Key, action);
            session.Generation++;
            session.Level = level;
            session.EndAtMs = preset.DurationSec > 0 ? Now() + (long)(preset.DurationSec * 1000) : 0;
            session.OnPhase = true;

            Send(dev.Key, action, level);

            var loops = preset.LoopOnSec > 0 && preset.LoopOffSec > 0 && level > 0;
            if (loops)
            {
                ScheduleEdge(dev.Key, action, session.Generation, preset, msFromNow: (long)(preset.LoopOnSec * 1000));
            }
            else if (session.EndAtMs > 0)
            {
                var gen = session.Generation;
                capi.Event.RegisterCallback(_ =>
                {
                    if (!IsCurrent(dev.Key, action, gen)) return;
                    Send(dev.Key, action, 0);
                }, (int)(preset.DurationSec * 1000));
            }
        }
    }

    /// Zero every action channel on a device and end its sessions.
    public void Stop(string deviceKey)
    {
        foreach (var ((key, action), session) in sessions.Where(kv => kv.Key.deviceKey == deviceKey).ToList())
        {
            session.Generation++;
            Send(key, action, 0);
        }
    }

    public void StopAll()
    {
        foreach (var ((key, action), session) in sessions.ToList())
        {
            session.Generation++;
            Send(key, action, 0);
        }
    }

    void ScheduleEdge(string deviceKey, string action, int gen, Preset preset, long msFromNow)
    {
        capi.Event.RegisterCallback(_ =>
        {
            if (!IsCurrent(deviceKey, action, gen)) return;
            var session = sessions[(deviceKey, action)];

            // Duration expired: end on silence and stop scheduling.
            if (session.EndAtMs > 0 && Now() >= session.EndAtMs)
            {
                Send(deviceKey, action, 0);
                return;
            }

            session.OnPhase = !session.OnPhase;
            Send(deviceKey, action, session.OnPhase ? session.Level : 0);
            var nextMs = (long)((session.OnPhase ? preset.LoopOnSec : preset.LoopOffSec) * 1000);
            // Don't overshoot the duration end.
            if (session.EndAtMs > 0) nextMs = Math.Min(nextMs, Math.Max(1, session.EndAtMs - Now()));
            ScheduleEdge(deviceKey, action, gen, preset, nextMs);
        }, (int)Math.Max(1, msFromNow));
    }

    bool IsCurrent(string deviceKey, string action, int gen)
        => sessions.TryGetValue((deviceKey, action), out var s) && s.Generation == gen;

    Session GetOrCreate(string deviceKey, string action)
    {
        if (!sessions.TryGetValue((deviceKey, action), out var s))
            sessions[(deviceKey, action)] = s = new Session();
        return s;
    }

    void Send(string deviceKey, string action, double intensity)
    {
        var dev = devices.FindByKey(deviceKey);
        if (dev == null) return; // disconnected mid-pattern; nothing to command

        switch (action)
        {
            case "vibrate" when dev.CanVibrate:
                devices.RunAsync(() => devices.SetVibrateAsync(dev, intensity), "vibrate");
                break;
            // future: "oscillate", "rotate", ... map to their OutputTypes here
        }
    }

    long Now() => capi.ElapsedMilliseconds;
}
