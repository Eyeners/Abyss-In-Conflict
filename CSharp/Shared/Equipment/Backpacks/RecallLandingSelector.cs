using System;
using System.Collections.Generic;
using Barotrauma;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Per-backpack local geometry cache. Store entities, not world positions, so moving
    // submarines do not invalidate the cached connections or their sampling positions.
    internal sealed class RecallLandingSelector
    {
        private readonly List<WayPoint> nodes = new();
        private readonly List<(WayPoint A, WayPoint B)> edges = new();
        private readonly List<Vector2> route = new();
        private readonly List<(Vector2 Position, float Score)> ranked = new();
        private Submarine submarine;
        private Vector2 center;
        private double refresh;
        private int nodeCount;

        private void Refresh(Character user, Submarine searchSubmarine)
        {
            Vector2 local = user.WorldPosition - (searchSubmarine?.Position ?? Vector2.Zero);
            if (submarine == searchSubmarine && Timing.TotalTime < refresh
                && nodeCount == WayPoint.WayPointList.Count && Vector2.DistanceSquared(center, local) < 200 * 200) return;
            submarine = searchSubmarine; center = local; refresh = Timing.TotalTime + 2;
            nodeCount = WayPoint.WayPointList.Count;
            nodes.Clear(); edges.Clear();
            foreach (var node in WayPoint.WayPointList)
            {
                if (!Valid(node) || node.Submarine != submarine) continue;
                bool connected = false;
                foreach (var entity in node.linkedTo)
                {
                    if (!(entity is WayPoint other) || !Valid(other) || other.Submarine != submarine) continue;
                    connected = true;
                    if (node.ID >= other.ID) continue;
                    if (DistanceToSegment(user.WorldPosition, node.WorldPosition, other.WorldPosition) <= 1000)
                        edges.Add((node, other));
                }
                if (connected && Vector2.DistanceSquared(user.WorldPosition, node.WorldPosition) <= 1000 * 1000)
                    nodes.Add(node);
            }
        }

        private static bool Valid(WayPoint node) => !node.Removed && node.SpawnType == SpawnType.Path
            && node.CurrentHull != null;

        internal bool HasNearbyConnection(Character user)
        {
            if (user.CurrentHull == null) return false;
            Refresh(user, user.Submarine);
            int checks = 0;
            foreach (var edge in edges)
            {
                if (!Valid(edge.A) || !Valid(edge.B) || !edge.A.linkedTo.Contains(edge.B)) continue;
                Vector2 closest = ClosestPoint(user.WorldPosition, edge.A.WorldPosition, edge.B.WorldPosition);
                if (Vector2.DistanceSquared(closest, user.WorldPosition) > 200 * 200
                    || Hull.FindHull(closest, user.CurrentHull, useWorldCoordinates: true) != user.CurrentHull) continue;
                if (Vector2.DistanceSquared(closest, user.WorldPosition) < 1) return true;
                if (++checks > 6) break;
                Vector2 end = user.SimPosition + ConvertUnits.ToSimUnits(closest - user.WorldPosition);
                if (Submarine.PickBody(user.SimPosition, end,
                    collisionCategory: Physics.CollisionWall | Physics.CollisionRepairableWall | Physics.CollisionLevel,
                    customPredicate: fixture => !fixture.IsSensor && (fixture.Body.UserData is Entity entity
                        ? entity.Submarine == user.Submarine : user.Submarine == null), allowInsideFixture: true) == null) return true;
            }
            return false;
        }

        // mode: 0 retreat, 1 behind enemy, 2 approach. No new enemy pathfinding is run.
        internal bool TrySelect(Character user, Character enemy, int mode, Func<Vector2, Vector2?> resolve,
            out Vector2 result, Submarine searchSubmarine, Func<Vector2, bool> accept = null)
        {
            Refresh(user, searchSubmarine);
            route.Clear();
            var steering = enemy.AIController is HumanAIController human ? human.PathSteering
                : (enemy.AIController as EnemyAIController)?.PathSteering;
            var path = steering?.CurrentPath;
            if (path != null && !path.Unreachable && !path.Finished && path.CurrentNode != null)
            {
                route.Add(enemy.WorldPosition);
                for (int i = path.CurrentIndex; i < path.Nodes.Count && route.Count < 33; i++)
                {
                    var node = path.Nodes[i];
                    if (node.Removed) break;
                    route.Add(node.WorldPosition);
                }
            }
            // Only rank cheap geometry first. At most six line samples and four original
            // nodes reach the expensive support/space/visibility checks in each group.
            ranked.Clear();
            foreach (var edge in edges)
            {
                if (!Valid(edge.A) || !Valid(edge.B) || !edge.A.linkedTo.Contains(edge.B)) continue;
                Vector2 a = edge.A.WorldPosition, b = edge.B.WorldPosition;
                if (Math.Abs(b.Y - a.Y) > Math.Abs(b.X - a.X) * 0.5f) continue;
                if (!ClipSegment(user.WorldPosition, a, b, 799, out float start, out float end)) continue;
                Add(Vector2.Lerp(a, b, start));
                Add(Vector2.Lerp(a, b, end));
                Add(Vector2.Lerp(a, b, (start + end) * 0.5f));
            }
            if (Pick(6, out result)) return true;
            ranked.Clear();
            foreach (var node in nodes)
                if (Valid(node)) Add(node.WorldPosition);
            return Pick(4, out result);

            void Add(Vector2 position)
            {
                if (accept != null && !accept(position)) return;
                float jump = Vector2.Distance(user.WorldPosition, position);
                float remaining = Vector2.Distance(enemy.WorldPosition, position);
                float score;
                if (mode == 2)
                {
                    if (jump < 200 || jump > 800 || remaining < 600
                        || remaining >= Vector2.Distance(user.WorldPosition, enemy.WorldPosition) - 100) return;
                    score = remaining;
                }
                else
                {
                    score = RecallBackpack.CombatLandingScore(user.WorldPosition, enemy.WorldPosition,
                        enemy.AnimController.Dir, position, mode == 1);
                    if (!float.IsFinite(score)) return;
                }
                float clearance = float.PositiveInfinity;
                for (int i = 1; i < route.Count; i++)
                    clearance = Math.Min(clearance, DistanceToSegment(position, route[i - 1], route[i]));
                score -= TacticalBonus(float.IsFinite(clearance) ? clearance : 0, position.Y - enemy.WorldPosition.Y);
                foreach (var candidate in ranked)
                    if (Vector2.DistanceSquared(candidate.Position, position) < 25 * 25) return;
                int index = 0;
                while (index < ranked.Count && ranked[index].Score <= score) index++;
                if (index >= 6) return;
                ranked.Insert(index, (position, score));
                if (ranked.Count > 6) ranked.RemoveAt(6);
            }
            bool Pick(int budget, out Vector2 selected)
            {
                for (int i = 0; i < Math.Min(budget, ranked.Count); i++)
                {
                    var position = resolve(ranked[i].Position);
                    if (!position.HasValue) continue;
                    if (accept != null && !accept(position.Value)) continue;
                    float jump = Vector2.Distance(user.WorldPosition, position.Value);
                    float remaining = Vector2.Distance(enemy.WorldPosition, position.Value);
                    if (mode == 2)
                    {
                        if (jump < 200 || jump > 800 || remaining < 600
                            || remaining >= Vector2.Distance(user.WorldPosition, enemy.WorldPosition) - 100) continue;
                    }
                    else if (!float.IsFinite(RecallBackpack.CombatLandingScore(user.WorldPosition,
                        enemy.WorldPosition, enemy.AnimController.Dir, position.Value, mode == 1))) continue;
                    selected = position.Value;
                    return true;
                }
                selected = default;
                return false;
            }
        }

        internal static float TacticalBonus(float routeDistance, float height)
            => MathHelper.Clamp(routeDistance, 0, 400) * 0.65f + MathHelper.Clamp(height, 0, 300) * 0.5f;

        internal static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
            => Vector2.Distance(point, ClosestPoint(point, a, b));

        private static Vector2 ClosestPoint(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 delta = b - a;
            float length = delta.LengthSquared();
            float t = length < 0.001f ? 0 : MathHelper.Clamp(Vector2.Dot(point - a, delta) / length, 0, 1);
            return a + delta * t;
        }

        internal static bool ClipSegment(Vector2 origin, Vector2 a, Vector2 b, float radius, out float start, out float end)
        {
            start = end = 0;
            Vector2 delta = b - a, offset = a - origin;
            float length = delta.LengthSquared();
            if (length < 1) return false;
            float projection = Vector2.Dot(offset, delta);
            float discriminant = projection * projection - length * (offset.LengthSquared() - radius * radius);
            if (discriminant < 0) return false;
            float root = (float)Math.Sqrt(discriminant);
            start = Math.Max(0, (-projection - root) / length);
            end = Math.Min(1, (-projection + root) / length);
            return start <= end;
        }
    }
}
