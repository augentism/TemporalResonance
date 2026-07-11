using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TemporalResonance;

/// <summary>
/// Composition root. Wires up the layers:
/// ButtplugManager (device client) -> PatternPlayer (preset interpreter) ->
/// Dispatcher (arbitration) fed by HookRegistry (event/poll sources),
/// configured through PresetStore and exercised via TrCommands.
/// </summary>
public class TemporalResonanceModSystem : ModSystem
{
    ICoreClientAPI capi = null!;
    ButtplugManager devices = null!;
    PatternPlayer player = null!;
    PresetStore store = null!;
    HookRegistry hooks = null!;
    Dispatcher dispatcher = null!;
    TrCommands commands = null!;
    TrGuiDialog gui = null!;
    Harmony? harmony;

    // Talks to Intiface Central on the player's machine — client side only
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        devices = new ButtplugManager(api, Mod.Logger);
        devices.Message += text => api.ShowChatMessage($"[TemporalResonance] {text}");

        store = new PresetStore(api);
        store.Load();

        player = new PatternPlayer(api, devices);
        hooks = new HookRegistry();
        dispatcher = new Dispatcher(api, hooks, devices, player, store);
        gui = new TrGuiDialog(api, devices, player, store, hooks, dispatcher);
        api.Input.RegisterHotKey(TrGuiDialog.ToggleHotkey, "Temporal Resonance settings",
            GlKeys.O, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(TrGuiDialog.ToggleHotkey, _ => ToggleGui());

        commands = new TrCommands(api, devices, player, store, hooks, dispatcher) { OpenGui = () => ToggleGui() };
        commands.Register();

        RegisterDebugHooks();
        RegisterGameHooks();
        hooks.StartPolling(api, dispatcher);

        // Hardware doesn't stop on its own — silence everything when the session ends.
        api.Event.LeaveWorld += dispatcher.Reset;

        if (store.Config.AutoConnect)
            devices.RunAsync(() => devices.ConnectAsync(store.Config.ServerUrl), "connect");
    }

    /// Two synthetic hooks so the whole pipeline is testable before any real
    /// game hooks exist: fire the event with .tr fire debug-event, drive the
    /// poll with .tr debugpoll <0-100|nil>.
    void RegisterDebugHooks()
    {
        hooks.Register(new HookDescriptor
        {
            Id = "debug-event",
            DisplayName = "Debug event trigger",
            Kind = HookKind.Event,
            CooldownSec = 1
        });
        hooks.Register(new HookDescriptor
        {
            Id = "debug-poll",
            DisplayName = "Debug poll source",
            Kind = HookKind.Poll,
            CooldownSec = 0,
            PollIntervalSec = 0.5,
            Sampler = () => commands.DebugPollValue
        });
    }

    bool ToggleGui()
    {
        if (gui.IsOpened()) gui.TryClose(); else gui.TryOpen();
        return true;
    }

    /// Real game hooks: register the descriptor, then apply the Harmony
    /// patches that feed the dispatcher (see GameHookPatches).
    void RegisterGameHooks()
    {
        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.BlockBrokenHookId,
            DisplayName = "You broke a block",
            Kind = HookKind.Event,
            CooldownSec = 0.25
        });

        GameHookPatches.Capi = capi;
        GameHookPatches.Dispatcher = dispatcher;
        harmony = new Harmony("temporalresonance");
        harmony.PatchAll(typeof(GameHookPatches).Assembly);
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("temporalresonance");
        harmony = null;
        GameHookPatches.Capi = null;
        GameHookPatches.Dispatcher = null;
        if (capi != null)
        {
            hooks?.StopPolling();
            dispatcher?.Reset();
            capi.Event.LeaveWorld -= dispatcher!.Reset;
        }
        devices?.Dispose();
        base.Dispose();
    }
}
