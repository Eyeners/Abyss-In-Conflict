using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class RecallBackpackReloadAI : IAssemblyPlugin
    {
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.recall.backpack.reload");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveCombat), "GetWeaponPriority"),
                    prefix: new HarmonyMethod(typeof(RecallBackpackReloadAI), nameof(ConsiderCarriedAmmo)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC RecallBackpackReloadAI] " + error);
            }
        }

        private static void ConsiderCarriedAmmo(AIObjectiveCombat __instance, ItemComponent weapon, ref bool canSeekAmmo)
        {
            if (canSeekAmmo || GameMain.NetworkMember?.IsClient == true || !(weapon is RangedWeapon)) return;
            var user = __instance.character;
            if (user == null || user.Removed || user.IsDead || user.IsIncapacitated || user.LockHands
                || user.Inventory == null || weapon.Item.Removed || !weapon.Item.IsOwnedBy(user)
                || !weapon.Item.IsInteractable(user) || !weapon.IsEmpty(user)) return;
            var backpack = user.GetEquippedItem("aic_recall_backpack", InvSlotType.Bag);
            if (backpack == null || backpack.Condition <= 0) return;
            canSeekAmmo = HasCarriedAmmo(user, weapon);
        }

        internal static bool HasCarriedAmmo(Character user, ItemComponent weapon)
        {
            if (!weapon.RequiredItems.TryGetValue(RelatedItem.RelationType.Contained, out var requirements)) return false;
            foreach (var requirement in requirements)
            {
                if (weapon.Item.OwnInventory == null) return false;
                bool satisfied = false;
                foreach (var contained in weapon.Item.OwnInventory.AllItems)
                    if (contained.Condition > 0 && requirement.MatchesItem(contained)) { satisfied = true; break; }
                if (satisfied) continue;
                var ammunition = user.Inventory.FindItem(item => !item.Removed && item.Condition > 0
                    && item.HasIdentifierOrTags(requirement.Identifiers) && item.IsInteractable(user)
                    && !(item.ParentInventory?.Owner is Item container && container.HasTag(Tags.MobileRadio)), recursive: true);
                // This flag only affects weapon scoring. Native Reload still handles transfer,
                // reload time and the separate permission to seek supplies outside inventory.
                return ammunition != null;
            }
            return false;
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }
    }
}
