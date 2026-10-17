using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Collision;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // A vertical RepairTool strip. Query the physics broadphase instead of scanning all items.
    class AICOrbitalDamageTool : RepairTool
    {
        [Serialize(300f, IsPropertySaveable.No)] public float BeamWidth { get; set; }
        [Serialize(8, IsPropertySaveable.No)] public int MaxTargets { get; set; }
        private readonly HashSet<Body> seenBodies = new();
        private readonly HashSet<Entity> selectedTargets = new();
        private readonly HashSet<Character> charactersWithLimbs = new();
        private readonly List<(Entity entity, Body body, Vector2 position, float distance)> hits = new();
        private readonly List<AABB> blockers = new();
        private readonly HashSet<Structure> nearbyWalls = new();
        private readonly HashSet<Character> nearbyCharacters = new();
        private float characterRefresh;
        private float elapsed;

        public AICOrbitalDamageTool(Item item, ContentXElement element) : base(item, element) { }

        // Driven by the emitter so the damage stops at precisely the same lifetime.
        public void DamageTick(float deltaTime, Character attacker)
        {
            elapsed += deltaTime;
            if (elapsed < 0.1f) return;
            float step = elapsed;
            elapsed = 0;
            seenBodies.Clear();
            selectedTargets.Clear();
            charactersWithLimbs.Clear();
            hits.Clear();
            nearbyWalls.Clear();
            characterRefresh -= step;
            if (characterRefresh <= 0)
            {
                characterRefresh = 0.5f;
                nearbyCharacters.Clear();
                // Sleeping limbs may not have broadphase proxies. Refresh this fallback
                // at 2 Hz; damage itself still checks nearby limbs at 10 Hz.
                Vector2 center = Item.WorldPosition - Vector2.UnitY * (Range * 0.5f);
                foreach (var character in Character.CharacterList)
                {
                    if (character.Removed || !character.Enabled || character.AnimController == null) continue;
                    foreach (var limb in character.AnimController.Limbs)
                    {
                        if (limb.Removed || limb.IsSevered || limb.body?.FarseerBody == null) continue;
                        var body = limb.body.FarseerBody;
                        body.GetTransform(out var transform);
                        Vector2 offset = limb.WorldPosition - ConvertUnits.ToDisplayUnits(body.Position);
                        bool near = false;
                        foreach (var fixture in body.FixtureList)
                        {
                            fixture.Shape.ComputeAABB(out AABB bounds, ref transform, 0);
                            Vector2 min = ConvertUnits.ToDisplayUnits(bounds.LowerBound) + offset;
                            Vector2 max = ConvertUnits.ToDisplayUnits(bounds.UpperBound) + offset;
                            // Margin keeps approaching characters in the candidate set.
                            near = max.X >= center.X - BeamWidth * 0.5f - 300 && min.X <= center.X + BeamWidth * 0.5f + 300
                                && max.Y >= center.Y - Range * 0.5f - 300 && min.Y <= center.Y + Range * 0.5f + 300;
                            if (near) break;
                        }
                        if (near) { nearbyCharacters.Add(character); break; }
                    }
                }
            }
            blockers.Clear();
            Collect(null);
            foreach (var sub in Submarine.Loaded)
                if (!sub.Removed) Collect(sub);
            CollectCharacters();
            DamageStructures(step, attacker);
            hits.Sort((a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (!seenBodies.Add(hit.body)) continue;
                // The movement collider is not an extra limb. All actual overlapping limbs
                // receive their own full damage; multiple fixtures on one body do not.
                if (hit.body.UserData is Character character && charactersWithLimbs.Contains(character)) continue;
                if (!selectedTargets.Contains(hit.entity) && selectedTargets.Count >= MaxTargets) continue;
                Vector2 worldHit = ConvertUnits.ToDisplayUnits(hit.position) + (hit.entity.Submarine?.Position ?? Vector2.Zero);
                if (!IsExposed(worldHit)) continue;
                pickedPosition = hit.position;
                if (FixBody(attacker?.Removed == false ? attacker : null, hit.position, step, step, hit.body))
                    selectedTargets.Add(hit.entity);
            }
        }

        private void DamageStructures(float deltaTime, Character attacker)
        {
            if (StructureFixAmount >= 0) return;
            Vector2 top = Item.WorldPosition;
            float left = top.X - BeamWidth * 0.5f, right = top.X + BeamWidth * 0.5f;
            float bottom = top.Y - Range;
            Character user = attacker?.Removed == false ? attacker : null;
            float damage = -StructureFixAmount * deltaTime;
            // A wide strip can overlap several sections of the same wall. Do not
            // reduce it to one synthetic ray hit or require the original user to exist.
            foreach (var wall in nearbyWalls)
            {
                if (wall.Removed || !wall.HasBody || wall.IsPlatform || wall.Indestructible) continue;
                for (int i = 0; i < wall.SectionCount; i++)
                {
                    if (wall.SectionBodyDisabled(i)) continue;
                    // SectionPosition accounts for rotated/flipped walls and submarine offsets.
                    Vector2 center = wall.SectionPosition(i, world: true);
                    Vector2 direction = wall.DirectionUnit;
                    var rect = wall.GetSection(i).rect;
                    float length = wall.SectionLength(i);
                    float thickness = wall.IsHorizontal ? rect.Height : rect.Width;
                    float halfX = (Math.Abs(direction.X) * length + Math.Abs(direction.Y) * thickness) * 0.5f;
                    float halfY = (Math.Abs(direction.Y) * length + Math.Abs(direction.X) * thickness) * 0.5f;
                    if (center.X + halfX < left || center.X - halfX > right
                        || center.Y + halfY < bottom || center.Y - halfY > top.Y) continue;
                    Vector2 contact = new Vector2(MathHelper.Clamp(center.X, left, right), center.Y);
                    if (!IsExposed(contact)) continue;
                    wall.AddDamage(i, damage, user);
                }
            }
        }

        // Reuse one set of nearby indestructible wall bounds for the whole damage tick.
        internal static void CollectBlockers(Vector2 top, float width, float range, List<AABB> blockers)
        {
            blockers.Clear();
            void Query(Submarine sub)
            {
                Vector2 offset = sub?.SimPosition ?? Vector2.Zero;
                Vector2 localTop = ConvertUnits.ToSimUnits(top) - offset;
                var query = new AABB(localTop + ConvertUnits.ToSimUnits(new Vector2(-width * 0.5f, -range)),
                    localTop + ConvertUnits.ToSimUnits(new Vector2(width * 0.5f, 0)));
                GameMain.World.QueryAABB(fixture =>
                {
                    if (fixture.IsSensor || (fixture.CollisionCategories & Physics.CollisionWall) == 0
                        || fixture.Body.UserData is not Structure wall || wall.Removed
                        || wall.Submarine != sub || !wall.Indestructible || !wall.HasBody || wall.IsPlatform) return true;
                    fixture.Body.GetTransform(out var transform);
                    fixture.Shape.ComputeAABB(out AABB bounds, ref transform, 0);
                    if (bounds.UpperBound.X < query.LowerBound.X || bounds.LowerBound.X > query.UpperBound.X
                        || bounds.UpperBound.Y < query.LowerBound.Y || bounds.LowerBound.Y > query.UpperBound.Y) return true;
                    blockers.Add(new AABB(ConvertUnits.ToDisplayUnits(bounds.LowerBound + offset),
                        ConvertUnits.ToDisplayUnits(bounds.UpperBound + offset)));
                    return true;
                }, ref query);
            }
            Query(null);
            foreach (var sub in Submarine.Loaded)
                if (!sub.Removed) Query(sub);
        }

        internal static float StopHeight(Vector2 top, float x, float range, List<AABB> blockers)
        {
            float bottom = top.Y - range;
            foreach (var bounds in blockers)
                if (x >= bounds.LowerBound.X && x <= bounds.UpperBound.X && bounds.LowerBound.Y <= top.Y)
                    bottom = Math.Max(bottom, Math.Min(top.Y, bounds.UpperBound.Y));
            return bottom;
        }

        private bool IsExposed(Vector2 target)
            => target.Y >= StopHeight(Item.WorldPosition, target.X, Range, blockers);

        private void CollectCharacters()
        {
            // Character limbs can be sensors or absent from the active broadphase.
            // Use their actual shape transforms, not cached broadphase proxies.
            Vector2 top = ConvertUnits.ToSimUnits(Item.WorldPosition);
            float halfWidth = ConvertUnits.ToSimUnits(BeamWidth * 0.5f);
            Vector2 min = top + new Vector2(-halfWidth, -ConvertUnits.ToSimUnits(Range));
            Vector2 max = top + new Vector2(halfWidth, 0);
            foreach (var character in nearbyCharacters)
            {
                if (character.Removed || !character.Enabled || character.AnimController == null) continue;
                foreach (var limb in character.AnimController.Limbs)
                {
                    if (limb.Removed || limb.IsSevered || limb.body?.FarseerBody == null) continue;
                    Body body = limb.body.FarseerBody;
                    Vector2 offset = ConvertUnits.ToSimUnits(limb.WorldPosition) - body.Position;
                    body.GetTransform(out var transform);
                    foreach (var fixture in body.FixtureList)
                    {
                        fixture.Shape.ComputeAABB(out AABB bounds, ref transform, 0);
                        Vector2 lower = bounds.LowerBound + offset, upper = bounds.UpperBound + offset;
                        if (upper.X < min.X || lower.X > max.X || upper.Y < min.Y || lower.Y > max.Y) continue;
                        Vector2 worldHit = Vector2.Clamp((lower + upper) * 0.5f, min, max);
                        hits.Add((character, body, worldHit - offset, top.Y - worldHit.Y));
                        charactersWithLimbs.Add(character);
                        break;
                    }
                }
            }
        }

        private void Collect(Submarine sub)
        {
            Vector2 offset = sub?.SimPosition ?? Vector2.Zero;
            Vector2 start = ConvertUnits.ToSimUnits(Item.WorldPosition) - offset;
            Vector2 center = start - Vector2.UnitY * ConvertUnits.ToSimUnits(Range * 0.5f);
            Vector2 halfSize = ConvertUnits.ToSimUnits(new Vector2(BeamWidth, Range) * 0.5f);
            var bounds = new AABB(center - halfSize, center + halfSize);
            GameMain.World.QueryAABB(fixture =>
            {
                if (fixture.Body == Item.body?.FarseerBody) return true;
                var body = fixture.Body;
                if (body.UserData is Structure wall)
                {
                    if (!wall.Removed && wall.Submarine == sub)
                    {
                        nearbyWalls.Add(wall);
                        // Reuse the same area query for damage candidates and occlusion.
                        if (wall.Indestructible && wall.HasBody && !wall.IsPlatform && !fixture.IsSensor
                            && (fixture.CollisionCategories & Physics.CollisionWall) != 0)
                        {
                            body.GetTransform(out var transform);
                            fixture.Shape.ComputeAABB(out AABB wallBounds, ref transform, 0);
                            if (wallBounds.UpperBound.X >= bounds.LowerBound.X && wallBounds.LowerBound.X <= bounds.UpperBound.X
                                && wallBounds.UpperBound.Y >= bounds.LowerBound.Y && wallBounds.LowerBound.Y <= bounds.UpperBound.Y)
                                blockers.Add(new AABB(ConvertUnits.ToDisplayUnits(wallBounds.LowerBound + offset),
                                    ConvertUnits.ToDisplayUnits(wallBounds.UpperBound + offset)));
                        }
                    }
                    return true;
                }
                Character candidate = body.UserData is Limb hitCharacterLimb ? hitCharacterLimb.character : body.UserData as Character;
                if (candidate != null)
                {
                    if (!candidate.Removed && candidate.Submarine == sub) nearbyCharacters.Add(candidate);
                    return true;
                }
                if (fixture.IsSensor && body.UserData is not Structure) return true;
                Entity entity = body.UserData switch
                {
                    Limb limb => limb.character,
                    Holdable held => held.Item,
                    Entity target => target,
                    _ => null
                };
                if (entity == null || entity.Removed || entity.Submarine != sub || entity == Item) return true;
                if (entity is not Character && entity is not Barotrauma.Item && entity is not Structure) return true;
                fixture.GetAABB(out AABB actual, 0);
                // QueryAABB uses expanded proxies. Reject fixtures outside the actual strip.
                if (actual.UpperBound.X < bounds.LowerBound.X || actual.LowerBound.X > bounds.UpperBound.X
                    || actual.UpperBound.Y < bounds.LowerBound.Y || actual.LowerBound.Y > bounds.UpperBound.Y) return true;
                Vector2 position = Vector2.Clamp(actual.Center, bounds.LowerBound, bounds.UpperBound);
                if (body.UserData is Limb hitLimb) charactersWithLimbs.Add(hitLimb.character);
                hits.Add((entity, body, position, start.Y - position.Y));
                return true;
            }, ref bounds);
        }
    }
}
