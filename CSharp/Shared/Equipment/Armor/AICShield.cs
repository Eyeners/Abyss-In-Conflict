using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    // Damage interception only: visual/audio hooks run after an authoritative block.
    class AICShield : ItemComponent
    {
        [Serialize("", IsPropertySaveable.No)] public string EnabledTag { get; set; }
        [Serialize(2, IsPropertySaveable.No)] public int BatterySlot { get; set; }
        [Serialize("mobilebattery", IsPropertySaveable.No)] public string BatteryTag { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float BurnWeight { get; set; }
        [Serialize(1f, IsPropertySaveable.No)] public float DrainMultiplier { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float MinimumDamage { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float MinimumPenetration { get; set; }
        [Serialize(-1f, IsPropertySaveable.No)] public float FixedDrain { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool OncePerProjectile { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string Feedback { get; set; }
        private readonly HashSet<LimbType> protectedLimbs = new();

        public AICShield(Item item, ContentXElement element) : base(item, element)
        {
            foreach (var name in element.GetAttributeString("protectedlimbs",
                "Head,Torso,Waist,LeftArm,LeftForearm,LeftHand,RightArm,RightForearm,RightHand,LeftThigh,RightThigh,LeftLeg,RightLeg,LeftFoot,RightFoot").Split(','))
                if (Enum.TryParse(name.Trim(), true, out LimbType limb)) protectedLimbs.Add(limb);
            // No Update loop: equipment and battery are checked only when damage arrives.
            IsActive = false;
        }

        internal bool TryBlock(Character wearer, Limb limb, IEnumerable<Affliction> afflictions, float penetration)
        {
            if (!protectedLimbs.Contains(limb.type) || !Item.HasTag(EnabledTag)) return false;
            var payments = OncePerProjectile ? AICShieldSystem.ActivePayments : null;
            bool paid = payments?.Contains(this) == true;
            var battery = Item.OwnInventory?.GetItemAt(BatterySlot);
            bool powered = battery != null && battery.HasTag(BatteryTag) && battery.Condition > 0;
            if (!paid && !powered) return false;
            float damage = 0;
            foreach (var affliction in afflictions)
            {
                var prefab = affliction.Prefab;
                if (prefab.AfflictionType == "damage".ToIdentifier() && prefab.Identifier != "organdamage".ToIdentifier())
                    damage += Math.Max(0, affliction.Strength);
                else if (prefab.AfflictionType == "burn".ToIdentifier())
                    damage += Math.Max(0, affliction.Strength) * BurnWeight;
            }
            penetration = Math.Max(0, penetration);
            if (damage <= 0 || damage < MinimumDamage || penetration < MinimumPenetration) return false;
            if (!paid)
            {
                payments?.Add(this);
                float previous = battery.Condition;
                float drain = FixedDrain >= 0 ? FixedDrain : damage * penetration * DrainMultiplier;
                battery.Condition = Math.Max(0, previous - Math.Max(0, drain));
                // Cosmetics must never break authoritative damage interception.
                try
                {
                    Item.GetComponent<NanoShield>()?.HitFlash();
                    LuaCsSetup.Instance.Hook.Call("aic_shield_blocked", Item, wearer, Feedback, previous > 0 && battery.Condition <= 0);
                }
                catch (Exception error) { LuaCsLogger.LogError("[AIC Shield feedback] " + error); }
            }
            // Preserve legacy whole-hit blocking, including associated stun/impulse.
            return true;
        }
    }

    public sealed class AICShieldSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        private static ConditionalWeakTable<Projectile, HashSet<AICShield>> payments = new();
        private static Projectile activeProjectile;
        internal static HashSet<AICShield> ActivePayments => activeProjectile == null ? null : payments.GetOrCreateValue(activeProjectile);
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.armor.shields");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.DamageLimb)),
                    prefix: new HarmonyMethod(typeof(AICShieldSystem), nameof(BeforeDamage)) { priority = Priority.First });
                harmony.Patch(AccessTools.Method(typeof(Projectile), "Launch"),
                    prefix: new HarmonyMethod(typeof(AICShieldSystem), nameof(NewLaunch)));
                harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                    prefix: new HarmonyMethod(typeof(AICShieldSystem), nameof(BeginImpact)),
                    finalizer: new HarmonyMethod(typeof(AICShieldSystem), nameof(EndImpact)));
            }
            catch
            {
                harmony.UnpatchSelf(); harmony = null;
                throw;
            }
        }
        private static bool BeforeDamage(Character __instance, Limb hitLimb, IEnumerable<Affliction> afflictions,
            float penetration, ref AttackResult __result)
        {
            if (GameMain.NetworkMember?.IsClient == true || __instance.Removed || hitLimb == null || afflictions == null) return true;
            var armor = __instance.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes);
            var shield = armor?.GetComponent<AICShield>();
            if (shield == null || !shield.TryBlock(__instance, hitLimb, afflictions, penetration)) return true;
            __result = default;
            return false;
        }
        private static void NewLaunch(Projectile __instance) => payments.Remove(__instance);
        private static void BeginImpact(Projectile __instance, out Projectile __state)
        {
            __state = activeProjectile;
            activeProjectile = __instance;
        }
        private static void EndImpact(Projectile __state) => activeProjectile = __state;
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf(); harmony = null;
            payments = new(); activeProjectile = null;
        }
    }
}
