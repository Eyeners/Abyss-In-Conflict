using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal sealed class SpaceBackpackDamage : ItemComponent, IServerSerializable
    {
        private readonly BackpackHitbox hitbox = new BackpackHitbox();
        private float ventTime;
        [Serialize(false, IsPropertySaveable.Yes)] public bool VentTriggered { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)] public bool ExplosionTriggered { get; set; }
        internal bool Failing => ventTime > 0;
        internal static bool IsFailing(Character user) => user?.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag)
            ?.GetComponent<SpaceBackpackDamage>()?.Failing == true;

        public SpaceBackpackDamage(Item item, ContentXElement element) : base(item, element)
        { IsActive = true; }

        internal int CheckThresholds(float condition)
        {
            int result = 0;
            if (!VentTriggered && condition <= 60)
            { VentTriggered = true; ventTime = 4; result |= 1; }
            if (!ExplosionTriggered && condition <= 10)
            { ExplosionTriggered = true; result |= 2; }
            return result;
        }

        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);
        public override void Update(float deltaTime, Camera cam)
        {
            var user = Item.ParentInventory?.Owner as Character;
            bool worn = user != null && !user.Removed
                && user.GetEquippedItem("aic_space_propulsion_backpack", InvSlotType.Bag) == Item;
            var torso = worn ? user.AnimController?.GetLimb(LimbType.Torso) : null;
            hitbox.Update(Item, user);

            bool authority = GameMain.NetworkMember?.IsClient != true;
            if (authority && !Item.Removed && Entity.Spawner != null)
            {
                // Latch before spawning: the blast must not recursively trigger itself.
                int triggered = CheckThresholds(Item.ConditionPercentage);
                if ((triggered & 2) != 0)
                {
                    var prefab = ItemPrefab.Find(null, "aic_space_backpack_failure_blast".ToIdentifier());
                    if (prefab != null)
                        Entity.Spawner.AddItemToSpawnQueue(prefab, Item.WorldPosition);
                }
                if (triggered != 0) Sync();
            }
            if (ventTime <= 0) return;
            ventTime = Math.Max(0, ventTime - deltaTime);
            if (worn && !user.IsDead && torso?.body != null)
            {
                float phase = (4 - ventTime) * 5;
                float angle = torso.Rotation + (user.AnimController.Dir < 0 ? MathHelper.Pi : 0)
                    + (float)Math.Sin(phase) * 1.1f;
                Vector2 thrust = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 19.64f;
                if (authority)
                {
                    var boots = MagneticBoots.Get(user);
                    if (boots != null) { boots.WallWalk.Reset(); boots.Release(); }
                    var collider = user.AnimController.Collider;
                    if (collider != null) collider.ApplyForce(thrust * collider.Mass);
                    foreach (var limb in user.AnimController.Limbs)
                        if (!limb.Removed && !limb.IsSevered && limb.body != null && limb.body != collider)
                            limb.body.ApplyForce(thrust * limb.body.Mass);
                    var id = "aic_ground_adhesion_release".ToIdentifier();
                    var existing = user.CharacterHealth.GetAffliction(id);
                    if (existing != null) existing.Strength = Math.Max(existing.Strength, 0.3f);
                    else if (AfflictionPrefab.Prefabs.TryGet(id, out var prefab))
                        user.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(0.3f));
                }
#if CLIENT
                particleTimer -= deltaTime;
                if (particleTimer <= 0)
                {
                    particleTimer = 0.05f;
                    GameMain.ParticleManager?.CreateParticle("steamspray", torso.WorldPosition,
                        -Vector2.Normalize(thrust) * 220, 0, user.CurrentHull);
                }
#endif
            }
            if (authority && ventTime <= 0) Sync();
        }

        protected override void RemoveComponentSpecific() { hitbox.Release(); }
        private void Sync()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }
#if CLIENT
        private float particleTimer;
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        { ventTime = msg.ReadSingle(); VentTriggered = msg.ReadBoolean(); ExplosionTriggered = msg.ReadBoolean(); }
#endif
#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client client, NetEntityEvent.IData extraData = null)
        { msg.WriteSingle(ventTime); msg.WriteBoolean(VentTriggered); msg.WriteBoolean(ExplosionTriggered); }
#endif
    }

}
