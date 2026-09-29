using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    class AirburstIncendiary : ItemComponent
    {
        private float age;
        private float timer;
        private bool spent;
        private Vector2 lastVelocity;
        public AirburstIncendiary(Item item, ContentXElement element) : base(item, element) { IsActive = true; }

        public override void Update(float deltaTime, Camera cam)
        {
            if (spent || Item.Removed || GameMain.NetworkMember?.IsClient == true) return;
            var projectile = Item.GetComponent<Projectile>();
            if (projectile == null || !projectile.IsActive || Item.ParentInventory != null || Item.body == null) return;
            age += deltaTime;
            Vector2 velocity = Item.body.LinearVelocity;
            if (velocity.LengthSquared() > 0.01f) lastVelocity = velocity;
            if (age > 10) { spent = true; Entity.Spawner.AddItemToRemoveQueue(Item); return; }
            // Brief muzzle clearance; unlike a radial sensor this cannot see the shooter behind the round.
            if (age < 0.05f) return;
            timer -= deltaTime;
            if (timer > 0) return;
            timer = 0.025f;
            float speed = velocity.Length();
            if (speed < 0.1f) return;
            Vector2 start = Item.SimPosition;
            // Predict the nearly straight fragment spray along the current flight direction.
            Vector2 end = start + velocity * (3f / speed);
            var hit = Submarine.PickBody(start, end, collisionCategory:
                Physics.CollisionWall | Physics.CollisionRepairableWall | Physics.CollisionLevel | Physics.CollisionCharacter,
                customPredicate: fixture => IsTarget(fixture, projectile), allowInsideFixture: true);
            if (hit != null) Burst(projectile);
        }

        private bool IsTarget(Fixture fixture, Projectile projectile)
        {
            if (fixture.Body == Item.body.FarseerBody || fixture.IsSensor) return false;
            if (projectile.IgnoredBodies != null && projectile.IgnoredBodies.Contains(fixture.Body)) return false;
            var data = fixture.Body.UserData;
            if (data is Limb limb)
                return limb.character != projectile.Attacker && !limb.character.IsDead
                    && limb.character.Submarine == Item.Submarine;
            if (data is Item target)
                return target.Submarine == Item.Submarine && target.GetComponent<Door>() != null;
            if (data is Structure structure) return structure.Submarine == Item.Submarine;
            return Item.Submarine == null && (fixture.CollisionCategories & Physics.CollisionLevel) != 0;
        }

        private void Burst(Projectile source)
        {
            var prefab = ItemPrefab.Find(null, "aic_airburst_incendiary_fragment_2".ToIdentifier());
            if (prefab == null || Entity.Spawner == null) return;
            spent = true;
            var sub = Item.Submarine;
            var position = Item.SimPosition;
            var displayPosition = ConvertUnits.ToDisplayUnits(position);
            var attacker = source.Attacker;
            var launcher = source.Launcher;
            var ignored = source.IgnoredBodies == null ? new List<Body>() : new List<Body>(source.IgnoredBodies);
            var session = GameMain.GameSession;
            var level = Level.Loaded;
            float heading = (float)Math.Atan2(lastVelocity.Y, lastVelocity.X);
            for (int i = 0; i < 10; i++)
            {
                float angle = heading + Rand.Range(-0.35f, 0.35f);
                Entity.Spawner.AddItemToSpawnQueue(prefab, displayPosition, sub, onSpawned: fragment =>
                {
                    if (fragment.Removed) return;
                    if (GameMain.GameSession != session || Level.Loaded != level || sub?.Removed == true)
                    { Entity.Spawner.AddItemToRemoveQueue(fragment); return; }
                    var shot = fragment.GetComponent<Projectile>();
                    shot.Attacker = attacker;
                    shot.Launcher = launcher;
                    shot.Shoot(attacker, position, position, angle, ignored, true);
                });
            }
            Entity.Spawner.AddItemToRemoveQueue(Item);
        }
    }
}
