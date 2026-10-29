#if CLIENT
using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    // Placeholder geometry: keep these offsets in sync with ShoulderCannon.lua.
    public sealed class ShoulderCannonPreview : IAssemblyPlugin
    {
        private const float Forward = 12, Up = 30, BarrelLength = 45;
        private Harmony harmony;

        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.shoulder.cannon.preview");
            harmony.Patch(AccessTools.Method(typeof(Limb), "Draw",
                new[] { typeof(SpriteBatch), typeof(Camera), typeof(Color?), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(ShoulderCannonPreview), nameof(Draw)));
        }

        private static void Draw(Limb __instance, SpriteBatch spriteBatch)
        {
            var user = __instance.character;
            if (user == null || user.Removed || __instance.Removed || __instance.IsSevered
                || __instance.body == null || user.AnimController.GetLimb(LimbType.Torso) != __instance) return;
            var armor = user.GetEquippedItem("aic_test_shoulder_cannon_armor".ToIdentifier(), InvSlotType.OuterClothes);
            if (armor == null || armor.Removed || armor.Condition <= 0) return;
            var rotation = Matrix.CreateRotationZ(__instance.body.Rotation);
            Vector2 offset = Vector2.Transform(new Vector2(Forward * user.AnimController.Dir, Up), rotation);
            Vector2 aim = user.CursorWorldPosition - (__instance.WorldPosition + offset);
            Vector2 direction = aim.LengthSquared() > 0.001f ? Vector2.Normalize(aim)
                : new Vector2(user.AnimController.Dir, 0);
            Vector2 pivot = __instance.body.DrawPosition + offset;
            Vector2 end = pivot + direction * BarrelLength;
            Vector2 ScreenAxis(Vector2 p) => new Vector2(p.X, -p.Y);
            // World-space batch: width and length scale naturally with camera zoom.
            GUI.DrawLine(spriteBatch, ScreenAxis(pivot), ScreenAxis(end), Color.DarkSlateGray, width: 10);
            GUI.DrawLine(spriteBatch, ScreenAxis(pivot), ScreenAxis(end), Color.Orange, width: 6);
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; }
    }
}
#endif
