using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Particles;
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    class FoehnJetJump : ItemComponent, IClientSerializable, IServerSerializable
    {
        private enum Phase : byte { Idle, Boost, Coast, Brake, Landing }
        private Phase phase;
        private float elapsed, cooldown, landingTime, exhaustTime;
        private Character pilot;
        private bool leftGround;
        private readonly HashSet<PhysicsBody> bodies = new();
        private readonly List<(LimbType limb, Vector2 offset, int amount)> nozzles = new();
        private static readonly LimbType[] legTypes = { LimbType.LeftThigh, LimbType.RightThigh,
            LimbType.LeftLeg, LimbType.RightLeg, LimbType.LeftFoot, LimbType.RightFoot };
        private struct LegLease
        {
            internal Limb Limb;
            internal bool Enabled;
            internal float Force;
            internal Vector2 Anchor, Target;
        }
        private readonly LegLease[] legLeases = new LegLease[9];
        private int leasedLegs;
        private float legExtension;
        internal static readonly HashSet<FoehnJetJump> Posing = new();
        [Serialize(3f, IsPropertySaveable.No)] public float Cooldown { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float JumpSpeed { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float ForwardSpeed { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float BoostAcceleration { get; set; }
        [Serialize(0.6f, IsPropertySaveable.No)] public float BoostDuration { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float BoostHoldDuration { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float FallSpeedLimit { get; set; }
        [Serialize(0.3f, IsPropertySaveable.No)] public float LandingDuration { get; set; }
        [Serialize(180f, IsPropertySaveable.No)] public float LegPoseForce { get; set; }
        [Serialize(30f, IsPropertySaveable.No)] public float LegPoseTorque { get; set; }
        private bool Hovering => Item.GetComponent<HoverJetpack>() != null
            && Item.ParentInventory?.Owner is Character owner && HoverJetpack.Flying(owner);
        internal bool Flying => Hovering || phase == Phase.Boost || phase == Phase.Coast || phase == Phase.Brake;
        internal bool Landing => phase == Phase.Landing;
        internal bool ControlsAirPose(HumanoidAnimController anim) => !anim.Frozen
            && FoehnCharge.Equipped(anim.Character)?.Dashing != true
            && (Hovering || Flying && pilot == anim.Character && Usable(anim.Character) && !anim.OnGround);
        internal static FoehnJetJump Equipped(Character c) => HoverJetpack.Flying(c)
            ? HoverJetpack.Equipped(c).Item.GetComponent<FoehnJetJump>()
            : c?.GetEquippedItem("aic_wardens_gdf_foehn_powerarmor", InvSlotType.OuterClothes)?.GetComponent<FoehnJetJump>();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        public FoehnJetJump(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            foreach (var child in element.GetChildElements("nozzle"))
                if (Enum.TryParse<LimbType>(child.GetAttributeString("limb", "Torso"), true, out var limb))
                    nozzles.Add((limb, child.GetAttributeVector2("offset", Vector2.Zero),
                        Math.Clamp(child.GetAttributeInt("amount", 1), 1, 4)));
        }
        private bool Usable(Character c) => MovementState.ActiveOnLand(c)
            && !HoverBike.Riding(c) && Item.Condition > 0 && Equipped(c) == this;
        private void SetPhase(Phase next)
        {
            if (phase == next) return;
            phase = next;
            if (!Flying) ReleaseLegPose();
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void Start(Character c)
        {
            if (!Authority || cooldown > 0 || phase != Phase.Idle || !Usable(c)
                || c.IsKeyDown(InputType.Crouch) || ActiveCrawl.IsCrawling(c)
                || !c.AnimController.OnGround || FoehnSlide.Equipped(c)?.Sliding == true
                || MovementState.SelectingLadder(c)) return;
            pilot = c; elapsed = 0; leftGround = false; legExtension = 0; cooldown = Math.Max(0.5f, Cooldown);
            float direction = c.AnimController.Dir < 0 ? -1 : 1;
            var velocity = c.AnimController.Collider.LinearVelocity;
            ChangeVelocity(c, new Vector2(direction * ForwardSpeed - velocity.X, JumpSpeed - velocity.Y));
            SetPhase(Phase.Boost);
        }
        private void ChangeVelocity(Character c, Vector2 change)
        {
            if (!MathUtils.IsValid(change)) return;
            bodies.Clear();
            void Push(PhysicsBody body)
            {
                if (body == null || !body.Enabled || !bodies.Add(body)) return;
                body.LinearVelocity += change;
            }
            Push(c.AnimController.Collider);
            foreach (var limb in c.AnimController.Limbs)
                if (!limb.IsSevered && !limb.Removed) Push(limb.body);
            bodies.Clear();
        }
        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);
        // Leases last only until the next native animation update. Never retain stale ground IK targets.
        internal void ReleaseLegPose()
        {
            for (int i = 0; i < leasedLegs; i++)
            {
                var saved = legLeases[i]; var limb = saved.Limb;
                legLeases[i] = default;
                if (limb == null || limb.Removed || limb.IsSevered) continue;
                limb.PullJointWorldAnchorA = limb.body.FarseerBody.GetWorldPoint(saved.Anchor);
                limb.PullJointWorldAnchorB = saved.Target;
                limb.PullJointMaxForce = saved.Force;
                limb.PullJointEnabled = saved.Enabled;
            }
            leasedLegs = 0; Posing.Remove(this);
        }
        internal void ApplyLegPose(HumanoidAnimController anim, float dt)
        {
            ReleaseLegPose();
            var c = anim.Character;
            if (!ControlsAirPose(anim)) return;
            var waist = anim.GetLimb(LimbType.Waist) ?? anim.GetLimb(LimbType.Torso);
            if (waist == null || waist.Removed || waist.IsSevered) return;
            // Jetpack ascent retains a slight tuck; approaching the apex extends
            // both legs fully, including when thrust is used to brake a descent.
            float targetExtension = Hovering
                ? MathHelper.Lerp(0.5f, 1f, MathHelper.Clamp(1f - anim.Collider.LinearVelocity.Y / 1.5f, 0, 1))
                : phase == Phase.Brake ? 1 :
                MathHelper.Clamp((1.5f - anim.Collider.LinearVelocity.Y) / 3f, 0, 1);
            legExtension = MathHelper.Lerp(legExtension, targetExtension, 1-(float)Math.Exp(-8*Math.Max(0, dt)));
            float blend = Hovering ? 1 : MathHelper.Clamp(elapsed / 0.15f, 0, 1);
            // Offsets below are already sized for the vanilla human (JointScale = 0.5).
            float scale = MathHelper.Clamp(anim.RagdollParams.JointScale / 0.5f, 0.5f, 2f);
            float dir = anim.Dir < 0 ? -1 : 1;
            Vector2 torsoTarget = new Vector2(anim.Collider.SimPosition.X,
                anim.GetColliderBottom().Y + (anim.TorsoPosition ?? 1.1f * scale));
            Vector2 hipsTarget = torsoTarget - new Vector2(0, 0.35f * scale);
            HoldAirLimb(anim.GetLimb(LimbType.Torso), torsoTarget, 0, blend);
            if (waist.type == LimbType.Waist) HoldAirLimb(waist, hipsTarget, 0, blend);
            HoldAirLimb(anim.GetLimb(LimbType.Head), torsoTarget + new Vector2(0, 0.4f * scale), 0, blend);
            for (int i = 0; i < legTypes.Length; i++)
            {
                var limb = anim.GetLimb(legTypes[i]);
                if (limb == null || limb.Removed || limb.IsSevered || limb.body == null || !limb.body.Enabled) continue;
                // Targets are in simulation metres relative to the hips, mirrored with facing.
                Vector2 tucked, extended; float tuckedAngle;
                if (i < 2) { tucked = new Vector2(0.15f,-0.19f); extended = new Vector2(0.035f,-0.23f); tuckedAngle = 0.6f; }
                else if (i < 4) { tucked = new Vector2(0.10f,-0.43f); extended = new Vector2(0.025f,-0.57f); tuckedAngle = -0.65f; }
                else { tucked = new Vector2(-0.025f,-0.59f); extended = new Vector2(0.045f,-0.79f); tuckedAngle = 0; }
                Vector2 offset = Vector2.Lerp(tucked, extended, legExtension)*scale;
                offset.X *= dir;
                HoldAirLimb(limb, hipsTarget+offset, MathHelper.Lerp(tuckedAngle, 0, legExtension)*dir, blend);
            }
            if (leasedLegs > 0) Posing.Add(this);
        }
        private void HoldAirLimb(Limb limb, Vector2 target, float angle, float blend)
        {
            if (limb == null || limb.Removed || limb.IsSevered || limb.body == null || !limb.body.Enabled) return;
            legLeases[leasedLegs++] = new LegLease { Limb = limb, Enabled = limb.PullJointEnabled,
                Force = limb.PullJointMaxForce, Anchor = limb.PullJointLocalAnchorA, Target = limb.PullJointWorldAnchorB };
            limb.PullJointWorldAnchorA = limb.SimPosition;
            limb.PullJointWorldAnchorB = Vector2.Lerp(limb.SimPosition, target, blend);
            limb.PullJointMaxForce = Math.Max(1, limb.Mass)*Math.Max(0, LegPoseForce);
            limb.PullJointEnabled = true;
            limb.body.SmoothRotate(angle, Math.Max(1, limb.Mass)*Math.Max(0, LegPoseTorque)*blend);
        }
        protected override void RemoveComponentSpecific()
        { ReleaseLegPose(); base.RemoveComponentSpecific(); }
        public override void Update(float deltaTime, Camera cam)
        {
            if (Item.GetComponent<HoverJetpack>() != null)
            {
                if (!Hovering) ReleaseLegPose();
                return;
            }
            cooldown = Math.Max(0, cooldown - deltaTime);
            var user = Item.ParentInventory?.Owner as Character;
            if (!Flying || pilot != user || !Usable(user)) ReleaseLegPose();
#if CLIENT
            bool up = PlayerInput.KeyDown(Keys.W);
            bool pressed = up && !previousUp;
            previousUp = up;
            if (pressed && user == Character.Controlled && GameMain.WindowActive && Screen.Selected is GameScreen
                && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen && Usable(user))
            {
                if (Authority) Start(user);
                else if (cooldown <= 0 && phase == Phase.Idle) Item.CreateClientEvent(this);
            }
#endif
            // Passive fall protection is independent of the launch key and its cooldown.
            if (Authority && phase == Phase.Idle && Usable(user) && !user.AnimController.OnGround
                && user.AnimController.Collider.LinearVelocity.Y < -Math.Max(0.5f, FallSpeedLimit) * 0.8f)
            {
                pilot = user; elapsed = 0; leftGround = true; legExtension = 1;
                SetPhase(Phase.Brake);
            }
            if (phase == Phase.Idle) return;
            if (pilot != user || !Usable(user))
            {
                if (Authority) SetPhase(Phase.Idle);
                return;
            }
            elapsed += deltaTime;
            if (Authority)
            {
                if (phase == Phase.Landing)
                {
                    landingTime -= deltaTime;
                    if (landingTime <= 0) SetPhase(Phase.Idle);
                    return;
                }
                var anim = user.AnimController;
                if (!anim.OnGround) leftGround = true;
                if (elapsed > 0.15f && leftGround && anim.OnGround && anim.Collider.LinearVelocity.Y <= 0.5f)
                {
                    landingTime = Math.Max(0, LandingDuration); SetPhase(Phase.Landing); return;
                }
                if (elapsed > 12f || (elapsed > 1f && !leftGround)) { SetPhase(Phase.Idle); return; }
                float verticalSpeed = anim.Collider.LinearVelocity.Y;
                if (phase == Phase.Boost)
                {
                    if (elapsed >= BoostHoldDuration + BoostDuration || verticalSpeed <= 0) SetPhase(Phase.Coast);
                    else ChangeVelocity(user, Vector2.UnitY * Math.Max(0, BoostAcceleration)
                        * MathHelper.Clamp(1 - Math.Max(0, elapsed - BoostHoldDuration) / Math.Max(0.01f, BoostDuration), 0, 1) * deltaTime);
                }
                float limit = Math.Max(0.5f, FallSpeedLimit);
                if (verticalSpeed < -limit * 0.8f)
                {
                    SetPhase(Phase.Brake);
                    // Start braking ahead of the cap; include the upcoming gravity step.
                    float gravity = GameMain.World.Gravity.Y;
                    float predicted = verticalSpeed + gravity * deltaTime;
                    float correction = Math.Max(0, -limit - predicted);
                    float blend = MathHelper.Clamp((-verticalSpeed / limit - 0.8f) / 0.2f, 0, 1);
                    ChangeVelocity(user, Vector2.UnitY * Math.Max(correction, Math.Max(0, -gravity) * blend * deltaTime));
                }
            }
#if CLIENT
            if (Flying && (phase == Phase.Boost || phase == Phase.Brake)) DrawExhaust(user, deltaTime);
#endif
        }
#if CLIENT
        private bool previousUp;
        internal void DrawDashExhaust(Character c, int direction)
        {
            var prefab = ParticleManager.FindPrefab("aic_foehn_dash_steam");
            if (prefab == null) return;
            foreach (var nozzle in nozzles)
            {
                var limb = c.AnimController.GetLimb(nozzle.limb);
                if (limb == null || limb.Removed || limb.IsSevered) continue;
                Vector2 offset = nozzle.offset * (c.AnimController.RagdollParams.LimbScale / 0.5f);
                offset.X *= c.AnimController.Dir < 0 ? -1 : 1;
                offset = Vector2.Transform(offset, Matrix.CreateRotationZ(limb.Rotation));
                for (int i = 0; i < 4; i++)
                {
                    var velocity = new Vector2(-direction * Rand.Range(220f, 420f), Rand.Range(-80f, 80f));
                    GameMain.ParticleManager?.CreateParticle(prefab, limb.WorldPosition + offset,
                        velocity, hullGuess: c.CurrentHull, drawOrder: ParticleDrawOrder.Foreground);
                }
            }
        }
        internal void DrawRunExhaust(Character c, float dt, int runDirection) => DrawExhaust(c, dt, runDirection);
        private void DrawExhaust(Character c, float dt, int runDirection = 0)
        {
            exhaustTime -= dt;
            if (exhaustTime > 0) return;
            exhaustTime = 0.06f;
            var prefab = ParticleManager.FindPrefab("aic_foehn_jet_steam");
            foreach (var nozzle in nozzles)
            {
                if (runDirection != 0 && nozzle.limb != LimbType.LeftLeg && nozzle.limb != LimbType.RightLeg) continue;
                var limb = c.AnimController.GetLimb(nozzle.limb);
                if (limb == null || limb.IsSevered || limb.Removed) continue;
                // Offsets were authored for the vanilla 0.5 ragdoll scale; match the armour
                // sprites, which inherit ragdoll scale but intentionally ignore per-limb scale.
                Vector2 offset = nozzle.offset * (c.AnimController.RagdollParams.LimbScale / 0.5f);
                offset.X *= c.AnimController.Dir < 0 ? -1 : 1;
                offset = Vector2.Transform(offset, Matrix.CreateRotationZ(limb.Rotation));
                Vector2 direction = runDirection != 0 ? Vector2.Normalize(new Vector2(-runDirection, -0.25f))
                    : phase == Phase.Brake ? -Vector2.UnitY
                    : Vector2.Normalize(new Vector2(c.AnimController.Dir < 0 ? 0.35f : -0.35f, -1));
                for (int i = 0; i < nozzle.amount; i++)
                    GameMain.ParticleManager?.CreateParticle(prefab, limb.WorldPosition + offset,
                        direction * (180 - i * 12), hullGuess: c.CurrentHull, drawOrder: ParticleDrawOrder.Foreground);
            }
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => msg.WriteBoolean(true);
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            ReleaseLegPose();
            var previousPilot = pilot;
            bool wasFlying = Flying;
            phase = (Phase)msg.ReadByte(); cooldown = msg.ReadSingle(); elapsed = msg.ReadSingle();
            pilot = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            if (!wasFlying || previousPilot != pilot) legExtension = phase == Phase.Brake ? 1 : 0;
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            if (msg.ReadBoolean()) Start(client?.Character);
        }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteByte((byte)phase); msg.WriteSingle(cooldown); msg.WriteSingle(elapsed);
            msg.WriteUInt16(pilot?.ID ?? (ushort)0);
        }
#endif
    }

    public sealed class FoehnJetPose : IAssemblyPlugin
    {
        private Harmony harmony;
        private struct SavedPose { internal bool changed, standing, crouching; }
        public void Initialize()
        {
            harmony = new Harmony("aic.foehn.jet.pose");
            harmony.Patch(AccessTools.Method(typeof(AnimController), "GetCurrentSpeed"),
                postfix: new HarmonyMethod(typeof(FoehnJetPose), nameof(RunSpeed)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                prefix: new HarmonyMethod(typeof(FoehnJetPose), nameof(Before)),
                postfix: new HarmonyMethod(typeof(FoehnJetPose), nameof(Legs)),
                finalizer: new HarmonyMethod(typeof(FoehnJetPose), nameof(After)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateFallingProne"),
                prefix: new HarmonyMethod(typeof(FoehnJetPose), nameof(AllowFallingPose)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            foreach (var jet in new List<FoehnJetJump>(FoehnJetJump.Posing)) jet.ReleaseLegPose();
            harmony?.UnpatchSelf();
        }
        private static void Legs(HumanoidAnimController __instance, float __0)
            => FoehnJetJump.Equipped(__instance.Character)?.ApplyLegPose(__instance, __0);
        private static bool AllowFallingPose(HumanoidAnimController __instance)
            => FoehnJetJump.Equipped(__instance.Character)?.ControlsAirPose(__instance) != true;
        private static void RunSpeed(AnimController __instance, bool useMaxSpeed, ref float __result)
        {
            if (!useMaxSpeed || __instance.InWater || !__instance.OnGround
                || ActiveCrawl.IsCrawling(__instance.Character)) return;
            var charge = FoehnCharge.Equipped(__instance.Character);
            if (charge != null) __result = Math.Max(0, __result * charge.SpeedMultiplier
                - charge.SpeedPenalty / Math.Max(0.01f, __instance.Character.SpeedMultiplier));
        }
        private static void Before(HumanoidAnimController __instance, out SavedPose __state)
        {
            __state = default;
            var c = __instance.Character;
            var jet = FoehnJetJump.Equipped(c);
            jet?.ReleaseLegPose();
            if (jet == null || (!jet.Flying && !jet.Landing) || c.IsDead || c.IsIncapacitated
                || c.IsRagdolled || c.IsForceRagdolled || c.Stun > 0 || c.InWater
                || c.IsClimbing || c.IsAttachedToController()) return;
            __state = new SavedPose { changed = true, standing = __instance.forceStanding, crouching = __instance.Crouching };
            __instance.forceStanding = true;
            __instance.Crouching = jet.Landing && __instance.OnGround;
        }
        private static void After(HumanoidAnimController __instance, SavedPose __state)
        {
            if (!__state.changed) return;
            __instance.forceStanding = __state.standing;
            __instance.Crouching = __state.crouching;
            if (FoehnJetJump.Equipped(__instance.Character)?.Flying == true) __instance.levitatingCollider = false;
        }
    }
}
