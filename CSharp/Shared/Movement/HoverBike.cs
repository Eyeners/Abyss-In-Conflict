using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Particles;
#endif

namespace AIC.Components
{
    internal sealed class HoverBike : ItemComponent, IServerSerializable
    {
        [Serialize(10f, IsPropertySaveable.No)] public float MaxSpeed { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float Acceleration { get; set; }
        [Serialize(9f, IsPropertySaveable.No)] public float Braking { get; set; }
        [Serialize(0.75f, IsPropertySaveable.No)] public float HoverHeight { get; set; }
        [Serialize(35f, IsPropertySaveable.No)] public float Spring { get; set; }
        [Serialize(9f, IsPropertySaveable.No)] public float Damping { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float ColliderWidth { get; set; }
        [Serialize(0.3f, IsPropertySaveable.No)] public float ColliderRadius { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float JumpSpeed { get; set; }
        [Serialize(7f, IsPropertySaveable.No)] public float BoostAcceleration { get; set; }
        [Serialize(0.7f, IsPropertySaveable.No)] public float BoostHold { get; set; }
        [Serialize(0.6f, IsPropertySaveable.No)] public float BoostFade { get; set; }
        [Serialize(3f, IsPropertySaveable.No)] public float JumpCooldown { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float FallSpeedLimit { get; set; }
        [Serialize(22f, IsPropertySaveable.No)] public float MaxSlopeAngle { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float SlopeSmoothing { get; set; }
        [Serialize(60f, IsPropertySaveable.No)] public float SlopeTurnRate { get; set; }
        [Serialize(0.18f, IsPropertySaveable.No)] public float SlopeStepTolerance { get; set; }
        [Serialize(1.2f, IsPropertySaveable.No)] public float SeatBreakDistance { get; set; }
        [Serialize(0.2f, IsPropertySaveable.No)] public float SeatBreakDelay { get; set; }
        [Serialize("-0.4,0.25", IsPropertySaveable.No)] public Vector2 Seat { get; set; }
        [Serialize("0.22,0.48", IsPropertySaveable.No)] public Vector2 Handlebar { get; set; }
        [Serialize("-0.9,-0.35", IsPropertySaveable.No)] public Vector2 RearExhaust { get; set; }
        [Serialize("0.9,-0.35", IsPropertySaveable.No)] public Vector2 FrontExhaust { get; set; }
        private static readonly FieldInfo Impact = AccessTools.Field(typeof(Ragdoll), "strongestImpact");
        private static readonly HashSet<HoverBike> active = new();
        private Character rider;
        private HumanoidAnimController anim;
        private PhysicsBody collider;
        private int colliderIndex, previousIndex;
        private bool restoring, upHeld, airborne, limp;
        private float flightTime, cooldown, landing, exhaustTimer, syncTimer;
        private float tilt, tiltSin, tiltCos = 1;
        private float seatBreakTimer, seatGraceTimer;
        private readonly HashSet<PhysicsBody> bodies = new();
        private readonly List<(Limb Limb, Vector2 Anchor, float Force)> leases = new();
#if CLIENT
        private readonly Sprite sprite;
        private LightComponent headlight;
        private bool lightAttached;
        private bool pendingDraw;
        private float rideDepth;
#endif
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static HoverBike Equipped(Character c) =>
            c?.Inventory?.GetItemInLimbSlot(InvSlotType.Bag)?.GetComponent<HoverBike>();
        private bool Valid(Character c) => MovementState.Humanoid(c) && MovementState.OnLand(c)
            && Item.Condition > 0 && Equipped(c) == this;
        private bool CanDrive => Valid(rider) && MovementState.Conscious(rider) && rider.AllowInput;
        internal static bool Riding(Character c) => Equipped(c) is HoverBike bike && bike.Valid(c);
        public HoverBike(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
#if CLIENT
            var xml = element.GetChildElement("sprite");
            if (xml != null) sprite = new Sprite(xml);
#endif
        }
        private bool Mount(Character c)
        {
            if (!Valid(c)) return false;
            var controller = (HumanoidAnimController)c.AnimController;
            if (rider == c && CustomColliderStorage.Registered(controller, collider, colliderIndex)) return true;
            Release();
            rider = c; anim = controller;
            var normal = anim.Collider;
            bool recoveringRagdoll = !normal.Enabled;
            previousIndex = anim.ColliderIndex;
            collider = CustomColliderStorage.Add(anim, Math.Max(0.5f, ColliderWidth), 0,
                Math.Max(0.15f, ColliderRadius), out colliderIndex);
            anim.ColliderIndex = colliderIndex;
            collider.SetTransformIgnoreContacts(collider.SimPosition, 0);
            if (recoveringRagdoll)
            {
                var waist = anim.GetLimb(LimbType.Waist) ?? anim.MainLimb;
                collider.SetTransformIgnoreContacts(waist.SimPosition - RotateOffset(Seat), 0);
                collider.LinearVelocity = waist.body.LinearVelocity;
            }
            active.Add(this);
            seatGraceTimer = 0.6f;
            return true;
        }
        internal void Select(Ragdoll a, ref int index)
        {
            if (!restoring && anim == a && collider != null && !collider.Removed && Valid(rider)
                && CustomColliderStorage.Registered(a, collider, colliderIndex))
                index = colliderIndex;
        }
        internal void BeforeRecreate(Ragdoll a)
        {
            if (anim != a) return;
            // Recreate discards appended colliders. Release limb references and stop
            // redirecting ColliderIndex before native code builds the new list.
            Release();
            // Headroom may reject the normal collider in Release. Since the whole
            // ragdoll is about to be rebuilt, do not retain that custom index.
            CustomColliderStorage.ResetForRebuild(a);
        }
        private void RestoreLeases()
        {
            foreach (var lease in leases)
            {
                var l = lease.Limb;
                if (l.Removed || l.IsSevered) continue;
                if (rider?.Inventory != null
                    && (l.type == LimbType.LeftHand && rider.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) != null
                        || l.type == LimbType.RightHand && rider.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) != null)) continue;
                l.PullJointEnabled = false;
                l.PullJointWorldAnchorA = l.body.FarseerBody.GetWorldPoint(lease.Anchor);
                l.PullJointMaxForce = lease.Force;
                l.Disabled = false;
            }
            leases.Clear();
        }
        private void Release()
        {
            RestoreLeases();
            if (anim != null && !anim.Character.Removed && collider != null && !collider.Removed)
            {
                restoring = true;
                try
                {
                    CustomColliderStorage.RestoreSelected(anim, collider, colliderIndex, previousIndex > 1 ? 0 : previousIndex);
                    // If headroom blocks restoration, native animation will retry next frame.
                    CustomColliderStorage.RemoveUnusedTail(anim, collider);
                }
                finally { restoring = false; }
            }
            active.Remove(this); rider = null; anim = null; collider = null;
            airborne = false; flightTime = 0; upHeld = false; limp = false;
            tilt = tiltSin = 0; tiltCos = 1;
            seatBreakTimer = seatGraceTimer = 0;
        }
        public override void Update(float dt, Camera cam)
        {
            if (rider != null && !Valid(rider)) Release();
            if (Authority && rider != null && collider != null && !collider.Removed && collider.Enabled)
            {
                seatGraceTimer = Math.Max(0, seatGraceTimer - dt);
                var waist = anim.GetLimb(LimbType.Waist) ?? anim.MainLimb;
                if (waist != null && !waist.Removed && !waist.IsSevered && seatGraceTimer <= 0)
                {
                    Vector2 seat = Seat;
                    if (landing > 0) seat.Y -= 0.08f;
                    float limit = Math.Max(0.25f, SeatBreakDistance);
                    bool displaced = Vector2.DistanceSquared(waist.SimPosition,
                        collider.SimPosition + RotateOffset(seat)) > limit * limit;
                    seatBreakTimer = displaced ? seatBreakTimer + Math.Max(0, dt) : 0;
                    if (displaced && seatBreakTimer >= Math.Max(0, SeatBreakDelay))
                        Eject(waist);
                }
                else seatBreakTimer = 0;
            }
#if CLIENT
            UpdateHeadlight();
#endif
        }
        internal static void DismountForGrab(Character character)
        {
            if (!Authority || Equipped(character) is not HoverBike bike) return;
            var waist = bike.anim?.GetLimb(LimbType.Waist) ?? bike.anim?.MainLimb;
            if (bike.rider == character && bike.collider != null && !bike.collider.Removed
                && waist != null && !waist.Removed && waist.body != null)
                bike.Eject(waist);
            else
            {
                // The item may have just been equipped, before its riding collider was created.
                bike.Item.Drop(character);
                bike.Release();
            }
        }
        private void Eject(Limb waist)
        {
            var owner = rider;
            var controller = anim;
            var oldCollider = collider;
            Vector2 bikePosition = collider.SimPosition;
            Vector2 bikeVelocity = collider.LinearVelocity;
            Vector2 riderPosition = waist.SimPosition;
            Vector2 riderVelocity = waist.body.LinearVelocity;
            float bikeAngle = tilt;
            // Drop removes the Bag equipment, so the next animation tick cannot
            // mount again. Native inventory networking replicates the dismount.
            Item.Drop(owner);
            Release();
            if (controller.Collider != oldCollider && !controller.Collider.Removed)
            {
                controller.Collider.SetTransformIgnoreContacts(riderPosition, 0);
                controller.Collider.LinearVelocity = riderVelocity;
            }
            if (Item.body != null)
            {
                Item.SetTransform(bikePosition, bikeAngle, findNewHull: true);
                Item.body.LinearVelocity = bikeVelocity;
            }
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        protected override void RemoveComponentSpecific()
        {
            Release();
#if CLIENT
            sprite?.Remove();
#endif
            base.RemoveComponentSpecific();
        }
        private void Gather()
        {
            bodies.Clear();
            if (anim.Collider?.Enabled == true) bodies.Add(anim.Collider);
            foreach (var l in anim.Limbs)
                if (!l.Removed && !l.IsSevered && l.body?.Enabled == true) bodies.Add(l.body);
        }
        private void ChangeVelocity(Vector2 delta)
        {
            foreach (var body in bodies) body.LinearVelocity += delta;
        }
        private Vector2 RotateOffset(Vector2 offset)
        {
            float x = offset.X * anim.Dir;
            return new Vector2(x * tiltCos - offset.Y * tiltSin, x * tiltSin + offset.Y * tiltCos);
        }
        private void UpdateSlope(bool supported, float back, float front, Vector2 backNormal, Vector2 frontNormal, float dt)
        {
            float target = 0;
            float dx = (FrontExhaust.X - RearExhaust.X) * anim.Dir;
            if (supported && Math.Abs(dx) > 0.1f && backNormal.Y > 0.4f && frontNormal.Y > 0.4f)
            {
                // A pair of horizontal stair tops must not masquerade as one ramp.
                float expectedRise = dx * (-backNormal.X / backNormal.Y - frontNormal.X / frontNormal.Y) * 0.5f;
                float rise = front - back;
                if (Vector2.Dot(backNormal, frontNormal) > 0.9f
                    && Math.Abs(rise - expectedRise) <= Math.Max(0, SlopeStepTolerance))
                {
                    float limit = MathHelper.ToRadians(Math.Clamp(MaxSlopeAngle, 0, 45));
                    target = Math.Clamp((float)Math.Atan(rise / dx), -limit, limit);
                }
            }
            float change = (target - tilt) * (1 - (float)Math.Exp(-Math.Max(0, SlopeSmoothing) * dt));
            float maxChange = MathHelper.ToRadians(Math.Max(0, SlopeTurnRate)) * dt;
            tilt += Math.Clamp(change, -maxChange, maxChange);
            tiltSin = (float)Math.Sin(tilt); tiltCos = (float)Math.Cos(tilt);
        }
        private bool Floor(Vector2 offset, out float y, out Vector2 surfaceNormal)
        {
            y = 0;
            surfaceNormal = Vector2.UnitY;
            Vector2 start = collider.SimPosition + new Vector2(offset.X * anim.Dir, 0.15f);
            float closest = 1, floor = 0; bool found = false;
            Vector2 foundNormal = Vector2.UnitY;
            GameMain.World.RayCast((fixture, point, normal, fraction) =>
            {
                if (fixture.IsSensor || normal.Y < 0.4f || fraction >= closest) return -1;
                bool ground = fixture.Body.UserData is Structure s && s.Submarine == rider.Submarine
                    || fixture.Body.UserData is Item i && i.Submarine == rider.Submarine && i.GetComponent<Door>() != null
                    || rider.Submarine == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
                if (!ground) return -1;
                closest = fraction; floor = point.Y; foundNormal = normal; found = true; return fraction;
            }, start, start - Vector2.UnitY * Math.Max(1.2f, HoverHeight + 0.7f));
            y = floor; surfaceNormal = foundNormal; return found;
        }
        internal bool Animate(HumanoidAnimController a, float dt)
        {
            if (a.Frozen || !Mount(a.Character)) return true;
            // Preserve the native collision stun formula without handing vehicle
            // movement back to UpdateRagdollControlsMovement (which disables its body).
            if (Impact?.GetValue(a) is float impact && impact > 0)
            {
                if (Authority) rider.SetStun(Math.Min(impact * 0.5f, 5));
                Impact.SetValue(a, 0f);
            }
            RestoreLeases();
            bool canDrive = CanDrive;
            if (!canDrive && !limp) anim.ResetPullJoints();
            limp = !canDrive;
            bool localFacing = Authority;
#if CLIENT
            localFacing |= rider == Character.Controlled;
#endif
            // Native Character.Control only updates cursor-facing while OnGround.
            // Riding deliberately stays above the floor, including when hovering normally.
            if (localFacing && canDrive)
            {
                float cursorDelta = rider.CursorPosition.X - a.Collider.Position.X;
                if (cursorDelta < -40) a.TargetDir = Direction.Left;
                else if (cursorDelta > 40) a.TargetDir = Direction.Right;
            }
            if (canDrive && a.TargetDir != a.Direction && Timing.TotalTime > a.FlipLockTime) a.Flip();
            anim.ColliderIndex = colliderIndex;
            if (!collider.Enabled)
            {
                // Native ragdoll control disables the main collider. Skipping its
                // recovery animation must not leave the vehicle anchored in midair.
                var waist = anim.GetLimb(LimbType.Waist) ?? anim.MainLimb;
                Vector2 root = waist.SimPosition - RotateOffset(Seat);
                collider.SetTransformIgnoreContacts(root, 0);
                collider.LinearVelocity = waist.body.LinearVelocity;
                collider.PhysEnabled = true;
                collider.Enabled = true;
                collider.FarseerBody.Awake = true;
            }
            collider.FarseerBody.FixedRotation = true;
            if (Math.Abs(collider.Rotation) > 0.001f) collider.SetTransformIgnoreContacts(collider.SimPosition, 0);
            anim.levitatingCollider = false;
            dt = Math.Clamp(dt, 0, 0.05f);
            cooldown = Math.Max(0, cooldown - dt); landing = Math.Max(0, landing - dt);
            bool leftGround = Floor(RearExhaust, out float back, out var backNormal),
                rightGround = Floor(FrontExhaust, out float front, out var frontNormal);
            bool ground = leftGround || rightGround;
            float floor = leftGround && rightGround ? Math.Max(back, front) : leftGround ? back : front;
            float height = collider.SimPosition.Y - floor;
            bool nearGround = ground && height <= HoverHeight + 0.25f;
            if (!airborne && !nearGround)
            {
                // Recovering from ragdoll in the air is a fall, not a fresh boost.
                airborne = true;
                flightTime = Math.Max(0, BoostHold) + Math.Max(0.01f, BoostFade) + 0.01f;
            }
            bool simulate = Authority;
#if CLIENT
            simulate |= rider == Character.Controlled;
#endif
            if (simulate)
            {
                Gather();
                int input = canDrive ? (rider.IsKeyDown(InputType.Right) ? 1 : 0) - (rider.IsKeyDown(InputType.Left) ? 1 : 0) : 0;
                Vector2 velocity = collider.LinearVelocity;
                float penalty = rider.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)?.GetComponent<VelocityLance>()?.HorizontalPenalty(rider) ?? 0;
                float desired = input * Math.Max(0, MaxSpeed - penalty);
                float accel = input == 0 || velocity.X * input < 0 ? Braking : Acceleration;
                float dx = canDrive ? Math.Clamp(desired - velocity.X, -Math.Max(0, accel) * dt, Math.Max(0, accel) * dt) : 0;
                ChangeVelocity(new Vector2(dx, 0));
                bool up = rider.IsKeyDown(InputType.Up);
                if (canDrive && up && !upHeld && !airborne && nearGround && cooldown <= 0
                    && rider.SelectedItem?.GetComponent<Ladder>() == null && rider.SelectedSecondaryItem?.GetComponent<Ladder>() == null)
                {
                    airborne = true; flightTime = 0; cooldown = Math.Max(0, JumpCooldown);
                    ChangeVelocity(new Vector2(0, Math.Max(0, JumpSpeed) - collider.LinearVelocity.Y));
                }
                upHeld = up;
                if (airborne) flightTime += dt;
                if (airborne && flightTime > Math.Max(0, BoostHold) + Math.Max(0.01f, BoostFade)
                    && nearGround && collider.LinearVelocity.Y <= 0)
                { airborne = false; landing = 0.25f; }
                float lift = 0;
                if (!airborne && nearGround)
                    lift = Math.Clamp(Math.Max(0, -GameMain.World.Gravity.Y) + (HoverHeight - height) * Math.Max(0, Spring)
                        - collider.LinearVelocity.Y * Math.Max(0, Damping), 0, 45);
                else if (airborne)
                    lift = Math.Max(0, BoostAcceleration) * Math.Clamp(1 - (flightTime - BoostHold) / Math.Max(0.01f, BoostFade), 0, 1);
                foreach (var body in bodies) body.ApplyForce(Vector2.UnitY * lift * body.Mass);
                float limit = Math.Max(0.5f, FallSpeedLimit);
                if (collider.LinearVelocity.Y < -limit)
                    ChangeVelocity(new Vector2(0, -limit - collider.LinearVelocity.Y));
                bodies.Clear();
            }
            UpdateSlope(leftGround && rightGround && nearGround && !airborne, back, front, backNormal, frontNormal, dt);
            ApplyPose();
            foreach (var limb in anim.Limbs) limb.Disabled = false;
#if CLIENT
            Exhaust(dt);
            UpdateHeadlight();
#endif
#if SERVER
            syncTimer -= dt;
            if (syncTimer <= 0) { syncTimer = 0.15f; Item.CreateServerEvent(this); }
#endif
            return false;
        }
        private void Pose(LimbType type, Vector2 offset, float angle, bool relativeToSeat = true)
        {
            var l = anim.GetLimb(type);
            if (l == null || l.Removed || l.IsSevered) return;
            float scale = Math.Clamp(anim.RagdollParams.JointScale / 0.5f, 0.6f, 1.8f);
            Vector2 local = relativeToSeat ? Seat + offset * scale : offset;
            local.Y -= landing > 0 ? 0.08f : 0;
            leases.Add((l, l.PullJointLocalAnchorA, l.PullJointMaxForce));
            // A FixedMouseJoint damps absolute world velocity, so moving its target
            // each frame drags the rider backward and can hold the rig up in midair.
            // Use relative velocity and equal/opposite forces: pose control cannot
            // supply any net lift or brake the whole motorcycle.
            l.PullJointEnabled = false;
            Vector2 target = collider.SimPosition + RotateOffset(local);
            Vector2 acceleration = (target - l.SimPosition) * 90
                + (collider.LinearVelocity - l.body.LinearVelocity) * 18;
            float magnitude = acceleration.Length();
            if (float.IsFinite(magnitude) && magnitude > 0)
            {
                if (magnitude > 300) acceleration *= 300 / magnitude;
                Vector2 force = acceleration * Math.Max(0.01f, l.Mass);
                l.body.ApplyForce(force);
                collider.ApplyForce(-force);
            }
            l.body.SmoothRotate(angle * anim.Dir + tilt, Math.Max(1, l.Mass) * 50);
        }
        private void ApplyPose()
        {
            if (limp) anim.ResetPullJoints();
            Pose(LimbType.Waist, Vector2.Zero, 0.1f);
            Pose(LimbType.LeftThigh, new Vector2(0.22f, -0.06f), 1.1f);
            Pose(LimbType.RightThigh, new Vector2(0.22f, -0.06f), 1.1f);
            Pose(LimbType.LeftLeg, new Vector2(0.35f, -0.33f), -0.3f);
            Pose(LimbType.RightLeg, new Vector2(0.35f, -0.33f), -0.3f);
            Pose(LimbType.LeftFoot, new Vector2(0.4f, -0.51f), 0);
            Pose(LimbType.RightFoot, new Vector2(0.4f, -0.51f), 0);
            if (limp)
            {
                // Keep the seated lower body, but release upper-body position targets.
                // A weak forward lean prevents the balanced upright pose from persisting.
                var torso = anim.GetLimb(LimbType.Torso);
                if (torso != null && !torso.Removed && !torso.IsSevered)
                    torso.body.SmoothRotate(1.05f * anim.Dir + tilt, Math.Max(1, torso.Mass) * 8);
                return;
            }
            Pose(LimbType.Torso, new Vector2(0.1f, 0.38f), 0.2f);
            Pose(LimbType.Head, new Vector2(0.2f, 0.73f), 0);
            if (rider.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) == null) Pose(LimbType.LeftHand, Handlebar, 1, false);
            if (rider.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) == null) Pose(LimbType.RightHand, Handlebar, 1, false);
        }
#if CLIENT
        internal void UpdateHeadlight()
        {
            headlight ??= Item.GetComponent<LightComponent>();
            if (headlight?.Light == null) return;
            var owner = Item.ParentInventory?.Owner as Character;
            if (owner == null || owner.Removed || Equipped(owner) != this)
            {
                if (lightAttached)
                {
                    lightAttached = false;
                    headlight.SetLightSourceTransform();
                    headlight.CheckIfNeedsUpdate();
                }
                return;
            }
            lightAttached = true;
            var controller = owner.AnimController;
            Vector2 root;
            if (anim == controller && collider != null && !collider.Removed && collider.Enabled)
                root = collider.Position;
            else
            {
                var waist = controller.GetLimb(LimbType.Waist) ?? controller.MainLimb;
                if (waist == null) { headlight.Light.Enabled = false; return; }
                Vector2 seat = new Vector2(Seat.X * controller.Dir, Seat.Y);
                root = waist.Position - ConvertUnits.ToDisplayUnits(new Vector2(seat.X * tiltCos - seat.Y * tiltSin,
                    seat.X * tiltSin + seat.Y * tiltCos));
            }
            Vector2 offset = headlight.LightOffset * Item.Scale;
            offset.X *= controller.Dir;
            var light = headlight.Light;
            // The inventory item body is disabled while equipped, so bind to the
            // rendered motorcycle's root rather than the parked item's body.
            light.ParentBody = null;
            light.ParentSub = owner.Submarine;
            light.Position = root + new Vector2(offset.X * tiltCos - offset.Y * tiltSin,
                offset.X * tiltSin + offset.Y * tiltCos);
            light.Rotation = tilt - headlight.Rotation * controller.Dir + (controller.Dir < 0 ? MathHelper.Pi : 0);
            // Rotation already turns the cone left. Mirror only its vertical axis
            // to preserve the original off-centre texture origin, never FlipHorizontally twice.
            light.LightSpriteEffect = controller.Dir < 0 ? SpriteEffects.FlipVertically : SpriteEffects.None;
            light.Enabled = headlight.IsOn && Item.Condition > 0;
            light.Color = light.Enabled ? headlight.LightColor : Color.Transparent;
        }
        private void Exhaust(float dt)
        {
            exhaustTimer -= dt;
            if (exhaustTimer > 0) return;
            exhaustTimer = 0.06f;
            var prefab = ParticleManager.FindPrefab("aic_foehn_jet_steam");
            if (prefab == null) return;
            for (int index = 0; index < 2; index++)
            {
                Vector2 offset = index == 0 ? RearExhaust : FrontExhaust;
                Vector2 world = ConvertUnits.ToDisplayUnits(collider.SimPosition + RotateOffset(offset)) + (rider.Submarine?.Position ?? Vector2.Zero);
                GameMain.ParticleManager?.CreateParticle(prefab, world, new Vector2(160 * tiltSin, -160 * tiltCos),
                    hullGuess: rider.CurrentHull, drawOrder: ParticleDrawOrder.Foreground);
            }
        }
        internal void BeginDraw(Ragdoll ragdoll, bool onlySevered)
        {
            pendingDraw = !onlySevered && !ragdoll.SimplePhysicsEnabled && sprite != null
                && Equipped(ragdoll.Character) == this;
            if (!pendingDraw) return;
            float front = 0, rear = 1;
            foreach (var type in new[] { LimbType.Torso, LimbType.Waist, LimbType.RightThigh, LimbType.RightLeg, LimbType.RightFoot })
                if (ragdoll.GetLimb(type)?.ActiveSprite is Sprite s) front = Math.Max(front, s.Depth);
            foreach (var type in new[] { LimbType.LeftThigh, LimbType.LeftLeg, LimbType.LeftFoot, LimbType.LeftHand })
                if (ragdoll.GetLimb(type)?.ActiveSprite is Sprite s) rear = Math.Min(rear, s.Depth);
            rideDepth = (rear > front ? (front + rear) * 0.5f : front + 0.00001f) + ragdoll.GetDepthOffset();
        }
        internal void BeforeLimb(Limb limb, SpriteBatch batch)
        {
            if (pendingDraw && limb.ActiveSprite != null && limb.ActiveSprite.Depth <= rideDepth)
                DrawInRagdoll(limb.character.AnimController, batch);
        }
        internal void DrawInRagdoll(Ragdoll ragdoll, SpriteBatch batch)
        {
            if (!pendingDraw) return;
            pendingDraw = false;
            Vector2 world;
            if (anim == ragdoll && collider != null && !collider.Removed && collider.Enabled && Valid(rider))
            {
                collider.UpdateDrawPosition();
                world = collider.DrawPosition;
            }
            else
            {
                // Rendering depends on equipment, not on the active riding state.
                // During stun/ragdoll follow the pelvis, never a disabled collider.
                var waist = ragdoll.GetLimb(LimbType.Waist) ?? ragdoll.MainLimb;
                if (waist == null || waist.Removed) return;
                waist.body.UpdateDrawPosition();
                float seatX = Seat.X * ragdoll.Dir;
                world = waist.body.DrawPosition - ConvertUnits.ToDisplayUnits(
                    new Vector2(seatX * tiltCos - Seat.Y * tiltSin, seatX * tiltSin + Seat.Y * tiltCos));
            }
            sprite.Draw(batch, new Vector2(world.X, -world.Y), Color.White, -tilt, Item.Scale,
                ragdoll.Dir < 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None, rideDepth);
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            airborne = msg.ReadBoolean(); flightTime = msg.ReadSingle(); cooldown = msg.ReadSingle(); landing = msg.ReadSingle();
        }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteBoolean(airborne); msg.WriteSingle(flightTime); msg.WriteSingle(cooldown); msg.WriteSingle(landing);
        }
#endif
        internal static void Clear()
        {
            foreach (var bike in new List<HoverBike>(active)) bike.Release();
        }
    }
    public sealed class HoverBikeSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        private static void CanClimb(Character __instance, ref bool __result)
        {
            if (__result && HoverBike.Equipped(__instance) != null) __result = false;
        }
        private static bool Animate(HumanoidAnimController __instance, float __0)
            => HoverBike.Equipped(__instance.Character)?.Animate(__instance, __0) ?? true;
        private static void Collider(Ragdoll __instance, ref int value)
            => HoverBike.Equipped(__instance.Character)?.Select(__instance, ref value);
        private static bool Slot(Inventory __instance, Item item, int i, ref bool __result)
        {
            if (item?.GetComponent<HoverBike>() == null) return true;
            if (__instance is CharacterInventory ci && i >= 0 && i < ci.SlotTypes.Length && ci.SlotTypes[i] == InvSlotType.Bag) return true;
            __result = false; return false;
        }
#if CLIENT
        private static void LightTransform(LightComponent __instance)
        {
            if (__instance.Item.ParentInventory?.Owner is Character c
                && HoverBike.Equipped(c) is HoverBike bike && bike.Item == __instance.Item)
                bike.UpdateHeadlight();
        }
        private static void BeginDraw(Ragdoll __instance, bool onlyDrawSeveredLimbs)
            => HoverBike.Equipped(__instance.Character)?.BeginDraw(__instance, onlyDrawSeveredLimbs);
        private static void LimbDraw(Limb __instance, SpriteBatch spriteBatch)
            => HoverBike.Equipped(__instance.character)?.BeforeLimb(__instance, spriteBatch);
        private static void EndDraw(Ragdoll __instance, SpriteBatch spriteBatch)
            => HoverBike.Equipped(__instance.Character)?.DrawInRagdoll(__instance, spriteBatch);
#endif
        public void Initialize()
        {
            harmony = new Harmony("aic.hoverbike");
            try
            {
                harmony.Patch(AccessTools.PropertyGetter(typeof(Character), "CanClimb"),
                    postfix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(CanClimb)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(Animate)) { priority = Priority.First });
                harmony.Patch(AccessTools.PropertySetter(typeof(Ragdoll), "ColliderIndex"),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(Collider)));
                harmony.Patch(AccessTools.Method(typeof(Inventory), "CanBePutInSlot", new[] { typeof(Item), typeof(int), typeof(bool), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(Slot)));
                harmony.Patch(AccessTools.Method(typeof(ItemInventory), "CanBePutInSlot", new[] { typeof(Item), typeof(int), typeof(bool), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(Slot)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    prefix: new HarmonyMethod(typeof(HoverBike), nameof(HoverBike.Clear)));
#if CLIENT
                harmony.Patch(AccessTools.Method(typeof(LightComponent), "SetLightSourceTransformProjSpecific"),
                    postfix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(LightTransform)));
                harmony.Patch(AccessTools.Method(typeof(LightComponent), "Update"),
                    postfix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(LightTransform)));
                harmony.Patch(AccessTools.Method(typeof(Ragdoll), "Draw"),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(BeginDraw)),
                    postfix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(EndDraw)));
                harmony.Patch(AccessTools.Method(typeof(Limb), "Draw"),
                    prefix: new HarmonyMethod(typeof(HoverBikeSystem), nameof(LimbDraw)));
#endif
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        public void Dispose() { HoverBike.Clear(); harmony?.UnpatchSelf(); }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
