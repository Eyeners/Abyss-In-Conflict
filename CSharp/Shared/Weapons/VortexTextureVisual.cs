#if CLIENT
using System;
using Barotrauma;
using Barotrauma.Particles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    // 2D adaptation of TeraDistortB: decoded texture vectors, age modulation,
    // and a shared moving surface for distortion and visible energy.
    internal static class VortexTextureVisual
    {
        private const int Steps = 40;
        private static Texture2D texture;
        private static readonly Vector4[] field = new Vector4[(Steps + 1) * (Steps + 1)];
        private static readonly VertexPositionColorTexture[] grid = new VertexPositionColorTexture[field.Length];
        private static readonly VertexPositionColorTexture[] triangles = new VertexPositionColorTexture[Steps * Steps * 6];
        internal static void Clear() { texture = null; }
        private static bool Prepare()
        {
            var prefab = ParticleManager.FindPrefab("aic_cnc_vortex_glow");
            if (prefab == null || prefab.Sprites.Count == 0) return false;
            var sprite = prefab.Sprites[0];
            if (texture == sprite.Texture) return true;
            // The game's sprite texture may be block-compressed. GetData<Color>
            // cannot decode those blocks; read the original PNG into a temporary
            // uncompressed texture instead, without changing the active render target.
            using var stream = System.IO.File.OpenRead(sprite.FilePath.Value);
            using var decoded = Texture2D.FromStream(sprite.Texture.GraphicsDevice, stream);
            int width = decoded.Width, height = decoded.Height;
            var pixels = new Color[width * height];
            decoded.GetData(pixels);
            var rect = sprite.SourceRect;
            for (int y = 0; y <= Steps; y++)
            for (int x = 0; x <= Steps; x++)
            {
                float px = rect.X + x / (float)Steps * (rect.Width - 1), py = rect.Y + y / (float)Steps * (rect.Height - 1);
                int ix = (int)px, iy = (int)py;
                Vector4 C(int a, int b) => pixels[b * width + a].ToVector4();
                Vector4 sample = Vector4.Lerp(Vector4.Lerp(C(ix, iy), C(Math.Min(ix + 1, width - 1), iy), px - ix),
                    Vector4.Lerp(C(ix, Math.Min(iy + 1, height - 1)), C(Math.Min(ix + 1, width - 1), Math.Min(iy + 1, height - 1)), px - ix), py - iy);
                Vector3 normal = new Vector3(sample.X, sample.Y, sample.Z) * 2 - Vector3.One;
                if (normal.LengthSquared() > 0.00001f) normal.Normalize();
                field[y * (Steps + 1) + x] = new Vector4(normal, sample.W);
            }
            texture = sprite.Texture;
            return true;
        }
        internal static void Draw(Vector2 start, Vector2 end, float age, float seconds, GameScreen screen,
            GraphicsDevice graphics, BasicEffect effect, Viewport viewport, bool glow)
        {
            if (!Prepare()) return;
            Vector2 a = screen.Cam.WorldToScreen(start), b = screen.Cam.WorldToScreen(end);
            Vector2 axis = b - a;
            if (axis.LengthSquared() < 1) return;
            float zoom = axis.Length() / Vector2.Distance(start, end);
            axis.Normalize();
            Vector2 side = new Vector2(-axis.Y, axis.X), centre = Vector2.Lerp(a, b, age);
            float halfWidth = 35 + 60 * age;
            float envelope = Math.Min(1, age / 0.08f) * Math.Min(1, (1 - age) / 0.18f);
            // A visible 2D compression shell supplies the displacement. The original
            // texture modulates its broken edge, rather than its sparse alpha erasing it.
            float halfLength = 34 + 8 * age;
            for (int y = 0; y <= Steps; y++)
            for (int x = 0; x <= Steps; x++)
            {
                int index = y * (Steps + 1) + x;
                Vector2 uv = new Vector2(x, y) / Steps, q = uv * 2 - Vector2.One;
                float edge = MathHelper.SmoothStep(0, 1, Math.Min(1 - Math.Abs(q.X), 1 - Math.Abs(q.Y)) / 0.15f);
                var normal = field[index];
                Vector2 pos = centre + (axis * (q.X * halfLength) + side * (q.Y * halfWidth)) * zoom;
                Vector2 offset = Vector2.Zero;
                Color color = Color.White;
                if (!glow)
                {
                    float rho = q.Length();
                    float shell = MathHelper.Clamp((rho - 0.25f) / 0.75f, 0, 1);
                    float crest = shell * shell * (1 - shell) * 6.75f;
                    Vector2 gradient = axis * (q.X / halfLength) + side * (q.Y / halfWidth);
                    if (gradient.LengthSquared() > 0.000001f) gradient.Normalize();
                    float detail = 0.8f + normal.W * 0.35f + normal.Z * 0.12f;
                    offset = gradient * (30 * crest * detail * zoom * envelope * edge);
                    offset += (axis * normal.X + side * normal.Y) * (4 * crest * normal.W * zoom * envelope * edge);
                }
                else
                {
                    float oscillation = (float)Math.Sin(q.Y * 24 - seconds * 27 + q.X * 13);
                    float shimmer = 0.65f + 0.35f * oscillation * oscillation;
                    color = new Color(155, 180, 255) * (envelope * edge * shimmer * 0.28f);
                }
                grid[index] = new VertexPositionColorTexture(new Vector3(pos, 0), color,
                    glow ? uv : new Vector2((pos.X + offset.X) / viewport.Width, (pos.Y + offset.Y) / viewport.Height));
            }
            int count = 0;
            for (int y = 0; y < Steps; y++)
            for (int x = 0; x < Steps; x++)
            {
                int i = y * (Steps + 1) + x, j = i + Steps + 1;
                triangles[count++] = grid[i]; triangles[count++] = grid[j]; triangles[count++] = grid[i + 1];
                triangles[count++] = grid[i + 1]; triangles[count++] = grid[j]; triangles[count++] = grid[j + 1];
            }
            var scene = effect.Texture;
            if (glow) effect.Texture = texture;
            graphics.BlendState = glow ? Microsoft.Xna.Framework.Graphics.BlendState.Additive : Microsoft.Xna.Framework.Graphics.BlendState.Opaque;
            foreach (var pass in effect.CurrentTechnique.Passes)
            { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, triangles, 0, count / 3); }
            if (glow)
            {
                // A wider, dim skirt shares the exact same centre and age as the ring.
                for (int i = 0; i < count; i++)
                {
                    Vector2 p = new Vector2(triangles[i].Position.X, triangles[i].Position.Y);
                    triangles[i].Position = new Vector3(centre + (p - centre) * 1.22f, 0);
                    triangles[i].Color *= 0.18f;
                }
                foreach (var pass in effect.CurrentTechnique.Passes)
                { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, triangles, 0, count / 3); }
                // Short attached fragments, batched in one submission; no extra particle timers.
                int sparkCount = 0;
                for (int i = 0; i < 12; i++)
                {
                    float phase = i * 2.399963f + seconds * (i % 2 == 0 ? 0.7f : -0.5f);
                    float pulse = Math.Max(0, (float)Math.Sin(seconds * 23 + i * 7.13f));
                    Vector2 p = centre + (axis * ((float)Math.Cos(phase) * halfLength * 0.8f)
                        + side * ((float)Math.Sin(phase) * halfWidth * 0.8f)) * zoom;
                    Vector2 tail = p - axis * ((3 + 7 * pulse) * zoom), width = side * (0.65f * zoom);
                    Color c = new Color(210, 220, 255) * (envelope * pulse * 0.65f);
                    void V(Vector2 point, Color tint) => triangles[sparkCount++] = new VertexPositionColorTexture(new Vector3(point, 0), tint, Vector2.Zero);
                    V(p - width, c); V(p + width, c); V(tail - width, Color.Transparent);
                    V(tail - width, Color.Transparent); V(p + width, c); V(tail + width, Color.Transparent);
                }
                effect.TextureEnabled = false;
                foreach (var pass in effect.CurrentTechnique.Passes)
                { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, triangles, 0, sparkCount / 3); }
                effect.TextureEnabled = true;
            }
            effect.Texture = scene;
        }
    }
}
#endif
