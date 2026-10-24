using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed class RiftCascadeTest : ItemComponent, IServerSerializable
    {
        [Serialize(false, IsPropertySaveable.No)] public bool Node { get; set; }
        private readonly Queue<(Vector2 Position, Limb Limb, Vector2 Offset)> points = new();
        private Limb following;
        private Vector2 limbOffset;
        internal Item Previous;
        private Item lastNode;
        private float elapsed, next;
        private bool started;
        private Submarine anchor;
        public RiftCascadeTest(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        internal void Hit(Fixture target, Vector2 normal)
        {
            if (Node || started || GameMain.NetworkMember?.IsClient == true) return;
            started = true;
            anchor = Item.Submarine;
            Vector2 hit = Item.WorldPosition;
            var hitLimb = target.Body.UserData as Limb;
            points.Enqueue((hit - (anchor?.Position ?? Vector2.Zero), hitLimb, hitLimb == null ? Vector2.Zero : hit - hitLimb.WorldPosition));
            var character = (target.Body.UserData as Limb)?.character;
            if (character != null)
            {
                Vector2 previous = hit;
                var limbs = character.AnimController.Limbs.Where(l => l != hitLimb && !l.IsSevered && l.body != null).ToList();
                for (int i = 0; i < 3 && limbs.Count > 0; i++)
                {
                    var limb = limbs.OrderBy(l => Vector2.DistanceSquared(previous, l.WorldPosition)).First();
                    limbs.Remove(limb);
                    Vector2 outward = limb.WorldPosition - character.WorldPosition;
                    if (outward.LengthSquared() < 1) outward = normal;
                    if (outward.LengthSquared() < 1) outward = Vector2.UnitY;
                    outward.Normalize();
                    previous = limb.WorldPosition + outward * 16;
                    points.Enqueue((previous - (anchor?.Position ?? Vector2.Zero), limb, outward * 16));
                }
            }
            else
            {
                if (normal.LengthSquared() < 0.01f) normal = Vector2.UnitY;
                normal.Normalize();
                Vector2 tangent = new Vector2(-normal.Y, normal.X);
                float positive = 0, negative = 0;
                bool firstSide = Rand.Range(0f, 1f) > 0.5f;
                for (int i = 1; i <= 3; i++)
                {
                    bool side = i == 1 ? firstSide : i == 2 ? !firstSide : Rand.Range(0f, 1f) > 0.5f;
                    float step = Rand.Range(35f, 80f);
                    if (side) positive += step; else negative += step;
                    points.Enqueue((hit + tangent * (side ? positive : -negative) + normal * Rand.Range(3f, 15f)
                        - (anchor?.Position ?? Vector2.Zero), null, Vector2.Zero));
                }
            }
        }
        public override void Update(float deltaTime, Camera cam)
        {
            if (Item.ParentInventory != null) return;
            if (Node)
            {
                if (following != null && !following.character.Removed && following.body != null)
                    Item.SetTransform(FarseerPhysics.ConvertUnits.ToSimUnits(following.WorldPosition + limbOffset
                        - (following.character.Submarine?.Position ?? Vector2.Zero)), 0, forceSubmarine: following.character.Submarine);
#if CLIENT
                if (!started) { started = true; RiftCascadeDrawing.Add(Item); }
#endif
                elapsed += deltaTime;
                if (elapsed >= 0.5f)
                {
#if CLIENT
                    SonicRefraction.AddRiftExplosion(Item.WorldPosition);
#endif
                    ApplyStatusEffects(ActionType.OnActive, deltaTime);
                    IsActive = false;
                }
                return;
            }
            if (!started || points.Count == 0) return;
            elapsed += deltaTime;
            if (elapsed < next) return;
            next += 0.1f;
            var prefab = ItemPrefab.Find(null, "aic_rift_test_node".ToIdentifier());
            var point = points.Dequeue();
            var spawnSub = point.Limb?.character.Submarine ?? anchor;
            Vector2 spawnPoint = point.Limb == null ? point.Position : point.Limb.WorldPosition + point.Offset - (spawnSub?.Position ?? Vector2.Zero);
            Entity.Spawner.AddItemToSpawnQueue(prefab, spawnPoint, spawnSub, onSpawned: node =>
            {
                var component = node.GetComponent<RiftCascadeTest>();
                component.following = point.Limb;
                component.limbOffset = point.Offset;
                component.Previous = lastNode;
                lastNode = node;
#if SERVER
                node.CreateServerEvent(component);
#endif
            });
        }
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            var character = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            int index = msg.ReadInt32();
            following = character != null && index >= 0 && index < character.AnimController.Limbs.Length ? character.AnimController.Limbs[index] : null;
            limbOffset = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            Previous = Entity.FindEntityByID(msg.ReadUInt16()) as Item;
        }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteUInt16(following?.character.ID ?? (ushort)0);
            msg.WriteInt32(following == null ? -1 : Array.IndexOf(following.character.AnimController.Limbs, following));
            msg.WriteSingle(limbOffset.X); msg.WriteSingle(limbOffset.Y);
            msg.WriteUInt16(Previous?.ID ?? (ushort)0);
        }
