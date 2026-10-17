using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Particles;
#endif

namespace AIC.Components
{
    public sealed class AICOrbitalStrike : IAssemblyPlugin
    {
        private Harmony harmony;
        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.orbital.strike");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                postfix: new HarmonyMethod(typeof(AICOrbitalStrike), nameof(OnHit)));
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), nameof(RangedWeapon.Use)),
                prefix: new HarmonyMethod(typeof(AICOrbitalStrike), nameof(CanUse)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Shoot)),
                prefix: new HarmonyMethod(typeof(AICOrbitalStrike), nameof(BeforeShoot)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(Projectile), "LaunchProjSpecific"),
                prefix: new HarmonyMethod(typeof(AICOrbitalStrike), nameof(DrawOrbitalBeam)));
#endif
        }
        public void OnLoadCompleted() { }
#if CLIENT
        private static readonly List<FarseerPhysics.Collision.AABB> beamBlockers = new();
        private static readonly List<float> beamEdges = new();
        private static bool DrawOrbitalBeam(Projectile __instance, Vector2 startLocation, Vector2 endLocation)
        {
            // Tracking beams are drawn from the shot event, including shots with no hits.
            if (__instance.Item.Prefab.Identifier == "aic_orbital_tracking_laser".ToIdentifier()) return false;
            if (__instance.Item.Prefab.Identifier != "aic_orbital_laser".ToIdentifier()
                && __instance.Item.Prefab.Identifier != "aic_orbital_pulse_laser".ToIdentifier()
                && __instance.Item.Prefab.Identifier != "aic_orbital_tracking_laser".ToIdentifier()
                && __instance.Item.Prefab.Identifier != "aic_orbital_pulse2_laser".ToIdentifier()) return true;
            // Native fallback uses camera width when fewer than MaxTargetsToHit were hit.
            // Item.WorldPosition remains at the last actual ray hit, in world coordinates.
            Vector2 end = __instance.Item.WorldPosition;
            bool wide = __instance.Item.Prefab.Identifier == "aic_orbital_laser".ToIdentifier();
            if (wide)
            {
                DrawWideBeam(startLocation);
                return false;
            }
            if (Vector2.DistanceSquared(startLocation, end) < 1)
                end = startLocation + new Vector2((float)Math.Cos(__instance.Item.body.Rotation),
                    (float)Math.Sin(__instance.Item.body.Rotation)) * 3000;
            string particle = "aic_orbital_beam";
            var prefab = ParticleManager.FindPrefab(particle);
            if (prefab != null)
            {
                // The string overload always uses Default; XML draworder does not set
                // this per-instance flag. Foreground bypasses the hull/outside filter.
                GameMain.ParticleManager?.CreateParticle(prefab, end, Vector2.Zero,
                    -(float)Math.Atan2(end.Y - startLocation.Y, end.X - startLocation.X),
                    drawOrder: ParticleDrawOrder.Foreground, collisionIgnoreTimer: 1,
                    tracerPoints: Tuple.Create(startLocation, end));
            }
            return false;
        }

        private static void DrawWideBeam(Vector2 top)
        {
            var prefab = ParticleManager.FindPrefab("aic_orbital_beam_wide");
            if (prefab == null || GameMain.ParticleManager == null) return;
            AICOrbitalDamageTool.CollectBlockers(top, 300, 3000, beamBlockers);
            beamEdges.Clear();
            beamEdges.Add(top.X - 150);
            beamEdges.Add(top.X + 150);
            foreach (var blocker in beamBlockers)
            {
                beamEdges.Add(MathHelper.Clamp(blocker.LowerBound.X, top.X - 150, top.X + 150));
                beamEdges.Add(MathHelper.Clamp(blocker.UpperBound.X, top.X - 150, top.X + 150));
            }
            beamEdges.Sort();
            for (int i = 1; i < beamEdges.Count; i++)
            {
                float width = beamEdges[i] - beamEdges[i - 1];
                if (width < 0.01f) continue;
                float x = (beamEdges[i] + beamEdges[i - 1]) * 0.5f;
                Vector2 start = new Vector2(x, top.Y);
                Vector2 end = new Vector2(x, AICOrbitalDamageTool.StopHeight(top, x, 3000, beamBlockers));
                if (start.Y <= end.Y) continue;
                var beam = GameMain.ParticleManager.CreateParticle(prefab, end, Vector2.Zero,
                    MathHelper.PiOver2, drawOrder: ParticleDrawOrder.Foreground,
                    collisionIgnoreTimer: 1, tracerPoints: Tuple.Create(start, end));
                if (beam != null) beam.Size = new Vector2(beam.Size.X, width);
            }
        }
