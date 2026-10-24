using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using System.Reflection;
using System.Reflection.Emit;

namespace AIC.Components
{
    public sealed class ActiveCrawl : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly Identifier Id = "aic_active_crawl".ToIdentifier();
        private sealed class InputState { internal bool Up, Down; }
        private static readonly Dictionary<Character, InputState> inputs = new();
        private static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static bool IsCrawling(Character c) =>
            (c?.CharacterHealth?.GetAffliction(Id)?.Strength ?? 0) > 0;
        private static bool Valid(Character c) => MovementState.ActiveOnLand(c)
            && c.AnimController is HumanoidAnimController a && a.CanWalk
            && a.TryGetCollider(1, out var small) && small != null
            && !HoverBike.Riding(c) && !HoverJetpack.Flying(c) && !MovementState.SelectingLadder(c);
        private static void Set(Character c, bool enabled)
        {
            if (IsCrawling(c) == enabled) return;
            if (enabled)
            {
                if (!AfflictionPrefab.Prefabs.TryGet(Id, out var prefab)) return;
                c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(1));
            }
            else c.CharacterHealth.ReduceAfflictionOnAllLimbs(Id, float.MaxValue);
            c.HealthUpdateInterval = 0;
#if SERVER
            GameMain.Server?.CreateEntityEvent(c, new Character.CharacterStatusEventData(forceAfflictionData: true));
#endif
        }
        private static void Input(Character c, bool blocked)
        {
            if (!Authority || c == null || c.Removed) return;
            if (!inputs.TryGetValue(c, out var state)) inputs[c] = state = new InputState();
            bool up = c.IsKeyDown(InputType.Up), down = c.IsKeyDown(InputType.Down);
            bool enter = down && !state.Down, leave = up && !state.Up;
            state.Up = up; state.Down = down;
            if (blocked || !c.AllowInput || !Valid(c)) return;
            if (!c.IsKeyDown(InputType.Crouch)) return;
            if (leave) Set(c, false);
            else if (enter && !up && c.AnimController.OnGround
                && FoehnSlide.Equipped(c)?.Sliding != true
                && FoehnJetJump.Equipped(c)?.Flying != true) Set(c, true);
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
            if (Authority && IsCrawling(__instance) && !Valid(__instance)) Set(__instance, false);
        }
        private static void Speed(AnimController __instance, ref float __result)
        {
            if (IsCrawling(__instance.Character) && Valid(__instance.Character))
                __result = __instance.GetSpeed(AnimationType.Walk) * 0.75f;
        }
        private static void Remove(Character __instance) => inputs.Remove(__instance);
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
        private static void Clear() => inputs.Clear();
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
                harmony.Patch(AccessTools.Method(typeof(AnimController), "GetCurrentSpeed"), postfix: new HarmonyMethod(typeof(ActiveCrawl), nameof(Speed)) { priority = Priority.Last });
                harmony.Patch(AccessTools.Method(typeof(HumanoidAnimController), "UpdateStanding"), transpiler: new HarmonyMethod(typeof(ActiveCrawl), nameof(Standing)));
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
