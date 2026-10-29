#if CLIENT
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    // Resamples the completed scene through a displaced UV mesh. No opaque beam sprite.
    public sealed class SonicRefraction : IAssemblyPlugin
    {
        private const float Lifetime = 0.45f;
        private const float HalfWidth = 36f;
        private const float Displacement = 16f;
        private sealed record Wave(Vector2 Start, Vector2 End, double Born, bool Ring = false,
            int ShieldDirection = 0, float ShieldStrength = 0, float RingLifetime = 0.9f, float RingStrength = 90f,
            float[] FanReach = null, Vector2? VortexEnd = null, bool Ground = false, bool Teleport = false,
            float GroundScale = 1f, float GroundStrength = 1f, EffectAnchor Anchor = default, bool Rift = false, bool VortexGlow = false, bool Stasis = false, bool Freeze = false, bool Preview = false, bool Mirror = false, int PreviewOwner = 0)
        {
            internal Vector2 WorldStart => Start + Anchor.DrawOffset;
            internal Vector2 WorldEnd => End + Anchor.DrawOffset;
        }
        internal static void AddTeleport(Vector2 departure, Vector2 arrival, byte stage = 0)
        {
            if (stage != 2)
            {
            waves.Add(new Wave(departure, new Vector2(180, 0), Timing.TotalTime, true,
                RingLifetime: 0.55f, RingStrength: 110f, Teleport: true));
                var arc = Barotrauma.Particles.ParticleManager.FindPrefab("aic_teleport_microarc");
                if (arc != null)
                    for (int i = 0; i < 7; i++)
                    {
                        float a = Rand.Range(0f, MathHelper.TwoPi);
                        Vector2 p = departure + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * Rand.Range(25f, 110f);
                        GameMain.ParticleManager?.CreateParticle(arc, p, Vector2.Zero, -a, Hull.FindHull(p),
                            drawOrder: Barotrauma.Particles.ParticleDrawOrder.Foreground);
                    }
            }
            if (stage != 1)
            waves.Add(new Wave(arrival, new Vector2(125, 0), Timing.TotalTime, true,
                RingLifetime: 0.3f, RingStrength: 35f));
        }
        internal static void AddGroundWave(Vector2 start, Vector2 end, float duration, float scale = 1f, float strength = 1f, EffectAnchor anchor = default)
            => waves.Add(new Wave(start, end, Timing.TotalTime, true,
                RingLifetime: duration, Ground: true, GroundScale: scale, GroundStrength: strength, Anchor: anchor));
        internal static void AddVortex(Vector2 start, Vector2 end, bool glowEnabled = true, EffectAnchor? anchor = null)
        {
            Vector2 delta = end - start;
            float distance = delta.Length();
            if (distance < 1) return;
            float duration = distance / (glowEnabled ? SonicProjector.VortexSpeed : 400f);
            waves.Add(new Wave(start, new Vector2(95, 0), Timing.TotalTime, true,
                RingLifetime: duration, RingStrength: 18, VortexEnd: end, Anchor: anchor ?? EffectAnchor.At(start), VortexGlow: glowEnabled));
        }
        internal static void AddFan(Vector2 origin, float angle, float halfAngle, float[] reach, EffectAnchor? anchor = null)
            => waves.Add(new Wave(origin, new Vector2(angle, halfAngle), Timing.TotalTime, FanReach: reach,
                Anchor: anchor ?? EffectAnchor.At(origin)));
        internal static void AddImpactRing(Vector2 centre)
            => waves.Add(new Wave(centre, new Vector2(85f, 0), Timing.TotalTime, true,
                RingLifetime: 0.25f, RingStrength: 10f));
        internal static void AddChrononBurst(Vector2 centre)
            => waves.Add(new Wave(centre, new Vector2(140f, 0), Timing.TotalTime, true,
                RingLifetime: 0.4f, RingStrength: 32f));
        internal static void PreviewStasis(Vector2 centre, float radius)
        {
            waves.RemoveAll(w => w.Preview && w.PreviewOwner == 0);
            waves.Add(new Wave(centre, new Vector2(radius, 0), Timing.TotalTime, true,
                RingLifetime: 0.1f, RingStrength: 8f, Anchor: EffectAnchor.At(centre), Stasis: true, Preview: true));
        }
        internal static void CancelNPCStasisPreview(int owner)
            => waves.RemoveAll(w => w.Preview && w.PreviewOwner == owner);
        internal static void AddNPCStasisPreview(int owner, Vector2 centre, float radius, float duration)
        {
            CancelNPCStasisPreview(owner);
            waves.Add(new Wave(centre, new Vector2(radius, 0), Timing.TotalTime, true,
                RingLifetime: duration, RingStrength: 8f, Anchor: EffectAnchor.At(centre),
                Stasis: true, Preview: true, PreviewOwner: owner));
        }
        internal static void AddStasis(Vector2 centre, float radius, float duration, bool freeze = false)
        {
            waves.RemoveAll(w => w.Preview && w.PreviewOwner == 0);
            if (freeze)
                waves.Add(new Wave(centre, new Vector2(radius * 1.06f, 0), Timing.TotalTime, true,
                    RingLifetime: 0.22f, RingStrength: 40f, Anchor: EffectAnchor.At(centre)));
            waves.Add(new Wave(centre, new Vector2(radius, 0), Timing.TotalTime, true,
                RingLifetime: duration + 0.45f, RingStrength: 32f, Anchor: EffectAnchor.At(centre), Stasis: true, Freeze: freeze));
        }
        internal static void AddShield(Vector2 centre, Vector2 radii, int direction, float speed)
            => waves.Add(new Wave(centre, radii, Timing.TotalTime, true, direction,
                6f + 24f * Math.Clamp(speed / 10f, 0f, 1f)));
        internal static void AddMirrorSurface(Vector2 start, Vector2 end)
            => waves.Add(new Wave(start, end, Timing.TotalTime, Mirror: true));
        internal static void AddRing(Vector2 centre, float radius)
            => waves.Add(new Wave(centre, new Vector2(radius, 0), Timing.TotalTime, true));
        internal static void AddRiftExplosion(Vector2 centre)
            => waves.Add(new Wave(centre, new Vector2(155f, 0), Timing.TotalTime, true,
                RingLifetime: 1.1f, RingStrength: 38f, Anchor: EffectAnchor.At(centre), Rift: true));
        private static readonly List<Wave> waves = new();
        private static readonly System.Reflection.FieldInfo sceneField = AccessTools.Field(typeof(GameScreen), "renderTargetFinal");
        private static BasicEffect effect;
        private static RenderTarget2D composite;
        private static Texture2D originalScene;
        private static SpriteBatch copyBatch;
        private static VertexPositionColorTexture[] vertices = Array.Empty<VertexPositionColorTexture>();
        private static VertexPositionColorTexture[] grid = Array.Empty<VertexPositionColorTexture>();
        private Harmony harmony;
        internal static void Add(Vector2 start, Vector2 end, EffectAnchor? anchor = null)
        {
            if (Vector2.DistanceSquared(start, end) > 1) waves.Add(new Wave(start, end, Timing.TotalTime,
                Anchor: anchor ?? EffectAnchor.At(start)));
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.sonic.refraction");
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "DrawMap"),
                transpiler: new HarmonyMethod(typeof(SonicRefraction), nameof(Inject)),
                finalizer: new HarmonyMethod(typeof(SonicRefraction), nameof(RestoreScene)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            VortexTextureVisual.Clear();
            FoehnDashAfterimages.Clear();
            harmony?.UnpatchSelf(); waves.Clear(); effect?.Dispose(); effect = null;
            composite?.Dispose(); composite = null; copyBatch?.Dispose(); copyBatch = null;
        }
        private static IEnumerable<CodeInstruction> Inject(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int last = code.FindLastIndex(i => i.operand is System.Reflection.MethodInfo m
                && m.DeclaringType == typeof(GraphicsDevice) && m.Name == "SetRenderTarget");
            if (last < 0) throw new InvalidOperationException("Sonic refraction: scene composition hook not found.");
            code.InsertRange(last + 1, new[] {
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SonicRefraction), nameof(Draw))) });
            return code;
        }
        private static void RestoreScene(GameScreen __instance)
        {
            if (originalScene == null) return;
            sceneField.SetValue(__instance, originalScene); originalScene = null;
        }
        private static void Draw(GameScreen __instance, GraphicsDevice graphics)
        {
            waves.RemoveAll(w => w.Mirror || w.ShieldDirection != 0 || Timing.TotalTime - w.Born >= (w.Ring ? w.RingLifetime : Lifetime) || Timing.TotalTime < w.Born);
            foreach (var charge in FoehnCharge.Visible) charge.QueueShieldRefraction();
            ReflectionShield.QueueSurfaces();
            bool railBeams = RailThreadRenderer.HasBeams;
            bool dashTrails = FoehnDashAfterimages.Active;
            bool tsunamiCharge = TsunamiCharge.Visible.Exists(c => c.HasSparks);
            bool riftNodes = RiftCascadeDrawing.Active;
            if (waves.Count == 0 && !railBeams && !riftNodes && !tsunamiCharge && !dashTrails && !ChrononStasis.Visible && !ReflectionShield.Visible) return;
            var texture = sceneField.GetValue(__instance) as Texture2D;
            if (texture == null) return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (composite == null || composite.Width != texture.Width || composite.Height != texture.Height)
            {
                composite?.Dispose();
                composite = new RenderTarget2D(graphics, texture.Width, texture.Height);
            }
            copyBatch ??= new SpriteBatch(graphics);
            effect ??= new BasicEffect(graphics) { TextureEnabled = true, VertexColorEnabled = true };
            var viewport = graphics.Viewport;
            effect.Texture = texture;
            effect.World = effect.View = Matrix.Identity;
            effect.Projection = Matrix.CreateOrthographicOffCenter(0, viewport.Width, viewport.Height, 0, 0, 1);
            var blend = graphics.BlendState;
            var depth = graphics.DepthStencilState;
            var raster = graphics.RasterizerState;
            var sampler = graphics.SamplerStates[0];
            try
            {
                if (dashTrails) FoehnDashAfterimages.PrepareMasks();
                graphics.SetRenderTarget(composite);
                copyBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
                copyBatch.Draw(texture, new Rectangle(0, 0, texture.Width, texture.Height), Color.White);
                copyBatch.End();
                graphics.BlendState = BlendState.Opaque;
                graphics.DepthStencilState = DepthStencilState.None;
                graphics.RasterizerState = RasterizerState.CullNone;
                graphics.SamplerStates[0] = SamplerState.LinearClamp;
                foreach (var wave in waves)
                {
                    if (wave.VortexEnd is Vector2 vortexTarget && wave.VortexGlow)
                    {
                        VortexTextureVisual.Draw(wave.WorldStart, vortexTarget + wave.Anchor.DrawOffset,
                            (float)((Timing.TotalTime - wave.Born) / wave.RingLifetime), (float)(Timing.TotalTime - wave.Born),
                            __instance, graphics, effect, viewport, false);
                        continue;
                    }
                    if (wave.Ground)
                    {
                        DrawGroundWave(wave, __instance, graphics, viewport);
                        continue;
                    }
                    if (wave.Mirror)
                    {
                        DrawMirrorSurface(wave, __instance, graphics, viewport);
                        continue;
                    }
                    if (wave.FanReach != null)
                    {
                        DrawFan(wave, __instance, graphics, viewport);
                        continue;
                    }
                    if (wave.Ring)
                    {
                        DrawRing(wave, __instance, graphics, viewport);
                        if (wave.Freeze) DrawStasisFacets(wave, __instance, graphics, viewport);
                        continue;
                    }
                    Vector2 start = __instance.Cam.WorldToScreen(wave.WorldStart);
                    Vector2 end = __instance.Cam.WorldToScreen(wave.WorldEnd);
                    Vector2 axis = end - start;
                    float length = axis.Length();
                    if (length < 1) continue;
                    axis /= length;
                    Vector2 normal = new Vector2(-axis.Y, axis.X);
                    float zoom = length / Vector2.Distance(wave.Start, wave.End);
                    float age = (float)(Timing.TotalTime - wave.Born);
                    int columns = Math.Max(2, (int)Math.Ceiling(length / 12));
                    const int rows = 12;
                    int count = columns * rows * 6;
                    if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
                    VertexPositionColorTexture Vertex(int x, int y)
                    {
                        float u = (float)x / columns, v = (float)y / rows * 2 - 1;
                        // Fade only near the endpoints, rather than over the entire beam.
                        float edgeLength = Math.Min(12f * zoom, length * 0.25f);
                        float endFade = Math.Clamp(Math.Min(u, 1 - u) * length / edgeLength, 0f, 1f);
                        float envelope = (1 - v * v) * endFade;
                        float strength = envelope * (1 - age / Lifetime);
                        Vector2 pos = start + axis * (length * u) + normal * (HalfWidth * zoom * v);
                        // Curved fronts: the centre leads while both edges trail behind.
                        float phase = (u * length / zoom + 0.5f * HalfWidth * v * v) / 45f - age * 35;
                        float warpedPhase = phase + 0.65f * (float)Math.Sin(phase * 0.5f);
                        float ripple = 0.5f + 0.5f * (float)Math.Sin(warpedPhase);
                        // The front normal points forward at the centre and diagonally outward at the sides.
                        Vector2 outward = Vector2.Normalize(axis + normal * v);
                        Vector2 sample = pos - outward * (ripple * Displacement * zoom * strength);
                        return new VertexPositionColorTexture(new Vector3(pos, 0), Color.White,
                            new Vector2(sample.X / viewport.Width, sample.Y / viewport.Height));
                    }
                    int index = 0;
                    int gridCount = (columns + 1) * (rows + 1);
                    if (grid.Length < gridCount) grid = new VertexPositionColorTexture[gridCount];
                    for (int x = 0; x <= columns; x++)
                        for (int y = 0; y <= rows; y++) grid[x * (rows + 1) + y] = Vertex(x, y);
                    for (int x = 0; x < columns; x++)
                        for (int y = 0; y < rows; y++)
                        {
                            int aIndex = x * (rows + 1) + y, bIndex = aIndex + rows + 1;
                            var a = grid[aIndex]; var b = grid[bIndex];
                            var c = grid[aIndex + 1]; var d = grid[bIndex + 1];
                            vertices[index++] = a; vertices[index++] = b; vertices[index++] = c;
                            vertices[index++] = c; vertices[index++] = b; vertices[index++] = d;
                        }
                    foreach (var pass in effect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / 3);
                    }
                }
                foreach (var wave in waves)
                    if (wave.VortexEnd is Vector2 vortexTarget && wave.VortexGlow)
                        VortexTextureVisual.Draw(wave.WorldStart, vortexTarget + wave.Anchor.DrawOffset,
                            (float)((Timing.TotalTime - wave.Born) / wave.RingLifetime), (float)(Timing.TotalTime - wave.Born),
                            __instance, graphics, effect, viewport, true);
                if (railBeams) RailThreadRenderer.Draw(__instance, graphics, effect);
                if (riftNodes) RiftCascadeDrawing.Draw(__instance, graphics, effect);
                if (dashTrails || ChrononStasis.Visible || ReflectionShield.Visible)
                {
                    copyBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
                    FoehnDashAfterimages.Draw(copyBatch, __instance.Cam);
                    ChrononStasis.DrawAll(copyBatch, __instance.Cam);
                    ReflectionShield.DrawImpacts(copyBatch, __instance.Cam);
                    copyBatch.End();
                }
                if (tsunamiCharge)
                {
                    copyBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive);
                    foreach (var charge in TsunamiCharge.Visible)
                        if (charge.HasSparks) charge.Draw(copyBatch, __instance.Cam);
                    copyBatch.End();
                }
            }
            finally
            {
                graphics.BlendState = blend; graphics.DepthStencilState = depth;
                graphics.RasterizerState = raster; graphics.SamplerStates[0] = sampler;
                graphics.SetRenderTarget(null);
            }
            originalScene = texture;
            sceneField.SetValue(__instance, composite);
            GameMain.PerformanceCounter.AddElapsedTicks("Draw:Map:AICRefraction", System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
        private static void DrawGroundWave(Wave wave, GameScreen screen, GraphicsDevice graphics, Viewport viewport)
        {
            float progress = (float)(Timing.TotalTime - wave.Born) / wave.RingLifetime;
            Vector2 start = screen.Cam.WorldToScreen(wave.WorldStart), end = screen.Cam.WorldToScreen(wave.WorldEnd);
            Vector2 axis = end - start;
            float length = axis.Length();
            if (length < 0.01f) return;
            float zoom = length / Vector2.Distance(wave.Start, wave.End);
            axis /= length;
            Vector2 side = new Vector2(-axis.Y, axis.X);
            Vector2 centre = Vector2.Lerp(start, end, progress);
            const int columns = 32, rows = 24;
            int count = columns * rows * 6;
            if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
            int gridCount = (columns + 1) * (rows + 1);
            if (grid.Length < gridCount) grid = new VertexPositionColorTexture[gridCount];
            for (int x = 0; x <= columns; x++)
                for (int y = 0; y <= rows; y++)
                {
                    float q = 2f * x / columns - 1, v = 2f * y / rows - 1;
                    // Long trailing shoulder, short leading compression region.
                    float along = q < 0 ? q * 120f : q * 55f;
                    float envelope = Math.Max(0, 1 - q * q) * Math.Max(0, 1 - v * v);
                    envelope *= envelope;
                    Vector2 pos = centre + (axis * along + side * (v * 75f)) * (zoom * wave.GroundScale);
                    // Pull sampling toward the axis: structure lines bow smoothly around the centre.
                    Vector2 sample = pos + (axis * (22f * envelope) - side * (v * 46f * envelope)) * (zoom * wave.GroundStrength);
                    grid[x * (rows + 1) + y] = new VertexPositionColorTexture(new Vector3(pos, 0), Color.White,
                        new Vector2(sample.X / viewport.Width, sample.Y / viewport.Height));
                }
            int index = 0;
            for (int x = 0; x < columns; x++)
                for (int y = 0; y < rows; y++)
                {
                    int a = x * (rows + 1) + y, b = a + rows + 1;
                    vertices[index++] = grid[a]; vertices[index++] = grid[b]; vertices[index++] = grid[a + 1];
                    vertices[index++] = grid[a + 1]; vertices[index++] = grid[b]; vertices[index++] = grid[b + 1];
                }
            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / 3);
            }
        }
        private static void DrawFan(Wave wave, GameScreen screen, GraphicsDevice graphics, Viewport viewport)
        {
            int sectors = wave.FanReach.Length - 1;
            const int bands = 96;
            int count = sectors * bands * 6;
            if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
            int gridCount = (sectors + 1) * (bands + 1);
            if (grid.Length < gridCount) grid = new VertexPositionColorTexture[gridCount];
            float age = (float)(Timing.TotalTime - wave.Born);
            Vector2 origin = screen.Cam.WorldToScreen(wave.WorldStart);
            for (int s = 0; s <= sectors; s++)
            {
                float angle = wave.End.X + wave.End.Y * (2f * s / sectors - 1);
                Vector2 direction = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                Vector2 screenStep = screen.Cam.WorldToScreen(wave.WorldStart + direction) - origin;
                float reach = wave.FanReach[s];
                for (int b = 0; b <= bands; b++)
                {
                    float distance = reach * b / bands;
                    float edge = Math.Min(12f, reach * 0.25f);
                    float fade = edge > 0 ? Math.Clamp(Math.Min(distance, reach - distance) / edge, 0, 1) : 0;
                    float strength = fade * (float)Math.Sin(MathHelper.Pi * s / sectors) * (1 - age / Lifetime);
                    // Shared world-distance phase gives coherent expanding arcs across the fan.
                    float phase = distance / 45f - age * 35;
                    float ripple = 0.5f + 0.5f * (float)Math.Sin(phase + 0.65f * (float)Math.Sin(phase * 0.5f));
                    Vector2 pos = origin + screenStep * distance;
                    Vector2 sample = pos - screenStep * (Displacement * ripple * strength);
                    grid[s * (bands + 1) + b] = new VertexPositionColorTexture(new Vector3(pos, 0), Color.White,
                        new Vector2(sample.X / viewport.Width, sample.Y / viewport.Height));
                }
            }
            int index = 0;
            for (int s = 0; s < sectors; s++)
                for (int b = 0; b < bands; b++)
                {
                    int a = s * (bands + 1) + b, c = a + bands + 1;
                    vertices[index++] = grid[a]; vertices[index++] = grid[c]; vertices[index++] = grid[a + 1];
                    vertices[index++] = grid[a + 1]; vertices[index++] = grid[c]; vertices[index++] = grid[c + 1];
                }
            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / 3);
            }
        }
        private static void DrawMirrorSurface(Wave wave, GameScreen screen, GraphicsDevice graphics, Viewport viewport)
        {
            Vector2 start = screen.Cam.WorldToScreen(wave.Start), end = screen.Cam.WorldToScreen(wave.End);
            Vector2 axis = end - start;
            if (axis.LengthSquared() < 1) return;
            axis.Normalize();
            Vector2 normal = new Vector2(-axis.Y, axis.X);
            float zoom = screen.Cam.Zoom;
            const int rows = 24, columns = 8;
            int count = rows * columns * 6;
            if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
            VertexPositionColorTexture Vertex(int x, int y)
            {
                float u = (float)y / rows, v = 2f * x / columns - 1;
                Vector2 pos = Vector2.Lerp(start, end, u) + normal * (v * 6 * zoom);
                float envelope = (1 - v * v) * (float)Math.Sin(MathHelper.Pi * u);
                float shimmer = 0.75f + 0.25f * (float)Math.Sin(u * 19 - Timing.TotalTime * 4);
                Vector2 sample = pos + normal * (3 * zoom * envelope * shimmer);
                return new VertexPositionColorTexture(new Vector3(pos, 0), Color.White,
                    new Vector2(sample.X / viewport.Width, sample.Y / viewport.Height));
            }
            int gridCount = (rows + 1) * (columns + 1);
            if (grid.Length < gridCount) grid = new VertexPositionColorTexture[gridCount];
            for (int y = 0; y <= rows; y++)
                for (int x = 0; x <= columns; x++) grid[y * (columns + 1) + x] = Vertex(x, y);
            int index = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < columns; x++)
                {
                    int at = y * (columns + 1) + x, next = at + columns + 1;
                    var a = grid[at]; var b = grid[at + 1];
                    var c = grid[next]; var d = grid[next + 1];
                    vertices[index++] = a; vertices[index++] = b; vertices[index++] = c;
                    vertices[index++] = c; vertices[index++] = b; vertices[index++] = d;
                }
            foreach (var pass in effect.CurrentTechnique.Passes)
            { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / 3); }
        }
        private static void DrawStasisFacets(Wave wave, GameScreen screen, GraphicsDevice graphics, Viewport viewport)
        {
            const int pieces = 42;
            float elapsed = (float)(Timing.TotalTime - wave.Born);
            float fade = Math.Clamp(elapsed / 0.18f, 0, 1) * Math.Clamp((wave.RingLifetime - elapsed) / 0.45f, 0, 1);
            float zoom = screen.Cam.Zoom;
            float radius = wave.End.X * zoom;
            Vector2 centre = screen.Cam.WorldToScreen(wave.WorldStart);
            if (vertices.Length < pieces * 3) vertices = new VertexPositionColorTexture[pieces * 3];
            for (int i = 0; i < pieces; i++)
            {
                float angle = i * 2.399963f;
                float distance = (float)Math.Sqrt((i + 0.5f) / pieces) * radius * 0.87f;
                Vector2 pos = centre + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * distance;
                float size = (10 + 12 * (0.5f + 0.5f * (float)Math.Sin(i * 7.1f))) * zoom;
                Vector2 shift = new Vector2((float)Math.Sin(i * 3 + elapsed * 1.3f), (float)Math.Cos(i * 5 - elapsed)) * (7 * zoom * fade);
                for (int j = 0; j < 3; j++)
                {
                    float a = angle + j * MathHelper.TwoPi / 3 + 0.22f * (float)Math.Sin(i + j * 2);
                    Vector2 p = pos + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * size;
                    Vector2 uv = p + shift;
                    vertices[i * 3 + j] = new VertexPositionColorTexture(new Vector3(p, 0), Color.White,
                        new Vector2(uv.X / viewport.Width, uv.Y / viewport.Height));
                }
            }
            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, pieces);
            }
        }
        private static void DrawRing(Wave wave, GameScreen screen, GraphicsDevice graphics, Viewport viewport)
        {
            Vector2 centre = screen.Cam.WorldToScreen(wave.WorldStart);
            float zoom = Vector2.Distance(centre, screen.Cam.WorldToScreen(wave.WorldStart + Vector2.UnitX));
            bool shield = wave.ShieldDirection != 0;
            float age = shield ? 0 : (float)(Timing.TotalTime - wave.Born) / wave.RingLifetime;
            Vector2 vortexAxis = Vector2.UnitX, vortexSide = Vector2.UnitY;
            if (wave.VortexEnd is Vector2 vortexEnd)
            {
                vortexEnd += wave.Anchor.DrawOffset;
                centre = screen.Cam.WorldToScreen(Vector2.Lerp(wave.WorldStart, vortexEnd, age));
                vortexAxis = Vector2.Normalize(screen.Cam.WorldToScreen(vortexEnd) - screen.Cam.WorldToScreen(wave.WorldStart));
                vortexSide = new Vector2(-vortexAxis.Y, vortexAxis.X);
            }
            float radius = wave.End.X * (0.2f + 0.8f * age) * zoom;
            if (wave.Stasis)
                radius = wave.End.X * (wave.Preview ? 1 : MathHelper.SmoothStep(0, 1, Math.Min(1, age * wave.RingLifetime / 0.18f))) * zoom;
            if (wave.Rift)
                radius = wave.End.X * (age < 0.1f ? MathHelper.Lerp(0.45f, 1f, age / 0.1f)
                    : MathHelper.Lerp(1f, 0.65f, (age - 0.1f) / 0.9f)) * zoom;
            float teleportTime = age * wave.RingLifetime;
            float collapse = Math.Clamp((teleportTime - 0.5f) / 0.05f, 0f, 1f);
            if (wave.Teleport)
                radius = wave.End.X * (teleportTime < 0.3f
                    ? MathHelper.Lerp(0.15f, 1f, teleportTime / 0.3f)
                    : 1f - collapse) * zoom;
            // Keep the crest at the same location while the leading edge compresses
            // and the trailing region stretches inward as speed increases.
            float shieldSpeed = shield ? Math.Clamp((wave.ShieldStrength - 6f) / 24f, 0f, 1f) : 0f;
            // The original 0.55..1.00 shell is the midpoint (5 m/s).
            // Keep a readable compression region ahead of the 0.85 crest even at full speed.
            float shieldInner = shieldSpeed <= 0.5f
                ? MathHelper.Lerp(0.65f, 0.55f, shieldSpeed * 2)
                : MathHelper.Lerp(0.55f, 0.20f, (shieldSpeed - 0.5f) * 2);
            float shieldOuter = MathHelper.Lerp(1.05f, 0.95f, shieldSpeed);
            const int sectors = 128, bands = 24;
            int count = sectors * bands * 6;
            if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
            VertexPositionColorTexture Vertex(int sector, int band)
            {
                float angle = shield ? -MathHelper.PiOver2 + MathHelper.Pi * sector / sectors
                    : MathHelper.TwoPi * sector / sectors;
                float t = (float)band / bands;
                Vector2 radial = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                float r = radius * (0.35f + 0.65f * t);
                if (wave.Teleport || wave.Stasis) r = radius * t;
                // Single smooth outer crest with a long inward falloff; no periodic ripples.
                float crest = t * t * (1 - t) * 6.75f;
                float strength = crest * (1 - age * age);
                if (wave.Rift)
                {
                    // A lens around the opaque core: displaced scene samples pull the
                    // surroundings inward, then return continuously to the original image.
                    float envelope = Math.Min(1f, age / 0.08f) * (1 - age) * (1 - age);
                    float seed = (float)(wave.Born % 100) * 17f;
                    float irregularity = 1 + 0.18f * (float)(Math.Sin(angle * 5 + seed + age * 9)
                        * Math.Sin(angle * 9 - age * 13));
                    strength = crest * envelope * irregularity;
                }
                if (wave.Teleport)
                {
                    // Hold the full-size lens while its peak shifts to the central black core.
                    // Only the final stage contracts the geometry and displacement together.
                    strength = teleportTime < 0.3f
                        ? (float)Math.Sin(MathHelper.Pi * t) * (0.1f + 3f * teleportTime)
                        : 3.5f * (1 - t) * (1 - t) * Math.Min(r / (5f * zoom), 1f) * (1 - collapse);
                    float seed = (float)(wave.Born % 100) * 17f;
                    float turbulence = (float)(Math.Sin(angle * 5 + teleportTime * 31 + seed)
                        * Math.Sin(t * 13 - teleportTime * 23 + seed));
                    strength *= 1f + 0.3f * turbulence;
                }
                Vector2 pos = centre + radial * r;
                if (wave.Stasis)
                {
                    float elapsed = age * wave.RingLifetime;
                    float fade = wave.Preview ? 1 : Math.Clamp((wave.RingLifetime - elapsed) / 0.45f, 0, 1);
                    float shell = (float)Math.Exp(-Math.Pow((t - 0.92f) / 0.065f, 2));
                    float edge = MathHelper.SmoothStep(0, 1, Math.Clamp((1 - t) / 0.05f, 0, 1));
                    float lens = (wave.Freeze ? 0.65f : 0.28f) * t * (float)Math.Sqrt(Math.Max(0, 1 - t * t));
                    if (wave.Freeze || wave.Preview)
                    {
                        float phase = (float)Timing.TotalTime;
                        float warp = (float)(Math.Sin(angle * 7 + phase * 3) * Math.Sin(angle * 11 - phase * 2));
                        pos += radial * (warp * 5 * zoom * t * t * edge);
                        lens *= 1 + 0.3f * (float)Math.Sin(angle * 3 + t * 8 - phase * 2);
                    }
                    float ripple = 1 + 0.18f * (float)(Math.Sin(angle * 9 + elapsed * 7)
                        * Math.Sin(angle * 5 - elapsed * 11));
                    strength = (shell * 1.8f * ripple + lens) * edge * fade;
                }
                if (wave.VortexEnd != null)
                {
                    float shell = 0.35f + 0.65f * t;
                    float halfWidth = 35 + 60 * age;
                    pos = centre + (vortexAxis * (radial.X * 18) + vortexSide * (radial.Y * halfWidth)) * (shell * zoom);
                    radial = Vector2.Normalize(vortexAxis * (radial.X / 18) + vortexSide * (radial.Y / halfWidth));
                    strength = crest * (float)Math.Sin(MathHelper.Pi * age);
                }
                if (shield)
                {
                    radial.X *= wave.ShieldDirection;
                    const float peakT = 2f / 3f;
                    const float peakRadius = 0.85f;
                    float shell = t <= peakT
                        ? MathHelper.Lerp(shieldInner, peakRadius, t / peakT)
                        : MathHelper.Lerp(peakRadius, shieldOuter, (t - peakT) / (1 - peakT));
                    pos = centre + new Vector2(radial.X * wave.End.X, radial.Y * wave.End.Y) * (shell * zoom);
                    strength *= (float)Math.Sin(MathHelper.Pi * sector / sectors);
                }
                // Sample farther out to pull the visible scene inward.
                Vector2 sample = pos + radial * ((shield ? -wave.ShieldStrength : wave.RingStrength) * zoom * strength);
                Color tint = Color.White;
                if (wave.Teleport && teleportTime < 0.3f)
                {
                    float core = radius * 0.22f;
                    if (r < core * 0.8f) tint = Color.Black;
                    else if (r < core) tint = new Color(125, 45, 255);
                }
                return new VertexPositionColorTexture(new Vector3(pos, 0), tint,
                    new Vector2(sample.X / viewport.Width, sample.Y / viewport.Height));
            }
            int index = 0;
            int gridCount = (sectors + 1) * (bands + 1);
            if (grid.Length < gridCount) grid = new VertexPositionColorTexture[gridCount];
            for (int s = 0; s <= sectors; s++)
                for (int b = 0; b <= bands; b++) grid[s * (bands + 1) + b] = Vertex(s, b);
            for (int s = 0; s < sectors; s++)
                for (int b = 0; b < bands; b++)
                {
                    int aIndex = s * (bands + 1) + b, eIndex = aIndex + bands + 1;
                    var a = grid[aIndex]; var c = grid[aIndex + 1];
                    var d = grid[eIndex + 1]; var e = grid[eIndex];
                    vertices[index++] = a; vertices[index++] = e; vertices[index++] = c;
                    vertices[index++] = c; vertices[index++] = e; vertices[index++] = d;
                }
            foreach (var pass in effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, count / 3);
            }
        }
    }
}
#endif

