using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class SpaceBackpackAI : IAssemblyPlugin
    {
        private Harmony harmony;
        internal static bool Applies(Character user) => user != null && user.IsAIControlled
            && !SpaceBackpackDamage.IsFailing(user)
            && !MagneticBoots.EnabledFor(user)
            && !user.Removed && !user.IsDead && !user.IsIncapacitated
            && !user.IsRagdolled && !user.InWater && user.AnimController is HumanoidAnimController
            && GameMain.World.Gravity.LengthSquared() < 0.01f
            && user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag)
                ?.GetComponent<SpacePropulsionBackpack>() is SpacePropulsionBackpack backpack
            && backpack.IsOn && backpack.Item.Condition > 0;

        public void Initialize()
        {
            harmony = new Harmony("aic.space.backpack.ai");
            try
            {
                Patch(typeof(HumanAIController), "Update");
                Patch(typeof(IndoorsSteeringManager), "GetDiffAndAdvance");
                Patch(typeof(IndoorsSteeringManager), "Wander");
                Patch(typeof(AIObjectiveCombat), "Attack");
                Patch(typeof(AIObjectiveCombat), "UseWeapon");
                Patch(typeof(AIObjectiveCombat), "Move");
                Patch(typeof(AIObjectiveIdle), "Act");
                Patch(typeof(AIObjectiveIdle), "Wander");
                Patch(typeof(AIObjectiveGoTo), "Act");
                harmony.Patch(AccessTools.Method(typeof(HumanAIController), "SpotEnemies"),
                    transpiler: new HarmonyMethod(typeof(SpaceBackpackAI), nameof(SpaceSpotting)));
                harmony.Patch(AccessTools.Method(typeof(AIObjectiveGoTo), "Act"),
                    prefix: new HarmonyMethod(typeof(SpaceBackpackAI), nameof(RecoverGoTo)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC SpaceBackpackAI] " + error);
            }
        }
        private void Patch(Type type, string name) => harmony.Patch(AccessTools.Method(type, name),
            transpiler: new HarmonyMethod(typeof(SpaceBackpackAI), nameof(NavigationOnly)));
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        // Replace only navigation decisions, never the actual water state or survival/equipment checks.
        private static IEnumerable<CodeInstruction> NavigationOnly(IEnumerable<CodeInstruction> source)
        {
            var water = AccessTools.PropertyGetter(typeof(Ragdoll), "InWater");
            var head = AccessTools.PropertyGetter(typeof(Ragdoll), "HeadInWater");
            var climb = AccessTools.PropertyGetter(typeof(Character), "CanClimb");
            foreach (var instruction in source)
            {
                string replacement = instruction.Calls(water) ? nameof(NavigatesFreely)
                    : instruction.Calls(head) ? nameof(HeadNavigatesFreely)
                    : instruction.Calls(climb) ? nameof(NeedsLadder) : null;
                if (replacement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SpaceBackpackAI), replacement);
                }
                yield return instruction;
            }
        }
        private static bool NavigatesFreely(Ragdoll anim) => anim.InWater || Applies(anim.Character) || MagneticChainPack.FreeNavigation(anim.Character);
        private static bool HeadNavigatesFreely(Ragdoll anim) => anim.HeadInWater || Applies(anim.Character) || MagneticChainPack.FreeNavigation(anim.Character);
        private static bool NeedsLadder(Character user) => !Applies(user) && !MagneticChainPack.FreeNavigation(user) && user.CanClimb;

        private static IEnumerable<CodeInstruction> SpaceSpotting(IEnumerable<CodeInstruction> source)
        {
            // Keep native scan scheduling, range, hostility and line of sight. Only the
            // head-relative cone changes: rotating a flight pose must not blind the pilot.
            bool matched = false;
            foreach (var instruction in source)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float value && value == 70f)
                {
                    matched = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpaceBackpackAI), nameof(SpottingAngle)));
                }
            }
            if (!matched) throw new InvalidOperationException("Space spotting: native 70-degree cone not found.");
        }
        private static float SpottingAngle(float original, HumanAIController controller)
            => Applies(controller.Character) || MagneticChainPack.FreeNavigation(controller.Character) ? 180f : original;

        private static bool RecoverGoTo(AIObjectiveGoTo __instance)
        {
            var user = __instance.character;
            bool magnetic = MagneticChainPack.FreeNavigation(user);
            if ((!Applies(user) && !magnetic) || GameMain.NetworkMember?.IsClient == true) return true;
            var backpack = user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag)
                ?.GetComponent<SpacePropulsionBackpack>();
            var chain = magnetic ? MagneticChainPack.Equipped(user) : null;
            var navigation = chain?.Navigation ?? backpack?.Navigation;
            if (navigation == null || !navigation.TryMove(user, __instance, out var desired)) return true;
            if (chain != null) desired = chain.ConstrainRecovery(user, desired);
            // Keep the objective alive while bypassing the unreachable-path timeout, not its completion checks.
            user.AIController.SteeringManager.Reset();
            user.AIController.SteeringManager.SteeringManual(1, desired);
            return false;
        }

        internal static AIObjectiveCombat ActiveCombat(Character user)
        {
            var manager = (user.AIController as HumanAIController)?.ObjectiveManager;
            var active = manager?.GetLastActiveObjective<AIObjectiveCombat>();
            if (active != null && !active.Abandon && !active.IsCompleted) return active;
            var objective = manager?.CurrentObjective;
            if (objective == null) return null;
            if (objective is AIObjectiveCombat combat) return combat;
            if (objective.SourceObjective is AIObjectiveCombat source) return source;
            foreach (var child in objective.GetSubObjectivesRecursive(true))
                if (child is AIObjectiveCombat nested) return nested;
            return null;
        }

        internal static Vector2 DesiredVelocity(Character user, float acceleration)
        {
            Vector2 desired = user.AnimController.TargetMovement;
            if (!MathUtils.IsValid(desired)) return Vector2.Zero;
            float speed = desired.Length();
            if (speed < 0.01f) return Vector2.Zero;
            float limit = 2f;
            bool recovering = user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag)
                ?.GetComponent<SpacePropulsionBackpack>()?.Navigation.IsRecovering == true
                || MagneticChainPack.Equipped(user)?.Navigation.IsRecovering == true;
            var path = (user.AIController as HumanAIController)?.PathSteering?.CurrentPath;
            if (!recovering && path != null && !path.Unreachable && !path.Finished && path.CurrentNode != null)
            {
                float distance = ConvertUnits.ToSimUnits(Vector2.Distance(user.WorldPosition, path.CurrentNode.WorldPosition));
                // Leave room to turn at a node rather than accelerating through it at full speed.
                limit = Math.Min(limit, Math.Max(0.4f, (float)Math.Sqrt(2 * acceleration * Math.Max(0, distance - 0.3f)) * 0.65f));
            }
            return desired / speed * Math.Min(speed, limit);
        }
    }
}
