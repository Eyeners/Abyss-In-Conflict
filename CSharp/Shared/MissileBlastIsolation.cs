using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class MissileBlastIsolation : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly Identifier Missile = "aic_gdf_cluster_micro_missile".ToIdentifier();
        private static readonly Identifier Guided = "aic_interceptable_projectile".ToIdentifier();

        public void Initialize()
        {
            harmony = new Harmony("aic.missile.blast.isolation");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Explosion), "Explode"),
                    transpiler: new HarmonyMethod(typeof(MissileBlastIsolation), nameof(Transpile)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC MissileBlastIsolation] Initialization failed: " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static bool CanTakeBlast(ItemPrefab prefab, Entity source)
        {
            // Decoys and other explosives retain normal damage; sibling guided rounds do not.
            // Do not mutate shared prefabs: explosions may recursively trigger other explosions.
            if (prefab.Identifier == Missile && source is Item item && item.HasTag(Guided)) return false;
            return prefab.DamagedByExplosions;
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var getter = AccessTools.PropertyGetter(typeof(ItemPrefab), "DamagedByExplosions");
            int matches = 0;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(getter)) continue;
                // Explode(worldPosition, damageSource, attacker): damageSource is argument 2.
                code[i].opcode = OpCodes.Ldarg_2;
                code[i].operand = null;
                code.Insert(++i, new CodeInstruction(OpCodes.Call,
                    AccessTools.Method(typeof(MissileBlastIsolation), nameof(CanTakeBlast))));
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("Explosion item damage check changed.");
            return code;
        }
    }
}
