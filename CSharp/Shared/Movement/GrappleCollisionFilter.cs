using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class GrappleCollisionFilter : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.grapple.collision.filter");
            try
            {
                foreach (string name in new[] { "OnProjectileCollision", "HandleProjectileCollision" })
                {
                    var method = AccessTools.Method(typeof(Projectile), name);
                    if (method == null) throw new MissingMethodException("Projectile." + name);
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(GrappleCollisionFilter), nameof(IgnoreCharacters)));
                }
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                LuaCsLogger.LogError("[AIC GrappleCollisionFilter] " + error);
            }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); }

        private static bool IgnoreCharacters(Projectile __instance, Fixture target, ref bool __result)
        {
            if (__instance.Item.Prefab.Identifier.Value != "aic_grapple_anchor") return true;
            if (!(target?.Body?.UserData is Limb) && !(target?.Body?.UserData is Character)) return true;
            // Reject before physics resolution and again before queued impact processing:
            // no hit count, impact effect, velocity loss or projectile deactivation.
            __result = false;
            return false;
        }
    }
}
