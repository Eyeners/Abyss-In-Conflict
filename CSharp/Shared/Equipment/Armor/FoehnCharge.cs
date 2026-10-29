using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics.Collision;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using HarmonyLib;
using Barotrauma.LuaCs;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    partial class FoehnCharge : ItemComponent, IServerSerializable, IClientSerializable
    {
        [Serialize(1.6f, IsPropertySaveable.No)] public float RunMultiplier { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool DisableImpact { get; set; }
        [Serialize(0.8f, IsPropertySaveable.No)] public float AccelerationTime { get; set; }
        [Serialize(4f, IsPropertySaveable.No)] public float MinimumHitSpeed { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float TargetCooldown { get; set; }
        [Serialize(450f, IsPropertySaveable.No)] public float HorizontalImpulse { get; set; }
        [Serialize(160f, IsPropertySaveable.No)] public float UpwardImpulse { get; set; }
        [Serialize(9f, IsPropertySaveable.No)] public float MaxVelocityChange { get; set; }
        [Serialize(0.85f, IsPropertySaveable.No)] public float HitRadiusX { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float HitRadiusY { get; set; }
        [Serialize(0.15f, IsPropertySaveable.No)] public float ForwardOffset { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float HitSpeedLoss { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float SpeedRecovery { get; set; }
        [Serialize(7f, IsPropertySaveable.No)] public float ImpactReferenceSpeed { get; set; }
        [Serialize(0.2f, IsPropertySaveable.No)] public float ImpactGainPerSpeed { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float MaxImpactMultiplier { get; set; }
        [Serialize(0.1f, IsPropertySaveable.No)] public float ControlGainPerSpeed { get; set; }
        [Serialize(1.5f, IsPropertySaveable.No)] public float MaxControlMultiplier { get; set; }
        internal float SpeedPenalty { get; private set; }
#if CLIENT
        private readonly Sprite shield;
        private float afterimageTimer;
        internal static readonly HashSet<FoehnCharge> Visible = new();
#endif
        private readonly Attack chargeAttack;
        private sealed record ImpactVisual(Vector2 Position) : IEventData;
        private readonly List<(Affliction Affliction, System.Xml.Linq.XElement Xml, bool Stun)> baseAfflictions = new();
        private float ramp, queryTimer, syncTimer;
        private bool syncedDisableImpact;
        private int direction;
        private readonly Dictionary<Character, Limb> candidates = new();
        private readonly HashSet<PhysicsBody> bodies = new();
        private readonly Dictionary<Character, double> hitUntil = new();
        private readonly List<Character> expired = new();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal float SpeedMultiplier => 1 + ramp * (Math.Max(1, RunMultiplier) - 1);
        internal static FoehnCharge Equipped(Character c) => c?.GetEquippedItem(
            "aic_wardens_gdf_foehn_powerarmor".ToIdentifier(), InvSlotType.OuterClothes)?.GetComponent<FoehnCharge>();
        internal float EvasionChance(Character wearer)
        {
            if (Dashing && DashValid(wearer)) return 0.8f;
            if (DisableImpact || direction == 0 || !Valid(wearer)) return 0;
            float speed = wearer.AnimController.Collider.LinearVelocity.X * direction;
            if (speed < MinimumHitSpeed || speed < 5f) return 0;
            return MathHelper.Lerp(0.1f, 0.2f, MathHelper.Clamp((speed - 5f) / 5f, 0, 1));
        }
        public FoehnCharge(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            var attackElement = element.GetChildElement("Attack");
            if (attackElement != null) chargeAttack = new Attack(attackElement, item.Prefab.Identifier.ToString(), item);
            if (chargeAttack != null)
                foreach (var affliction in chargeAttack.Afflictions.Keys)
                    baseAfflictions.Add((affliction, chargeAttack.Afflictions[affliction],
                        affliction.Prefab.Identifier == "stun".ToIdentifier()));
#if CLIENT
            var spriteElement = element.GetChildElement("sprite");
            if (spriteElement != null) shield = new Sprite(spriteElement);
#endif
        }
        public override void RemoveComponentSpecific()
        {
            ReleaseDashPose();
#if CLIENT
            Visible.Remove(this);
            FoehnDashAfterimages.Remove(Item);
            shield?.Remove();
#endif
            base.RemoveComponentSpecific();
        }
        private bool Valid(Character c) => (Dashing && DashValid(c)) || NormalValid(c);
        private bool NormalValid(Character c) => MovementState.ActiveOnLand(c) && Item.Condition > 0
            && c.AnimController is HumanoidAnimController anim && anim.OnGround && !anim.Crouching
            && !HoverBike.Riding(c) && !HoverJetpack.Flying(c) && Equipped(c) == this && Item.GetComponent<FoehnJetJump>()?.Flying != true
            && Item.GetComponent<FoehnJetJump>()?.Landing != true
            && Item.GetComponent<FoehnSlide>()?.Sliding != true;
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        public override void Update(float dt, Camera cam)
        {
            SpeedPenalty = Math.Max(0, SpeedPenalty - Math.Max(0, SpeedRecovery) * dt);
            var c = Item.ParentInventory?.Owner as Character;
            UpdateDash(c, dt);
#if CLIENT
            afterimageTimer -= dt;
            if (Dashing && DashValid(c) && afterimageTimer <= 0)
            {
                afterimageTimer = 0.03f;
                FoehnDashAfterimages.Capture(Item, c);
            }
            if (!Dashing) afterimageTimer = 0;
#endif
            bool valid = Valid(c);
#if CLIENT
            Visible.Remove(this);
#endif
            int previous = direction;
            bool calculate = Authority;
#if CLIENT
            calculate |= c == Character.Controlled;
#endif
            if (calculate)
            {
                int wanted = Dashing ? dashDirection : 0;
                if (!Dashing && valid && c.CanRun && (c.IsKeyDown(InputType.Run) || c.ForceRun))
                {
                    if (c.IsKeyDown(InputType.Left)) wanted--;
                    if (c.IsKeyDown(InputType.Right)) wanted++;
                }
                if (wanted != direction) ramp = 0;
                direction = wanted;
                ramp = direction == 0 ? 0 : Math.Min(1, ramp + dt / Math.Max(0.05f, AccelerationTime));
            }
            else if (!valid) { direction = 0; ramp = 0; }
#if SERVER
            syncTimer -= dt;
            if (previous != direction || syncedDisableImpact != DisableImpact || ((direction != 0 || SpeedPenalty > 0) && syncTimer <= 0))
            { syncTimer = 0.25f; syncedDisableImpact = DisableImpact; Item.CreateServerEvent(this); }
#endif
            if (direction == 0 || !valid)
            {
                candidates.Clear();
                expired.Clear();
                foreach (var pair in hitUntil)
                    if (pair.Key.Removed || pair.Value <= Timing.TotalTime) expired.Add(pair.Key);
                foreach (var target in expired) hitUntil.Remove(target);
                return;
            }
            float speed = c.AnimController.Collider.LinearVelocity.X * direction;
#if CLIENT
            if (speed > 0.5f) Item.GetComponent<FoehnJetJump>()?.DrawRunExhaust(c, dt, direction);
            if (!DisableImpact && speed >= MinimumHitSpeed) Visible.Add(this);
#endif
            if (DisableImpact || !Authority || speed < MinimumHitSpeed) return;
            queryTimer -= dt;
            if (queryTimer > 0) return;
            float interval = 0.05f;
            queryTimer = interval;
            expired.Clear();
            foreach (var pair in hitUntil)
                if (pair.Key.Removed || pair.Value <= Timing.TotalTime) expired.Add(pair.Key);
            foreach (var target in expired) hitUntil.Remove(target);
            candidates.Clear();
            Vector2 center = c.AnimController.Collider.SimPosition + new Vector2(direction * ForwardOffset, 0);
            float reach = Math.Max(0.1f, HitRadiusX), height = Math.Max(0.1f, HitRadiusY);
            var bounds = new AABB(center + new Vector2(direction < 0 ? -reach : 0, -height),
                center + new Vector2(direction > 0 ? reach : 0, height));
            GameMain.World.QueryAABB(fixture =>
            {
                if (fixture.Body.UserData is Limb limb && limb.character is Character target
                    && target != c && !target.Removed && !target.IsDead && target.Submarine == c.Submarine
                    && !c.IsOnFriendlyTeam(target) && !hitUntil.ContainsKey(target)
                    && (limb.SimPosition.X - center.X) * direction >= 0
                    && Math.Pow((limb.SimPosition.X - center.X) / reach, 2)
                       + Math.Pow((limb.SimPosition.Y - center.Y) / height, 2) <= 1) candidates.TryAdd(target, limb);
                return true;
            }, ref bounds);
            foreach (var candidate in candidates)
            {
                float hitSpeed = c.AnimController.Collider.LinearVelocity.X * direction;
                if (!float.IsFinite(hitSpeed) || hitSpeed < MinimumHitSpeed) break;
                var target = candidate.Key;
                if (target.AnimController?.Collider == null) continue;
                if (Submarine.PickBody(center, target.AnimController.Collider.SimPosition,
                    collisionCategory: Physics.CollisionWall | Physics.CollisionLevel,
                    customPredicate: f => f.Body.UserData is Structure structure ? structure.Submarine == c.Submarine
                        : f.Body.UserData is Item obstacle ? obstacle.Submarine == c.Submarine && obstacle.GetComponent<Door>() != null
                        : c.Submarine == null, allowInsideFixture: true) != null) continue;
                hitUntil[target] = Timing.TotalTime + Math.Max(0.1f, TargetCooldown);
                float extraSpeed = (Dashing ? 10f : hitSpeed) - ImpactReferenceSpeed;
                float impact = SpeedScale(extraSpeed, ImpactGainPerSpeed, MaxImpactMultiplier);
                float control = SpeedScale(extraSpeed, ControlGainPerSpeed, MaxControlMultiplier);
                if (Dashing) { impact *= 1.5f; control *= 1.5f; }
                Hit(c, candidate.Value, impact, control);
#if CLIENT
                SonicRefraction.AddImpactRing(candidate.Value.WorldPosition);
#endif
#if SERVER
                Item.CreateServerEvent(this, new ImpactVisual(candidate.Value.WorldPosition));
#endif
                Push(target, impact, control);
                if (!Dashing) SlowWearer(c);
            }
        }
        private static float SpeedScale(float extraSpeed, float gain, float maximum)
            => Math.Clamp(1 + extraSpeed * Math.Max(0, gain), 0, Math.Max(1, maximum));
        private void Hit(Character wearer, Limb limb, float impact, float control)
        {
            if (chargeAttack == null) return;
            float baseStun = chargeAttack.Stun;
            try
            {
                // Keep the native Attack path, including armor and affliction resistances.
                chargeAttack.Afflictions.Clear();
                foreach (var entry in baseAfflictions)
                    chargeAttack.Afflictions.Add(entry.Affliction.CreateMultiplied(entry.Stun ? control : impact, entry.Affliction), entry.Xml);
                chargeAttack.Stun = baseStun * control;
                chargeAttack.DoDamageToLimb(wearer, limb, limb.WorldPosition, 1f, playSound: true);
            }
            finally
            {
                chargeAttack.Afflictions.Clear();
                foreach (var entry in baseAfflictions) chargeAttack.Afflictions.Add(entry.Affliction, entry.Xml);
                chargeAttack.Stun = baseStun;
            }
        }
        private void SlowWearer(Character wearer)
        {
            float loss = Math.Min(Math.Max(0, HitSpeedLoss), Math.Max(0, wearer.AnimController.Collider.LinearVelocity.X * direction));
            SpeedPenalty += loss;
            bodies.Clear();
            bodies.Add(wearer.AnimController.Collider);
            foreach (var limb in wearer.AnimController.Limbs)
                if (!limb.Removed && !limb.IsSevered && limb.body != null) bodies.Add(limb.body);
            foreach (var body in bodies)
                if (body.Enabled) body.LinearVelocity -= new Vector2(direction * loss, 0);
            bodies.Clear();
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void Push(Character target, float impact, float control)
        {
            bodies.Clear();
            float mass = 0;
            void Add(PhysicsBody body)
            {
                if (body == null || !body.Enabled || body.Mass <= 0 || !float.IsFinite(body.Mass) || !bodies.Add(body)) return;
                mass += body.Mass;
            }
            Add(target.AnimController.Collider);
            foreach (var limb in target.AnimController.Limbs)
                if (!limb.Removed && !limb.IsSevered) Add(limb.body);
            if (mass <= 0 || !float.IsFinite(mass)) return;
            Vector2 change = new Vector2(direction * Math.Max(0, HorizontalImpulse) * impact,
                Math.Max(0, UpwardImpulse) * control) / mass;
            float length = change.Length();
            float maxChange = Math.Max(0, MaxVelocityChange) * control;
            if (length > maxChange) change *= maxChange / length;
            foreach (var body in bodies) body.LinearVelocity += change;
            bodies.Clear();
        }
#if CLIENT
        internal void QueueShieldRefraction()
        {
            var c = Item.ParentInventory?.Owner as Character;
            if (DisableImpact || !Valid(c) || direction == 0) return;
            Vector2 world = c.AnimController.Collider.Position + (c.Submarine?.Position ?? Vector2.Zero)
                + new Vector2(ConvertUnits.ToDisplayUnits(direction * ForwardOffset), 0);
            SonicRefraction.AddShield(world, ConvertUnits.ToDisplayUnits(new Vector2(HitRadiusX, HitRadiusY)),
                direction, Math.Abs(c.AnimController.Collider.LinearVelocity.X));
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            ReadDash(msg);
            direction = msg.ReadInt32(); ramp = msg.ReadSingle(); SpeedPenalty = msg.ReadSingle(); DisableImpact = msg.ReadBoolean();
            if (msg.ReadBoolean()) SonicRefraction.AddImpactRing(new Vector2(msg.ReadSingle(), msg.ReadSingle()));
        }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            WriteDash(msg);
            msg.WriteInt32(direction); msg.WriteSingle(ramp); msg.WriteSingle(SpeedPenalty); msg.WriteBoolean(DisableImpact);
            var visual = extraData is Item.ComponentStateEventData data ? data.ComponentData as ImpactVisual : null;
            msg.WriteBoolean(visual != null);
            if (visual != null) { msg.WriteSingle(visual.Position.X); msg.WriteSingle(visual.Position.Y); }
        }
#endif
    }
    public sealed class FoehnChargeVisuals : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
#if CLIENT
            harmony = new Harmony("aic.foehn.charge.visuals");
            // Shield rendering is composed by SonicRefraction before scene postprocessing.
#endif
        }
#if CLIENT
        private static void Draw(SpriteBatch __2)
        {
            if (FoehnCharge.Visible.Count == 0) return;
            __2.Begin();
            try { }
            finally { __2.End(); }
        }
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
#if CLIENT
            FoehnCharge.Visible.Clear();
#endif
        }
    }
}
