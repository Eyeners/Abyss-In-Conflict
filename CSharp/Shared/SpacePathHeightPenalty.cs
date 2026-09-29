using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class SpacePathHeightPenalty : IAssemblyPlugin
    {
        private Harmony harmony;
        [ThreadStatic] private static PathFinder freeFlightFinder;

        public void Initialize()
        {
            harmony = new Harmony("aic.space.path.height.penalty");
            try
            {
                var findPath = typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(m => m.Name == "FindPath" && m.GetParameters().Length > 1
                        && m.GetParameters()[0].ParameterType == typeof(Vector2)
                        && m.GetParameters()[1].ParameterType == typeof(Vector2));
                harmony.Patch(findPath,
                    transpiler: new HarmonyMethod(typeof(SpacePathHeightPenalty), nameof(SkipHeightPenalty)));
                harmony.Patch(AccessTools.Method(typeof(IndoorsSteeringManager), "CalculateSteeringSeek"),
                    prefix: new HarmonyMethod(typeof(SpacePathHeightPenalty), nameof(BeginSearch)),
                    finalizer: new HarmonyMethod(typeof(SpacePathHeightPenalty), nameof(EndSearch)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC SpacePathHeightPenalty] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); freeFlightFinder = null; }

        private static void BeginSearch(IndoorsSteeringManager __instance, out PathFinder __state)
        {
            __state = freeFlightFinder;
            freeFlightFinder = SpaceBackpackAI.Applies(__instance.character) ? __instance.PathFinder : null;
        }
        private static void EndSearch(PathFinder __state) { freeFlightFinder = __state; }

        private static bool UseHeightPenalty(PathFinder finder)
            => finder.InsideSubmarine && !ReferenceEquals(finder, freeFlightFinder);

        private static IEnumerable<CodeInstruction> SkipHeightPenalty(IEnumerable<CodeInstruction> source)
        {
            var getter = AccessTools.PropertyGetter(typeof(PathFinder), nameof(PathFinder.InsideSubmarine));
            var replacement = AccessTools.Method(typeof(SpacePathHeightPenalty), nameof(UseHeightPenalty));
            bool replaced = false;
            foreach (var instruction in source)
            {
                // Only the FIRST InsideSubmarine check guards start-node vertical penalties.
                // Later checks enforce search range and endpoint/outside-node costs and must remain intact.
                if (!replaced && instruction.Calls(getter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                    replaced = true;
                }
                yield return instruction;
            }
            if (!replaced) throw new InvalidOperationException("PathFinder height-penalty guard not found.");
        }
    }
}
