using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace AIC.Components
{
    internal sealed class ReloadClock
    {
        internal double ReadyAt { get; private set; }
        internal double Add(double now, float duration, bool perItem)
        {
            double start = perItem ? Math.Max(now, ReadyAt) : now;
            ReadyAt = start + Math.Max(0, duration);
            return start;
        }
        internal float Remaining(double now) => (float)Math.Max(0, ReadyAt - now);
    }

    // Inventory reconciliation, timing and the fire gate are shared by all opted-in
    // weapons. Their XML owns durations, batch policy, fuel requirements and sounds.
    internal sealed class InsertionReload : ItemComponent
    {
        [Serialize(1f, IsPropertySaveable.No)] public float Duration { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool PerItem { get; set; }
        [Serialize("", IsPropertySaveable.No)] public string AmmoTag { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool SoundAtStart { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool EndSound { get; set; }
        [Serialize(false, IsPropertySaveable.No)] public bool CancelSoundOnRemoval { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float MinimumFuelCondition { get; set; }
        [Serialize(0f, IsPropertySaveable.No)] public float FuelCost { get; set; }
        [Serialize(6, IsPropertySaveable.No)] public int FuelSlots { get; set; }
        private HashSet<Item> previous = new(), current = new();
        private readonly List<Item> added = new();
        private readonly ReloadClock clock = new();
        private RangedWeapon weapon;
        private bool dirty = true;
#if CLIENT
        private readonly Queue<(double At, Item Ammo)> sounds = new();
        private bool ending;
#endif
        private static readonly Action<RangedWeapon, float> SetReloadTimer =
            AccessTools.MethodDelegate<Action<RangedWeapon, float>>(AccessTools.PropertySetter(typeof(RangedWeapon), "ReloadTimer"));
        internal float Remaining => clock.Remaining(Timing.TotalTime);
        public InsertionReload(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        internal void InventoryChanged() { dirty = true; IsActive = true; }
        internal void Reconcile()
        {
            if (Item.Removed) return;
            dirty = false;
            current.Clear(); added.Clear();
            if (Item.OwnInventory != null)
                foreach (var ammo in Item.OwnInventory.AllItems)
                    if (!ammo.Removed && (string.IsNullOrEmpty(AmmoTag) || ammo.HasTag(AmmoTag)))
                    {
                        if (!current.Add(ammo)) continue;
                        if (!previous.Contains(ammo)) added.Add(ammo);
                    }
            (previous, current) = (current, previous);
            if (added.Count == 0) return;
            double now = Timing.TotalTime;
            int count = PerItem ? added.Count : 1;
            for (int i = 0; i < count; i++)
            {
                double start = clock.Add(now, Duration, PerItem);
#if CLIENT
                sounds.Enqueue((SoundAtStart ? start : clock.ReadyAt, added[i]));
#endif
            }
#if CLIENT
            ending = EndSound;
#endif
            weapon ??= Item.GetComponent<RangedWeapon>();
            if (weapon != null)
            {
                SetReloadTimer(weapon, Math.Max(weapon.ReloadTimer, Remaining));
                weapon.IsActive = true;
            }
        }
        private Item Fuel()
        {
            if (Item.OwnInventory == null) return null;
            for (int i = 0; i < Math.Min(FuelSlots, Item.OwnInventory.Capacity); i++)
            {
                var fuel = Item.OwnInventory.GetItemAt(i);
                if (fuel != null && !fuel.Removed && fuel.HasTag(AmmoTag)
                    && fuel.Condition >= MinimumFuelCondition) return fuel;
            }
            return null;
        }
        internal bool CanFire
        {
            get { Reconcile(); return Remaining <= 0 && (MinimumFuelCondition <= 0 || Fuel() != null); }
        }
        internal void Fired()
        {
            if (GameMain.NetworkMember?.IsClient != true && FuelCost > 0)
            {
                var fuel = Fuel();
                if (fuel != null) fuel.Condition = Math.Max(0, fuel.Condition - FuelCost);
            }
        }
        public override void Update(float dt, Camera cam)
        {
            if (dirty) Reconcile();
#if CLIENT
            while (sounds.Count > 0 && sounds.Peek().At <= Timing.TotalTime)
            {
                var sound = sounds.Dequeue();
                if (!CancelSoundOnRemoval || !sound.Ammo.Removed && previous.Contains(sound.Ammo))
                    PlaySound(ActionType.OnUse);
            }
            if (ending && Remaining <= 0)
            {
                ending = false;
                if (previous.Count > 0) PlaySound(ActionType.OnSecondaryUse);
            }
#endif
            if (Remaining <= 0) IsActive = false;
        }
        public override bool Use(float dt, Character character = null) => false;
        public override bool SecondaryUse(float dt, Character character = null) => false;
    }

    public sealed class InsertionReloadHooks : IAssemblyPlugin
    {
        private Harmony harmony;
        private static void InventoryChanged(ItemContainer __instance)
            => __instance.Item.GetComponent<InsertionReload>()?.InventoryChanged();
        private static bool Before(RangedWeapon __instance, ref bool __result)
        {
            if (__instance.Item.GetComponent<InsertionReload>()?.CanFire != false) return true;
            __result = false;
            return false;
        }
        private static void After(RangedWeapon __instance, bool __result)
        {
            if (__result && __instance.LastProjectile != null)
                __instance.Item.GetComponent<InsertionReload>()?.Fired();
        }
        public void Initialize()
        {
            harmony = new Harmony("aic.insertionreload");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "Use"),
                    prefix: new HarmonyMethod(typeof(InsertionReloadHooks), nameof(Before)),
                    postfix: new HarmonyMethod(typeof(InsertionReloadHooks), nameof(After)));
                harmony.Patch(AccessTools.Method(typeof(ItemContainer), "OnItemContained"),
                    postfix: new HarmonyMethod(typeof(InsertionReloadHooks), nameof(InventoryChanged)));
                harmony.Patch(AccessTools.Method(typeof(ItemContainer), "OnItemRemoved"),
                    postfix: new HarmonyMethod(typeof(InsertionReloadHooks), nameof(InventoryChanged)));
            }
            catch { harmony.UnpatchSelf(); throw; }
        }
        public void Dispose() => harmony?.UnpatchSelf();
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
    }
}
