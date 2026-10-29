using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Barotrauma.Particles;
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    internal sealed partial class ChrononStasis : ItemComponent, IServerSerializable, IClientSerializable
    {
        [Serialize(3f, IsPropertySaveable.No)] public float Radius { get; set; }
        [Serialize(3f, IsPropertySaveable.No)] public float Duration { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool FreezeMode { get; set; }
        private bool freezeActive;
        private Character caster;
        internal static bool HasFreezingField => Fields.Exists(f => f.freezeActive && f.remaining > 0 && !f.Item.Removed);
#if CLIENT
        private bool targeting;
        private Vector2 targetPoint;
#endif
        internal static bool ShouldFreeze(Character c)
        {
            if (c == null || c.Removed) return false;
            foreach (var field in Fields)
                if (field.freezeActive && field.remaining > 0 && !field.Item.Removed && field.anchor?.Removed != true
                    && c != field.caster && Vector2.DistanceSquared(c.WorldPosition, field.WorldCentre) <= field.CurrentRadius * field.CurrentRadius)
                    return true;
            return false;
        }
        internal static readonly List<ChrononStasis> Fields = new();
        private readonly List<StoredProjectile> shots = new();
        private readonly List<(Vector2 Point, Vector2 Direction, string Prefab)> marks = new();
        private Vector2 centre;
        private Submarine anchor;
        private float remaining, age, fragments;
        private bool qHeld;
#if CLIENT
        private float particleTimer;
        private int observedMarks;
        private readonly List<(Vector2 Point, float Born, int Seed)> impacts = new();
        private void ObserveImpacts()
        {
            if (observedMarks > marks.Count) { observedMarks = 0; impacts.Clear(); }
            while (observedMarks < marks.Count)
            {
                impacts.Add((marks[observedMarks].Point, (float)Timing.TotalTime, observedMarks));
                observedMarks++;
            }
            impacts.RemoveAll(p => Timing.TotalTime - p.Born > 0.75);
        }
        private readonly Dictionary<string, List<ParticleEmitter>> heldEmitters = new();
        internal static bool Visible => Fields.Exists(f => f.remaining > 0 || f.fragments > 0);
        private void DrawHeldParticles(float dt)
        {
            particleTimer -= dt;
            if (particleTimer > 0) return;
            particleTimer = 0.035f;
            foreach (var mark in marks)
            {
                if (!heldEmitters.TryGetValue(mark.Prefab, out var emitters))
                {
                    emitters = new List<ParticleEmitter>();
                    var prefab = ItemPrefab.Find(null, mark.Prefab.ToIdentifier());
                    if (prefab != null)
                        foreach (var component in prefab.ConfigElement.Elements())
                            if (component.Name.LocalName.Equals("Projectile", StringComparison.OrdinalIgnoreCase))
                                foreach (var element in component.Elements())
                                    if (element.Name.LocalName.Equals("ParticleEmitter", StringComparison.OrdinalIgnoreCase))
                                        emitters.Add(new ParticleEmitter(element));
                    heldEmitters[mark.Prefab] = emitters;
                }
                Vector2 end = WorldCentre + mark.Point, start = end - mark.Direction * 22;
                float angle = -(float)Math.Atan2(mark.Direction.Y, mark.Direction.X);
                foreach (var emitter in emitters)
                    emitter.Emit(0.035f, end, null, angle, angle, 1f, 1f, 1f,
                        emitter.Prefab.Properties.ColorMultiplier, null, false, Tuple.Create(start, end));
            }
        }
#endif
        private readonly record struct Request(bool Freeze, Vector2 Point) : IEventData;
        private bool Authority => GameMain.NetworkMember?.IsClient != true;
        private Vector2 WorldCentre => centre + (anchor?.Position ?? Vector2.Zero);
        private float CurrentRadius => ConvertUnits.ToDisplayUnits(Radius) * MathHelper.SmoothStep(0, 1, Math.Min(1, age / 0.18f));
        public ChrononStasis(Item item, ContentXElement element) : base(item, element)
        { IsActive = true; Fields.Add(this); }

        private bool Start(Character c, bool freeze, Vector2 point)
        {
            if (!Authority || remaining > 0 || fragments > 0 || c == null || c.Removed || c.IsDead
                || c.IsIncapacitated || ChrononFreeze.Frozen(c) || !c.AllowInput || Item.Condition <= 0
                || c.Inventory?.GetItemInLimbSlot(InvSlotType.Bag) != Item) return false;
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) return false;
            caster = c; freezeActive = freeze;
            anchor = freeze ? (Hull.FindHull(point)?.Submarine ?? c.Submarine) : c.Submarine;
            centre = (freeze ? point : c.WorldPosition) - (anchor?.Position ?? Vector2.Zero);
            remaining = Duration; age = 0; marks.Clear(); shots.Clear();
#if CLIENT
            observedMarks = 0; impacts.Clear();
#endif
#if CLIENT
            SonicRefraction.AddStasis(WorldCentre, ConvertUnits.ToDisplayUnits(Radius), Duration, freezeActive);
#endif
            Sync();
            return true;
        }
        private void Sync()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
        public override void Update(float deltaTime, Camera cam)
        {
            if (Authority) UpdateAI(deltaTime);
#if CLIENT
            bool down = PlayerInput.KeyDown(Keys.Q);
            var user = Character.Controlled;
            bool usable = GUI.KeyboardDispatcher.Subscriber == null && user != null && !user.IsDead
                && user.AllowInput && !user.IsIncapacitated && !ChrononFreeze.Frozen(user) && Item.Condition > 0
                && user.Inventory?.GetItemInLimbSlot(InvSlotType.Bag) == Item;
            if (!usable || !FreezeMode || remaining > 0 || fragments > 0) targeting = false;
            if (usable && down && !qHeld && remaining <= 0 && fragments <= 0)
            {
                if (FreezeMode) targeting = true;
                else if (Authority) Start(user, false, user.WorldPosition);
                else Item.CreateClientEvent(this, new Request(false, user.WorldPosition));
            }
            if (targeting)
            {
                targetPoint = user.CursorWorldPosition;
                SonicRefraction.PreviewStasis(targetPoint, ConvertUnits.ToDisplayUnits(Radius));
                if (!down && qHeld)
                {
                    targeting = false;
                    if (Authority) Start(user, true, targetPoint);
                    else Item.CreateClientEvent(this, new Request(true, targetPoint));
                }
            }
            qHeld = down;
#endif
            fragments = Math.Max(0, fragments - deltaTime);
            if (remaining <= 0) return;
            #if CLIENT
            DrawHeldParticles(deltaTime);
#endif
            age += deltaTime;
            remaining = Math.Max(0, remaining - deltaTime);
            if (remaining == 0)
            {
                fragments = 0.45f;
                if (Authority) { Release(); Sync(); }
            }
        }
        // Only outside-to-inside crossings are stopped. Outgoing fire passes normally.
        private bool Entry(Vector2 start, Vector2 end, out Vector2 point)
        {
            point = default;
            Vector2 delta = end - start, offset = start - WorldCentre;
            float radius = CurrentRadius, a = delta.LengthSquared();
            float c = offset.LengthSquared() - radius * radius;
            if (remaining <= 0 || radius < 1 || c <= 0 || a < 0.001f || shots.Count >= 128) return false;
            float b = Vector2.Dot(offset, delta), disc = b * b - a * c;
            if (b >= 0 || disc < 0) return false;
            float t = (-b - (float)Math.Sqrt(disc)) / a;
            if (t < 0 || t > 1) return false;
            point = start + delta * t;
            return true;
        }
        internal static bool Intercept(Projectile projectile, Vector2 start, Vector2 end, Vector2 velocity)
        {
            if (projectile.Item.Removed) return false;
            ChrononStasis field = null; Vector2 point = default; float best = float.MaxValue;
            foreach (var candidate in Fields)
            {
                if (candidate.Item.Removed || candidate.anchor?.Removed == true
                    || !candidate.Entry(start, end, out var hit)) continue;
                float d = Vector2.DistanceSquared(start, hit);
                if (d < best) { best = d; field = candidate; point = hit; }
            }
            if (field == null) return false;
            // Let native hits before the boundary resolve first; never catch through walls.
            Vector2 localStart = ConvertUnits.ToSimUnits(start - (projectile.Item.Submarine?.Position ?? Vector2.Zero));
            Vector2 localEnd = ConvertUnits.ToSimUnits(point - (projectile.Item.Submarine?.Position ?? Vector2.Zero));
            if (projectile.DoRayCast(localStart, localEnd, projectile.Item.Submarine).Count > 0) return false;
            var dir = Vector2.Normalize(end - start);
#if CLIENT
            // DoHitscan is skipped: explicitly retain its native truncated launch visuals.
            if (projectile.Hitscan) projectile.LaunchProjSpecific(start, point);
            // Use resolved emitters, including inherited variant configuration.
            string prefabId = projectile.Item.Prefab.Identifier.Value;
            if (!field.heldEmitters.TryGetValue(prefabId, out var cachedEmitters) || cachedEmitters.Count == 0)
            {
                var emitters = new List<ParticleEmitter>();
                foreach (var emitter in projectile.particleEmitters)
                    emitters.Add(new ParticleEmitter(emitter.Prefab));
                field.heldEmitters[prefabId] = emitters;
            }
#endif
            if (GameMain.NetworkMember?.IsClient == true)
            {
                // Visual prediction only. The server records payloads and removes/spawns items.
                projectile.Item.body.Enabled = false;
                projectile.IsActive = false;
                return true;
            }
            field.shots.Add(StoredProjectile.Capture(projectile, point - field.WorldCentre, dir, velocity));
            field.marks.Add((point - field.WorldCentre, dir, projectile.Item.Prefab.Identifier.Value));
            projectile.Item.body.Enabled = false;
            projectile.IsActive = false;
            Entity.Spawner.AddItemToRemoveQueue(projectile.Item);
            field.Sync();
            return true;
        }
        private void Release()
        {
            foreach (var shot in shots)
            {
                Vector2 world = WorldCentre + shot.Point + shot.Direction * 2;
                var sub = anchor?.Removed == false ? anchor : null;
                shot.Spawn(world, sub);
            }
            shots.Clear();
        }
        protected override void RemoveComponentSpecific()
        {
#if CLIENT
            SonicRefraction.CancelNPCStasisPreview(Item.ID);
#endif
            remaining = 0;
            if (Authority && shots.Count > 0 && GameMain.GameSession != null) Release();
            Fields.Remove(this);
            base.RemoveComponentSpecific();
        }
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            bool freeze = msg.ReadBoolean();
            var point = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            Start(client?.Character, freeze, point);
        }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteSingle(remaining); msg.WriteSingle(age); msg.WriteUInt16(anchor?.ID ?? 0);
            msg.WriteBoolean(freezeActive); msg.WriteUInt16(caster?.ID ?? 0);
            WriteAI(msg);
            msg.WriteSingle(centre.X); msg.WriteSingle(centre.Y);
            msg.WriteInt32(marks.Count);
            foreach (var m in marks)
            { msg.WriteSingle(m.Point.X); msg.WriteSingle(m.Point.Y); msg.WriteSingle(m.Direction.X); msg.WriteSingle(m.Direction.Y); msg.WriteString(m.Prefab); }
        }
