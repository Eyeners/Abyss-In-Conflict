using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Input;
#endif

namespace AIC.Components
{
    class RecallBackpack : ItemComponent, IClientSerializable
    {
        private sealed class RecallState { internal bool Locked; internal double NextUse; }
        private static readonly ConditionalWeakTable<Character, RecallState> States = new ConditionalWeakTable<Character, RecallState>();
        private static readonly Identifier DisorderId = "aic_temporal_disorder".ToIdentifier();
        [Serialize(false, IsPropertySaveable.Yes)] public bool JumpMode { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool FailureTriggered { get; set; }
        [Serialize(0, IsPropertySaveable.Yes)] public int FailureJumpsRemaining { get; set; }
        private float failureTimer;
        private readonly BackpackHitbox hitbox = new BackpackHitbox();
        private float timer;
        private double nextAICheck;
        private double nextSpotCheck;
        private readonly RecallLandingSelector landingSelector = new RecallLandingSelector();
        private Character recoveryUser;
        private double nextRecoveryCheck;
        private double nextCoreCheck;
        private double nextCarriedCoreCheck;
        private AIObjectiveContainItem loadCore;
        public RecallBackpack(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        private bool Worn(Character user) => user != null && !user.Removed && !user.IsDead
            && Item.Condition > 0 && user.GetEquippedItem("aic_recall_backpack", InvSlotType.Bag) == Item;

        public override void Update(float deltaTime, Camera cam)
        {
            var user = Item.ParentInventory?.Owner as Character;
            hitbox.Update(Item, user);
#if CLIENT
            bool q = Keyboard.GetState().IsKeyDown(Keys.Q);
            bool pressed = q && !previousQ;
            previousQ = q;
            if (pressed && Worn(user) && user == Character.Controlled && !user.IsIncapacitated
                && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen)
            {
                var cursor = cam.ScreenToWorld(PlayerInput.MousePosition);
                if (GameMain.NetworkMember?.IsClient == true) Item.CreateClientEvent(this, new JumpRequest(cursor));
                else Recall(user, false, cursor);
            }
#endif
            if (GameMain.NetworkMember?.IsClient == true) return;
            if (UpdateFailure(user, deltaTime)) return;
            UpdateCoreLoading(user);
            if (!Worn(user)) { recoveryUser = null; return; }
            if (user.IsAIControlled && !user.IsIncapacitated && !user.IsRagdolled
                && !user.IsEscorted && !user.IsHandcuffed && user.SelectedBy == null
                && Timing.TotalTime >= nextSpotCheck && user.AIController is HumanAIController observer)
            {
                nextSpotCheck = Timing.TotalTime + 0.5;
                // Native indoor scanning is conditional. Keep its hostility, range, facing
                // and visibility rules, but also scan while this backpack is worn indoors.
                observer.SpotEnemies();
            }
            timer -= deltaTime;
            if (timer > 0) return;
            timer = 0.1f;
            var state = States.GetValue(user, _ => new RecallState());
            if (user.Vitality > 0) state.Locked = false;
            // The lower health bar spans MinVitality..0; halfway is not +50 vitality.
            if (!state.Locked && user.Vitality <= user.CharacterHealth.MinVitality * 0.5f)
                Recall(user, true);
            else if (TryRecoverNavigation(user)) return;
            else if (user.IsAIControlled && !user.IsIncapacitated && !user.IsRagdolled
                && user.SelectedBy == null && Timing.TotalTime >= state.NextUse
                && Timing.TotalTime >= nextAICheck
                && !(user.CharacterHealth.GetAffliction(DisorderId)?.Duration > 0))
                TryAIJump(user);
        }

        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

        protected override void RemoveComponentSpecific()
        {
            hitbox.Release();
            if (loadCore != null) loadCore.Abandon = true;
        }

        private bool UpdateFailure(Character user, float deltaTime)
        {
            if (user == null || user.Removed || user.GetEquippedItem("aic_recall_backpack", InvSlotType.Bag) != Item) return false;
            CheckFailureThreshold(Item.ConditionPercentage);
            if (FailureJumpsRemaining <= 0) return false;
            if (loadCore != null) { loadCore.Abandon = true; loadCore = null; }
            failureTimer -= deltaTime;
            if (failureTimer > 0) return true;
            failureTimer = 0.5f;
            var sound = ItemPrefab.Find(null, "aic_recall_hum".ToIdentifier());
            var blast = ItemPrefab.Find(null, "aic_temporal_rupture".ToIdentifier());
            if (Entity.Spawner == null || sound == null || blast == null) return true;
            Vector2 destination = user.WorldPosition + RandomDirection() * ConvertUnits.ToDisplayUnits(8f);
            FailureJumpsRemaining--;
            MoveUser(user, destination);
            hitbox.Update(Item, user);
            Entity.Spawner.AddItemToSpawnQueue(sound, destination);
            ApplyJumpDisorder(user, blast, destination);
            States.GetValue(user, _ => new RecallState()).NextUse = Timing.TotalTime + 1;
            return true;
        }

        internal void CheckFailureThreshold(float conditionPercentage)
        {
            if (FailureTriggered || conditionPercentage > 10) return;
            FailureTriggered = true;
            FailureJumpsRemaining = 5;
            failureTimer = 0;
        }

        private static Vector2 RandomDirection()
        {
            float angle = Rand.Range(0f, MathHelper.TwoPi);
            return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
        }
        internal static float DriftRadius(float conditionPercentage)
            => ConvertUnits.ToDisplayUnits(8f) * (1 - MathHelper.Clamp(conditionPercentage / 100f, 0, 1));

        private Vector2 ApplyDrift(Character user, Vector2 intended, bool shortJump)
        {
            float radius = DriftRadius(Item.ConditionPercentage);
            if (radius <= 0) return intended;
            Vector2 destination = intended + RandomDirection() * radius * (float)Math.Sqrt(Rand.Range(0f, 1f));
            if (shortJump)
            {
                Vector2 offset = destination - user.WorldPosition;
                float range = ConvertUnits.ToDisplayUnits(8f);
                if (offset.LengthSquared() > range * range)
                    destination = user.WorldPosition + Vector2.Normalize(offset) * range;
            }
            return destination;
        }

        private void UpdateCoreLoading(Character user)
        {
            bool eligible = Worn(user) && user.IsAIControlled && !user.IsIncapacitated
                && !user.IsRagdolled && !user.LockHands;
            // Reload carried supplies independently of objective priority and submarine team.
            // Combat must not prevent an enemy boarding party from using its own inventory.
            if (eligible && Timing.TotalTime >= nextCarriedCoreCheck)
            {
                nextCarriedCoreCheck = Timing.TotalTime + 0.5;
                var socket = Item.GetComponent<ItemContainer>()?.Inventory;
                var loaded = socket?.GetItemAt(0);
                bool ready = loaded != null && !loaded.Removed
                    && loaded.Prefab.Identifier == "aic_recall_core" && loaded.Condition >= 3;
                if (socket != null && !ready)
                {
                    var spare = user.Inventory.FindItem(candidate => candidate != loaded && !candidate.Removed
                        && candidate.Prefab.Identifier == "aic_recall_core" && candidate.Condition >= 3
                        && user.CanInteractWith(candidate, checkLinked: false), recursive: true);
                    if (spare != null)
                        ready = socket.TryPutItem(spare, 0, allowSwapping: true, allowCombine: false, user: user);
                }
                if (ready)
                {
                    if (loadCore != null) { loadCore.Abandon = true; loadCore = null; }
                    return;
                }
            }
            if (loadCore != null)
            {
                if (!eligible || loadCore.character != user
                    || (user.AIController as HumanAIController)?.ObjectiveManager?.Objectives.Contains(loadCore) != true)
                {
                    loadCore.Abandon = true;
                    loadCore = null;
                    return;
                }
                if (!loadCore.Abandon && !loadCore.IsCompleted) return;
                nextCoreCheck = Timing.TotalTime + (loadCore.Abandon ? 10 : 1);
                loadCore = null;
            }
            if (!eligible || Timing.TotalTime < nextCoreCheck) return;
            nextCoreCheck = Timing.TotalTime + 1;
            var manager = (user.AIController as HumanAIController)?.ObjectiveManager;
            var container = Item.GetComponent<ItemContainer>();
            if (manager == null || container == null) return;
            var core = container.Inventory.GetItemAt(0);
            if (core != null && !core.Removed && core.Prefab.Identifier == "aic_recall_core" && core.Condition >= 3) return;

            // Use the native oxygen-tank workflow, but never spawn supplies or empty storage slots.
            loadCore = new AIObjectiveLoadRecallCore(user, container, manager)
            {
                BasePriority = 75,
                TargetSlot = 0,
                ConditionLevel = 100f / 3f,
                RemoveExisting = true,
                RemoveExistingPredicate = item => container.Inventory.IsInSlot(item, 0),
                RemoveMax = 1,
                ItemCount = 1,
                MoveWholeStack = false,
                AllowStealing = false,
                GetItemPriority = item => item.ConditionPercentage
            };
            manager.AddObjective(loadCore);
        }

        private void TryAIJump(Character user)
        {
            nextAICheck = Timing.TotalTime + 0.5;
            var controller = user.AIController as HumanAIController;
            var objective = controller?.ObjectiveManager?.CurrentObjective;
            // Follow the selected objective chain, not queued tasks or idle wandering.
            if (objective == null || objective is AIObjectiveIdle) return;
            AIObjectiveGoTo goal = null;
            AIObjectiveCombat combat = objective.SourceObjective as AIObjectiveCombat;
            for (var current = objective; current != null; current = current.CurrentSubObjective)
            {
                if (current.Abandon || current.IsCompleted) return;
                if (current is AIObjectiveGoTo goTo) goal = goTo;
                if (current is AIObjectiveCombat fighting) combat = fighting;
            }
            var core = Item.GetComponent<ItemContainer>()?.Inventory.GetItemAt(0);
            if (core == null || core.Removed || core.Condition < 3 || core.Prefab.Identifier != "aic_recall_core") return;
            // Close ranged combat takes precedence over a GoTo subobjective used to back off.
            // A blocked tactical jump must not fall through to jumping towards the enemy.
            if (TryCombatJump(user, combat)) return;
            if (TryCombatApproach(user, combat)) return;
            ISpatialEntity target = goal?.Target;
            if (goal != null && (goal.IsCloseEnough || target == null || target == user)) return;
            if (target == null && combat != null && !combat.TargetEliminated
                && combat.Mode != AIObjectiveCombat.CombatMode.Retreat)
                target = combat.Enemy;
            var path = controller.PathSteering?.CurrentPath;
            if (target == null || path == null || path.Unreachable || path.Finished || path.CurrentNode == null) return;

            float range = ConvertUnits.ToDisplayUnits(8f);
            float bestDistance = Vector2.Distance(user.WorldPosition, target.WorldPosition) - 100;
            WayPoint best = null;
            for (int i = path.CurrentIndex; i < path.Nodes.Count; i++)
            {
                var node = path.Nodes[i];
                if (node.Removed || node.Submarine != user.Submarine || node.CurrentHull == null) continue;
                float distance = Vector2.Distance(user.WorldPosition, node.WorldPosition);
                if (distance < 200 || distance > range) continue;
                float remaining = Vector2.Distance(node.WorldPosition, target.WorldPosition);
                if (remaining >= bestDistance || !ClearLanding(user, node)) continue;
                best = node;
                bestDistance = remaining;
            }
            if (best == null) return;
            Vector2 before = user.WorldPosition;
            Recall(user, false, best.WorldPosition, aiJump: true);
            if (Vector2.DistanceSquared(before, user.WorldPosition) < 1) return;
            controller.PathSteering.ResetPath();
            controller.PathSteering.findPathTimer = -0.01f;
            controller.SteeringManager.Reset();
        }

        private static bool NetworkNode(WayPoint node) => !node.Removed
            && node.SpawnType == SpawnType.Path && node.CurrentHull != null
            && node.linkedTo.OfType<WayPoint>().Any(other => !other.Removed && other.SpawnType == SpawnType.Path);

        private bool TryRecoverNavigation(Character user)
        {
            // Only recover our own teleports; do not interrupt ordinary swimming or outside jobs.
            if (recoveryUser != user) { recoveryUser = null; return false; }
            if (!user.IsAIControlled || user.IsIncapacitated || user.IsRagdolled || user.SelectedBy != null)
                return false;
            if (Timing.TotalTime < nextRecoveryCheck) return false;
            nextRecoveryCheck = Timing.TotalTime + 1;
            if (landingSelector.HasNearbyConnection(user))
            {
                recoveryUser = null;
                return false;
            }
            var nearby = WayPoint.WayPointList.Where(NetworkNode)
                .Select(node => new { Node = node, Distance = Vector2.DistanceSquared(user.WorldPosition, node.WorldPosition) })
                .Where(candidate => candidate.Distance <= 800 * 800)
                .OrderBy(candidate => candidate.Distance).ToArray();
            // A nearby visible network node lets native steering take over. A closed door or
            // wall must not make an isolated landing look connected just because it is close.
            foreach (var candidate in nearby.Where(candidate => candidate.Distance <= 200 * 200).Take(16))
            {
                var node = candidate.Node;
                if (user.CurrentHull == null || node.CurrentHull != user.CurrentHull) continue;
                Vector2 end = user.SimPosition + ConvertUnits.ToSimUnits(node.WorldPosition - user.WorldPosition);
                if (Submarine.PickBody(user.SimPosition, end,
                    collisionCategory: Physics.CollisionWall | Physics.CollisionRepairableWall | Physics.CollisionLevel,
                    customPredicate: fixture => !fixture.IsSensor && (fixture.Body.UserData is Entity entity
                        ? entity.Submarine == user.Submarine : user.Submarine == null), allowInsideFixture: true) != null) continue;
                recoveryUser = null;
                return false;
            }
            // Outside hulls the character's Submarine may be null. Search by world distance,
            // including nearby submarine interiors, instead of requiring the old submarine.
            foreach (var candidate in nearby.Take(24))
            {
                if (candidate.Distance < 1 || !ClearLanding(user, candidate.Node)) continue;
                Vector2 before = user.WorldPosition;
                Recall(user, false, candidate.Node.WorldPosition, aiJump: true, navigationRecovery: true);
                return Vector2.DistanceSquared(before, user.WorldPosition) >= 1;
            }
            // No recovery destination is not a reason to disable normal combat jumps.
            return false;
        }

        private bool TryCombatApproach(Character user, AIObjectiveCombat combat)
        {
            var enemy = combat?.Enemy;
            if (combat == null || combat.Abandon || combat.IsCompleted || combat.TargetEliminated
                || (combat.Mode != AIObjectiveCombat.CombatMode.Offensive
                    && combat.Mode != AIObjectiveCombat.CombatMode.Defensive)
                || enemy == null || enemy.Removed || enemy.IsDead || enemy.Submarine != user.Submarine
                || combat.Weapon == null || combat.Weapon.Removed
                || !user.HasEquippedItem(combat.Weapon, predicate: CharacterInventory.IsHandSlotType)
                || !(combat.Weapon.GetComponent<RangedWeapon>() is RangedWeapon weapon) || weapon.IsEmpty(user)) return false;
            float distance = Vector2.Distance(user.WorldPosition, enemy.WorldPosition);
            if (distance <= 600) return false;
            // Search nearby connections even when aiming has stopped native walking.
            if (!landingSelector.TrySelect(user, enemy, 2,
                position => ClearCombatLanding(user, enemy, position), out Vector2 best)) return false;
            Vector2 before = user.WorldPosition;
            Recall(user, false, best, aiJump: true);
            if (Vector2.DistanceSquared(before, user.WorldPosition) < 1) return false;
            (user.AIController as HumanAIController)?.SteeringManager.Reset();
            return true;
        }

        private bool TryCombatJump(Character user, AIObjectiveCombat combat)
        {
            var enemy = combat?.Enemy;
            if (combat == null || combat.Abandon || combat.IsCompleted || combat.TargetEliminated
                || (combat.Mode != AIObjectiveCombat.CombatMode.Offensive
                    && combat.Mode != AIObjectiveCombat.CombatMode.Defensive)
                || enemy == null || enemy.Removed || enemy.IsDead || enemy.Submarine != user.Submarine
                || Vector2.DistanceSquared(user.WorldPosition, enemy.WorldPosition) > 300 * 300
                || combat.Weapon == null || combat.Weapon.Removed
                || !user.HasEquippedItem(combat.Weapon, predicate: CharacterInventory.IsHandSlotType)
                || combat.Weapon.GetComponent<RangedWeapon>() == null) return false;

            // Retreat remains a strict priority over flanking; tactical weights rank
            // positions within each group, never overriding collision or firing checks.
            for (int group = 0; group < 2; group++)
            {
                if (!landingSelector.TrySelect(user, enemy, group,
                    position => ClearCombatLanding(user, enemy, position), out Vector2 destination)) continue;
                Vector2 before = user.WorldPosition;
                Recall(user, false, destination, aiJump: true);
                if (Vector2.DistanceSquared(before, user.WorldPosition) >= 1)
                    (user.AIController as HumanAIController)?.SteeringManager.Reset();
                return true;
            }
            nextAICheck = Timing.TotalTime + 1;
            return true;
        }

        internal static float CombatLandingScore(Vector2 origin, Vector2 enemy, float facing,
            Vector2 destination, bool behind)
        {
            Vector2 jump = destination - origin, separation = destination - enemy;
            float jumpDistance = jump.Length(), enemyDistance = separation.Length();
            if (jumpDistance < 200 || jumpDistance > 800 || enemyDistance < 300)
                return float.PositiveInfinity;
            if (behind)
            {
                if (separation.X * facing > -200) return float.PositiveInfinity;
            }
            else
            {
                Vector2 away = origin - enemy;
                if (away.LengthSquared() < 1
                    || Vector2.Dot(jump, Vector2.Normalize(away)) < jumpDistance * 0.5f)
                    return float.PositiveInfinity;
            }
            return -jumpDistance;
        }

        private static bool ClearCombatLanding(Character user, Character enemy, Vector2 position)
        {
            var sub = user.Submarine;
            var hull = Hull.FindHull(position, user.CurrentHull, useWorldCoordinates: true);
            if (hull == null || hull.Submarine != sub || !ClearLanding(user, position, sub)) return false;
            // Connections across ladders/shafts are not standing surfaces. Check support
            // under both sides; keep swimming candidates usable without requiring a floor.
            if (!user.InWater)
            {
                Vector2 center = ConvertUnits.ToSimUnits(position - (sub?.Position ?? Vector2.Zero));
                Vector2 half = user.AnimController.Collider.GetSize() * 0.5f;
                for (int side = -1; side <= 1; side += 2)
                {
                    Vector2 foot = center + new Vector2(half.X * 0.7f * side, -half.Y + 0.05f);
                    if (Submarine.PickBody(foot, foot - new Vector2(0, 0.6f),
                        collisionCategory: Physics.CollisionWall | Physics.CollisionPlatform | Physics.CollisionLevel,
                        customPredicate: fixture => !fixture.IsSensor && (fixture.Body.UserData is Entity entity
                            ? entity.Submarine == sub : sub == null), allowInsideFixture: false) == null) return false;
                }
            }
            return ClearShot(user, enemy, position, sub);
        }

        private static bool ClearShot(Character user, Character enemy, Vector2 position, Submarine sub)
        {
            Vector2 offset = user.AnimController.GetLimb(LimbType.Torso)?.WorldPosition - user.WorldPosition
                ?? Vector2.Zero;
            Vector2 subPosition = sub?.Position ?? Vector2.Zero;
            Vector2 start = ConvertUnits.ToSimUnits(position + offset - subPosition);
            Vector2 end = ConvertUnits.ToSimUnits(
                (enemy.AnimController.GetLimb(LimbType.Torso)?.WorldPosition ?? enemy.WorldPosition) - subPosition);
            // Test a direct firing line, including closed doors and projectile-blocking items.
            // Characters are excluded: this is cover prediction, not a guarantee of a hit.
            return Submarine.PickBody(start, end,
                collisionCategory: Physics.CollisionWall | Physics.CollisionRepairableWall
                    | Physics.CollisionLevel | Physics.CollisionItemBlocking,
                customPredicate: fixture => !fixture.IsSensor && (fixture.Body.UserData is Entity entity
                    ? entity.Submarine == sub
                        && (!(entity is Item item) || item.ParentInventory == null)
                    : sub == null), allowInsideFixture: true) == null;
        }

        private static bool ClearLanding(Character user, WayPoint node)
            => ClearLanding(user, node.WorldPosition, node.Submarine);

        private static bool ClearLanding(Character user, Vector2 position, Submarine sub)
        {
            var collider = user.AnimController?.Collider;
            if (collider == null) return false;
            Vector2 center = ConvertUnits.ToSimUnits(position - (sub?.Position ?? Vector2.Zero));
            Vector2 half = collider.GetSize() * 0.5f;
            // Check the destination, not the travel segment: short jumps can cross walls.
            // Multiple probes also reject closed doors and narrow spaces around a path point.
            for (int i = -1; i <= 1; i++)
            {
                Vector2 horizontal = new Vector2(half.X, 0), vertical = new Vector2(0, half.Y);
                if (!Clear(center - horizontal + vertical * i, center + horizontal + vertical * i)
                    || !Clear(center - vertical + horizontal * i, center + vertical + horizontal * i)) return false;
            }
            return true;

            bool Clear(Vector2 start, Vector2 end) => Submarine.PickBody(start, end,
                collisionCategory: Physics.CollisionWall | Physics.CollisionRepairableWall | Physics.CollisionLevel,
                customPredicate: fixture => !fixture.IsSensor && (fixture.Body.UserData is Entity entity
                    ? entity.Submarine == sub : sub == null), allowInsideFixture: true) == null;
        }

        private void Recall(Character user, bool automatic, Vector2 cursor = default, bool aiJump = false,
            bool navigationRecovery = false)
        {
            if (!Worn(user) || FailureJumpsRemaining > 0 || (!FailureTriggered && Item.ConditionPercentage <= 10)
                || GameMain.NetworkMember?.IsClient == true || (!automatic && user.IsIncapacitated)) return;
            var state = States.GetValue(user, _ => new RecallState());
            if ((!navigationRecovery && Timing.TotalTime < state.NextUse) || (automatic && state.Locked)) return;
            state.NextUse = Timing.TotalTime + (aiJump ? 3 : 1);
            bool jump = (JumpMode || aiJump) && !automatic;
            float cost = jump ? 3 : 6;
            var core = Item.GetComponent<ItemContainer>()?.Inventory.GetItemAt(0);
            if (core == null || core.Removed || core.Condition < cost || core.Prefab.Identifier != "aic_recall_core") return;
            Vector2 destination;
            if (jump)
            {
                if (!float.IsFinite(cursor.X) || !float.IsFinite(cursor.Y)) return;
                Vector2 offset = cursor - user.WorldPosition;
                float distance = offset.Length();
                if (!float.IsFinite(distance) || distance < 1) return;
                float maxDistance = ConvertUnits.ToDisplayUnits(8f);
                destination = user.WorldPosition + offset * Math.Min(1f, maxDistance / distance);
            }
            else
            {
                var sub = Submarine.MainSubs.FirstOrDefault(s => s != null && s.TeamID == user.TeamID);
                var waypoint = WayPoint.WayPointList.FirstOrDefault(w => !w.Removed && w.Submarine == sub
                    && sub != null && w.SpawnType == SpawnType.Human && w.AssignedJob?.Identifier == "medicaldoctor"
                    && w.CurrentHull != null);
                if (waypoint == null) return;
                destination = waypoint.WorldPosition;
            }
            var sound = ItemPrefab.Find(null, "aic_recall_hum".ToIdentifier());
            var blast = ItemPrefab.Find(null, "aic_temporal_rupture".ToIdentifier());
            if (Entity.Spawner == null || sound == null || (jump && blast == null)) return;
            destination = ApplyDrift(user, destination, jump);

            MoveUser(user, destination);
            hitbox.Update(Item, user);
            core.Condition -= cost;
            if (core.Condition <= 0) Entity.Spawner.AddItemToRemoveQueue(core);
            Entity.Spawner.AddItemToSpawnQueue(sound, destination);
            if (jump) ApplyJumpDisorder(user, blast, destination);
            if (automatic || user.Vitality <= 0) state.Locked = true;
        }

        private void MoveUser(Character user, Vector2 destination)
        {
            // Release world-space constraints before moving the complete character ragdoll.
            var boots = MagneticBoots.Get(user);
            if (boots != null) { boots.WallWalk.Reset(); boots.Release(); }
            RecallRopeRegistry.SnapConnected(user);
            user.TeleportTo(destination);
            if (user.IsAIControlled)
            {
                recoveryUser = user;
                nextRecoveryCheck = Timing.TotalTime + 1;
            }
            var anim = user.AnimController;
            if (anim?.Collider != null) anim.Collider.LinearVelocity = Vector2.Zero;
            if (anim != null)
                foreach (var limb in anim.Limbs)
                    if (!limb.Removed && !limb.IsSevered && limb.body != null)
                    { limb.body.LinearVelocity = Vector2.Zero; limb.body.AngularVelocity = 0; }
            var steering = (user.AIController as HumanAIController)?.PathSteering;
            if (steering != null) { steering.ResetPath(); steering.findPathTimer = -0.01f; steering.Reset(); }
        }

        private static void ApplyJumpDisorder(Character user, ItemPrefab blast, Vector2 destination)
        {
            var health = user.CharacterHealth;
            var affliction = health.GetAffliction(DisorderId);
            int tier = affliction != null && affliction.Duration > 0 ? (int)Math.Round(affliction.Strength) : 0;
            if (tier >= 1)
            {
                var effect = ItemPrefab.Find(null, "aic_temporal_backlash_fx".ToIdentifier());
                if (effect != null) Entity.Spawner.AddItemToSpawnQueue(effect, destination);
            }
            if (tier >= 3)
                Entity.Spawner.AddItemToSpawnQueue(blast, destination);
            else if (tier >= 1)
            {
                AddDamage("organdamage", tier == 1 ? 20 : 40);
                AddDamage("nausea", 20);
                if (tier == 2) AddDamage("concussion", 20);
            }
            if (affliction == null && AfflictionPrefab.Prefabs.TryGet(DisorderId, out var prefab))
            {
                health.ApplyAffliction(null, prefab.Instantiate(1));
                affliction = health.GetAffliction(DisorderId);
            }
            if (affliction != null)
            {
                // Levels are counters, not doses scaled by the wearer's max vitality.
                affliction.Strength = Math.Min(3, tier + 1);
                affliction.Duration = 3;
                user.HealthUpdateInterval = 0;
            }
            void AddDamage(string id, float strength)
            {
                if (AfflictionPrefab.Prefabs.TryGet(id.ToIdentifier(), out var prefab))
                    health.ApplyAffliction(null, prefab.Instantiate(strength, user));
            }
        }
#if CLIENT
        private sealed record JumpRequest(Vector2 Cursor) : ItemComponent.IEventData;
        private bool previousQ;
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
        {
            var cursor = extraData is Item.ComponentStateEventData data && data.ComponentData is JumpRequest request
                ? request.Cursor : Vector2.Zero;
            msg.WriteSingle(cursor.X);
            msg.WriteSingle(cursor.Y);
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client client)
        {
            var cursor = new Vector2(msg.ReadSingle(), msg.ReadSingle());
            Recall(client?.Character, false, cursor);
        }
#endif
    }
}
