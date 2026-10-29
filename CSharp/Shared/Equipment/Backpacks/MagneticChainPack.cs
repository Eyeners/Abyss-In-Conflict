using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed partial class MagneticChainPack : ItemComponent, IServerSerializable, IClientSerializable
    {
        [Serialize(false, IsPropertySaveable.Yes)] public bool IsOn { get; set; }
        [Serialize(400f, IsPropertySaveable.No)] public float Reach { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float MoveSpeed { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float SprintSpeed { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool AttackMode { get; set; }
        private sealed class Grip
        {
            internal MapEntity Wall;
            internal Fixture Contact;
            internal Vector2 Local;
            internal double Attached, DisabledUntil;
            internal Vector2 World => Local + (Wall?.Submarine?.Position ?? Vector2.Zero);
        }
        private readonly Grip[] grips = { new(), new(), new(), new(), new(), new() };
        private readonly HashSet<PhysicsBody> bodies = new();
        private float scanTimer;
        private int nextGrip;
        private Character owner;
        private Submarine ownerSub;
#if CLIENT
        private readonly Sprite link, claw;
        private readonly Vector2[] tips = new Vector2[9];
        private readonly Vector2[] curve = new Vector2[49];
        private bool tipsReady;
        private double drawTime;
#endif
        public MagneticChainPack(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            InitCombat(element);
            RegisterInterference(this);
#if CLIENT
            link = new Sprite(element.GetChildElement("linksprite"));
            claw = new Sprite(element.GetChildElement("clawsprite"));
#endif
        }
        internal static MagneticChainPack Equipped(Character c) =>
            c?.Inventory?.GetItemInLimbSlot(InvSlotType.Bag)?.GetComponent<MagneticChainPack>();
        private bool Valid(Character c) => Timing.TotalTime >= allDisabledUntil && IsOn && Item.Condition > 0 && MovementState.Conscious(c)
            && c.AnimController is HumanoidAnimController && Equipped(c) == this
            && !c.IsClimbing && !c.IsAttachedToController() && !MovementState.SelectingLadder(c);
        private bool RetainAnchors(Character c) => Valid(c) || Timing.TotalTime >= allDisabledUntil && IsOn && Item.Condition > 0
            && MovementState.Humanoid(c) && !c.IsDead && c.Stun > 0 && Equipped(c) == this;
        private Grip stunAnchor;
        private Vector2 stunOffset;
        private void HoldStunned(Character c)
        {
            if (!RetainAnchors(c) || c.Stun <= 0) { stunAnchor = null; return; }
            var anim = c.AnimController;
            var torso = anim.GetLimb(LimbType.Torso) ?? anim.GetLimb(LimbType.Waist);
            if (torso == null || torso.Removed || torso.IsSevered || torso.body?.Enabled != true) return;
            if (stunAnchor == null || !Ready(stunAnchor))
            {
                stunAnchor = null;
                foreach (var g in grips) if (Ready(g)) { stunAnchor = g; break; }
                if (stunAnchor == null) return;
                stunOffset = torso.WorldPosition - stunAnchor.World;
            }
            // Suspend only the backpack-bearing torso. Gravity and joints govern the other limbs.
            anim.ResetPullJoints();
            bodies.Clear();
            if (anim.Collider?.Enabled == true) bodies.Add(anim.Collider);
            foreach (var l in anim.Limbs)
                if (!l.Removed && !l.IsSevered && l.body?.Enabled == true) bodies.Add(l.body);
            float mass = 0;
            foreach (var body in bodies) mass += body.Mass;
            bodies.Clear();
            Vector2 error = ConvertUnits.ToSimUnits(stunAnchor.World + stunOffset - torso.WorldPosition);
            Vector2 acceleration = error * 35 - torso.body.LinearVelocity * 7;
            if (acceleration.LengthSquared() > 900) acceleration = Vector2.Normalize(acceleration) * 30;
            torso.body.ApplyForce((acceleration - GameMain.World.Gravity) * mass);
        }
        private Vector2 Input(Character c)
        {
            if (c.IsAIControlled) return aiMovement / Math.Max(0.1f, MoveSpeed);
            Vector2 v = new((c.IsKeyDown(InputType.Right) ? 1 : 0) - (c.IsKeyDown(InputType.Left) ? 1 : 0),
                (c.IsKeyDown(InputType.Up) ? 1 : 0) - (c.IsKeyDown(InputType.Down) ? 1 : 0));
            if (v.LengthSquared() > 1) v.Normalize();
            return v;
        }
        private void Changed()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        public override void Update(float dt, Camera cam)
        {
            var c = Item.ParentInventory?.Owner as Character;
            if (IsOn && c != null && Equipped(c) == this)
            {
                if (c.SelectedItem?.GetComponent<Ladder>() != null) c.SelectedItem = null;
                if (c.SelectedSecondaryItem?.GetComponent<Ladder>() != null) c.SelectedSecondaryItem = null;
            }
            if (owner != c || ownerSub != c?.Submarine)
            {
                bool newWearer = owner != c;
                owner = c; ownerSub = c?.Submarine;
                if (newWearer && c?.IsAIControlled == true && GameMain.NetworkMember?.IsClient != true)
                { IsOn = true; AttackMode = true; Changed(); }
#if CLIENT
                tipsReady = false;
#endif
            }
            UpdateCombat(c, dt);
            if (GameMain.NetworkMember?.IsClient == true) return;
            bool changed = false;
            foreach (var g in grips)
                if (g.Wall != null && (!RetainAnchors(c) || g.Wall.Removed
                    || g.Contact == null || AnchorEntity(g.Contact) != g.Wall || !g.Contact.Body.FixtureList.Contains(g.Contact)))
                { g.Wall = null; changed = true; }
            if (!Valid(c)) { HoldStunned(c); if (changed) Changed(); return; }
            stunAnchor = null;
            UpdateAIRecovery(c);
            int settled = 0;
            foreach (var g in grips) if (Ready(g)) settled++;
            foreach (var g in grips)
                if (g.Wall != null && Vector2.DistanceSquared(c.WorldPosition, g.World) > Reach * Reach
                    && (!Ready(g) || settled > 1))
                {
                    if (Ready(g)) settled--;
                    g.Wall = null; changed = true;
                }
            scanTimer -= dt;
            if (scanTimer <= 0)
            {
                scanTimer = 0.15f;
                Vector2 movement = Input(c);
                // Only one hand-off per scan: the other claws retain their anchors.
                int index = nextGrip++ % grips.Length;
                for (int i = 0; i < grips.Length; i++)
                    if (grips[i].Wall == null && Timing.TotalTime >= grips[i].DisabledUntil) { index = i; break; }
                var old = grips[index];
                if (old.Wall != null)
                {
                    var obstruction = Sweep(c, c.WorldPosition, old.World, out var obstructionPoint, wallsOnly: true);
                    if (obstruction != null && obstruction.Body.UserData != old.Wall
                        && Vector2.DistanceSquared(c.WorldPosition, obstructionPoint) + 100 < Vector2.DistanceSquared(c.WorldPosition, old.World))
                    { old.Wall = null; changed = true; }
                }
                if (old.Wall == null || (settled > 1 && movement.LengthSquared() > 0
                    && Vector2.Dot(old.World - c.WorldPosition, movement) < -Reach * 0.25f))
                    changed |= FindGrip(c, index, movement);
            }
            if (changed) Changed();
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        private static bool Ready(Grip g) => Timing.TotalTime >= g.DisabledUntil && g.Wall != null && !g.Wall.Removed && Timing.TotalTime - g.Attached >= 0.2;
        internal bool Supported(HumanoidAnimController anim)
        {
            if (!Valid(anim.Character) || anim.Frozen || anim.AnimationTestPose
                || anim.Collider == null || !anim.Collider.Enabled) return false;
            foreach (var g in grips) if (Ready(g)) return true;
            return false;
        }
        private bool FindGrip(Character c, int index, Vector2 movement)
        {
            if (Timing.TotalTime < grips[index].DisabledUntil) return false;
            MapEntity best = null;
            Fixture bestContact = null;
            Vector2 bestLocal = default;
            float bestScore = float.NegativeInfinity;
            Vector2 origin = c.WorldPosition;
            for (int ray = 0; ray < 36; ray++)
            {
                float angle = ray * MathHelper.TwoPi / 36;
                Vector2 direction = new((float)Math.Cos(angle), (float)Math.Sin(angle));
                MapEntity wall = null; Vector2 point = default; float nearest = 1;
                Fixture contact = null;
                void Scan(Submarine sub)
                {
                    Vector2 start = ConvertUnits.ToSimUnits(origin - (sub?.Position ?? Vector2.Zero));
                    GameMain.World.RayCast((fixture, hit, normal, fraction) =>
                    {
                        var s = AnchorEntity(fixture);
                        if (s == null || s.Submarine != sub || fraction >= nearest) return -1;
                        nearest = fraction; wall = s; contact = fixture; point = ConvertUnits.ToDisplayUnits(hit);
                        return fraction;
                    }, start, start + direction * ConvertUnits.ToSimUnits(Reach));
                }
                Scan(null);
                foreach (var sub in Submarine.Loaded) Scan(sub);
                if (wall == null || nearest * Reach < 25) continue;
                Vector2 world = point + (wall.Submarine?.Position ?? Vector2.Zero);
                float separation = 2;
                bool crowded = false;
                for (int i = 0; i < grips.Length; i++)
                {
                    if (i == index || grips[i].Wall == null) continue;
                    Vector2 delta = grips[i].World - origin;
                    if (Vector2.DistanceSquared(grips[i].World, world) < 45 * 45) { crowded = true; break; }
                    if (delta.LengthSquared() > 1)
                        separation = Math.Min(separation, 1 - Vector2.Dot(Vector2.Normalize(delta), direction));
                }
                if (crowded) continue;
                float score = separation * 4 + nearest
                    + Vector2.Dot(direction, movement) * 0.8f;
                if (score <= bestScore) continue;
                bestScore = score; best = wall; bestContact = contact; bestLocal = point;
            }
            if (best == null) return false;
            grips[index].Wall = best; grips[index].Local = bestLocal;
            grips[index].Contact = bestContact;
            grips[index].Attached = Timing.TotalTime;
            return true;
        }
        internal bool Move(HumanoidAnimController anim)
        {
            var c = anim.Character;
            if (!Supported(anim)) return true;
            bool simulate = GameMain.NetworkMember?.IsClient != true;
#if CLIENT
            simulate |= c == Character.Controlled;
#endif
            if (simulate)
            {
                float speed = !c.IsAIControlled && c.IsKeyDown(InputType.Run) ? SprintSpeed : MoveSpeed;
                Vector2 desired = LimitAI(c, Input(c) * speed);
                Grip last = null; int count = 0;
                foreach (var g in grips) if (Ready(g)) { last = g; count++; }
                Vector2 outward = Vector2.Zero;
                float allowedOutward = float.PositiveInfinity;
                if (count == 1)
                {
                    Vector2 delta = c.WorldPosition - last.World;
                    float distance = delta.Length();
                    if (distance > 1)
                    {
                        outward = delta / distance;
                        allowedOutward = ConvertUnits.ToSimUnits(Reach - 8 - distance) * 8;
                        float radial = Vector2.Dot(desired, outward);
                        if (radial > allowedOutward) desired -= outward * (radial - allowedOutward);
                    }
                }
                bodies.Clear(); bodies.Add(anim.Collider);
                foreach (var limb in anim.Limbs)
                    if (!limb.Removed && !limb.IsSevered && limb.body != null) bodies.Add(limb.body);
                foreach (var body in bodies)
                {
                    if (!body.Enabled) continue;
                    if (count == 1 && allowedOutward < speed)
                    {
                        float radial = Vector2.Dot(body.LinearVelocity, outward);
                        if (radial > Math.Max(0, allowedOutward))
                            body.LinearVelocity -= outward * (radial - Math.Max(0, allowedOutward));
                    }
                    // Each body needs its own velocity error: the collider reaching
                    // cruising speed does not imply that the torso has caught up.
                    Vector2 acceleration = (desired - body.LinearVelocity) * 8;
                    if (acceleration.LengthSquared() > 400) acceleration = Vector2.Normalize(acceleration) * 20;
                    body.ApplyForce((acceleration - GameMain.World.Gravity) * body.Mass);
                }
                bodies.Clear();
            }
            anim.levitatingCollider = false;
            return false;
        }
        internal void SuspendedPose(HumanoidAnimController anim)
        {
            var collider = anim.Collider;
            float scale = anim.RagdollParams.JointScale / 0.5f;
            float lean = -Input(anim.Character).X * 0.12f;
            collider.SmoothRotate(lean, Math.Max(1, collider.Mass) * 15);
            // Relative-velocity pose springs follow the moving collider without
            // braking translation. Reaction forces keep pose correction internal.
            void Pose(LimbType type, Vector2 offset, float angle, bool onlyFree = false)
            {
                var limb = anim.GetLimb(type);
                if (limb == null || limb.Removed || limb.IsSevered || limb.body == null
                    || !limb.body.Enabled || (onlyFree && limb.Disabled)) return;
                Vector2 target = collider.SimPosition + offset * scale;
                float stiffness = onlyFree ? 14 : 40;
                float damping = onlyFree ? 3 : 7;
                Vector2 acceleration = (target - limb.SimPosition) * stiffness
                    + (collider.LinearVelocity - limb.body.LinearVelocity) * damping;
                if (acceleration.LengthSquared() > 3600) acceleration = Vector2.Normalize(acceleration) * 60;
                Vector2 force = acceleration * limb.Mass;
                limb.body.ApplyForce(force);
                collider.ApplyForce(-force);
                if (!onlyFree) limb.body.SmoothRotate(angle, Math.Max(1, limb.Mass) * 7);
            }
            float dir = anim.Dir;
            Pose(LimbType.Waist, Vector2.Zero, lean);
            Pose(LimbType.Torso, new Vector2(0, 0.4f), lean);
            Pose(LimbType.Head, new Vector2(0, 0.77f), lean, true);
            Pose(LimbType.LeftFoot, new Vector2(-0.12f * dir, -0.8f), 0, true);
            Pose(LimbType.RightFoot, new Vector2(0.16f * dir, -0.76f), 0, true);
            // Held/aimed hands are marked Disabled by native item animation.
            // Empty hands receive a rest target again as soon as the item is put away.
            if (anim.Character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) == null)
                Pose(LimbType.LeftHand, new Vector2(-0.22f * dir, 0.05f), 0, true);
            if (anim.Character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) == null)
                Pose(LimbType.RightHand, new Vector2(0.22f * dir, 0.05f), 0, true);
        }
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteBoolean(IsOn);
            foreach (var g in grips)
            { msg.WriteUInt16(g.Wall?.ID ?? (ushort)0); msg.WriteSingle(g.Local.X); msg.WriteSingle(g.Local.Y); }
            WriteCombat(msg);
        }
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            IsOn = msg.ReadBoolean();
            foreach (var g in grips)
            {
                var wall = Entity.FindEntityByID(msg.ReadUInt16()) as MapEntity;
                Vector2 local = new(msg.ReadSingle(), msg.ReadSingle());
                if (wall != g.Wall || local != g.Local) g.Attached = Timing.TotalTime;
                g.Wall = wall; g.Local = local;
            }
            ReadCombat(msg);
        }
        internal void Draw(Limb torso, SpriteBatch batch)
        {
            var c = torso.character;
            Vector2 origin = torso.DrawPosition + new Vector2(-14 * c.AnimController.Dir, 5);
            float dt = (float)Math.Clamp(Timing.TotalTime - drawTime, 0, 0.05); drawTime = Timing.TotalTime;
            float dir = c.AnimController.Dir;
            Vector2 Screen(Vector2 p) => new(p.X, -p.Y);
            for (int i = 0; i < 9; i++)
            {
                var g = i < 6 ? grips[i] : null;
                bool attached = g != null && RetainAnchors(c) && g.Wall != null && !g.Wall.Removed;
                Vector2 root = origin + new Vector2(0, (i - 2.5f) * 4);
                float foldSide = i < 3 ? 1 : -1;
                float spread = i % 3;
                Vector2 rest = new(-dir * (18 + spread * 5), foldSide * (18 + spread * 4));
                if (i >= 7) rest = new Vector2(-dir * (24 + (i - 7) * 30), 85 - (i - 7) * 20);
                Vector2 goal = attached ? g.Local + (g.Wall.Submarine?.DrawPosition ?? Vector2.Zero) - root : rest;
                if (i >= 6 && CombatTip(i - 6, out Vector2 combatTip))
                { goal = combatTip - root; attached = true; }
                // Store visual offsets relative to the wearer so submarine movement is not interpolated twice.
                if (!tipsReady) tips[i] = rest;
                Vector2 delta = goal - tips[i];
                if (i >= 6 && attached) tips[i] = goal;
                else tips[i] += delta.Length() <= dt * 1600 ? delta : Vector2.Normalize(delta) * dt * 1600;
                Vector2 tip = root + tips[i];
                Vector2 a = root + new Vector2(-dir * (34 + spread * 5), foldSide * (33 + spread * 5));
                Vector2 b = root + new Vector2(-dir * (40 + spread * 5), foldSide * (23 + spread * 4));
                if (i >= 7)
                { a = root + new Vector2(-dir * (40 + spread * 8), 25); b = root + new Vector2(-dir * (45 + spread * 8), 60); }
                if (attached)
                {
                    Vector2 side = new(-tips[i].Y, tips[i].X);
                    if (side.LengthSquared() > 1) side.Normalize();
                    a = Vector2.Lerp(root, tip, 0.33f) + side * (i % 2 == 0 ? 20 : -20);
                    b = Vector2.Lerp(root, tip, 0.67f) - side * (i % 2 == 0 ? 12 : -12);
                }
                for (int j = 0; j <= 48; j++)
                {
                    float t = j / 48f * 3;
                    curve[j] = t < 1 ? Vector2.CatmullRom(root, root, a, b, t)
                        : t < 2 ? Vector2.CatmullRom(root, a, b, tip, t - 1)
                        : Vector2.CatmullRom(a, b, tip, tip, t - 2);
                }
                float carry = 0;
                for (int j = 1; j <= 48; j++)
                {
                    Vector2 p = curve[j - 1], step = curve[j] - p;
                    float length = step.Length(); if (length < 0.001f) continue;
                    float angle = (float)Math.Atan2(-step.Y, step.X);
                    GUI.DrawLine(batch, Screen(p), Screen(curve[j]), new Color(42, 46, 50), width: 5);
                    for (float at = carry; at < length; at += 9)
                        batch.Draw(link.Texture, Screen(p + step * (at / length)), link.SourceRect, Color.White,
                            angle, new Vector2(link.SourceRect.Width, link.SourceRect.Height) * 0.5f, 0.25f, SpriteEffects.None, 0);
                    carry = (carry - length) % 9; if (carry < 0) carry += 9;
                }
                Vector2 end = curve[48] - curve[47];
                Sprite head = i < 7 ? claw : BladeSprite(i - 6);
                batch.Draw(head.Texture, Screen(tip), head.SourceRect, Color.White,
                    (float)Math.Atan2(-end.Y, end.X) - (i < 7 ? MathHelper.PiOver2 : 0),
                    new Vector2(head.SourceRect.Width, head.SourceRect.Height) * 0.5f,
                    i < 7 ? 0.42f : 0.6f, SpriteEffects.None, 0);
            }
            tipsReady = true;
        }
        public override void RemoveComponentSpecific()
        { link?.Remove(); claw?.Remove(); blade?.Remove(); blueBlade?.Remove(); base.RemoveComponentSpecific(); }
#endif
    }
    public sealed class MagneticChainPackPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.magnetic.chain.prototype");
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                prefix: new HarmonyMethod(typeof(MagneticChainPackPlugin), nameof(Begin)),
                postfix: new HarmonyMethod(typeof(MagneticChainPackPlugin), nameof(End)),
                finalizer: new HarmonyMethod(typeof(MagneticChainPackPlugin), nameof(Restore)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateSwimming"),
                prefix: new HarmonyMethod(typeof(MagneticChainPackPlugin), nameof(SwimmingPose)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(Limb), "Draw", new[] { typeof(SpriteBatch), typeof(Camera), typeof(Color?), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(MagneticChainPackPlugin), nameof(Draw)));
#endif
        }
        private sealed class PoseState
        {
            internal bool Swimming, ForceStanding, Restored;
            internal float Lock;
            internal Vector2 Movement;
            internal MagneticChainPack Pack;
        }
        private static bool SwimmingPose(HumanoidAnimController __instance)
        {
            var pack = MagneticChainPack.Equipped(__instance.Character);
            if (pack?.Supported(__instance) != true) return true;
            pack.SuspendedPose(__instance);
            return false;
        }
        private static void Begin(HumanoidAnimController __instance, out PoseState __state)
        {
            __state = null;
            var pack = MagneticChainPack.Equipped(__instance.Character);
            if (pack?.Supported(__instance) != true) return;
            pack.CaptureAIInput(__instance.Character);
            __state = new PoseState { Pack = pack, Swimming = __instance.swimming,
                ForceStanding = __instance.forceStanding, Lock = __instance.swimmingStateLockTimer,
                Movement = __instance.TargetMovement };
            // Run the complete native animation, including hand recovery and flipping.
            // Select its floating pose only; never change the character's water state.
            __instance.swimming = true;
            __instance.forceStanding = false;
            __instance.swimmingStateLockTimer = 10;
            __instance.TargetMovement = Vector2.Zero;
            var c = __instance.Character;
            bool facing = GameMain.NetworkMember?.IsClient != true;
#if CLIENT
            facing |= c == Character.Controlled;
#endif
            if (facing)
            {
                float dx = c.CursorPosition.X - __instance.Collider.Position.X;
                if (Math.Abs(dx) > 10) __instance.TargetDir = dx > 0 ? Direction.Right : Direction.Left;
            }
        }
        private static void Restore(HumanoidAnimController __instance, PoseState __state)
        {
            if (__state == null || __state.Restored) return;
            __instance.swimming = __state.Swimming;
            __instance.forceStanding = __state.ForceStanding;
            __instance.swimmingStateLockTimer = __state.Lock;
            __instance.TargetMovement = __state.Movement;
            __state.Restored = true;
        }
        private static void End(HumanoidAnimController __instance, PoseState __state)
        {
            Restore(__instance, __state);
            __state?.Pack.Move(__instance);
        }
#if CLIENT
        private static void Draw(Limb __instance, SpriteBatch __0)
        {
            if (__instance.type == LimbType.Torso && !__instance.IsSevered)
                MagneticChainPack.Equipped(__instance.character)?.Draw(__instance, __0);
        }
#endif
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
