using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TemporalResonance;

/// <summary>
/// The settings dialog, toggled with a hotkey (default O) or .tr gui.
/// Three sections: connection/panic, hook -> device -> preset assignment,
/// and a preset editor. It is a pure view over PresetStore / HookRegistry /
/// Dispatcher — every control change goes straight through the same builder
/// API the chat commands use, so the two surfaces can't drift apart.
///
/// The whole dialog is recomposed on any structural change (selection,
/// create/delete, device list change); only slider drags mutate in place.
/// </summary>
public class TrGuiDialog : GuiDialog
{
    public const string ToggleHotkey = "temporalresonancegui";

    readonly ButtplugManager devices;
    readonly PatternPlayer player;
    readonly PresetStore store;
    readonly HookRegistry hooks;
    readonly Dispatcher dispatcher;

    // Current selections; validated against live data on every compose.
    string? selHookId;
    string? selDeviceKey;
    string? selPresetId;
    string newPresetName = "";

    public TrGuiDialog(ICoreClientAPI capi, ButtplugManager devices, PatternPlayer player,
                       PresetStore store, HookRegistry hooks, Dispatcher dispatcher) : base(capi)
    {
        this.devices = devices;
        this.player = player;
        this.store = store;
        this.hooks = hooks;
        this.dispatcher = dispatcher;

        devices.DevicesChanged += () => { if (IsOpened()) Recompose(); };
    }

