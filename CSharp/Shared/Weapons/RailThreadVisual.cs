using Barotrauma;
using Barotrauma.Items.Components;
#if CLIENT
using System;
using System.Collections.Generic;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    // Visual-only component: damage and endpoints remain native Projectile hitscan.
    internal sealed class RailThreadVisual : ItemComponent
    {
        [Serialize(3, IsPropertySaveable.No)] public int ThreadsPerGroup { get; set; }
        [Serialize(15f, IsPropertySaveable.No)] public float ThreadAmplitude { get; set; }
        [Serialize(8f, IsPropertySaveable.No)] public float CoreHalfWidth { get; set; }
        [Serialize(2f, IsPropertySaveable.No)] public float ThreadHalfWidth { get; set; }
#if CLIENT
        internal readonly Sprite ThreadSprite;
        internal bool ImpactEmitted;
#endif
        public RailThreadVisual(Item item, ContentXElement element) : base(item, element)
        {
#if CLIENT
            ThreadSprite = new Sprite(element.GetChildElement("sprite"));
#endif
        }
        protected override void RemoveComponentSpecific()
        {
#if CLIENT
            // Queued beams own their reference until the short visual lifetime ends.
            RailThreadRenderer.Retire(ThreadSprite);
#endif
            base.RemoveComponentSpecific();
        }
    }
