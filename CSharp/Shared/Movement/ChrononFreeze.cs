using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Local time pause: gameplay/health/animation stop, while the global world and rendering continue.
    internal static class ChrononFreeze
    {
        private sealed class Pose
        {
            internal readonly Dictionary<PhysicsBody, BodyState> Bodies = new();
        }
        private readonly record struct BodyState(Vector2 Position, float Rotation, Vector2 Velocity,
            float AngularVelocity, BodyType Type);
        private static readonly Dictionary<Character, Pose> frozen = new();
        private static readonly List<Character> released = new();
        private static readonly List<DurationListElement> pausedEffects = new();
        // DelayedListElement is internal in 1.15; keep it out of our field metadata.
        private static readonly List<object> pausedDelayed = new();
        internal static bool Frozen(Character c) => c != null && frozen.ContainsKey(c);
        private static Character Owner(Item item)
        {
            while (item?.ParentInventory != null)
            {
                if (item.ParentInventory.Owner is Character c) return c;
                item = item.ParentInventory.Owner as Item;
            }
            return null;
        }
        private static bool TargetFrozen(ISerializableEntity target)
            => target is Character c ? Frozen(c) : target is Limb l ? Frozen(l.character)
                : target is Item i && Frozen(Owner(i));
        private static void Add(Pose pose, PhysicsBody body)
        {
            if (body == null || pose.Bodies.ContainsKey(body)) return;
            pose.Bodies.Add(body, new BodyState(body.SimPosition, body.Rotation, body.LinearVelocity,
                body.AngularVelocity, body.BodyType));
            body.BodyType = BodyType.Kinematic;
            body.LinearVelocity = Vector2.Zero; body.AngularVelocity = 0;
        }
        private static void Refresh()
        {
            if (frozen.Count == 0 && !ChrononStasis.HasFreezingField) return;
            released.Clear();
            foreach (var pair in frozen)
                if (!ChrononStasis.ShouldFreeze(pair.Key)) released.Add(pair.Key);
            foreach (var c in released)
            {
                Restore(frozen[c]); frozen.Remove(c);
            }
            foreach (var c in Character.CharacterList)
            {
                if (!ChrononStasis.ShouldFreeze(c) || frozen.ContainsKey(c)) continue;
                var pose = new Pose();
                Add(pose, c.AnimController?.Collider);
                if (c.AnimController != null)
                    foreach (var limb in c.AnimController.Limbs) if (!limb.IsSevered) Add(pose, limb.body);
                foreach (var item in Item.ItemList) if (Owner(item) == c) Add(pose, item.body);
                frozen.Add(c, pose);
            }
            Hold();
        }
        private static void Hold()
        {
            foreach (var pose in frozen.Values)
                foreach (var pair in pose.Bodies)
                {
                    var b = pair.Key;
                    if (b.Removed) continue;
                    b.BodyType = BodyType.Kinematic;
                    b.SetTransform(pair.Value.Position, pair.Value.Rotation, true);
                    b.LinearVelocity = Vector2.Zero; b.AngularVelocity = 0;
                    b.FarseerBody.ResetDynamics();
                }
        }
        private static void Restore(Pose pose)
        {
            foreach (var pair in pose.Bodies)
            {
                var b = pair.Key;
                if (b.Removed) continue;
                b.BodyType = pair.Value.Type;
                b.LinearVelocity = pair.Value.Velocity;
                b.AngularVelocity = pair.Value.AngularVelocity;
            }
        }
        internal static void Clear()
        {
            foreach (var pose in frozen.Values) Restore(pose);
            frozen.Clear(); released.Clear();
            RestoreEffects();
        }
        private static bool CharacterUpdate(Character __instance) => !Frozen(__instance);
        private static bool RagdollUpdate(Ragdoll __instance) => !Frozen(__instance.Character);
        private static bool HealthUpdate(CharacterHealth __instance) => !Frozen(__instance.Character);
        private static bool ItemUpdate(Item __instance, float deltaTime, Camera cam)
        {
            if (frozen.Count == 0) return true;
            if (!Frozen(Owner(__instance))) return true;
            // Field lifetime belongs to world time, even when two casters freeze one another.
            __instance.GetComponent<ChrononStasis>()?.Update(deltaTime, cam);
            return false;
        }
        private static void PauseEffects()
        {
            if (frozen.Count == 0) return;
            for (int i = DelayedEffect.DelayList.Count - 1; i >= 0; i--)
            {
                var entry = DelayedEffect.DelayList[i];
                var targets = entry.Targets.FindAll(TargetFrozen);
                if (targets.Count == 0) continue;
                if (targets.Count == entry.Targets.Count)
                {
                    DelayedEffect.DelayList.RemoveAt(i); pausedDelayed.Add(entry);
                }
                else
                {
                    var paused = new DelayedListElement(entry.Parent, entry.Entity, targets, entry.Delay,
                        entry.WorldPosition, entry.StartPosition);
                    paused.GetPositionBasedOnTargets = entry.GetPositionBasedOnTargets;
                    entry.Targets.RemoveAll(TargetFrozen); pausedDelayed.Add(paused);
                }
            }
            // Separate mixed-target timers so outside targets keep their original time flow.
            for (int i = StatusEffect.DurationList.Count - 1; i >= 0; i--)
            {
                var entry = StatusEffect.DurationList[i];
                var targets = entry.Targets.FindAll(TargetFrozen);
                if (targets.Count == 0) continue;
                if (targets.Count == entry.Targets.Count)
                {
                    StatusEffect.DurationList.RemoveAt(i); pausedEffects.Add(entry);
                }
                else
                {
                    var paused = new DurationListElement(entry.Parent, entry.Entity, targets, entry.Duration, entry.User);
                    paused.Timer = entry.Timer;
                    entry.Targets.RemoveAll(TargetFrozen); pausedEffects.Add(paused);
                }
            }
        }
        private static void RestoreEffects()
        {
            StatusEffect.DurationList.AddRange(pausedEffects); pausedEffects.Clear();
            foreach (var entry in pausedDelayed) DelayedEffect.DelayList.Add((DelayedListElement)entry);
            pausedDelayed.Clear();
        }
        internal static void Install(Harmony harmony)
        {
            void Before(Type type, string method, string patch, Type[] args = null)
                => harmony.Patch(AccessTools.Method(type, method, args), prefix:
                    new HarmonyMethod(typeof(ChrononFreeze), patch) { priority = Priority.First });
            Before(typeof(Character), "UpdateAll", nameof(Refresh));
            Before(typeof(Character), "Update", nameof(CharacterUpdate), new[] { typeof(float), typeof(Camera) });
            Before(typeof(AICharacter), "Update", nameof(CharacterUpdate), new[] { typeof(float), typeof(Camera) });
            Before(typeof(Ragdoll), "UpdateRagdoll", nameof(RagdollUpdate));
            Before(typeof(HumanoidAnimController), "UpdateAnim", nameof(RagdollUpdate));
            Before(typeof(FishAnimController), "UpdateAnim", nameof(RagdollUpdate));
            Before(typeof(CharacterHealth), "Update", nameof(HealthUpdate));
            Before(typeof(Item), "Update", nameof(ItemUpdate), new[] { typeof(float), typeof(Camera) });
            Before(typeof(World), "Step", nameof(Hold), new[] { typeof(float) });
            harmony.Patch(AccessTools.Method(typeof(StatusEffect), "UpdateAll"),
                prefix: new HarmonyMethod(typeof(ChrononFreeze), nameof(PauseEffects)),
                finalizer: new HarmonyMethod(typeof(ChrononFreeze), nameof(RestoreEffects)));
        }
    }
}

