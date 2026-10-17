using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using FarseerPhysics.Dynamics.Joints;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // 平直竖墙的测试控制器；使用原版 FootIK，不增加动画资源或修改实际 InWater。
    internal sealed class MagneticWallWalk
    {
        internal bool Active { get; private set; }
        internal static readonly HashSet<MagneticWallWalk> ActiveWalkers = new HashSet<MagneticWallWalk>();
        private Structure wall;
        private Submarine characterSub;
        private Vector2 localPoint, normal;
        private Body wallBody;
        private readonly RevoluteJoint[] footJoints = new RevoluteJoint[2];
        private readonly Limb[] anchoredFeet = new Limb[2];
        private World jointWorld;
        private float scanTimer, cooldown, phase, climb;
        private const float ExtraWallDistance = 0.05f;
        private static readonly LimbType[] Feet = { LimbType.LeftFoot, LimbType.RightFoot };
        internal void Reset()
        {
            ReleaseFeet();
            ActiveWalkers.Remove(this);
            Active = false;
            wall = null;
            wallBody = null;
            scanTimer = 0;
            cooldown = 0.3f;
        }
        private void ReleaseFeet()
        {
            for (int i = 0; i < footJoints.Length; i++)
                ReleaseFoot(i);
        }
        private void ReleaseFoot(int i)
        {
                var joint = footJoints[i];
                if (joint != null)
                {
                    joint.Enabled = false;
                    if (jointWorld != null && jointWorld.JointList.Contains(joint)) jointWorld.Remove(joint);
                }
                footJoints[i] = null;
                anchoredFeet[i] = null;
        }
        private void AttachFoot(int i, Limb foot)
        {
            if (footJoints[i] != null || foot == null || foot.Removed || foot.IsSevered
                || foot.body == null || !foot.body.Enabled || wallBody == null || !wallBody.Enabled) return;
            Vector2 solePoint = foot.SimPosition - normal * Math.Max(foot.body.Radius, 0.02f);
            if (Math.Abs(Vector2.Dot(solePoint - localPoint, normal)) >= 0.25f) return;
            Vector2 contact = new Vector2(localPoint.X, solePoint.Y);
            bool supported = false;
            GameMain.World.RayCast((fixture, point, n, fraction) =>
            {
                if (fixture.IsSensor || fixture.Body != wallBody) return -1;
                supported = true; return 0;
            }, contact + normal * 0.1f, contact - normal * 0.1f);
            if (!supported) return;
            var joint = new RevoluteJoint(foot.body.FarseerBody, wallBody,
                foot.body.FarseerBody.GetLocalPoint(solePoint), wallBody.GetLocalPoint(contact), false);
            joint.CollideConnected = true;
            jointWorld = GameMain.World;
            jointWorld.Add(joint);
            footJoints[i] = joint;
            anchoredFeet[i] = foot;
        }
        internal bool Update(Character user, bool enabled, float dt)
        {
            cooldown -= dt;
            var anim = user.AnimController as HumanoidAnimController;
            bool limp = MagneticBoots.NeedsFootAnchors(user);
            if (!enabled || anim?.Collider == null
                || (user.IsClimbing && !limp) || (user.IsAIControlled && !Active && !limp) || anim.AnimationTestPose
                || user.CurrentHull == null || user.Submarine == null || GameMain.World.Gravity.LengthSquared() > 0.01f)
            { Reset(); return false; }
            if (Active && (wall == null || wall.Removed || wallBody == null || !wallBody.Enabled || characterSub != user.Submarine)) Reset();
            bool hasAnchor = false;
            for (int i = 0; i < Feet.Length; i++)
            {
                var foot = anchoredFeet[i];
                if (footJoints[i] != null && (foot == null || foot.Removed || foot.IsSevered
                    || anim.GetLimb(Feet[i]) != foot || !jointWorld.JointList.Contains(footJoints[i]))) ReleaseFoot(i);
                hasAnchor |= footJoints[i] != null;
            }
            // 失控后不再执行行走姿态或主碰撞体速度控制，物理关节单独保持脚部吸附。
            if (limp)
            {
                if (!Active) return false;
                PreserveLimp(anim);
                return true;
            }
            bool left = user.IsKeyDown(InputType.Left), right = user.IsKeyDown(InputType.Right);
            if (Active && ((normal.X > 0 && right) || (normal.X < 0 && left)))
            { Reset(); return false; }
            scanTimer -= dt;
            bool moving = user.IsKeyDown(InputType.Up) || user.IsKeyDown(InputType.Down);
            if (scanTimer <= 0 && (Active || cooldown <= 0) && !(Active && hasAnchor && !moving))
            {
                scanTimer = 0.1f;
                Vector2 direction = Active ? -normal : (right ? Vector2.UnitX : -Vector2.UnitX);
                Vector2 start = anim.Collider.SimPosition;
                float reach = Active ? 1.8f : 1.2f;
                Structure found = null;
                Body foundBody = null;
                Vector2 hit = default, hitNormal = default;
                float closest = 1;
                void Scan(Submarine sub)
                {
                    Vector2 offset = sub == user.Submarine ? Vector2.Zero : ConvertUnits.ToSimUnits(sub.Position);
                    GameMain.World.RayCast((fixture, point, n, fraction) =>
                    {
                        if (fixture.IsSensor || fixture.Body.UserData is not Structure structure || structure.Removed
                            || structure.Submarine != sub || Math.Abs(n.X) < 0.9f
                            || Vector2.Dot(n, direction) > -0.9f || fraction > closest) return -1;
                        found = structure; foundBody = fixture.Body; hit = point + offset; hitNormal = n; closest = fraction;
                        return fraction;
                    }, start - offset, start + direction * reach - offset);
                }
                Scan(user.Submarine);
                if (!Active && !left && !right)
                {
                    direction = -direction;
                    Scan(user.Submarine);
                }
                if (found == null) { Reset(); return false; }
                if (wallBody != foundBody) ReleaseFeet();
                wall = found;
                wallBody = foundBody;
                characterSub = user.Submarine;
                normal = hitNormal.X > 0 ? Vector2.UnitX : -Vector2.UnitX;
                localPoint = hit - Offset(user);
                Active = true;
                ActiveWalkers.Add(this);
            }
            if (!Active) return false;
            climb = (user.IsKeyDown(InputType.Up) ? 1 : 0) - (user.IsKeyDown(InputType.Down) ? 1 : 0);
            phase += Math.Abs(climb) * dt * 7;
            if (climb != 0) ReleaseFeet();
            // 客户端不为其他玩家重写物理速度，交给原版同步。
            if (GameMain.NetworkMember is { IsClient: true } && user.IsRemotelyControlled) return true;
            Vector2 surface = localPoint + Offset(user);
            // 恢复腿长方案，在原先贴墙距离上增加五厘米，减少屈膝。
            float height = (anim.upperLegLength + anim.lowerLegLength) * 0.85f + 0.05f + ExtraWallDistance;
            float error = height - Vector2.Dot(anim.Collider.SimPosition - surface, normal);
            Vector2 carrier = user.Submarine == null && wall.Submarine != null ? wall.Submarine.Velocity : Vector2.Zero;
            anim.Collider.LinearVelocity = carrier + normal * MathHelper.Clamp(error * 8, -2, 2) + Vector2.UnitY * climb * 1.2f;
            // 抵消实际世界重力，而不是额外施加固定标准重力；仅在主动贴墙时生效。
            anim.Collider.ApplyForce(-GameMain.World.Gravity * anim.Collider.Mass);
            foreach (var limb in anim.Limbs)
                if (!limb.Removed && !limb.IsSevered && limb.body != null && limb.body.Enabled)
                    limb.body.ApplyForce(-GameMain.World.Gravity * limb.body.Mass);
            return true;
        }
        private Vector2 Offset(Character user) => user.Submarine == null && wall?.Submarine != null
            ? ConvertUnits.ToSimUnits(wall.Submarine.Position) : Vector2.Zero;

        internal void PreserveLimp(HumanoidAnimController anim)
        {
            if (!Active || wall == null || wall.Removed || wallBody == null || !wallBody.Enabled) return;
            climb = 0;
            for (int i = 0; i < Feet.Length; i++) AttachFoot(i, anim.GetLimb(Feet[i]));
        }

        internal void Pose(HumanoidAnimController anim)
        {
            var p = anim.CurrentSwimParams;
            var torso = anim.GetLimb(LimbType.Torso);
            if (p == null || torso == null) return;
            if (!anim.Character.IsRemotelyControlled)
            {
                Vector2 tangent = new Vector2(0, -normal.X);
                float cursorAlongWall = Vector2.Dot(ConvertUnits.ToSimUnits(anim.Character.CursorPosition) - torso.SimPosition, tangent);
                if (Math.Abs(cursorAlongWall) > 0.4f)
                    anim.TargetDir = cursorAlongWall > 0 ? Direction.Right : Direction.Left;
            }
            bool turning = (anim.TargetDir == Direction.Right ? 1 : -1) != anim.Dir;
            if (turning) ReleaseFeet();
            float angle = normal.X > 0 ? -MathHelper.PiOver2 : MathHelper.PiOver2;
            anim.Collider.FarseerBody.FixedRotation = false;
            anim.Collider.SmoothRotate(angle, p.SteerTorque * 2);
            var waist = anim.GetLimb(LimbType.Waist);
            if (waist != null && !waist.Disabled)
            {
                waist.body.MoveToPos(anim.Collider.SimPosition, 10);
                waist.body.SmoothRotate(angle, p.TorsoTorque);
            }
            torso.body.MoveToPos(anim.Collider.SimPosition + normal * 0.25f, 10);
            torso.body.SmoothRotate(angle + (anim.TorsoAngle ?? 0) * anim.Dir, p.TorsoTorque);
            var head = anim.GetLimb(LimbType.Head);
            if (head != null && !head.Disabled)
            {
                head.body.MoveToPos(torso.SimPosition + normal * 0.35f, 5);
                anim.RotateHead(head);
            }
            Vector2 surface = localPoint + Offset(anim.Character);
            for (int i = 0; i < Feet.Length; i++)
            {
                var foot = anim.GetLimb(Feet[i]);
                if (foot == null || foot.Removed || foot.Disabled || foot.IsSevered)
                { ReleaseFoot(i); continue; }
                float cycle = phase + i * MathHelper.Pi;
                float step = climb == 0 ? 0 : (float)Math.Cos(cycle) * 0.22f;
                float lift = climb == 0 ? 0 : Math.Max(0, (float)Math.Sin(cycle)) * 0.08f;
                Vector2 target = new Vector2(surface.X, anim.Collider.SimPosition.Y + step + (i == 0 ? -0.1f : 0.1f));
                float sole = Math.Max(foot.body.Radius, 0.02f);
                target += normal * (sole + lift);
                anim.FootIK(foot, target, p.LegTorque, p.FootTorque, p.FootAngleInRadians);
                if (climb == 0 && !turning && wallBody != null && wallBody.Enabled)
                {
                    AttachFoot(i, foot);
                }
            }
            anim.levitatingCollider = false;
        }
    }
}
