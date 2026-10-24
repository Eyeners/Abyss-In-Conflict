#if CLIENT
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Collision.Shapes;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    // Limb-local paths: historical tail samples follow the current limb pose too.
    public sealed class NaniteInfectionVisual : IAssemblyPlugin
    {
        private static readonly Identifier Infection = "aic_nanite_infection".ToIdentifier();
        private Harmony harmony;
        private static readonly List<Limb> region = new();
        public void Initialize()
        {
            harmony = new Harmony("aic.nanite.infection.visual");
            harmony.Patch(AccessTools.Method(typeof(Limb), "Draw", new[] { typeof(SpriteBatch), typeof(Camera), typeof(Color?), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(NaniteInfectionVisual), nameof(Draw)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() => harmony?.UnpatchSelf();
        private static float Hash(float x) { double n = Math.Sin(x * 127.1) * 43758.5453; return (float)(n - Math.Floor(n)); }
        private static float Noise(float t, float seed)
        {
            float n = (float)Math.Floor(t), f = t - n;
            return MathHelper.Lerp(Hash(n + seed), Hash(n + seed + 1), f * f * (3 - 2 * f));
        }
        private static void Draw(Limb __instance, SpriteBatch __0)
        {
            var limb = __instance;
            if (limb.IsSevered || limb.body == null || limb.character?.CharacterHealth == null) return;
            float strength = limb.character.CharacterHealth.GetAffliction(Infection, limb)?.Strength ?? 0;
            if (strength < 1 || limb.body.FarseerBody.FixtureList.Count == 0) return;
            region.Clear();
            foreach (var member in limb.character.AnimController.Limbs)
            {
                if (member.HealthIndex != limb.HealthIndex || member.IsSevered || member.body == null
                    || member.body.FarseerBody.FixtureList.Count == 0) continue;
                var shape = member.body.FarseerBody.FixtureList[0].Shape;
                if (shape is PolygonShape || shape is CircleShape) region.Add(member);
            }
            if (region.Count == 0) return;
            int count = strength < 30 ? 4 : strength < 60 ? 9 : 16;
            float now = (float)(Timing.TotalTime % 10000);
            for (int i = 0; i < count; i++)
            {
                // Every physical limb evaluates the same regional particles, but only
                // their current owner draws them. The budget is never multiplied by limbs.
                float seed = limb.character.ID * 0.017f + limb.HealthIndex * 137.3f + i * 19.7f;
                int Index(int step)
                {
                    if (region.Count == 1) return 0;
                    int period = 2 * (region.Count - 1), value = (step % period + period) % period;
                    return value < region.Count ? value : period - value;
                }
                float Phase(float time) => time * (0.22f + Hash(seed + 5) * 0.18f) + Hash(seed + 9) * 10;
                float phase = Phase(now);
                int step = (int)Math.Floor(phase);
                if (region[Index(step + (phase - step >= 0.75f ? 1 : 0))] != limb) continue;
                Vector2 Position(float time)
                {
                    float p = Phase(time); int s = (int)Math.Floor(p);
                    float blend = MathHelper.SmoothStep(0, 1, MathHelper.Clamp((p - s - 0.5f) * 2, 0, 1));
                    return Vector2.Lerp(Point(region[Index(s)], time, seed), Point(region[Index(s + 1)], time, seed), blend);
                }
                const float trailDuration = 0.55f;
                const int trailSegments = 10;
                Vector2 last = Position(now - trailDuration);
                for (int j = 1; j <= trailSegments; j++)
                {
                    float progress = j / (float)trailSegments;
                    Vector2 p = Position(now - trailDuration + progress * trailDuration);
                    // Keep a readable grey trail against dark clothing; the old
                    // subpixel, low-alpha 0.2s line was overwhelmed by the white head.
                    GUI.DrawLine(__0, last, p, new Color(175, 180, 185) * (progress * 0.85f), width: 1.2f);
                    last = p;
                }
                GUI.DrawRectangle(__0, new Rectangle((int)(last.X - 1), (int)(last.Y - 1), 2, 2), new Color(225, 230, 232), isFilled: true);
            }
        }
        private static Vector2 Point(Limb limb, float time, float seed)
        {
            var shape = limb.body.FarseerBody.FixtureList[0].Shape;
            float t = time * (0.8f + Hash(seed + 4));
            Vector2 local;
            if (shape is CircleShape circle)
            {
                float angle = Noise(t, seed) * MathHelper.TwoPi * 2;
                float radius = circle.Radius * (0.1f + Noise(t + 8, seed + 17) * 0.65f);
                local = circle.Position + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius;
            }
            else
            {
                var polygon = (PolygonShape)shape;
                Vector2 centre = Vector2.Zero;
                foreach (var vertex in polygon.Vertices) centre += vertex;
                centre /= polygon.Vertices.Count;
                Vector2 sum = centre * 0.8f; float weight = 0.8f;
                for (int v = 0; v < polygon.Vertices.Count; v++)
                {
                    float w = Noise(t, seed + v * 13.1f);
                    w = w * w * w * w;
                    sum += polygon.Vertices[v] * w; weight += w;
                }
                local = sum / weight;
            }
            Vector2 world = ConvertUnits.ToDisplayUnits(limb.body.FarseerBody.GetWorldPoint(local))
                + (limb.character.Submarine?.DrawPosition ?? Vector2.Zero);
            return new Vector2(world.X, -world.Y);
        }
    }
}
#endif
