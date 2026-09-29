#if CLIENT
using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AIC.Components
{
    public sealed class GavelTrajectory : IAssemblyPlugin
    {
        private const string WeaponId = "aic_gdf_gavelrotary_grenadelauncher";
        private const string ThermalWeaponId = "aic_faithguard_molten_thermal_launcher";
        private const float RefreshInterval = 0.1f;
        private const float Horizon = 2.4f;
        private const int Segments = 48;
        private static readonly Vector2[] points = new Vector2[Segments + 1];
        private static Item cachedWeapon;
        private static Item cachedAmmo;
        private static Submarine cachedSub;
        private static double nextRefresh;
        private static bool valid;
        private static int pointCount;
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.gavel.trajectory");
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "DrawHUD",
                new[] { typeof(SpriteBatch), typeof(Character) }),
                postfix: new HarmonyMethod(typeof(GavelTrajectory), nameof(Draw)));
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            cachedWeapon = null;
            cachedAmmo = null;
            cachedSub = null;
            valid = false;
        }

        private static void Draw(RangedWeapon __instance, SpriteBatch spriteBatch, Character character)
        {
            var item = __instance.Item;
            if (item.Prefab.Identifier.Value != WeaponId && item.Prefab.Identifier.Value != ThermalWeaponId) return;
            if (character != Character.Controlled || character == null || character.IsDead
                || character.IsIncapacitated || character.InWater || item.InWater
                || item.Removed || item.Condition <= 0 || item.body == null
                || !character.IsKeyDown(InputType.Aim) || Screen.Selected?.Cam == null)
            {
                valid = false;
                return;
            }

            // False is essential: prediction must not activate ammunition containers.
            var projectile = __instance.FindProjectile(false);
            if (projectile == null || projectile.Hitscan || projectile.Item.body == null)
            {
                valid = false;
                return;
            }
            if (!valid || cachedWeapon != item || cachedAmmo != projectile.Item
                || cachedSub != item.Submarine || Timing.TotalTime >= nextRefresh)
            {
                cachedWeapon = item;
                cachedAmmo = projectile.Item;
                cachedSub = item.Submarine;
                nextRefresh = Timing.TotalTime + RefreshInterval;
                Vector2 origin = item.body.SimPosition + __instance.TransformedBarrelPos;
                float angle = item.body.Rotation - (item.body.Dir == 1 ? 0 : MathHelper.Pi);
                // Native DoLaunch multiplies impulse by mass before applying it.
                float speed = MathHelper.Clamp(projectile.LaunchImpulse + __instance.LaunchImpulse,
                    0, NetConfig.MaxPhysicsBodyVelocity * 0.95f);
                Vector2 velocity = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * speed;
                Vector2 gravity = GameMain.World.Gravity * projectile.Item.body.FarseerBody.GravityScale;
                pointCount = 0;
                Vector2 previous = item.body.SimPosition;
                for (int i = 0; i <= Segments; i++)
                {
                    float t = Horizon * i / Segments;
                    Vector2 next = origin + velocity * t + gravity * (0.5f * t * t);
                    // Include the weapon-to-muzzle segment so a muzzle protruding through a wall cannot bypass it.
                    var obstruction = Submarine.CheckVisibility(previous, next,
                        ignoreLevel: cachedSub != null, ignoreSubs: true,
                        blocksVisibilityPredicate: fixture =>
                            fixture.Body.UserData is Entity entity
                                ? entity.Submarine == cachedSub
                                : cachedSub == null);
                    if (obstruction != null)
                    {
                        if (i > 0)
                            points[pointCount++] = ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition);
                        break;
                    }
                    points[pointCount++] = ConvertUnits.ToDisplayUnits(next);
                    previous = next;
                }
                valid = true;
            }

            var camera = Screen.Selected.Cam;
            Vector2 submarineOffset = cachedSub?.Position ?? Vector2.Zero;
            for (int i = 1; i < pointCount; i++)
            {
                // Cached points remain submarine-local so a moving submarine does not leave the line behind.
                Vector2 a = camera.WorldToScreen(points[i - 1] + submarineOffset);
                Vector2 b = camera.WorldToScreen(points[i] + submarineOffset);
                if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y)) continue;
                GUI.DrawLine(spriteBatch, a, b, new Color(110, 210, 235, 150), width: 2);
            }
        }
    }
}
#endif
