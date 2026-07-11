using System;

namespace TemporalResonance;

public enum HookKind
{
    /// Discrete trigger ("took damage") — dispatched at full strength, once.
    Event,
    /// Continuous source ("health level") — sampled on an interval, 0..1.
    Poll
}

/// <summary>
/// A stimulus source descriptor. Id is the persistence key for user
/// assignments — renaming it orphans saved config, so never change it.
/// Adding a new hook to the mod should touch only the registry (plus, for
/// events, one wiring line at the game-event source).
/// </summary>
public class HookDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required HookKind Kind { get; init; }

    /// Minimum seconds between dispatches of this hook.
    public double CooldownSec { get; init; }

    /// Polls only: seconds between samples.
    public double PollIntervalSec { get; init; }

    /// Polls only: returns current level 0..1, or null when the source is
    /// unavailable (dead, wrong screen, ...).
    public Func<float?>? Sampler { get; init; }
}
