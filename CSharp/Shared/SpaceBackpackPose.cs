using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class SpaceBackpackPose : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly LimbType[] Feet = { LimbType.LeftFoot, LimbType.RightFoot };
        private sealed class CruiseFacing
        {
            internal float Sign;
            internal Vector2 ThreatPosition;
            internal Submarine ThreatSubmarine;
            internal double ThreatUntil;
            internal bool Aiming;
        }
        private static ConditionalWeakTable<Character, CruiseFacing> cruiseFacing = new ConditionalWeakTable<Character, CruiseFacing>();
        public void Initialize()
        {
            harmony = new Harmony("aic.space.backpack.pose");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"),
                    prefix: new HarmonyMethod(typeof(SpaceBackpackPose), nameof(BeginPose)),
                    postfix: new HarmonyMethod(typeof(SpaceBackpackPose), nameof(EndPose)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateSwimming"),
                    prefix: new HarmonyMethod(typeof(SpaceBackpackPose), nameof(UpdatePose)));
                harmony.Patch(AccessTools.Method(typeof(Character), "AddAttacker"),
                    postfix: new HarmonyMethod(typeof(SpaceBackpackPose), nameof(ReactToHit)));
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC SpaceBackpackPose] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            cruiseFacing = new ConditionalWeakTable<Character, CruiseFacing>();
        }

        private static bool Applies(HumanoidAnimController anim)
        {
            var user = anim.Character;
            return user != null && !user.Removed && !user.IsDead && !user.IsIncapacitated
                && !MagneticBoots.EnabledFor(user)
                && !user.IsRagdolled && !user.IsClimbing && !user.InWater
                && !anim.AnimationTestPose && anim.Collider != null
                && GameMain.World.Gravity.LengthSquared() < 0.01f
                && user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag)
                    ?.GetComponent<SpacePropulsionBackpack>()?.IsOn == true;
        }

        private static void ReactToHit(Character __instance, Character character, float damage)
        {
            if (damage <= 0 || character == null || character == __instance || character.Removed
                || character.IsDead || !SpaceBackpackAI.Applies(__instance)) return;
            var combat = SpaceBackpackAI.ActiveCombat(__instance);
            if (combat?.Mode == AIObjectiveCombat.CombatMode.Retreat) return;
            if (character.TeamID == __instance.TeamID && combat?.Enemy != character) return;
            var state = cruiseFacing.GetValue(__instance, _ => new CruiseFacing { Sign = __instance.AnimController.Dir });
            // Remember the hit location, not a live enemy position through walls.
            state.ThreatSubmarine = character.Submarine;
            state.ThreatPosition = character.WorldPosition - (character.Submarine?.Position ?? Vector2.Zero);
            state.ThreatUntil = Timing.TotalTime + 1.5;
        }

        private struct PoseState
        {
            public bool Active, Swimming, ForceStanding;
            public float LockTimer;
        }
        private static void BeginPose(HumanoidAnimController __instance, out PoseState __state)
        {
            __state = default;
            if (!Applies(__instance)) return;
            __state = new PoseState { Active = true, Swimming = __instance.swimming,
                ForceStanding = __instance.forceStanding, LockTimer = __instance.swimmingStateLockTimer };
            // Scope only the animation branch. Do not change InWater, gravity, or movement input.
            __instance.swimming = true;
            __instance.forceStanding = false;
            __instance.swimmingStateLockTimer = 10f;
            if (SpaceBackpackAI.Applies(__instance.Character))
            {
                Vector2 facing = GetAIDirection(__instance);
                if (Math.Abs(facing.X) > 0.1f)
                    __instance.TargetDir = facing.X > 0 ? Direction.Right : Direction.Left;
            }
        }
        private static void EndPose(HumanoidAnimController __instance, PoseState __state)
        {
            if (!__state.Active) return;
            __instance.swimming = __state.Swimming;
            __instance.forceStanding = __state.ForceStanding;
            __instance.swimmingStateLockTimer = __state.LockTimer;
            __instance.levitatingCollider = false;
        }
        private static bool UpdatePose(HumanoidAnimController __instance)
        {
            var anim = __instance;
            if (!Applies(anim)) return true;
            var torso = anim.GetLimb(LimbType.Torso);
            var head = anim.GetLimb(LimbType.Head);
            var parameters = anim.CurrentSwimParams;
            if (torso == null || parameters == null) return false;
            var collider = anim.Collider;
            bool ai = SpaceBackpackAI.Applies(anim.Character);
            // Native cursor flipping requires OnGround. Our floating pose must supply it;
            // remote players retain the direction received through native networking.
            bool localFacing = GameMain.NetworkMember == null;
#if CLIENT
            localFacing |= anim.Character == Character.Controlled;
#endif
            if (!ai && localFacing)
            {
                float horizontalAim = ConvertUnits.ToSimUnits(anim.Character.CursorPosition).X - torso.SimPosition.X;
                if (Math.Abs(horizontalAim) > 0.1f)
                    anim.TargetDir = horizontalAim > 0 ? Direction.Right : Direction.Left;
            }
            Vector2 aim = (ai ? GetAIDirection(anim)
                : ConvertUnits.ToSimUnits(anim.Character.CursorPosition) - torso.SimPosition) * anim.Dir;
            if (aim.LengthSquared() > 0.16f)
            {
                bool combatPose = ai && cruiseFacing.TryGetValue(anim.Character, out var state) && state.Aiming;
                // The 45-degree offset belongs to cruise, not AI weapon alignment.
                float angle = MathHelper.WrapAngle((float)Math.Atan2(aim.Y, aim.X)
                    - (combatPose ? 0 : MathHelper.PiOver4 * anim.Dir));
                collider.SmoothRotate(angle, parameters.SteerTorque * (ai ? 2f : 1f));
            }
            Vector2 up = new Vector2(-(float)Math.Sin(collider.Rotation), (float)Math.Cos(collider.Rotation));
            torso.body.MoveToPos(collider.SimPosition + up * 0.4f, 5f);
            torso.body.SmoothRotate(collider.Rotation + (anim.TorsoAngle ?? 0) * anim.Dir, parameters.TorsoTorque);
            if (head != null && !head.Disabled) anim.RotateHead(head);
            var waist = anim.GetLimb(LimbType.Waist) ?? torso;
            Vector2 feet = waist.SimPosition - up * (anim.upperLegLength + anim.lowerLegLength);
            foreach (var type in Feet)
            {
                var foot = anim.GetLimb(type);
                if (foot != null && !foot.Disabled && !foot.IsSevered)
                    anim.FootIK(foot, feet, parameters.LegTorque, parameters.FootTorque, parameters.FootAngleInRadians);
            }
            // Item holding/aiming is updated by the remaining native animation code.
            return false;
        }

        private static Vector2 GetAIDirection(HumanoidAnimController anim)
        {
            var user = anim.Character;
            var facing = cruiseFacing.GetValue(user, _ => new CruiseFacing { Sign = anim.Dir });
            var combat = SpaceBackpackAI.ActiveCombat(user);
            Vector2 aim = Vector2.Zero;
            bool reacting = facing.ThreatUntil > Timing.TotalTime
                && combat?.Mode != AIObjectiveCombat.CombatMode.Retreat;
            // Native combat already tracks this enemy and sets its aim cursor before the
            // visibility test. Facing must not depend on that test; firing still does.
            if (combat != null && !combat.Abandon && !combat.IsCompleted
                && combat.Mode != AIObjectiveCombat.CombatMode.Retreat
                && combat.Enemy != null && !combat.Enemy.Removed && !combat.Enemy.IsDead)
            {
                aim = ConvertUnits.ToSimUnits(combat.Enemy.WorldPosition - user.WorldPosition);
                facing.ThreatUntil = 0;
            }
            else if (reacting)
                aim = ConvertUnits.ToSimUnits(facing.ThreatPosition
                    + (facing.ThreatSubmarine?.Position ?? Vector2.Zero) - user.WorldPosition);
            else if (user.IsKeyDown(InputType.Aim))
                aim = ConvertUnits.ToSimUnits(user.CursorPosition) - (anim.GetLimb(LimbType.Torso)?.SimPosition ?? anim.Collider.SimPosition);
            facing.Aiming = aim.LengthSquared() > 0.16f;
            if (facing.Aiming)
            {
                if (Math.Abs(aim.X) > 0.1f) facing.Sign = Math.Sign(aim.X);
                return aim;
            }
            Vector2 movement = anim.TargetMovement;
            // Translation remains owned by navigation. Small lateral corrections on vertical
            // routes must not rotate the torso or repeatedly flip the entire ragdoll.
            if (Math.Abs(movement.X) > 0.35f && Math.Abs(movement.X) > Math.Abs(movement.Y) * 0.75f)
                facing.Sign = Math.Sign(movement.X);
            return Vector2.UnitX * facing.Sign;
        }
    }
}
