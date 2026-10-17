#if CLIENT
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    public sealed class ForesightGridThermal : IAssemblyPlugin
    {
        private const float Range = 5000, RefreshInterval = 0.3f;
        private readonly struct Shape
        {
            internal readonly Vector2 Center, Half;
            internal readonly float Cosine, Sine;
            internal readonly byte Relation;
            internal Shape(Vector2 center, Vector2 half, float rotation, byte relation)
            { Center = center; Half = half; Cosine = (float)Math.Cos(rotation); Sine = (float)Math.Sin(rotation); Relation = relation; }
        }
        private sealed class Layer : IDisposable
        {
            internal readonly Submarine Submarine;
            internal readonly ForesightGridMask Mask = new();
            internal readonly List<Shape> Shapes = new();
            internal Texture2D Texture;
            internal Layer(Submarine submarine) { Submarine = submarine; }
            internal Vector2 Offset => Submarine?.DrawPosition ?? Vector2.Zero;
            public void Dispose() { Texture?.Dispose(); Texture = null; }
        }
        private static readonly List<Layer> layers = new();
        [ThreadStatic] private static byte replacingThermal;
        private static Character cachedUser;
        private static GameSession session;
        private static bool cachedShowDead, reportedError;
        private static double nextRefresh;
        private static Vector2 viewCenter, viewSize;
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.foresight.grid.thermal");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(StatusHUD), "DrawHUD", new[] { typeof(SpriteBatch), typeof(Character) }),
                    prefix: new HarmonyMethod(typeof(ForesightGridThermal), nameof(BeginHud)),
                    finalizer: new HarmonyMethod(typeof(ForesightGridThermal), nameof(EndHud)));
                harmony.Patch(AccessTools.Method(typeof(StatusHUD), "DrawThermalOverlay"),
                    prefix: new HarmonyMethod(typeof(ForesightGridThermal), nameof(DrawGrid)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"),
                    postfix: new HarmonyMethod(typeof(ForesightGridThermal), nameof(Clear)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf(); Clear();
                LuaCsLogger.LogError("[AIC ForesightGridThermal] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); Clear(); }

        private static void BeginHud(StatusHUD __instance, out byte __state)
        {
            __state = replacingThermal;
            replacingThermal = __instance.Item.Prefab.Identifier == "aic_foresight_t3_armor" ? (byte)1
                : __instance.Item.Prefab.Identifier == "aic_wardens_gdf_foehn_powerarmor" ? (byte)2 : (byte)0;
        }
        private static void EndHud(byte __state) { replacingThermal = __state; }

        private static bool DrawGrid(SpriteBatch spriteBatch, Character user, bool showDeadCharacters)
        {
            if (replacingThermal == 0 || user == null || user != Character.Controlled || Screen.Selected?.Cam == null) return true;
            if (replacingThermal == 2) return DrawFoehn(spriteBatch, user, showDeadCharacters);
            var camera = Screen.Selected.Cam;
            var view = camera.WorldView;
            Vector2 size = new Vector2(view.Width, view.Height);
            Vector2 center = new Vector2(view.X + view.Width * 0.5f, view.Y - view.Height * 0.5f);
            // Native thermal rendering also flushes the HUD batch and restores this state.
            // Flush before updating textures that the previous batch may still reference.
            spriteBatch.End();
            bool drawing = false;
            try
            {
                if (session != GameMain.GameSession || cachedUser != user) Clear();
                if (cachedUser != user || cachedShowDead != showDeadCharacters || Timing.TotalTime >= nextRefresh
                    || (Timing.TotalTime >= nextRefresh - 0.2
                        && (Vector2.DistanceSquared(center, viewCenter) > 256 * 256
                            || size.X > viewSize.X + 256 || size.Y > viewSize.Y + 256)))
                {
                    Rebuild(spriteBatch.GraphicsDevice, user, showDeadCharacters, center, size);
                    cachedUser = user; session = GameMain.GameSession; cachedShowDead = showDeadCharacters;
                    nextRefresh = Timing.TotalTime + RefreshInterval; viewCenter = center; viewSize = size;
                }
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp);
                drawing = true;
                foreach (var layer in layers)
                {
                    if (layer.Texture == null || layer.Submarine?.Removed == true) continue;
                    var mask = layer.Mask;
                    Vector2 topLeft = camera.WorldToScreen(mask.Origin + layer.Offset);
                    Vector2 bottomRight = camera.WorldToScreen(mask.Origin + layer.Offset
                        + new Vector2(mask.Width * mask.CellSize, -mask.Height * mask.CellSize));
                    int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);
                    int width = (int)Math.Round(bottomRight.X) - x, height = (int)Math.Round(bottomRight.Y) - y;
                    if (width <= 0 || height <= 0) continue;
                    spriteBatch.Draw(layer.Texture, new Rectangle(x, y, width, height),
                        new Rectangle(0, 0, mask.Width, mask.Height), Color.White);
                }
                reportedError = false;
                return false;
            }
            catch (Exception error)
            {
                if (!reportedError) LuaCsLogger.LogError("[AIC ForesightGridThermal] " + error);
                reportedError = true;
                cachedUser = null;
                // Only a rendering error falls back to native thermal, never creature size.
                return true;
            }
            finally
            {
                if (drawing) spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            }
        }

        private static bool DrawFoehn(SpriteBatch spriteBatch, Character user, bool showDead)
        {
            spriteBatch.End();
            spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, transformMatrix: Screen.Selected.Cam.Transform);
            try
            {
                var sprite = GUIStyle.UIThermalGlow.Value.Sprite;
                foreach (var target in Character.CharacterList)
                {
                    // Deliberately include HideInThermalGoggles targets, as with the Foresight scanner.
                    if (target == user || target.Removed || !target.Enabled || target.AnimController == null
                        || (!showDead && target.IsDead)
                        || Vector2.DistanceSquared(user.WorldPosition, target.WorldPosition) > Range * Range) continue;
                    Color color = user.TeamID != CharacterTeamType.None && user.TeamID == target.TeamID
                        ? Color.LimeGreen : user.IsOnFriendlyTeam(target) ? Color.Cyan : Color.OrangeRed;
                    foreach (var limb in target.AnimController.Limbs)
                    {
                        if (limb.body == null || !limb.body.Enabled
                            || (limb != target.AnimController.MainLimb && limb.Mass < 0.5f)) continue;
                        Vector2 size = ConvertUnits.ToDisplayUnits(limb.body.GetSize()) / sprite.size;
                        Vector2 pos = limb.body.DrawPosition;
                        if (!MathUtils.IsValid(pos) || !MathUtils.IsValid(size)) continue;
                        sprite.Draw(spriteBatch, new Vector2(pos.X, -pos.Y), color * 0.45f,
                            0f, Math.Max(size.X, size.Y) * 2f);
                    }
                }
            }
            finally
            {
                spriteBatch.End();
                spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            }
            return false;
        }

        private static void Rebuild(GraphicsDevice device, Character user, bool showDead, Vector2 center, Vector2 size)
        {
            foreach (var layer in layers) layer.Shapes.Clear();
            Vector2 halfView = size * 0.5f + new Vector2(256);
            foreach (var target in Character.CharacterList)
            {
                if (target == user || !target.Enabled || target.Removed || (!showDead && target.IsDead)
                    || target.AnimController == null || Vector2.DistanceSquared(user.WorldPosition, target.WorldPosition) > Range * Range) continue;
                byte relation = user.TeamID != CharacterTeamType.None && user.TeamID == target.TeamID ? (byte)2
                    : user.IsOnFriendlyTeam(target) ? (byte)1 : (byte)3;
                Layer targetLayer = null;
                foreach (var limb in target.AnimController.Limbs)
                {
                    if (limb != target.AnimController.MainLimb && limb.Mass < 0.5f) continue;
                    var body = limb.body;
                    if (body == null || !body.Enabled) continue;
                    Vector2 half = ConvertUnits.ToDisplayUnits(body.GetSize()) * 0.5f;
                    Vector2 position = body.DrawPosition;
                    float rotation = body.DrawRotation;
                    if (!MathUtils.IsValid(position) || !MathUtils.IsValid(half) || !float.IsFinite(rotation)
                        || half.X <= 0 || half.Y <= 0) continue;
                    var shape = new Shape(position, half, rotation, relation);
                    float extentX = Math.Abs(shape.Cosine) * half.X + Math.Abs(shape.Sine) * half.Y;
                    float extentY = Math.Abs(shape.Sine) * half.X + Math.Abs(shape.Cosine) * half.Y;
                    if (Math.Abs(position.X - center.X) > halfView.X + extentX
                        || Math.Abs(position.Y - center.Y) > halfView.Y + extentY) continue;
                    if (targetLayer == null)
                    {
                        foreach (var layer in layers)
                            if (layer.Submarine == target.Submarine) { targetLayer = layer; break; }
                        if (targetLayer == null) { targetLayer = new Layer(target.Submarine); layers.Add(targetLayer); }
                    }
                    targetLayer.Shapes.Add(new Shape(position - targetLayer.Offset, half, rotation, relation));
                }
            }
            for (int i = layers.Count - 1; i >= 0; i--)
                if (layers[i].Shapes.Count == 0 || layers[i].Submarine?.Removed == true)
                { layers[i].Dispose(); layers.RemoveAt(i); }

            // Keep the usual 16-unit cells. Zooming out or many overlapping giant limbs
            // increases cell size, instead of dropping large creatures or using native glow.
            float cellSize = 16;
            while ((size.X + 512) / cellSize > ForesightGridMask.Size - 2
                || (size.Y + 512) / cellSize > ForesightGridMask.Size - 2) cellSize *= 2;
            while (true)
            {
                long work = 0;
                foreach (var layer in layers)
                {
                    Vector2 localCenter = center - layer.Offset;
                    Vector2 origin = new Vector2((float)Math.Floor((localCenter.X - halfView.X) / cellSize) * cellSize,
                        (float)Math.Ceiling((localCenter.Y + halfView.Y) / cellSize) * cellSize);
                    int width = (int)Math.Ceiling((localCenter.X + halfView.X - origin.X) / cellSize);
                    int height = (int)Math.Ceiling((origin.Y - localCenter.Y + halfView.Y) / cellSize);
                    layer.Mask.Reset(origin, width, height, cellSize);
                    foreach (var shape in layer.Shapes)
                    {
                        var bounds = layer.Mask.Bounds(shape.Center, shape.Half, shape.Cosine, shape.Sine);
                        work += bounds.Width * bounds.Height;
                    }
                }
                if (work <= ForesightGridMask.WorkBudget || cellSize >= 16384) break;
                cellSize *= 2;
            }
            foreach (var layer in layers)
            {
                foreach (var shape in layer.Shapes)
                    layer.Mask.AddBox(shape.Center, shape.Half, shape.Cosine, shape.Sine, shape.Relation);
                layer.Mask.Colorize();
                if (layer.Texture == null || layer.Texture.IsDisposed || layer.Texture.GraphicsDevice != device)
                {
                    layer.Dispose();
                    layer.Texture = new Texture2D(device, ForesightGridMask.Size, ForesightGridMask.Size, false, SurfaceFormat.Color);
                }
                layer.Texture.SetData(layer.Mask.Pixels);
            }
        }

        private static void Clear()
        {
            foreach (var layer in layers) layer.Dispose();
            layers.Clear(); cachedUser = null; session = null; nextRefresh = 0;
        }
    }
}
#endif
