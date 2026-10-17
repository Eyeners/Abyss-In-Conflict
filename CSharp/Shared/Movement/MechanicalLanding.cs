using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class MechanicalLanding : IAssemblyPlugin
    {
        private static readonly Identifier Species = "aic_exsequent_frame_hlf01".ToIdentifier();
        private sealed class LandingState
        {
            internal float Remaining;
        }
        private struct ImpactState
        {
            internal bool Landing;
            internal float PreviousImpact;
        }
        private struct PoseState
        {
            internal bool Changed;
            internal bool Crouching;
        }
        private static ConditionalWeakTable<Ragdoll, LandingState> landings = new();
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.mechanical.landing");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Ragdoll), "ApplyImpact"),
                    prefix: new HarmonyMethod(typeof(MechanicalLanding), nameof(BeforeImpact)),
                    finalizer: new HarmonyMethod(typeof(MechanicalLanding), nameof(AfterImpact)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                    prefix: new HarmonyMethod(typeof(MechanicalLanding), nameof(BeforeAnimation)),
                    finalizer: new HarmonyMethod(typeof(MechanicalLanding), nameof(AfterAnimation)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC MechanicalLanding] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); landings = new(); }

        private static bool CanBrace(Character c) => c != null && c.SpeciesName == Species
            && !c.Removed && !c.IsDead && !c.IsIncapacitated && !c.IsRagdolled
            && c.Stun <= 0 && !c.InWater && !c.IsClimbing;

        private static void BeforeImpact(Ragdoll __instance, Vector2 worldNormal, Vector2 velocity,
            out ImpactState __state)
        {
            __state = default;
            if (!CanBrace(__instance.Character) || worldNormal.Y < 0.5f || velocity.Y > -3f) return;
            __state.Landing = true;
            __state.PreviousImpact = __instance.strongestImpact;
        }
        private static void AfterImpact(Ragdoll __instance, ImpactState __state)
        {
            if (!__state.Landing) return;
            // Keep damage and OnImpact effects. Remove only this floor contact's
            // contribution to the direct stun applied by HumanoidAnimController.
            __instance.strongestImpact = __state.PreviousImpact;
            landings.GetOrCreateValue(__instance).Remaining = 0.3f;
        }
        private static void BeforeAnimation(HumanoidAnimController __instance, float deltaTime,
            out PoseState __state)
        {
            __state = default;
            if (!landings.TryGetValue(__instance, out var landing)) return;
            if (!CanBrace(__instance.Character))
            {
                landings.Remove(__instance);
                return;
            }
            if (landing.Remaining <= 0)
            {
                landings.Remove(__instance);
                return;
            }
            landing.Remaining -= deltaTime;
            if (!__instance.OnGround) return;
            __state.Changed = true;
            __state.Crouching = __instance.Crouching;
            __instance.Crouching = true;
        }
        private static void AfterAnimation(HumanoidAnimController __instance, PoseState __state)
        {
            if (__state.Changed) __instance.Crouching = __state.Crouching;
        }
    }
}
