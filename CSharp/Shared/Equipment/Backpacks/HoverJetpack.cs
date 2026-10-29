using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Particles;
#endif

namespace AIC.Components
{
    internal sealed class HoverJetpack : ItemComponent
    {
        [Serialize(false, IsPropertySaveable.Yes)] public bool IsOn { get; set; }
        [Serialize(6f, IsPropertySaveable.No)] public float MaxSpeed { get; set; }
        [Serialize(5f, IsPropertySaveable.No)] public float Acceleration { get; set; }
        [Serialize(0.5f, IsPropertySaveable.No)] public float Braking { get; set; }
        [Serialize(8f, IsPropertySaveable.No)] public float VerticalSpeed { get; set; }
        [Serialize(18f, IsPropertySaveable.No)] public float MaxLiftAcceleration { get; set; }
        private Character pilot;
        private float exhaustTimer;
#if CLIENT
        private bool soundThrusting;
        private float soundStartDelay;
#endif
        private readonly HashSet<PhysicsBody> bodies = new();
        internal static HoverJetpack Equipped(Character c) =>
            c?.Inventory?.GetItemInLimbSlot(InvSlotType.Bag)?.GetComponent<HoverJetpack>();
        internal static bool Flying(Character c) => Equipped(c)?.Valid(c) == true
            && (c.IsKeyDown(InputType.Up) || !c.AnimController.OnGround);
        private bool Valid(Character c) => IsOn && Item.Condition > 0 && MovementState.ActiveOnLand(c)
            && !MovementState.SelectingLadder(c) && Equipped(c) == this;
        public HoverJetpack(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        public override void Update(float dt, Camera cam)
        {
            if (!Valid(pilot)) pilot = null;
#if CLIENT
            var wearer = Item.ParentInventory?.Owner as Character;
            bool thrust = Valid(wearer) && !wearer.AnimController.Frozen && wearer.IsKeyDown(InputType.Up);
            if (thrust && !soundThrusting)
            {
                PlaySound(ActionType.OnUse, wearer);
                soundStartDelay = 0.12f;
            }
            else if (!thrust && soundThrusting)
            {
                StopSounds(ActionType.OnSuccess);
                PlaySound(ActionType.OnFailure, wearer);
            }
            soundThrusting = thrust;
            if (thrust)
            {
                soundStartDelay -= dt;
                if (soundStartDelay <= 0) PlaySound(ActionType.OnSuccess, wearer);
            }
#endif
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        protected override void RemoveComponentSpecific()
        {
#if CLIENT
            StopSounds(ActionType.OnSuccess);
#endif
            base.RemoveComponentSpecific();
        }
        internal bool Fly(HumanoidAnimController anim, float dt)
        {
            var c = anim.Character;
            if (!Valid(c) || anim.Frozen || anim.Collider == null || !anim.Collider.Enabled) return true;
            bool thrusting = c.IsKeyDown(InputType.Up);
            if (!thrusting && anim.OnGround) return true;
            dt = Math.Clamp(dt, 0, 0.05f);
            var body = anim.Collider;
            if (pilot != c)
            {
                pilot = c;
            }
            bool simulate = GameMain.NetworkMember?.IsClient != true;
#if CLIENT
            simulate |= c == Character.Controlled;
#endif
            if (simulate)
            {
                float cursorDelta = c.CursorPosition.X - body.Position.X;
                if (cursorDelta < -30) anim.TargetDir = Direction.Left;
                else if (cursorDelta > 30) anim.TargetDir = Direction.Right;
                if (anim.TargetDir != anim.Direction && Timing.TotalTime > anim.FlipLockTime) anim.Flip();
                int horizontal = (c.IsKeyDown(InputType.Right) ? 1 : 0) - (c.IsKeyDown(InputType.Left) ? 1 : 0);
                float xAcceleration = Math.Clamp((horizontal * MaxSpeed - body.LinearVelocity.X) * 5,
                    -Math.Max(0, horizontal == 0 ? Braking : Acceleration),
                    Math.Max(0, horizontal == 0 ? Braking : Acceleration));
                // W supplies thrust directly, including during a fall. No altitude target.
                // Taper only near the upward speed cap; releasing W removes lift.
                float lift = thrusting ? Math.Min(Math.Max(0, MaxLiftAcceleration),
                    Math.Max(0, -GameMain.World.Gravity.Y + (Math.Max(0, VerticalSpeed) - body.LinearVelocity.Y) * 6)) : 0;
                bodies.Clear();
                bodies.Add(body);
                foreach (var limb in anim.Limbs)
                    if (!limb.Removed && !limb.IsSevered && limb.body != null) bodies.Add(limb.body);
                foreach (var part in bodies)
                    if (part.Enabled) part.ApplyForce(new Vector2(xAcceleration, lift) * part.Mass);
                bodies.Clear();
            }
            anim.levitatingCollider = false;
            foreach (var limb in anim.Limbs) limb.Disabled = false;
#if CLIENT
            exhaustTimer -= dt;
            if (thrusting && exhaustTimer <= 0)
            {
                exhaustTimer = 0.025f;
                var torso = anim.GetLimb(LimbType.Torso);
                var prefab = ParticleManager.FindPrefab("aic_atmos_jet_trail");
                if (torso != null && prefab != null)
                    GameMain.ParticleManager.CreateParticle(prefab,
                        torso.WorldPosition + new Vector2(-26 * anim.Dir, -25),
                        new Vector2(Rand.Range(-25f, 25f), Rand.Range(-330f, -220f)), MathHelper.PiOver2, c.CurrentHull);
                var smoke = ParticleManager.FindPrefab("aic_atmos_jet_smoke");
                if (torso != null && smoke != null)
                    GameMain.ParticleManager.CreateParticle(smoke,
                        torso.WorldPosition + new Vector2(-26 * anim.Dir, -35),
                        new Vector2(Rand.Range(-20f, 20f), -100), 0, c.CurrentHull);
            }
#endif
            return false;
        }
    }
    public sealed class HoverJetpackMotion : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool Before(HumanoidAnimController __instance, float __0) =>
            HoverJetpack.Equipped(__instance.Character)?.Fly(__instance, __0) ?? true;
        public void Initialize()
        {
            harmony = new Harmony("aic.hoverjetpack");
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                prefix: new HarmonyMethod(typeof(HoverJetpackMotion), nameof(Before)) { priority = Priority.First });
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
