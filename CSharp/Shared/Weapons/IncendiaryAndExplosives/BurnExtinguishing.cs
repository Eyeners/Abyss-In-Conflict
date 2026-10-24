using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class BurnExtinguishing : IAssemblyPlugin
    {
        private static readonly Identifier BurnTag = "aic_extinguishable_burn".ToIdentifier();
        private static readonly HashSet<Item> burns = new HashSet<Item>();
        private const float SprayRadius = 100f;
        private const float SurfaceTolerance = 10f;
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.burn.extinguishing");
            try
            {
                burns.Clear();
                foreach (var constructor in typeof(Item).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(BurnExtinguishing), nameof(Register)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Remove), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(BurnExtinguishing), nameof(Unregister)));
                var repair = AccessTools.Method(typeof(RepairTool), "Repair");
                if (repair == null) throw new MissingMethodException("RepairTool.Repair");
                harmony.Patch(repair, transpiler: new HarmonyMethod(typeof(BurnExtinguishing), nameof(Inject)));
                foreach (var item in Item.ItemList) Register(item);
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                burns.Clear();
                LuaCsLogger.LogError("[AIC BurnExtinguishing] " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); burns.Clear(); }

        private static void Register(Item __instance)
        {
            if (GameMain.NetworkMember?.IsClient != true && !__instance.Removed && __instance.HasTag(BurnTag))
                burns.Add(__instance);
        }

        private static void Unregister(Item __instance) => burns.Remove(__instance);

        private static IEnumerable<CodeInstruction> Inject(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            MethodInfo amount = AccessTools.PropertyGetter(typeof(RepairTool), nameof(RepairTool.ExtinguishAmount));
            MethodInfo fraction = AccessTools.PropertyGetter(typeof(Submarine), "LastPickedFraction");
            int index = code.FindIndex(c => c.Calls(amount));
            if (index < 1 || code[index - 1].opcode != OpCodes.Ldarg_0)
                throw new InvalidOperationException("Cannot locate the native extinguishing block.");

            // Read the same clipped ray fraction as vanilla, including RepairMultiple tools.
            // Do not use the global LastPickedFraction after Repair: later raycasts overwrite it.
            CodeInstruction loadFraction = null;
            for (int i = 0; i < index - 1; i++)
            {
                if (!code[i].Calls(fraction)) continue;
                var store = code[i + 1];
                if (store.opcode == OpCodes.Stloc_0) loadFraction = new CodeInstruction(OpCodes.Ldloc_0);
                else if (store.opcode == OpCodes.Stloc_1) loadFraction = new CodeInstruction(OpCodes.Ldloc_1);
                else if (store.opcode == OpCodes.Stloc_2) loadFraction = new CodeInstruction(OpCodes.Ldloc_2);
                else if (store.opcode == OpCodes.Stloc_3) loadFraction = new CodeInstruction(OpCodes.Ldloc_3);
                else if (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S)
                    loadFraction = new CodeInstruction(OpCodes.Ldloc, store.operand);
            }
            if (loadFraction == null) throw new InvalidOperationException("Cannot locate the native spray range local.");
            var added = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                loadFraction,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BurnExtinguishing), nameof(Extinguish)))
            };
            added[0].labels.AddRange(code[index - 1].labels);
            code[index - 1].labels.Clear();
            code.InsertRange(index - 1, added);
            return code;
        }

        private static void Extinguish(RepairTool tool, Vector2 rayStart, Vector2 rayEnd, float deltaTime, float endFraction)
        {
            if (GameMain.NetworkMember?.IsClient == true || Entity.Spawner == null
                || !(tool.ExtinguishAmount > 0) || !(deltaTime > 0) || !float.IsFinite(deltaTime)
                || tool.Item.Removed || tool.Item.CurrentHull == null || !float.IsFinite(endFraction)
                || burns.Count == 0) return;
            var sub = tool.Item.CurrentHull.Submarine;
            Vector2 offset = sub?.Position ?? Vector2.Zero;
            Vector2 start = ConvertUnits.ToDisplayUnits(rayStart) + offset;
            Vector2 end = ConvertUnits.ToDisplayUnits(Vector2.Lerp(rayStart, rayEnd, MathHelper.Clamp(endFraction, 0, 1))) + offset;
            // Condition changes can remove items through status effects. Snapshot only the
            // registered burns so removal callbacks cannot invalidate this enumeration.
            var snapshot = ScratchLists<Item>.Rent();
            try
            {
                snapshot.AddRange(burns);
                foreach (var burn in snapshot)
                {
                    if (burn.Removed || burn.Condition <= 0 || burn.ParentInventory != null
                        || burn.Submarine != sub || !burn.HasTag(BurnTag)) continue;
                    Vector2 target = burn.WorldPosition;
                    if (!TryGetSprayPoint(start, end, target, out Vector2 closest)) continue;
                    var hull = Hull.FindHull(closest, tool.Item.CurrentHull, useWorldCoordinates: true);
                    // A droplet embedded in a wall may have no CurrentHull; visibility still protects the far side.
                    if (hull == null) continue;

                    if (!tool.RepairThroughWalls && Vector2.DistanceSquared(target, closest) > SurfaceTolerance * SurfaceTolerance)
                    {
                        var obstruction = Submarine.CheckVisibility(
                            ConvertUnits.ToSimUnits(closest - offset), ConvertUnits.ToSimUnits(target - offset),
                            ignoreLevel: sub != null, ignoreSubs: true,
                            blocksVisibilityPredicate: fixture => fixture.Body.UserData is Entity entity
                                ? entity != burn && entity.Submarine == sub : sub == null);
                        // Attached droplets can sit a few pixels inside the surface they hit.
                        if (obstruction != null && Vector2.DistanceSquared(
                            ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition) + offset, target)
                            > SurfaceTolerance * SurfaceTolerance) continue;
                    }

                    burn.Condition = Math.Max(0, burn.Condition - tool.ExtinguishAmount * deltaTime);
                    if (burn.Condition <= 0) Entity.Spawner.AddItemToRemoveQueue(burn);
                }
            }
            finally { ScratchLists<Item>.Return(snapshot); }
        }

        private static bool TryGetSprayPoint(Vector2 start, Vector2 end, Vector2 target, out Vector2 closest)
        {
            Vector2 direction = end - start;
            float lengthSquared = direction.LengthSquared();
            float along = lengthSquared > 0.0001f ? Vector2.Dot(target - start, direction) / lengthSquared : 0;
            closest = start + direction * MathHelper.Clamp(along, 0, 1);
            // Only a small surface allowance beyond the endpoint, never a full spray radius behind walls.
            if (along < 0 || (along > 1 && Vector2.DistanceSquared(target, end) > SurfaceTolerance * SurfaceTolerance)) return false;
            return Vector2.DistanceSquared(target, closest) <= SprayRadius * SprayRadius;
        }
    }
}