#endif
#if CLIENT
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
        {
            var request = extraData is Item.ComponentStateEventData data && data.ComponentData is Request r ? r : new Request(false, Item.WorldPosition);
            msg.WriteBoolean(request.Freeze); msg.WriteSingle(request.Point.X); msg.WriteSingle(request.Point.Y);
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            bool active = remaining > 0;
            remaining = msg.ReadSingle(); age = msg.ReadSingle();
            anchor = Entity.FindEntityByID(msg.ReadUInt16()) as Submarine;
            freezeActive = msg.ReadBoolean(); caster = Entity.FindEntityByID(msg.ReadUInt16()) as Character;
            ReadAI(msg);
            centre = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            int count = msg.ReadInt32();
            if (!active && remaining > 0) { observedMarks = 0; impacts.Clear(); }
            marks.Clear();
            for (int i = 0; i < count; i++) marks.Add((new Vector2(msg.ReadSingle(), msg.ReadSingle()), new Vector2(msg.ReadSingle(), msg.ReadSingle()), msg.ReadString()));
            if (!active && remaining > 0) SonicRefraction.AddStasis(WorldCentre, ConvertUnits.ToDisplayUnits(Radius), remaining, freezeActive);
            if (active && remaining <= 0) fragments = 0.45f;
        }
        internal static void DrawAll(SpriteBatch batch, Camera camera)
        {
            foreach (var field in Fields)
            {
                if (field.remaining <= 0 && field.fragments <= 0) continue;
                Vector2 centre = field.centre + (field.anchor?.DrawPosition ?? Vector2.Zero);
                float fade = field.remaining > 0 ? 1 : field.fragments / 0.45f;
                float radius = field.CurrentRadius + (1 - fade) * 55;
                field.ObserveImpacts();
                // Live fragments slide in both directions around the surface; they are not stored shots.
                foreach (var impact in field.impacts)
                {
                    float elapsed = (float)Timing.TotalTime - impact.Born;
                    DrawImpactFragments(batch, camera, centre, impact.Point, elapsed, impact.Seed, fade);
                }
                if (field.remaining <= 0) continue;
                foreach (var mark in field.marks)
                {
                    Vector2 p = camera.WorldToScreen(centre + mark.Point);
                    Vector2 q = camera.WorldToScreen(centre + mark.Point - mark.Direction * 22);
                    GUI.DrawLine(batch, p, q, new Color(210, 225, 240) * 0.16f, width: 3 * camera.Zoom);
                    GUI.DrawLine(batch, p, q, Color.White * 0.9f, width: 1.5f * camera.Zoom);
                }
            }
        }
        internal static void DrawImpactFragments(SpriteBatch batch, Camera camera, Vector2 centre,
            Vector2 point, float elapsed, int impactSeed, float fade = 1)
        {
            float baseAngle = (float)Math.Atan2(point.Y, point.X);
            float surfaceRadius = point.Length();
            for (int i = 0; i < 18; i++)
            {
                float seed = i * 2.39996f + impactSeed * 1.73f;
                float life = 0.35f + 0.35f * (0.5f + 0.5f * (float)Math.Sin(seed * 3));
                if (elapsed >= life) continue;
                float speed = (i % 2 == 0 ? 1 : -1) * (0.65f + 0.7f * (0.5f + 0.5f * (float)Math.Cos(seed)));
                Vector2 At(float time)
                {
                    float angle = baseAngle + speed * time;
                    float r = surfaceRadius + (float)Math.Sin(seed + time * 17) * 6 * time / life
                        + (i % 4 == 0 ? time * 35 : -time * 9);
                    return centre + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * r;
                }
                Vector2 head = camera.WorldToScreen(At(elapsed));
                Vector2 tail = camera.WorldToScreen(At(Math.Max(0, elapsed - 0.025f - 0.018f * (i % 3))));
                float alpha = (1 - elapsed / life) * fade;
                GUI.DrawLine(batch, tail, head, new Color(255, 157, 65) * (alpha * 0.22f), width: 4 * camera.Zoom);
                GUI.DrawLine(batch, tail, head, Color.Lerp(new Color(255, 246, 210), new Color(244, 125, 38), elapsed / life) * alpha,
                    width: (i % 3 == 0 ? 1.6f : 0.8f) * camera.Zoom);
            }
        }
