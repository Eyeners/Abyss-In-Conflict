using System;
using System.Collections;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class CloudProjectileFilter : IAssemblyPlugin
    {
        // Attach to the cloud emitter, not the grenade that creates it.
        private static readonly Identifier CloudTag = "aic_emp_shot_filter".ToIdentifier();
        // Explicit opt-in for projectiles whose electrical payload is scripted.
        private static readonly Identifier PayloadTag = "aic_cloud_electrical_payload".ToIdentifier();
        private static readonly Identifier Emp = "emp".ToIdentifier();
        private static readonly Identifier Shock = "electricshock".ToIdentifier();
        private static readonly Identifier Interceptable = "aic_interceptable_projectile".ToIdentifier();
        private static readonly Identifier ElectricalHealth = "aic_electrical_projectile_health".ToIdentifier();
        private Harmony harmony;
        private static FieldInfo fixtureField;
        private static PropertyInfo fixtureProperty;

        public void Initialize()
        {
            harmony = new Harmony("aic.cloud.projectile.filter");
            try
            {
                var type = typeof(Projectile);
                var collision = AccessTools.Method(type, "OnProjectileCollision");
                var handle = AccessTools.Method(type, "HandleProjectileCollision");
                var raycast = AccessTools.Method(type, "DoRayCast");
                if (collision == null || handle == null || raycast == null)
                    throw new MissingMethodException("Projectile collision API changed");
                var hitType = raycast.ReturnType.GetGenericArguments()[0];
                fixtureField = AccessTools.Field(hitType, "Fixture");
                fixtureProperty = AccessTools.Property(hitType, "Fixture");
                if (fixtureField == null && fixtureProperty == null)
                    throw new MissingMemberException("HitscanResult.Fixture");
                harmony.Patch(collision, prefix: new HarmonyMethod(typeof(CloudProjectileFilter), nameof(CollisionPrefix)));
                harmony.Patch(handle, prefix: new HarmonyMethod(typeof(CloudProjectileFilter), nameof(HitPrefix)));
                harmony.Patch(raycast, postfix: new HarmonyMethod(typeof(CloudProjectileFilter), nameof(RaycastPostfix)));
                harmony.Patch(AccessTools.Method(typeof(Item), "AddDamage"),
                    prefix: new HarmonyMethod(typeof(CloudProjectileFilter), nameof(ElectricalDamagePrefix)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC CloudFilter] Initialization failed: " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            fixtureField = null;
            fixtureProperty = null;
        }

        private static bool IsElectrical(Projectile projectile)
        {
            if (projectile.Item.HasTag(PayloadTag)) return true;
            var attack = projectile.Attack;
            if (attack == null) return false;
            // Read the live attack so dynamic ammunition modifications are respected.
            foreach (var affliction in attack.Afflictions.Keys)
            {
                var id = affliction.Prefab.Identifier;
                if (affliction.Strength > 0 && (id == Emp || id == Shock)) return true;
            }
            return false;
        }

        private static bool IsCloud(Fixture fixture)
        {
            return fixture?.Body?.UserData is Item item && item.HasTag(CloudTag);
        }

        private static bool CollisionPrefix(Projectile __instance, Fixture target, ref bool __result)
        {
            bool guided = __instance.Item.HasTag(Interceptable);
            var targetItem = target?.Body?.UserData as Item;
            if ((guided && __instance.Item.Condition <= 0)
                || (targetItem != null && targetItem.HasTag(Interceptable)
                    && (targetItem.Removed || targetItem.Condition <= 0)))
            {
                __result = false;
                return false;
            }
            // A target missile must not treat an incoming bullet as its own impact/detonation.
            // The incoming bullet still runs its separate collision handler and deals damage.
            if (guided && target?.Body?.UserData is Item other
                && other.GetComponent<Projectile>() != null
                && !other.HasTag("aic_homing_decoy".ToIdentifier()))
            {
                __result = false;
                return false;
            }
            if (!IsCloud(target) || IsElectrical(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool HitPrefix(Projectile __instance, Fixture target, ref bool __result)
        {
            return CollisionPrefix(__instance, target, ref __result);
        }

        private static void RaycastPostfix(Projectile __instance, object __result)
        {
            if (!(__result is IList hits)) return;
            bool? electrical = null;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var hit = hits[i];
                var fixture = (fixtureField != null ? fixtureField.GetValue(hit)
                    : fixtureProperty.GetValue(hit)) as Fixture;
                if (!IsCloud(fixture)) continue;
                if (!electrical.HasValue) electrical = IsElectrical(__instance);
                if (!electrical.Value) hits.RemoveAt(i);
            }
        }

        private static bool ElectricalDamagePrefix(Item __instance, Attack attack, float deltaTime, ref AttackResult __result)
        {
            if (!__instance.HasTag(ElectricalHealth)) return true;
            __result = new AttackResult();
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) return false;
            if (__instance.Removed || __instance.Condition <= 0 || __instance.Indestructible
                || __instance.InvulnerableToDamage || attack == null) return false;
            float damage = 0;
            foreach (var affliction in attack.Afflictions.Keys)
            {
                var id = affliction.Prefab.Identifier;
                if (id != Emp && id != Shock) continue;
                if (affliction.Strength <= 0 || Rand.Range(0f, 1f) > affliction.Probability) continue;
                damage += affliction.Strength * (affliction.AffectedByAttackMultipliers ? attack.DamageMultiplier : 1f);
            }
            if (attack.Duration > 0) damage *= deltaTime;
            damage = Math.Max(0f, damage);
            __instance.Condition -= damage;
            __result = new AttackResult(damage, null);
            return false;
        }
    }
}
