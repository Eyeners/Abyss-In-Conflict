using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
#if CLIENT
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed partial class TsunamiCharge : IServerSerializable
    {
        private float heat, coolingDelay, ventTime, lightTime;
        private bool spawningRound;
        internal bool Overheated => ventTime > 0;
        internal void Fired()
        {
            if (GameMain.NetworkMember?.IsClient == true) return;
            heat = Math.Min(60, heat + 20);
            coolingDelay = 0.8f;
            lightTime = 0.3f;
            if (heat >= 60)
            {
                ventTime = 2;
                ApplyStatusEffects(ActionType.OnUse, 1);
            }
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }

        private void UpdateHeat(float dt)
        {
            lightTime = Math.Max(0, lightTime - dt);
            var light = Item.GetComponent<LightComponent>();
            if (light != null) light.IsOn = lightTime > 0;
            if (ventTime > 0)
            {
                ApplyStatusEffects(ActionType.OnActive, dt);
                ventTime = Math.Max(0, ventTime - dt);
                if (ventTime == 0) heat = 0;
            }
            else if (Item.GetComponent<RangedWeapon>()?.currentChargeTime > 0)
            {
                coolingDelay = 0.8f;
            }
            else
            {
                float coolingTime = Math.Max(0, dt - coolingDelay);
                coolingDelay = Math.Max(0, coolingDelay - dt);
                heat = Math.Max(0, heat - coolingTime * 20);
            }
            if (GameMain.NetworkMember?.IsClient == true || spawningRound) return;
            var inventory = Item.GetComponent<ItemContainer>()?.Inventory;
            if (inventory == null || !inventory.IsEmpty()) return;
            var prefab = ItemPrefab.Prefabs.Find(p => p.Identifier == "aic_wardens_tsunami_bolt");
            if (prefab == null || Entity.Spawner == null) return;
            spawningRound = true;
            Entity.Spawner.AddItemToSpawnQueue(prefab, inventory, onSpawned: round =>
            {
                spawningRound = false;
                if (Item.Removed) Entity.Spawner.AddItemToRemoveQueue(round);
            });
        }
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        {
            msg.WriteSingle(heat); msg.WriteSingle(coolingDelay);
            msg.WriteSingle(ventTime); msg.WriteSingle(lightTime);
        }
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            bool wasVenting = Overheated;
            heat = msg.ReadSingle(); coolingDelay = msg.ReadSingle();
            ventTime = msg.ReadSingle(); lightTime = msg.ReadSingle();
            if (!wasVenting && Overheated) ApplyStatusEffects(ActionType.OnUse, 1);
        }
        public override void DrawHUD(SpriteBatch spriteBatch, Character character)
        {
            if (character == null || !character.IsKeyDown(InputType.Aim) || !character.CanAim
                || !character.HasEquippedItem(Item, predicate: CharacterInventory.IsHandSlotType)
                || GUI.MouseOn != null || Inventory.IsMouseOnInventory) return;
            var weapon = Item.GetComponent<RangedWeapon>();
            Vector2 p = weapon.crosshairPos + new Vector2(72, -25) * GUI.Scale;
            int width = (int)(7 * GUI.Scale), height = (int)(50 * GUI.Scale);
            GUI.DrawRectangle(spriteBatch, new Rectangle((int)p.X - 2, (int)p.Y - 2, width + 4, height + 4), Color.Black * 0.7f, true);
            float level = Overheated ? ventTime / 2 : heat / 60;
            int fill = (int)(height * MathHelper.Clamp(level, 0, 1));
            GUI.DrawRectangle(spriteBatch, new Rectangle((int)p.X, (int)p.Y + height - fill, width, fill), Color.Gold, true);
        }
#endif
    }

    public sealed class TsunamiHeatPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
            harmony = new Harmony("aic.tsunami.heat");
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "Use"),
                prefix: new HarmonyMethod(typeof(TsunamiHeatPlugin), nameof(CanFire)),
                postfix: new HarmonyMethod(typeof(TsunamiHeatPlugin), nameof(AfterFire)));
#if CLIENT
            harmony.Patch(AccessTools.Method(typeof(RangedWeapon), "UpdateHUDComponentSpecific"),
                postfix: new HarmonyMethod(typeof(TsunamiHeatPlugin), nameof(FixedCrosshairSize)));
#endif
        }
        private static bool CanFire(RangedWeapon __instance, ref bool __result)
        {
            if (__instance.Item.GetComponent<TsunamiCharge>()?.Overheated != true) return true;
            __instance.currentChargeTime = 0;
            __result = false;
            return false;
        }
        private static void AfterFire(RangedWeapon __instance, bool __result)
        {
            if (__result) __instance.Item.GetComponent<TsunamiCharge>()?.Fired();
        }
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
#if CLIENT
            var hint = AccessTools.TypeByName("JiuzhouHint.JiuzhouHint");
            if (hint != null)
                harmony.Patch(AccessTools.Method(hint, "DrawHint"),
                    prefix: new HarmonyMethod(typeof(TsunamiHeatPlugin), nameof(UseHeatDisplay)));
#endif
        }
#if CLIENT
        private static void FixedCrosshairSize(RangedWeapon __instance)
        {
            if (__instance.Item.GetComponent<TsunamiCharge>() == null) return;
            // Screen-space reticle: keep both the frame and pointer independent of camera zoom.
            float scale = __instance.CrossHairScale * GUI.Scale;
            __instance.currentCrossHairScale = scale;
            __instance.currentCrossHairPointerScale = scale;
        }

        private static bool UseHeatDisplay(RangedWeapon __0)
            => __0.Item.GetComponent<TsunamiCharge>() == null;
#endif
        public void Dispose() { harmony?.UnpatchSelf(); }
    }
}
