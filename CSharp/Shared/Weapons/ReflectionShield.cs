using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed class ReflectionShield : ItemComponent, IServerSerializable, IClientSerializable
    {
        [Serialize(20f, IsPropertySaveable.No)] public float EnergyPerShot { get; set; }
        internal static readonly List<ReflectionShield> Shields = new();
        private Vector2 impactPoint, impactNormal;
        private Submarine impactSub;
        private ushort impactSequence;
        private sealed class Flight { internal Vector2 Direction; internal bool Reflected; }
        private static ConditionalWeakTable<Projectile, Flight> flights = new();
        public ReflectionShield(Item item, ContentXElement element) : base(item, element)
        { IsActive = true; Shields.Add(this); }
        private Holdable Holdable => Item.GetComponent<Holdable>();
        private PhysicsBody ShieldBody => Holdable?.Body ?? Item.body;

        private Character Holder
        {
            get
            {
                var c = Holdable?.Picker ?? Item.ParentInventory?.Owner as Character;
                if (c == null || c.Removed || c.IsDead || Item.Condition <= 0 || ShieldBody == null) return null;
                foreach (var held in c.HeldItems) if (held == Item) return c;
                return null;
            }
        }
        private Item Battery()
        {
            var inventory = Item.GetComponent<ItemContainer>()?.Inventory;
            if (inventory == null) return null;
            foreach (var cell in inventory.AllItems)
                if (!cell.Removed && cell.HasTag("mobilebattery".ToIdentifier())
                    && cell.Condition >= EnergyPerShot) return cell;
            return null;
        }
        private Vector2 Outward(Character c)
        {
            float angle = ShieldBody.Rotation;
            Vector2 normal = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            // Holdable may already rotate the item by Pi when facing left.
            return normal.X * c.AnimController.Dir < 0 ? -normal : normal;
        }

        internal static void BeginRay(Projectile __instance, Vector2 dir, out int __state)
        {
            __state = __instance.MaxTargetsToHit;
            if (Shields.Count == 0) return;
            var flight = flights.GetValue(__instance, _ => new Flight());
            flight.Direction = dir; flight.Reflected = false;
        }
        internal static void EndRay(Projectile __instance, int __state)
        {
            if (__state > 0 && flights.TryGetValue(__instance, out var flight) && flight.Reflected)
                __instance.MaxTargetsToHit = __state;
        }
        internal static bool Collision(Projectile __instance, Fixture target, Vector2 collisionNormal,
            Vector2 velocity, ref bool __result)
        {
            var p = __instance;
            if (flights.TryGetValue(p, out var flight) && flight.Reflected) { __result = false; return false; }
            if (target == null || target.IsSensor || p.IgnoredBodies?.Contains(target.Body) == true) return true;
            var item = target?.Body?.UserData as Item;
            var shield = item?.GetComponent<ReflectionShield>();
            if (shield == null && target?.Body != null)
                foreach (var candidate in Shields)
                    if (candidate.ShieldBody?.FarseerBody == target.Body || candidate.Holdable?.Pusher?.FarseerBody == target.Body)
                    { shield = candidate; item = candidate.Item; break; }
            var holder = shield?.Holder;
            if (holder == null || shield.Battery() is not Item battery) return true;
            flight ??= flights.GetValue(p, _ => new Flight());
            Vector2 incomingVelocity = velocity.LengthSquared() > 0.0001f ? velocity : p.Item.body.LinearVelocity;
            Vector2 direction = p.Hitscan ? flight.Direction : incomingVelocity;
            if (direction.LengthSquared() < 0.0001f) direction = p.Item.body.LinearVelocity;
            if (direction.LengthSquared() < 0.0001f && p.Hitscan)
                direction = new Vector2((float)Math.Cos(p.Item.body.Rotation), (float)Math.Sin(p.Item.body.Rotation));
            if (direction.LengthSquared() < 0.0001f) return true;
            direction.Normalize();
            Vector2 normal = shield.Outward(holder);
            // Only the broad outward face reflects. The back and rim retain ordinary item damage.
            if (Vector2.Dot(direction, normal) >= -0.02f
                || (collisionNormal.LengthSquared() > 0.001f
                    && Math.Abs(Vector2.Dot(Vector2.Normalize(collisionNormal), normal)) < 0.65f)) return true;
            Vector2 reflected = Vector2.Normalize(direction - 2 * Vector2.Dot(direction, normal) * normal);
            Vector2 point = p.Item.WorldPosition;
            var shot = StoredProjectile.Capture(p, point, reflected, reflected * incomingVelocity.Length());
            flight.Reflected = true;
            // Stop the native hitscan loop at this contact, including penetrating ammunition.
            if (p.Hitscan) p.MaxTargetsToHit = 1;
            p.IsActive = false;
            p.Item.HiddenInGame = true;
            p.Item.body.Enabled = false;
            __result = true;
            if (GameMain.NetworkMember?.IsClient == true) return false;

            battery.Condition -= shield.EnergyPerShot;
            // Reflection becomes the holder's shot: do not retain the original shooter's immunity.
            shot.User = holder; shot.Launcher = item; shot.Ignored.Clear();
            shot.Ignored.Add(shield.ShieldBody.FarseerBody);
            if (shield.Holdable?.Pusher != null) shot.Ignored.Add(shield.Holdable.Pusher.FarseerBody);
            shot.Ignored.Add(holder.AnimController.Collider.FarseerBody);
            foreach (var limb in holder.AnimController.Limbs)
                if (!limb.IsSevered && limb.body != null) shot.Ignored.Add(limb.body.FarseerBody);
            Entity.Spawner.AddItemToRemoveQueue(p.Item);
            // Spawn outside the front face, not within the shield's contact skin.
            var sub = item.Submarine;
            Vector2 launch = point + normal * 4 + reflected * 2;
            shot.Spawn(launch, sub);
            shield.impactSub = sub;
            shield.impactPoint = point - (sub?.Position ?? Vector2.Zero);
            shield.impactNormal = normal;
            shield.impactSequence++;
#if SERVER
            item.CreateServerEvent(shield);
#else
            shield.AddImpact();
#endif
            return false;
        }
        public override void RemoveComponentSpecific()
        { Shields.Remove(this); base.RemoveComponentSpecific(); }
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client) { }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteUInt16(impactSequence); msg.WriteUInt16(impactSub?.ID ?? 0);
            msg.WriteSingle(impactPoint.X); msg.WriteSingle(impactPoint.Y);
            msg.WriteSingle(impactNormal.X); msg.WriteSingle(impactNormal.Y);
        }
