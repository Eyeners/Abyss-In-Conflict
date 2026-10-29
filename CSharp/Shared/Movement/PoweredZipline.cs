using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using FarseerPhysics;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    // Local prototype: native projectiles own attachment and submarine transforms.
    class ZiplineCable
    {
        internal Item Item;
        internal static double InteractionLock;
        internal Item A, B;
        internal Character Rider;
        internal float TravelDirection = 1;
        private bool interactDown;
        private double nextMount;
#if CLIENT
        private readonly Sprite pulley, wheel;
#endif
        internal ZiplineCable(Item owner, Item a, Item b, ContentXElement element)
        {
            Item = owner; A = a; B = b;
#if CLIENT
            pulley = new Sprite(element.GetChildElement("pulleysprite"));
            wheel = new Sprite(element.GetChildElement("wheelsprite"));
            interactDown = Keyboard.GetState().IsKeyDown(Keys.E);
#endif
        }
        internal static bool Fixed(Item i) => i != null && !i.Removed && i.Condition > 0
            && i.GetComponent<Projectile>()?.IsStuckToTarget == true;
        internal bool Ready => Fixed(A) && Fixed(B) && A.Submarine == B.Submarine
            && Vector2.DistanceSquared(A.WorldPosition, B.WorldPosition) >= 10000
            && Vector2.DistanceSquared(A.WorldPosition, B.WorldPosition) <= 3000 * 3000;
        private float Along(Vector2 p) => MathHelper.Clamp(Vector2.Dot(p - A.WorldPosition,
            B.WorldPosition - A.WorldPosition) / Vector2.DistanceSquared(A.WorldPosition, B.WorldPosition), 0, 1);
        internal Vector2 Handle => Vector2.Lerp(A.WorldPosition, B.WorldPosition,
            Along(Rider.WorldPosition + new Vector2(0, 65)));
        internal void Release() { Rider = null; nextMount = Timing.TotalTime + 0.5; }
        internal void Update(float dt, Camera cam)
        {
            if (GameMain.NetworkMember != null) { Release(); return; }
            if (Rider != null && (!Ready || Rider.Removed || Rider.IsDead || Rider.IsIncapacitated)) Release();
#if CLIENT
            var c = Character.Controlled;
            bool down = Keyboard.GetState().IsKeyDown(Keys.E);
            bool press = down && !interactDown; interactDown = down;
            if (Rider != null && Rider != c) Release();
            if (!press || Timing.TotalTime < InteractionLock || c == null || GUI.KeyboardDispatcher.Subscriber != null || GUI.InputBlockingMenuOpen) return;
            if (Rider == c) { InteractionLock = Timing.TotalTime + 0.2; Release(); return; }
            if (!Ready || c.IsDead || c.IsIncapacitated || c.IsClimbing || c.IsAttachedToController()
                || Timing.TotalTime < nextMount) return;
            foreach (var line in PoweredZipline.Lines) if (line.Rider == c) return;
            var head = c.WorldPosition + new Vector2(0, 65);
            float t = Along(head);
            if (Vector2.DistanceSquared(head, Vector2.Lerp(A.WorldPosition, B.WorldPosition, t)) > 85 * 85) return;
            InteractionLock = Timing.TotalTime + 0.2;
            Rider = c; TravelDirection = t < 0.5f ? 1 : -1;
#endif
        }
        internal void Move(HumanoidAnimController anim)
        {
            if (!Ready || Rider != anim.Character) return;
            var c = Rider;
            // A collision may stop the rig, but must never leave a distant hand tether.
            if (Vector2.DistanceSquared(Handle, c.WorldPosition + new Vector2(0, 65)) > 160 * 160)
            { Release(); return; }
            Vector2 delta = B.WorldPosition - A.WorldPosition;
            Vector2 direction = Vector2.Normalize(delta);
            float input = (c.IsKeyDown(InputType.Right) ? 1 : 0) - (c.IsKeyDown(InputType.Left) ? 1 : 0);
            if (input != 0) TravelDirection = input * (direction.X >= 0 ? 1 : -1);
            float t = Along(c.WorldPosition + new Vector2(0, 65));
            if ((t <= 0.015f && TravelDirection < 0) || (t >= 0.985f && TravelDirection > 0)) { Release(); return; }
            Vector2 target = Handle - new Vector2(0, 65);
            Vector2 correction = ConvertUnits.ToSimUnits(target - c.WorldPosition) * 8;
            if (correction.LengthSquared() > 9) correction = Vector2.Normalize(correction) * 3;
            Vector2 velocity = direction * (3 * TravelDirection) + correction;
            var bodies = new HashSet<PhysicsBody> { anim.Collider };
            foreach (var limb in anim.Limbs)
                if (!limb.Removed && !limb.IsSevered && limb.body != null) bodies.Add(limb.body);
            foreach (var body in bodies)
            {
                if (body == null || !body.Enabled) continue;
                Vector2 acceleration = (velocity - body.LinearVelocity) * 12 - GameMain.World.Gravity;
                if (acceleration.LengthSquared() > 3600) acceleration = Vector2.Normalize(acceleration) * 60;
                body.ApplyForce(acceleration * body.Mass);
            }
            var id = "aic_ground_adhesion_release".ToIdentifier();
            var affliction = c.CharacterHealth.GetAffliction(id);
            if (affliction != null) affliction.Strength = Math.Max(affliction.Strength, 0.3f);
            else if (AfflictionPrefab.Prefabs.TryGet(id, out var prefab)) c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(0.3f));
            anim.TargetDir = direction.X * TravelDirection >= 0 ? Direction.Right : Direction.Left;
            anim.Collider.SmoothRotate(0, Math.Max(1, anim.Collider.Mass) * 12);
            foreach (var type in new[] { LimbType.LeftHand, LimbType.RightHand })
            {
                var hand = anim.GetLimb(type);
                if (hand?.body == null || hand.IsSevered || !hand.body.Enabled) continue;
                Vector2 handTarget = Handle + new Vector2(type == LimbType.LeftHand ? -10 : 10, -14);
                Vector2 error = ConvertUnits.ToSimUnits(handTarget - hand.WorldPosition);
                Vector2 force = (error * 65 + (anim.Collider.LinearVelocity - hand.body.LinearVelocity) * 8) * hand.Mass;
                if (force.LengthSquared() > 10000) force = Vector2.Normalize(force) * 100;
                hand.body.ApplyForce(force);
            }
        }
