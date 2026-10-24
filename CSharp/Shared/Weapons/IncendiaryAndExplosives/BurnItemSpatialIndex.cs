using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Broad phase only: vanilla still applies distance, cover, fire and item damage.
    public sealed class BurnItemSpatialIndex : IAssemblyPlugin
    {
        private const float CellSize = 400;
        private static readonly Identifier BurnTag = "aic_extinguishable_burn".ToIdentifier();
        private readonly record struct Entry(Item Item, int Order);
        private sealed class Grid
        {
            internal Submarine Submarine;
            internal float Radius;
            internal readonly Dictionary<(int X, int Y), List<Entry>> Cells = new();
        }
        private static readonly List<Grid> grids = new();
        private static readonly List<Entry> moving = new();
        private static readonly HashSet<Item> indexed = new();
        private static readonly Stack<List<Entry>> pool = new();
        [ThreadStatic] private static Stack<List<Item>> queryFrames;
        private static double builtAt = double.NaN;
        private static int itemCount;
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.burn.item.spatial.index");
            try
            {
                Clear();
                harmony.Patch(AccessTools.Method(typeof(Explosion), nameof(Explosion.Explode),
                    new[] { typeof(Vector2), typeof(Entity), typeof(Character) }),
                    prefix: new HarmonyMethod(typeof(BurnItemSpatialIndex), nameof(BeginQuery)),
                    transpiler: new HarmonyMethod(typeof(BurnItemSpatialIndex), nameof(Inject)),
                    finalizer: new HarmonyMethod(typeof(BurnItemSpatialIndex), nameof(EndQuery)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    postfix: new HarmonyMethod(typeof(BurnItemSpatialIndex), nameof(Clear)));
                // Inventory and scripted transforms can change a static item's bucket
                // between two burns in the same update. Moving items are queried live.
                foreach (var method in new[]
                {
                    AccessTools.Method(typeof(Item), "Move"),
                    AccessTools.Method(typeof(Item), "SetTransform"),
                    AccessTools.PropertySetter(typeof(Item), nameof(Item.ParentInventory)),
                    AccessTools.PropertySetter(typeof(Item), nameof(Item.BodyType))
                })
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(BurnItemSpatialIndex), nameof(Changing)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                Clear();
                LuaCsLogger.LogError("[AIC BurnItemSpatialIndex] " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); Clear(); }

        private static void Clear()
        {
            grids.Clear(); moving.Clear(); indexed.Clear(); pool.Clear();
            builtAt = double.NaN; itemCount = 0;
        }

        private static void Changing(Item __instance)
        {
            if (indexed.Contains(__instance)) builtAt = double.NaN;
        }
        private static void BeginQuery(out bool __state)
        {
            queryFrames ??= new Stack<List<Item>>();
            queryFrames.Push(null);
            __state = true;
        }
        private static void EndQuery(bool __state)
        {
            // Finalizers also run if native damage or another mod's callback throws.
            // A nested explosion has its own frame and cannot reuse its parent's list.
            if (__state && queryFrames?.Count > 0) ScratchLists<Item>.Return(queryFrames.Pop());
        }

        private static IEnumerable<CodeInstruction> Inject(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int damage = code.FindIndex(c => c.Calls(AccessTools.Method(typeof(Explosion), "DamageCharacters")));
            var list = AccessTools.Field(typeof(Item), nameof(Item.ItemList));
            int index = code.FindIndex(Math.Max(0, damage), c => c.LoadsField(list));
            // The last argument to DamageCharacters is the fully modified display range.
            if (damage < 1 || index < 0 || !code[damage - 1].opcode.Name.StartsWith("ldloc"))
                throw new InvalidOperationException("Cannot locate vanilla explosion item broad phase.");
            var added = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(code[damage - 1].opcode, code[damage - 1].operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BurnItemSpatialIndex), nameof(Candidates)))
            };
            added[0].labels.AddRange(code[index].labels);
            added[0].blocks.AddRange(code[index].blocks);
            code.RemoveAt(index);
            code.InsertRange(index, added);
            return code;
        }

        private static List<Item> Candidates(Vector2 position, Entity source, float range)
        {
            if (!(source is Item burn) || !burn.HasTag(BurnTag)
                || !float.IsFinite(range) || range < 0 || range > 2000
                || !float.IsFinite(position.X) || !float.IsFinite(position.Y)
                || queryFrames == null || queryFrames.Count == 0 || queryFrames.Peek() != null) return Item.ItemList;
            if (builtAt != Timing.TotalTime || itemCount != Item.ItemList.Count) Rebuild();
            var selected = ScratchLists<Entry>.Rent();
            List<Item> result = null;
            bool committed = false;
            try
            {
                foreach (var grid in grids)
                {
                    Vector2 local = position - (grid.Submarine?.Position ?? Vector2.Zero);
                    float reach = range + grid.Radius;
                    int left = Cell(local.X - reach), right = Cell(local.X + reach);
                    int bottom = Cell(local.Y - reach), top = Cell(local.Y + reach);
                    // Unusual huge modded bodies should never create an enormous grid walk.
                    if ((long)(right - left + 1) * (top - bottom + 1) > 4096) return Item.ItemList;
                    for (int x = left; x <= right; x++)
                    for (int y = bottom; y <= top; y++)
                        if (grid.Cells.TryGetValue((x, y), out var entries))
                            foreach (var entry in entries) AddCandidate(selected, entry, position, range);
                }
                // Dynamic bodies and inventories can move between explosions in one update.
                foreach (var entry in moving) AddCandidate(selected, entry, position, range);
                selected.Sort(static (a, b) => a.Order.CompareTo(b.Order));
                result = ScratchLists<Item>.Rent();
                foreach (var entry in selected) result.Add(entry.Item);
                queryFrames.Pop(); queryFrames.Push(result);
                committed = true;
                return result;
            }
            finally
            {
                ScratchLists<Entry>.Return(selected);
                if (!committed) ScratchLists<Item>.Return(result);
            }
        }
        private static void AddCandidate(List<Entry> selected, Entry entry, Vector2 position, float range)
        {
            var item = entry.Item;
            if (item.Removed) return;
            float radius = item.body == null ? 0 : ConvertUnits.ToDisplayUnits(item.body.GetMaxExtent());
            // Conservative box test; leave the exact native circle test untouched.
            if (Overlaps(position, item.WorldPosition, range + radius)) selected.Add(entry);
        }

        internal static bool Overlaps(Vector2 center, Vector2 point, float reach)
            => Math.Abs(center.X - point.X) <= reach && Math.Abs(center.Y - point.Y) <= reach;

        private static int Cell(float value) => (int)Math.Floor(value / CellSize);

        private static void Rebuild()
        {
            foreach (var grid in grids)
                foreach (var entries in grid.Cells.Values) { entries.Clear(); pool.Push(entries); }
            grids.Clear(); moving.Clear(); indexed.Clear();
            for (int i = 0; i < Item.ItemList.Count; i++)
            {
                var item = Item.ItemList[i];
                if (item.Removed) continue;
                var entry = new Entry(item, i);
                if (item.ParentInventory != null || (item.body != null && item.body.BodyType != BodyType.Static))
                { moving.Add(entry); continue; }
                indexed.Add(item);
                var grid = grids.Find(g => g.Submarine == item.Submarine);
                if (grid == null) { grid = new Grid { Submarine = item.Submarine }; grids.Add(grid); }
                Vector2 local = item.WorldPosition - (item.Submarine?.Position ?? Vector2.Zero);
                var key = (Cell(local.X), Cell(local.Y));
                if (!grid.Cells.TryGetValue(key, out var entries))
                    grid.Cells[key] = entries = pool.Count > 0 ? pool.Pop() : new List<Entry>();
                entries.Add(entry);
                if (item.body != null)
                    grid.Radius = Math.Max(grid.Radius, ConvertUnits.ToDisplayUnits(item.body.GetMaxExtent()));
            }
            itemCount = Item.ItemList.Count;
            builtAt = Timing.TotalTime;
        }
    }
}
