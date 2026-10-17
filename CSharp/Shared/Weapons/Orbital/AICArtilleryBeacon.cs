using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Spawned stage items also provide the clients with an animation start time.
    class AICArtilleryBeacon : ItemComponent
    {
        [Serialize("beacon", IsPropertySaveable.No)] public string Stage { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float WarningSoundDuration { get; set; }
        private Character attacker;
        private float age, grounded, throwerX;
        private int nextStrike, frame = -1;
        private float[] strikeTimes;
        private bool armed, finished, pending, ignited, barrageStarted;
#if CLIENT
        private bool warningSoundPlayed;
#endif

        public AICArtilleryBeacon(Item item, ContentXElement element) : base(item, element) { IsActive = true; }

        public override bool Use(float deltaTime, Character user = null)
        {
            if (Stage != "beacon" || user == null) return false;
            attacker = user;
            throwerX = user.WorldPosition.X;
            armed = true;
            return true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (finished || Item.Removed) return;
            // Run on both server and clients: inventory state is already replicated.
            // Once released into the world, keep the flare lit even if picked up again.
            if (Stage == "beacon")
            {
                if (Item.ParentInventory == null) ignited = true;
                var light = Item.GetComponent<LightComponent>();
                if (light != null)
                {
                    light.IsOn = ignited && Item.Condition > 0;
                    light.IsActive = light.IsOn;
                }
            }
            if (Stage != "beacon" && Item.body != null)
            {
                Item.body.CollisionCategories = Category.None;
                Item.body.CollidesWith = Category.None;
            }
            age += deltaTime;
#if CLIENT
            if (Stage == "area" && frame < 0)
            {
                frame = 0;
                GameMain.ParticleManager?.CreateParticle("aic_artillery_area_warning", Item.WorldPosition, Vector2.Zero, 0, Item.CurrentHull);
            }
            if (Stage == "marker")
            {
                if (!warningSoundPlayed && WarningSoundDuration > 0 && age >= 1.15f - WarningSoundDuration)
                {
                    warningSoundPlayed = true;
                    PlaySound(ActionType.OnUse);
                }
                int next = Math.Min(22, (int)(age / 0.05f));
                if (next != frame)
                {
                    frame = next;
                    GameMain.ParticleManager?.CreateParticle("aic_orbital_lock_" + frame, Item.WorldPosition, Vector2.Zero, 0, Item.CurrentHull);
                }
            }
#endif
            if (GameMain.NetworkMember?.IsClient == true || pending) return;
            if (Stage == "beacon")
            {
                if (barrageStarted) return;
                // Throwable retains the actual thrower, including native throw paths.
                var thrower = Item.GetComponent<Throwable>()?.CurrentThrower;
                if (thrower != null && !thrower.Removed)
                {
                    attacker = thrower;
                    throwerX = thrower.WorldPosition.X;
                    armed = true;
                }
                if (!armed || Item.ParentInventory != null || Item.body == null) { grounded = 0; return; }
                // Require support directly underneath: an apex or a wall hit is not a landing.
                bool resting = Item.body.LinearVelocity.LengthSquared() < 0.25f
                    && Submarine.PickBody(Item.SimPosition, Item.SimPosition - Vector2.UnitY * 0.25f,
                        ignoredBodies: new List<Body> { Item.body.FarseerBody },
                        collisionCategory: Physics.CollisionWall | Physics.CollisionRepairableWall
                            | Physics.CollisionLevel | Physics.CollisionPlatform,
                        customPredicate: fixture => !fixture.IsSensor,
                        allowInsideFixture: true) != null;
                grounded = resting ? grounded + deltaTime : 0;
                if (grounded < 3) return;
                pending = true;
                Spawn("aic_artillery_area", Item.Position, area =>
                {
                    barrageStarted = true;
                    pending = false;
                    // The flare continues burning independently of the one-shot barrage.
                });
            }
            else if (Stage == "area")
            {
                if (strikeTimes == null)
                {
                    strikeTimes = new float[24];
                    for (int batch = 0; batch < 4; batch++)
                        for (int i = 0; i < 6; i++)
                            strikeTimes[batch * 6 + i] = 5 + batch * 3 + Rand.Range(-0.4f, 0.4f);
                    Array.Sort(strikeTimes);
                }
                // Schedule whole marker animations, preserving sound/launch alignment.
                while (nextStrike < strikeTimes.Length && age >= strikeTimes[nextStrike])
                {
                    nextStrike++;
                    // Choose the prefab on the server so every client hears the same variant.
                    Spawn("aic_artillery_marker" + Rand.Range(1, 4), Item.Position + new Vector2(Rand.Range(-600f, 600f), 0), marker => { });
                }
                if (nextStrike >= strikeTimes.Length && age >= 16) Finish();
            }
            else if (Stage == "marker" && age >= 1.15f)
            {
                pending = true;
                float x = attacker?.Removed == false ? attacker.WorldPosition.X : throwerX;
                Vector2 offset = new Vector2(x < Item.WorldPosition.X ? -600 : 600, 2000);
                Vector2 target = Item.WorldPosition;
                // The shell's spawn point is the temporary muzzle; no persistent turret is needed.
                Spawn("aic_artillery_shell", Item.Position + offset, round =>
                {
                    var projectile = round.GetComponent<Projectile>();
                    projectile.Attacker = attacker?.Removed == false ? attacker : null;
                    Vector2 direction = target - round.WorldPosition;
                    projectile.Shoot(projectile.Attacker, round.SimPosition, round.SimPosition,
                        (float)Math.Atan2(direction.Y, direction.X), new List<Body>(), true);
                    Finish();
                });
            }
        }

        private void Finish()
        {
            if (finished) return;
            finished = true;
            Entity.Spawner?.AddItemToRemoveQueue(Item);
        }

        private void Spawn(string identifier, Vector2 position, Action<Item> callback)
        {
            var prefab = ItemPrefab.Find(null, identifier.ToIdentifier());
            if (prefab == null || Entity.Spawner == null) { Finish(); return; }
            var sub = Item.Submarine;
            var level = Level.Loaded;
            var session = GameMain.GameSession;
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, sub, onSpawned: spawned =>
            {
                if (finished || Item.Removed || sub?.Removed == true || Level.Loaded != level || GameMain.GameSession != session)
                { Entity.Spawner?.AddItemToRemoveQueue(spawned); return; }
                var component = spawned.GetComponent<AICArtilleryBeacon>();
                if (component != null) { component.attacker = attacker; component.throwerX = throwerX; }
                callback(spawned);
            });
        }
    }
}
