using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // A reusable equipment modifier: 0.5 means half the incoming hit probability.
    internal sealed class ProjectileEvasion : ItemComponent
    {
        [Serialize(1f, IsPropertySaveable.No)] public float HitMultiplier { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool TorsoOnly { get; set; }
        public ProjectileEvasion(Item item, ContentXElement element) : base(item, element) { }
    }

    public sealed class EvasionCloak : IAssemblyPlugin
    {
        private sealed class Decision { internal bool Ignore; }
        private sealed class Shot
        {
            internal readonly ConditionalWeakTable<Character, Decision> Targets = new();
        }
        private static ConditionalWeakTable<Projectile, Shot> shots = new();
        private static readonly Identifier EvasionId = "aic_projectile_evasion".ToIdentifier();
        private Harmony harmony;
        internal static float Chance(float speed)
            => float.IsFinite(speed) ? MathHelper.Lerp(0.2f, 0.8f, MathHelper.Clamp(speed / 5f, 0, 1)) : 0.2f;

        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.evasion.cloak");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Projectile), "ShouldIgnoreCharacterCollision"),
                    postfix: new HarmonyMethod(typeof(EvasionCloak), nameof(IgnoreCollision)));
                harmony.Patch(AccessTools.Method(typeof(Projectile), "Launch"),
                    prefix: new HarmonyMethod(typeof(EvasionCloak), nameof(NewShot)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC EvasionCloak] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; shots = new(); }
        private static void NewShot(Projectile __instance) => shots.Remove(__instance);
        private static void IgnoreCollision(Projectile __instance, Character character, ref bool __result)
        {
            // Multiplayer damage decisions belong to the server; no client-side rerolls.
            if (__result || GameMain.NetworkMember?.IsClient == true || character == null || character.Removed) return;
            var shot = shots.GetValue(__instance, _ => new Shot());
            if (ChrononPack.Equipped(character)?.Dashing == true)
            {
                shot.Targets.GetValue(character, _ => new Decision()).Ignore = true;
                __result = true;
                return;
            }
            if (shot.Targets.TryGetValue(character, out var cached))
            {
                __result = cached.Ignore;
                return;
            }
            var cloak = character.GetEquippedItem("aic_evasion_cloak".ToIdentifier(), InvSlotType.Bag);
            var attacker = __instance.User ?? __instance.Attacker;
            float hitChance = 1 - MathHelper.Clamp((attacker?.CharacterHealth.GetAfflictionStrengthByIdentifier(EvasionId) ?? 0) * 0.02f, 0, 0.6f);
            if (cloak != null && !cloak.Removed && cloak.Condition > 0 && character.AnimController?.Collider != null)
                hitChance *= 1 - Chance(character.AnimController.Collider.LinearVelocity.Length());
            hitChance *= 1 - (FoehnCharge.Equipped(character)?.EvasionChance(character) ?? 0);
            var chronon = ChrononPack.Equipped(character);
            // HandleProjectileCollision sets lastTarget before this filter. Keep the
            // passive dodge torso-only, then cache it for the entire projectile/target.
            bool chrononEligible = __instance.lastTarget?.Body.UserData is Limb hit
                && hit.character == character && hit.type == LimbType.Torso
                && chronon?.CanEvadeTorso(character) == true;
            if (chrononEligible) hitChance *= 0.5f;
            if (character.Inventory != null)
                foreach (var item in character.Inventory.AllItems)
                {
                    var modifier = item.GetComponent<ProjectileEvasion>();
                    if (modifier == null || item.Removed || item.Condition <= 0 || !character.HasEquippedItem(item)) continue;
                    if (modifier.TorsoOnly && !(__instance.lastTarget?.Body.UserData is Limb limb
                        && limb.character == character && limb.type == LimbType.Torso)) continue;
                    hitChance *= MathHelper.Clamp(modifier.HitMultiplier, 0, 1);
                }
            // One roll for all sources; cache the result for this projectile/character.
            float chance = 1 - MathHelper.Clamp(hitChance, 0, 1);
            bool ignore = !character.IsDead && chance > 0 && Rand.Range(0f, 1f) < chance;
            shot.Targets.Add(character, new Decision { Ignore = ignore });
            if (ignore && chronon != null && chronon.Item.Condition > 0 && !chronon.Dashing)
                chronon.ShowEvasion(character);
            __result = ignore;
        }
    }
}
