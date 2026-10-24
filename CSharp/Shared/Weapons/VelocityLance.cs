using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using FarseerPhysics;

namespace AIC.Components
{
    internal sealed class VelocityLance : ItemComponent, IServerSerializable
    {
        [Serialize(268f, IsPropertySaveable.No)] public float TipOffset { get; set; }
        [Serialize(0.4f, IsPropertySaveable.No)] public float BladeRange { get; set; }
        [Serialize(0.1f, IsPropertySaveable.No)] public float SweepRadius { get; set; }
        [Serialize(0.2f, IsPropertySaveable.No)] public float ArmDelay { get; set; }
        [Serialize(0.4f, IsPropertySaveable.No)] public float Recovery { get; set; }
        [Serialize(0.3f, IsPropertySaveable.No)] public float DamageGainPerSpeed { get; set; }
        [Serialize(4f, IsPropertySaveable.No)] public float MaxDamageMultiplier { get; set; }
        [Serialize(0.15f, IsPropertySaveable.No)] public float PenetrationGainPerSpeed { get; set; }
        [Serialize(2.5f, IsPropertySaveable.No)] public float MaxPenetrationMultiplier { get; set; }
        [Serialize(0.15f, IsPropertySaveable.No)] public float StunGainPerSpeed { get; set; }
        [Serialize(2.5f, IsPropertySaveable.No)] public float MaxStunMultiplier { get; set; }
        [Serialize(45f, IsPropertySaveable.No)] public float ImpulsePerSpeed { get; set; }
        [Serialize(450f, IsPropertySaveable.No)] public float MaxImpulse { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float MaxVelocityChange { get; set; }
        [Serialize(0.25f, IsPropertySaveable.No)] public float RecoilFraction { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float MaxSpeedLoss { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float TargetCooldown { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float SpeedRecovery { get; set; }
        private readonly Attack attack;
        private readonly List<(Affliction Affliction, System.Xml.Linq.XElement Xml, bool Stun)> strengths = new();
        private readonly Dictionary<Character, double> hitUntil = new();
        private readonly List<Character> expired = new();
        private readonly HashSet<PhysicsBody> bodies = new();
        private readonly Dictionary<Character, Limb> candidates = new();
        private readonly List<Limb> ordered = new();
        private Character holder;
        private Vector2 direction, previousTip;
        private Submarine previousSub;
        private bool braced, shootHeld, hasTip;
        private float armTimer, recoveryTimer;
        private float speedPenalty;
        internal float HorizontalPenalty(Character c) => holder == c && Valid(c) ? speedPenalty : 0;
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        public VelocityLance(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            var xml = element.GetChildElement("Attack");
            if (xml == null) return;
            attack = new Attack(xml, item.Prefab.Identifier.ToString(), item);
            foreach (var aff in attack.Afflictions.Keys)
                strengths.Add((aff, attack.Afflictions[aff], aff.Prefab.Identifier == "stun".ToIdentifier()));
        }
        private bool Valid(Character c) => c != null && !c.Removed && !c.IsDead && !c.IsIncapacitated
            && !c.IsRagdolled && !c.IsForceRagdolled && c.Stun <= 0 && c.AllowInput
            && !c.IsClimbing && !c.IsAttachedToController() && c.CanAim
            && c.AnimController is HumanoidAnimController && Item.Condition > 0
            && c.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) == Item
            && c.Inventory?.GetItemInLimbSlot(InvSlotType.LeftHand) == Item;
        private void Sync()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void Stop()
        {
            if (!braced) return;
            braced = false; hasTip = false; candidates.Clear(); ordered.Clear();
            recoveryTimer = Math.Max(0, Recovery);
            if (Authority) Sync();
        }
        public override void Update(float dt, Camera cam)
        {
            armTimer = Math.Max(0, armTimer - dt);
            recoveryTimer = Math.Max(0, recoveryTimer - dt);
            speedPenalty = Math.Max(0, speedPenalty - Math.Max(0, SpeedRecovery) * dt);
            expired.Clear();
            foreach (var entry in hitUntil)
                if (entry.Key.Removed || entry.Value <= Timing.TotalTime) expired.Add(entry.Key);
            foreach (var target in expired) hitUntil.Remove(target);
            if (!Valid(holder)) { Stop(); holder = null; shootHeld = false; speedPenalty = 0; }
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        internal void Prepare(AnimController anim, ref bool aim, ref Vector2 itemPos, ref Vector2? targetPos)
        {
            var c = anim.Character;
            if (!Valid(c)) return;
            if (Authority)
            {
                if (holder != c) { Stop(); holder = c; shootHeld = false; }
                bool shoot = c.IsKeyDown(InputType.Shoot);
                if (!shoot) Stop();
                if (shoot && !shootHeld && !braced && recoveryTimer <= 0)
                {
                    Vector2 delta = ConvertUnits.ToSimUnits(c.CursorPosition) - anim.AimSourceSimPos;
                    if (MathUtils.IsValid(delta) && delta.LengthSquared() > 0.0001f)
                    {
                        direction = Vector2.Normalize(delta);
                        braced = true; hasTip = false;
                        armTimer = Math.Max(0, ArmDelay); Sync();
                    }
                }
                shootHeld = shoot;
            }
            // Always track aim when carried; only this item's hold target is overridden.
            aim = true;
            itemPos = ConvertUnits.ToSimUnits(Item.GetComponent<Holdable>().AimPos);
            if (braced && holder == c) targetPos = anim.AimSourceSimPos + direction * 10;
        }
        internal void Sweep(AnimController anim)
        {
            if (!Authority || !braced || holder != anim.Character || !Valid(holder) || Item.body == null) return;
            Vector2 actual = new Vector2((float)Math.Cos(Item.body.Rotation), (float)Math.Sin(Item.body.Rotation)) * Item.body.Dir;
            Vector2 tip = Item.SimPosition + actual * ConvertUnits.ToSimUnits(TipOffset * Item.Scale);
            if (!MathUtils.IsValid(tip)) { Stop(); return; }
            bool reset = !hasTip || previousSub != holder.Submarine || Vector2.DistanceSquared(tip, previousTip) > 25;
            if (reset) previousTip = tip;
            previousSub = holder.Submarine; hasTip = true;
            if (armTimer > 0 || Vector2.Dot(actual, direction) < 0.95f) { previousTip = tip; return; }
            // A wall between the wearer and the blade prevents reaching through it.
            if (Submarine.PickBody(anim.AimSourceSimPos, tip,
                collisionCategory: Physics.CollisionWall | Physics.CollisionLevel,
                customPredicate: f => f.Body.UserData is Structure s ? s.Submarine == holder.Submarine
                    : f.Body.UserData is Item i ? i.Submarine == holder.Submarine && i.GetComponent<Door>() != null
                    : holder.Submarine == null, allowInsideFixture: true) != null)
            { previousTip = tip; return; }
            candidates.Clear();
            Vector2 normal = new Vector2(-direction.Y, direction.X) * Math.Clamp(SweepRadius, 0, 0.3f);
            for (int i = -1; i <= 1; i++)
            {
                Vector2 offset = normal * i;
                if (!reset) Trace(previousTip + offset, tip + offset);
                Trace(tip - direction * Math.Clamp(BladeRange, 0.05f, 1) + offset, tip + offset);
            }
            ordered.Clear();
            foreach (var limb in candidates.Values) ordered.Add(limb);
            ordered.Sort((a, b) => Vector2.DistanceSquared(a.SimPosition, previousTip).CompareTo(Vector2.DistanceSquared(b.SimPosition, previousTip)));
            foreach (var limb in ordered) Hit(limb);
            previousTip = tip;
        }
        private void Trace(Vector2 start, Vector2 end)
        {
            if (Vector2.DistanceSquared(start, end) < 0.000001f) return;
            Limb closest = null;
            GameMain.World.RayCast((fixture, point, normal, fraction) =>
            {
                if (fixture.IsSensor) return -1;
                if (fixture.Body.UserData is Limb limb)
                {
                    if (limb.character == holder || limb.character.Submarine != holder.Submarine
                        || limb.Removed || limb.IsSevered || limb.character.Removed || limb.character.IsDead) return -1;
                    closest = limb;
                    return fraction;
                }
                bool wall = fixture.Body.UserData is Structure s && s.Submarine == holder.Submarine
                    || fixture.Body.UserData is Item i && i.Submarine == holder.Submarine && i.GetComponent<Door>() != null
                    || holder.Submarine == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
                if (!wall) return -1;
                closest = null;
                return fraction;
            }, start, end);
            if (closest != null && !holder.IsOnFriendlyTeam(closest.character) && !OnCooldown(closest.character))
                candidates.TryAdd(closest.character, closest);
        }
        private bool OnCooldown(Character target) => hitUntil.TryGetValue(target, out double until) && until > Timing.TotalTime;
        internal void LimitSpeed(AnimController anim, ref float speed)
        {
            if (holder != anim.Character || !Valid(holder) || anim.InWater || !anim.OnGround) return;
            speed = Math.Max(0, speed - speedPenalty / Math.Max(0.01f, holder.SpeedMultiplier));
        }
        private void Hit(Limb limb)
        {
            var target = limb.character;
            if (target.AnimController?.Collider == null || OnCooldown(target)) return;
            float speed = Vector2.Dot(holder.AnimController.Collider.LinearVelocity - target.AnimController.Collider.LinearVelocity, direction);
            if (!float.IsFinite(speed)) return;
            hitUntil[target] = Timing.TotalTime + Math.Max(0.1f, TargetCooldown);
            speed = Math.Max(0, speed);
            float damage = Math.Clamp(1 + speed * Math.Max(0, DamageGainPerSpeed), 1, Math.Max(1, MaxDamageMultiplier));
            float stun = Math.Clamp(1 + speed * Math.Max(0, StunGainPerSpeed), 1, Math.Max(1, MaxStunMultiplier));
            if (attack != null)
            {
                float basePenetration = attack.Penetration;
                try
                {
                    float penetrationScale = Math.Clamp(1 + speed * Math.Max(0, PenetrationGainPerSpeed),
                        1, Math.Max(1, MaxPenetrationMultiplier));
                    attack.Penetration = basePenetration * penetrationScale;
                    // Native damage cloning reads NonClampedStrength, not the mutable Strength.
                    attack.Afflictions.Clear();
                    foreach (var entry in strengths)
                        attack.Afflictions.Add(entry.Affliction.CreateMultiplied(entry.Stun ? stun : damage, entry.Affliction), entry.Xml);
                    attack.DoDamageToLimb(holder, limb, limb.WorldPosition, 1, playSound: true);
                }
                finally
                {
                    attack.Penetration = basePenetration;
                    attack.Afflictions.Clear();
                    foreach (var entry in strengths) attack.Afflictions.Add(entry.Affliction, entry.Xml);
                }
            }
            float impulse = Math.Min(Math.Max(0, MaxImpulse), speed * Math.Max(0, ImpulsePerSpeed));
            float targetMass = GatherBodies(target);
            if (targetMass > 0)
            {
                float change = Math.Min(Math.Max(0, MaxVelocityChange), impulse / targetMass);
                foreach (var body in bodies) body.LinearVelocity += direction * change;
                // Recoil uses the impulse actually delivered after the target velocity cap.
                impulse = change * targetMass;
            }
            float wearerMass = GatherBodies(holder);
            if (wearerMass > 0)
            {
                float loss = Math.Min(Math.Max(0, MaxSpeedLoss), impulse * Math.Max(0, RecoilFraction) / wearerMass);
                loss = Math.Min(loss, Math.Max(0, Vector2.Dot(holder.AnimController.Collider.LinearVelocity, direction)));
                foreach (var body in bodies) body.LinearVelocity -= direction * loss;
                speedPenalty += loss * Math.Abs(direction.X);
                if (loss > 0) Sync();
            }
            bodies.Clear();
        }
        private float GatherBodies(Character c)
        {
            bodies.Clear(); float mass = 0;
            void Add(PhysicsBody body)
            {
                if (body != null && body.Enabled && float.IsFinite(body.Mass) && body.Mass > 0 && bodies.Add(body)) mass += body.Mass;
            }
            Add(c.AnimController.Collider);
            foreach (var limb in c.AnimController.Limbs) if (!limb.Removed && !limb.IsSevered) Add(limb.body);
            return mass;
        }
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            braced = msg.ReadBoolean();
            holder = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            float angle = msg.ReadSingle();
            speedPenalty = Math.Max(0, msg.ReadSingle());
            if (!float.IsFinite(angle)) { braced = false; return; }
            direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteBoolean(braced); msg.WriteUInt16(holder?.ID ?? (ushort)0);
            msg.WriteSingle((float)Math.Atan2(direction.Y, direction.X));
            msg.WriteSingle(speedPenalty);
        }
#endif
    }
    public sealed class VelocityLancePose : IAssemblyPlugin
    {
        private Harmony harmony;
        private static void Before(AnimController __instance, Item item, ref bool aim, ref Vector2 itemPos, ref Vector2? targetPos)
            => item.GetComponent<VelocityLance>()?.Prepare(__instance, ref aim, ref itemPos, ref targetPos);
        private static void After(AnimController __instance, Item item) => item.GetComponent<VelocityLance>()?.Sweep(__instance);
        private static void Speed(AnimController __instance, ref float __result)
            => __instance.Character.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand)?.GetComponent<VelocityLance>()?.LimitSpeed(__instance, ref __result);
        public void Initialize()
        {
            harmony = new Harmony("aic.velocitylance.pose");
            try { harmony.Patch(AccessTools.Method(typeof(AnimController), "HoldItem"),
                prefix: new HarmonyMethod(typeof(VelocityLancePose), nameof(Before)),
                postfix: new HarmonyMethod(typeof(VelocityLancePose), nameof(After)));
                harmony.Patch(AccessTools.Method(typeof(AnimController), "GetCurrentSpeed"),
                    postfix: new HarmonyMethod(typeof(VelocityLancePose), nameof(Speed)) { priority = Priority.Last }); }
            catch { harmony.UnpatchSelf(); throw; }
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
