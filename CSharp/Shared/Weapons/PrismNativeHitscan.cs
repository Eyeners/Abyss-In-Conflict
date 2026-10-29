using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal sealed class BatteryHitscanWeapon : RangedWeapon, IServerSerializable
    {
        [Serialize(40f, IsPropertySaveable.No)] public float BatteryCost { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string ProjectileIdentifier { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string AlternateProjectile { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string AlternateAttachmentTag { get; set; }
#if CLIENT
        private static readonly Action<RangedWeapon, float> SetReloadTimer =
            AccessTools.MethodDelegate<Action<RangedWeapon, float>>(AccessTools.PropertySetter(typeof(RangedWeapon), "ReloadTimer"));
#endif
        public BatteryHitscanWeapon(Item item, ContentXElement element) : base(item, element) { }
        public override bool Use(float dt, Character character = null)
        {
            if (GameMain.NetworkMember?.IsClient == true || character == null || character.Removed || Entity.Spawner == null) return false;
            string projectileId = ProjectileIdentifier;
            if (!string.IsNullOrEmpty(AlternateAttachmentTag) && Item.OwnInventory != null)
                foreach (var attachment in Item.OwnInventory.AllItems)
                    if (attachment.HasTag(AlternateAttachmentTag.ToIdentifier())) { projectileId = AlternateProjectile; break; }
            var prefab = ItemPrefab.Find(null, projectileId.ToIdentifier());
            if (prefab == null || Item.OwnInventory == null) return false;
            Item battery = null;
            foreach (var cell in Item.OwnInventory.AllItems)
                if (!cell.Removed && cell.HasTag("mobilebattery".ToIdentifier()) && cell.Condition >= Math.Max(0, BatteryCost)) { battery = cell; break; }
            if (battery == null || !base.Use(dt, character)) return false;
            battery.Condition = Math.Max(0, battery.Condition - Math.Max(0, BatteryCost));
            var sub = Item.Submarine;
            var muzzle = Item.SimPosition + TransformedBarrelPos;
            var aimSource = character.AnimController.AimSourceSimPos;
            float angle = Item.body.Rotation + (Item.body.Dir < 0 ? MathHelper.Pi : 0);
            float multiplier = (1 + Item.GetQualityModifier(Quality.StatType.FirepowerMultiplier)
                + character.GetStatValue(StatTypes.RangedAttackMultiplier)) * WeaponDamageModifier;
            var ignored = new List<Body>();
            foreach (var limb in character.AnimController.Limbs)
                if (limb.body != null && !limb.IsSevered) ignored.Add(limb.body.FarseerBody);
            var session = GameMain.GameSession;
            Entity.Spawner.AddItemToSpawnQueue(prefab, FarseerPhysics.ConvertUnits.ToDisplayUnits(muzzle), sub, onSpawned: spawned =>
            {
                if (spawned.Removed) return;
                if (GameMain.GameSession != session || Item.Removed || character.Removed || sub?.Removed == true)
                { Entity.Spawner.AddItemToRemoveQueue(spawned); return; }
                var projectile = spawned.GetComponent<Projectile>();
                projectile.Launcher = Item;
                projectile.Attacker = character;
                projectile.Shoot(character, aimSource, muzzle, angle, ignored, true, multiplier, 0);
            });
#if SERVER
            Item.CreateServerEvent(this);
#endif
            return true;
        }
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        { PlaySound(ActionType.OnUse); SetReloadTimer(this, Reload); IsActive = true; }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null) { }
#endif
    }

    internal sealed class PrismSplit : ItemComponent
    {
        [Serialize("", IsPropertySaveable.No)] public string ChildProjectile { get; set; }
        [Serialize(3, IsPropertySaveable.No)] public int BranchCount { get; set; }
        [Serialize(70f, IsPropertySaveable.No)] public float SurfaceSpread { get; set; }
        [Serialize(80f, IsPropertySaveable.No)] public float SurfaceAngleLimit { get; set; }
        [Serialize(true, IsPropertySaveable.No)] public bool IgnoreShooter { get; set; }
        [Serialize(true, IsPropertySaveable.No)] public bool IgnoreHitCharacter { get; set; }
        private bool emitted;
        public PrismSplit(Item item, ContentXElement element) : base(item, element) { }
        internal void Emit(Projectile source, Fixture target, Vector2 normal)
        {
            if (emitted || GameMain.NetworkMember?.IsClient == true || Entity.Spawner == null) return;
            var prefab = ItemPrefab.Find(null, ChildProjectile.ToIdentifier());
            if (prefab == null) return;
            emitted = true;
            Character originCharacter = (target.Body.UserData as Limb)?.character;
            bool surface = originCharacter == null;
            float angle = Item.body.Rotation;
            Vector2 incoming = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            if (normal.LengthSquared() < 0.001f) normal = -incoming;
            else normal.Normalize();
            if (Vector2.Dot(normal, incoming) > 0) normal = -normal;
            var user = source.User ?? source.Attacker;
            var launcher = source.Launcher;
            bool focused = false;
            if (launcher != null)
                foreach (var container in launcher.GetComponents<ItemContainer>())
                    foreach (var attachment in container.Inventory.AllItems)
                        if (attachment.HasTag("aic_prism_focus".ToIdentifier())) focused = true;
            float multiplier = source.Attack?.DamageMultiplier ?? 1;
            var sub = Item.Submarine;
            Vector2 point = Item.Position;
            var session = GameMain.GameSession;
            var ignored = new List<Body>();
            // Exclusions are explicitly configurable for each projectile.
            foreach (var character in new[] { IgnoreHitCharacter ? originCharacter : null, IgnoreShooter ? user : null })
                if (character?.AnimController != null)
                    foreach (var limb in character.AnimController.Limbs)
                        if (limb.body != null) ignored.Add(limb.body.FarseerBody);
            int count = Math.Max(0, BranchCount);
            for (int i = 0; i < count; i++)
            {
                Vector2 direction;
                if (focused)
                {
                    Vector2 basis = surface ? incoming - 2 * Vector2.Dot(incoming, normal) * normal : incoming;
                    float baseAngle = (float)Math.Atan2(basis.Y, basis.X);
                    float halfAngle = MathHelper.ToRadians(surface ? 15f : 22.5f);
                    float low = -halfAngle, high = halfAngle;
                    if (surface)
                    {
                        float relative = MathHelper.WrapAngle(baseAngle - (float)Math.Atan2(normal.Y, normal.X));
                        low = Math.Max(low, -MathHelper.PiOver2 - relative);
                        high = Math.Min(high, MathHelper.PiOver2 - relative);
                    }
                    float focusedAngle = baseAngle + Rand.Range(low, high);
                    direction = new Vector2((float)Math.Cos(focusedAngle), (float)Math.Sin(focusedAngle));
                }
                else
                    direction = PrismGeometry.Branch(incoming, normal, surface, i, count, Rand.Range(0f, 1f), SurfaceSpread, SurfaceAngleLimit);
                float heading = (float)Math.Atan2(direction.Y, direction.X);
                Vector2 position = point + (surface ? normal : direction) * 2;
                Entity.Spawner.AddItemToSpawnQueue(prefab, position, sub, onSpawned: spawned =>
                {
                    if (spawned.Removed) return;
                    if (GameMain.GameSession != session || sub?.Removed == true)
                    { Entity.Spawner.AddItemToRemoveQueue(spawned); return; }
                    var projectile = spawned.GetComponent<Projectile>();
                    projectile.Launcher = launcher?.Removed == false ? launcher : null;
                    projectile.Attacker = user?.Removed == false ? user : null;
                    projectile.Shoot(projectile.Attacker, spawned.SimPosition, spawned.SimPosition, heading, ignored, true, multiplier, 0);
                });
            }
        }
    }

    public sealed class PrismSplitSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.prism.native.split");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                postfix: new HarmonyMethod(typeof(PrismSplitSystem), nameof(AfterImpact)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }
        private static void AfterImpact(Projectile __instance, Fixture target, Vector2 collisionNormal, bool __result)
        {
            if (__result && GameMain.NetworkMember?.IsClient != true)
                __instance.Item.GetComponent<PrismSplit>()?.Emit(__instance, target, collisionNormal);
        }
    }
}
