using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace TemporalResonance;

/// <summary>
/// Owns the persisted TrConfig and exposes the builder API the future UI
/// (and the chat commands, meanwhile) use to edit presets, assignments and
/// inversions. Every mutator saves immediately — the config is tiny.
/// </summary>
public class PresetStore
{
    const string ConfigFile = "temporalresonance.json";

    readonly ICoreClientAPI capi;

    public TrConfig Config { get; private set; } = new();

    public PresetStore(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public void Load()
    {
        try
        {
            Config = capi.LoadModConfig<TrConfig>(ConfigFile) ?? new TrConfig();
        }
        catch (Exception ex)
        {
            capi.Logger.Error("[TemporalResonance] Failed to load config, using defaults: {0}", ex);
            Config = new TrConfig();
        }
        Save();
    }

    public void Save() => capi.StoreModConfig(Config, ConfigFile);

    // ---- Presets ----

    public Preset CreatePreset(string name)
    {
        var preset = new Preset { Id = NewPresetId(name), Name = name };
        Config.Presets[preset.Id] = preset;
        Save();
        return preset;
    }

    public Preset? GetPreset(string id) => Config.Presets.TryGetValue(id, out var p) ? p : null;

    public bool UpdatePreset(string id, Action<Preset> edit)
    {
        if (!Config.Presets.TryGetValue(id, out var preset)) return false;
        edit(preset);
        Save();
        return true;
    }

    /// Deletes a preset and strips every assignment that referenced it.
    public bool DeletePreset(string id)
    {
        if (!Config.Presets.Remove(id)) return false;
        foreach (var perDevice in Config.Assignments.Values)
        {
            foreach (var deviceKey in perDevice.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
                perDevice.Remove(deviceKey);
        }
        Save();
        return true;
    }

    // ---- Assignments / inversions ----

    public void Assign(string hookId, string deviceKey, string presetId)
    {
        if (!Config.Assignments.TryGetValue(hookId, out var perDevice))
            Config.Assignments[hookId] = perDevice = new Dictionary<string, string>();
        perDevice[deviceKey] = presetId;
        Save();
    }

    public bool Unassign(string hookId, string deviceKey)
    {
        var removed = Config.Assignments.TryGetValue(hookId, out var perDevice) && perDevice.Remove(deviceKey);
        if (removed) Save();
        return removed;
    }

    public IReadOnlyDictionary<string, string> AssignmentsFor(string hookId)
        => Config.Assignments.TryGetValue(hookId, out var perDevice)
            ? perDevice
            : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();

    public void SetInverted(string hookId, string deviceKey, bool inverted)
    {
        if (!Config.Inversions.TryGetValue(hookId, out var set))
            Config.Inversions[hookId] = set = new HashSet<string>();
        if (inverted) set.Add(deviceKey); else set.Remove(deviceKey);
        Save();
    }

    public bool IsInverted(string hookId, string deviceKey)
        => Config.Inversions.TryGetValue(hookId, out var set) && set.Contains(deviceKey);

    string NewPresetId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "preset";
        var id = slug;
        for (var n = 2; Config.Presets.ContainsKey(id); n++) id = $"{slug}-{n}";
        return id;
    }
}
