using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class BrutalSlaughter : IAssemblyPlugin
    {
        private static readonly Identifier TalentId = "aic_brutal_slaughter".ToIdentifier();
        private static readonly Identifier SuppressionId = "aic_suppression".ToIdentifier();
        private static readonly Identifier PanicId = "aic_panic".ToIdentifier();
        private static readonly Identifier FearlessTag = "aic_fearless".ToIdentifier();
        private const float Radius = 300f;
        private const float PanicStrength = 5f;
        private Harmony harmony;
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;

        private sealed class Hit
        {
            internal Character Victim, Attacker;
            internal bool Dead, Down, Ranged;
        }
        private sealed class PanicCombat : AIObjectiveCombat
        {
            internal PanicCombat(Character actor, Character source, AIObjectiveManager manager)
                : base(actor, source, CombatMode.Retreat, manager) { }
        }
        private sealed class PanicState
        {
            internal double NextCheck;
            internal double ProgressTime;
            internal double NextEscape;
            internal double EscapeUntil;
            internal Vector2 ProgressPosition;
            internal PanicEscape Escape;
            internal PanicCombat Combat;
        }
        private sealed class PanicEscape : AIObjectiveGoTo
        {
            internal PanicEscape(WayPoint target, Character actor, AIObjectiveManager manager)
                : base(target, actor, manager)
            {
                SpeakIfFails = false;
                AllowGoingOutside = false;
                CloseEnough = 40f;
                OverridePriority = AIObjectiveManager.MaxObjectivePriority;
            }
        }
        private static ConditionalWeakTable<HumanAIController, PanicState> panicStates = new();
        private static readonly HashSet<HumanAIController> active = new();

        public void Initialize()
        {
            harmony = new Harmony("aic.brutal.slaughter");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Character), "ApplyAttack"),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(BeforeAttack)),
                    postfix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(AfterHit)));
                harmony.Patch(AccessTools.Method(typeof(RepairTool), "FixBody"),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(BeforeRepair)),
                    postfix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(AfterHit)));
                harmony.Patch(AccessTools.Method(typeof(HumanAIController), "Update", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(UpdatePanic)));
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveCombat), "GetPriority"),
                    postfix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(Priority)));
                harmony.Patch(AccessTools.PropertyGetter(typeof(AIObjectiveCombat), "AllowCoolDown"),
                    postfix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(Cooldown)));
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveCombat), "OnDeselected"),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(Deselected)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(Clear)));
                harmony.Patch(AccessTools.Method(typeof(Character), "Remove"),
                    prefix: new HarmonyMethod(typeof(BrutalSlaughter), nameof(Removing)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC BrutalSlaughter] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { Clear(); harmony?.UnpatchSelf(); }

        // Stun alone is not a takedown: require the human's vitality to reach zero.
        private static bool Down(Character c) => c.IsHuman && c.CharacterHealth.Vitality <= 0f;
        private static Hit Capture(Character victim, Character attacker, bool ranged)
        {
            if (!Authority || victim == null || victim.Removed || victim.IsDead || attacker == null
                || attacker.Removed || !attacker.HasTalent(TalentId) || victim == attacker
                || HumanAIController.IsFriendly(attacker, victim)) return null;
            return new Hit { Victim = victim, Attacker = attacker, Dead = victim.IsDead,
                Down = Down(victim), Ranged = ranged };
        }
        private static void BeforeAttack(Character __instance, Character attacker, Attack attack, out Hit __state)
        {
            __state = null;
            Item item = attack?.SourceItem;
            if (item == null) return;
            var projectile = item.GetComponent<Projectile>();
            bool melee = item.GetComponent<MeleeWeapon>() != null && projectile?.IsActive != true;
            if (!melee && projectile == null && item.GetComponent<RepairTool>() == null) return;
            __state = Capture(__instance, attacker, !melee);
        }
        private static void BeforeRepair(Character user, Body targetBody, out Hit __state)
        {
            var victim = targetBody?.UserData as Character ?? (targetBody?.UserData as Limb)?.character;
            __state = Capture(victim, user, true);
        }
        private static void AfterHit(Hit __state)
        {
            if (__state == null || __state.Victim.Removed) return;
            var victim = __state.Victim;
            if (!(!__state.Dead && victim.IsDead) && !(!__state.Down && Down(victim))) return;
            Identifier id = __state.Ranged ? SuppressionId : PanicId;
            if (!AfflictionPrefab.Prefabs.TryGet(id, out var prefab)) return;
            // Only a confirmed takedown scans candidates; no periodic target search.
            foreach (Character target in Character.CharacterList)
            {
                if (target == victim || target == __state.Attacker || target.Removed || !target.Enabled
                    || !target.IsHuman || target.IsDead || target.IsIncapacitated
                    || HumanAIController.IsFriendly(__state.Attacker, target)
                    || Vector2.DistanceSquared(victim.WorldPosition, target.WorldPosition) > Radius * Radius) continue;
                if (!__state.Ranged && Fearless(target)) continue;
                float strength = __state.Ranged ? prefab.MaxStrength : PanicStrength;
                var affliction = prefab.Instantiate(strength);
                affliction.Source = __state.Attacker;
                target.CharacterHealth.ApplyAffliction(null, affliction);
                // These are control meters, independent of target maximum vitality.
                var applied = target.CharacterHealth.GetAffliction(id);
                if (applied != null)
                {
                    applied.Strength = strength;
                    applied.Source = __state.Attacker;
                }
            }
        }
        private static bool Fearless(Character actor) => actor?.Params?.tags.Contains(FearlessTag) == true
            || actor?.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes)?.HasTag(FearlessTag) == true;

        private static bool Panicking(Character actor) => actor != null && !actor.Removed && actor.IsHuman
            && !actor.IsPlayer && !actor.IsDead && !actor.IsIncapacitated
            && !Fearless(actor)
            && actor.CharacterHealth.GetAfflictionStrengthByIdentifier(PanicId) > 0;
        private static void UpdatePanic(HumanAIController __instance)
        {
            if (!Authority) return;
            Character actor = __instance.Character;
            if (!panicStates.TryGetValue(__instance, out var state))
            {
                if (!Panicking(actor)) return;
                state = new PanicState { ProgressPosition = actor.WorldPosition, ProgressTime = Timing.TotalTime };
                panicStates.Add(__instance, state);
                active.Add(__instance);
            }
            if (Timing.TotalTime < state.NextCheck) return;
            state.NextCheck = Timing.TotalTime + 0.2;
            if (!Panicking(actor))
            {
                Stop(__instance, state);
                active.Remove(__instance);
                panicStates.Remove(__instance);
                return;
            }
            Character source = actor.CharacterHealth.GetAffliction(PanicId)?.Source;
            if (source == null || source.Removed || source.IsDead)
            {
                Stop(__instance, state);
                return;
            }
            if (state.Escape != null)
            {
                if (!state.Escape.Abandon && !state.Escape.IsCompleted && Timing.TotalTime < state.EscapeUntil) return;
                Stop(__instance, state);
                state.ProgressPosition = actor.WorldPosition;
                state.ProgressTime = Timing.TotalTime;
            }
            if (Timing.TotalTime - state.ProgressTime >= 0.8)
            {
                bool stuck = Vector2.DistanceSquared(actor.WorldPosition, state.ProgressPosition) < 25f * 25f;
                state.ProgressPosition = actor.WorldPosition;
                state.ProgressTime = Timing.TotalTime;
                if (stuck && Timing.TotalTime >= state.NextEscape
                    && Vector2.DistanceSquared(actor.WorldPosition, source.WorldPosition) < 800f * 800f
                    && actor.CanSeeTarget(source))
                {
                    state.NextEscape = Timing.TotalTime + 1.5;
                    if (TryEscape(__instance, state, source)) return;
                }
            }
            if (state.Combat != null && !state.Combat.Abandon && !state.Combat.IsCompleted
                && state.Combat.Enemy == source) return;
            Stop(__instance, state);
            state.Combat = new PanicCombat(actor, source, __instance.ObjectiveManager);
            state.Combat.AbortCondition = _ => !Panicking(actor);
            __instance.ObjectiveManager.AddObjective(state.Combat);
            __instance.SortTimer = 0;
        }
        // At a dead end, seek a reachable indoor waypoint past the threat instead of
        // repeatedly asking FindSafety for the same hull. Limit expensive path probes.
        private static bool TryEscape(HumanAIController ai, PanicState state, Character source)
        {
            Character actor = ai.Character;
            if (actor.Submarine == null || actor.CurrentHull == null) return false;
            Vector2 direction = source.WorldPosition - actor.WorldPosition;
            if (!actor.InWater) direction.Y = 0;
            if (direction.LengthSquared() < 1f) return false;
            direction.Normalize();
            var candidates = new List<(WayPoint Point, float Score)>();
            foreach (WayPoint point in WayPoint.WayPointList)
            {
                if (point.Removed || point.Submarine != actor.Submarine || point.CurrentHull == null) continue;
                Vector2 beyond = point.WorldPosition - source.WorldPosition;
                float distance = Vector2.DistanceSquared(point.WorldPosition, actor.WorldPosition);
                if (distance > 1000f * 1000f || Vector2.Dot(beyond, direction) < 120f) continue;
                float score = distance + Math.Abs(beyond.Y) * 200f;
                int index = candidates.FindIndex(c => score < c.Score);
                if (index < 0) index = candidates.Count;
                if (index >= 6) continue;
                candidates.Insert(index, (point, score));
                if (candidates.Count > 6) candidates.RemoveAt(6);
            }
            foreach (var candidate in candidates)
            {
                var path = ai.PathSteering.PathFinder.FindPath(actor.SimPosition, candidate.Point.SimPosition,
                    actor.Submarine, nodeFilter: node => node.Waypoint.CurrentHull != null);
                if (path == null || path.Unreachable || path.HasOutdoorsNodes) continue;
                Stop(ai, state);
                state.Escape = new PanicEscape(candidate.Point, actor, ai.ObjectiveManager);
                state.Escape.AbortCondition = _ => !Panicking(actor);
                state.EscapeUntil = Timing.TotalTime + 2.5;
                ai.ObjectiveManager.AddObjective(state.Escape);
                ai.SortTimer = 0;
                return true;
            }
            return false;
        }
        private static void Priority(AIObjectiveCombat __instance, ref float __result)
        {
            if (__instance is PanicCombat)
                __instance.Priority = __result = Panicking(__instance.character) && !__instance.Abandon
                    && !__instance.IsCompleted ? AIObjectiveManager.MaxObjectivePriority : 0;
            else if (Authority && Panicking(__instance.character)) __instance.Priority = __result = 0;
        }
        private static void Cooldown(AIObjectiveCombat __instance, ref bool __result)
        {
            if (__instance is PanicCombat) __result = false;
        }
        private static bool Deselected(AIObjectiveCombat __instance) => !(__instance is PanicCombat);
        private static void Stop(HumanAIController ai, PanicState state)
        {
            if (state.Escape != null)
            {
                state.Escape.Abandon = true;
                ai.ObjectiveManager.Objectives.Remove(state.Escape);
                state.Escape = null;
                ai.SortTimer = 0;
            }
            if (state.Combat == null) return;
            state.Combat.Abandon = true;
            ai.ObjectiveManager.Objectives.Remove(state.Combat);
            state.Combat = null;
            ai.SortTimer = 0;
        }
        private static void Removing(Character __instance)
        {
            if (__instance.AIController is HumanAIController ai)
            {
                if (panicStates.TryGetValue(ai, out var state)) Stop(ai, state);
                active.Remove(ai);
                panicStates.Remove(ai);
            }
        }
        private static void Clear()
        {
            foreach (var ai in active)
                if (panicStates.TryGetValue(ai, out var state)) Stop(ai, state);
            active.Clear();
            panicStates = new();
        }
    }
}