#endif
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; AICOrbitalCooldown.Reset(); }

        private static bool CanUse(RangedWeapon __instance, ref bool __result)
        {
            if (__instance.Item.GetComponent<AICOrbitalCooldown>() == null || AICOrbitalCooldown.Remaining <= 0) return true;
            __instance.currentChargeTime = 0;
            __instance.tryingToCharge = false;
            __result = false;
            return false;
        }

        private static bool BeforeShoot(Projectile __instance)
        {
            if (GameMain.NetworkMember?.IsClient == true) return true;
            var designation = __instance.Item.GetComponent<AICOrbitalDesignation>();
            if (designation == null) return true;
            if (!AICOrbitalCooldown.TryStart())
            {
                Entity.Spawner?.AddItemToRemoveQueue(__instance.Item);
                return false;
            }
            designation.Authorized = true;
            return true;
        }

        private static void OnHit(Projectile __instance, bool __result)
        {
            if (!__result || GameMain.NetworkMember?.IsClient == true) return;
            __instance.Item.GetComponent<AICOrbitalDesignation>()?.Mark(__instance);
        }
    }

    // One deadline for all teams and weapons, independent of inventory and durability.
    class AICOrbitalCooldown : ItemComponent, IServerSerializable
    {
        private static readonly HashSet<AICOrbitalCooldown> instances = new();
        private static double readyAt;
        private bool initialSync, lightSuppressed, restoreLight;
#if CLIENT
        private float previewTimer, beepTimer;
#endif
        public static float Remaining => (float)Math.Max(0, readyAt - Timing.TotalTime);
        public AICOrbitalCooldown(Item item, ContentXElement element) : base(item, element)
        {
            instances.Add(this);
            IsActive = true;
        }
        public override void Update(float deltaTime, Camera cam)
        {
            // First update also synchronizes weapons spawned during an existing cooldown.
            if (!initialSync) { Sync(); initialSync = true; }
            var light = Item.GetComponent<LightComponent>();
            bool cooling = Remaining > 0;
            var weapon = Item.GetComponent<RangedWeapon>();
            var user = Item.ParentInventory?.Owner as Character;
            if (weapon != null && (cooling || user == null || !user.IsKeyDown(InputType.Shoot) || !user.IsKeyDown(InputType.Aim)))
            {
                weapon.currentChargeTime = 0;
                weapon.tryingToCharge = false;
            }
            if (light != null)
            {
                if (cooling)
                {
                    if (!lightSuppressed) { restoreLight = light.IsOn; lightSuppressed = true; }
                    light.IsOn = false;
                }
                else if (lightSuppressed)
                {
                    light.IsOn = restoreLight;
                    lightSuppressed = false;
                }
            }
#if CLIENT
            UpdatePreview(deltaTime, cooling);
#endif
        }
#if CLIENT
        private void UpdatePreview(float deltaTime, bool cooling)
        {
            var weapon = Item.GetComponent<RangedWeapon>();
            var user = Item.ParentInventory?.Owner as Character;
            if (cooling || weapon == null || weapon.currentChargeTime <= 0 || user == null
                || !user.IsKeyDown(InputType.Shoot) || !user.IsKeyDown(InputType.Aim))
            { previewTimer = 0; beepTimer = 0; return; }
            beepTimer -= deltaTime;
            if (beepTimer <= 0)
            {
                beepTimer = 0.5f;
                PlaySound(ActionType.OnUse, user: user);
            }
            previewTimer -= deltaTime;
            if (previewTimer > 0) return;
            previewTimer = 0.04f;
            var probe = weapon.FindProjectile(false);
            if (probe == null || Item.body == null) return;
            Vector2 start = weapon.TransformedBarrelPos + Item.body.SimPosition;
            if (Item.Submarine != null) start += Item.Submarine.SimPosition;
            float rotation = Item.body.Rotation + (Item.body.Dir < 0 ? -MathHelper.Pi : 0);
            Vector2 end = start + new Vector2((float)Math.Cos(rotation), (float)Math.Sin(rotation)) * 500;
            float nearest = float.MaxValue;
            Vector2 target = Vector2.Zero;
            void Cast(Submarine sub)
            {
                Vector2 offset = sub?.SimPosition ?? Vector2.Zero;
                foreach (var hit in probe.DoRayCast(start - offset, end - offset, sub))
                {
                    var body = hit.Fixture.Body;
                    if (sub == null && (body.UserData is Entity entity && entity.Submarine != null
                        || body.UserData is Limb otherLimb && otherLimb.character?.Submarine != null)) continue;
                    if (body.UserData is Limb limb && limb.character == user || body.UserData is Character
                        || body == Item.body.FarseerBody) continue;
                    Vector2 world = hit.Point + offset;
                    float distance = Vector2.DistanceSquared(start, world);
                    if (distance < nearest) { nearest = distance; target = world; }
                }
            }
            Cast(null);
            foreach (var sub in Submarine.Loaded) Cast(sub);
            if (nearest == float.MaxValue) return;
            int frame = Math.Clamp((int)(weapon.currentChargeTime / 0.05f), 0, 22);
            GameMain.ParticleManager?.CreateParticle("aic_orbital_lock_" + frame,
                ConvertUnits.ToDisplayUnits(target), Vector2.Zero, 0);
        }
#endif
        internal static bool TryStart()
        {
            if (Remaining > 0) return false;
            readyAt = Timing.TotalTime + 60;
            foreach (var component in instances)
            {
                if (component.Item.Removed) continue;
                var weapon = component.Item.GetComponent<RangedWeapon>();
                if (weapon != null) { weapon.currentChargeTime = 0; weapon.tryingToCharge = false; }
                component.Sync();
            }
            return true;
        }
        private void Sync()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        protected override void RemoveComponentSpecific()
        {
            instances.Remove(this);
            base.RemoveComponentSpecific();
        }
        internal static void Reset() { readyAt = 0; instances.Clear(); }
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
            => msg.WriteSingle(Remaining);
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            float remaining = msg.ReadSingle();
            if (float.IsFinite(remaining)) readyAt = Math.Max(readyAt, Timing.TotalTime + MathHelper.Clamp(remaining, 0, 60));
        }
