using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics.Dynamics;
using HarmonyLib;

namespace AIC.Components
{
    // Separate damage-only attack: secondary hits cannot repeat debuffs or recurse.
    internal sealed class TsunamiConduction : ItemComponent
    {
        private readonly Attack attack;
        private bool emitted;
        public TsunamiConduction(Item item, ContentXElement element) : base(item, element)
        {
            attack = new Attack(element.GetChildElement("Attack"), "Tsunami conduction", item);
        }
        internal List<Limb> Neighbours(Limb hit)
        {
            var result = new List<Limb>();
            if (emitted || hit == null || hit.Removed || hit.IsSevered) return result;
            var visited = new HashSet<Limb> { hit };
            var queue = new Queue<Limb>();
            queue.Enqueue(hit);
            bool Central(Limb limb) => limb.type == LimbType.Torso || limb.type == LimbType.Waist;
            bool trunk = Central(hit);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var joint in hit.character.AnimController.LimbJoints)
                {
                    if (joint.IsSevered) continue;
                    Limb next = joint.LimbA == current ? joint.LimbB : joint.LimbB == current ? joint.LimbA : null;
                    if (next == null || next.Removed || next.IsSevered || !visited.Add(next)) continue;
                    if (trunk && Central(next)) queue.Enqueue(next);
                    else result.Add(next);
                }
            }
            return result;
        }
        internal void Apply(Projectile projectile, List<Limb> neighbours)
        {
            if (emitted || neighbours == null || neighbours.Count == 0) return;
            emitted = true;
            neighbours.RemoveAll(l => l.Removed || l.IsSevered || l.character.Removed);
            if (neighbours.Count == 0) return;
            // XML contains half the base damage; inherit the original shot's quality/stat multiplier.
            attack.DamageMultiplier = (projectile.Attack?.DamageMultiplier ?? 1f) / neighbours.Count;
            foreach (var limb in neighbours)
                if (!limb.Removed && !limb.IsSevered && !limb.character.Removed)
                    attack.DoDamageToLimb(projectile.User ?? projectile.Attacker, limb, limb.WorldPosition, 1f, playSound: false);
        }
    }

    public sealed class TsunamiConductionSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            if (harmony != null) return;
            harmony = new Harmony("aic.tsunami.conduction");
            harmony.Patch(AccessTools.Method(typeof(Projectile), "HandleProjectileCollision"),
                prefix: new HarmonyMethod(typeof(TsunamiConductionSystem), nameof(Before)),
                postfix: new HarmonyMethod(typeof(TsunamiConductionSystem), nameof(After)));
        }
        private static void Before(Projectile __instance, Fixture target, out List<Limb> __state)
        {
            __state = null;
            if (GameMain.NetworkMember?.IsClient == true) return;
            // Capture connections before the primary hit can sever the struck limb.
            if (target?.Body.UserData is Limb limb)
                __state = __instance.Item.GetComponent<TsunamiConduction>()?.Neighbours(limb);
        }
        private static void After(Projectile __instance, bool __result, List<Limb> __state)
        {
            if (__result && __state != null && GameMain.NetworkMember?.IsClient != true)
                __instance.Item.GetComponent<TsunamiConduction>()?.Apply(__instance, __state);
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; }
    }
}
