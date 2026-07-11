using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

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

        api.Input.RegisterHotKey("temporalresonancepanic", "Temporal Resonance panic stop",
            GlKeys.P, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler("temporalresonancepanic", _ =>
        {
            dispatcher.Reset();
            api.ShowChatMessage("[TemporalResonance] Panic: all devices stopped, dispatcher reset.");
            return true;
        });

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
            CooldownSec = 1,
            Hidden = true
        });
        hooks.Register(new HookDescriptor
        {
            Id = "debug-poll",
            DisplayName = "Debug poll source",
            Kind = HookKind.Poll,
            CooldownSec = 0,
            PollIntervalSec = 0.5,
            Sampler = () => commands.DebugPollValue,
            Hidden = true
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

        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.PlayerHurtHookId,
            DisplayName = "You took damage",
            Kind = HookKind.Event,
            CooldownSec = 0.5
        });

        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.AteFoodHookId,
            DisplayName = "You ate food",
            Kind = HookKind.Event,
            CooldownSec = 1
        });

        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.AteMealHookId,
            DisplayName = "You ate a meal (bowl/placed)",
            Kind = HookKind.Event,
            CooldownSec = 1
        });

        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.DrankHookId,
            DisplayName = "You drank",
            Kind = HookKind.Event,
            CooldownSec = 1
        });

        hooks.Register(new HookDescriptor
        {
            Id = GameHookPatches.BearFootstepHookId,
            DisplayName = "Bear footsteps nearby",
            Kind = HookKind.Event,
            CooldownSec = 0.4
        });

        hooks.Register(new HookDescriptor
        {
            Id = "temporal-stability",
            DisplayName = "Temporal stability (1 = stable; invert for intensity on low stability)",
            Kind = HookKind.Poll,
            CooldownSec = 0,
            PollIntervalSec = 1,
            Sampler = SampleTemporalStability
        });

        hooks.Register(new HookDescriptor
        {
            Id = "health-level",
            DisplayName = "Health (1 = full; invert for intensity when hurt)",
            Kind = HookKind.Poll,
            CooldownSec = 0,
            PollIntervalSec = 1,
            Sampler = () => SampleAttributeRatio("health", "currenthealth", "maxhealth")
        });
        hooks.Register(new HookDescriptor
        {
            Id = "hunger-level",
            DisplayName = "Saturation (1 = full; invert for intensity when hungry)",
            Kind = HookKind.Poll,
            CooldownSec = 0,
            PollIntervalSec = 1,
            Sampler = () => SampleAttributeRatio("hunger", "currentsaturation", "maxsaturation")
        });

        GameHookPatches.Capi = capi;
        GameHookPatches.Dispatcher = dispatcher;
        harmony = new Harmony("temporalresonance");
        harmony.PatchAll(typeof(GameHookPatches).Assembly);
    }

    /// 0.00 (fully destabilized) .. 1.00 (stable); null while the player
    /// entity isn't available (loading screens, death) so the nil-streak
    /// auto-stop kicks in instead of holding the last level.
    float? SampleTemporalStability()
    {
        var behavior = capi.World?.Player?.Entity?.GetBehavior("temporalstabilityaffected");
        if (behavior is not EntityBehaviorTemporalStabilityAffected ent) return null;
        return (float)Math.Clamp(ent.OwnStability, 0, 1);
    }

    /// Normalized current/max from a WatchedAttributes tree on the controlling
    /// player's entity. capi.World.Player is always the local (controlling)
    /// player on the client — other players' entities are never sampled.
    /// WatchedAttributes are server-synced, so no behavior instance or Harmony
    /// patch is needed. Null when unavailable -> nil-streak auto-stop.
    float? SampleAttributeRatio(string treeName, string currentKey, string maxKey)
    {
        var tree = capi.World?.Player?.Entity?.WatchedAttributes.GetTreeAttribute(treeName);
        if (tree == null) return null;
        var max = tree.GetFloat(maxKey);
        if (max <= 0) return null;
        return Math.Clamp(tree.GetFloat(currentKey) / max, 0f, 1f);
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
