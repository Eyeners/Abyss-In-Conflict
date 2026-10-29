using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    partial class FoehnCharge
    {
        private readonly List<(Limb Limb, bool Enabled, float Force, Vector2 Anchor, Vector2 Target)> dashPose = new();
        internal void ReleaseDashPose()
        {
            foreach (var saved in dashPose)
            {
                var l = saved.Limb;
                if (l.Removed || l.body == null) continue;
                l.PullJointEnabled = saved.Enabled;
                l.PullJointMaxForce = saved.Force;
                l.PullJointWorldAnchorA = l.body.FarseerBody.GetWorldPoint(saved.Anchor);
                l.PullJointWorldAnchorB = saved.Target;
            }
            dashPose.Clear();
        }
        internal void PrepareDashPose(HumanoidAnimController anim)
        {
            ReleaseDashPose();
            if (!Dashing || !DashValid(anim.Character)) return;
            FaceDash(anim);
        }
        private void FaceDash(HumanoidAnimController anim)
        {
            anim.TargetDir = dashDirection > 0 ? Direction.Right : Direction.Left;
            if (anim.Direction != anim.TargetDir) anim.Flip();
        }
        private void PoseDash(Character c)
        {
            var anim = c.AnimController as HumanoidAnimController;
            if (anim == null) return;
            // Native animation/held-item handling still runs; leases are restored before its next update.
            FaceDash(anim);
            float scale = MathHelper.Clamp(anim.RagdollParams.JointScale / 0.5f, 0.5f, 2f);
            Vector2 hips = anim.Collider.SimPosition;
            void Hold(LimbType type, float x, float y, float angle)
            {
                var l = anim.GetLimb(type);
                if (l == null || l.Removed || l.IsSevered || l.body == null || !l.body.Enabled) return;
                if ((type == LimbType.LeftHand || type == LimbType.RightHand) && l.Disabled) return;
                dashPose.Add((l, l.PullJointEnabled, l.PullJointMaxForce, l.PullJointLocalAnchorA, l.PullJointWorldAnchorB));
                l.PullJointWorldAnchorA = l.SimPosition;
                l.PullJointWorldAnchorB = hips + new Vector2(x * dashDirection, y) * scale;
                l.PullJointMaxForce = Math.Max(1, l.Mass) * 220;
                l.PullJointEnabled = true;
                l.body.SmoothRotate(angle * dashDirection, Math.Max(1, l.Mass) * 35);
            }
            Hold(LimbType.Waist, 0, 0, -0.25f);
            Hold(LimbType.Torso, 0.18f, 0.34f, -0.4f);
            Hold(LimbType.Head, 0.3f, 0.69f, -0.12f);
            Hold(LimbType.LeftThigh, 0.25f, -0.19f, 0.85f);
            Hold(LimbType.LeftLeg, 0.25f, -0.47f, -0.8f);
            Hold(LimbType.LeftFoot, 0.08f, -0.65f, 0);
            Hold(LimbType.RightThigh, -0.23f, -0.24f, -0.65f);
            Hold(LimbType.RightLeg, -0.45f, -0.5f, -0.45f);
            Hold(LimbType.RightFoot, -0.62f, -0.7f, -0.35f);
            if (!c.HeldItems.Any())
            {
                Hold(LimbType.LeftHand, 0.52f, 0.38f, 0);
                Hold(LimbType.RightHand, -0.25f, 0.18f, 0);
            }
        }
    }
}
