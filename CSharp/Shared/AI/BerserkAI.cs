using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class BerserkAI : IAssemblyPlugin
    {
        private static readonly Identifier AfflictionId = "aic_berserk".ToIdentifier();
        private sealed class State
        {
            internal double NextCheck;
            internal double NextTarget;
            internal double LastSeen;
            internal Character Target;
            internal BerserkCombat Combat;
        }
        // A separate objective type preserves existing orders and ordinary combat objectives.
        private sealed class BerserkCombat : AIObjectiveCombat
        {
            internal BerserkCombat(Character actor, Character target, AIObjectiveManager manager)
                : base(actor, target, CombatMode.Offensive, manager) { }
        }
        private static ConditionalWeakTable<HumanAIController, State> states = new();
        private static readonly HashSet<HumanAIController> active = new();
        private Harmony harmony;

        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static bool IsBerserk(Character actor) => actor != null && !actor.Removed && !actor.IsDead
            && !actor.IsPlayer && actor.AIController is HumanAIController
            && actor.CharacterHealth.GetAfflictionStrengthByIdentifier(AfflictionId) > 0;

        public void Initialize()
        {
            harmony = new Harmony("aic.berserk.ai");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(HumanAIController), "Update", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(Update)));
                harmony.Patch(AccessTools.Method(typeof(HumanAIController), "IsFriendly",
                    new[] { typeof(Character), typeof(Character), typeof(bool), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(BerserkAI), nameof(Friendly)));
                harmony.Patch(AccessTools.Method(typeof(HumanAIController), "AddCombatObjective"),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(AllowOrdinaryCombat)));
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveCombat), "GetPriority"),
                    postfix: new HarmonyMethod(typeof(BerserkAI), nameof(Priority)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(AIObjectiveCombat), "AllowCoolDown"),
                    postfix: new HarmonyMethod(typeof(BerserkAI), nameof(Cooldown)));
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveCombat), "OnDeselected"),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(Deselected)));
                harmony.Patch(AccessTools.Method(typeof(Projectile), "ShouldIgnoreCharacterCollision"),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(Collision)),
                    finalizer: new HarmonyMethod(typeof(BerserkAI), nameof(RestoreCollision)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(Clear)));
                harmony.Patch(AccessTools.Method(typeof(Character), "Remove"),
                    prefix: new HarmonyMethod(typeof(BerserkAI), nameof(Removing)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC BerserkAI] " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { Clear(); harmony?.UnpatchSelf(); }

        private static bool Valid(Character actor, Character target) => target != null && target != actor
            && !target.Removed && target.Enabled && !target.IsDead && !target.IsIncapacitated
            && !target.InDetectable && Vector2.DistanceSquared(actor.WorldPosition, target.WorldPosition) <= 1500 * 1500;

        private static void Update(HumanAIController __instance)
        {
            if (!Authority) return;
            var state = states.GetValue(__instance, _ => new State());
            double now = Timing.TotalTime;
            if (now < state.NextCheck) return;
            state.NextCheck = now + 0.25;
            var actor = __instance.Character;
            if (!IsBerserk(actor) || actor.IsIncapacitated)
            {
                Stop(__instance, state);
                active.Remove(__instance);
                return;
            }
            active.Add(__instance);
            bool valid = Valid(actor, state.Target);
            if (valid && actor.CanSeeTarget(state.Target)) state.LastSeen = now;
            if (!valid || now - state.LastSeen > 2 || state.Combat?.Abandon == true || state.Combat?.IsCompleted == true)
            {
                Stop(__instance, state);
                if (now < state.NextTarget) return;
                state.NextTarget = now + 0.5;
                Character closest = null;
                float distance = 1500 * 1500;
                foreach (var candidate in Character.CharacterList)
                {
                    if (!Valid(actor, candidate)) continue;
                    float squared = Vector2.DistanceSquared(actor.WorldPosition, candidate.WorldPosition);
                    if (squared >= distance || !actor.CanSeeTarget(candidate)) continue;
                    closest = candidate;
                    distance = squared;
                }
                if (closest == null) return;
                state.Target = closest;
                state.LastSeen = now;
                state.Combat = new BerserkCombat(actor, closest, __instance.ObjectiveManager);
                state.Combat.AbortCondition = _ => !IsBerserk(actor);
                __instance.ObjectiveManager.AddObjective(state.Combat);
                __instance.SortTimer = 0;
            }
        }

        private static void Stop(HumanAIController ai, State state)
        {
            if (state.Combat != null)
            {
                state.Combat.Abandon = true;
                ai.ObjectiveManager.Objectives.Remove(state.Combat);
                ai.SortTimer = 0;
            }
            state.Combat = null;
            state.Target = null;
        }
        private static void Clear()
        {
            foreach (var ai in active)
                if (states.TryGetValue(ai, out var state)) Stop(ai, state);
            active.Clear();
            states = new();
        }
        private static void Removing(Character __instance)
        {
            if (__instance.AIController is HumanAIController ai)
            {
                if (states.TryGetValue(ai, out var state)) Stop(ai, state);
                active.Remove(ai);
                states.Remove(ai);
            }
        }
        private static void Friendly(Character __0, Character __1, ref bool __result)
        {
            if (Authority && __0 != __1 && IsBerserk(__0)) __result = false;
        }
        private static bool AllowOrdinaryCombat(HumanAIController __instance) => !Authority || !IsBerserk(__instance.Character);
        private static void Priority(AIObjectiveCombat __instance, ref float __result)
        {
            if (__instance is BerserkCombat)
                __instance.Priority = __result = IsBerserk(__instance.character) && !__instance.Abandon && !__instance.IsCompleted
                    ? AIObjectiveManager.MaxObjectivePriority : 0;
        }
        private static void Cooldown(AIObjectiveCombat __instance, ref bool __result)
        {
            if (__instance is BerserkCombat) __result = false;
        }
        // Native outpost combat marks its target criminal on deselection; this temporary
        // objective must not mark innocent targets merely because the berserk effect ended.
        private static bool Deselected(AIObjectiveCombat __instance) => !(__instance is BerserkCombat);
        private static void Collision(Projectile __instance, out bool __state)
        {
            __state = __instance.FriendlyFire;
            if (Authority && IsBerserk(__instance.User)) __instance.FriendlyFire = true;
        }
        private static void RestoreCollision(Projectile __instance, bool __state) => __instance.FriendlyFire = __state;
    }
}
