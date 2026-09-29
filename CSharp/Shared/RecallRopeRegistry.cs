using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    public sealed class RecallRopeRegistry : IAssemblyPlugin
    {
        private static readonly Dictionary<Item, (Projectile Projectile, Rope Rope)> ropes = new();
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("aic.recall.rope.registry");
            try
            {
                ropes.Clear();
                foreach (var constructor in typeof(Item).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(RecallRopeRegistry), nameof(Register)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Remove), Type.EmptyTypes),
                    prefix: new HarmonyMethod(typeof(RecallRopeRegistry), nameof(Unregister)));
                // One discovery pass also covers items that predate plugin initialization.
                foreach (var item in Item.ItemList) Register(item);
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                ropes.Clear();
                LuaCsLogger.LogError("[AIC RecallRopeRegistry] " + error);
            }
        }

        private static void Register(Item __instance)
        {
            if (__instance.Removed) return;
            var rope = __instance.GetComponent<Rope>();
            var projectile = __instance.GetComponent<Projectile>();
            if (rope != null && projectile != null) ropes[__instance] = (projectile, rope);
        }

        private static void Unregister(Item __instance) => ropes.Remove(__instance);

        internal static void SnapConnected(Character user)
        {
            if (user == null) return;
            List<Rope> matching = null;
            foreach (var entry in ropes)
            {
                var (projectile, rope) = entry.Value;
                if (!entry.Key.Removed && !rope.Snapped && IsConnected(projectile, user))
                    (matching ??= new List<Rope>()).Add(rope);
            }
            // Resolve ownership and attachment at use time: both can change after firing.
            // Snap outside enumeration so callbacks may safely remove registered items.
            if (matching != null)
                foreach (var rope in matching)
                    if (!rope.Item.Removed) rope.Snapped = true;
        }

        private static bool IsConnected(Projectile projectile, Character user)
        {
            if (projectile.User == user || projectile.Attacker == user) return true;
            // Also cover a launcher picked up after somebody else fired it.
            var launcher = projectile.Launcher;
            if (launcher != null && !launcher.Removed
                && user.HasEquippedItem(launcher, predicate: CharacterInventory.IsHandSlotType)) return true;
            var target = projectile.StickTarget?.UserData;
            if (target is Limb limb) return limb.character == user;
            if (target is Character character) return character == user;
            // Carried equipment moves with its wearer too. Backpack hits are normally
            // mapped to the torso by BackpackAttachments, and are covered above.
            return target is Item item && !item.Removed && item.IsOwnedBy(user);
        }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); ropes.Clear(); }
    }
}