#endif
#if CLIENT
        private readonly List<(Vector2 Point, Vector2 Normal, Submarine Sub, double Born, int Seed)> impacts = new();
        private void AddImpact()
            => impacts.Add((impactPoint, impactNormal, impactSub, Timing.TotalTime, impactSequence));
        internal static bool Visible => Shields.Exists(s => s.impacts.Count > 0);
        internal static void QueueSurfaces()
        {
            foreach (var shield in Shields)
            {
                shield.impacts.RemoveAll(i => Timing.TotalTime - i.Born > 0.75 || i.Sub?.Removed == true);
                var holder = shield.Holder;
                if (holder == null || shield.Battery() == null) continue;
                Vector2 normal = shield.Outward(holder), along = new Vector2(-normal.Y, normal.X);
                Vector2 centre = shield.Item.WorldPosition + normal * (shield.Item.Rect.Width * 0.5f + 3);
                float halfHeight = shield.Item.Rect.Height * 0.5f;
                SonicRefraction.AddMirrorSurface(centre - along * halfHeight, centre + along * halfHeight);
            }
        }
        internal static void DrawImpacts(SpriteBatch batch, Camera camera)
        {
            foreach (var shield in Shields)
                foreach (var impact in shield.impacts)
                {
                    Vector2 point = impact.Point + (impact.Sub?.DrawPosition ?? Vector2.Zero);
                    ChrononStasis.DrawImpactFragments(batch, camera, point - impact.Normal * 300,
                        impact.Normal * 300, (float)(Timing.TotalTime - impact.Born), impact.Seed);
                }
        }
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null) { }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            ushort previous = impactSequence;
            impactSequence = msg.ReadUInt16(); impactSub = Entity.FindEntityByID(msg.ReadUInt16()) as Submarine;
            impactPoint = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            impactNormal = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            if (impactSequence != previous) AddImpact();
        }
#endif
        internal static void Clear() { Shields.Clear(); flights = new(); }
    }
    public sealed class ReflectionShieldPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.reflection.shield");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "DoHitscan"),
                prefix: new HarmonyMethod(typeof(ReflectionShield), nameof(ReflectionShield.BeginRay)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(ReflectionShield), nameof(ReflectionShield.EndRay)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                prefix: new HarmonyMethod(typeof(ReflectionShield), nameof(ReflectionShield.Collision)) { priority = Priority.First });
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); ReflectionShield.Clear(); }
    }
}
