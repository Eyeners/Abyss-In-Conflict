#if CLIENT
using System;
using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    internal static class FoehnDashAfterimages
    {
        private sealed record Part(Texture2D Texture, Rectangle Source, Vector2 Position,
            Vector2 Origin, float Rotation, float Scale, SpriteEffects Flip);
        private sealed record Frame(Item Owner, double Born, Submarine Sub, Vector2 SubPosition, List<Part> Parts, Vector2 BurstOffset);
        private static readonly List<Frame> frames = new();
        private const float Lifetime = 0.28f;
        internal static bool Active
        {
            get
            {
                frames.RemoveAll(f => f.Owner.Removed || f.Sub?.Removed == true || Timing.TotalTime < f.Born || Timing.TotalTime - f.Born >= Lifetime);
                return frames.Count > 0;
            }
        }
        internal static void Remove(Item item) => frames.RemoveAll(f => f.Owner == item);
        private static readonly Dictionary<(Texture2D, Rectangle), Texture2D> masks = new();
        private static readonly HashSet<(Texture2D, Rectangle)> failedMasks = new();
        internal static void Clear()
        {
            frames.Clear();
            ClearMasks();
        }
        private static void ClearMasks()
        {
            foreach (var mask in masks.Values) mask.Dispose();
            masks.Clear();
            failedMasks.Clear();
        }
        internal static void PrepareMasks()
        {
            foreach (var frame in frames)
                foreach (var part in frame.Parts)
                    if (!part.Texture.IsDisposed) Silhouette(part);
        }
        private static Texture2D Silhouette(Part part)
        {
            var key = (part.Texture, part.Source);
            if (masks.TryGetValue(key, out var mask)) return mask;
            if (failedMasks.Contains(key)) return null;
            if (part.Source.Width <= 0 || part.Source.Height <= 0) return null;
            // Cache only the selected limb region, not a copy of the full armor atlas.
            // Preserve transparency, discard the armor's RGB detail completely.
            if (masks.Count >= 128) return null;
            var pixels = new Color[part.Source.Width * part.Source.Height];
            // DXT textures cannot be read as Color[]. GPU sampling decodes them into
            // a Color render target first. Run before scene composition, never mid-batch.
            var graphics = part.Texture.GraphicsDevice;
            var previousTargets = graphics.GetRenderTargets();
            var previousViewport = graphics.Viewport;
            try
            {
                using var decoded = new RenderTarget2D(graphics, part.Source.Width, part.Source.Height,
                    false, SurfaceFormat.Color, DepthFormat.None);
                using var copier = new SpriteBatch(graphics);
                graphics.SetRenderTarget(decoded);
                graphics.Clear(Color.Transparent);
                copier.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
                copier.Draw(part.Texture, new Rectangle(0, 0, part.Source.Width, part.Source.Height), part.Source, Color.White);
                copier.End();
                graphics.SetRenderTarget(null);
                decoded.GetData(pixels);
            }
            catch (Exception error)
            {
                failedMasks.Add(key);
                DebugConsole.ThrowError("[AIC] Dash silhouette texture could not be decoded: " + error.Message);
                return null;
            }
            finally
            {
                if (previousTargets.Length > 0) graphics.SetRenderTargets(previousTargets);
                else graphics.SetRenderTarget(null);
                graphics.Viewport = previousViewport;
            }
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color((byte)255, (byte)255, (byte)255, pixels[i].A);
            mask = new Texture2D(part.Texture.GraphicsDevice, part.Source.Width, part.Source.Height);
            mask.SetData(pixels);
            masks.Add(key, mask);
            return mask;
        }
        internal static void Capture(Item item, Character c, Vector2 burstOffset = default)
        {
            if (c == null || c.Removed || c.InvisibleTimer > 0) return;
            var parts = new List<Part>();
            bool left = c.AnimController.Dir < 0;
            var flip = left ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
            foreach (var limb in c.AnimController.Limbs)
            {
                if (limb.Removed || limb.IsSevered || limb.body == null) continue;
                limb.body.UpdateDrawPosition();
                float rotation = left ? limb.body.DrawRotation : -limb.body.DrawRotation;
                bool clothed = false;
                foreach (var w in limb.WearingItems)
                {
                    if (w.Sprite?.Texture == null) continue;
                    clothed = true;
                    float scale = w.Scale;
                    if (w.InheritScale)
                    {
                        if (!w.IgnoreTextureScale) scale *= limb.TextureScale;
                        if (!w.IgnoreLimbScale) scale *= limb.Scale;
                    }
                    Vector2 origin = w.InheritOrigin && limb.ActiveSprite != null ? limb.ActiveSprite.Origin : w.Sprite.Origin;
                    if (left) origin.X = w.Sprite.SourceRect.Width - origin.X;
                    parts.Add(new Part(w.Sprite.Texture, w.Sprite.SourceRect, limb.body.DrawPosition, origin,
                        rotation + (left ? w.Rotation : -w.Rotation), scale, flip));
                }
                // Foehn replaces the body sprites with armor. Avoid a second naked silhouette.
                if (!clothed && limb.ActiveSprite?.Texture != null)
                {
                    var sprite = limb.ActiveSprite;
                    parts.Add(new Part(sprite.Texture, sprite.SourceRect, limb.body.DrawPosition,
                        sprite.Origin, rotation, limb.Scale * limb.TextureScale, flip));
                }
            }
            if (parts.Count == 0) return;
            frames.Add(new Frame(item, Timing.TotalTime, c.Submarine, c.Submarine?.DrawPosition ?? Vector2.Zero, parts, burstOffset));
            int count = 0;
            for (int i = frames.Count - 1; i >= 0; i--)
                if (frames[i].Owner == item && ++count > (item.Prefab.Identifier == "aic_chronon_pack".ToIdentifier() ? 19 : 7)) frames.RemoveAt(i);
            while (frames.Count > 64) frames.RemoveAt(0);
        }
        internal static void Draw(SpriteBatch batch, Camera camera)
        {
            foreach (var frame in frames)
            {
                float life = MathHelper.Clamp(1 - (float)(Timing.TotalTime - frame.Born) / Lifetime, 0, 1);
                Color tint = Color.Lerp(new Color(90, 145, 175), new Color(220, 240, 250), life);
                if (frame.Owner.Prefab.Identifier == "aic_chronon_pack".ToIdentifier())
                    tint = Color.Lerp(new Color(230, 80, 5), new Color(255, 220, 110), life);
                tint.A = (byte)(255 * 0.3f * life * life);
                Vector2 offset = (frame.Sub?.DrawPosition ?? Vector2.Zero) - frame.SubPosition;
                offset += frame.BurstOffset * MathHelper.SmoothStep(0.25f, 1, 1 - life);
                foreach (var part in frame.Parts)
                {
                    if (part.Texture.IsDisposed) continue;
                    if (frame.BurstOffset != Vector2.Zero)
                    {
                        batch.Draw(part.Texture, camera.WorldToScreen(part.Position + offset), part.Source,
                            Color.White * (0.22f * life * life), part.Rotation, part.Origin,
                            part.Scale * camera.Zoom, part.Flip, 0);
                        continue;
                    }
                    if (!masks.TryGetValue((part.Texture, part.Source), out var mask)) continue;
                    batch.Draw(mask, camera.WorldToScreen(part.Position + offset), null, tint,
                        part.Rotation, part.Origin, part.Scale * camera.Zoom, part.Flip, 0);
                }
            }
        }
    }
}
#endif
