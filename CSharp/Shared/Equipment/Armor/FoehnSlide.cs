using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    // Server owns motion; clients receive only action transitions and use native physics sync.
    class FoehnSlide : ItemComponent, IClientSerializable, IServerSerializable
    {
        [Serialize(0.85f, IsPropertySaveable.No)] public float Duration { get; set; }
        [Serialize(1.2f, IsPropertySaveable.No)] public float Cooldown { get; set; }
        [Serialize(4f, IsPropertySaveable.No)] public float Deceleration { get; set; }
        [Serialize(3f, IsPropertySaveable.No)] public float MinimumSpeed { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float HipDrop { get; set; }
        private Character pilot;
        private float remaining, cooldown, scale;
        private int direction;
        private readonly List<PoseLimb> pose = new();
        private sealed class PoseLimb
        {
            internal Limb Limb;
            internal bool Enabled;
            internal float Force;
            internal Vector2 Anchor, RelativeTarget, Offset;
            internal float Angle;
        }
        internal bool Sliding => pilot != null && Valid(pilot);
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static FoehnSlide Equipped(Character c) => c?.GetEquippedItem(
            "aic_wardens_gdf_foehn_powerarmor".ToIdentifier(), InvSlotType.OuterClothes)?.GetComponent<FoehnSlide>();
        public FoehnSlide(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        private bool Valid(Character c) => MovementState.ActiveOnLand(c) && !MovementState.SelectingLadder(c)
            && Item.Condition > 0 && Equipped(c) == this
            && c.AnimController is HumanoidAnimController a && a.OnGround
            && a.TryGetCollider(1, out var small) && small != null
            && Item.GetComponent<FoehnJetJump>()?.Flying != true
            && Item.GetComponent<FoehnJetJump>()?.Landing != true
            && !HoverBike.Riding(c) && !HoverJetpack.Flying(c) && !LowPostureCollider.Suppressed(c) && !ActiveCrawl.IsCrawling(c)
            && !c.IsKeyDown(InputType.Crouch);
        private void Start(Character c)
        {
            if (!Authority || pilot != null || cooldown > 0 || !Valid(c) || !c.AllowInput || !c.CanRun
                || !(c.IsKeyDown(InputType.Run) || c.ForceRun)
                || !(c.IsKeyDown(InputType.Left) || c.IsKeyDown(InputType.Right))) return;
            float speed = c.AnimController.Collider.LinearVelocity.X;
            if (Math.Abs(speed) < Math.Max(0.1f, MinimumSpeed)) return;
            pilot = c; direction = Math.Sign(speed);
            remaining = Math.Max(0.1f, Duration);
            Capture();
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        private void Capture()
        {
            ReleasePose();
            if (pilot?.AnimController == null) return;
            var a = pilot.AnimController;
            var head = a.GetLimb(LimbType.Head); var foot = a.GetLimb(LimbType.RightFoot);
            scale = head != null && foot != null ? MathHelper.Clamp(Math.Abs(head.SimPosition.Y-foot.SimPosition.Y)/1.5f, 0.6f, 1.8f) : 1;
            foreach (var limb in a.Limbs)
            {
                if (limb.Removed || limb.IsSevered || limb.body == null) continue;
                Vector2 offset; float angle;
                switch (limb.type)
                {
                    case LimbType.Torso: offset = new Vector2(-0.35f, 0.45f); angle = 1.1f; break;
                    case LimbType.Head: offset = new Vector2(-0.65f, 0.6f); angle = 0.3f; break;
                    case LimbType.Waist: offset = new Vector2(0, 0.28f); angle = 0.8f; break;
                    case LimbType.LeftThigh: case LimbType.RightThigh: offset = new Vector2(0.2f, 0.24f); angle = 1.15f; break;
                    case LimbType.LeftLeg: case LimbType.RightLeg: offset = new Vector2(0.48f, 0.13f); angle = 1.2f; break;
                    case LimbType.LeftFoot: case LimbType.RightFoot: offset = new Vector2(0.7f, 0.08f); angle = 0; break;
                    default: continue; // Native held-item/hand handling remains available.
                }
                pose.Add(new PoseLimb { Limb = limb, Enabled = limb.PullJointEnabled, Force = limb.PullJointMaxForce,
                    Anchor = limb.PullJointLocalAnchorA, RelativeTarget = limb.PullJointWorldAnchorB-pilot.SimPosition,
                    Offset = offset, Angle = angle });
            }
        }
        internal void ApplyPose(HumanoidAnimController a)
        {
            if (!Sliding) return;
            float blend = MathHelper.Clamp((Math.Max(0.1f, Duration)-remaining)/0.12f, 0, 1);
            Vector2 root = new Vector2(a.Collider.SimPosition.X, a.FloorY);
            foreach (var p in pose)
            {
                var l = p.Limb;
                if (l.Removed || l.IsSevered) continue;
                var target = root + new Vector2(p.Offset.X*direction, p.Offset.Y)*scale;
                // Lower the seated body while keeping feet at their existing floor targets.
                float dropWeight = l.type == LimbType.LeftFoot || l.type == LimbType.RightFoot ? 0
                    : l.type == LimbType.LeftLeg || l.type == LimbType.RightLeg ? 0.25f
                    : l.type == LimbType.LeftThigh || l.type == LimbType.RightThigh ? 0.65f : 1;
                target.Y -= Math.Max(0, HipDrop)*scale*dropWeight;
                l.PullJointWorldAnchorA = l.SimPosition;
                l.PullJointWorldAnchorB = Vector2.Lerp(l.SimPosition, target, blend);
                l.PullJointMaxForce = Math.Max(1, l.Mass)*260;
                l.PullJointEnabled = true;
                l.body.SmoothRotate(p.Angle*direction, Math.Max(1, l.Mass)*55);
            }
        }
        private void ReleasePose()
        {
            foreach (var p in pose)
            {
                var l = p.Limb;
                if (l.Removed || l.IsSevered) continue;
                l.PullJointMaxForce = p.Force;
                l.PullJointWorldAnchorA = l.body.FarseerBody.GetWorldPoint(p.Anchor);
                l.PullJointWorldAnchorB = (l.character?.SimPosition ?? Vector2.Zero)+p.RelativeTarget;
                l.PullJointEnabled = p.Enabled;
            }
            pose.Clear();
        }
        private void Stop()
        {
            bool active = pilot != null;
            remaining = 0; ReleasePose();
            pilot = null;
            if (active) cooldown = Math.Max(0, Cooldown);
#if SERVER
            if (active && !Item.Removed) Item.CreateServerEvent(this);
#endif
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        public override void Update(float dt, Camera cam)
        {
            cooldown = Math.Max(0, cooldown-dt);
#if CLIENT
            bool down = PlayerInput.KeyDown(Keys.S);
            bool pressed = down && !previousDown; previousDown = down;
            var owner = Item.ParentInventory?.Owner as Character;
            bool inputAllowed = owner == Character.Controlled && GameMain.WindowActive && Screen.Selected is GameScreen
                && !GUI.InputBlockingMenuOpen && GUI.KeyboardDispatcher.Subscriber == null;
            if (releasePending && (!down || !inputAllowed || !Valid(owner)))
            {
                releasePending = false;
                if (!Authority) Item.CreateClientEvent(this, new SlideInput(false));
                Stop();
            }
            if (pressed && inputAllowed && Valid(owner))
            {
                if (Authority) { Start(owner); releasePending = pilot != null; }
                else if (cooldown <= 0 && pilot == null)
                {
                    releasePending = true;
                    Item.CreateClientEvent(this, new SlideInput(true));
                }
            }
#endif
            if (pilot == null) return;
            if (!Valid(pilot)) { Stop(); return; }
            // Expiry ends the timed phase, not the held pose. Only release/interruption ends it.
            remaining = Math.Max(0, remaining-dt);
            if (!Authority) return;
            var body = pilot.AnimController.Collider;
            var velocity = body.LinearVelocity;
            float speed = velocity.X*direction;
            if (speed <= 0) return;
            // Only subtract momentum. A wall/impact can reduce speed without it being replenished.
            float loss = Math.Min(speed, Math.Max(0, Deceleration)*dt);
            body.LinearVelocity = new Vector2(velocity.X-direction*loss, velocity.Y);
            foreach (var limb in pilot.AnimController.Limbs)
                if (!limb.Removed && !limb.IsSevered && limb.body != null && limb.body != body)
                    limb.body.LinearVelocity -= new Vector2(direction*loss, 0);
        }
        protected override void RemoveComponentSpecific() { Stop(); base.RemoveComponentSpecific(); }
#if CLIENT
        private bool previousDown, releasePending;
        private readonly struct SlideInput : IEventData
        {
            internal readonly bool Held;
            internal SlideInput(bool held) { Held = held; }
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
            => msg.WriteBoolean(extraData is Item.ComponentStateEventData data
                && data.ComponentData is SlideInput input && input.Held);
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            ReleasePose(); remaining = msg.ReadSingle(); cooldown = msg.ReadSingle(); direction = msg.ReadInt32();
            pilot = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            // A delayed start acknowledgement must not restore the local pose after S was released.
            if (pilot == Character.Controlled && !releasePending) { remaining = 0; pilot = null; }
            if (pilot != null) Capture();
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            if (msg.ReadBoolean()) Start(client?.Character);
            else if (client?.Character != null && client.Character == pilot) Stop();
        }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        { msg.WriteSingle(remaining); msg.WriteSingle(cooldown); msg.WriteInt32(direction); msg.WriteUInt16(pilot?.ID ?? (ushort)0); }
#endif
    }
}
