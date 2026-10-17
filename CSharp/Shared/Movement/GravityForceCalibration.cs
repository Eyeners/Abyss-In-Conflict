using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    public sealed class GravityForceCalibration : IAssemblyPlugin
    {
        // Standard gravity, independent of the editor's zero-gravity toggle.
        private const float Acceleration = 3.0f * 9.82f;
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.gravity.force.calibration");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(TriggerComponent), "ApplyForce"),
                    prefix: new HarmonyMethod(typeof(GravityForceCalibration), nameof(FieldForce)));
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static bool FieldForce(TriggerComponent __instance, PhysicsBody body)
        {
            // Prefix 返回 true 表示原版继续执行；false 表示本次施力已由这里接管。
            string id = __instance.Item.Prefab.Identifier.Value;
            if (id != "aic_antigravity_field" && id != "aic_rectangular_gravity_field"
                && id != "aic_gravity_gun_field") return true;
            if (body == null || !body.Enabled) return false;
            // XML retains the relative strengths (500, 500, 1000). Apply m*a per body,
            // bypassing the native limb multiplier and light-body force adjustment.
            Vector2 direction = __instance.DirectionalForce / 500.0f;
            // XML 的 500 对应 3g，1000 对应 6g；此时 direction 包含方向和倍率，不是单位向量。
            if (__instance.Item.FlippedX) direction.X = -direction.X;
            if (__instance.Item.FlippedY) direction.Y = -direction.Y;
            direction = MathUtils.RotatePoint(direction, -__instance.Item.RotationRad);
            // F=m*a，各碰撞体按自身质量受力；不在这里乘 deltaTime，物理引擎负责积分。
            body.ApplyForce(direction * (Acceleration * body.Mass));
            return false;
        }

    }
}
