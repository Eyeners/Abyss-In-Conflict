using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal class MagneticBoots : ItemComponent
    {
        // CustomInterface 的 propertyname="IsOn" 直接控制此属性，Serialize 负责保存参数。
        [Serialize(false, IsPropertySaveable.Yes)] public bool IsOn { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool WallWalkEnabled { get; set; }
        internal readonly MagneticWallWalk WallWalk = new MagneticWallWalk();
        private readonly FootAnchor[] anchors = { new FootAnchor(LimbType.LeftFoot), new FootAnchor(LimbType.RightFoot) };
        internal static readonly HashSet<MagneticBoots> Attached = new HashSet<MagneticBoots>();
        internal bool Grounded;
        private float floor;
        private Submarine supportSub;
        private float supportLocalY;
        internal Vector2 SupportVelocity => IsOn && Grounded && owner?.Submarine == null
            && supportSub != null && !supportSub.Removed ? supportSub.Velocity : Vector2.Zero;
        private Character owner;
        private Submarine submarine;
        private float scanTimer;
        private bool wasAnchoring;
        private sealed class FootAnchor
        {
            internal readonly LimbType Type;
            internal Joint Joint;
            internal Submarine ExteriorSub;
            internal Vector2 SurfacePoint;
            internal Limb Foot;
            internal Body Surface;
            internal World World;
            internal FootAnchor(LimbType type) { Type = type; }
            internal void Release()
            {
                // 先禁用再移出物理世界；不能只清空 C# 引用，否则约束仍可能留在世界中。
                if (Joint != null && World != null)
                {
                    Joint.Enabled = false;
                    if (World.JointList.Contains(Joint)) World.Remove(Joint);
                }
                Joint = null; Foot = null; Surface = null; World = null; ExteriorSub = null;
            }
        }
        public MagneticBoots(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        internal static MagneticBoots Get(Character user) => user?.GetEquippedItem("aic_magnetic_boots_armor".ToIdentifier(), InvSlotType.OuterClothes)
            ?.GetComponent<MagneticBoots>();
        internal static bool EnabledFor(Character user) => Get(user)?.IsOn == true && !SpaceBackpackDamage.IsFailing(user);
        // Ordinary stun is separate from IsIncapacitated and voluntary ragdoll.
        internal static bool NeedsFootAnchors(Character user) =>
            user.IsDead || user.IsIncapacitated || user.IsRagdolled || user.Stun > 0.0f;
        internal void Release()
        {
            foreach (var anchor in anchors) anchor.Release();
            Grounded = false;
            supportSub = null;
            wasAnchoring = false;
            Attached.Remove(this);
        }
        public override void Update(float deltaTime, Camera cam)
        {
            var user = Item.ParentInventory?.Owner as Character;
            if (Item.Removed || !IsOn || user == null || user.Removed || Get(user) != this || SpaceBackpackDamage.IsFailing(user))
            { WallWalk.Reset(); Release(); return; }
            if (owner != user || submarine != user.Submarine)
            // 穿戴者或坐标系发生变化时，旧关节不能沿用。
            { Release(); owner = user; submarine = user.Submarine; scanTimer = 0; }
            var anim = user.AnimController as HumanoidAnimController;
            if (anim == null) { Release(); return; }
            if (HumanJumpClimb.Controlling(user)) { WallWalk.Reset(); Release(); return; }
            if (WallWalk.Update(user, WallWalkEnabled, deltaTime)) { Release(); return; }
            bool limp = NeedsFootAnchors(user);
            if (user.IsClimbing && !limp) { Release(); return; }
            // Keep stationary feet attached before an impact/death can move them out of ray range.
            bool anchoring = limp || anim.TargetMovement.LengthSquared() < 0.0001f;
            // 状态切换立即扫描，平时每 0.1 秒扫描；已建立的关节仍由每步物理求解。
            if (anchoring != wasAnchoring) { scanTimer = 0; wasAnchoring = anchoring; }
            if (!anchoring)
                foreach (var anchor in anchors) anchor.Release();
            foreach (var anchor in anchors)
                if (anchor.Joint != null && (anchor.Foot.Removed || anchor.Foot.IsSevered
                    || anchor.Surface == null || !anchor.Surface.Enabled || !anchor.Surface.BodyType.Equals(BodyType.Static)
                    || (anchor.Surface.UserData is Entity entity && entity.Removed)
                    || (anchor.ExteriorSub != null && (anchor.ExteriorSub.Removed
                        || anchor.ExteriorSub.PhysicsBody == null || !anchor.ExteriorSub.PhysicsBody.Enabled)))) anchor.Release();
            foreach (var anchor in anchors)
                if (anchor.Joint != null && anchor.ExteriorSub != null)
                {
                    floor = anchor.Joint.WorldAnchorB.Y;
                }
            if (supportSub != null && !supportSub.Removed && user.Submarine == null)
                floor = supportLocalY + ConvertUnits.ToSimUnits(supportSub.Position).Y;
            scanTimer -= deltaTime;
            if (scanTimer <= 0)
            {
                scanTimer = 0.1f;
                Grounded = false;
                supportSub = null;
                foreach (var anchor in anchors)
                {
                    if (anchor.Joint != null)
                    {
                        Grounded = true;
                        if (anchor.ExteriorSub != null)
                        {
                            supportSub = anchor.ExteriorSub;
                            supportLocalY = anchor.Surface.GetWorldPoint(anchor.SurfacePoint).Y;
                        }
                        continue;
                    }
                    var foot = anim.GetLimb(anchor.Type);
                    if (foot == null || foot.Removed || foot.IsSevered || foot.body == null || !foot.body.Enabled) continue;
                    Vector2 bottom = foot.SimPosition - Vector2.UnitY * foot.body.GetSize().Y * 0.5f;
                    Vector2 hit = Vector2.Zero;
                    Fixture surface = null;
                    float closest = 1.0f;
                    void Scan(Submarine targetSub)
                    {
                        // Hull 外是世界坐标，结构仍在潜艇坐标中：射线减偏移，命中点再加回来。
                        Vector2 offset = targetSub == user.Submarine ? Vector2.Zero
                            : ConvertUnits.ToSimUnits(targetSub.Position);
                        GameMain.World.RayCast((fixture, point, normal, fraction) =>
                        {
                            if (fixture.IsSensor || fixture.Body == foot.body.FarseerBody || normal.Y < 0.5f
                                || fixture.Body.UserData is not Structure structure || structure.Submarine != targetSub
                                || fraction > closest) return -1;
                            closest = fraction; surface = fixture; hit = point + offset; return fraction;
                        }, bottom + Vector2.UnitY * 0.12f - offset, bottom - Vector2.UnitY * 0.35f - offset);
                    }
                    Scan(user.Submarine);
                    if (user.Submarine == null)
                        foreach (var candidate in Submarine.Loaded) Scan(candidate);
                    if (surface == null) continue;
                    Grounded = true; floor = hit.Y;
                    var surfaceSub = (surface.Body.UserData as Structure)?.Submarine;
                    if (user.Submarine == null && surfaceSub != null)
                    {
                        supportSub = surfaceSub;
                        supportLocalY = hit.Y - ConvertUnits.ToSimUnits(surfaceSub.Position).Y;
                    }
                    if (!anchoring) continue;
                    anchor.Foot = foot; anchor.Surface = surface.Body; anchor.World = GameMain.World;
                    if (user.Submarine == null && surfaceSub != null)
                    {
                        // Join the moving world-space submarine body, not its hidden structure body.
                        var shipBody = surfaceSub.PhysicsBody?.FarseerBody;
                        if (shipBody == null || !shipBody.Enabled) continue;
                        anchor.ExteriorSub = surfaceSub;
                        anchor.SurfacePoint = surface.Body.GetLocalPoint(hit - ConvertUnits.ToSimUnits(surfaceSub.Position));
                        anchor.Joint = new RevoluteJoint(foot.body.FarseerBody, shipBody,
                            foot.body.FarseerBody.GetLocalPoint(bottom), shipBody.GetLocalPoint(hit), false);
                        // 固定脚底连接点但允许脚转动；保留与船体碰撞，不使用弹簧追赶。
                        anchor.Joint.CollideConnected = true;
                    }
                    else
                        anchor.Joint = new RevoluteJoint(foot.body.FarseerBody, surface.Body,
                            foot.body.FarseerBody.GetLocalPoint(bottom), surface.Body.GetLocalPoint(hit), false);
                    GameMain.World.Add(anchor.Joint);
                    Attached.Add(this);
                }
            }
            if (!Grounded || limp || anim.Collider == null) return;
            anim.onGround = true;
            anim.floorY = anim.standOnFloorY = floor;
            // Exterior feet are constrained directly; do not fight them with a height servo.
            if (user.Submarine == null && supportSub != null) return;
            // Native water locomotion omits the standing height servo. Restore it locally, not water state.
            float targetY = floor + anim.Collider.Height * 0.5f + anim.Collider.Radius + anim.ColliderHeightFromFloor;
            float relativeY = anim.Collider.LinearVelocity.Y - SupportVelocity.Y;
            float ay = MathHelper.Clamp((targetY - anim.Collider.SimPosition.Y) * 45 - relativeY * 12, -35, 35);
            anim.Collider.ApplyForce(new Vector2(0, ay * anim.Collider.Mass));
        }
        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);
        public override void RemoveComponentSpecific() { WallWalk.Reset(); Release(); base.RemoveComponentSpecific(); }
    }

    public sealed class MagneticBootsAnimation : IAssemblyPlugin
    {
        private Harmony harmony;
        private struct Saved { internal bool Active, Water, Swimming, Standing; internal float LockTimer; }
        public void Initialize()
        {
            harmony = new Harmony("aic.magnetic.boots");
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                prefix: new HarmonyMethod(typeof(MagneticBootsAnimation), nameof(Begin)),
                finalizer: new HarmonyMethod(typeof(MagneticBootsAnimation), nameof(End)));
            harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateSwimming"),
                prefix: new HarmonyMethod(typeof(MagneticBootsAnimation), nameof(WallPose)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.TeleportTo), new[] { typeof(Vector2) }),
                prefix: new HarmonyMethod(typeof(MagneticBootsAnimation), nameof(BeforeTeleport)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            foreach (var walker in new List<MagneticWallWalk>(MagneticWallWalk.ActiveWalkers)) walker.Reset();
            foreach (var boots in new List<MagneticBoots>(MagneticBoots.Attached)) boots.Release();
        }
        private static void BeforeTeleport(Character __instance)
        {
            // Lua gates also teleport dragged bodies; release constraints before moving either ragdoll.
            var boots = MagneticBoots.Get(__instance);
            if (boots == null) return;
            boots.WallWalk.Reset();
            boots.Release();
        }
        private static void Begin(HumanoidAnimController __instance, out Saved __state)
        {
            // 仅在本次动画调用内伪装成站立；不是把角色永久改成不在水中。
            __state = default;
            var user = __instance.Character;
            if (HumanJumpClimb.Controlling(user)) return;
            var boots = MagneticBoots.Get(user);
            if (boots?.IsOn == true && boots.WallWalkEnabled && boots.WallWalk.Active && MagneticBoots.NeedsFootAnchors(user))
                boots.WallWalk.PreserveLimp(__instance);
            if (boots?.IsOn == true && boots.WallWalk.Active && !MagneticBoots.NeedsFootAnchors(user))
            {
                __state = new Saved { Active = true, Water = __instance.inWater, Swimming = __instance.swimming,
                    Standing = __instance.forceStanding, LockTimer = __instance.swimmingStateLockTimer };
                __instance.swimming = true;
                __instance.forceStanding = false;
                __instance.swimmingStateLockTimer = 10;
                return;
            }
            if (boots?.IsOn != true || !boots.Grounded || MagneticBoots.NeedsFootAnchors(user)) return;
            // Character.Control skips land facing in water, while we skip swimming below.
            // Remote characters keep the direction supplied by native networking/AI.
            if (__instance.inWater && !user.IsAIControlled && !user.IsRemotelyControlled
                && !__instance.IsUsingItem && __instance.Anim != AnimController.Animation.CPR
                && __instance.Collider != null)
            {
                if (!user.FollowCursor) __instance.TargetDir = Direction.Right;
                else
                {
                    float offset = user.CursorPosition.X - __instance.Collider.Position.X;
                    if (offset < -40.0f) __instance.TargetDir = Direction.Left;
                    else if (offset > 40.0f) __instance.TargetDir = Direction.Right;
                }
            }
            __state = new Saved { Active = true, Water = __instance.inWater, Swimming = __instance.swimming, Standing = __instance.forceStanding, LockTimer = __instance.swimmingStateLockTimer };
            __instance.inWater = false; __instance.swimming = false; __instance.forceStanding = true;
        }
        private static void End(HumanoidAnimController __instance, Saved __state)
        {
            // Harmony finalizer 在原方法抛异常时也能恢复状态；不吞掉原异常。
            if (!__state.Active) return;
            __instance.inWater = __state.Water; __instance.swimming = __state.Swimming; __instance.forceStanding = __state.Standing;
            __instance.swimmingStateLockTimer = __state.LockTimer;
        }
        private static bool WallPose(HumanoidAnimController __instance)
        {
            var boots = MagneticBoots.Get(__instance.Character);
            if (boots?.IsOn != true || !boots.WallWalk.Active || MagneticBoots.NeedsFootAnchors(__instance.Character)) return true;
            boots.WallWalk.Pose(__instance);
            return false;
        }
    }
}
