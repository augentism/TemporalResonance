using System.Collections.Generic;

namespace TemporalResonance;

/// <summary>
/// Persisted mod config. All cross-references are by stable string ids:
/// preset ids, hook ids (HookDescriptor.Id), and device keys (TrDevice.Key —
/// device name, since Buttplug indices aren't stable across sessions).
/// The sparse hook × device assignment shape means any device can respond to
/// any hook with any preset, and new hooks/devices need no schema change.
/// </summary>
public class TrConfig
{
    public int Version { get; set; } = 1;
    public string ServerUrl { get; set; } = ButtplugManager.DefaultUrl;
    public bool AutoConnect { get; set; } = false;

    /// presetId -> preset
    public Dictionary<string, Preset> Presets { get; set; } = new();

    /// hookId -> deviceKey -> presetId
    public Dictionary<string, Dictionary<string, string>> Assignments { get; set; } = new();

    /// hookId -> device keys that receive 1-scale instead of scale (polls only)
    public Dictionary<string, HashSet<string>> Inversions { get; set; } = new();
}