#if CLIENT
    public sealed class RailThreadRenderer : IAssemblyPlugin
    {
        private sealed record Strand(float Amplitude, float Frequency, float Phase, float Drift);
        private sealed record Beam(Vector2 Start, Vector2 End, double Born, Sprite Sprite, Strand[] Strands,
            float CoreHalfWidth, float ThreadHalfWidth, EffectAnchor Anchor);
        private static readonly List<Beam> beams = new();
        private static readonly List<Sprite> retired = new();
        private static VertexPositionColorTexture[] vertices = Array.Empty<VertexPositionColorTexture>();
        private static SamplerState threadSampler;
        // Smooth seeded noise, continuous in both world distance and time; no per-frame rerolls.
        private static float Noise(float x, float y, float seed)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float Hash(int a, int b)
            {
                double value = Math.Sin(a * 127.1 + b * 311.7 + seed * 73.3) * 43758.5453;
                return (float)(2 * (value - Math.Floor(value)) - 1);
            }
            return MathHelper.Lerp(MathHelper.Lerp(Hash(ix, iy), Hash(ix + 1, iy), fx),
                MathHelper.Lerp(Hash(ix, iy + 1), Hash(ix + 1, iy + 1), fx), fy);
        }
        private Harmony harmony;
        internal static void Retire(Sprite sprite) { if (sprite != null) retired.Add(sprite); }
        internal static bool HasBeams
        {
            get
            {
                beams.RemoveAll(b => Timing.TotalTime - b.Born >= 1 || Timing.TotalTime < b.Born);
                for (int i = retired.Count - 1; i >= 0; i--)
                    if (!beams.Exists(b => b.Sprite == retired[i]))
                    { retired[i].Remove(); retired.RemoveAt(i); }
                return RailImpactVisual.Active || beams.Count > 0;
            }
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.rail.thread.visual");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "LaunchProjSpecific"),
                postfix: new HarmonyMethod(typeof(RailThreadRenderer), nameof(Launched)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                postfix: new HarmonyMethod(typeof(RailThreadRenderer), nameof(Impact)));
        }
        private static void Impact(Projectile __instance, FarseerPhysics.Dynamics.Fixture target, bool __result)
        {
            var visual = __instance.Item.GetComponent<RailThreadVisual>();
            if (!__result || visual == null || visual.ImpactEmitted) return;
            visual.ImpactEmitted = true;
            RailImpactVisual.Add(__instance.Item.WorldPosition, target);
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf(); beams.Clear();
            RailImpactVisual.Clear();
            foreach (var sprite in retired) sprite.Remove();
            retired.Clear(); vertices = Array.Empty<VertexPositionColorTexture>();
            threadSampler?.Dispose(); threadSampler = null;
        }
        private static void Launched(Projectile __instance, Vector2 __0, Vector2 __1)
        {
            var visual = __instance.Item.GetComponent<RailThreadVisual>();
            if (visual == null || Vector2.DistanceSquared(__0, __1) < 1) return;
            // Three groups, configurable strands per group. This is a 2D approximation,
            // not an assertion that SAGE BurstCount denotes full independent ribbons.
            var strands = new Strand[3 * Math.Max(0, visual.ThreadsPerGroup)];
            for (int i = 0; i < strands.Length; i++)
                strands[i] = new Strand(visual.ThreadAmplitude * Rand.Range(2f / 3f, 1f),
                    Rand.Range(1.5f, 3.8f), Rand.Range(0f, MathHelper.TwoPi), Rand.Range(-5f, 5f));
            beams.Add(new Beam(__0, __1, Timing.TotalTime, visual.ThreadSprite, strands,
                visual.CoreHalfWidth, visual.ThreadHalfWidth, EffectAnchor.At(__0)));
        }
        internal static void Draw(GameScreen screen, GraphicsDevice graphics, BasicEffect effect)
        {
            graphics.BlendState = BlendState.Additive;
            threadSampler ??= new SamplerState { Filter = TextureFilter.Linear,
                AddressU = TextureAddressMode.Mirror, AddressV = TextureAddressMode.Clamp };
            graphics.SamplerStates[0] = threadSampler;
            foreach (var beam in beams)
            {
                float age = (float)(Timing.TotalTime - beam.Born);
                Vector2 offset = beam.Anchor.DrawOffset;
                Vector2 start = screen.Cam.WorldToScreen(beam.Start + offset), end = screen.Cam.WorldToScreen(beam.End + offset);
                Vector2 axis = end - start;
                float length = axis.Length();
                if (length < 1) continue;
                float worldLength = Vector2.Distance(beam.Start, beam.End);
                float zoom = length / worldLength;
                int segments = Math.Max(2, (int)Math.Ceiling(worldLength / 12f));
                axis /= length;
                Vector2 normal = new Vector2(-axis.Y, axis.X);
                int count = (beam.Strands.Length + 1) * segments * 6 * 2;
                if (vertices.Length < count) vertices = new VertexPositionColorTexture[count];
                int index = 0;
                // A broad low-intensity ribbon gives each path a soft skirt; the narrow
                // pass keeps the centre legible without turning every strand into a wire.
                for (int layer = 0; layer < 2; layer++)
                for (int strandIndex = -1; strandIndex < beam.Strands.Length; strandIndex++)
                {
                    bool core = strandIndex < 0;
                    var strand = core ? null : beam.Strands[strandIndex];
                    float coreFade = 1 - MathHelper.SmoothStep(0, 1, MathHelper.Clamp((age - 0.3f) / 0.6f, 0, 1));
                    Color tint = core ? Color.White * coreFade
                        : age < 0.25f ? Color.Lerp(Color.White, new Color(32, 71, 255), age / 0.25f)
                        : age < 0.5f ? Color.Lerp(new Color(32, 71, 255), new Color(30, 56, 119), (age - 0.25f) / 0.25f)
                        : new Color(30, 56, 119) * Math.Max(0, (0.9f - age) / 0.4f);
                    if (!core) tint *= layer == 0 ? 0.42f : 0.65f;
                    else if (layer == 0) tint *= 0.42f;
                    float halfWidth = (core ? beam.CoreHalfWidth : beam.ThreadHalfWidth * (1 + age)) * zoom;
                    if (layer == 0) halfWidth *= core ? 3f : 4f;
                    Vector2 Centre(float u)
                    {
                        if (core) return Vector2.Lerp(start, end, u);
                        float distance = u * worldLength;
                        float envelope = MathHelper.SmoothStep(0, 1, Math.Min(distance, worldLength - distance) / 40f);
                        float coordinate = distance / (75f + strand.Frequency * 22f);
                        float time = age * (0.7f + strand.Frequency * 0.35f);
                        float spread = 0.3f + age * (0.8f + strand.Frequency * 0.25f);
                        float offset = strand.Amplitude * spread * Noise(coordinate, time, strand.Phase)
                            + strand.Amplitude * 0.22f * Noise(distance / 48f, time * 1.3f, strand.Phase + 11)
                            + strand.Drift * age;
                        return Vector2.Lerp(start, end, u) + normal * (offset * envelope * zoom);
                    }
                    float Remaining(float t)
                    {
                        if (core) return 1;
                        // Spatially stable thresholds: each patch dissolves once, without flickering back.
                        float distance = t * worldLength;
                        float variation = 0.5f + 0.5f * Noise(distance / 65f, 0, strand.Phase + 37);
                        float startFade = 0.2f + variation * 0.48f;
                        return 1 - MathHelper.SmoothStep(0, 1, (age - startFade) / 0.18f);
                    }
                    for (int s = 0; s < segments; s++)
                    {
                        float u = s / (float)segments, v = (s + 1) / (float)segments;
                        Vector2 a = Centre(u), b = Centre(v);
                        Vector2 Side(float t)
                        {
                            float step = 1f / segments;
                            Vector2 tangent = Vector2.Normalize(Centre(Math.Min(1, t + step)) - Centre(Math.Max(0, t - step)));
                            return new Vector2(-tangent.Y, tangent.X) * halfWidth;
                        }
                        float remainingA = Remaining(u), remainingB = Remaining(v);
                        Vector2 sideA = Side(u) * remainingA, sideB = Side(v) * remainingB;
                        // The core samples the bright central strip; threads tile at a fixed world scale.
                        float texA = core ? 0.5f : u * worldLength / 256f + strand.Phase;
                        float texB = core ? 0.5f : v * worldLength / 256f + strand.Phase;
                        float EndFade(float t) => MathHelper.SmoothStep(0, 1, Math.Min(t, 1 - t) * worldLength / 10f);
                        Color colorA = tint * (EndFade(u) * remainingA), colorB = tint * (EndFade(v) * remainingB);
                        var al = new VertexPositionColorTexture(new Vector3(a - sideA, 0), colorA, new Vector2(texA, 0));
                        var ar = new VertexPositionColorTexture(new Vector3(a + sideA, 0), colorA, new Vector2(texA, 1));
                        var bl = new VertexPositionColorTexture(new Vector3(b - sideB, 0), colorB, new Vector2(texB, 0));
                        var br = new VertexPositionColorTexture(new Vector3(b + sideB, 0), colorB, new Vector2(texB, 1));
                        vertices[index++] = al; vertices[index++] = bl; vertices[index++] = ar;
                        vertices[index++] = ar; vertices[index++] = bl; vertices[index++] = br;
                    }
                }
                effect.Texture = beam.Sprite.Texture;
                foreach (var pass in effect.CurrentTechnique.Passes)
                { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, vertices, 0, index / 3); }
            }
            RailImpactVisual.Draw(screen, graphics, effect);
        }
    }
#endif
}