#endif
    }

    public sealed class ChrononStasisPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private sealed class Track { internal Vector2 Position; }
        private static ConditionalWeakTable<Projectile, Track> tracks = new();
        public void Initialize()
        {
            harmony = new Harmony("aic.chronon.stasis");
            ChrononFreeze.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(CharacterHealth), "ApplyDamage"),
                postfix: new HarmonyMethod(typeof(ChrononStasis), nameof(ChrononStasis.AIDamaged)));
            harmony.Patch(AccessTools.Method(typeof(Projectile), "DoHitscan"), prefix: new HarmonyMethod(typeof(ChrononStasisPlugin), nameof(Hitscan)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Projectile), "Update"), prefix: new HarmonyMethod(typeof(ChrononStasisPlugin), nameof(Physical)) { priority = Priority.First });
            harmony.Patch(AccessTools.Method(typeof(Projectile), "Launch"), postfix: new HarmonyMethod(typeof(ChrononStasisPlugin), nameof(Launched)));
        }
        private static void Launched(Projectile __instance)
            => tracks.GetValue(__instance, _ => new Track()).Position = __instance.Item.WorldPosition;
        private static bool Hitscan(Projectile __instance, Vector2 dir)
        {
            Vector2 start = __instance.Item.WorldPosition;
            return !ChrononStasis.Intercept(__instance, start, start + dir * ConvertUnits.ToDisplayUnits(500), Vector2.Zero);
        }
        private static bool Physical(Projectile __instance, float deltaTime)
        {
            if (__instance.Hitscan || !__instance.IsActive || __instance.Item.body?.Enabled != true) return true;
            var body = __instance.Item.body;
            Vector2 velocity = body.LinearVelocity;
            if (velocity.LengthSquared() < 0.01f) return true;
            Vector2 current = __instance.Item.WorldPosition;
            var track = tracks.GetValue(__instance, _ => new Track { Position = current });
            Vector2 start = track.Position;
            track.Position = current;
            return !ChrononStasis.Intercept(__instance, start, current + ConvertUnits.ToDisplayUnits(velocity * deltaTime), velocity);
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { ChrononFreeze.Clear(); harmony?.UnpatchSelf(); ChrononStasis.Fields.Clear(); tracks = new(); }
    }
}