#endif
    }
    public sealed class RiftCascadeTestPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.rift.cascade.test");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                postfix: new HarmonyMethod(typeof(RiftCascadeTestPlugin), nameof(Hit)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(Projectile), "LaunchProjSpecific"),
                postfix: new HarmonyMethod(typeof(RiftCascadeTestPlugin), nameof(Launch)));
#endif
        }
#if CLIENT
        private static void Launch(Projectile __instance, Vector2 __0, Vector2 __1)
        {
            if (__instance.Item.GetComponent<RiftCascadeTest>() is { Node: false })
                RiftCascadeDrawing.Beam(__0, __1);
        }
#endif
        private static void Hit(Projectile __instance, Fixture target, Vector2 collisionNormal, bool __result)
        { if (__result) __instance.Item.GetComponent<RiftCascadeTest>()?.Hit(target, collisionNormal); }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
#if CLIENT
            RiftCascadeDrawing.Clear();
#endif
        }
    }
#if CLIENT
    internal static class RiftCascadeDrawing
    {
        private sealed record Rift(Vector2 Point, Vector2 From, double Born, float Seed, EffectAnchor Anchor, bool IsBeam = false, Item NodeItem = null);
        private static readonly List<Rift> rifts = new();
        private static readonly VertexPositionColorTexture[] mesh = new VertexPositionColorTexture[96 * 9];
        internal static void Clear() => rifts.Clear();
        internal static void Beam(Vector2 start, Vector2 end) => rifts.Add(new Rift(end, start, Timing.TotalTime, 0, EffectAnchor.At(start), true));
        internal static bool Active
        {
            get { rifts.RemoveAll(r => Timing.TotalTime - r.Born > (r.IsBeam ? 0.5 : 3.2) || Timing.TotalTime < r.Born); return rifts.Count > 0; }
        }
        internal static void Add(Item item)
        {
            Vector2 p = item.WorldPosition;
            rifts.Add(new Rift(p, p, Timing.TotalTime, Rand.Range(0f, 100f), EffectAnchor.At(p), NodeItem: item));
        }
        private static float Noise(float angle, float time, float seed)
        {
            float sample = angle / MathHelper.TwoPi * 32;
            int x = (int)Math.Floor(sample), y = (int)Math.Floor(time * 9);
            float u = sample - x, v = time * 9 - y;
            u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
            float H(int a, int b) => Hash(((a % 32 + 32) % 32) * 17 + b * 137 + seed) * 2 - 1;
            return MathHelper.Lerp(MathHelper.Lerp(H(x, y), H(x + 1, y), u), MathHelper.Lerp(H(x, y + 1), H(x + 1, y + 1), u), v) * 0.3f;
        }
        private static float Hash(float seed) { double n = Math.Sin(seed * 127.1) * 43758.5453; return (float)(n - Math.Floor(n)); }
        internal static void Draw(GameScreen screen, GraphicsDevice graphics, BasicEffect effect)
        {
            effect.TextureEnabled = false;
            foreach (var r in rifts)
            {
                float age = (float)(Timing.TotalTime - r.Born);
                Vector2 world = r.NodeItem != null ? r.NodeItem.WorldPosition : r.Point + r.Anchor.DrawOffset;
                Vector2 centre = screen.Cam.WorldToScreen(world);
                float zoom = Vector2.Distance(centre, screen.Cam.WorldToScreen(world + Vector2.UnitX));
                if (age < 0.5f)
                {
                    var previous = r.NodeItem?.GetComponent<RiftCascadeTest>()?.Previous;
                    Vector2 from = screen.Cam.WorldToScreen(previous != null ? previous.WorldPosition : r.From + r.Anchor.DrawOffset);
                    Vector2 delta = centre - from;
                    if (delta.LengthSquared() > 1)
                    {
                        Vector2 side = Vector2.Normalize(new Vector2(-delta.Y, delta.X));
                        Vector2 last = from;
                        int arcCount = 0;
                        for (int i = 1; i <= 12; i++)
                        {
                            float u = i / 12f;
                            Vector2 p = Vector2.Lerp(from, centre, u) + side * (i == 12 || r.IsBeam ? 0 : Noise(i, (float)Math.Floor(age * 20), r.Seed) * 65 * zoom);
                            Strip(last, p, side * (r.IsBeam ? 15 : 10) * zoom, new Color(100, 60, 255) * 0.25f * (1 - age / 0.5f), ref arcCount);
                            Strip(last, p, side * (r.IsBeam ? 7 : 5) * zoom, new Color(130, 80, 255) * (1 - age / 0.5f), ref arcCount);
                            Strip(last, p, side * (r.IsBeam ? 2.8f : 1.8f) * zoom, Color.White * (1 - age / 0.5f), ref arcCount);
                            last = p;
                        }
                        graphics.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.Additive;
                        foreach (var pass in effect.CurrentTechnique.Passes)
                        { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, arcCount / 3); }
                    }
                    continue;
                }
                if (r.IsBeam) continue;
                float t = age - 0.5f;
                Clouds(centre, t, r.Seed, zoom, graphics, effect);
                for (int l = 0; l < 3; l++)
                {
                    float seed = r.Seed + l * 31.7f;
                    float localTime = t - (l == 0 ? 0 : Hash(seed + 3) * 0.16f);
                    if (localTime < 0) continue;
                    float life = 0.65f + Hash(seed + 1) * 0.65f;
                    float f = localTime < 0.1f ? localTime / 0.1f : Math.Max(0, 1 - (localTime - 0.1f) / life);
                    if (f <= 0) continue;
                    float radius = (32 + Hash(seed + 2) * 25) * (localTime < 0.1f ? 0.75f + 0.25f * (float)Math.Sqrt(f) : 0.1f + 0.9f * f) * zoom;
                    Vector2 shift = new Vector2(Hash(seed + 4) - 0.5f, Hash(seed + 5) - 0.5f) * 48;
                    Vector2 c = centre + shift * (1 - localTime * 0.35f) * zoom;
                    int n = 0;
                    for (int i = 0; i < 96; i++)
                    {
                        float a = i * MathHelper.TwoPi / 96, b = (i + 1) * MathHelper.TwoPi / 96;
                        Vector2 A(float angle, float extra) => c + new Vector2((float)Math.Cos(angle) * (0.8f + Hash(seed + 8) * 0.4f), (float)Math.Sin(angle)) * (radius * (1 + Noise(angle, localTime, seed)) + extra);
                        void V(Vector2 p, Color color) => mesh[n++] = new VertexPositionColorTexture(new Vector3(p, 0), color, Vector2.Zero);
                        Color purple = new Color(120, 65, 255) * Math.Min(1, f * 3);
                        V(A(a, 0), purple); V(A(b, 0), purple); V(A(a, 55 * zoom), Color.Transparent);
                        V(A(a, 55 * zoom), Color.Transparent); V(A(b, 0), purple); V(A(b, 55 * zoom), Color.Transparent);
                        V(c, Color.Black * Math.Min(1, f * 4)); V(A(a, 0), Color.Black * Math.Min(1, f * 4)); V(A(b, 0), Color.Black * Math.Min(1, f * 4));
                    }
                    graphics.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.NonPremultiplied;
                    foreach (var pass in effect.CurrentTechnique.Passes) { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, n / 3); }
                }
            }
            effect.TextureEnabled = true;
        }
        private static readonly VertexPositionColorTexture[] cloudMesh = new VertexPositionColorTexture[18 * 32 * 9];
        private static void Clouds(Vector2 centre, float age, float seed, float zoom, GraphicsDevice graphics, BasicEffect effect)
        {
            int n = 0;
            for (int puff = 0; puff < 18; puff++)
            {
                float s = seed + puff * 11.3f;
                float t = age - Hash(s + 1) * 0.22f;
                if (t < 0) continue;
                float life = 1.5f + Hash(s + 2);
                float opacity = Math.Min(1, t / 0.15f) * Math.Max(0, 1 - t / life);
                float angle = Hash(s + 3) * MathHelper.TwoPi;
                Vector2 c = centre + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * (20 + Hash(s + 4) * 65 + t * 20) * zoom;
                float radius = (25 + Hash(s + 5) * 35) * (1 + t * 0.3f) * zoom;
                Color outer = Color.Lerp(new Color(75, 30, 140), new Color(100, 0, 24), Math.Min(1, t / 0.6f)) * (opacity * 0.7f);
                Color inner = new Color(3, 0, 8) * (opacity * 0.85f);
                void V(Vector2 p, Color color) => cloudMesh[n++] = new VertexPositionColorTexture(new Vector3(p, 0), color, Vector2.Zero);
                Vector2 P(float a, float scale) => c + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * radius * scale * (1 + Noise(a, t * 0.25f, s));
                for (int i = 0; i < 32; i++)
                {
                    float a = i * MathHelper.TwoPi / 32, b = (i + 1) * MathHelper.TwoPi / 32;
                    V(c, inner); V(P(a, 0.55f), outer); V(P(b, 0.55f), outer);
                    V(P(a, 0.55f), outer); V(P(a, 1), Color.Transparent); V(P(b, 0.55f), outer);
                    V(P(b, 0.55f), outer); V(P(a, 1), Color.Transparent); V(P(b, 1), Color.Transparent);
                }
            }
            if (n == 0) return;
            graphics.BlendState = Microsoft.Xna.Framework.Graphics.BlendState.NonPremultiplied;
            foreach (var pass in effect.CurrentTechnique.Passes) { pass.Apply(); graphics.DrawUserPrimitives(PrimitiveType.TriangleList, cloudMesh, 0, n / 3); }
        }
        private static void Strip(Vector2 a, Vector2 b, Vector2 side, Color color, ref int count)
        {
            int start = count;
            mesh[count++] = new(new Vector3(a - side, 0), color, Vector2.Zero);
            mesh[count++] = new(new Vector3(b - side, 0), color, Vector2.Zero);
            mesh[count++] = new(new Vector3(a + side, 0), color, Vector2.Zero);
            mesh[count++] = mesh[start + 2]; mesh[count++] = mesh[start + 1];
            mesh[count++] = new(new Vector3(b + side, 0), color, Vector2.Zero);
        }
    }
#endif
}
