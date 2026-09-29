using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Owned by the equipment component; tracks only live bodies for plugin cleanup.
    internal sealed class BackpackHitbox
    {
        private static readonly HashSet<BackpackHitbox> active = new HashSet<BackpackHitbox>();
        private Body body;
        private World world;

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
                body.CreateRectangle(0.24f, 0.50f, 1, Vector2.Zero,
                    Physics.CollisionItemBlocking, Physics.CollisionProjectile);
                active.Add(this);
            }
            Vector2 offset = Vector2.Transform(new Vector2(-0.28f * user.AnimController.Dir, 0),
                Matrix.CreateRotationZ(torso.Rotation));
            body.SetTransform(torso.SimPosition + offset, torso.Rotation);
            body.LinearVelocity = torso.body.LinearVelocity;
        }

        internal void Release()
        {
            if (body != null)
            {
                body.Enabled = false;
                if (world != null && world.BodyList.Contains(body)) world.Remove(body);
            }
            body = null; world = null;
            active.Remove(this);
        }

        internal static void ClearAll()
        {
            foreach (var hitbox in active.ToArray()) hitbox.Release();
        }
    }

    public sealed class BackpackHitboxPlugin : IAssemblyPlugin
    {
        public void Initialize() { }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { BackpackHitbox.ClearAll(); }
    }
}
