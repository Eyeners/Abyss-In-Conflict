#if CLIENT
using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Collision.Shapes;
using FarseerPhysics;

namespace AIC.Components
{
    internal static class RailImpactVisual
    {
        private sealed record Thread(float Angle, float Length, float Drift, float Seed, Mark[] Path);
        private sealed record Mark(Surface Surface, Vector2 Local);
        private sealed record Impact(Vector2 Point, double Born, EffectAnchor Anchor, Thread[] Threads, Surface Surface);
        private sealed class Surface
        {
            internal Body Body;
            internal Vector2[] Outline;
            internal Vector2 Hit;
            internal float Start, Perimeter;
            internal Vector2 Centre => Outline.Aggregate(Vector2.Zero, (sum, p) => sum + p) / Outline.Length;
            internal Vector2 Interior(float seed)
            {
                int i = (int)(Hash(seed) * Outline.Length) % Outline.Length;
                return Vector2.Lerp(Centre, Vector2.Lerp(Outline[i], Outline[(i + 1) % Outline.Length], Hash(seed + 3)),
                    0.2f + Hash(seed + 7) * 0.55f);
            }
            internal Submarine Sub => Body.UserData is Limb l ? l.character.Submarine
                : Body.UserData is Item i ? i.Submarine : Body.UserData is Structure s ? s.Submarine : null;
            internal Vector2 World(Vector2 local) => ConvertUnits.ToDisplayUnits(Body.GetWorldPoint(local)) + (Sub?.DrawPosition ?? Vector2.Zero);
            internal Vector2 Local(Vector2 world) => Body.GetLocalPoint(ConvertUnits.ToSimUnits(world - (Sub?.DrawPosition ?? Vector2.Zero)));
            internal bool Contains(Vector2 p)
            {
                bool positive = false, negative = false;
                for (int i = 0; i < Outline.Length; i++)
                {
                    Vector2 a = Outline[i], e = Outline[(i + 1) % Outline.Length] - a, d = p - a;
                    float cross = e.X * d.Y - e.Y * d.X;
                    positive |= cross > 0.000001f; negative |= cross < -0.000001f;
                    if (positive && negative) return false;
                }
                return true;
            }
            internal Vector2 Constrain(Vector2 p)
            {
                if (Contains(p)) return p;
                Vector2 nearest = Outline[0]; float best = float.MaxValue;
                for (int i = 0; i < Outline.Length; i++)
                {
                    Vector2 a = Outline[i], e = Outline[(i + 1) % Outline.Length] - a;
                    Vector2 q = a + e * MathHelper.Clamp(Vector2.Dot(p - a, e) / Math.Max(e.LengthSquared(), 0.000001f), 0, 1);
                    float d = Vector2.DistanceSquared(p, q);
                    if (d < best) { nearest = q; best = d; }
                }
                return nearest;
            }
            internal Vector2 Along(float distance)
            {
                float d = ((Start + distance) % Perimeter + Perimeter) % Perimeter;
                for (int i = 0; i < Outline.Length; i++)
                {
                    Vector2 a = Outline[i], b = Outline[(i + 1) % Outline.Length];
                    float length = Vector2.Distance(a, b);
                    if (d <= length) return Vector2.Lerp(a, b, d / Math.Max(length, 0.00001f));
                    d -= length;
                }
                return Outline[0];
            }
            internal static Surface Create(Fixture fixture, Vector2 point)
            {
                Vector2[] outline;
                if (fixture.Shape is PolygonShape polygon) outline = polygon.Vertices.ToArray();
                else if (fixture.Shape is CircleShape circle)
                {
                    outline = new Vector2[32];
                    for (int i = 0; i < outline.Length; i++)
                    {
                        float a = i * MathHelper.TwoPi / outline.Length;
                        outline[i] = circle.Position + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * circle.Radius;
                    }
                }
                else return null;
                var result = new Surface { Body = fixture.Body, Outline = outline };
                result.Hit = fixture.Body.GetLocalPoint(ConvertUnits.ToSimUnits(point - (result.Sub?.Position ?? Vector2.Zero)));
                float best = float.MaxValue, total = 0;
                for (int i = 0; i < outline.Length; i++)
                {
                    Vector2 a = outline[i], edge = outline[(i + 1) % outline.Length] - a;
                    float length = edge.Length();
                    float t = MathHelper.Clamp(Vector2.Dot(result.Hit - a, edge) / Math.Max(edge.LengthSquared(), 0.000001f), 0, 1);
                    float d = Vector2.DistanceSquared(result.Hit, a + edge * t);
                    if (d < best) { best = d; result.Start = total + length * t; }
                    total += length;
                }
                result.Perimeter = total;
                return total > 0.00001f ? result : null;
            }
        }
        private static readonly List<Impact> impacts = new();
        private static readonly VertexPositionColorTexture[] mesh = new VertexPositionColorTexture[6 * 48 * 6 * 3 + 48 * 12];
        private static readonly Vector2[] pathPoints = new Vector2[49];
        private static readonly Vector2[] pathSides = new Vector2[49];
        private static readonly float[] pathFade = new float[49];
        internal static bool Active
        {
            get { impacts.RemoveAll(i => Timing.TotalTime - i.Born >= 1.65 || Timing.TotalTime < i.Born); return impacts.Count > 0; }
        }
        internal static void Clear() => impacts.Clear();
        internal static void Add(Vector2 point, Fixture target)
        {
            var surface = Surface.Create(target, point);
            var candidates = new List<Surface>();
            if (target.Body.UserData is Limb hit)
                foreach (var limb in hit.character.AnimController.Limbs)
                {
                    if (limb == hit || limb.IsSevered || limb.body == null) continue;
                    var fixture = limb.body.FarseerBody.FixtureList.FirstOrDefault();
                    if (fixture == null) continue;
                    var other = Surface.Create(fixture, limb.WorldPosition);
                    if (other != null) candidates.Add(other);
                }
            var strands = new Thread[6];
            float angle = Rand.Range(0f, MathHelper.TwoPi);
            for (int i = 0; i < strands.Length; i++)
            {
                float seed = Rand.Range(0f, 100f);
                var marks = new List<Mark>();
                if (surface != null)
                {
                    var current = surface;
                    Vector2 position = current.World(current.Constrain(current.Hit));
                    marks.Add(new Mark(current, current.Local(position)));
                    float heading = angle + i * MathHelper.TwoPi / 6;
                    // Constant world-space steps and a fixed speed: a larger target only
                    // provides more room, never a longer/faster normalized path.
                    for (int step = 0; step < 40; step++)
                    {
                        float desired = heading + (Noise(step * 0.23f, seed) - 0.5f) * 1.2f;
                        bool moved = false;
                        for (int turn = 0; turn < 13; turn++)
                        {
                            float candidateAngle = desired + (turn % 2 == 0 ? 1 : -1) * ((turn + 1) / 2) * 0.48f;
                            Vector2 nextPoint = position + new Vector2((float)Math.Cos(candidateAngle), (float)Math.Sin(candidateAngle)) * 3;
                            Surface owner = current.Contains(current.Local(nextPoint)) ? current
                                : candidates.FirstOrDefault(s => s.Contains(s.Local(nextPoint)));
                            if (owner == null) continue;
                            current = owner; position = nextPoint; heading = candidateAngle;
                            marks.Add(new Mark(current, current.Local(position)));
                            moved = true; break;
                        }
                        if (!moved) break;
                    }
                }
                strands[i] = new Thread(angle + i * MathHelper.TwoPi / 6 + Rand.Range(-0.35f, 0.35f),
                    Rand.Range(80f, 145f), Rand.Range(20f, 42f), seed, marks.ToArray());
            }
            impacts.Add(new Impact(point, Timing.TotalTime, EffectAnchor.At(point), strands, surface));
        }
        private static float Hash(float value) { double n = Math.Sin(value * 127.1) * 43758.5453; return (float)(n - Math.Floor(n)); }
        private static float Noise(float x, float seed)
        {
            float floor = (float)Math.Floor(x), f = x - floor;
            return MathHelper.Lerp(Hash(floor + seed), Hash(floor + 1 + seed), f * f * (3 - 2 * f));
        }
        internal static void Draw(GameScreen screen, GraphicsDevice graphics, BasicEffect effect)
        {
            if (impacts.Count == 0) return;
            effect.TextureEnabled = false;
            graphics.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.Additive;
            foreach (var impact in impacts)
            {
                float age = (float)(Timing.TotalTime - impact.Born), life = age / 1.65f;
                var surface = impact.Surface;
                Vector2 world = surface != null ? surface.World(surface.Hit) : impact.Point + impact.Anchor.DrawOffset;
                Vector2 centre = screen.Cam.WorldToScreen(world);
                float zoom = Vector2.Distance(centre, screen.Cam.WorldToScreen(world + Vector2.UnitX));
                int count = 0;
                void V(Vector2 p, Color color) => mesh[count++] = new VertexPositionColorTexture(new Vector3(p, 0), color, Vector2.Zero);
                // Small blue skirt and white centre; fading, not an expanding blast.
                float core = 1 - MathHelper.SmoothStep(0, 1, MathHelper.Clamp((age - 0.16f) / 0.6f, 0, 1));
                for (int i = 0; i < 48; i++)
                {
                    float a = i * MathHelper.TwoPi / 48, b = (i + 1) * MathHelper.TwoPi / 48;
                    Vector2 A(float r) => centre + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * r * zoom;
                    Vector2 B(float r) => centre + new Vector2((float)Math.Cos(b), (float)Math.Sin(b)) * r * zoom;
                    V(centre, new Color(60, 120, 255) * (core * 0.65f)); V(A(30), Color.Transparent); V(B(30), Color.Transparent);
                    V(centre, Color.White * core); V(A(12), Color.White * (core * 0.9f)); V(B(12), Color.White * (core * 0.9f));
                    V(A(12), Color.White * (core * 0.9f)); V(A(20), new Color(40, 100, 255) * (core * 0.15f)); V(B(12), Color.White * (core * 0.9f));
                    V(B(12), Color.White * (core * 0.9f)); V(A(20), new Color(40, 100, 255) * (core * 0.15f)); V(B(20), new Color(40, 100, 255) * (core * 0.15f));
                }
                foreach (var strand in impact.Threads)
                {
                    Vector2 outward = new Vector2((float)Math.Cos(strand.Angle), (float)Math.Sin(strand.Angle));
                    Vector2 across = new Vector2(-outward.Y, outward.X);
                    float growthTime = strand.Path.Length > 1 ? (strand.Path.Length - 1) * 3f / 180f
                        : 0.55f + Hash(strand.Seed + 91) * 0.2f;
                    float Born(float u) => u * growthTime;
                    Vector2 P(float u)
                    {
                        // Reveal a fixed-length noisy path from root to tip. Older sections
                        // drift while new sections are drawn; the existing trail never stretches.
                        float sectionAge = Math.Max(0, age - Born(u));
                        if (strand.Path.Length > 1)
                        {
                            float progress = u * (strand.Path.Length - 1);
                            int index = Math.Min(strand.Path.Length - 2, (int)progress);
                            float blend = progress - index;
                            var first = strand.Path[index]; var second = strand.Path[index + 1];
                            Vector2 W(Mark mark, int anchorIndex)
                            {
                                // Small, fixed-unit crawling displacement constrained to the
                                // material region; never interpolate toward a distant body centre.
                                float span = 2 + Math.Min(sectionAge, 0.7f) * 7;
                                Vector2 wander = new Vector2(Noise(anchorIndex * 0.24f - age * 1.4f, strand.Seed + 41) - 0.5f,
                                    Noise(anchorIndex * 0.31f + age, strand.Seed + 91) - 0.5f) * span;
                                return mark.Surface.World(mark.Surface.Constrain(mark.Local + ConvertUnits.ToSimUnits(wander)));
                            }
                            return screen.Cam.WorldToScreen(Vector2.Lerp(W(first, index), W(second, index + 1), blend));
                        }
                        float distance = 14 + sectionAge * strand.Drift * 0.45f + u * strand.Length;
                        float bend = (Noise(u * 4 - age * 1.3f, strand.Seed) - 0.5f) * (32 + u * 42 + sectionAge * 20)
                            + (Noise(u * 11 - age * 2.1f, strand.Seed + 71) - 0.5f) * 9;
                        float crawl = (Noise(u * 6 + age, strand.Seed + 41) - 0.5f) * 14;
                        return centre + (outward * (distance + crawl) + across * bend) * zoom;
                    }
                    float Remaining(float u)
                    {
                        float sectionAge = age - Born(u);
                        if (sectionAge <= 0) return 0;
                        float appear = MathHelper.SmoothStep(0, 1, Math.Min(1, sectionAge / 0.035f));
                        // Tail sections age first. Stable patch lifetimes open gaps as the
                        // head advances, then the last fragments fade after growth stops.
                        float start = 0.18f + Noise(u * 9, strand.Seed + 17) * 0.32f;
                        float disappear = MathHelper.SmoothStep(0, 1, MathHelper.Clamp((sectionAge - start) / 0.3f, 0, 1));
                        return appear * (1 - disappear);
                    }
                    // Geometry and erosion are shared by all three brightness layers.
                    for (int s = 0; s <= 48; s++)
                    {
                        float t = s / 48f;
                        pathPoints[s] = P(t);
                        Vector2 d = P(Math.Min(1, t + 0.015f)) - P(Math.Max(0, t - 0.015f));
                        pathSides[s] = new Vector2(-d.Y, d.X) / Math.Max(0.001f, d.Length());
                        pathFade[s] = Remaining(t) * (1 - t * t * 0.65f);
                    }
                    for (int layer = 0; layer < 3; layer++)
                    for (int s = 0; s < 48; s++)
                    {
                        float u = s / 48f, v = (s + 1) / 48f;
                        Vector2 a = pathPoints[s], b = pathPoints[s + 1];
                        float fa = pathFade[s], fb = pathFade[s + 1];
                        if (fa <= 0 && fb <= 0) continue;
                        float Width(float t)
                        {
                            float haze = Noise(t * 6, strand.Seed + 53);
                            float spreading = MathHelper.Clamp((age - Born(t)) / 0.65f, 0, 1);
                            return (layer == 0 ? 4 + 14 * haze * spreading
                                : layer == 1 ? 1.4f + haze * (1 + spreading)
                                : (0.65f + 0.45f * (1 - haze)) * (1 - 0.65f * spreading)) * zoom;
                        }
                        Vector2 sa = pathSides[s] * Width(u) * (layer == 0 ? (float)Math.Sqrt(fa) : fa);
                        Vector2 sb = pathSides[s + 1] * Width(v) * (layer == 0 ? (float)Math.Sqrt(fb) : fb);
                        float sectionLife = Math.Max(0, age - Born((u + v) * 0.5f));
                        Color tint = sectionLife < 0.25f ? Color.Lerp(Color.White, new Color(32, 71, 255), sectionLife / 0.25f)
                            : Color.Lerp(new Color(32, 71, 255), new Color(30, 56, 119), Math.Min(1, (sectionLife - 0.25f) / 0.4f));
                        tint *= layer == 0 ? 0.065f : layer == 1 ? 0.16f : 0.85f;
                        V(a - sa, tint * fa); V(b - sb, tint * fb); V(a + sa, tint * fa);
                        V(a + sa, tint * fa); V(b - sb, tint * fb); V(b + sb, tint * fb);
                    }
                }
                foreach (var pass in effect.CurrentTechnique.Passes)
                { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, count / 3); }
            }
            effect.TextureEnabled = true;
        }
    }
}
#endif