#if CLIENT
        internal void Draw(SpriteBatch batch)
        {
            if (!Ready) return;
            Vector2 Screen(Vector2 p) => new Vector2(p.X, -p.Y);
            Vector2 a = A.WorldPosition + (A.Submarine == null ? Vector2.Zero : A.Submarine.DrawPosition - A.Submarine.Position);
            Vector2 b = B.WorldPosition + (B.Submarine == null ? Vector2.Zero : B.Submarine.DrawPosition - B.Submarine.Position);
            GUI.DrawLine(batch, Screen(a), Screen(b), new Color(35, 38, 40), width: 4);
            if (Rider == null) return;
            Vector2 p = Handle + (A.Submarine == null ? Vector2.Zero : A.Submarine.DrawPosition - A.Submarine.Position);
            float angle = -(float)Math.Atan2(b.Y - a.Y, b.X - a.X);
            pulley.Draw(batch, Screen(p), Color.White, angle, 0.65f);
            wheel.Draw(batch, Screen(p), Color.White, (float)Timing.TotalTime * TravelDirection * 10, 0.45f);
        }
#endif
    }
    class PoweredZipline : ItemComponent
    {
        internal static readonly List<ZiplineCable> Lines = new();
        internal static readonly List<PoweredZipline> Guns = new();
        private readonly ContentXElement config;
        private Item pending;
        private Item flying;
#if CLIENT
        private bool motorRunning;
        private float motorDelay;
#endif
        public PoweredZipline(Item item, ContentXElement element) : base(item, element)
        { config = element; IsActive = true; Guns.Add(this); }
        internal void Launch(Item anchor) { flying = anchor; }
        internal void Anchor(Item anchor)
        {
            if (flying == anchor) flying = null;
            if (Item.Removed) { Entity.Spawner?.AddItemToRemoveQueue(anchor); return; }
            if (!ZiplineCable.Fixed(pending)) { pending = anchor; return; }
            // Consume both anchors even when the pair is invalid. The next shot
            // must start a fresh pair instead of retrying the old distant endpoint.
            var first = pending;
            pending = null;
            var cable = new ZiplineCable(Item, first, anchor, config);
            if (!cable.Ready)
            {
                Entity.Spawner?.AddItemToRemoveQueue(first);
                Entity.Spawner?.AddItemToRemoveQueue(anchor);
                return;
            }
            Lines.Add(cable);
        }
        public override void Update(float dt, Camera cam)
        {
            for (int i = Lines.Count - 1; i >= 0; i--)
            {
                var cable = Lines[i];
                if (cable.Item.Removed || !cable.Ready)
                {
                    cable.Release();
                    if (cable.A != null && !cable.A.Removed) Entity.Spawner?.AddItemToRemoveQueue(cable.A);
                    if (cable.B != null && !cable.B.Removed) Entity.Spawner?.AddItemToRemoveQueue(cable.B);
                    Lines.RemoveAt(i); continue;
                }
                if (cable.Item == Item) cable.Update(dt, cam);
            }
#if CLIENT
            Character rider = null;
            foreach (var cable in Lines)
                if (cable.Item == Item && cable.Ready && cable.Rider != null
                    && cable.Rider.AnimController.Collider.LinearVelocity.LengthSquared() > 0.16f)
                { rider = cable.Rider; break; }
            bool running = rider != null && GameMain.NetworkMember == null;
            if (running && !motorRunning)
            { PlaySound(ActionType.OnUse, rider); motorDelay = 0.12f; }
            if (!running && motorRunning)
            { StopSounds(ActionType.OnSuccess); PlaySound(ActionType.OnFailure); }
            motorRunning = running;
            if (running)
            { motorDelay -= dt; if (motorDelay <= 0) PlaySound(ActionType.OnSuccess, rider); }
#endif
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        protected override void RemoveComponentSpecific()
        {
            Guns.Remove(this);
#if CLIENT
            StopSounds(ActionType.OnSuccess);
#endif
            base.RemoveComponentSpecific();
        }
#if CLIENT
        internal void DrawPreview(SpriteBatch batch)
        {
            if (Item.Removed || GameMain.NetworkMember != null) return;
            bool inFlight = flying != null && !flying.Removed && flying.ParentInventory == null;
            bool anchored = ZiplineCable.Fixed(pending);
            if (!inFlight && !anchored) return;
            Vector2 RenderPosition(Item i) => i.WorldPosition
                + (i.Submarine == null ? Vector2.Zero : i.Submarine.DrawPosition - i.Submarine.Position);
            Vector2 muzzle = RenderPosition(Item) + ConvertUnits.ToDisplayUnits(
                Item.GetComponent<RangedWeapon>()?.TransformedBarrelPos ?? Vector2.Zero);
            // First shot pays rope out from the muzzle. The second shot takes
            // that free end with it, keeping the first anchor stationary.
            Vector2 a = anchored ? RenderPosition(pending) : muzzle;
            Vector2 b = inFlight ? RenderPosition(flying) : muzzle;
            if (Vector2.DistanceSquared(a, b) < 1) return;
            a.Y = -a.Y; b.Y = -b.Y;
            GUI.DrawLine(batch, a, b, new Color(35, 38, 40), width: 4);
        }
#endif
    }
    class ZiplineAnchor : ItemComponent
    {
        private bool registered;
        private bool launched;
        private float age;
        public ZiplineAnchor(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        public override void Update(float dt, Camera cam)
        {
            if (Item.ParentInventory != null) return;
            if (GameMain.NetworkMember != null) return;
            if (Item.Condition <= 0)
            { Entity.Spawner?.AddItemToRemoveQueue(Item); return; }
            age += dt;
            var projectile = Item.GetComponent<Projectile>();
            if (!launched && projectile?.Launcher != null)
            {
                launched = true;
                projectile.Launcher.GetComponent<PoweredZipline>()?.Launch(Item);
            }
            if (!registered && projectile?.IsStuckToTarget == true)
            {
                registered = true;
                var launcher = projectile.Launcher?.GetComponent<PoweredZipline>();
                if (launcher == null) { Entity.Spawner?.AddItemToRemoveQueue(Item); return; }
                launcher.Anchor(Item);
            }
            if ((!registered && age > 8) || (registered && projectile?.Launcher?.Removed != false)) Entity.Spawner?.AddItemToRemoveQueue(Item);
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
    }
    public sealed class PoweredZiplinePlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.powered.zipline");
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                postfix: new HarmonyMethod(typeof(PoweredZiplinePlugin), nameof(Animate)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(Limb), "Draw", new[] { typeof(SpriteBatch), typeof(Camera), typeof(Color?), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(PoweredZiplinePlugin), nameof(Draw)));
#endif
        }
        private static void Animate(HumanoidAnimController __instance)
        {
            foreach (var line in PoweredZipline.Lines)
                if (!line.Item.Removed && line.Rider == __instance.Character) line.Move(__instance);
        }
#if CLIENT
        private static void Draw(Limb __instance, SpriteBatch __0)
        {
            if (__instance.character != Character.Controlled || __instance.type != LimbType.Torso) return;
            foreach (var line in PoweredZipline.Lines) if (!line.Item.Removed) line.Draw(__0);
            foreach (var gun in PoweredZipline.Guns) gun.DrawPreview(__0);
        }
#endif
        public void Dispose()
        {
            harmony?.UnpatchSelf();
#if CLIENT
            foreach (var gun in PoweredZipline.Guns) gun.StopSounds(ActionType.OnSuccess);
#endif
            PoweredZipline.Guns.Clear(); PoweredZipline.Lines.Clear(); ZiplineCable.InteractionLock = 0;
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
