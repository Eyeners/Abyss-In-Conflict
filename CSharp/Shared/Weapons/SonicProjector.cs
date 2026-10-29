using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Particles;
#endif

namespace AIC.Components
{
    internal static class SonicGeometry
    {
        internal static bool InCone(Vector2 offset, Vector2 direction, float range, float halfAngle)
        {
            float squared = offset.LengthSquared();
            if (squared < 0.000001f || squared > range * range) return false;
            return Vector2.Dot(offset, direction) >= (float)Math.Sqrt(squared) * (float)Math.Cos(halfAngle);
        }
        internal static float WaterEntry(Vector2 origin, Vector2 dir, float length, Func<Vector2, bool> water)
        {
            if (water(origin)) return 0;
            int steps = Math.Max(1, (int)Math.Ceiling(length / 25));
            float previous = 0;
            for (int i = 1; i <= steps; i++)
            {
                float distance = length * i / steps;
                if (water(origin + dir * distance))
                {
                    for (int b = 0; b < 8; b++)
                    {
                        float middle = (previous + distance) * 0.5f;
                        if (water(origin + dir * middle)) distance = middle; else previous = middle;
                    }
                    return distance;
                }
                previous = distance;
            }
            return -1;
        }
        internal static float WaterLength(Vector2 origin, Vector2 dir, float length, Func<Vector2, bool> water)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(length / 25));
            for (int i = 1; i <= steps; i++)
                if (!water(origin + dir * (length * i / steps))) return length * (i - 1) / steps;
            return length;
        }
    }

    // Native weapon input, aim, cooldown, sounds and HUD; instantaneous custom
    // queries replace physical ammunition. Damage and battery cost are authoritative.
    internal sealed class SonicProjector : RangedWeapon, IServerSerializable
    {
        [Serialize(20f, IsPropertySaveable.No)] public float BatteryCost { get; set; }
        [Serialize(20f, IsPropertySaveable.No)] public float AirRange { get; set; }
        [Serialize(3, IsPropertySaveable.No)] public int AirTargets { get; set; }
        [Serialize(20f, IsPropertySaveable.No)] public float WaterRange { get; set; }
        [Serialize(22f, IsPropertySaveable.No)] public float WaterAngle { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float WaterDamageMultiplier { get; set; }
        [Serialize(96, IsPropertySaveable.No)] public int BubbleCount { get; set; }
        private readonly Attack sonicAttack;
        private readonly RangeLimitedBeam limitedBeam = new() { IgnoreClouds = true };
        private readonly HashSet<Item> wetClouds = new();
        private readonly List<PendingCloud> pendingClouds = new();
        private sealed record PendingCloud(Item Cloud, double Due);
        private void DisperseCloud(Item cloud, float distance)
        {
            if (cloud.Removed || cloud.Condition <= 0) return;
            if (!vortexShot) { Entity.Spawner.AddEntityToRemoveQueue(cloud); return; }
            pendingClouds.Add(new PendingCloud(cloud, Timing.TotalTime + distance / VortexSpeed));
            IsActive = true;
        }
        private void DisperseAirClouds(Pulse pulse)
        {
            if (pulse.AirLength <= 0) return;
            Vector2 direction = new Vector2((float)Math.Cos(pulse.Angle), (float)Math.Sin(pulse.Angle));
            Vector2 end = pulse.Origin + direction * pulse.AirLength;
            // Query cloud bodies independently of projectile collision categories and penetration count.
            wetClouds.Clear();
            void Collect(Submarine space)
            {
                Vector2 offset = space?.Position ?? Vector2.Zero;
                Vector2 padding = new Vector2(145f);
                var box = new AABB(ConvertUnits.ToSimUnits(Vector2.Min(pulse.Origin, end) - padding - offset),
                    ConvertUnits.ToSimUnits(Vector2.Max(pulse.Origin, end) + padding - offset));
                GameMain.World.QueryAABB(f =>
                {
                    if (f.Body.UserData is Item cloud && cloud.Submarine == space && CloudProjectileFilter.IsCloud(cloud))
                        wetClouds.Add(cloud);
                    return true;
                }, ref box);
            }
            Collect(null);
            foreach (var sub in Submarine.Loaded) if (!sub.Removed) Collect(sub);
            foreach (var cloud in wetClouds)
            {
                if (cloud.Removed) continue;
                float along = Vector2.Dot(cloud.WorldPosition - pulse.Origin, direction);
                float distance = MathHelper.Clamp(along, 0, pulse.AirLength);
                float width = vortexShot ? MathHelper.Lerp(35f, 95f, distance / pulse.AirLength) : 36f;
                Vector2 nearest = pulse.Origin + direction * distance;
                // Existing cloud emitters have a 50-display-unit body radius.
                if (Vector2.DistanceSquared(cloud.WorldPosition, nearest) > (width + 50f) * (width + 50f)
                    || Trace(pulse.Origin, cloud.WorldPosition, false) < 0.999f) continue;
                DisperseCloud(cloud, distance);
            }
            wetClouds.Clear();
        }
        private readonly Dictionary<Limb, BeamHit> airHits = new();
        private readonly List<BeamHit> ordered = new();
        private readonly HashSet<Limb> wetLimbs = new();
        private readonly Func<Fixture, bool> areaCallback;
        private readonly record struct BeamHit(Limb Limb, Vector2 Point, float Fraction);
        private sealed record Pulse(Vector2 Origin, float Angle, float AirLength, bool Water,
            Vector2 WaterOrigin, float WaterLength, float HalfAngle, bool Vortex = false) : IEventData;
        private Character shooter;
        private bool vortexShot;
        private EffectAnchor shotAnchor;
        internal const float VortexSpeed = 1800f;
        private readonly List<PendingHit> pendingHits = new();
        private sealed record GroundHit(Vector2 Position, Submarine Sub, Character User, int Direction, double Due);
        private readonly List<GroundHit> groundHits = new();
        private sealed record PendingHit(Character User, Limb Limb, Vector2 Point, Vector2 Origin,
            double Due, float Multiplier, EffectAnchor Anchor);
        private void ApplyOrQueue(Character user, Limb limb, Vector2 point, Vector2 origin)
        {
            if (sonicAttack == null) return;
            if (!vortexShot) { sonicAttack.DoDamageToLimb(user, limb, point, 1, playSound: true); return; }
            pendingHits.Add(new PendingHit(user, limb, point, origin,
                Timing.TotalTime + Vector2.Distance(origin, point) / VortexSpeed, sonicAttack.DamageMultiplier, shotAnchor));
            IsActive = true;
        }
        public override void Update(float deltaTime, Camera cam)
        {
            base.Update(deltaTime, cam);
            if (GameMain.NetworkMember?.IsClient == true) return;
            for (int i = pendingClouds.Count - 1; i >= 0; i--)
            {
                var hit = pendingClouds[i];
                if (Timing.TotalTime < hit.Due) continue;
                pendingClouds.RemoveAt(i);
                if (!hit.Cloud.Removed)
                    Entity.Spawner.AddEntityToRemoveQueue(hit.Cloud);
            }
            for (int i = groundHits.Count - 1; i >= 0; i--)
            {
                var hit = groundHits[i];
                if (Timing.TotalTime < hit.Due) continue;
                groundHits.RemoveAt(i);
                if (hit.Sub?.Removed == true) continue;
                var prefab = ItemPrefab.Find(null, "aic_sonic_groundwave".ToIdentifier());
                if (prefab != null)
                    Entity.Spawner.AddItemToSpawnQueue(prefab, hit.Position, hit.Sub,
                        onSpawned: spawned => spawned.GetComponent<AICShockwaveGrenade>().StartAt(hit.User, hit.Direction));
            }
            for (int i = pendingHits.Count - 1; i >= 0; i--)
            {
                var hit = pendingHits[i];
                if (Timing.TotalTime < hit.Due) continue;
                pendingHits.RemoveAt(i);
                if (!Valid(hit.Limb) || hit.User.Removed) continue;
                // A delayed hit must not follow a target that has left the wave path.
                Vector2 offset = hit.Anchor.Offset;
                if (Vector2.DistanceSquared(hit.Point + offset, hit.Limb.WorldPosition) > 50f * 50f
                    || Trace(hit.Origin + offset, hit.Limb.WorldPosition, false) < 0.999f) continue;
                float previous = sonicAttack.DamageMultiplier;
                try
                {
                    sonicAttack.DamageMultiplier = hit.Multiplier;
                    sonicAttack.DoDamageToLimb(hit.User, hit.Limb, hit.Limb.WorldPosition, 1, playSound: true);
                }
                finally { sonicAttack.DamageMultiplier = previous; }
            }
            if (pendingHits.Count > 0 || groundHits.Count > 0 || pendingClouds.Count > 0) IsActive = true;
        }
        private Submarine traceSpace;
        private Vector2 coneOrigin, coneDirection;
        private float coneRange, coneHalfAngle;
        private static readonly Func<Vector2, bool> WaterTest = InWater;
#if CLIENT
        private static readonly Action<RangedWeapon, float> SetReloadTimer =
            AccessTools.MethodDelegate<Action<RangedWeapon, float>>(AccessTools.PropertySetter(typeof(RangedWeapon), "ReloadTimer"));
#endif
        public SonicProjector(Item item, ContentXElement element) : base(item, element)
        {
            var xml = element.GetChildElement("Attack");
            if (xml != null) sonicAttack = new Attack(xml, item.Prefab.Identifier.ToString(), item);
            areaCallback = AreaFixture;
        }
        private Item Battery()
        {
            if (Item.OwnInventory == null) return null;
            foreach (var cell in Item.OwnInventory.AllItems)
                if (!cell.Removed && cell.HasTag("mobilebattery".ToIdentifier()) && cell.Condition >= Math.Max(0, BatteryCost)) return cell;
            return null;
        }
        public override bool Use(float dt, Character character = null)
        {
            // Client input still reaches the server through native character input.
            // One server event supplies visuals/sound, including the firing client.
            if (GameMain.NetworkMember?.IsClient == true || character == null || character.Removed) return false;
            var battery = Battery();
            if (battery == null || !base.Use(dt, character)) return false;
            battery.Condition = Math.Max(0, battery.Condition - Math.Max(0, BatteryCost));
            shooter = character;
            vortexShot = false;
            foreach (var attachment in Item.OwnInventory.AllItems)
                if (attachment.HasTag("aic_sonic_vortex".ToIdentifier())) { vortexShot = true; break; }
            try
            {
                var pulse = Fire(character);
                DisperseAirClouds(pulse);
                pulse = pulse with { Vortex = vortexShot };
#if SERVER
                Item.CreateServerEvent(this, pulse);
#endif
#if CLIENT
                DrawPulse(pulse);
#endif
            }
            finally { shooter = null; airHits.Clear(); ordered.Clear(); wetLimbs.Clear(); wetClouds.Clear(); }
            return true;
        }
        private Pulse Fire(Character user)
        {
            float angle = Item.body.Rotation + (Item.body.Dir < 0 ? MathHelper.Pi : 0);
            Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vector2 origin = Item.WorldPosition + ConvertUnits.ToDisplayUnits(TransformedBarrelPos);
            shotAnchor = EffectAnchor.At(origin);
            float range = ConvertUnits.ToDisplayUnits(Math.Clamp(AirRange, 0.1f, 100));
            float waterRange = ConvertUnits.ToDisplayUnits(Math.Clamp(WaterRange, 0.1f, 100));
            float half = MathHelper.ToRadians(Math.Clamp(WaterAngle, 1, 120) * 0.5f);
            Vector2 aimSource = ConvertUnits.ToDisplayUnits(user.AnimController.AimSourceSimPos)
                + (user.Submarine?.Position ?? Vector2.Zero);
            if (Trace(aimSource, origin, false) < 0.999f)
                return new Pulse(origin, angle, 0, false, origin, waterRange, half);
            if (InWater(origin))
            {
                WaterPulse(origin, direction, waterRange, half);
                return new Pulse(origin, angle, 0, true, origin, waterRange, half);
            }
            Vector2 end = origin + direction * range;
            float solid = Trace(origin, end, true);
            bool floorHit = limitedBeam.WallNormal.Y > 0.5f;
            float length = range * solid;
            float entry = WaterEntry(origin, direction, length);
            float airLength = entry >= 0 ? entry : length;
            foreach (var hit in airHits.Values)
                if (hit.Fraction * range <= airLength + 0.01f) ordered.Add(hit);
            ordered.Sort(static (a, b) => a.Fraction.CompareTo(b.Fraction));
            int count = 0;
            foreach (var hit in ordered)
            {
                if (!Valid(hit.Limb)) continue;
                ApplyOrQueue(user, hit.Limb, hit.Point, origin);
                if (++count >= (vortexShot ? 3 : Math.Clamp(AirTargets, 1, 16)))
                    return new Pulse(origin, angle, hit.Fraction * range, false, origin, waterRange, half);
            }
            Vector2 waterOrigin = origin + direction * Math.Max(0, entry);
            if (vortexShot && entry < 0 && solid < 1 && floorHit)
            {
                Vector2 point = origin + direction * length;
                groundHits.Add(new GroundHit(point - (Item.Submarine?.Position ?? Vector2.Zero), Item.Submarine,
                    user, direction.X < 0 ? -1 : 1, Timing.TotalTime + length / VortexSpeed));
                IsActive = true;
            }
            if (entry >= 0) WaterPulse(waterOrigin, direction, waterRange, half);
            return new Pulse(origin, angle, airLength, entry >= 0, waterOrigin, waterRange, half);
        }
        private bool Valid(Limb limb) => limb != null && !limb.Removed && !limb.IsSevered
            && limb.body?.Enabled == true && !limb.character.Removed && limb.character != shooter;
        private float Trace(Vector2 start, Vector2 end, bool air)
        {
            float fraction = limitedBeam.Cast(start, end, air, Item, shooter);
            if (air)
                foreach (var hit in limitedBeam.airHits.Values)
                    airHits[hit.Limb] = new BeamHit(hit.Limb, hit.Point, hit.Fraction);
            return fraction;
        }
        private static bool InWater(Vector2 world)
        {
            var hull = Hull.FindHull(world, useWorldCoordinates: true);
            return hull == null || hull.WaterVolume > 0 && world.Y < hull.WorldSurface;
        }
        // Quarter-metre sampling with a refined transition; no fluid particles or
        // physical water colliders are needed, and outside the hull is ocean.
        private static float WaterEntry(Vector2 origin, Vector2 dir, float length)
            => SonicGeometry.WaterEntry(origin, dir, length, WaterTest);
        private static float WaterLength(Vector2 origin, Vector2 dir, float length)
            => SonicGeometry.WaterLength(origin, dir, length, WaterTest);
        private bool AreaFixture(Fixture f)
        {
            if (f.Body.UserData is Item cloud && cloud.Submarine == traceSpace && CloudProjectileFilter.IsCloud(cloud)
                && SonicGeometry.InCone(cloud.WorldPosition - coneOrigin, coneDirection, coneRange, coneHalfAngle)) wetClouds.Add(cloud);
            if (!f.IsSensor && f.Body.UserData is Limb limb && Valid(limb) && limb.character.Submarine == traceSpace
                && SonicGeometry.InCone(limb.WorldPosition - coneOrigin, coneDirection, coneRange, coneHalfAngle)) wetLimbs.Add(limb);
            return true;
        }
        private void WaterPulse(Vector2 origin, Vector2 direction, float range, float halfAngle)
        {
            coneOrigin = origin; coneDirection = direction; coneRange = range; coneHalfAngle = halfAngle;
            CollectSpace(null);
            foreach (var sub in Submarine.Loaded) if (!sub.Removed) CollectSpace(sub);
            foreach (var cloud in wetClouds)
            {
                if (cloud.Removed || !InWater(cloud.WorldPosition)) continue;
                Vector2 delta = cloud.WorldPosition - origin;
                float length = delta.Length();
                if (Trace(origin, cloud.WorldPosition, false) < 0.999f
                    || WaterLength(origin, delta / length, length) < length - 0.01f) continue;
                DisperseCloud(cloud, length);
            }
            if (sonicAttack == null) return;
            float previousMultiplier = sonicAttack.DamageMultiplier;
            sonicAttack.DamageMultiplier = previousMultiplier * Math.Max(0, WaterDamageMultiplier);
            try
            {
                foreach (var limb in wetLimbs)
                {
                    if (!Valid(limb) || !InWater(limb.WorldPosition)) continue;
                    Vector2 delta = limb.WorldPosition - origin;
                    float length = delta.Length();
                    if (Trace(origin, limb.WorldPosition, false) < 0.999f
                        || WaterLength(origin, delta / length, length) < length - 0.01f) continue;
                    // The fourth argument is deltaTime; damage uses Attack.DamageMultiplier.
                    ApplyOrQueue(shooter, limb, limb.WorldPosition, origin);
                }
            }
            finally { sonicAttack.DamageMultiplier = previousMultiplier; }
        }
        private void CollectSpace(Submarine space)
        {
            traceSpace = space;
            Vector2 local = ConvertUnits.ToSimUnits(coneOrigin) - (space?.SimPosition ?? Vector2.Zero);
            Vector2 reach = new Vector2(ConvertUnits.ToSimUnits(coneRange));
            var box = new AABB(local - reach, local + reach);
            GameMain.World.QueryAABB(areaCallback, ref box);
        }
#if CLIENT
        private void Ribbon(Vector2 start, Vector2 end, float thickness, bool water = false)
        {
            Vector2 delta = end - start;
            float length = delta.Length();
            if (length < 1) return;
            var prefab = ParticleManager.FindPrefab(water ? "aic_sonic_ribbon_water" : "aic_sonic_ribbon");
            if (prefab == null) return;
            var particle = GameMain.ParticleManager?.CreateParticle(prefab, (start + end) * 0.5f, Vector2.Zero,
                -(float)Math.Atan2(delta.Y, delta.X), Hull.FindHull((start + end) * 0.5f));
            if (particle != null) particle.Size = water
                ? new Vector2(length, thickness * 368f)
                : new Vector2(length / 1888f, thickness);
        }
        private void DrawPulse(Pulse pulse)
        {
            var anchor = EffectAnchor.At(pulse.Origin);
            Vector2 dir = new Vector2((float)Math.Cos(pulse.Angle), (float)Math.Sin(pulse.Angle));
            if (pulse.Vortex) SonicRefraction.AddVortex(pulse.Origin, pulse.Origin + dir * pulse.AirLength, anchor: anchor);
            else SonicRefraction.Add(pulse.Origin, pulse.Origin + dir * pulse.AirLength, anchor);
            if (!pulse.Water) return;
            var fanReach = new float[33];
            for (int i = 0; i < fanReach.Length; i++)
            {
                float angle = pulse.Angle + pulse.HalfAngle * (2f * i / (fanReach.Length - 1) - 1);
                Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                float distance = pulse.WaterLength * Trace(pulse.WaterOrigin, pulse.WaterOrigin + direction * pulse.WaterLength, false);
                fanReach[i] = WaterLength(pulse.WaterOrigin, direction, distance);
            }
            if (pulse.Vortex)
                for (int i = 0; i < fanReach.Length; i += 8)
                {
                    float a = pulse.Angle + pulse.HalfAngle * (2f * i / (fanReach.Length - 1) - 1);
                    SonicRefraction.AddVortex(pulse.WaterOrigin, pulse.WaterOrigin + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * fanReach[i], anchor: anchor);
                }
            else SonicRefraction.AddFan(pulse.WaterOrigin, pulse.Angle, pulse.HalfAngle, fanReach, anchor);
            for (int i = 0; i < 9; i++)
            {
                float angle = pulse.Angle + pulse.HalfAngle * (i / 4f - 1);
                Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                float distance = pulse.WaterLength * Trace(pulse.WaterOrigin, pulse.WaterOrigin + direction * pulse.WaterLength, false);
                distance = WaterLength(pulse.WaterOrigin, direction, distance);
                Ribbon(pulse.WaterOrigin, pulse.WaterOrigin + direction * distance, 0.045f, water: true);
            }
            for (int i = 0; i < Math.Clamp(BubbleCount, 0, 256); i++)
            {
                float angle = pulse.Angle + Rand.Range(-pulse.HalfAngle, pulse.HalfAngle);
                Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                // Clip before sampling: nearby walls must shorten the plume,
                // rather than discard almost all particles sampled at full range.
                float reach = pulse.WaterLength * Trace(pulse.WaterOrigin,
                    pulse.WaterOrigin + direction * pulse.WaterLength, false);
                reach = WaterLength(pulse.WaterOrigin, direction, reach);
                if (reach < 1) continue;
                // Uniform distance retains a visible near-muzzle plume as well
                // as bubbles farther down the cone.
                float distance = Rand.Range(0f, reach);
                Vector2 position = pulse.WaterOrigin + direction * distance;
                if (!InWater(position)) continue;
                GameMain.ParticleManager?.CreateParticle("aic_sonic_bubbles", position,
                    direction * Rand.Range(30f, 80f) + new Vector2(0, 20), 0, Hull.FindHull(position));
            }
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            var pulse = new Pulse(new Vector2(msg.ReadSingle(), msg.ReadSingle()), msg.ReadSingle(), msg.ReadSingle(),
                msg.ReadBoolean(), new Vector2(msg.ReadSingle(), msg.ReadSingle()), msg.ReadSingle(), msg.ReadSingle(), msg.ReadBoolean());
            DrawPulse(pulse);
            PlaySound(ActionType.OnUse);
            SetReloadTimer(this, Reload);
            IsActive = true;
        }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            var pulse = extraData is Item.ComponentStateEventData data && data.ComponentData is Pulse p
                ? p : new Pulse(Item.WorldPosition, 0, 0, false, Item.WorldPosition, 0, 0);
            msg.WriteSingle(pulse.Origin.X); msg.WriteSingle(pulse.Origin.Y);
            msg.WriteSingle(pulse.Angle); msg.WriteSingle(pulse.AirLength); msg.WriteBoolean(pulse.Water);
            msg.WriteSingle(pulse.WaterOrigin.X); msg.WriteSingle(pulse.WaterOrigin.Y);
            msg.WriteSingle(pulse.WaterLength); msg.WriteSingle(pulse.HalfAngle);
            msg.WriteBoolean(pulse.Vortex);
        }
#endif
    }
}
