using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace AIC.Components
{
    partial class AICCloneChamber
    {
        internal static readonly Dictionary<Character, AICCloneChamber> StasisOccupants = new();
        private Character stasisOccupant;

        private void RefreshStasisOccupant()
        {
            var occupant = !Item.Removed && IsDeployed && Item.Condition > 0 ? Seat?.User : null;
            if (occupant == stasisOccupant) return;
            if (stasisOccupant != null && StasisOccupants.TryGetValue(stasisOccupant, out var old) && old == this)
                StasisOccupants.Remove(stasisOccupant);
            stasisOccupant = occupant;
            if (occupant != null) StasisOccupants[occupant] = this;
        }

        private static void OnStasisSeatChanged(Barotrauma.Items.Components.Controller __instance)
            => __instance.Item.GetComponent<AICCloneChamber>()?.RefreshStasisOccupant();

        private static readonly HashSet<Identifier> StasisTypes = new()
        {
            "alieninfection".ToIdentifier(), "infection".ToIdentifier(),
            "bleeding".ToIdentifier(), "poison".ToIdentifier(), "paralysis".ToIdentifier()
        };

        internal static void PatchStasis(Harmony harmony)
        {
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(CharacterHealth), "UpdateBleedingProjSpecific"),
                prefix: new HarmonyMethod(typeof(AICCloneChamber), nameof(AllowBleedingParticles)));
#endif
            harmony.Patch(AccessTools.Method(typeof(Barotrauma.Items.Components.Controller), "Select", new[] { typeof(Character) }),
                postfix: new HarmonyMethod(typeof(AICCloneChamber), nameof(OnStasisSeatChanged)));
            // Derived afflictions (notably husk and bleeding) do work outside base.Update.
            // Patch each native override as well as the base method.
            var signature = new[] { typeof(CharacterHealth), typeof(Limb), typeof(float) };
            foreach (var type in typeof(Affliction).Assembly.GetTypes())
            {
                if (!typeof(Affliction).IsAssignableFrom(type)) continue;
                var update = type.GetMethod("Update",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, signature, null);
                if (update == null || update.IsAbstract) continue;
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(AICCloneChamber), nameof(AllowAfflictionUpdate)));
            }
        }

        private static bool AllowAfflictionUpdate(Affliction __instance, CharacterHealth __0)
        {
            if (StasisOccupants.Count == 0) return true;
            if (!StasisTypes.Contains(__instance.Prefab.AfflictionType)) return true;
            return !IsInStasis(__0?.Character);
        }

#if CLIENT
        private static bool AllowBleedingParticles(CharacterHealth __instance)
            => !IsInStasis(__instance.Character);
#endif

        private static bool IsInStasis(Character character)
        {
            if (StasisOccupants.Count == 0 || !Alive(character)) return false;
            if (!StasisOccupants.TryGetValue(character, out var chamber)) return false;
            // Verify the actual seat user: selection alone must never grant protection.
            return chamber != null && !chamber.Item.Removed && chamber.IsDeployed
                && chamber.Item.Condition > 0 && chamber.Seat?.User == character;
        }
    }
}
