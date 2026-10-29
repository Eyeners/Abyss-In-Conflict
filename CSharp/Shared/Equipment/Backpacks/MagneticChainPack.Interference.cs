using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    partial class MagneticChainPack
    {
        private double allDisabledUntil;
        private readonly double[] armDisabledUntil = new double[3];
        private float heldTime, damageClock, stunClock;
        private static readonly List<WeakReference<MagneticChainPack>> interferencePacks = new();
        private static void RegisterInterference(MagneticChainPack pack) => interferencePacks.Add(new(pack));
        internal static void ClearInterference() => interferencePacks.Clear();
        private static void ApplyGripDamage(Character target, string id, float strength, Limb limb)
        {
            if (AfflictionPrefab.Prefabs.TryGet(id.ToIdentifier(), out var prefab))
                target.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(strength));
        }
        private void DisableAll()
        {
            allDisabledUntil = Math.Max(allDisabledUntil, Timing.TotalTime + 5);
            foreach (var g in grips) { g.Wall = null; g.DisabledUntil = allDisabledUntil; }
            for (int i = 0; i < arms.Length; i++) DisableArm(i, 5);
            stunAnchor = null;
            Changed();
        }
        private void DisableArm(int index, double seconds)
        {
            armDisabledUntil[index] = Math.Max(armDisabledUntil[index], Timing.TotalTime + seconds);
            if (arms[index].Phase != 0)
                ReturnArm(index, Item.ParentInventory?.Owner as Character,
                    index == 0 && grabbed != null ? grabbed.WorldPosition : arms[index].Position((float)(Timing.TotalTime - arms[index].Born)));
            Changed();
        }
        internal static void ElectricalHit(Vector2 point, float radius, bool emp, Character victim = null)
        {
            if (GameMain.NetworkMember?.IsClient == true || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
            float radiusSquared = Math.Max(24, radius) * Math.Max(24, radius);
            for (int p = interferencePacks.Count - 1; p >= 0; p--)
            {
                if (!interferencePacks[p].TryGetTarget(out var pack) || pack.Item.Removed)
                { interferencePacks.RemoveAt(p); continue; }
                var wearer = pack.Item.ParentInventory?.Owner as Character;
                if (Equipped(wearer) != pack) continue;
                bool Near(Vector2 position) => Vector2.DistanceSquared(position, point) <= radiusSquared;
                if (emp && (wearer == victim || Near(wearer.WorldPosition))) { pack.DisableAll(); continue; }
                if (emp)
                {
                    foreach (var g in pack.grips)
                    {
                        if (g.Wall == null || !Near(g.World)) continue;
                        g.DisabledUntil = Math.Max(g.DisabledUntil, Timing.TotalTime + 3);
                        g.Wall = null; pack.Changed();
                    }
                }
                for (int i = 0; i < pack.arms.Length; i++)
                {
                    var arm = pack.arms[i];
                    if (arm.Phase != 1 && arm.Phase != 3) continue;
                    Vector2 tip = i == 0 && pack.grabbed != null ? pack.grabbed.WorldPosition
                        : arm.Position((float)(Timing.TotalTime - arm.Born));
                    if (Near(tip)) pack.DisableArm(i, 3);
                }
            }
        }
#if SERVER
        private void WriteInterference(IWriteMessage msg)
        {
            msg.WriteSingle((float)Math.Max(0, allDisabledUntil - Timing.TotalTime));
            foreach (var g in grips) msg.WriteSingle((float)Math.Max(0, g.DisabledUntil - Timing.TotalTime));
            foreach (var time in armDisabledUntil) msg.WriteSingle((float)Math.Max(0, time - Timing.TotalTime));
        }
#endif
#if CLIENT
        private void ReadInterference(IReadMessage msg)
        {
            allDisabledUntil = Timing.TotalTime + msg.ReadSingle();
            foreach (var g in grips) g.DisabledUntil = Timing.TotalTime + msg.ReadSingle();
            for (int i = 0; i < arms.Length; i++) armDisabledUntil[i] = Timing.TotalTime + msg.ReadSingle();
        }
#endif
    }
    public sealed class MagneticChainInterference : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool Electrical(Attack attack, out bool emp)
        {
            emp = false; bool shock = false;
            if (attack == null) return false;
            foreach (var a in attack.Afflictions.Keys)
            {
                if (a.Strength <= 0) continue;
                emp |= a.Prefab.Identifier == "emp".ToIdentifier();
                shock |= a.Prefab.Identifier == "electricshock".ToIdentifier();
            }
            return emp || shock;
        }
        private static void Damage(Attack __instance, IDamageable target, Vector2 worldPosition)
        {
            if (Electrical(__instance, out bool emp))
                MagneticChainPack.ElectricalHit(worldPosition, 28, emp, target as Character);
        }
        private static void LimbDamage(Attack __instance, Limb targetLimb, Vector2 worldPosition)
        {
            if (Electrical(__instance, out bool emp))
                MagneticChainPack.ElectricalHit(worldPosition, 28, emp, targetLimb?.character);
        }
        private static void Blast(Explosion __instance, Vector2 worldPosition)
        {
            if (__instance.EmpStrength > 0)
                MagneticChainPack.ElectricalHit(worldPosition, __instance.Attack.Range, true);
            else if (Electrical(__instance.Attack, out bool emp))
                MagneticChainPack.ElectricalHit(worldPosition, __instance.Attack.Range, emp);
        }
        private static void Afflicted(CharacterHealth __instance, Limb targetLimb, Affliction affliction)
        {
            if (affliction == null || affliction.Strength <= 0) return;
            bool emp = affliction.Prefab.Identifier == "emp".ToIdentifier();
            if (!emp && affliction.Prefab.Identifier != "electricshock".ToIdentifier()) return;
            var c = __instance.Character;
            MagneticChainPack.ElectricalHit(targetLimb?.WorldPosition ?? c.WorldPosition, 28, emp, c);
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.magnetic.chain.interference");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Attack), "DoDamage"), prefix: new HarmonyMethod(typeof(MagneticChainInterference), nameof(Damage)));
                harmony.Patch(AccessTools.Method(typeof(Attack), "DoDamageToLimb"), prefix: new HarmonyMethod(typeof(MagneticChainInterference), nameof(LimbDamage)));
                harmony.Patch(AccessTools.Method(typeof(Explosion), "Explode"), prefix: new HarmonyMethod(typeof(MagneticChainInterference), nameof(Blast)));
                harmony.Patch(AccessTools.Method(typeof(CharacterHealth), "ApplyAffliction"), postfix: new HarmonyMethod(typeof(MagneticChainInterference), nameof(Afflicted)));
            }
            catch (Exception error) { harmony.UnpatchSelf(); DebugConsole.ThrowError("[AIC MagneticChain EMP] " + error); }
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); MagneticChainPack.ClearInterference(); }
    }
}
