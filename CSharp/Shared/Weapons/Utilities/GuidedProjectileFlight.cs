using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class GuidedProjectileFlight : IAssemblyPlugin
    {
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.guided.projectile.flight");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Projectile), "Update"),
                    transpiler: new HarmonyMethod(typeof(GuidedProjectileFlight), nameof(UpdateTranspiler)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC GuidedFlight] Initialization failed: " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static float FlightThreshold(float original, Projectile projectile)
        {
            var id = projectile.Item.Prefab.Identifier;
            // Below 5 m/s vanilla clears IsBullet and stops OnActive (including guidance).
            // Keep the native impact/removal lifecycle; never reactivate a spent projectile.
            return id == "aic_slow_homing_grenade".ToIdentifier()
                || id == "aic_presight_nano_swarm_projectile".ToIdentifier()
                || id == "aic_gdf_cluster_micro_missile".ToIdentifier() ? 0f : original;
        }

        private static IEnumerable<CodeInstruction> UpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int matches = 0;
            for (int i = 1; i < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_R4 || !(code[i].operand is float value) || value != 25f
                    || !(code[i - 1].operand is System.Reflection.MethodInfo method)
                    || method.Name != "LengthSquared") continue;
                code.InsertRange(i + 1, new[] {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GuidedProjectileFlight), nameof(FlightThreshold)))
                });
                matches++;
                i += 2;
            }
            if (matches != 1) throw new InvalidOperationException("Projectile speed threshold not found uniquely.");
            return code;
        }
    }
}
