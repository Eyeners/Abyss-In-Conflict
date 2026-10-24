using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    // Visual only: no fixture, projectile interception, or world ray interaction.
    class NanoShield : ItemComponent, IServerSerializable
    {
        [Serialize(110f, IsPropertySaveable.No)] public float Radius { get; set; }
        [Serialize(0.25f, IsPropertySaveable.No)] public float FlashDuration { get; set; }
        [Serialize("0,-25", IsPropertySaveable.No)] public Vector2 Offset { get; set; }
        internal static readonly HashSet<NanoShield> Live = new();
        private float flash, eventDelay;
        private bool pendingEvent;
#if CLIENT
        private readonly Sprite sprite;
#endif
        public NanoShield(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = false;
#if CLIENT
            var child = element.GetChildElement("sprite");
            if (child != null) sprite = new Sprite(child);
#endif
        }
        internal void HitFlash()
        {
            flash = Math.Max(0.01f, FlashDuration);
            IsActive = true; Live.Add(this);
#if SERVER
            pendingEvent = true;
            if (eventDelay <= 0) SendFlash();
#endif
        }
        public override void UpdateBroken(float dt, Camera cam) => Update(dt, cam);
        public override void Update(float dt, Camera cam)
        {
            flash = Math.Max(0, flash-dt); eventDelay -= dt;
#if SERVER
            if (pendingEvent && eventDelay <= 0) SendFlash();
#endif
            if (flash <= 0 && !pendingEvent) { IsActive = false; Live.Remove(this); }
        }
#if SERVER
        private void SendFlash() { pendingEvent = false; eventDelay = 0.05f; Item.CreateServerEvent(this); }
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
            => msg.WriteBoolean(true);
#endif
#if CLIENT
        public void ClientEventRead(IReadMessage msg, float sendingTime) { if (msg.ReadBoolean()) HitFlash(); }
        internal void Draw(SpriteBatch batch, Camera camera)
        {
            var wearer = Item.ParentInventory?.Owner as Character;
            if (flash <= 0 || sprite == null || wearer == null || wearer.Removed
                || wearer.Inventory?.GetItemInLimbSlot(InvSlotType.OuterClothes) != Item) return;
            var torso = wearer.AnimController.GetLimb(LimbType.Torso);
            if (torso == null || torso.Removed) return;
            var offset = Vector2.Transform(new Vector2(Offset.X*wearer.AnimController.Dir, Offset.Y),
                Matrix.CreateRotationZ(torso.Rotation));
            var center = camera.WorldToScreen(torso.WorldPosition+offset);
            float radius = Math.Max(1, Radius)*camera.Zoom;
            batch.Draw(sprite.Texture, new Rectangle((int)(center.X-radius), (int)(center.Y-radius),
                (int)(radius*2), (int)(radius*2)), sprite.SourceRect,
                new Color(220,224,224,210)*MathHelper.Clamp(flash/Math.Max(0.01f,FlashDuration),0,1));
        }
#endif
        protected override void RemoveComponentSpecific()
        {
            Live.Remove(this);
#if CLIENT
            sprite?.Remove();
#endif
            base.RemoveComponentSpecific();
        }
    }
    public sealed class NanoShieldSystem : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize()
        {
#if CLIENT
            harmony = new Harmony("aic.nano.shield.visual");
            harmony.Patch(AccessTools.Method(typeof(GameScreen), "Draw"),
                postfix:new HarmonyMethod(typeof(NanoShieldSystem), nameof(Draw)));
#endif
        }
#if CLIENT
        private static void Draw(SpriteBatch __2)
        {
            if (NanoShield.Live.Count == 0) return;
            __2.Begin();
            try { foreach (var shield in NanoShield.Live) shield.Draw(__2,GameMain.GameScreen.Cam); }
            finally { __2.End(); }
        }
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted() { }
        public void Dispose() { harmony?.UnpatchSelf(); NanoShield.Live.Clear(); }
    }
}
