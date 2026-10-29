using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using System.Linq;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    class GrappleForceControl : ItemComponent, IClientSerializable, IServerSerializable
    {
        [Serialize(1f, IsPropertySaveable.No)]
        public float GravityMultiplier { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool WinchMode { get; set; }
        public GrappleForceControl(Item item, ContentXElement element) : base(item, element) { IsActive = true; }

        private bool IsHeldBy(Character character) => character != null && !character.Removed
            && !character.IsDead && character.HeldItems.Contains(Item);

        public override void Update(float deltaTime, Camera cam)
        {
#if CLIENT
            ReadWheel(deltaTime);
#endif
        }
        // Condition is zero throughout recharge; controls must continue while the gun is 'broken'.
        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

#if CLIENT
        private int? previousWheel;
        private int wheelRemainder;
        private bool dirty;
        private float sendTimer;
        private void ReadWheel(float deltaTime)
        {
            int wheel = Mouse.GetState().ScrollWheelValue;
            int difference = previousWheel.HasValue ? wheel - previousWheel.Value : 0;
            previousWheel = wheel;
            var user = Character.Controlled;
            if (!IsHeldBy(user)) { wheelRemainder = 0; dirty = false; return; }
            if (user.IsKeyDown(InputType.Aim) && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen)
            {
                wheelRemainder += difference;
                int steps = wheelRemainder / 120;
                if (steps != 0)
                {
                    wheelRemainder -= steps * 120;
                    GravityMultiplier = MathHelper.Clamp(GravityMultiplier + steps * 0.25f, 0.25f, 3f);
                    dirty = true;
                }
            }
            else wheelRemainder = 0;
            sendTimer -= deltaTime;
            if (dirty && sendTimer <= 0)
            {
                if (GameMain.NetworkMember?.IsClient == true) Item.CreateClientEvent(this);
                dirty = false;
                sendTimer = 0.1f;
            }
        }
        public override void DrawHUD(SpriteBatch spriteBatch, Character character)
        {
            if (character != Character.Controlled || !IsHeldBy(character)) return;
            GUI.DrawString(spriteBatch, new Vector2(40, 180),
                $"钩锁拉力 {GravityMultiplier:0.00} × 重力 · {(WinchMode ? "收放绳：W/S" : "持续牵引")}", Color.Cyan);
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
            => msg.WriteByte((byte)MathHelper.Clamp((int)Math.Round(GravityMultiplier * 4), 1, 12));
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            int step = msg.ReadByte();
            if (step >= 1 && step <= 12 && !dirty) GravityMultiplier = step * 0.25f;
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            int step = msg.ReadByte();
            if (step < 1 || step > 12 || !IsHeldBy(client?.Character)) return;
            GravityMultiplier = step * 0.25f;
            Item.CreateServerEvent(this);
        }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
            => msg.WriteByte((byte)MathHelper.Clamp((int)Math.Round(GravityMultiplier * 4), 1, 12));
#endif
    }
    // Dedicated hook component: no mode or launch-time patch required.
    class GrapplePullAssist : ItemComponent
    {
        private static readonly Identifier ReleaseId = "aic_ground_adhesion_release".ToIdentifier();
        private float paidOutLength = -1;
        public GrapplePullAssist(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        public override void Update(float deltaTime, Camera cam)
        {
            var rope = Item.GetComponent<Rope>();
            var projectile = Item.GetComponent<Projectile>();
            if (rope == null || projectile == null || Item.Removed || Item.ParentInventory != null) return;
            // Native Rope does not pull a standing character toward a static structure in every branch.
            // Keep its attachment/snap behavior, but own the force so it cannot be applied twice.
            rope.SourcePullForce = 0;
            var user = projectile.User ?? projectile.Attacker;
            if (rope.Snapped || !rope.IsActive || !projectile.IsStuckToTarget
                || user == null || user.Removed || user.IsDead || user.IsIncapacitated
                || !user.IsKeyDown(InputType.Aim)
                || projectile.Launcher?.ParentInventory?.Owner != user)
            { paidOutLength = -1; return; }
            var collider = user.AnimController.Collider;
            if (collider == null) return;
            Vector2 difference = Item.WorldPosition - user.WorldPosition;
            float distance = difference.Length();
            if (distance > rope.MaxLength) return;
            bool winch = projectile.Launcher.GetComponent<GrappleForceControl>()?.WinchMode == true;
            if (!winch) paidOutLength = -1;
            if (paidOutLength < 0) paidOutLength = MathHelper.Clamp(distance, 80, Math.Max(80, rope.MaxLength));
            int reel = (user.IsKeyDown(InputType.Down) ? 1 : 0) - (user.IsKeyDown(InputType.Up) ? 1 : 0);
            paidOutLength = MathHelper.Clamp(paidOutLength + reel * 200f * MathHelper.Clamp(deltaTime, 0, 0.1f),
                80, Math.Max(80, rope.MaxLength));
            // MaxLength is the native break distance, not a winch setting. Keep
            // it unchanged: shortening it would immediately snap a taut rope.
            if ((winch && distance < paidOutLength - 3) || distance < (winch ? 1 : 80)) return;
            float multiplier = MathHelper.Clamp(projectile.Launcher.GetComponent<GrappleForceControl>()?.GravityMultiplier ?? 1f, 0.25f, 3f);
            Vector2 direction = difference / distance;
            float approachingSpeed = Vector2.Dot(collider.LinearVelocity - (Item.body?.LinearVelocity ?? Vector2.Zero), direction);
            float support = Math.Max(0, -Vector2.Dot(GameMain.World.Gravity, direction));
            float tension = MathHelper.Clamp((distance - paidOutLength) * 0.01f * 18f - approachingSpeed * 7f + support,
                0, support + Math.Max(GameMain.World.Gravity.Length(), 9.82f) * multiplier);
            if (!winch) { paidOutLength = -1; tension = Math.Max(GameMain.World.Gravity.Length(), 9.82f) * multiplier; }
            Vector2 acceleration = direction * tension;
            // Equal acceleration on the rig avoids dragging only the capsule while the limbs lag behind.
            if (collider.Enabled) collider.ApplyForce(acceleration * collider.Mass, maxVelocity: 5f);
            foreach (var limb in user.AnimController.Limbs)
            {
                if (limb.Removed || limb.IsSevered || limb.body == null || !limb.body.Enabled
                    || limb.body == collider) continue;
                limb.body.ApplyForce(acceleration * limb.body.Mass, maxVelocity: 5f);
            }
            if (GameMain.NetworkMember?.IsClient == true) return;
            if (MagneticBoots.EnabledFor(user)) return;
            var health = user.CharacterHealth;
            var existing = health.GetAffliction(ReleaseId);
            if (existing != null) existing.Strength = Math.Max(existing.Strength, 0.3f);
            else if (AfflictionPrefab.Prefabs.TryGet(ReleaseId, out var prefab))
                health.ApplyAffliction(null, prefab.Instantiate(0.3f));
        }
    }
}
