using System.Collections.Generic;

namespace TemporalResonance;

/// <summary>
/// A named output bundle: per-action intensity (0..1) plus Lovense-style
/// timing — total duration (0 = infinite) and an optional on/off loop.
/// Serialized into the mod config; Id is the persistence key.
/// </summary>
public class Preset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// action name ("vibrate"; later "oscillate", "rotate", ...) -> intensity 0..1
    public Dictionary<string, double> Actions { get; set; } = new();

    /// Total run time in seconds; 0 means run until replaced or stopped.
    public double DurationSec { get; set; }

    /// Loop on-phase seconds; 0 = no looping (constant level).
    public double LoopOnSec { get; set; }

    /// Loop off-phase seconds.
    public double LoopOffSec { get; set; }
}
