using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class AssistedMeleeForce : IAssemblyPlugin
    {
        private static readonly Identifier Exoskeleton = "aic_wardens_assisted_exoskeleton".ToIdentifier();
        private static readonly Identifier Muscle = "aic_artificial_muscle_fiber_assistance".ToIdentifier();
        private static readonly PropertyInfo Force = AccessTools.Property(typeof(Attack), "TargetForce");
        private Harmony harmony;

        private sealed class ForceState
        {
            internal Attack Attack;
            internal float Original;
        }

        public void Initialize()
        {
            harmony = new Harmony("aic.assisted.melee.force");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(MeleeWeapon), "HandleImpact"),
                    prefix: new HarmonyMethod(typeof(AssistedMeleeForce), nameof(BeforeImpact)),
                    finalizer: new HarmonyMethod(typeof(AssistedMeleeForce), nameof(AfterImpact)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC AssistedMeleeForce] Initialization failed: " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static void BeforeImpact(MeleeWeapon __instance, out ForceState __state)
        {
            __state = null;
            var health = __instance.User?.CharacterHealth;
            var attack = __instance.Attack;
            if (health == null || attack == null || attack.TargetForce == 0) return;
            float bonus = 0;
            if (health.GetAfflictionStrengthByIdentifier(Exoskeleton) > 0) bonus += 0.4f;
            if (health.GetAfflictionStrengthByIdentifier(Muscle) > 0) bonus += 1.5f;
            if (bonus == 0) return;
            // Match the two additive damage bonuses, without scaling impulse or ranged attacks.
            __state = new ForceState { Attack = attack, Original = attack.TargetForce };
            Force.SetValue(attack, __state.Original * (1 + bonus));
        }

        private static void AfterImpact(ForceState __state)
        {
            // Restore even after an exception: a weapon must not retain its previous user's bonus.
            if (__state != null) Force.SetValue(__state.Attack, __state.Original);
        }
    }
}
