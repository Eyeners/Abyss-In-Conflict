using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Share the native center-distance broad phase only. Effects still own their
    // interval, probability, conditions, resistances and affliction application.
    public sealed class NaniteCloudTargets : IAssemblyPlugin
    {
        private sealed class OptIn { }
        private static ConditionalWeakTable<StatusEffect, OptIn> effects = new();
        private static readonly Dictionary<(int, int), List<(Character character, int order)>> cells = new();
        private static readonly Stack<List<(Character character, int order)>> pool = new();
        private static readonly Dictionary<(Vector2, float), List<Character>> queries = new();
        private static readonly List<List<Character>> retired = new();
        private static int readers;
        private static double builtAt = double.NaN;
        private static int characterCount;
        private const float CellSize = 200;
        private Harmony harmony;

        public void PreInitPatching() => Initialize();
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.nanite.cloud.targets");
            try
            {
                harmony.Patch(AccessTools.Constructor(typeof(StatusEffect), new[] { typeof(ContentXElement), typeof(string) }),
                    postfix: new HarmonyMethod(typeof(NaniteCloudTargets), nameof(Register)));
                harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.AddNearbyTargets)),
                    prefix: new HarmonyMethod(typeof(NaniteCloudTargets), nameof(AddTargets)));
                harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.TeleportTo)),
                    postfix: new HarmonyMethod(typeof(NaniteCloudTargets), nameof(Invalidate)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    postfix: new HarmonyMethod(typeof(NaniteCloudTargets), nameof(ClearCaches)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC NaniteCloudTargets] " + error);
            }
        }
        public void OnLoadCompleted() { }
        private static void Invalidate() => builtAt = double.NaN;
        public void Dispose()
        {
            harmony?.UnpatchSelf(); harmony = null;
            ClearCaches();
            effects = new(); pool.Clear();
            builtAt = double.NaN;
        }
        private static void ClearCaches()
        {
            ClearQueries();
            foreach (var entries in cells.Values) { entries.Clear(); pool.Push(entries); }
            cells.Clear(); characterCount = 0;
        }
        private static void ClearQueries()
        {
            foreach (var list in queries.Values)
            {
                if (readers == 0) ScratchLists<Character>.Return(list);
                else retired.Add(list);
            }
            queries.Clear();
            builtAt = double.NaN;
        }
        private static void ReturnRetired()
        {
            if (readers != 0) return;
            foreach (var list in retired) ScratchLists<Character>.Return(list);
            retired.Clear();
        }
        private static void Register(StatusEffect __instance, ContentXElement element)
        {
            if (element.GetAttributeBool("aicsharednanitequery", false))
                effects.GetValue(__instance, _ => new OptIn());
        }
        private static int Cell(float value) => (int)Math.Floor(value / CellSize);
        private static bool AddTargets(StatusEffect __instance, Vector2 worldPosition, List<ISerializableEntity> targets)
        {
            float range = __instance.Range;
            if (!effects.TryGetValue(__instance, out _) || !float.IsFinite(range) || range <= 0 || range > 2000
                || !float.IsFinite(worldPosition.X) || !float.IsFinite(worldPosition.Y)
                || !__instance.HasTargetType(StatusEffect.TargetType.NearbyCharacters)
                || __instance.HasTargetType(StatusEffect.TargetType.NearbyItems)) return true;
            if (builtAt != Timing.TotalTime || characterCount != Character.CharacterList.Count) Rebuild();
            var key = (worldPosition, range);
            if (!queries.TryGetValue(key, out var nearby))
            {
                var selected = ScratchLists<(Character character, int order)>.Rent();
                nearby = ScratchLists<Character>.Rent();
                bool committed = false;
                try
                {
                    for (int x = Cell(worldPosition.X - range); x <= Cell(worldPosition.X + range); x++)
                    for (int y = Cell(worldPosition.Y - range); y <= Cell(worldPosition.Y + range); y++)
                        if (cells.TryGetValue((x, y), out var entries))
                            foreach (var entry in entries)
                                if (Vector2.DistanceSquared(entry.character.WorldPosition, worldPosition) < range * range)
                                    selected.Add(entry);
                    selected.Sort(static (a, b) => a.order.CompareTo(b.order));
                    foreach (var entry in selected) nearby.Add(entry.character);
                    queries.Add(key, nearby);
                    committed = true;
                }
                finally
                {
                    ScratchLists<(Character character, int order)>.Return(selected);
                    if (!committed) ScratchLists<Character>.Return(nearby);
                }
            }
            readers++;
            try
            {
                foreach (var character in nearby)
                    if (character.Enabled && !character.Removed
                        && Vector2.DistanceSquared(character.WorldPosition, worldPosition) < range * range
                        && __instance.IsValidTarget(character)) targets.Add(character);
            }
            finally { readers--; ReturnRetired(); }
            return false;
        }
        private static void Rebuild()
        {
            foreach (var entries in cells.Values) { entries.Clear(); pool.Push(entries); }
            cells.Clear(); ClearQueries();
            int order = 0;
            foreach (var character in Character.CharacterList)
            {
                if (!character.Removed)
                {
                    var key = (Cell(character.WorldPosition.X), Cell(character.WorldPosition.Y));
                    if (!cells.TryGetValue(key, out var entries))
                        cells[key] = entries = pool.Count > 0 ? pool.Pop() : new();
                    entries.Add((character, order));
                }
                order++;
            }
            characterCount = Character.CharacterList.Count;
            builtAt = Timing.TotalTime;
        }
    }
}
