using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class RedStarModeGate : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly Identifier Rifle = "aic_gdf_redstar_plasma_rifle".ToIdentifier();
        public void Initialize()
        {
            harmony = new Harmony("aic.redstar.mode.before.shot");
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "Use"),
                prefix: new HarmonyMethod(typeof(RedStarModeGate), nameof(BeforeShot)));
        }
        private static bool BeforeShot(RangedWeapon __instance, ref bool __result)
        {
            var item = __instance.Item;
            if (item.Prefab.Identifier != Rifle) return true;
            foreach (var container in item.GetComponents<ItemContainer>())
            {
                if (container.Inventory == null) continue;
                foreach (var token in container.Inventory.AllItems)
                {
                    if (token.Prefab.Identifier != "aic_gdf_redstar_mode_token".ToIdentifier()) continue;
                    bool beam = token.HasTag("aic_gdf_redstar_mode_beam".ToIdentifier());
                    if (!beam && item.ConditionPercentage < 99) return true;
                    // Do not refill charge here: remaining beam energy belongs to the XML cycle.
                    if (!beam) token.Tags = "aic_gdf_redstar_mode_token,aic_gdf_redstar_mode_beam";
                    __result = false;
                    return false; // The RepairTool on this item handles the charged attack.
                }
            }
            // A newly spawned weapon may still be waiting for its hidden mode token.
            if (item.ConditionPercentage >= 99) { __result = false; return false; }
            return true;
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() => harmony?.UnpatchSelf();
    }
}
