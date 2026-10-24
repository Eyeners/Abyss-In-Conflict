using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class GroundAdhesionRelease : IAssemblyPlugin
    {
        private static readonly Identifier AfflictionId = "aic_ground_adhesion_release".ToIdentifier();
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.ground.adhesion.release");
            try
            {
                foreach (string name in new[] { "UpdateStanding", "UpdateStandingSimple" })
                    harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), name),
                        transpiler: new HarmonyMethod(typeof(GroundAdhesionRelease), nameof(PreserveVelocity)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                    postfix: new HarmonyMethod(typeof(GroundAdhesionRelease), nameof(ReleaseHeightControl)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC GroundAdhesionRelease] " + error);
            }
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static bool IsReleased(HumanoidAnimController controller)
        {
            // 磁吸开关优先于隐藏 aff；aff 不会被删除，只是在磁吸期间不执行解除效果。
            var character = controller.Character;
            if (FoehnJetJump.Equipped(character)?.Flying == true) return true;
            return character != null && !character.Removed
                && !MagneticBoots.EnabledFor(character)
                && character.CharacterHealth?.GetAfflictionStrengthByIdentifier(AfflictionId) > 0;
        }

        private static IEnumerable<CodeInstruction> PreserveVelocity(IEnumerable<CodeInstruction> source)
        {
            // 改写 IL 中的速度 setter，而非跳过整个站立动画；保留姿态与持枪动画。
            var setter = AccessTools.PropertySetter(typeof(PhysicsBody), nameof(PhysicsBody.LinearVelocity));
            var replacement = AccessTools.Method(typeof(GroundAdhesionRelease), nameof(SetStandingVelocity));
            int replaced = 0;
            foreach (var instruction in source)
            {
                if (!instruction.Calls(setter)) { yield return instruction; continue; }
                var loadController = new CodeInstruction(OpCodes.Ldarg_0);
                // 跳转标签和异常块标记跟着替换点移动，否则可能生成无效 IL。
                loadController.labels.AddRange(instruction.labels);
                loadController.blocks.AddRange(instruction.blocks);
                yield return loadController;
                yield return new CodeInstruction(OpCodes.Call, replacement);
                replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("Standing velocity setter not found; game API changed.");
        }

        private static void SetStandingVelocity(PhysicsBody body, Vector2 velocity, HumanoidAnimController controller)
        {
            // 普通角色仍走原 setter，仅目标角色主碰撞体保留外力产生的速度。
            // Preserve the normal pose/IK update, only bypass the collider's locomotion velocity assignment.
            if (body == controller.Collider && IsReleased(controller)) return;
            if (body == controller.Collider && FoehnSlide.Equipped(controller.Character)?.Sliding == true)
                velocity.X = body.LinearVelocity.X;
            body.LinearVelocity = velocity;
        }

        private static void ReleaseHeightControl(HumanoidAnimController __instance)
        {
            // UpdateAnim reestablishes the native flag next frame, so no forced true restoration is needed.
            if (IsReleased(__instance)) __instance.levitatingCollider = false;
        }
    }
}
