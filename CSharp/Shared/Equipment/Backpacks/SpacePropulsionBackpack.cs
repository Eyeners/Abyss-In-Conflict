using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    class SpacePropulsionBackpack : ItemComponent, IClientSerializable, IServerSerializable
    {
        private static readonly Identifier ReleaseId = "aic_ground_adhesion_release".ToIdentifier();
        private bool braking;
        private Vector2 aiThrust;
        private float aiSyncTimer;
        internal readonly SpaceNavigationRecovery Navigation = new SpaceNavigationRecovery();
        [Serialize(true, IsPropertySaveable.Yes)]
        public bool IsOn { get; set; }
        [Serialize(0.5f, IsPropertySaveable.Yes)]
        public float ThrustGravityMultiplier { get; set; }
        [Serialize(0.1f, IsPropertySaveable.Yes)]
        public float CruiseGravityMultiplier { get; set; }
        [Serialize(0.05f, IsPropertySaveable.Yes)]
        public float PrecisionGravityMultiplier { get; set; }
        [Serialize(5f, IsPropertySaveable.Yes)]
        public float SpeedLimit { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool SafetyOverride { get; set; }

        private float PlayerGravity(Character user) => user.IsKeyDown(InputType.Crouch)
            ? PrecisionGravityMultiplier : user.IsKeyDown(InputType.Run) ? ThrustGravityMultiplier : CruiseGravityMultiplier;

        public SpacePropulsionBackpack(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        private bool IsWornBy(Character user) => user != null && !user.Removed && !user.IsDead
            && !user.IsIncapacitated && user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag) == Item;

        private void SetBrake(bool value)
        {
            if (braking == value) return;
            braking = value;
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }

        public override void Update(float deltaTime, Camera cam)
        {
#if CLIENT
            bool q = Keyboard.GetState().IsKeyDown(Keys.Q);
            bool pressed = q && !previousQ;
            previousQ = q;
#endif
            var user = Item.ParentInventory?.Owner as Character;
            if (!IsOn || Item.Condition <= 0 || SpaceBackpackDamage.IsFailing(user) || !IsWornBy(user))
            { SetBrake(false); Navigation.Reset(); return; }
            var collider = user.AnimController?.Collider;
            if (collider == null || !collider.Enabled) return;
            bool autopilot = SpaceBackpackAI.Applies(user);
#if CLIENT
            if (autopilot && GameMain.NetworkMember?.IsClient == true)
            {
                DrawExhaust(user, aiThrust, deltaTime);
                return;
            }
            bool blocked = user == Character.Controlled
                && (GUI.KeyboardDispatcher.Subscriber != null || GUI.InputBlockingMenuOpen);
            if (pressed && user == Character.Controlled && !blocked)
            {
                SetBrake(true);
                if (GameMain.NetworkMember?.IsClient == true) Item.CreateClientEvent(this);
            }
#endif
            Vector2 direction = Vector2.Zero;
            if (FoehnJetJump.Equipped(user)?.Flying == true) return;
            if (user.IsKeyDown(InputType.Up)) direction.Y += 1;
            if (user.IsKeyDown(InputType.Down)) direction.Y -= 1;
            if (user.IsKeyDown(InputType.Left)) direction.X -= 1;
            if (user.IsKeyDown(InputType.Right)) direction.X += 1;
#if CLIENT
            if (blocked) direction = Vector2.Zero;
#endif
            if (!autopilot && direction.LengthSquared() > 0) { direction.Normalize(); SetBrake(false); }
            float speedLimit = Math.Max(0.1f, SpeedLimit);
            bool overspeed = !SafetyOverride && collider.LinearVelocity.LengthSquared() > speedLimit * speedLimit + 0.001f;
            if (!autopilot && !braking && direction == Vector2.Zero && !overspeed) return;

            // Use reference gravity, including on maps whose actual gravity is zero.
            float acceleration = 9.82f * MathHelper.Clamp(ThrustGravityMultiplier, 0, 5);
            // Native Run input (default Shift) is already replicated to the server.
            if (!autopilot && !braking)
                acceleration = 9.82f * MathHelper.Clamp(PlayerGravity(user), 0, 5);
            acceleration *= MathHelper.Clamp(Item.ConditionPercentage / 100f, 0, 1);
            Vector2 thrust = direction * acceleration;
            if (autopilot)
            {
                Vector2 desired = SpaceBackpackAI.DesiredVelocity(user, acceleration);
                SetBrake(desired.LengthSquared() < 0.0001f);
                float gain = (1 - (float)Math.Exp(-3 * deltaTime)) / Math.Max(deltaTime, 0.0001f);
                thrust = (desired - collider.LinearVelocity) * gain;
                if (thrust.LengthSquared() > acceleration * acceleration) thrust = Vector2.Normalize(thrust) * acceleration;
                aiThrust = thrust;
#if SERVER
                aiSyncTimer -= deltaTime;
                if (aiSyncTimer <= 0) { aiSyncTimer = 0.15f; Item.CreateServerEvent(this); }
#endif
            }
            else if (braking)
            {
                Vector2 velocity = collider.LinearVelocity;
                float speed = velocity.Length();
                // Exponential braking, capped to the same thrust budget and never reversing velocity.
                thrust = speed > 0.001f ? -velocity / speed * Math.Min(acceleration,
                    speed * (1 - (float)Math.Exp(-3 * deltaTime)) / Math.Max(deltaTime, 0.0001f)) : Vector2.Zero;
            }
            if (!autopilot && !braking && !SafetyOverride)
                thrust = LimitThrust(collider.LinearVelocity, thrust, acceleration, speedLimit, deltaTime);
            collider.ApplyForce(thrust * collider.Mass);
            foreach (var limb in user.AnimController.Limbs)
            {
                if (limb.Removed || limb.IsSevered || limb.body == null || !limb.body.Enabled || limb.body == collider) continue;
                limb.body.ApplyForce(thrust * limb.body.Mass);
            }
            if (GameMain.NetworkMember?.IsClient != true && !MagneticBoots.EnabledFor(user))
            {
                var existing = user.CharacterHealth.GetAffliction(ReleaseId);
                if (existing != null) existing.Strength = Math.Max(existing.Strength, 0.3f);
                else if (AfflictionPrefab.Prefabs.TryGet(ReleaseId, out var prefab))
                    user.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(0.3f));
            }
#if CLIENT
            DrawExhaust(user, thrust, deltaTime);
#endif
        }
        internal static Vector2 LimitThrust(Vector2 velocity, Vector2 thrust, float acceleration, float limit, float deltaTime)
        {
            if (deltaTime <= 0) return Vector2.Zero;
            Vector2 next = velocity + thrust * deltaTime;
            if (next.LengthSquared() <= limit * limit) return thrust;
            // Project the predicted velocity onto the speed circle: tangential steering is retained.
            Vector2 correction = (Vector2.Normalize(next) * limit - velocity) / deltaTime;
            // External impulses/closing override above the cap must brake smoothly, never teleport velocity.
            if (correction.LengthSquared() > acceleration * acceleration)
                correction = Vector2.Normalize(correction) * acceleration;
            return correction;
        }
#if CLIENT
        private void DrawExhaust(Character user, Vector2 thrust, float deltaTime)
        {
            exhaustTimer -= deltaTime;
            if (exhaustTimer <= 0)
            {
                exhaustTimer = 0.1f;
                if (braking)
                {
                    Emit(user, Vector2.UnitX); Emit(user, -Vector2.UnitX);
                    Emit(user, Vector2.UnitY); Emit(user, -Vector2.UnitY);
                }
                else if (thrust.LengthSquared() > 0.01f) Emit(user, -Vector2.Normalize(thrust));
            }
        }
        private bool previousQ;
        private float exhaustTimer;
        private void Emit(Character user, Vector2 direction)
        {
            var particle = GameMain.ParticleManager.CreateParticle("steamspray",
                user.WorldPosition + direction * 18, direction * 90, 0, user.CurrentHull);
            if (particle != null) particle.Size *= 0.3f;
        }
        public override void DrawHUD(SpriteBatch spriteBatch, Character character)
        {
            if (character != Character.Controlled || !IsWornBy(character)) return;
            GUI.DrawString(spriteBatch, new Vector2(40, 210),
                Item.Condition <= 0 ? "Thruster: BROKEN" : SpaceBackpackDamage.IsFailing(character) ? "Thruster: LEAK / OUT OF CONTROL"
                    : !IsOn ? "Thruster: OFF" : braking ? "Thruster: BRAKING | WASD to resume"
                    : $"Thruster: {PlayerGravity(character) * Item.ConditionPercentage / 100f:0.##}g | {Item.ConditionPercentage:0}% | Ctrl fine / Shift boost / Q brake | {(SafetyOverride ? "OVERRIDE" : "MAX " + SpeedLimit.ToString("0.#"))}", Color.LightCyan);
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) => msg.WriteBoolean(true);
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            braking = msg.ReadBoolean();
            aiThrust = new Vector2(msg.ReadSingle(), msg.ReadSingle());
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            bool requested = msg.ReadBoolean();
            if (requested && IsOn && IsWornBy(client?.Character)) SetBrake(true);
        }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteBoolean(braking);
            msg.WriteSingle(aiThrust.X);
            msg.WriteSingle(aiThrust.Y);
        }
#endif
    }
}
