using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
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
            if (shot.Targets.TryGetValue(character, out var cached))
            {
                __result = cached.Ignore;
                return;
            }
            var cloak = character.GetEquippedItem("aic_evasion_cloak", InvSlotType.Bag);
            var attacker = __instance.User ?? __instance.Attacker;
            float chance = MathHelper.Clamp((attacker?.CharacterHealth.GetAfflictionStrengthByIdentifier(EvasionId) ?? 0) * 0.02f, 0, 0.6f);
            if (cloak != null && !cloak.Removed && cloak.Condition > 0 && character.AnimController?.Collider != null)
                chance = Math.Max(chance, Chance(character.AnimController.Collider.LinearVelocity.Length()));
            chance = Math.Max(chance, FoehnCharge.Equipped(character)?.EvasionChance(character) ?? 0);
            // Multiple sources share one roll; use the strongest rather than stacking rerolls.
            bool ignore = !character.IsDead && chance > 0 && Rand.Range(0f, 1f) < chance;
            shot.Targets.Add(character, new Decision { Ignore = ignore });
            __result = ignore;
        }
    }
}
