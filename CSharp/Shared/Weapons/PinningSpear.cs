using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using FarseerPhysics.Dynamics.Contacts;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal sealed class PinningSpear : ItemComponent
    {
        [Serialize(1, IsPropertySaveable.No)] public int MaxTargets { get; set; }
        [Serialize(8f, IsPropertySaveable.No)] public float PinDuration { get; set; }
        [Serialize(4f, IsPropertySaveable.No)] public float FlightDuration { get; set; }
        [Serialize(3f, IsPropertySaveable.No)] public float BreakDistance { get; set; }
        [Serialize(150f, IsPropertySaveable.No)] public float TipOffset { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float CarrySpeed { get; set; }
        [Serialize(10f, IsPropertySaveable.No)] public float CarryAcceleration { get; set; }
        [Serialize(0.6f, IsPropertySaveable.No)] public float CarryDuration { get; set; }
        private readonly Attack attack;
        private readonly HashSet<Character> hits = new();
        private readonly List<(Limb limb, RevoluteJoint joint)> captures = new();
        private Character thrower;
        private Vector2 previous, direction;
        private Submarine flightSub;
        private WeldJoint pin;
        private float remaining;
        private float carryTime;
        private Vector2 carryDirection;
        private bool flying;
        private Body pendingWall;
        private Vector2 pendingPosition;
        private readonly HashSet<PhysicsBody> carriedBodies = new();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;

        public PinningSpear(Item item, ContentXElement element) : base(item, element)
        {
            var xml = element.GetChildElement("Attack");
            if (xml != null) attack = new Attack(xml, item.Prefab.Identifier.ToString(), item);
            IsActive = true;
        }
        public override bool Use(float deltaTime, Character character = null)
        {
            if (character == null || character.Removed || character.IsDead || !character.AllowInput
                || !character.IsKeyDown(InputType.Aim)
                || character.Inventory?.GetItemInLimbSlot(InvSlotType.RightHand) != Item) return false;
            if (!Authority) return true;
            Vector2 aim = character.CursorWorldPosition - Item.WorldPosition;
            if (aim.LengthSquared() < 0.001f) return false;
            aim.Normalize();
            Vector2 position = Item.SimPosition;
            Item.Drop(character);
            Item.SetTransform(position, (float)Math.Atan2(aim.Y, aim.X), findNewHull: true);
            Item.body.LinearVelocity = aim * 24f + character.AnimController.Collider.LinearVelocity;
            Item.body.CollidesWith = Category.Cat1 | Category.Cat8;
            Item.body.FarseerBody.IsBullet = true;
            Launched(character);
            return true;
        }
        internal void Launched(Character user)
        {
            if (!Authority || Item.body == null) return;
            Release();
            thrower = user;
            flightSub = Item.Submarine;
            Vector2 velocity = Item.body.LinearVelocity;
            if (velocity.LengthSquared() < 1) return;
            direction = Vector2.Normalize(velocity);
            if (Item.body.Dir < 0) Item.FlipX(relativeToSub: false);
            Item.body.SetTransform(Item.SimPosition, (float)Math.Atan2(direction.Y, direction.X));
            Item.body.AngularVelocity = 0;
            previous = Item.SimPosition;
            remaining = Math.Max(0.1f, FlightDuration);
            flying = true;
            IsActive = true;
            Item.body.FarseerBody.OnCollision += Collided;
        }
        private bool IsWall(Fixture fixture) =>
            fixture.Body.UserData is Structure s && !s.IsPlatform && s.Submarine == Item.Submarine
            || fixture.Body.UserData is Item i && i.Submarine == Item.Submarine && i.GetComponent<Door>() != null
            || Item.Submarine == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
        private bool Collided(Fixture self, Fixture other, Contact contact)
        {
            if (other.Body.UserData is Structure { IsPlatform: true }) return false;
            // Queue only: the physics world is locked during collision callbacks.
            if (flying && !other.IsSensor && pendingWall == null && IsWall(other))
            {
                pendingWall = other.Body;
                pendingPosition = Item.SimPosition;
            }
            return true;
        }
        public override void Update(float dt, Camera cam)
        {
            if (!Authority || (!flying && pin == null)) return;
            if (Item.Removed || Item.Condition <= 0 || Item.ParentInventory != null
                || Item.body == null || !Item.body.Enabled)
            { Release(); return; }
            if (Item.Submarine != flightSub)
            {
                previous += ConvertUnits.ToSimUnits((flightSub?.Position ?? Vector2.Zero)
                    - (Item.Submarine?.Position ?? Vector2.Zero));
                flightSub = Item.Submarine;
            }
            remaining -= dt;
            if (remaining <= 0 || (pin != null && (!pin.Enabled || !GameMain.World.JointList.Contains(pin))))
            { Release(); return; }
            if (flying)
            {
                // The physical shaft can hit the floor before the tip ray reaches it.
                if (pendingWall != null)
                {
                    PinTo(pendingWall, pendingPosition);
                    pendingWall = null;
                }
            }
            if (flying)
            {
                var velocity = Item.body.LinearVelocity;
                if (velocity.LengthSquared() > 1) direction = Vector2.Normalize(velocity);
                if (captures.Count == 0)
                {
                    Item.body.SetTransform(Item.SimPosition, (float)Math.Atan2(direction.Y, direction.X));
                    Item.body.AngularVelocity = 0;
                }
                else
                    direction = new Vector2((float)Math.Cos(Item.body.Rotation), (float)Math.Sin(Item.body.Rotation));
                Vector2 tip = Item.SimPosition + direction * ConvertUnits.ToSimUnits(TipOffset * Item.Scale);
                Sweep(previous, tip);
                previous = Item.SimPosition + direction * ConvertUnits.ToSimUnits(TipOffset * Item.Scale);
            }
            for (int n = captures.Count - 1; n >= 0; n--)
            {
                var entry = captures[n];
                var limb = entry.limb;
                if (limb.Removed || limb.IsSevered || limb.character.Removed
                    || limb.character.Submarine != Item.Submarine || !limb.body.Enabled
                    || !entry.joint.Enabled || !GameMain.World.JointList.Contains(entry.joint)
                    || Vector2.DistanceSquared(entry.joint.WorldAnchorA, entry.joint.WorldAnchorB) > BreakDistance * BreakDistance)
                {
                    if (GameMain.World.JointList.Contains(entry.joint)) GameMain.World.Remove(entry.joint);
                    captures.RemoveAt(n);
                }
                else if (pin != null && !limb.character.IsDead && limb.character.Stun < 0.3f)
                {
                    // Refresh only while the wall and limb joints are intact.
                    // Do not shorten another stun or clear it when this pin releases.
                    limb.character.SetStun(0.3f);
                }
            }
            if (flying && captures.Count > 0 && carryTime > 0)
            {
                carryTime = Math.Max(0, carryTime - dt);
                var target = captures[0].limb.character;
                carriedBodies.Clear();
                carriedBodies.Add(Item.body);
                if (target.AnimController.Collider != null) carriedBodies.Add(target.AnimController.Collider);
                foreach (var part in target.AnimController.Limbs)
                    if (!part.Removed && !part.IsSevered && part.body != null) carriedBodies.Add(part.body);
                // Accelerate the connected assembly together, never kick every limb
                // to projectile speed in one frame. The constraint holds the puncture.
                Vector2 velocity = captures[0].limb.body.LinearVelocity;
                float acceleration = Math.Clamp((CarrySpeed - Vector2.Dot(velocity, carryDirection)) * 4,
                    0, Math.Max(0, CarryAcceleration));
                foreach (var part in carriedBodies)
                    if (part.Enabled)
                        part.ApplyForce((carryDirection * acceleration - GameMain.World.Gravity) * part.Mass);
                carriedBodies.Clear();
            }
        }
        private void Sweep(Vector2 start, Vector2 end)
        {
            if (Vector2.DistanceSquared(start, end) < 0.000001f) return;
            // Recast after each hit: the nearest fixture always wins, including walls.
            for (int pass = 0; pass <= Math.Clamp(MaxTargets, 1, 16); pass++)
            {
                Body nearest = null;
                Vector2 impact = end;
                GameMain.World.RayCast((fixture, point, normal, fraction) =>
                {
                    if (fixture.IsSensor || fixture.Body == Item.body.FarseerBody) return -1;
                    if (fixture.Body.UserData is Limb limb)
                    {
                        if (hits.Count >= Math.Clamp(MaxTargets, 1, 16) || limb.Removed || limb.IsSevered
                            || limb.character == thrower || limb.character.Removed
                            || limb.character.Submarine != flightSub || hits.Contains(limb.character)) return -1;
                    }
                    else
                    {
                        if (!IsWall(fixture)) return -1;
                    }
                    nearest = fixture.Body;
                    impact = point;
                    return fraction;
                }, start, end);
                if (nearest == null) return;
                if (nearest.UserData is Limb hit)
                {
                    hits.Add(hit.character);
                    attack?.DoDamageToLimb(thrower, hit, hit.WorldPosition, 1, playSound: true);
                    if (!hit.Removed && !hit.IsSevered)
                    {
                        // Move only the unconnected spear, never the victim. Its centre
                        // and the limb's impact point coincide before the joint exists.
                        Item.body.SetTransform(impact, Item.body.Rotation);
                        var joint = new RevoluteJoint(Item.body.FarseerBody, hit.body.FarseerBody,
                            Vector2.Zero, hit.body.FarseerBody.GetLocalPoint(impact))
                        { CollideConnected = false };
                        GameMain.World.Add(joint);
                        captures.Add((hit, joint));
                        carryDirection = direction;
                        carryTime = Math.Max(0, CarryDuration);
                        // Remove the large relative velocity before the rigid joint
                        // gets its first physics solve; do not accelerate the victim here.
                        Item.body.LinearVelocity = hit.body.LinearVelocity;
                        Item.body.AngularVelocity = hit.body.AngularVelocity;
                    }
                    // Repositioning invalidates this frame's old sweep segment.
                    return;
                }
                PinTo(nearest, impact - direction * ConvertUnits.ToSimUnits(TipOffset * Item.Scale));
                return;
            }
        }
        private void PinTo(Body wall, Vector2 position)
        {
            // Moving an already attached spear teleports its joint anchor and yanks
            // the victim. Pin the current position instead.
            if (captures.Count > 0) position = Item.SimPosition;
            Item.body.SetTransform(position, Item.body.Rotation);
            Item.body.ResetDynamics();
            // Keep the original limb joint: wall -> spear -> punctured limb.
            // Do not move its anchor to a new point after impact.
            pin = new WeldJoint(wall, Item.body.FarseerBody, position, position, useWorldCoordinates: true)
            { FrequencyHz = 5f, DampingRatio = 1f };
            GameMain.World.Add(pin);
            flying = false;
            remaining = Math.Max(0.1f, PinDuration);
        }
        private void Release()
        {
            if (Item.body != null) Item.body.FarseerBody.OnCollision -= Collided;
            pendingWall = null;
            carryTime = 0;
            carriedBodies.Clear();
            if (pin != null && GameMain.World.JointList.Contains(pin)) GameMain.World.Remove(pin);
            pin = null;
            flying = false;
            foreach (var entry in captures)
                if (GameMain.World.JointList.Contains(entry.joint)) GameMain.World.Remove(entry.joint);
            captures.Clear();
            hits.Clear();
            thrower = null;
        }
        public override void UpdateBroken(float dt, Camera cam) => Release();
        protected override void RemoveComponentSpecific() => Release();
    }

}
