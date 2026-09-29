using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using FarseerPhysics.Dynamics;

namespace AIC.Components
{
    class LaserTripwire : RepairTool
    {
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool Deployed { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool Tripped { get; set; }
        [Serialize(false, IsPropertySaveable.Yes)]
        public bool Disabled { get; set; }
        [Serialize(1f, IsPropertySaveable.No)]
        public float ArmingDelay { get; set; }
        private float armedAge;

        public LaserTripwire(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
        }

        public void Deploy()
        {
            if (Disabled || Item.Condition <= 0) return;
            Deployed = true;
            armedAge = 0;
            Sync(nameof(Deployed));
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (Item.Removed) return;
            if (Disabled || Item.Condition <= 0)
            {
                DisableTrap();
                return;
            }
            var light = Item.GetComponent<LightComponent>();
            if (light != null) light.IsOn = Deployed && !Tripped;
            if (!Deployed || Tripped || Item.ParentInventory != null) return;
            if (Item.body != null)
            {
                // Apply on clients too: both the beam light and the tool ray use body direction.
                Item.body.Dir = 1;
                Item.body.BodyType = BodyType.Static;
                Item.body.CollisionCategories = Physics.CollisionCharacter;
                Item.body.CollidesWith = Physics.CollisionProjectile;
            }
            armedAge += deltaTime;
            if (armedAge >= ArmingDelay && (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient))
                base.Use(deltaTime, null);
            base.Update(deltaTime, cam);
            IsActive = true;
        }

        public void Trip(Character character)
        {
            if (Disabled || Item.Condition <= 0 || !Deployed || Tripped || armedAge < ArmingDelay || character == null
                || character.Removed || character.IsDead || !character.Enabled || Item.Removed) return;
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) return;
            var payload = Item.OwnInventory?.GetItemAt(0);
            if (payload == null || payload.Removed) return;
            Tripped = true;
            Sync(nameof(Tripped));
            // Match the vanilla detonator: Use the contents, do not replace their explosion.
            payload.Use(1f, null);
        }

        public override void UpdateBroken(float deltaTime, Camera cam)
        {
            DisableTrap();
        }

        private void DisableTrap()
        {
            if (!Disabled)
            {
                Disabled = true;
                Sync(nameof(Disabled));
            }
            var light = Item.GetComponent<LightComponent>();
            if (light != null) light.IsOn = false;
            Item.NonInteractable = false;
            if (Item.body != null && Item.ParentInventory == null)
            {
                Item.body.BodyType = BodyType.Dynamic;
                Item.body.CollisionCategories = Physics.CollisionItem;
                Item.body.CollidesWith = Physics.DefaultItemCollidesWith;
            }
        }

        private void Sync(string name)
        {
#if SERVER
            if (SerializableProperties.TryGetValue(name.ToIdentifier(), out var property))
                GameMain.NetworkMember?.CreateEntityEvent(Item, new Item.ChangePropertyEventData(property, this));
#endif
        }
    }
}
