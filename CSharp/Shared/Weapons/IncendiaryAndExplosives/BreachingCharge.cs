using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Contacts;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class BreachingCharge : IAssemblyPlugin
    {
        private static readonly Identifier ChargeId = "aic_breaching_charge".ToIdentifier();
        private static readonly Identifier BlastId = "aic_breaching_charge_blast".ToIdentifier();
        private static readonly Identifier GridId = "aic_laser_grid_grenade".ToIdentifier();
        private static readonly Identifier GridEmitterId = "aic_laser_grid_emitter".ToIdentifier();
        private static readonly Identifier TripwireId = "aic_laser_tripwire_detonator".ToIdentifier();
        private static readonly Identifier DoorTag = "door".ToIdentifier();
        private const float Depth = 110f;
        private const float DoorRange = 50f;
        private const float FuseDelay = 3f;
        private static ConditionalWeakTable<Item, object> triggered = new ConditionalWeakTable<Item, object>();
        private static int generation;
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.breaching.charge");
            try
            {
                var method = AccessTools.Method(typeof(Item), "OnCollision");
                if (method == null) throw new MissingMethodException("Item.OnCollision");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(BreachingCharge), nameof(OnCollision)));
                harmony.Patch(AccessTools.Method(typeof(RepairTool), "FixBody"),
                    prefix: new HarmonyMethod(typeof(BreachingCharge), nameof(TripwireHit)));
                DebugConsole.NewMessage("[AIC BreachingCharge] Item collision hook ready");
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC BreachingCharge] Initialization failed: " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            generation++;
            harmony?.UnpatchSelf();
            triggered = new ConditionalWeakTable<Item, object>();
        }

        private static bool OnCollision(Item __instance, Fixture f1, Fixture f2, Contact contact, ref bool __result)
        {
            var item = __instance;
            bool laserGrid = item.Prefab.Identifier == GridId;
            bool tripwire = item.Prefab.Identifier == TripwireId;
            if ((!tripwire && !laserGrid && item.Prefab.Identifier != ChargeId) || item.Removed || item.ParentInventory != null) return true;
            if (tripwire && (item.Condition <= 0 || item.GetComponent<LaserTripwire>()?.Disabled == true)) return true;
            if (triggered.TryGetValue(item, out _)) { __result = false; return false; }
            var target = f2;
            // Throwable does not need to launch a Projectile. Condition marks an armed throw.
            if (item.Condition >= 100 || target == null || target.IsSensor || contact == null) return true;
            var surface = target.Body.UserData as Entity;
            bool isDoor = surface is Item surfaceItem && surfaceItem.GetComponent<Door>() != null;
            bool isStructure = surface is Structure;
            bool isLevel = (target.CollisionCategories & Physics.CollisionLevel) != 0;
            if (!isDoor && !isStructure && !isLevel) return true;
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) return true;
            var prefab = ItemPrefab.Find(null, laserGrid ? GridEmitterId : BlastId);
            if (prefab == null || Entity.Spawner == null) return true;

            contact.GetWorldManifold(out Vector2 normal, out var points);
            // Contact normals point from fixture A to B, not necessarily from charge to wall.
            Vector2 inward = contact.FixtureA.Body == f1.Body ? normal : -normal;
            if (inward.LengthSquared() < 0.0001f) return true;
            inward.Normalize();
            var sub = isLevel ? null : surface?.Submarine;
            Vector2 impact = ConvertUnits.ToDisplayUnits(points[0]) + (sub?.Position ?? Vector2.Zero);
            var session = GameMain.GameSession;
            var level = Level.Loaded;
            int token = generation;
            triggered.Add(item, new object());
            __result = false;
            // Body and door changes must run outside the physics contact callback.
            CoroutineManager.Invoke(() =>
            {
                if (token != generation || item.Removed || GameMain.GameSession != session
                    || Level.Loaded != level || Entity.Spawner == null) return;
                if (tripwire)
                {
                    // Keep the original item and its inventory: never respawn/copy live explosives.
                    if (item.body != null)
                    {
                        item.body.LinearVelocity = Vector2.Zero;
                        item.body.AngularVelocity = 0;
                        item.body.BodyType = BodyType.Static;
                        item.body.Enabled = true;
                        // Rotation already encodes the surface direction; discard the held item's mirroring.
                        item.body.Dir = 1;
                        Vector2 position = impact - inward * 8f - (sub?.Position ?? Vector2.Zero);
                        item.SetTransform(ConvertUnits.ToSimUnits(position),
                            (float)Math.Atan2(-inward.Y, -inward.X), forceSubmarine: sub);
                    }
                    item.NonInteractable = true;
                    item.GetComponent<LaserTripwire>()?.Deploy();
                    return;
                }
                if (laserGrid)
                {
                    item.NonInteractable = true;
                    item.HiddenInGame = true;
                    if (item.body != null) item.body.Enabled = false;
                    Vector2 emitterPosition = impact - inward * 8f - (sub?.Position ?? Vector2.Zero);
                    Entity.Spawner.AddItemToSpawnQueue(prefab, emitterPosition, sub, onSpawned: emitter =>
                    {
                        if (emitter.Removed) return;
                        if (token != generation || GameMain.GameSession != session || Level.Loaded != level
                            || (sub != null && sub.Removed))
                        {
                            Entity.Spawner.AddItemToRemoveQueue(emitter);
                            return;
                        }
                        // The autonomous tool fires along its local +X axis, away from the surface.
                        float angle = (float)Math.Atan2(-inward.Y, -inward.X);
                        emitter.SetTransform(ConvertUnits.ToSimUnits(emitterPosition), angle, forceSubmarine: sub);
                    });
                    Entity.Spawner.AddItemToRemoveQueue(item);
                    return;
                }
                if (item.body != null)
                {
                    item.body.LinearVelocity = Vector2.Zero;
                    item.body.AngularVelocity = 0;
                    // Disabled bodies suppress the native item sprite as well as physics.
                    item.body.BodyType = BodyType.Static;
                    item.body.Enabled = true;
                    Vector2 surfacePosition = impact - inward * (11f * item.Scale + 2f);
                    Vector2 localPosition = surfacePosition - (sub?.Position ?? Vector2.Zero);
                    float rotation = (float)Math.Atan2(inward.Y, inward.X) - MathHelper.PiOver2;
                    item.SetTransform(ConvertUnits.ToSimUnits(localPosition), rotation, forceSubmarine: sub);
                }
                item.HiddenInGame = false;
                item.NonInteractable = true;
                Vector2 localImpact = impact - (sub?.Position ?? Vector2.Zero);
                CoroutineManager.Invoke(() =>
                {
                    if (token != generation || item.Removed || GameMain.GameSession != session
                        || Level.Loaded != level || Entity.Spawner == null || (sub != null && sub.Removed)) return;
                    item.HiddenInGame = true;
                    // Keep the contact anchored to a moving submarine during the fuse.
                    Vector2 currentImpact = localImpact + (sub?.Position ?? Vector2.Zero);
                    BreakDoors(currentImpact);
                    Entity.Spawner.AddItemToSpawnQueue(prefab, currentImpact + inward * Depth, onSpawned: blast =>
                    {
                        if (blast.Removed) return;
                        if (token != generation || GameMain.GameSession != session || Level.Loaded != level)
                        {
                            Entity.Spawner.AddItemToRemoveQueue(blast);
                            return;
                        }
                        blast.HiddenInGame = true;
                    });
                    Entity.Spawner.AddItemToRemoveQueue(item);
                }, FuseDelay);
            }, 0.0f);
            return false;
        }

        private static void BreakDoors(Vector2 point)
        {
            foreach (var item in Item.ItemList)
            {
                if (item.Removed || !item.HasTag(DoorTag)) continue;
                var door = item.GetComponent<Door>();
                if (door == null) continue;
                // Barotrauma world rectangles use a top edge with Y increasing upwards.
                var rect = item.WorldRect;
                var nearest = new Vector2(MathHelper.Clamp(point.X, rect.X, rect.Right),
                    MathHelper.Clamp(point.Y, rect.Y - rect.Height, rect.Y));
                if (Vector2.DistanceSquared(point, nearest) > DoorRange * DoorRange) continue;
                item.NonInteractable = false;
                item.NonPlayerTeamInteractable = false;
                item.Indestructible = false;
                item.InvulnerableToDamage = false;
                door.Stuck = 0;
                item.Condition = 0;
#if SERVER
                SyncProperty(item, "NonInteractable");
                SyncProperty(item, "NonPlayerTeamInteractable");
#endif
            }
        }

        private static bool TripwireHit(RepairTool __instance, Body targetBody, ref bool __result)
        {
            if (!(__instance is LaserTripwire wire)) return true;
            var character = targetBody?.UserData is Limb limb ? limb.character : targetBody?.UserData as Character;
            wire.Trip(character);
            __result = character != null;
            return false;
        }

#if SERVER
        private static void SyncProperty(Item item, string name)
        {
            if (item.SerializableProperties.TryGetValue(name.ToIdentifier(), out var property))
                GameMain.NetworkMember?.CreateEntityEvent(item, new Item.ChangePropertyEventData(property, item));
        }
#endif
    }
}
