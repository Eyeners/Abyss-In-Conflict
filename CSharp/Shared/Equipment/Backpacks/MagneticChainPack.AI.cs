using System;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    partial class MagneticChainPack
    {
        private Vector2 aiMovement;
        private double supportProbeTime;
        private Vector2 supportProbePosition;
        private bool supportProbeResult;
        private double nextProgressCheck, lastProgressTime, nextForcedRepath, lastAIIntent;
        private Vector2 lastProgressPosition;
        private Character progressOwner;
        private Submarine progressSub;
        private void UpdateAIRecovery(Character c)
        {
            if (!c.IsAIControlled || GameMain.NetworkMember?.IsClient == true) return;
            double now = Timing.TotalTime;
            if (now < nextProgressCheck) return;
            nextProgressCheck = now + 0.25;
            Vector2 position = c.WorldPosition - (c.Submarine?.Position ?? Vector2.Zero);
            if (progressOwner != c || progressSub != c.Submarine || Vector2.DistanceSquared(position, lastProgressPosition) > 400)
            {
                progressOwner = c; progressSub = c.Submarine;
                lastProgressPosition = position; lastProgressTime = now;
                return;
            }
            var ai = c.AIController as HumanAIController;
            var steering = ai?.PathSteering;
            if (steering == null) return;
            var goal = ai.ObjectiveManager?.GetLastActiveObjective<AIObjectiveGoTo>();
            bool travelling = goal != null && !goal.Abandon && !goal.IsCompleted && !goal.IsCloseEnough
                && goal.Target != null && goal.Target != c && !(goal.Target is Entity entity && entity.Removed);
            var path = steering.CurrentPath;
            bool stalledPath = path != null && !path.Finished && !path.Unreachable && path.CurrentNode != null
                && now - lastAIIntent < 4;
            if (!travelling && !stalledPath) { lastProgressTime = now; return; }
            if (now - lastProgressTime < 2 || now < nextForcedRepath) return;
            // This runs independently of GoTo.Act, including after native steering stops issuing movement.
            nextForcedRepath = now + 2;
            var node = path?.CurrentNode;
            steering.ResetPath();
            steering.findPathTimer = -0.01f;
            if (travelling) steering.SteeringSeek(c.GetRelativeSimPosition(goal.Target), 1);
            else if (node != null && !node.Removed) steering.SteeringSeek(c.GetRelativeSimPosition(node), 1);
            supportProbeTime = 0;
            scanTimer = 0;
            // Renew anchors one at a time, retaining the others while the path is rebuilt.
            int count = 0; foreach (var grip in grips) if (Ready(grip)) count++;
            if (count > 1)
            {
                for (int i = 0; i < grips.Length; i++)
                    if (Ready(grips[i]) && FindGrip(c, i, aiMovement)) { Changed(); break; }
            }
        }
        internal readonly SpaceNavigationRecovery Navigation = new();
        internal Vector2 ConstrainRecovery(Character c, Vector2 desired) => LimitAI(c, desired, forceSupportCheck: true);
        private static MapEntity AnchorEntity(Fixture fixture)
        {
            if (fixture == null || fixture.IsSensor || !fixture.Body.Enabled) return null;
            if (fixture.Body.UserData is Structure wall && !wall.Removed) return wall;
            if (fixture.Body.UserData is Item item && !item.Removed)
            {
                if (item.GetComponent<Door>() is Door door) return door.IsOpen ? null : item;
                if (fixture.Body.BodyType == BodyType.Static && item.GetComponent<Ladder>() == null) return item;
            }
            return null;
        }
        internal static bool FreeNavigation(Character c) => c?.IsAIControlled == true
            && c.AnimController is HumanoidAnimController anim && Equipped(c)?.Supported(anim) == true;
        internal void CaptureAIInput(Character c)
        {
            if (!c.IsAIControlled) { aiMovement = Vector2.Zero; return; }
            // Read native steering before the suspended animation temporarily zeros TargetMovement.
            aiMovement = SpaceBackpackAI.DesiredVelocity(c, 20);
            if (aiMovement.LengthSquared() > 0.01f) lastAIIntent = Timing.TotalTime;
            float max = Math.Max(0.1f, MoveSpeed);
            if (aiMovement.LengthSquared() > max * max) aiMovement = Vector2.Normalize(aiMovement) * max;
            aiMovement = LimitAI(c, aiMovement);
        }
        private Vector2 LimitAI(Character c, Vector2 desired, bool forceSupportCheck = false)
        {
            if (!c.IsAIControlled || desired.LengthSquared() < 0.0001f) return desired;
            var path = (c.AIController as HumanAIController)?.PathSteering?.CurrentPath;
            if (!forceSupportCheck && !Navigation.IsRecovering && path != null && !path.Unreachable && !path.Finished && path.CurrentNode != null) return desired;
            // Wandering/combat steering cannot drift outside the current support envelope.
            Vector2 next = c.WorldPosition + ConvertUnits.ToDisplayUnits(desired * 0.35f);
            float limit = Math.Max(0, Math.Min(400, Reach) - 10);
            foreach (var grip in grips)
                if (Ready(grip) && Vector2.DistanceSquared(next, grip.World) <= limit * limit) return desired;
            // An old set of anchors must not prevent moving toward a new reachable wall.
            return ProbeSupport(next, limit) ? desired : Vector2.Zero;
        }
        private bool ProbeSupport(Vector2 position, float range)
        {
            if (Timing.TotalTime < supportProbeTime && Vector2.DistanceSquared(position, supportProbePosition) < 1600)
                return supportProbeResult;
            supportProbeTime = Timing.TotalTime + 0.15;
            supportProbePosition = position;
            supportProbeResult = false;
            void Scan(Submarine sub)
            {
                var start = ConvertUnits.ToSimUnits(position - (sub?.Position ?? Vector2.Zero));
                for (int i = 0; i < 16 && !supportProbeResult; i++)
                {
                    float angle = i * MathHelper.TwoPi / 16;
                    var end = start + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * ConvertUnits.ToSimUnits(range);
                    GameMain.World.RayCast((fixture, point, normal, fraction) =>
                    {
                        var entity = AnchorEntity(fixture);
                        if (entity == null || entity.Submarine != sub) return -1;
                        supportProbeResult = true;
                        return 0;
                    }, start, end);
                }
            }
            Scan(null);
            foreach (var sub in Submarine.Loaded) { if (supportProbeResult) break; Scan(sub); }
            return supportProbeResult;
        }
    }
}
