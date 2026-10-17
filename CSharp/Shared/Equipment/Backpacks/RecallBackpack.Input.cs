#if CLIENT
using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace AIC.Components
{
    partial class RecallBackpack
    {
        internal static RecallBackpack Preview;
        private Character releaseUser;
        private bool previousQ, releaseMode;
        private float holdTime;

        private bool CanReleaseInput(Character user) => Worn(user) && user == Character.Controlled
            && !user.IsIncapacitated && GameMain.WindowActive && Screen.Selected is GameScreen
            && GUI.KeyboardDispatcher.Subscriber == null && !GUI.InputBlockingMenuOpen;

        private void CancelReleaseInput()
        {
            releaseUser = null;
            holdTime = 0;
            if (Preview == this) Preview = null;
        }

        private void UpdateReleaseInput(Character user, float deltaTime, Camera cam)
        {
            // Use the game's sampled input, not a separate hardware poll. A quick tap
            // needs no charge threshold; release is checked on every update.
            bool q = PlayerInput.KeyDown(Keys.Q);
            bool pressed = q && !previousQ;
            previousQ = q;
            if (!CanReleaseInput(user) || cam == null)
            {
                CancelReleaseInput();
                return;
            }
            if (releaseUser != null && (releaseUser != user || releaseMode != JumpMode))
            {
                CancelReleaseInput();
                return;
            }
            if (pressed)
            {
                releaseUser = user;
                releaseMode = JumpMode;
                holdTime = 0;
                Preview = this;
            }
            if (releaseUser == null) return;
            if (q) { holdTime += deltaTime; return; }
            var cursor = cam.ScreenToWorld(PlayerInput.MousePosition);
            CancelReleaseInput();
            if (GameMain.NetworkMember?.IsClient == true) Item.CreateClientEvent(this, new JumpRequest(cursor));
            else Recall(user, false, cursor);
        }

        internal void DrawReleasePreview(SpriteBatch spriteBatch)
        {
            var user = releaseUser;
            if (holdTime < 0.15f || !JumpMode || !CanReleaseInput(user)) return;
            var core = Item.GetComponent<ItemContainer>()?.Inventory.GetItemAt(0);
            if (core == null || core.Removed || core.Prefab.Identifier != "aic_recall_core"
                || core.Condition < 3) return;
            var cam = Screen.Selected.Cam;
            Vector2 origin = user.WorldPosition;
            float range = ConvertUnits.ToDisplayUnits(8f);
            Vector2 offset = cam.ScreenToWorld(PlayerInput.MousePosition) - origin;
            if (offset.LengthSquared() > range * range) offset = Vector2.Normalize(offset) * range;
            Color color = new Color(100, 220, 255, 180);
            Vector2 previous = cam.WorldToScreen(origin + Vector2.UnitX * range);
            for (int i = 1; i <= 96; i++)
            {
                float angle = MathHelper.TwoPi * i / 96;
                Vector2 next = cam.WorldToScreen(origin + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * range);
                GUI.DrawLine(spriteBatch, previous, next, color, width: 2);
                previous = next;
            }
            Vector2 target = cam.WorldToScreen(origin + offset);
            GUI.DrawLine(spriteBatch, cam.WorldToScreen(origin), target, color * 0.5f, width: 1);
            GUI.DrawLine(spriteBatch, target - Vector2.UnitX * 7, target + Vector2.UnitX * 7, color, width: 2);
            GUI.DrawLine(spriteBatch, target - Vector2.UnitY * 7, target + Vector2.UnitY * 7, color, width: 2);
        }
    }

    public sealed class RecallBackpackPreview : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.recall.preview");
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "Draw"),
                postfix: new HarmonyMethod(typeof(RecallBackpackPreview), nameof(Draw)));
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; RecallBackpack.Preview = null; }
        private static void Draw(SpriteBatch __2)
        {
            if (RecallBackpack.Preview == null) return;
            __2.Begin();
            try { RecallBackpack.Preview.DrawReleasePreview(__2); }
            finally { __2.End(); }
        }
    }
}
#endif
