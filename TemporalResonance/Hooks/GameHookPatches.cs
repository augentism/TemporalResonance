using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace TemporalResonance;

/// <summary>
/// Harmony patches that translate game actions into dispatcher events.
/// The pattern for every event hook: postfix a game method, filter to the
/// local client player, hop to the main thread, call Dispatch(hookId).
///
/// Note: patching a virtual base method only fires for overrides that call
/// base.*; vanilla block subclasses do, so this covers normal block breaking.
/// </summary>
[HarmonyPatch]
public static class GameHookPatches
{
    public const string BlockBrokenHookId = "block-broken";

    // Set by the ModSystem before patching; cleared on dispose.
    public static ICoreClientAPI? Capi;
    public static Dispatcher? Dispatcher;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Block), nameof(Block.OnBlockBroken))]
    public static void OnBlockBroken(IWorldAccessor world, IPlayer byPlayer)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        // Client-side call for the current player only — not other players,
        // not mobs/mechanisms (byPlayer null), not the server-side invocation.
        if (world.Side != EnumAppSide.Client) return;
        if (byPlayer == null || byPlayer.PlayerUID != capi.World.Player?.PlayerUID) return;

        // Dispatcher state is main-thread-only.
        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(BlockBrokenHookId), "temporalresonance");
    }
}