#endif
    }

    class AICOrbitalDesignation : ItemComponent
    {
        [Serialize("aic_orbital_marker", IsPropertySaveable.No)] public string MarkerIdentifier { get; set; }
        private bool marked;
        internal bool Authorized;
        public AICOrbitalDesignation(Item item, ContentXElement element) : base(item, element) { }
        public void Mark(Projectile projectile)
        {
            if (!Authorized || marked || Entity.Spawner == null) return;
            var prefab = ItemPrefab.Find(null, MarkerIdentifier.ToIdentifier());
            if (prefab == null) return;
            marked = true;
            var position = Item.Position;
            var sub = Item.Submarine;
            var attacker = projectile.Attacker ?? projectile.User;
            var launcher = projectile.Launcher;
            var level = Level.Loaded;
            var session = GameMain.GameSession;
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, sub, onSpawned: marker =>
            {
                if (Level.Loaded != level || GameMain.GameSession != session || sub?.Removed == true)
                { Entity.Spawner?.AddItemToRemoveQueue(marker); return; }
                marker.GetComponent<AICOrbitalEmitter>().Attacker = attacker;
                marker.GetComponent<AICOrbitalEmitter>().Launcher = launcher;
            });
        }
    }

    // Both the marker and the temporary firing point are static, non-colliding items.
    class AICOrbitalEmitter : ItemComponent, IServerSerializable
    {
        [Serialize(false, IsPropertySaveable.No)] public bool IsMarker { get; set; }
        [Serialize(2500f, IsPropertySaveable.No)] public float Height { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float Lifetime { get; set; }
        [Serialize(0.1f, IsPropertySaveable.No)] public float Interval { get; set; }
        [Serialize("aic_orbital_emitter", IsPropertySaveable.No)] public string EmitterIdentifier { get; set; }
        [Serialize("aic_orbital_laser", IsPropertySaveable.No)] public string ProjectileIdentifier { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool SeekEnemies { get; set; }
        [Serialize(1000f, IsPropertySaveable.No)] public float SearchRadius { get; set; }
        [Serialize(0, IsPropertySaveable.No)] public int ShotLimit { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float HorizontalOffset { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float SpreadDegrees { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool AimAtMarker { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool TrackTarget { get; set; }
        [Serialize(0.5f, IsPropertySaveable.No)] public float TurnSpeed { get; set; }
        [Serialize(15f, IsPropertySaveable.No)] public float TrackingArc { get; set; }
        public Character Attacker;
        public Item Launcher;
        private Item marker;
        private int shotsFired;
        private float age, timer;
        private bool started, pending, expired;
        private Character trackedTarget;
        private Vector2 trackingCenter;
        private Submarine trackingSubmarine;
        private Vector2 TrackingWorldCenter => trackingCenter
            + (trackingSubmarine?.Removed == false ? trackingSubmarine.Position : Vector2.Zero);
        private float initialAngle, trackingAngle, searchTimer;
        private bool trackingInitialized;
        private Vector2 shotVisualStart, shotVisualEnd;
#if CLIENT
        private float markerParticleTimer;
        private int visualShots;
#endif
        public AICOrbitalEmitter(Item item, ContentXElement element) : base(item, element) { IsActive = true; }

        public override void Update(float deltaTime, Camera cam)
        {
            if (expired || Item.Removed) return;
            if (IsMarker && TrackTarget) Item.HiddenInGame = true;
            if (IsMarker && TrackTarget && trackedTarget?.Removed == false && !trackedTarget.IsDead)
            {
                Item.SetTransform(ConvertUnits.ToSimUnits(trackedTarget.WorldPosition
                    - (Item.Submarine?.Position ?? Vector2.Zero)), 0);
                Item.UpdateTransform();
            }
#if CLIENT
            if (TrackTarget && GameMain.NetworkMember?.IsClient == true)
            {
                age += deltaTime;
                if (age >= Lifetime) { expired = true; Item.HiddenInGame = true; return; }
            }
            if (IsMarker && (!TrackTarget || (trackedTarget?.Removed == false && !trackedTarget.IsDead)))
            {
                markerParticleTimer -= deltaTime;
                if (markerParticleTimer <= 0)
                {
                    markerParticleTimer = 0.08f;
                    GameMain.ParticleManager?.CreateParticle("aic_orbital_marker_glow", Item.WorldPosition,
                        Vector2.Zero, 0, Item.CurrentHull);
                }
            }
#endif
            if (Item.body != null)
            {
                Item.body.BodyType = BodyType.Static;
                Item.body.CollisionCategories = Category.None;
                Item.body.CollidesWith = Category.None;
            }
            if (GameMain.NetworkMember?.IsClient == true) return;
            age += deltaTime;
            if (age >= Lifetime)
            {
                Finish();
                return;
            }
            if (IsMarker)
            {
                if (started) return;
                started = true;
                Vector2 offset = new Vector2(Rand.Range(-HorizontalOffset, HorizontalOffset), Height);
                Spawn(EmitterIdentifier, Item.Position + offset, emitter =>
                {
                    var component = emitter.GetComponent<AICOrbitalEmitter>();
                    component.Attacker = Attacker;
                    component.Launcher = Launcher;
                    component.marker = Item;
                    component.trackingSubmarine = Item.Submarine;
                    component.trackingCenter = Item.Position;
                    component.Lifetime = Math.Max(0.1f, Lifetime - age);
                });
                return;
            }
            Item.GetComponent<AICOrbitalDamageTool>()?.DamageTick(deltaTime, Attacker);
            if (TrackTarget) UpdateTracking(deltaTime);
            timer -= deltaTime;
            if (timer > 0 || pending) return;
            timer = Math.Max(0.02f, Interval);
            Character target = TrackTarget ? trackedTarget : SeekEnemies ? FindEnemy() : null;
            if (SeekEnemies && target == null) return;
            pending = true;
            Spawn(ProjectileIdentifier, Item.Position, round =>
            {
                pending = false;
                if (SeekEnemies && !ValidEnemy(target))
                { Entity.Spawner?.AddItemToRemoveQueue(round); return; }
                var shot = round.GetComponent<Projectile>();
                shot.Attacker = Attacker?.Removed == false ? Attacker : null;
                shot.Launcher = Launcher?.Removed == false ? Launcher : null;
                Vector2 direction = target == null ? -Vector2.UnitY : target.WorldPosition - Item.WorldPosition;
                if (target == null && AimAtMarker && marker?.Removed == false)
                    direction = marker.WorldPosition - Item.WorldPosition;
                float angle = (float)Math.Atan2(direction.Y, direction.X)
                    + MathHelper.ToRadians(Rand.Range(-SpreadDegrees, SpreadDegrees));
                if (TrackTarget) angle = trackingAngle;
                Vector2 shotStart = round.WorldPosition;
                shot.Shoot(shot.Attacker, round.SimPosition, round.SimPosition, angle, new List<Body>(), true);
                shotsFired++;
                if (TrackTarget)
                {
                    shotVisualStart = shotStart;
                    Vector2 forward = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                    float distance = Vector2.Dot(round.WorldPosition - shotStart, forward);
                    shotVisualEnd = shotStart + forward * (distance > 1 ? Math.Min(distance, 3000) : 3000);
#if CLIENT
                    DrawTrackingShot();
#endif
#if SERVER
                    Item.CreateServerEvent(this);
#endif
                }
                if (ShotLimit > 0 && shotsFired >= ShotLimit) Finish();
            });
        }

        private void UpdateTracking(float deltaTime)
        {
            if (!trackingInitialized)
            {
                Vector2 direction = TrackingWorldCenter - Item.WorldPosition;
                trackingAngle = initialAngle = (float)Math.Atan2(direction.Y, direction.X);
                trackingInitialized = true;
            }
            searchTimer -= deltaTime;
            if (trackedTarget != null && !ValidEnemy(trackedTarget))
            {
                trackedTarget = null;
                searchTimer = 0;
                SyncTrackingMarker();
            }
            if (trackedTarget == null && searchTimer <= 0)
            {
                searchTimer = 0.2f;
                trackedTarget = FindEnemy();
                SyncTrackingMarker();
            }
            if (trackedTarget == null) return;
            Vector2 aim = trackedTarget.WorldPosition - Item.WorldPosition;
            trackingAngle = TurnTowards(initialAngle, trackingAngle, (float)Math.Atan2(aim.Y, aim.X),
                TurnSpeed, TrackingArc, deltaTime);
        }

        private void SyncTrackingMarker()
        {
            if (marker?.Removed != false) return;
            var visual = marker.GetComponent<AICOrbitalEmitter>();
            if (visual == null || visual.trackedTarget == trackedTarget) return;
            visual.trackedTarget = trackedTarget;
#if SERVER
            marker.CreateServerEvent(visual);
#endif
        }

        internal static float TurnTowards(float initial, float current, float desired, float speed, float arc, float deltaTime)
        {
            float limit = MathHelper.ToRadians(Math.Max(0, arc));
            float offset = MathHelper.Clamp(MathHelper.WrapAngle(desired - initial), -limit, limit);
            float delta = MathHelper.WrapAngle(initial + offset - current);
            float step = MathHelper.ToRadians(Math.Max(0, speed)) * deltaTime;
            return current + MathHelper.Clamp(delta, -step, step);
        }

#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            msg.WriteUInt16(trackedTarget?.Removed == false ? trackedTarget.ID : (ushort)0);
            msg.WriteBoolean(expired);
            msg.WriteUInt16((ushort)Math.Min(shotsFired, ushort.MaxValue));
            msg.WriteSingle(shotVisualStart.X);
            msg.WriteSingle(shotVisualStart.Y);
            msg.WriteSingle(shotVisualEnd.X);
            msg.WriteSingle(shotVisualEnd.Y);
            msg.WriteUInt16(marker?.Removed == false ? marker.ID : (ushort)0);
        }
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            trackedTarget = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            expired = msg.ReadBoolean();
            int count = msg.ReadUInt16();
            shotVisualStart = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            shotVisualEnd = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            var markerItem = Entity.FindEntityByID(msg.ReadUInt16()) as Item;
            if (TrackTarget && !IsMarker && count > visualShots) DrawTrackingShot();
            visualShots = count;
            if (expired)
            {
                Item.HiddenInGame = true;
                if (markerItem?.GetComponent<AICOrbitalEmitter>() is AICOrbitalEmitter visual)
                {
                    visual.expired = true;
                    markerItem.HiddenInGame = true;
                }
            }
        }

        private void DrawTrackingShot()
        {
            var prefab = ParticleManager.FindPrefab("aic_orbital_tracking_beam");
            Vector2 direction = shotVisualEnd - shotVisualStart;
            if (prefab == null || direction.LengthSquared() < 1) return;
            GameMain.ParticleManager?.CreateParticle(prefab, shotVisualEnd, Vector2.Zero,
                -(float)Math.Atan2(direction.Y, direction.X), drawOrder: ParticleDrawOrder.Foreground,
                collisionIgnoreTimer: 1, tracerPoints: Tuple.Create(shotVisualStart, shotVisualEnd));
        }
#endif

        private bool ValidEnemy(Character target)
        {
            if (target == null || target.Removed || target.IsDead || !target.Enabled
                || Attacker == null || Attacker.Removed || target == Attacker || Attacker.IsFriendly(target)) return false;
            Vector2 center = TrackTarget ? TrackingWorldCenter : marker?.Removed == false ? marker.WorldPosition : Item.WorldPosition - Vector2.UnitY * Height;
            return target.WorldPosition.Y < Item.WorldPosition.Y
                && Vector2.DistanceSquared(target.WorldPosition, center) <= SearchRadius * SearchRadius;
        }

        private Character FindEnemy()
        {
            Character closest = null;
            float best = float.MaxValue;
            Vector2 center = TrackTarget ? TrackingWorldCenter : marker?.Removed == false ? marker.WorldPosition : Item.WorldPosition - Vector2.UnitY * Height;
            foreach (var candidate in Character.CharacterList)
            {
                if (!ValidEnemy(candidate)) continue;
                float distance = Vector2.DistanceSquared(candidate.WorldPosition, center);
                if (distance < best) { best = distance; closest = candidate; }
            }
            return closest;
        }

        private void Finish()
        {
            if (expired) return;
            expired = true;
            if (TrackTarget) Item.HiddenInGame = true;
#if SERVER
            if (TrackTarget) Item.CreateServerEvent(this);
#endif
            Entity.Spawner?.AddItemToRemoveQueue(Item);
            if (marker != null && !marker.Removed)
            {
                var visual = marker.GetComponent<AICOrbitalEmitter>();
                if (TrackTarget && visual != null) visual.Finish();
                else Entity.Spawner?.AddItemToRemoveQueue(marker);
            }
        }

        private void Spawn(string identifier, Vector2 position, Action<Item> callback)
        {
            var prefab = ItemPrefab.Find(null, identifier.ToIdentifier());
            if (prefab == null || Entity.Spawner == null) { pending = false; return; }
            var sub = Item.Submarine;
            var level = Level.Loaded;
            var session = GameMain.GameSession;
            Entity.Spawner.AddItemToSpawnQueue(prefab, position, sub, onSpawned: spawned =>
            {
                if (expired || Item.Removed || sub?.Removed == true || level != Level.Loaded || session != GameMain.GameSession)
                { Entity.Spawner?.AddItemToRemoveQueue(spawned); return; }
                callback(spawned);
            });
        }
    }
}
