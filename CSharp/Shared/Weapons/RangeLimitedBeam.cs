using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
namespace AIC.Components
{
    // Distance is the supplied world-coordinate segment length. No weapon or damage logic.
    internal sealed class RangeLimitedBeam
    {
        private const Category HitscanCategories = Physics.CollisionWall | Physics.CollisionCharacter
            | Physics.CollisionItemBlocking | Physics.CollisionProjectile | Physics.CollisionLevel
            | Physics.CollisionLagCompensationBody;
        internal readonly record struct BeamHit(Limb Limb, Vector2 Point, float Fraction);
        internal readonly Dictionary<Limb, BeamHit> airHits = new();
        internal readonly Dictionary<Item, float> cloudHits = new();
        internal bool IgnoreClouds { get; set; }
        private readonly Func<Fixture, Vector2, Vector2, float, float> rayCallback;
        private readonly Func<Fixture, bool> startCallback;
        private Item sourceItem;
        private Character shooter;
        private Submarine traceSpace;
        private Vector2 traceStart, traceWorldStart, traceWorldEnd;
        private float wallFraction;
        internal Vector2 WallNormal { get; private set; }
        private bool collectAir;
        internal RangeLimitedBeam() { rayCallback = RayFixture; startCallback = StartFixture; }
        internal float Cast(Vector2 start, Vector2 end, bool air, Item source, Character user)
        {
            sourceItem = source; shooter = user; airHits.Clear(); cloudHits.Clear();
            float fraction = Trace(start, end, air);
            sourceItem = null; shooter = null;
            return fraction;
        }
        private bool Valid(Limb limb) => limb != null && !limb.Removed && !limb.IsSevered
            && limb.body?.Enabled == true && !limb.character.Removed && limb.character != shooter;
        private bool Wall(Fixture f)
        {
            if ((f.CollisionCategories & HitscanCategories) == 0 || f.CollidesWith == Category.None) return false;
            object data = f.Body.UserData;
            if (data is Structure s) return s.Submarine == traceSpace;
            if (data is Item i) return i != sourceItem && i.Submarine == traceSpace && i.Condition > 0
                && !(IgnoreClouds && CloudProjectileFilter.IsCloud(i))
                && (i.Prefab.DamagedByProjectiles || i.GetComponent<Door>() != null);
            return traceSpace == null && data is not Entity && data is not Limb
                && (f.CollisionCategories & Physics.CollisionLevel) != 0;
        }
        private bool StartFixture(Fixture f)
        {
            if (f.IsSensor || (f.CollisionCategories & HitscanCategories) == 0 || f.CollidesWith == Category.None) return true;
            bool wall = Wall(f);
            Limb limb = f.Body.UserData as Limb;
            bool airLimb = collectAir && Valid(limb) && limb.character.Submarine == traceSpace;
            Item cloud = f.Body.UserData as Item;
            bool airCloud = IgnoreClouds && collectAir && cloud != null && cloud.Submarine == traceSpace
                && CloudProjectileFilter.IsCloud(cloud);
            if (!wall && !airLimb && !airCloud) return true;
            f.Body.GetTransform(out var transform);
            if (!f.Shape.TestPoint(ref transform, ref traceStart)) return true;
            if (airCloud) cloudHits[cloud] = 0;
            else if (wall) wallFraction = 0;
            else airHits[limb] = new BeamHit(limb, traceWorldStart, 0);
            return true;
        }
        private float RayFixture(Fixture f, Vector2 point, Vector2 normal, float fraction)
        {
            if (f.IsSensor || (f.CollidesWith == Category.None && f.CollisionCategories != Physics.CollisionLagCompensationBody)) return -1;
            if (IgnoreClouds && f.Body.UserData is Item cloud && cloud.Submarine == traceSpace && CloudProjectileFilter.IsCloud(cloud))
            {
                if (collectAir && (!cloudHits.TryGetValue(cloud, out var previousCloud) || fraction < previousCloud))
                    cloudHits[cloud] = fraction;
            }
            else if (Wall(f))
            {
                if (fraction < wallFraction) { wallFraction = fraction; WallNormal = normal; }
            }
            else if (collectAir && f.Body.UserData is Limb limb && Valid(limb) && limb.character.Submarine == traceSpace)
            {
                if (!airHits.TryGetValue(limb, out var previous) || fraction < previous.Fraction)
                    airHits[limb] = new BeamHit(limb, Vector2.Lerp(traceWorldStart, traceWorldEnd, fraction), fraction);
            }
            return 1;
        }
        private float Trace(Vector2 start, Vector2 end, bool air)
        {
            if (Vector2.DistanceSquared(start, end) < 0.0001f) return 1;
            traceWorldStart = start; traceWorldEnd = end; wallFraction = 1; collectAir = air; WallNormal = Vector2.Zero;
            CastSpace(null);
            foreach (var sub in Submarine.Loaded) if (!sub.Removed) CastSpace(sub);
            return wallFraction;
        }
        private void CastSpace(Submarine space)
        {
            traceSpace = space;
            Vector2 offset = space?.SimPosition ?? Vector2.Zero;
            traceStart = ConvertUnits.ToSimUnits(traceWorldStart) - offset;
            Vector2 end = ConvertUnits.ToSimUnits(traceWorldEnd) - offset;
            var box = new AABB(traceStart - new Vector2(0.001f), traceStart + new Vector2(0.001f));
            GameMain.World.QueryAABB(startCallback, ref box);
            GameMain.World.RayCast(rayCallback, traceStart, end, HitscanCategories);
        }
    }
}
