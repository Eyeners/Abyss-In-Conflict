using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class LowPostureCollider : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool restoring;
        private sealed class LowCollider
        {
            internal PhysicsBody Body;
            internal int Index, Previous;
        }
        private static readonly Dictionary<HumanoidAnimController, LowCollider> saved = new();
        private struct PoseState { internal bool Changed, Crouching; }
        private static readonly Identifier SuppressionId = "aic_suppression".ToIdentifier();
        private static readonly Identifier HeavyLoadId = "aic_heavy_load".ToIdentifier();
        internal static bool Suppressed(Character c) => c?.CharacterHealth != null
            && (c.CharacterHealth.GetAfflictionStrengthByIdentifier(SuppressionId) >= 20
                || c.CharacterHealth.GetAfflictionStrengthByIdentifier(HeavyLoadId) > 0.1f);
        private static void ForceProne(Character __instance)
        {
            if (__instance.IsHumanoid && MovementState.ActiveOnLand(__instance) && Suppressed(__instance))
                __instance.SetInput(InputType.Crouch, false, true);
        }
        private static bool NeedsSmall(HumanoidAnimController a)
        {
            var c = a.Character;
            return !restoring && MovementState.ActiveOnLand(c)
                && !HoverBike.Riding(c) && !HoverJetpack.Flying(c) && a.TryGetCollider(1, out var small) && small != null
                && (Suppressed(c) || ActiveCrawl.IsCrawling(c) || FoehnSlide.Equipped(c)?.Sliding == true);
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.lowposture.collider");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Character), "Control"),
                    prefix: new HarmonyMethod(typeof(LowPostureCollider), nameof(ForceProne)));
                harmony.Patch(AccessTools.PropertySetter(typeof(Ragdoll), nameof(Ragdoll.ColliderIndex)),
                    prefix: new HarmonyMethod(typeof(LowPostureCollider), nameof(SelectCollider)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(Ragdoll), nameof(Ragdoll.ColliderHeightFromFloor)),
                    postfix: new HarmonyMethod(typeof(LowPostureCollider), nameof(FloorClearance)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                    prefix: new HarmonyMethod(typeof(LowPostureCollider), nameof(Before)),
                    postfix: new HarmonyMethod(typeof(LowPostureCollider), nameof(After)),
                    finalizer: new HarmonyMethod(typeof(LowPostureCollider), nameof(RestorePose)));
                harmony.Patch(AccessTools.Method(typeof(Character), "Remove", Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(LowPostureCollider), nameof(Remove)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    prefix: new HarmonyMethod(typeof(LowPostureCollider), nameof(Clear)));
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        private static void SelectCollider(Ragdoll __instance, ref int value)
        {
            if (!restoring && (value == 0 || value == 1) && __instance is HumanoidAnimController a
                && NeedsSmall(a) && saved.TryGetValue(a, out var low) && Registered(a, low)) value = low.Index;
        }
        private static bool Registered(Ragdoll a, LowCollider low) => CustomColliderStorage.Registered(a, low.Body, low.Index);
        internal static void BeforeRecreate(Ragdoll __instance)
        {
            if (__instance is not HumanoidAnimController a || !saved.TryGetValue(a, out var low)) return;
            // Native Recreate replaces the list without resetting its current index.
            // Forget this lease before any ColliderIndex hooks can reuse it.
            saved.Remove(a);
            if (a.ColliderIndex == low.Index && Registered(a, low))
                CustomColliderStorage.ResetForRebuild(a);
            // Native CreateColliders owns destruction of the old physics bodies.
        }
        private static void FloorClearance(Ragdoll __instance, ref float __result)
        {
            if (__instance is HumanoidAnimController a && saved.TryGetValue(a, out var low)
                && a.Collider == low.Body) __result = 0.03f;
        }
        private static LowCollider GetLowCollider(HumanoidAnimController a)
        {
            if (saved.TryGetValue(a, out var low) && Registered(a, low)) return low;
            var normal = a.Collider;
            // A real horizontal capsule, not merely shorter metadata on an unchanged fixture.
            var body = CustomColliderStorage.Add(a, Math.Max(normal.Height, normal.Radius * 2), 0,
                normal.Radius, out int index);
            low = new LowCollider { Body = body, Index = index, Previous = a.ColliderIndex };
            saved[a] = low;
            return low;
        }
        private static void Before(HumanoidAnimController __instance, out PoseState __state)
        {
            __state = default;
            if (__instance.Frozen) return;
            if (NeedsSmall(__instance))
            {
                var low = GetLowCollider(__instance);
                bool entering = __instance.Collider != low.Body;
                if (entering) low.Previous = __instance.ColliderIndex;
                __instance.ColliderIndex = low.Index;
                if (entering && __instance.OnGround)
                {
                    var position = low.Body.SimPosition;
                    position.Y = Math.Min(position.Y, __instance.FloorY + low.Body.Radius + 0.03f);
                    low.Body.SetTransformIgnoreContacts(position, 0);
                }
            }
            else if (saved.TryGetValue(__instance, out var previous))
            {
                Restore(__instance, previous);
            }
            if (saved.TryGetValue(__instance, out var active) && __instance.Collider == active.Body)
            {
                __state = new PoseState { Changed = true, Crouching = __instance.Crouching };
                __instance.Crouching = true;
            }
        }
        private static void After(HumanoidAnimController __instance)
        {
            if (!__instance.Frozen) FoehnSlide.Equipped(__instance.Character)?.ApplyPose(__instance);
        }
        private static void RestorePose(HumanoidAnimController __instance, PoseState __state)
        {
            if (__state.Changed) __instance.Crouching = __state.Crouching;
        }
        private static void Restore(HumanoidAnimController a, LowCollider previous)
        {
            if (a.Character.Removed || !Registered(a, previous)) return;
            restoring = true;
            try { CustomColliderStorage.RestoreSelected(a, previous.Body, previous.Index,
                previous.Previous > 1 ? previous.Previous : a.Crouching && !a.InWater ? 1 : 0); }
            finally { restoring = false; }
        }
        private static void Remove(Character __instance)
        {
            if (__instance.AnimController is HumanoidAnimController a) saved.Remove(a);
        }
        private static void Clear()
        {
            foreach (var entry in saved)
            {
                var a = entry.Key; var low = entry.Value;
                Restore(a, low);
                if (low.Body.Removed) continue;
                // Keep an active blocked collider registered until native character cleanup.
                if (a.Collider == low.Body) continue;
                CustomColliderStorage.RemoveUnusedTail(a, low.Body);
            }
            saved.Clear();
        }
        public void Dispose() { Clear(); harmony?.UnpatchSelf(); }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
