using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class ActiveCrawl : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly Identifier Id = "aic_active_crawl".ToIdentifier();
        private static readonly Identifier KneelId = "aic_active_kneel".ToIdentifier();
        private sealed class InputState { internal bool Up, Down; internal float Phase, DeltaTime, Transition; internal int PoseLevel; internal Vector2 Origin; internal readonly Dictionary<Limb, Vector2> Starts = new(); }
        private static readonly Dictionary<Character, InputState> inputs = new();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static bool IsCrawling(Character c) =>
            Level(c) > 0;
        internal static int Level(Character c) => (c?.CharacterHealth?.GetAffliction(Id)?.Strength ?? 0) > 0 ? 2
            : (c?.CharacterHealth?.GetAffliction(KneelId)?.Strength ?? 0) > 0 ? 1 : 0;
        private static bool Valid(Character c) => MovementState.ActiveOnLand(c)
            && c.AnimController is HumanoidAnimController a && a.CanWalk
            && a.TryGetCollider(1, out var small) && small != null
            && !HoverBike.Riding(c) && !HoverJetpack.Flying(c) && !MovementState.SelectingLadder(c);
        private static void Set(Character c, int level)
        {
            if (Level(c) == level) return;
            c.CharacterHealth.ReduceAfflictionOnAllLimbs(Id, float.MaxValue);
            c.CharacterHealth.ReduceAfflictionOnAllLimbs(KneelId, float.MaxValue);
            if (level > 0)
            {
                if (!AfflictionPrefab.Prefabs.TryGet(level == 2 ? Id : KneelId, out var prefab)) return;
                c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(1));
            }
            c.HealthUpdateInterval = 0;
#if SERVER
            GameMain.Server?.CreateEntityEvent(c, new Character.CharacterStatusEventData(forceAfflictionData: true));
#endif
        }
        private static void Input(Character c, bool blocked)
        {
            if (!Authority || c == null || c.Removed) return;
            if (!inputs.TryGetValue(c, out var state)) inputs[c] = state = new InputState();
            bool crouch = c.IsKeyDown(InputType.Crouch);
            bool up = crouch && c.IsKeyDown(InputType.Up), down = crouch && c.IsKeyDown(InputType.Down);
            bool enter = down && !state.Down, leave = up && !state.Up;
            state.Up = up; state.Down = down;
            if (blocked || !c.AllowInput || !Valid(c) || HumanJumpClimb.Controlling(c)) return;
            if (!c.IsKeyDown(InputType.Crouch)) return;
            if (leave) Set(c, Math.Max(0, Level(c) - 1));
            else if (enter && !up && c.AnimController.OnGround
                && FoehnSlide.Equipped(c)?.Sliding != true
                && FoehnJetJump.Equipped(c)?.Flying != true) Set(c, Math.Min(2, Level(c) + 1));
        }
#if CLIENT
        private static void LocalInput(Character __instance)
        {
            if (__instance != Character.Controlled) return;
            bool blocked = Character.DisableControls || Screen.Selected != GameMain.GameScreen
                || GUI.InputBlockingMenuOpen || GUI.PauseMenuOpen || GameSession.IsTabMenuOpen
                || GUI.KeyboardDispatcher.Subscriber != null;
            Input(__instance, blocked);
            if (!Authority && blocked && __instance.IsKeyDown(InputType.Crouch))
            {
                __instance.ClearInput(InputType.Up);
                __instance.ClearInput(InputType.Down);
            }
        }
#endif
#if SERVER
        private static void RemoteInput(Character __instance)
        {
            if (__instance.IsRemotePlayer && !__instance.AnimController.Frozen) Input(__instance, false);
        }
