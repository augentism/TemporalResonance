using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace TemporalResonance;

/// <summary>
/// Harmony patches that translate game actions into dispatcher events.
/// The pattern for every event hook: postfix a game method, filter to the
/// local client player if necessary, hop to the main thread, call Dispatch(hookId).
///
/// Note: patching a virtual base method only fires for overrides that call
/// base.*
/// </summary>
[HarmonyPatch]
public static class GameHookPatches
{
    public const string BlockBrokenHookId = "block-broken";
    public const string PlayerHurtHookId = "player-hurt";
    public const string AteFoodHookId = "ate-food";
    public const string AteMealHookId = "ate-meal";
    public const string DrankHookId = "drank";
    public const string BearFootstepHookId = "bear-footstep";
    public const string TemporalStormHookId = "temporal-storm-start";

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
        
        if (world.Side != EnumAppSide.Client) return;
        if (byPlayer == null || byPlayer.PlayerUID != capi.World.Player?.PlayerUID) return;
        
        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(BlockBrokenHookId), "temporalresonance");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityPlayer), nameof(EntityPlayer.OnHurt))]
    public static void OnHurt(EntityPlayer __instance, DamageSource damageSource, float damage)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        // OnHurt runs client-side for the local player (it drives the camera
        // shake), so filter to that invocation: real damage, not a heal
        // (heals route through OnHurt too), our own entity only.
        if (damage <= 0 || damageSource?.Type == EnumDamageType.Heal) return;
        if (__instance.World?.Side != EnumAppSide.Client) return;
        if (__instance.EntityId != capi.World.Player?.Entity?.EntityId) return;

        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(PlayerHurtHookId), "temporalresonance");
    }

    // tryEatStop is invoked on both sides when the eat interaction ends, but
    // its body only acts server-side — so on the client we re-check the same
    // "actually ate" conditions it uses: held long enough and item is edible.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CollectibleObject), "tryEatStop")]
    public static void OnEatStop(CollectibleObject __instance, float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        if (byEntity?.World?.Side != EnumAppSide.Client || secondsUsed < 0.95f) return;
        if (byEntity is not EntityPlayer eplr || eplr.EntityId != capi.World.Player?.Entity?.EntityId) return;
        if (slot?.Itemstack == null
            || __instance.GetNutritionProperties(byEntity.World, slot.Itemstack, byEntity) == null) return;

        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(AteFoodHookId), "temporalresonance");
    }

    // Bowl meals (held or placed) both route through tryFinishEatMeal; like
    // tryEatStop it early-returns on the client, so re-check its conditions:
    // 1.45s eat threshold and edible contents.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockMeal), "tryFinishEatMeal")]
    public static void OnFinishEatMeal(BlockMeal __instance, float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        if (byEntity?.World?.Side != EnumAppSide.Client || secondsUsed < 1.45f) return;
        if (byEntity is not EntityPlayer eplr || eplr.EntityId != capi.World.Player?.Entity?.EntityId) return;
        if (__instance.GetContentNutritionProperties(byEntity.World, slot, byEntity) == null) return;

        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(AteMealHookId), "temporalresonance");
    }

    // Drinking: BlockLiquidContainerBase overrides tryEatStop WITHOUT calling
    // base when the container has liquid, so the ate-food patch never sees it.
    // Same client-side re-check pattern; nutrition-per-litre null (e.g. plain
    // water in some configs) means the vanilla code wouldn't drink either.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockLiquidContainerBase), "tryEatStop")]
    public static void OnDrinkStop(BlockLiquidContainerBase __instance, float secondsUsed, ItemSlot slot, EntityAgent byEntity)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        if (byEntity?.World?.Side != EnumAppSide.Client || secondsUsed < 0.95f) return;
        if (byEntity is not EntityPlayer eplr || eplr.EntityId != capi.World.Player?.Entity?.EntityId) return;

        var stack = slot?.Itemstack;
        if (stack == null || __instance.IsEmpty(stack)) return; // empty container falls through to base = ate-food path
        if (__instance.GetContentProps(stack) == null
            || __instance.GetNutritionPropertiesPerLitre(byEntity.World, stack, byEntity) == null) return;

        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(DrankHookId), "temporalresonance");
    }

    static readonly AccessTools.FieldRef<AnimationManager, Entity> AnimManagerEntity =
        AccessTools.FieldRefAccess<AnimationManager, Entity>("entity");

    // Bear footsteps are animationSounds on the bear's walk/run animations
    // (sounds/creature/bear/footsteps/...). The client animator calls
    // ShouldPlaySound only for rendered nearby entities when the step frame is
    // reached, so this fires exactly when a footstep is audible. Additionally
    // gated on the sound's own range vs distance to the local player, since
    // "rendered" can exceed "audible".
    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationManager), nameof(AnimationManager.ShouldPlaySound),
        typeof(string), typeof(AnimationSound))]
    public static void OnAnimationSound(AnimationManager __instance, AnimationSound sound)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null) return;

        var path = sound?.Attributes.Location?.Path;
        if (path == null || !path.StartsWith("sounds/creature/bear/footsteps")) return;

        var bear = AnimManagerEntity(__instance);
        var playerPos = capi.World?.Player?.Entity?.Pos;
        if (bear?.World?.Side != EnumAppSide.Client || playerPos == null) return;

        var range = sound!.Attributes.Range;
        var distance = bear.Pos.DistanceTo(playerPos.XYZ);
        if (distance > range || range <= 0) return;

        // Perceived loudness: the sound's base volume attenuated linearly to
        // ~zero at its range (the game fades to 1% there). A bear stepping
        // next to you hits full intensity; at the edge of earshot, a whisper.
        var volume = sound.Attributes.Volume?.avg ?? 1f;
        var loudness = Math.Clamp(volume * (1f - (float)(distance / range)), 0f, 1f);
        if (loudness <= 0) return;

        capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(BearFootstepHookId, loudness), "temporalresonance");
    }

    static bool lastStormActive;

    /// Reset transition state on world join/leave so a storm already raging
    /// when the player joins fires the hook once.
    public static void ResetStormState() => lastStormActive = false;

    // The server pushes TemporalStormRunTimeData to the client via this
    // private handler; nowStormActive flipping false -> true is storm start.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SystemTemporalStability), "onServerData")]
    public static void OnStormData(TemporalStormRunTimeData data)
    {
        var capi = Capi;
        var dispatcher = Dispatcher;
        if (capi == null || dispatcher == null || data == null) return;

        var wasActive = lastStormActive;
        lastStormActive = data.nowStormActive;
        if (data.nowStormActive && !wasActive)
        {
            capi.Event.EnqueueMainThreadTask(() => dispatcher.Dispatch(TemporalStormHookId), "temporalresonance");
        }
    }
}
