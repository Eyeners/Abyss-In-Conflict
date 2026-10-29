using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class PostureWeaponControl : IAssemblyPlugin
    {
        private Harmony harmony;
        private struct ShotState { internal PhysicsBody Body; internal Vector2 Velocity; }
        private static bool Prone(Character c) => ActiveCrawl.Level(c) == 2
            && MovementState.ActiveOnLand(c) && c.AnimController.OnGround;
        private static void Spread(Character __0, ref float __result)
        {
            if (Prone(__0)) __result *= 0.5f;
        }
        private static void BeforeShot(RangedWeapon __instance, Character __1, out ShotState __state)
        {
            __state = default;
            if (Prone(__1) && __instance.Item.body != null)
                __state = new ShotState { Body = __instance.Item.body, Velocity = __instance.Item.body.LinearVelocity };
        }
        private static void AfterShot(bool __result, ShotState __state)
        {
            if (__result && __state.Body != null && !__state.Body.Removed)
                __state.Body.LinearVelocity = Vector2.Lerp(__state.Velocity, __state.Body.LinearVelocity, 0.5f);
        }
        private static bool LadderSelect(Character __0, ref bool __result)
        {
            if (MagneticChainPack.Equipped(__0)?.IsOn != true) return true;
            __result = false; return false;
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.posture.weapon.control");
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "GetSpread", new[] { typeof(Character) }),
                postfix: new HarmonyMethod(typeof(PostureWeaponControl), nameof(Spread)));
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "Use", new[] { typeof(float), typeof(Character) }),
                prefix: new HarmonyMethod(typeof(PostureWeaponControl), nameof(BeforeShot)),
                postfix: new HarmonyMethod(typeof(PostureWeaponControl), nameof(AfterShot)));
            harmony.Patch(AccessTools.Method(typeof(Ladder), "Select", new[] { typeof(Character) }),
                prefix: new HarmonyMethod(typeof(PostureWeaponControl), nameof(LadderSelect)));
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