#endif
        private static void Update(Character __instance)
        {
            if (Authority && IsCrawling(__instance) && !Valid(__instance)) Set(__instance, 0);
        }
        private static void AnimationStep(HumanoidAnimController __instance, float deltaTime)
        {
            var c = __instance.Character;
            if (!IsCrawling(c) && !inputs.ContainsKey(c)) return;
            if (!inputs.TryGetValue(c, out var state)) inputs[c] = state = new InputState();
            state.DeltaTime = MathHelper.Clamp(deltaTime, 0, 0.1f);
            int level = Level(c);
            if (state.PoseLevel != level)
            {
                state.PoseLevel = level; state.Transition = 0; state.Starts.Clear();
                state.Origin = __instance.Collider.SimPosition;
                foreach (var limb in __instance.Limbs)
                    if (!limb.Removed && !limb.IsSevered) state.Starts[limb] = limb.SimPosition;
            }
            else state.Transition += state.DeltaTime;
        }
        private static void Speed(AnimController __instance, ref float __result)
        {
            if (IsCrawling(__instance.Character) && Valid(__instance.Character))
                __result = __instance.GetSpeed(AnimationType.Walk) * (Level(__instance.Character) == 1 ? 0.8f : 0.6f);
        }
        private static void CrawlPose(HumanoidAnimController __instance)
        {
            var a = __instance;
            var c = a.Character;
            if (Level(c) != 1 || !Valid(c) || !a.OnGround) return;
            if (!inputs.TryGetValue(c, out var state)) inputs[c] = state = new InputState();
            float scale = MathHelper.Clamp(a.RagdollParams.JointScale / 0.5f, 0.5f, 2), dir = a.Dir;
            float movement = MathHelper.Clamp(Math.Abs(a.TargetMovement.X), 0, 1);
            state.Phase += state.DeltaTime * movement * 7;
            Vector2 floor = a.GetColliderBottom();
            float blend = MathHelper.SmoothStep(0, 1, MathHelper.Clamp(state.Transition / 0.45f, 0, 1));
            // Keep the existing foot support underneath the body during the change.
            var leftFoot = a.GetLimb(LimbType.LeftFoot);
            var rightFoot = a.GetLimb(LimbType.RightFoot);
            if (leftFoot != null && rightFoot != null && state.Starts.TryGetValue(leftFoot, out var lf)
                && state.Starts.TryGetValue(rightFoot, out var rf))
            {
                float supportX = (lf.X + rf.X) * 0.5f + dir * 0.45f * scale;
                floor.X = MathHelper.Lerp(supportX + a.Collider.SimPosition.X - state.Origin.X, floor.X, blend);
            }
            Vector2 hips = floor + new Vector2(-dir * 0.18f, 0.53f) * scale;
            Vector2 torso = hips + new Vector2(dir * 0.30f, 0.08f) * scale;
            CrawlLimb(a, state, a.GetLimb(LimbType.Waist), hips, -dir * 1.1f);
            CrawlLimb(a, state, a.GetLimb(LimbType.Torso), torso, -dir * 1.3f);
            CrawlLimb(a, state, a.GetLimb(LimbType.Head), torso + new Vector2(dir * 0.31f, 0.15f) * scale, -dir * 0.25f);
            for (int i = 0; i < 2; i++)
            {
                float cycle = state.Phase + i * MathHelper.Pi;
                float stride = (float)Math.Cos(cycle) * 0.15f;
                float lift = Math.Max(0, (float)Math.Sin(cycle)) * 0.09f * movement;
                Vector2 knee = floor + new Vector2(dir * (-0.15f + stride), 0.16f + lift) * scale;
                Vector2 foot = knee + new Vector2(-dir * 0.30f, -0.05f) * scale;
                CrawlSegment(a, state, a.GetLimb(i == 0 ? LimbType.LeftThigh : LimbType.RightThigh), hips, knee);
                CrawlSegment(a, state, a.GetLimb(i == 0 ? LimbType.LeftLeg : LimbType.RightLeg), knee, foot);
                CrawlLimb(a, state, a.GetLimb(i == 0 ? LimbType.LeftFoot : LimbType.RightFoot), foot, dir * 0.9f);
                var slot = i == 0 ? InvSlotType.LeftHand : InvSlotType.RightHand;
                if (c.Inventory?.GetItemInLimbSlot(slot) != null) continue;
                Vector2 shoulder = torso + new Vector2(dir * 0.15f, 0.04f) * scale;
                Vector2 palm = floor + new Vector2(dir * (0.48f - stride), 0.09f + lift) * scale;
                Vector2 elbow = (shoulder + palm) * 0.5f + new Vector2(-dir * 0.12f, 0.02f) * scale;
                CrawlSegment(a, state, a.GetLimb(i == 0 ? LimbType.LeftArm : LimbType.RightArm), shoulder, elbow);
                CrawlSegment(a, state, a.GetLimb(i == 0 ? LimbType.LeftForearm : LimbType.RightForearm), elbow, palm);
                CrawlLimb(a, state, a.GetLimb(i == 0 ? LimbType.LeftHand : LimbType.RightHand), palm, 0);
            }
        }
        private static void CrawlSegment(HumanoidAnimController a, InputState state, Limb limb, Vector2 start, Vector2 end)
            => CrawlLimb(a, state, limb, (start + end) * 0.5f, (float)Math.Atan2(end.Y - start.Y, end.X - start.X) + MathHelper.PiOver2);
        private static void CrawlLimb(HumanoidAnimController a, InputState state, Limb limb, Vector2 target, float angle)
        {
            if (limb?.body == null || limb.IsSevered || limb.Disabled) return;
            float blend = MathHelper.SmoothStep(0, 1, MathHelper.Clamp(state.Transition / 0.45f, 0, 1));
            if (state.Starts.TryGetValue(limb, out var start))
                target = Vector2.Lerp(start + new Vector2(a.Collider.SimPosition.X - state.Origin.X, 0), target, blend);
            // Return to the original magnetic-wall style position controller.
            // Keep native force accumulation and joint ownership intact.
            if (limb.PullJointEnabled) limb.PullJointWorldAnchorB = target;
            limb.body.MoveToPos(target, 8);
            limb.body.SmoothRotate(limb.body.Rotation + MathUtils.GetShortestAngle(limb.body.Rotation, angle) * blend,
                Math.Max(1, limb.Mass) * 15);
        }
        private static void Remove(Character __instance)
        {
            inputs.Remove(__instance);
        }
        // Keep the crawling IK branch at rest. Native crouch idle pulls the feet
        // under the body with a hard-coded leg torque of 100, regardless of XML.
        // Alter only the animation branch predicate, never TargetMovement/velocity.
        private static bool UseCrouchIdle(bool stationary, HumanoidAnimController a)
            => stationary && !(IsCrawling(a.Character) && Valid(a.Character));
        private static IEnumerable<CodeInstruction> Standing(IEnumerable<CodeInstruction> instructions)
        {
            int matches = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(MathUtils)
                    && method.Name == nameof(MathUtils.NearlyEqual))
                {
                    matches++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ActiveCrawl), nameof(UseCrouchIdle)));
                }
            }
            if (matches != 1) throw new InvalidOperationException("ActiveCrawl: native standing animation predicate changed.");
        }
        private static void Clear()
        {
            inputs.Clear();
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.activecrawl");
            try
            {
#if CLIENT
                harmony.Patch(AccessTools.Method(typeof(Character), "ControlLocalPlayer", new[] { typeof(float), typeof(Camera), typeof(bool) }), postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(LocalInput)));
#endif
#if SERVER
                harmony.Patch(AccessTools.Method(typeof(Character), "UpdateNetInput", Type.EmptyTypes), postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(RemoteInput)));
#endif
                harmony.Patch(AccessTools.Method(typeof(Character), "Update", new[] { typeof(float), typeof(Camera) }), postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(Update)));
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateAnim"), prefix: new HarmonyMethod(typeof(ActiveCrawl), nameof(AnimationStep)) { priority = Priority.First });
                harmony.Patch(AccessTools.Method(typeof(AnimController), "GetCurrentSpeed"), postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(Speed)) { priority = Priority.Last });
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateStanding"),
                    postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(CrawlPose)),
                    transpiler: new HarmonyMethod(typeof(ActiveCrawl), nameof(Standing)));
                harmony.Patch(AccessTools.Method(typeof(Character), "Remove", Type.EmptyTypes), prefix: new HarmonyMethod(typeof(ActiveCrawl), nameof(Remove)));
                harmony.Patch(AccessTools.Method(typeof(GameSession), "EndRound"), prefix: new HarmonyMethod(typeof(ActiveCrawl), nameof(Clear)));
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        public void Dispose() { harmony?.UnpatchSelf(); Clear(); }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
