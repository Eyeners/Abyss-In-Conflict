using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    // Gravity locomotion is separate from the existing zero-gravity wall walker.
    public sealed class HumanJumpClimb : IAssemblyPlugin
    {
        private Harmony harmony;
        private sealed class State
        {
            internal bool Up, Air, LeftGround, Climbing, Mantle;
            internal int MantleStage;
            internal float MantleTime;
            internal float StageTime;
            internal bool RearCleared;
            internal float DeltaTime;
            internal float SupportTime;
            internal float LandingTime, LegExtension;
            internal readonly Vector2[] Palms = new Vector2[2];
            internal bool PalmsReady;
            internal int MovingPalm = -1, NextPalm;
            internal Vector2 PalmStart, PalmGoal;
            internal float PalmTime;
            internal int OriginalCollider = -1;
            internal bool OriginalFixedRotation;
            internal Vector2 Edge;
            internal float TraversePhase;
            internal int TraverseDirection;
            internal bool Traversed;
            internal PhysicsBody Collider;
            internal float Time, Lock, ClimbSpeed, Phase;
            internal int Side;
            internal Structure Wall;
            internal Vector2 Contact;
            internal Submarine Sub;
            internal readonly List<Lease> Leases = new();
        }
        private struct Lease
        {
            internal Limb Limb;
            internal bool Enabled;
            internal float Force;
            internal Vector2 Anchor, Target;
        }
        private sealed class Saved
        {
            internal bool Active, Restored, Swimming, Standing;
            internal float Lock;
            internal Vector2 Movement;
        }
        private static readonly Dictionary<Character, State> states = new();
        private static double nextCleanup;
        internal static bool Controlling(Character c) => c != null && states.TryGetValue(c, out var s) && (s.Air || s.Climbing || s.Mantle);
        internal static bool ClimbingOrMantling(Character c) => c != null && states.TryGetValue(c, out var s) && (s.Climbing || s.Mantle);
        private static InvSlotType ReservedHands(Character c)
        {
            if (c == null || c.Removed || !states.TryGetValue(c, out var s) || (!s.Climbing && !s.Mantle)) return InvSlotType.None;
            return InvSlotType.LeftHand | (s.Mantle && s.MantleStage >= 0 ? InvSlotType.RightHand : InvSlotType.None);
        }
        private static bool EquipSlots(CharacterInventory __instance, ref IEnumerable<InvSlotType> __2, ref bool __result)
        {
            var reserved = ReservedHands(__instance.Owner as Character);
            if (reserved == InvSlotType.None || __2 == null) return true;
            __2 = __2.Where(slot => (slot & reserved) == 0).ToArray();
            if (__2.Any()) return true;
            __result = false; return false;
        }
        private static bool EquipIndex(CharacterInventory __instance, Item __0, int __1, ref bool __result)
        {
            var reserved = ReservedHands(__instance.Owner as Character);
            if (reserved == InvSlotType.None || __0 == null || __1 < 0 || __1 >= __instance.SlotTypes.Length) return true;
            var slot = __instance.SlotTypes[__1];
            if ((slot & reserved) == 0 && __0.AllowedSlots.Any(option => (option & slot) != 0 && (option & reserved) == 0)) return true;
            __result = false; return false;
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.human.jump.climb");
            harmony.Patch(AccessTools.Method(typeof(CharacterInventory), "TryPutItem", new[] { typeof(Item), typeof(Character), typeof(IEnumerable<InvSlotType>), typeof(bool), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(HumanJumpClimb), nameof(EquipSlots)));
            harmony.Patch(AccessTools.Method(typeof(CharacterInventory), "TryPutItem", new[] { typeof(Item), typeof(int), typeof(bool), typeof(bool), typeof(Character), typeof(bool), typeof(bool), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(HumanJumpClimb), nameof(EquipIndex)));
            harmony.Patch(AccessTools.Method(typeof(CharacterInventory), "CanBePutInSlot", new[] { typeof(Item), typeof(int), typeof(bool) }),
                prefix: new HarmonyMethod(typeof(HumanJumpClimb), nameof(EquipIndex)));
            var before = new HarmonyMethod(typeof(HumanJumpClimb), nameof(Begin)) { priority = Priority.First };
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"), prefix: before,
                postfix: new HarmonyMethod(typeof(HumanJumpClimb), nameof(End)),
                finalizer: new HarmonyMethod(typeof(HumanJumpClimb), nameof(Restore)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateSwimming"),
                prefix: new HarmonyMethod(typeof(HumanJumpClimb), nameof(Swim)));
        }
        private static bool Valid(Character c)
        {
            if (!MovementState.ActiveOnLand(c) || !c.IsHuman || c.IsAIControlled || MovementState.SelectingLadder(c)
                || c.AnimController.Frozen || GameMain.World.Gravity.Y >= -0.1f
                || HoverBike.Riding(c) || HoverJetpack.Flying(c) || FoehnJetJump.Equipped(c) != null
                || ActiveCrawl.IsCrawling(c) || MagneticChainPack.Equipped(c)?.IsOn == true) return false;
            foreach (var line in PoweredZipline.Lines) if (line.Rider == c) return false;
#if CLIENT
            if (c == Character.Controlled && (!GameMain.WindowActive || Screen.Selected is not GameScreen
                || GUI.KeyboardDispatcher.Subscriber != null || GUI.InputBlockingMenuOpen)) return false;
#endif
            return true;
        }
        private static bool Free(Character c, InvSlotType slot) => c.Inventory?.GetItemInLimbSlot(slot) == null;
        private static bool Healthy(HumanoidAnimController a, LimbType t)
        { var l = a.GetLimb(t); return l != null && !l.Removed && !l.IsSevered && l.body?.Enabled == true; }
        private static void Release(State s)
        {
            foreach (var p in s.Leases)
            {
                var l = p.Limb;
                if (l == null || l.Removed || l.IsSevered || l.body == null) continue;
                l.PullJointWorldAnchorA = l.body.FarseerBody.GetWorldPoint(p.Anchor);
                l.PullJointWorldAnchorB = p.Target;
                l.PullJointMaxForce = p.Force; l.PullJointEnabled = p.Enabled;
            }
            s.Leases.Clear();
        }
        private static void Bodies(HumanoidAnimController a, Action<PhysicsBody> action)
        {
            var seen = new HashSet<PhysicsBody>();
            if (a.Collider?.Enabled == true) { seen.Add(a.Collider); action(a.Collider); }
            foreach (var l in a.Limbs)
                if (!l.Removed && !l.IsSevered && l.body?.Enabled == true && seen.Add(l.body)) action(l.body);
        }
        private static void Launch(HumanoidAnimController a, State s, float horizontal)
        {
            RestoreCollider(a, s);
            var boots = MagneticBoots.Get(a.Character);
            boots?.Release(); boots?.WallWalk.Reset();
            float vertical = (float)Math.Sqrt(2 * -GameMain.World.Gravity.Y * 1f);
            Vector2 change = new Vector2(horizontal, vertical) - a.Collider.LinearVelocity;
            Bodies(a, b => b.LinearVelocity += change);
            s.Climbing = s.Mantle = false; s.Wall = null; s.Air = true; s.LeftGround = false;
            s.LandingTime = s.LegExtension = 0;
            s.Time = 0; s.Lock = 0.4f; a.onGround = false;
            if (horizontal != 0) a.TargetDir = horizontal > 0 ? Direction.Right : Direction.Left;
        }
        private static bool Scan(HumanoidAnimController a, State s, int side)
        {
            Structure found = null; Vector2 contact = default; float closest = 1;
            Vector2 start = a.Collider.SimPosition + new Vector2(0, 0.25f);
            GameMain.World.RayCast((fixture, p, n, fraction) =>
            {
                if (fixture.IsSensor || fixture.Body.UserData is not Structure wall
                    || wall.Removed || wall.Submarine != a.Character.Submarine
                    || n.X * side > -0.75f || fraction >= closest) return -1;
                found = wall; contact = p; closest = fraction; return fraction;
            }, start, start + new Vector2(side * 0.85f, 0));
            if (found == null) return false;
            s.Wall = found; s.Contact = contact; s.Side = side; s.Sub = a.Character.Submarine;
            return true;
        }
        private static bool TryLedge(HumanoidAnimController a, State s, int side)
        {
            var c = a.Character;
            if (side == 0 || c.Submarine == null || !Free(c, InvSlotType.LeftHand)
                || !Free(c, InvSlotType.RightHand) || !Healthy(a, LimbType.LeftHand)
                || !Healthy(a, LimbType.RightHand)) return false;
            Vector2 hips = a.Collider.SimPosition;
            float scale = MathHelper.Clamp(a.RagdollParams.JointScale / 0.5f, 0.5f, 2);
            var leftHand = a.GetLimb(LimbType.LeftHand);
            var rightHand = a.GetLimb(LimbType.RightHand);
            float reach = 0.3f * scale;
            Structure found = null; Vector2 edge = default; float best = float.MaxValue;
            // The raised arms no longer fit the old hips+1.05 search window.
            // Probe around each actual palm, then rank reachable top contacts.
            foreach (var hand in new[] { leftHand, rightHand })
            for (int probe = -1; probe <= 2; probe++)
            {
                float x = hand.SimPosition.X + side * probe * 0.09f * scale;
                Vector2 top = new Vector2(x, hand.SimPosition.Y + reach);
                Vector2 end = new Vector2(x, hand.SimPosition.Y - reach);
                GameMain.World.RayCast((f, p, n, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                        || wall.Submarine != c.Submarine || n.Y < 0.8f) return -1;
                    float distance = Vector2.DistanceSquared(hand.SimPosition, p);
                    if (distance > reach * reach || distance >= best) return -1;
                    found = wall; edge = p; best = distance; return fraction;
                }, top, end);
            }
            if (found == null) return false;
            // Catching only needs an unobstructed approach. Standing clearance is
            // checked on the next W press, so a low ceiling does not prevent hanging.
            bool blocked = false;
            void Clear(Vector2 from, Vector2 to)
            {
                GameMain.World.RayCast((f, p, n, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                        || wall.Submarine != c.Submarine) return -1;
                    blocked = true; return 0;
                }, from, to);
            }
            Clear(new Vector2(hips.X, edge.Y + 0.1f), edge + new Vector2(0, 0.1f));
            if (blocked) return false;
            s.Edge = edge; s.Wall = found; s.Sub = c.Submarine; s.Side = side;
            s.TraversePhase = 0; s.TraverseDirection = 0; s.Traversed = false;
            s.Mantle = true; s.MantleStage = -1; s.MantleTime = s.StageTime = 0; s.Climbing = s.Air = false;
            MagneticBoots.Get(c)?.Release();
            return true;
        }
        private static bool CanStand(HumanoidAnimController a, State s)
        {
            var c = a.Character;
            float scale = MathHelper.Clamp(a.RagdollParams.JointScale / 0.5f, 0.5f, 2);
            Vector2 edge = s.Edge; int side = s.Side;
            bool blocked = false;
            for (int i = 0; i < 3; i++)
            {
                var bottom = edge + new Vector2(side * i * 0.2f * scale, 0.08f);
                GameMain.World.RayCast((f, p, n, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                        || wall.Submarine != c.Submarine) return -1;
                    blocked = true; return 0;
                }, bottom, bottom + new Vector2(0, 1.9f * scale));
            }
            if (blocked) return false;
            // The short landing step must have a top beneath it, not empty space
            // beyond a thin post. Keep the same collider margin used by the mantle.
            bool supported = false;
            float landingX = edge.X + side * Math.Max(a.Collider.Radius + 0.06f, 0.25f * scale);
            GameMain.World.RayCast((f, p, n, fraction) =>
            {
                if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                    || wall.Submarine != c.Submarine || n.Y < 0.8f) return -1;
                supported = true; return 0;
            }, new Vector2(landingX, edge.Y + 0.08f), new Vector2(landingX, edge.Y - 0.2f));
            return supported;
        }
        private static void Traverse(HumanoidAnimController a, State s, int direction, float dt)
        {
            s.TraverseDirection = 0;
            if (direction == 0 || s.Wall?.IsPlatform != true
                || !Free(a.Character, InvSlotType.LeftHand) || !Free(a.Character, InvSlotType.RightHand)) return;
            float scale = MathHelper.Clamp(a.RagdollParams.JointScale / 0.5f, 0.5f, 2);
            Vector2 candidate = s.Edge + new Vector2(direction * 0.55f * dt, 0);
            Structure support = null;
            // Check the next grip and its leading-hand margin; never traverse a gap.
            for (int i = 0; i < 2; i++)
            {
                bool found = false;
                float x = candidate.X + direction * i * 0.18f * scale;
                GameMain.World.RayCast((f, p, n, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                        || !wall.IsPlatform || wall.Submarine != s.Sub || n.Y < 0.8f
                        || Math.Abs(p.Y - s.Edge.Y) > 0.08f) return -1;
                    found = true; support = wall; return 0;
                }, new Vector2(x, s.Edge.Y + 0.1f), new Vector2(x, s.Edge.Y - 0.12f));
                if (!found) return;
            }
            // A wall below the platform can block the hanging body even with a valid grip.
            bool blocked = false;
            Vector2 start = a.Collider.SimPosition;
            for (int i = 0; i < 3; i++)
            {
                Vector2 origin = start + new Vector2(0, (i - 1) * 0.35f * scale);
                GameMain.World.RayCast((f, p, n, fraction) =>
                {
                    if (f.IsSensor || f.Body.UserData is not Structure wall || wall.Removed
                        || wall.IsPlatform || wall.Submarine != s.Sub) return -1;
                    blocked = true; return 0;
                }, origin, origin + new Vector2(direction * (a.Collider.Radius + 0.15f), 0));
            }
            if (blocked || Vector2.DistanceSquared(start, s.Edge + new Vector2(0, -1.2f * scale)) > 1.4f * 1.4f) return;
            s.Edge = candidate; s.Wall = support; s.Traversed = true;
            s.TraverseDirection = direction; s.TraversePhase += dt * 4.5f;
        }
        private static void Begin(HumanoidAnimController __instance, float __0, out Saved __state)
        {
            __state = new Saved();
            if (Timing.TotalTime >= nextCleanup)
            {
                nextCleanup = Timing.TotalTime + 2;
                foreach (var old in new List<Character>(states.Keys))
                    if (old.Removed) { Release(states[old]); states.Remove(old); }
            }
            var c = __instance.Character;
            if (!states.TryGetValue(c, out var s)) states[c] = s = new State();
            Release(s);
            bool up = c.IsKeyDown(InputType.Up);
            bool press = up && !s.Up; s.Up = up;
            float dt = MathHelper.Clamp(__0, 0, 0.1f);
            s.DeltaTime = dt;
            s.Lock -= dt; s.Time += dt;
            if (!Valid(c)) { RestoreCollider(__instance, s); s.Air = s.Climbing = s.Mantle = false; s.Wall = null; return; }
            bool simulate = GameMain.NetworkMember?.IsClient != true;
#if CLIENT
            simulate |= c == Character.Controlled;
#endif
            if (!simulate) return;
            int horizontal = (c.IsKeyDown(InputType.Right) ? 1 : 0) - (c.IsKeyDown(InputType.Left) ? 1 : 0);
            s.Collider = __instance.Collider;
            if (s.Mantle)
            {
                if (s.MantleStage == -1) Traverse(__instance, s, horizontal, dt);
                if (s.MantleStage >= 0) { s.MantleTime += dt; s.StageTime += dt; }
                if (s.MantleStage == -1 && press && Free(c, InvSlotType.RightHand) && CanStand(__instance, s))
                {
                    s.MantleStage = 0; s.MantleTime = s.StageTime = 0; s.RearCleared = false;
                    s.SupportTime = 0; s.PalmsReady = false; s.MovingPalm = -1;
                    s.OriginalCollider = __instance.ColliderIndex;
                    s.OriginalFixedRotation = __instance.Collider.FarseerBody.FixedRotation;
                    if (__instance.TryGetCollider(1, out var small) && small != null) __instance.ColliderIndex = 1;
                    __instance.Collider.FarseerBody.FixedRotation = false;
                    s.Collider = __instance.Collider;
                }
                if (s.Sub != c.Submarine || s.Wall == null || s.Wall.Removed || s.MantleTime > 7f
                    || !Free(c, InvSlotType.LeftHand) || (s.MantleStage >= 0 && !Free(c, InvSlotType.RightHand))
                    || c.IsKeyDown(InputType.Down))
                {
                    RestoreCollider(__instance, s); s.Mantle = false; s.Lock = 0.5f;
                    Bodies(__instance, b => b.LinearVelocity = new Vector2(0, Math.Min(0, b.LinearVelocity.Y)));
                    if (c.IsKeyDown(InputType.Down) && MagneticBoots.EnabledFor(c)
                        && Scan(__instance, s, s.Side)) s.Climbing = true;
                }
            }
            if (!s.Mantle && up && s.Lock <= 0 && (s.Climbing || s.Air || !__instance.OnGround))
                TryLedge(__instance, s, s.Climbing ? s.Side : horizontal != 0 ? horizontal : (__instance.Dir > 0 ? 1 : -1));
            var boots = MagneticBoots.Get(c);
            bool canClimb = MagneticBoots.EnabledFor(c) && boots.WallWalkEnabled && c.Submarine != null
                && Free(c, InvSlotType.LeftHand) && Healthy(__instance, LimbType.LeftHand)
                && Healthy(__instance, LimbType.LeftFoot) && Healthy(__instance, LimbType.RightFoot);
            if (s.Climbing)
            {
                if (!canClimb || s.Sub != c.Submarine || !Scan(__instance, s, s.Side))
                { s.Climbing = false; s.Lock = 0.25f; }
                else if (horizontal == -s.Side) Launch(__instance, s, -s.Side * 3f);
            }
            if (!s.Mantle && !s.Climbing && canClimb && s.Lock <= 0 && horizontal != 0
                && Scan(__instance, s, horizontal))
            { s.Climbing = true; s.Air = false; boots.Release(); boots.WallWalk.Reset(); }
            if (s.Climbing)
            {
                s.ClimbSpeed = Free(c, InvSlotType.RightHand) && Healthy(__instance, LimbType.RightHand)
                    ? ((up ? 1 : 0) - (c.IsKeyDown(InputType.Down) ? 1 : 0)) * 0.8f : 0;
                s.Phase += Math.Abs(s.ClimbSpeed) * dt * 7;
            }
            else if (!s.Mantle)
            {
                bool wasAir = s.Air;
                if (s.Air)
                {
                    if (!__instance.OnGround) s.LeftGround = true;
                    s.LandingTime = s.Time > 0.15f && s.LeftGround && __instance.OnGround
                        && __instance.Collider.LinearVelocity.Y <= 0.3f ? s.LandingTime + dt : 0;
                    if (s.LandingTime >= 0.06f || s.Time > 3) s.Air = false;
                }
                if (!wasAir && !s.Air && press && s.Lock <= 0 && __instance.OnGround && !c.IsKeyDown(InputType.Crouch))
                    Launch(__instance, s, horizontal * (c.IsKeyDown(InputType.Run) ? 4f : 2f));
            }
            if (!s.Climbing && !s.Air && !s.Mantle) return;
            __state.Active = true; __state.Swimming = __instance.swimming;
            __state.Standing = __instance.forceStanding; __state.Lock = __instance.swimmingStateLockTimer;
            __state.Movement = __instance.TargetMovement;
            __instance.swimming = true; __instance.forceStanding = false;
            __instance.swimmingStateLockTimer = 10; __instance.TargetMovement = Vector2.Zero;
        }
        private static bool Swim(HumanoidAnimController __instance) => !Controlling(__instance.Character);
        private static void Restore(HumanoidAnimController __instance, Saved __state)
        {
            if (__state == null || !__state.Active || __state.Restored) return;
            __instance.swimming = __state.Swimming; __instance.forceStanding = __state.Standing;
            __instance.swimmingStateLockTimer = __state.Lock; __instance.TargetMovement = __state.Movement;
            __state.Restored = true;
        }
        private static void Hold(State s, Limb l, Vector2 target, float angle, float strength = 100)
        {
            if (l == null || l.Removed || l.IsSevered || l.body?.Enabled != true) return;
            Vector2 acceleration = (target - l.SimPosition) * strength
                + (s.Collider.LinearVelocity - l.body.LinearVelocity) * 10;
            if (acceleration.LengthSquared() > 10000) acceleration = Vector2.Normalize(acceleration) * 100;
            Vector2 force = acceleration * l.Mass;
            l.body.ApplyForce(force);
            // Internal pose forces must not brake translation or support the entire rig in midair.
            s.Collider.ApplyForce(-force);
            l.body.SmoothRotate(angle, Math.Max(1, l.Mass) * 15);
        }
        private static void End(HumanoidAnimController __instance, Saved __state)
        {
            Restore(__instance, __state);
            if (!__state.Active || !states.TryGetValue(__instance.Character, out var s)) return;
            var a = __instance; var c = a.Character;
            if (s.Mantle && s.MantleStage >= 0 && s.OriginalCollider >= 0)
            {
                if (a.TryGetCollider(1, out var small) && small != null) a.ColliderIndex = 1;
                a.Collider.FarseerBody.FixedRotation = false; s.Collider = a.Collider;
            }
            a.levitatingCollider = false;
            // Suspend all native ground/hand pull targets for this physics step.
            foreach (var l in a.Limbs)
            {
                if (l.Removed || l.IsSevered || l.body == null || !l.PullJointEnabled) continue;
                if (((l.type == LimbType.LeftHand || l.type == LimbType.LeftArm || l.type == LimbType.LeftForearm)
                        && !Free(c, InvSlotType.LeftHand))
                    || ((l.type == LimbType.RightHand || l.type == LimbType.RightArm || l.type == LimbType.RightForearm)
                        && !Free(c, InvSlotType.RightHand))) continue;
                s.Leases.Add(new Lease { Limb = l, Enabled = l.PullJointEnabled, Force = l.PullJointMaxForce,
                    Anchor = l.PullJointLocalAnchorA, Target = l.PullJointWorldAnchorB });
                l.PullJointEnabled = false;
            }
            float scale = MathHelper.Clamp(a.RagdollParams.JointScale / 0.5f, 0.5f, 2);
            float dir = s.Climbing || s.Mantle ? s.Side : a.Dir;
            Vector2 hips = a.Collider.SimPosition;
            Vector2 torso = hips + new Vector2(0, 0.35f * scale);
            if (s.Mantle)
            {
                float clearance = a.Collider.Radius + 0.12f;
                float liftY = s.Edge.Y + Math.Max(clearance, 0.38f * scale);
                if (s.MantleStage == 0 && hips.Y >= liftY - 0.06f && s.StageTime > 0.4f
                    && Math.Abs(a.Collider.Rotation + dir * MathHelper.PiOver2) < 0.35f)
                { s.MantleStage = 1; s.StageTime = 0; }
                // Hang until a new W press. Once lifted, take only a small inward step.
                float inward = Math.Max(a.Collider.Radius + 0.06f, 0.25f * scale);
                Vector2 goal = s.MantleStage == -1
                    ? s.Edge + new Vector2(s.Traversed ? 0 : -s.Side * 0.15f * scale, -1.2f * scale)
                    : new Vector2(s.MantleStage == 0 ? s.Edge.X - s.Side * (a.Collider.Height * 0.5f + a.Collider.Radius + 0.05f)
                    : s.Edge.X + s.Side * (inward), liftY);
                if (s.MantleStage == 3)
                    goal.Y = MathHelper.Lerp(liftY, s.Edge.Y + 0.85f * scale,
                        MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.4f, 0, 1)));
                Vector2 desired = (goal - hips) * (s.MantleStage == 3 ? 8 : 5);
                if (desired.LengthSquared() > 4) desired = Vector2.Normalize(desired) * 2;
                Bodies(a, b => b.ApplyForce(((desired - b.LinearVelocity) * 12 - GameMain.World.Gravity) * b.Mass));
                Vector2 leadPlant = Plant(a, s, true, scale);
                var leadShin = a.GetLimb(LimbType.LeftLeg);
                bool kneeSupported = leadShin != null && !leadShin.IsSevered
                    && leadShin.SimPosition.Y >= s.Edge.Y - 0.04f * scale
                    && (leadShin.SimPosition.X - s.Edge.X) * s.Side >= -0.08f * scale;
                if (s.MantleStage == 1 && Math.Abs(hips.X - goal.X) < 0.16f && s.StageTime > 0.35f
                    && (kneeSupported || Planted(a.GetLimb(LimbType.LeftFoot), leadPlant, scale)))
                { s.MantleStage = 2; s.StageTime = 0; Bodies(a, b => b.LinearVelocity = Vector2.Zero); }
                // Feet may intersect one-way platforms or remain just outside a
                // solid lip. Once the rig is over the top, finish by standing up
                // rather than waiting indefinitely for exact foot contacts.
                bool bodyOverTop = (hips.X - s.Edge.X) * s.Side >= 0.08f * scale
                    && hips.Y >= s.Edge.Y + 0.2f * scale
                    && Math.Abs(hips.X - goal.X) < 0.3f * scale;
                s.SupportTime = s.MantleStage == 2 && bodyOverTop ? s.SupportTime + s.DeltaTime : 0;
                if (s.MantleStage == 2 && s.StageTime > 0.3f && s.SupportTime >= 0.05f)
                { s.MantleStage = 3; s.StageTime = 0; }
                if (s.MantleStage == 3 && s.StageTime > 0.45f && Math.Abs(hips.Y - goal.Y) < 0.1f
                    && Math.Abs(hips.X - goal.X) < 0.08f)
                {
                    Bodies(a, b => b.LinearVelocity = Vector2.Zero);
                    RestoreCollider(a, s); s.Mantle = false; s.Lock = 0.7f; Release(s); return;
                }
                a.TargetDir = s.Side > 0 ? Direction.Right : Direction.Left;
            }
            else if (s.Climbing)
            {
                float x = s.Contact.X - s.Side * 0.45f * scale;
                Vector2 desired = new Vector2(MathHelper.Clamp((x - hips.X) * 10, -2, 2), s.ClimbSpeed);
                Bodies(a, b => b.ApplyForce(((desired - b.LinearVelocity) * 10 - GameMain.World.Gravity) * b.Mass));
                a.TargetDir = s.Side > 0 ? Direction.Right : Direction.Left;
            }
            float lowBlend = s.Mantle && s.MantleStage >= 0
                ? s.MantleStage == 3 ? 1 - MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.4f, 0, 1)) : 1 : 0;
            a.Collider.SmoothRotate(-dir * MathHelper.PiOver2 * lowBlend, Math.Max(1, a.Collider.Mass) * 20);
            float mantleBend = !s.Mantle || s.MantleStage < 0 ? 0
                : s.MantleStage == 0 ? MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.4f, 0, 1))
                : s.MantleStage <= 2 ? 1 : 1 - MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.4f, 0, 1));
            Vector2 poseHips = hips + new Vector2(0, -0.1f * mantleBend * scale);
            if (s.Mantle && s.MantleStage >= 1)
            {
                float settle = s.MantleStage == 1 ? MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.35f, 0, 1)) : 1;
                float supportedHeight = s.Edge.Y + MathHelper.Lerp(0.8f, 0.46f, mantleBend) * scale;
                poseHips.Y = MathHelper.Lerp(poseHips.Y, supportedHeight, settle);
                torso = poseHips + new Vector2(0, 0.35f * scale);
            }
            torso += new Vector2(dir * 0.3f, -0.28f) * (mantleBend * scale);
            Hold(s, a.GetLimb(LimbType.Waist), poseHips, -dir * mantleBend * 0.25f);
            Hold(s, a.GetLimb(LimbType.Torso), torso, -dir * (0.12f + mantleBend * 1.05f));
            Hold(s, a.GetLimb(LimbType.Head), torso + new Vector2(dir * 0.25f * mantleBend, 0.38f - 0.2f * mantleBend) * scale, 0);
            float extension = MathHelper.Clamp((1.5f - a.Collider.LinearVelocity.Y) / 3, 0, 1);
            // Once extended for landing, contact impulses must not tuck the legs again.
            if (s.Air) { s.LegExtension = Math.Max(s.LegExtension, extension); extension = s.LegExtension; }
            for (int i = 0; i < 2; i++)
            {
                float stride = s.Climbing ? (float)Math.Sin(s.Phase + i * MathHelper.Pi) * 0.16f : 0;
                Vector2 knee = hips + new Vector2(dir * 0.25f, -0.25f + stride) * scale;
                Vector2 foot = s.Climbing
                    ? new Vector2(s.Contact.X - s.Side * 0.07f, hips.Y + (-0.48f + stride) * scale)
                    : hips + new Vector2(dir * 0.03f, s.Mantle ? -0.35f : MathHelper.Lerp(-0.58f, -0.82f, extension)) * scale;
                if (s.Mantle && s.MantleStage >= 0)
                {
                    // Feet become fixed surface targets, independent of the rising
                    // collider. First leg supports a low crouch while the other joins.
                    bool lead = i == 0;
                    Vector2 standingFoot = hips + new Vector2(dir * (lead ? 0.1f : -0.08f), -0.8f) * scale;
                    Vector2 tuckKnee = poseHips + new Vector2(dir * (lead ? 0.42f : 0.08f), lead ? 0.02f : -0.36f) * scale;
                    Vector2 tuckFoot = poseHips + new Vector2(dir * (lead ? 0.14f : -0.08f), lead ? -0.27f : -0.75f) * scale;
                    Vector2 kneePoint;
                    if (s.MantleStage >= 1)
                    {
                        float step = lead ? MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.3f, 0, 1))
                            : s.MantleStage == 1 ? 0 : MathHelper.SmoothStep(0, 1, MathHelper.Clamp(s.StageTime / 0.5f, 0, 1));
                        if ((lead && s.MantleStage >= 2) || s.MantleStage >= 3) step = 1;
                        Vector2 onTop = Plant(a, s, lead, scale);
                        foot = Vector2.Lerp(tuckFoot, onTop, step);
                        if (!lead && s.MantleStage <= 2)
                        {
                            Vector2 outside = s.Edge + new Vector2(-dir * 0.22f, 0.2f) * scale;
                            var trailing = a.GetLimb(LimbType.RightFoot);
                            if (s.MantleStage == 2 && trailing != null && trailing.SimPosition.Y >= s.Edge.Y + 0.1f * scale)
                                s.RearCleared = true;
                            foot = s.MantleStage == 1 ? s.Edge + new Vector2(-dir * 0.25f, -0.35f) * scale
                                : s.RearCleared ? onTop : outside;
                        }
                        Vector2 crouchKnee = onTop + new Vector2(dir * 0.18f, 0.27f) * scale;
                        Vector2 standingKnee = Vector2.Lerp(poseHips, onTop, 0.5f) + new Vector2(dir * 0.02f * scale, 0);
                        kneePoint = Vector2.Lerp(tuckKnee, Vector2.Lerp(standingKnee, crouchKnee, mantleBend), step);
                    }
                    else
                    {
                        foot = Vector2.Lerp(standingFoot, tuckFoot, mantleBend);
                        kneePoint = Vector2.Lerp(poseHips + new Vector2(dir * 0.03f, -0.4f) * scale, tuckKnee, mantleBend);
                    }
                    float thighAngle = (float)Math.Atan2(kneePoint.Y - poseHips.Y, kneePoint.X - poseHips.X) + MathHelper.PiOver2;
                    float shinAngle = (float)Math.Atan2(foot.Y - kneePoint.Y, foot.X - kneePoint.X) + MathHelper.PiOver2;
                    Hold(s, a.GetLimb(i == 0 ? LimbType.LeftThigh : LimbType.RightThigh),
                        (poseHips + kneePoint) * 0.5f, thighAngle);
                    Hold(s, a.GetLimb(i == 0 ? LimbType.LeftLeg : LimbType.RightLeg),
                        (kneePoint + foot) * 0.5f, shinAngle);
                }
                else if (s.Air)
                {
                    Vector2 thigh = hips + new Vector2(dir * MathHelper.Lerp(0.15f, 0.035f, extension),
                        MathHelper.Lerp(-0.19f, -0.23f, extension)) * scale;
                    Vector2 shin = hips + new Vector2(dir * MathHelper.Lerp(0.10f, 0.025f, extension),
                        MathHelper.Lerp(-0.43f, -0.57f, extension)) * scale;
                    Hold(s, a.GetLimb(i == 0 ? LimbType.LeftThigh : LimbType.RightThigh),
                        thigh, dir * MathHelper.Lerp(0.6f, 0, extension));
                    Hold(s, a.GetLimb(i == 0 ? LimbType.LeftLeg : LimbType.RightLeg),
                        shin, -dir * MathHelper.Lerp(0.65f, 0, extension));
                }
                else
                {
                Hold(s, a.GetLimb(i == 0 ? LimbType.LeftThigh : LimbType.RightThigh), knee, dir * 0.7f);
                Hold(s, a.GetLimb(i == 0 ? LimbType.LeftLeg : LimbType.RightLeg),
                    Vector2.Lerp(knee, foot, 0.6f), -dir * 0.55f);
                }
                Hold(s, a.GetLimb(i == 0 ? LimbType.LeftFoot : LimbType.RightFoot), foot, 0);
                bool handFree = Free(c, i == 0 ? InvSlotType.LeftHand : InvSlotType.RightHand);
                if (handFree)
                {
                    Vector2 hand = s.Mantle ? (s.MantleStage <= 0
                        ? s.Edge + new Vector2(s.Side * 0.06f, 0.05f)
                        : Vector2.Lerp(s.Edge + new Vector2(s.Side * (i == 0 ? 0.22f : 0.1f) * scale, 0.06f * scale),
                            torso + new Vector2(dir * 0.12f, -0.45f) * scale, s.MantleStage == 3 ? 1 - mantleBend : 0))
                        : s.Climbing ? new Vector2(s.Contact.X - s.Side * 0.025f, torso.Y + (0.48f - stride) * scale)
                        : torso + new Vector2(dir * MathHelper.Lerp(0.62f, 0.3f, MathHelper.Clamp(s.Time / 0.22f, 0, 1)),
                            MathHelper.Lerp(0.32f, 0.85f, MathHelper.Clamp(s.Time / 0.22f, 0, 1))) * scale;
                    if (s.Mantle && (s.MantleStage == 1 || s.MantleStage == 2))
                    {
                        hand = Regrip(s, i, new Vector2(hips.X + dir * 0.3f * scale,
                            s.Edge.Y + 0.06f * scale), scale);
                    }
                    if (s.Mantle && s.MantleStage == -1 && s.Wall?.IsPlatform == true)
                    {
                        // Opposite half cycles: one hand stays on the edge while the
                        // other drops slightly, reaches forward, then grips again.
                        float cycle = s.TraversePhase + i * MathHelper.Pi;
                        float reach = s.TraverseDirection == 0 ? 0 : (float)Math.Sin(cycle) * 0.16f * scale;
                        float lift = s.TraverseDirection == 0 ? 0 : Math.Max(0, (float)Math.Cos(cycle)) * 0.09f * scale;
                        hand = s.Edge + new Vector2((i == 0 ? -0.12f : 0.12f) * scale
                            + s.TraverseDirection * reach, 0.04f - lift);
                    }
                    PoseArm(a, s, i == 0, hand, dir, scale, s.Mantle && s.MantleStage == -1);
                }
            }
        }
        private static Vector2 Regrip(State s, int hand, Vector2 center, float scale)
        {
            Vector2 Goal(int index) => center + new Vector2(s.Side * (index == 0 ? 0.06f : -0.06f) * scale, 0);
            if (!s.PalmsReady)
            {
                s.Palms[0] = Goal(0); s.Palms[1] = Goal(1); s.PalmsReady = true;
            }
            if (hand == 0)
            {
                if (s.MovingPalm < 0)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int index = (s.NextPalm + j) % 2;
                        if (Vector2.DistanceSquared(s.Palms[index], Goal(index)) < 0.18f * 0.18f * scale * scale) continue;
                        s.MovingPalm = index; s.PalmStart = s.Palms[index]; s.PalmGoal = Goal(index); s.PalmTime = 0;
                        break;
                    }
                }
                if (s.MovingPalm >= 0)
                {
                    s.PalmTime += s.DeltaTime;
                    float t = MathHelper.Clamp(s.PalmTime / 0.22f, 0, 1);
                    s.Palms[s.MovingPalm] = Vector2.Lerp(s.PalmStart, s.PalmGoal, MathHelper.SmoothStep(0, 1, t))
                        + Vector2.UnitY * ((float)Math.Sin(t * MathHelper.Pi) * 0.07f * scale);
                    if (t >= 1) { s.NextPalm = 1 - s.MovingPalm; s.MovingPalm = -1; }
                }
            }
            return s.Palms[hand];
        }
        private static Vector2 Plant(HumanoidAnimController a, State s, bool lead, float scale)
        {
            float inward = Math.Max(a.Collider.Radius + 0.06f, 0.25f * scale);
            var foot = a.GetLimb(lead ? LimbType.LeftFoot : LimbType.RightFoot);
            float soleClearance = Math.Max(0.09f * scale, (foot?.body?.GetSize().Y ?? 0.12f * scale) * 0.5f + 0.02f * scale);
            return s.Edge + new Vector2(s.Side * (inward + (lead ? 0.08f : -0.08f) * scale), soleClearance);
        }
        private static void RestoreCollider(HumanoidAnimController a, State s)
        {
            if (s.OriginalCollider < 0) return;
            a.Collider.FarseerBody.FixedRotation = s.OriginalFixedRotation;
            a.ColliderIndex = s.OriginalCollider;
            a.Collider.FarseerBody.FixedRotation = s.OriginalFixedRotation;
            s.Collider = a.Collider; s.OriginalCollider = -1;
        }
        private static bool Planted(Limb foot, Vector2 target, float scale) => foot?.body != null
            && !foot.Removed && !foot.IsSevered && Vector2.DistanceSquared(foot.SimPosition, target) < 0.16f * 0.16f * scale * scale;
        private static void PoseArm(HumanoidAnimController a, State s, bool left, Vector2 target,
            float dir, float scale, bool straight)
        {
            var upper = a.GetLimb(left ? LimbType.LeftArm : LimbType.RightArm);
            var lower = a.GetLimb(left ? LimbType.LeftForearm : LimbType.RightForearm);
            var hand = a.GetLimb(left ? LimbType.LeftHand : LimbType.RightHand);
            if (upper?.body == null || lower?.body == null || hand?.body == null
                || upper.IsSevered || lower.IsSevered || hand.IsSevered) return;
            var torso = a.GetLimb(LimbType.Torso);
            Vector2 shoulder = (torso?.SimPosition ?? s.Collider.SimPosition) + new Vector2(0, 0.2f * scale);
            float u = Math.Max(0.12f, upper.body.GetSize().Y);
            float f = Math.Max(0.12f, lower.body.GetSize().Y);
            float h = Math.Max(0.04f, hand.body.GetSize().Y * 0.5f);
            float lowerLength = f + h;
            Vector2 delta = target - shoulder;
            if (delta.LengthSquared() < 0.0001f) return;
            Vector2 axis = Vector2.Normalize(delta);
            float distance = MathHelper.Clamp(delta.Length(), Math.Abs(u - lowerLength) + 0.01f,
                (u + lowerLength) * (straight ? 0.995f : 0.98f));
            // Solve shoulder/elbow/wrist together. Human arm sprites point down
            // at zero rotation; a fixed -0.7 angle fights an upward reach.
            float along = (u * u - lowerLength * lowerLength + distance * distance) / (2 * distance);
            float bend = (float)Math.Sqrt(Math.Max(0, u * u - along * along));
            Vector2 elbow = shoulder + axis * along + new Vector2(axis.Y, -axis.X) * (bend * dir);
            Vector2 palm = shoulder + axis * distance;
            Vector2 lowerAxis = Vector2.Normalize(palm - elbow);
            float upperAngle = (float)Math.Atan2(elbow.Y - shoulder.Y, elbow.X - shoulder.X) + MathHelper.PiOver2;
            float lowerAngle = (float)Math.Atan2(lowerAxis.Y, lowerAxis.X) + MathHelper.PiOver2;
            Hold(s, upper, (shoulder + elbow) * 0.5f, upperAngle, 160);
            Hold(s, lower, elbow + lowerAxis * (f * 0.5f), lowerAngle, 160);
            Hold(s, hand, palm, lowerAngle, 180);
        }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            foreach (var pair in states)
            {
                Release(pair.Value);
                if (!pair.Key.Removed && pair.Key.AnimController is HumanoidAnimController a)
                    RestoreCollider(a, pair.Value);
            }
            states.Clear(); nextCleanup = 0;
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
