using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Owned by the backpack, so unequipping cannot leave a global character cache behind.
    internal sealed class SpaceNavigationRecovery
    {
        private AIObjectiveGoTo objective;
        private double nextCheck, nextRepath, lastProgress, nativeGraceUntil, recoveryUntil;
        private Vector2 progressPosition, velocity;
        private float bestDistance;
        private bool recovering;
        private double lastUse;
        internal bool IsRecovering => recovering && Timing.TotalTime - lastUse < 0.3;
        private WayPoint rejoin;
        private int nodeCursor;
        private static readonly Vector2[] Rotations = CreateRotations();
        private readonly Dictionary<Vector2, bool> clearCache = new Dictionary<Vector2, bool>(64);
        private readonly Predicate<Fixture> visibilityFilter;
        private Submarine querySubmarine;

        internal SpaceNavigationRecovery() { visibilityFilter = BlocksVisibility; }
        private bool BlocksVisibility(Fixture fixture) => fixture.Body.UserData is Entity entity
            ? entity.Submarine == querySubmarine : querySubmarine == null;
        private static Vector2[] CreateRotations()
        {
            float[] angles = { 0, 0.4f, -0.4f, 0.8f, -0.8f, 1.2f, -1.2f, 1.57f, -1.57f, 2.2f, -2.2f };
            var rotations = new Vector2[angles.Length];
            for (int i = 0; i < angles.Length; i++)
                rotations[i] = new Vector2((float)Math.Cos(angles[i]), (float)Math.Sin(angles[i]));
            return rotations;
        }

        internal void Reset() { objective = null; recovering = false; rejoin = null; velocity = Vector2.Zero; nativeGraceUntil = recoveryUntil = 0; }

        internal bool TryMove(Character user, AIObjectiveGoTo goal, out Vector2 result)
        {
            result = Vector2.Zero;
            if (!SpaceBackpackAI.Applies(user) || goal == null || goal.Abandon || goal.IsCompleted
                || goal.Target == null || goal.Target == user || goal.IsCloseEnough
                || user.SelectedBy != null || goal.Target.Submarine != user.Submarine)
            { Reset(); return false; }
            // The fallback never creates an exit route for an objective forbidden from going outside.
            if (!goal.AllowGoingOutside && user.CurrentHull != null && goal.GetTargetHull() == null)
            { Reset(); return false; }
            double now = Timing.TotalTime;
            lastUse = now;
            Vector2 target = goal.Target.WorldPosition;
            float distance = Vector2.Distance(user.WorldPosition, target);
            if (objective != goal)
            {
                Reset(); objective = goal;
                progressPosition = user.WorldPosition; bestDistance = distance;
                lastProgress = now; nextCheck = nextRepath = 0;
            }
            if (now < nextCheck) { result = velocity; return recovering; }
            nextCheck = now + 0.2;
            // Only cache within this decision: movement, submarine changes and closing doors are
            // always rechecked next time, without per-character global state or per-ray closures.
            clearCache.Clear();
            querySubmarine = user.Submarine;
            var steering = (user.AIController as HumanAIController)?.PathSteering;
            var path = steering?.CurrentPath;
            bool valid = path != null && !path.Unreachable && !path.Finished && path.CurrentNode != null;
            bool targetClear = Clear(user, target);
            // A sparse network may still offer a valid path whose first node is far behind us.
            // Do not let that path pull a freely flying character away from an unobstructed target.
            bool leavingNetwork = valid && targetClear
                && !SupportsDirectTravel(user.WorldPosition, target, path.CurrentNode.WorldPosition);
            if (distance < bestDistance - 20 || Vector2.DistanceSquared(progressPosition, user.WorldPosition) > 10000)
            {
                lastProgress = now; bestDistance = distance; progressPosition = user.WorldPosition;
            }
            bool stalled = now >= nativeGraceUntil && now - lastProgress > 2;
            if (!recovering && valid && !stalled && !leavingNetwork) return false;
            if (!recovering && now < nativeGraceUntil && !leavingNetwork) return false;
            if (recovering && valid && CanResume(user, path.CurrentNode, target, targetClear, now))
                return ResumeNative(user, steering, distance, now);
            if (!recovering && HumanAIController.DebugAI)
                LuaCsLogger.Log("[Space navigation] " + user.Name + ": fallback (" + (leavingNetwork ? "direct target, leaving network" : valid ? "stalled" : "no path") + ")");
            if (!recovering) recoveryUntil = now + 1;
            recovering = true;
            if (targetClear) rejoin = null;
            if (now >= nextRepath)
            {
                nextRepath = now + 2;
                // Give the native pathfinder another opportunity from the new position.
                if (steering != null)
                {
                    // Merely setting IsPathDirty does not force CalculateSteeringSeek to rebuild.
                    // The recovery retry is already throttled here, independently of the native timer.
                    steering.ResetPath();
                    steering.findPathTimer = -0.01f;
                    steering.SteeringSeek(user.GetRelativeSimPosition(goal.Target), 1);
                    path = steering.CurrentPath;
                    if (path != null && !path.Unreachable && !path.Finished && path.CurrentNode != null
                        && CanResume(user, path.CurrentNode, target, targetClear, now))
                        return ResumeNative(user, steering, distance, now);
                }
                rejoin = targetClear ? null : FindNode(user, target);
            }
            if (rejoin != null && (rejoin.Removed || Vector2.DistanceSquared(user.WorldPosition, rejoin.WorldPosition) < 6400))
            { rejoin = null; nextRepath = 0; lastProgress = now; }
            Vector2 destination = rejoin?.WorldPosition ?? target;
            Vector2 diff = destination - user.WorldPosition;
            if (diff.LengthSquared() < 1) { velocity = Vector2.Zero; result = velocity; return true; }
            float destinationDistance = diff.Length();
            Vector2 forward = diff / destinationDistance;
            float maxReach = Math.Min(destinationDistance, 240);
            float margin = rejoin == null ? Math.Max(30, goal.CloseEnough) : 40;
            float remaining = Math.Max(0, destinationDistance - margin);
            float bestScore = float.NegativeInfinity;
            velocity = Vector2.Zero;
            for (int i = 0; i < Rotations.Length; i++)
            {
                float c = Rotations[i].X, s = Rotations[i].Y;
                Vector2 dir = new Vector2(forward.X * c - forward.Y * s, forward.X * s + forward.Y * c);
                float alignment = Vector2.Dot(dir, forward);
                // Conservative upper bound: shortening a blocked ray can only reduce its score.
                // Keep the original order and all directions; discard only candidates unable to win.
                if (alignment + maxReach / 240f * 0.8f < bestScore - 0.0001f) continue;
                float reach = maxReach;
                bool knownClear = i == 0 && rejoin == null && targetClear;
                while (reach >= 40 && !knownClear && !Clear(user, user.WorldPosition + dir * reach)) reach *= 0.5f;
                if (reach < 40) continue;
                float score = alignment + reach / 240f * 0.8f;
                if (score <= bestScore) continue;
                bestScore = score;
                float speed = Math.Min(1.5f, Math.Min(reach / 160f, remaining / 100f));
                velocity = dir * speed;
            }
            result = velocity;
            return true;
        }

        internal static bool SupportsDirectTravel(Vector2 position, Vector2 target, Vector2 node)
        {
            Vector2 toTarget = target - position;
            Vector2 toNode = node - position;
            if (toNode.LengthSquared() > 150 * 150) return false;
            if (toTarget.LengthSquared() < 1) return true;
            // Allow small node-position tolerances, but not a return to a node substantially behind us.
            return Vector2.Dot(toNode, Vector2.Normalize(toTarget)) >= -30;
        }

        private bool CanResume(Character user, WayPoint node, Vector2 target, bool targetClear, double now)
        {
            if (now < recoveryUntil || node == null || node.Removed) return false;
            if (Vector2.DistanceSquared(user.WorldPosition, node.WorldPosition) > 150 * 150) return false;
            if (targetClear && !SupportsDirectTravel(user.WorldPosition, target, node.WorldPosition)) return false;
            return CanReachPath(user, node);
        }

        private bool CanReachPath(Character user, WayPoint node)
        {
            // Path points can sit close to the floor/door frame: the swept-body test can reject a
            // node we have already reached. In that case let native node advancement/door logic act.
            return node != null && !node.Removed && node.Submarine == user.Submarine
                && ((Vector2.DistanceSquared(user.WorldPosition, node.WorldPosition) <= 10000
                        && Submarine.CheckVisibility(user.SimPosition,
                            user.SimPosition + ConvertUnits.ToSimUnits(node.WorldPosition - user.WorldPosition),
                            ignoreLevel: user.Submarine != null, ignoreSubs: true,
                            blocksVisibilityPredicate: visibilityFilter) == null)
                    || Clear(user, node.WorldPosition));
        }

        private bool ResumeNative(Character user, IndoorsSteeringManager steering, float distance, double now)
        {
            recovering = false;
            velocity = Vector2.Zero;
            rejoin = null;
            bestDistance = distance;
            progressPosition = user.WorldPosition;
            lastProgress = now;
            nativeGraceUntil = now + 2.5;
            // HumanAIController may still have selected outside steering earlier in this update.
            user.AIController.steeringManager = steering;
            steering.Reset();
            if (HumanAIController.DebugAI)
                LuaCsLogger.Log("[Space navigation] " + user.Name + ": native path resumed");
            // Allow AIObjectiveGoTo.Act to execute normally in this same frame.
            return false;
        }

        private WayPoint FindNode(Character user, Vector2 target)
        {
            WayPoint best = null;
            float bestScore = float.PositiveInfinity;
            int count = WayPoint.WayPointList.Count;
            int rays = 0;
            for (int i = 0; i < Math.Min(count, 256); i++)
            {
                nodeCursor %= count;
                var node = WayPoint.WayPointList[nodeCursor++];
                if (node.Removed || node.Submarine != user.Submarine || node.SpawnType != SpawnType.Path) continue;
                float distance = Vector2.Distance(user.WorldPosition, node.WorldPosition);
                if (distance < 80 || distance > 800) continue;
                float score = distance + Vector2.Distance(node.WorldPosition, target) * 0.7f;
                if (score >= bestScore) continue;
                if (++rays > 8) break;
                if (!Clear(user, node.WorldPosition)) continue;
                best = node; bestScore = score;
            }
            return best;
        }

        private bool Clear(Character user, Vector2 endWorld)
        {
            if (clearCache.TryGetValue(endWorld, out bool cached)) return cached;
            Vector2 start = user.SimPosition;
            Vector2 end = start + ConvertUnits.ToSimUnits(endWorld - user.WorldPosition);
            Vector2 delta = end - start;
            if (delta.LengthSquared() < 0.001f) { clearCache[endWorld] = true; return true; }
            Vector2 side = Vector2.Normalize(new Vector2(-delta.Y, delta.X))
                * Math.Max(0.15f, user.AnimController.Collider.GetSize().X * 0.5f);
            for (int i = -1; i <= 1; i++)
            {
                if (Submarine.CheckVisibility(start + side * i, end + side * i,
                    ignoreLevel: user.Submarine != null, ignoreSubs: true,
                    blocksVisibilityPredicate: visibilityFilter) != null)
                { clearCache[endWorld] = false; return false; }
            }
            clearCache[endWorld] = true;
            return true;
        }
    }
}
