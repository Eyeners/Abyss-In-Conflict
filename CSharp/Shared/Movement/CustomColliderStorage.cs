using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace AIC.Components
{
    internal static class CustomColliderStorage
    {
        private static readonly FieldInfo Bodies = AccessTools.Field(typeof(Ragdoll), "collider");
        private static readonly FieldInfo CurrentIndex = AccessTools.Field(typeof(Ragdoll), "colliderIndex");
        internal static List<PhysicsBody> List(Ragdoll owner) => (List<PhysicsBody>)Bodies.GetValue(owner);
        internal static bool Registered(Ragdoll owner, PhysicsBody body, int index)
        {
            var list = List(owner);
            return body != null && !body.Removed && index >= 0 && index < list.Count && list[index] == body;
        }
        internal static PhysicsBody Add(Ragdoll owner, float width, float height, float radius, out int index)
        {
            var normal = owner.Collider;
            var body = new PhysicsBody(width, height, radius,
                normal.FarseerBody.FixtureList[0].Shape.Density, BodyType.Dynamic,
                normal.CollisionCategories, normal.CollidesWith);
            body.UserData = owner.Character;
            body.Submarine = normal.Submarine;
            body.FarseerBody.OnCollision += owner.OnLimbCollision;
            body.FarseerBody.FixedRotation = true;
            body.PhysEnabled = false;
            var list = List(owner);
            index = list.Count;
            list.Add(body);
            return body;
        }
        internal static void ResetForRebuild(Ragdoll owner) => CurrentIndex.SetValue(owner, 0);
        internal static bool RestoreSelected(Ragdoll owner, PhysicsBody body, int index, int preferred)
        {
            if (!Registered(owner, body, index) || owner.ColliderIndex != index) return false;
            var list = List(owner);
            if (preferred < 0 || preferred >= list.Count || list[preferred].Removed) preferred = 0;
            // Keep native headroom checks and velocity transfer for ordinary exits.
            // Only ResetForRebuild bypasses those checks before destruction.
            owner.ColliderIndex = preferred;
            return owner.ColliderIndex != index;
        }
        internal static bool RemoveUnusedTail(Ragdoll owner, PhysicsBody body)
        {
            var list = List(owner);
            if (body == null || body.Removed || list.Count == 0 || list[list.Count - 1] != body
                || owner.ColliderIndex < 0 || owner.ColliderIndex >= list.Count || list[owner.ColliderIndex] == body) return false;
            list.RemoveAt(list.Count - 1);
            body.Remove();
            return true;
        }
    }

    public sealed class CustomColliderLifecycle : IAssemblyPlugin
    {
        private Harmony harmony;
        private static void BeforeRecreate(Ragdoll __instance, RagdollParams ragdollParams)
        {
            // A single ordered entry point invalidates both owners before native
            // CreateColliders replaces the list. Old bodies are removed by native code.
            HoverBike.Equipped(__instance.Character)?.BeforeRecreate(__instance);
            LowPostureCollider.BeforeRecreate(__instance);
            // A blocked dismount can leave an in-use custom body for native code
            // to retire later. Even without a current owner, its index cannot
            // survive a rebuild to a smaller native collider list.
            var target = ragdollParams ?? __instance.RagdollParams;
            if (__instance.ColliderIndex < 0 || __instance.ColliderIndex >= target.Colliders.Count)
                CustomColliderStorage.ResetForRebuild(__instance);
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.customcollider.lifecycle");
            harmony.Patch(AccessTools.Method(typeof(Ragdoll), "Recreate", new[] { typeof(RagdollParams) }),
                prefix: new HarmonyMethod(typeof(CustomColliderLifecycle), nameof(BeforeRecreate)));
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
