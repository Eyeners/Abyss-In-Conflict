using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Shared payload for stasis and reflection. Each sampled ray is replayed once.
    internal sealed class StoredProjectile
    {
        internal ItemPrefab Prefab;
        internal Character User;
        internal Item Launcher;
        internal Vector2 Point, Direction, Velocity;
        internal float Condition, Damage;
        internal int Quality, MaxTargets;
        internal bool Hitscan;
        internal List<Body> Ignored;

        internal static StoredProjectile Capture(Projectile p, Vector2 point, Vector2 direction, Vector2 velocity)
            => new StoredProjectile
            {
                Prefab = p.Item.Prefab, User = p.User ?? p.Attacker, Launcher = p.Launcher,
                Point = point, Direction = direction, Velocity = velocity,
                Condition = p.Item.Condition, Quality = p.Item.Quality,
                Damage = p.Attack?.DamageMultiplier ?? 1, Hitscan = p.Hitscan,
                MaxTargets = p.MaxTargetsToHit,
                Ignored = p.IgnoredBodies == null ? new List<Body>() : new List<Body>(p.IgnoredBodies)
            };

        internal void Spawn(Vector2 world, Submarine sub)
        {
            var session = GameMain.GameSession;
            Entity.Spawner.AddItemToSpawnQueue(Prefab, world - (sub?.Position ?? Vector2.Zero), sub,
                condition: Condition, quality: Quality, onSpawned: item =>
                {
                    if (GameMain.GameSession != session)
                    { Entity.Spawner.AddItemToRemoveQueue(item); return; }
                    var p = item.GetComponent<Projectile>();
                    if (p == null) return;
                    var user = User?.Removed == false ? User : null;
                    p.Launcher = Launcher?.Removed == false ? Launcher : null;
                    p.Attacker = user;
                    p.Spread = 0; p.HitScanCount = 1; p.MaxTargetsToHit = MaxTargets;
                    float angle = (float)Math.Atan2(Direction.Y, Direction.X);
                    p.Shoot(user, item.SimPosition, item.SimPosition, angle, Ignored, true, Damage, 0);
                    if (!Hitscan && !item.Removed && item.body != null) item.body.LinearVelocity = Velocity;
                });
        }
    }
}