    public override string ToggleKeyCombinationCode => ToggleHotkey;

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Recompose();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        if (configDirty)
        {
            store.Save();
            configDirty = false;
        }
    }

    void Recompose()
    {
        var hookIds = hooks.All.Where(h => !h.Hidden).Select(h => h.Id).OrderBy(id => id).ToArray();
        var deviceKeys = devices.Devices.Select(d => d.Key).ToArray();
        var presetIds = store.Config.Presets.Keys.OrderBy(id => id).ToArray();

        selHookId = Pick(selHookId, hookIds);
        selDeviceKey = Pick(selDeviceKey, deviceKeys);
        selPresetId = Pick(selPresetId, presetIds);

        var hook = selHookId == null ? null : hooks.Get(selHookId);
        var preset = selPresetId == null ? null : store.GetPreset(selPresetId);
        var assignedPresetId = selHookId != null && selDeviceKey != null
            && store.AssignmentsFor(selHookId).TryGetValue(selDeviceKey, out var pid) ? pid : null;

        const double w = 640, rowH = 30, gap = 8, labelW = 130;
        double y = 0;
        ElementBounds Row(double width = w, double x = 0, double h = rowH) => ElementBounds.Fixed(x, y, width, h);

        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        // Height is set to the real content height (y) after the rows are laid out below.
        var container = ElementBounds.Fixed(0, GuiStyle.TitleBarHeight, w, 10);
        bgBounds.WithChildren(container);

        var composer = capi.Gui.CreateCompo("trgui", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Temporal Resonance", () => TryClose())
            .BeginChildElements(container);

        // ---- Connection ----
        y += gap;
        composer.AddStaticText(
            devices.Connected ? $"Connected — {devices.Devices.Count} device(s)" : "Not connected",
            CairoFont.WhiteSmallishText(), Row(w - 305));
        composer.AddSmallButton(devices.Connected ? "Disconnect" : "Connect", OnConnectToggle, Row(100, w - 300));
        composer.AddSmallButton("Scan", OnScan, Row(80, w - 195));
        composer.AddSmallButton("Panic stop", () => { dispatcher.Panic(); return true; }, Row(105, w - 110));
        y += rowH + gap;

        // ---- Assignment section ----
        composer.AddStaticText("Assignments", CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold), Row());
        y += rowH;

        composer.AddStaticText("Device", CairoFont.WhiteSmallText(), Row(labelW, 0));
        AddDropDown(composer, deviceKeys, deviceKeys, selDeviceKey,
            key => { selDeviceKey = key; Recompose(); }, Row(w - labelW, labelW), "devicedrop",
            emptyLabel: "(no devices connected)");
        y += rowH + gap;

        composer.AddStaticText("Trigger", CairoFont.WhiteSmallText(), Row(labelW, 0));
        AddDropDown(composer, hookIds, hookIds.Select(HookLabel).ToArray(), selHookId,
            id => { selHookId = id; Recompose(); }, Row(w - labelW - 130, labelW), "hookdrop");
        composer.AddStaticText("Invert", CairoFont.WhiteSmallText(), Row(55, w - 120));
        composer.AddSwitch(OnInvertToggle, Row(40, w - 45), "invertswitch");
        y += rowH + gap;

        composer.AddStaticText("Preset", CairoFont.WhiteSmallText(), Row(labelW, 0));
        // "(none)" entry = unassigned
        var assignValues = new[] { "" }.Concat(presetIds).ToArray();
        var assignNames = new[] { "(none)" }.Concat(presetIds).ToArray();
        AddDropDown(composer, assignValues, assignNames, assignedPresetId ?? "",
            OnAssignChanged, Row(w - labelW, labelW), "assigndrop");
        y += rowH + gap * 2;

        // ---- Preset editor ----
        composer.AddStaticText("Presets", CairoFont.WhiteSmallishText().WithWeight(Cairo.FontWeight.Bold), Row());
        y += rowH;

        AddDropDown(composer, presetIds, presetIds, selPresetId,
            id => { selPresetId = id; Recompose(); }, Row(w - 250, 0), "presetdrop",
            emptyLabel: "(no presets)");
        composer.AddSmallButton("Test", OnTestPreset, Row(70, w - 245));
        composer.AddSmallButton("Stop", () => { player.StopAll(); return true; }, Row(70, w - 170));
        composer.AddSmallButton("Delete", OnDeletePreset, Row(90, w - 95));
        y += rowH + gap;

        composer.AddTextInput(Row(w - 130, 0), t => newPresetName = t, CairoFont.WhiteSmallText(), "newpresetname");
        composer.AddSmallButton("Create new", OnCreatePreset, Row(125, w - 125));
        y += rowH + gap;

        if (preset != null)
        {
            AddSliderRow(composer, ref y, "Intensity", "intensityslider", w, labelW, rowH, gap);
            AddSliderRow(composer, ref y, "Duration", "durationslider", w, labelW, rowH, gap);
            AddSliderRow(composer, ref y, "Loop on", "looponslider", w, labelW, rowH, gap);
            AddSliderRow(composer, ref y, "Loop off", "loopoffslider", w, labelW, rowH, gap);
        }

        container.WithFixedHeight(y + gap);
        composer.EndChildElements();
        SingleComposer = composer.Compose();

        SingleComposer.GetTextInput("newpresetname").SetPlaceHolderText("new preset name...");
        SingleComposer.GetSwitch("invertswitch").On =
            selHookId != null && selDeviceKey != null && store.IsInverted(selHookId, selDeviceKey);

        if (preset != null)
        {
            var intensity = (int)Math.Round((preset.Actions.TryGetValue("vibrate", out var vib) ? vib : 0) * 100);
            SetupSlider("intensityslider", intensity, 0, 100, v => $"{v}%", v => p => p.Actions["vibrate"] = v / 100.0);
            SetupSlider("durationslider", (int)Math.Round(preset.DurationSec * 10), 0, 300,
                v => v == 0 ? "∞" : $"{v / 10.0:0.0}s", v => p => p.DurationSec = v / 10.0);
            SetupSlider("looponslider", (int)Math.Round(preset.LoopOnSec * 10), 0, 100,
                v => $"{v / 10.0:0.0}s", v => p => p.LoopOnSec = v / 10.0);
            SetupSlider("loopoffslider", (int)Math.Round(preset.LoopOffSec * 10), 0, 100,
                v => $"{v / 10.0:0.0}s", v => p => p.LoopOffSec = v / 10.0);
        }
    }

    // ---- control handlers ----

    bool OnConnectToggle()
    {
        if (devices.Connected) devices.RunAsync(() => devices.DisconnectAsync(), "disconnect");
        else devices.RunAsync(async () => { await devices.ConnectAsync(store.Config.ServerUrl); OnMain(Recompose); }, "connect");
        return true;
    }

    bool OnScan()
    {
        if (devices.Connected) devices.RunAsync(() => devices.StartScanningAsync(), "scan");
        return true;
    }

    void OnAssignChanged(string presetId)
    {
        if (selHookId == null || selDeviceKey == null) return;
        if (presetId == "")
        {
            // Silence whatever this assignment last sent before removing it —
            // the hardware otherwise holds its level until something replaces it.
            // Re-playing the old preset at scale 0 only touches this preset's
            // action channels, leaving other hooks on the device alone.
            if (store.AssignmentsFor(selHookId).TryGetValue(selDeviceKey, out var oldPresetId)
                && store.GetPreset(oldPresetId) is { } oldPreset
                && devices.FindByKey(selDeviceKey) is { } dev)
            {
                player.Play(dev, oldPreset, 0);
            }
            store.Unassign(selHookId, selDeviceKey);
        }
        else store.Assign(selHookId, selDeviceKey, presetId);
    }

    void OnInvertToggle(bool on)
    {
        if (selHookId == null || selDeviceKey == null) return;
        store.SetInverted(selHookId, selDeviceKey, on);
    }

    bool OnTestPreset()
    {
        if (selPresetId == null || selDeviceKey == null) return true;
        var dev = devices.FindByKey(selDeviceKey);
        var preset = store.GetPreset(selPresetId);
        if (dev != null && preset != null) player.Play(dev, preset);
        return true;
    }

    bool OnCreatePreset()
    {
        var name = newPresetName.Trim();
        if (name.Length == 0) return true;
        var preset = store.CreatePreset(name);
        store.UpdatePreset(preset.Id, p => p.Actions["vibrate"] = 0.5);
        selPresetId = preset.Id;
        newPresetName = "";
        Recompose();
        return true;
    }

    bool OnDeletePreset()
    {
        if (selPresetId != null) store.DeletePreset(selPresetId);
        selPresetId = null;
        Recompose();
        return true;
    }

    // ---- helpers ----

    /// Sliders store scaled ints (percent / tenths of a second) but display
    /// through a format callback (e.g. 15 -> "1.5s"). This API version's
    /// slider fires on every drag tick, so edits mutate the preset in memory
    /// and the config is saved once when the dialog closes.
    void SetupSlider(string key, int value, int min, int max, System.Func<int, string> format, System.Func<int, Action<Preset>> edit)
    {
        var slider = SingleComposer.GetSlider(key);
        // Callbacks must be in place BEFORE SetValues — it renders the initial
        // text immediately, and without them that first render shows the raw int.
        slider.ShowTextWhenResting = true;
        slider.OnSliderTooltip = v => format(v);
        slider.OnSliderRestingText = v => format(v);
        slider.SetValues(value, min, max, 1);
        SliderEdits[key] = v =>
        {
            var preset = selPresetId == null ? null : store.GetPreset(selPresetId);
            if (preset != null)
            {
                edit(v)(preset);
                configDirty = true;
            }
            return true;
        };
    }

    bool configDirty;

    readonly System.Collections.Generic.Dictionary<string, ActionConsumable<int>> SliderEdits = new();

    void AddSliderRow(GuiComposer composer, ref double y, string label, string key,
                      double w, double labelW, double rowH, double gap)
    {
        composer.AddStaticText(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, labelW, rowH));
        composer.AddSlider(v => SliderEdits.TryGetValue(key, out var f) && f(v),
            ElementBounds.Fixed(labelW, y, w - labelW, rowH - 8), key);
        y += rowH + gap;
    }

    static void AddDropDown(GuiComposer composer, string[] values, string[] names, string? selected,
                            Action<string> onChanged, ElementBounds bounds, string key, string emptyLabel = "-")
    {
        // With no real entries, show an inert placeholder that never fires the
        // callback. An empty-string VALUE in a real list is legitimate (the
        // "(none)" unassign entry) and must still fire.
        var isPlaceholder = values.Length == 0;
        if (isPlaceholder)
        {
            values = new[] { "" };
            names = new[] { emptyLabel };
        }
        var index = Math.Max(0, Array.IndexOf(values, selected ?? ""));
        composer.AddDropDown(values, names, index,
            (code, _) => { if (!isPlaceholder) onChanged(code); }, bounds, key);
    }

    string HookLabel(string id)
    {
        var h = hooks.Get(id);
        return h == null ? id : $"{h.DisplayName} [{id}]";
    }

    static string? Pick(string? current, string[] available)
        => current != null && available.Contains(current) ? current : available.FirstOrDefault();

    void OnMain(Action action) => capi.Event.EnqueueMainThreadTask(action, "temporalresonance");
}
