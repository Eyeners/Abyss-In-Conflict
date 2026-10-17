using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
#if CLIENT
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    // Owned by the equipment component; tracks only live bodies for plugin cleanup.
    internal sealed class BackpackHitbox
    {
        private static readonly HashSet<BackpackHitbox> active = new HashSet<BackpackHitbox>();
        private Body body;
        private World world;
        private Character wearer;
        private const float Width = 0.24f, Height = 0.50f;

        internal void Update(Item item, Character user)
        {
            var torso = user != null && !user.Removed
                && user.GetEquippedItem(item.Prefab.Identifier, InvSlotType.Bag) == item
                ? user.AnimController?.GetLimb(LimbType.Torso) : null;
            if (item.Removed || item.Condition <= 0 || torso?.body == null || torso.Removed || torso.IsSevered
                || GameMain.World == null)
            { Release(); return; }
            if (world != GameMain.World) Release();
            if (body == null)
            {
                world = GameMain.World;
                body = world.CreateBody(Vector2.Zero, 0, BodyType.Kinematic);
                body.UserData = item;
                // Projectile-only collision: never push the wearer or catch on doors/floors.
                // Keep the rear edge at 0.40 m; trim 0.06 m from the wearer-facing edge.
                body.CreateRectangle(Width, Height, 1, Vector2.Zero,
                    Physics.CollisionItemBlocking, Physics.CollisionProjectile);
                active.Add(this);
            }
            Vector2 offset = Vector2.Transform(new Vector2(-0.28f * user.AnimController.Dir, 0),
                Matrix.CreateRotationZ(torso.Rotation));
            body.SetTransform(torso.SimPosition + offset, torso.Rotation);
            body.LinearVelocity = torso.body.LinearVelocity;
            wearer = user;
        }

        internal void Release()
        {
            if (body != null)
            {
                body.Enabled = false;
                if (world != null && world.BodyList.Contains(body)) world.Remove(body);
            }
            body = null; world = null; wearer = null;
            active.Remove(this);
        }

        internal static void ClearAll()
        {
            foreach (var hitbox in active.ToArray()) hitbox.Release();
        }
#if CLIENT
        internal static void DrawDebug(SpriteBatch spriteBatch, Camera camera)
        {
            foreach (var hitbox in active)
            {
                if (hitbox.body == null || !hitbox.body.Enabled || hitbox.wearer == null) continue;
                Vector2 offset = hitbox.wearer.Submarine?.Position ?? Vector2.Zero;
                Vector2 Corner(float x, float y) => camera.WorldToScreen(
                    ConvertUnits.ToDisplayUnits(hitbox.body.GetWorldPoint(new Vector2(x, y))) + offset);
                Vector2 a = Corner(-Width / 2, -Height / 2), b = Corner(Width / 2, -Height / 2);
                Vector2 c = Corner(Width / 2, Height / 2), d = Corner(-Width / 2, Height / 2);
                GUI.DrawLine(spriteBatch, a, b, Color.Cyan, width: 2);
                GUI.DrawLine(spriteBatch, b, c, Color.Cyan, width: 2);
                GUI.DrawLine(spriteBatch, c, d, Color.Cyan, width: 2);
                GUI.DrawLine(spriteBatch, d, a, Color.Cyan, width: 2);
            }
        }
#endif
    }

    public sealed class BackpackHitboxPlugin : IAssemblyPlugin
    {
#if CLIENT
        private Harmony harmony;
#endif
        public void Initialize()
        {
#if CLIENT
            harmony = new Harmony("aic.backpack.hitbox.debug");
            harmony.Patch(AccessTools.Method(typeof(GameScreen), nameof(GameScreen.Draw)),
                postfix: new HarmonyMethod(typeof(BackpackHitboxPlugin), nameof(DrawDebug)));
#endif
        }
#if CLIENT
        private static void DrawDebug(GameScreen __instance, SpriteBatch spriteBatch)
        {
            if (!GameMain.DebugDraw) return;
            spriteBatch.Begin();
            BackpackHitbox.DrawDebug(spriteBatch, __instance.Cam);
            spriteBatch.End();
        }
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
#if CLIENT
            harmony?.UnpatchSelf();
#endif
            BackpackHitbox.ClearAll();
        }
    }
}
