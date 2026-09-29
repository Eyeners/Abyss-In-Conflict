using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics.Dynamics;

namespace AIC.Components
{
    // Reuse native beam hit tests and status effects, without a held weapon or a dummy character.
    class LaserGridEmitter : RepairTool
    {
        [Serialize(20f, IsPropertySaveable.No)]
        public float Lifetime { get; set; }
        private float age;
        private bool expired;
        private bool collisionConfigured;

        public LaserGridEmitter(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (expired || Item.Removed) return;
            if (!collisionConfigured && Item.body != null)
            {
                // XML collision categories do not accept the enum value None.
                // Keep the body enabled for sprite rendering, but exclude it from raycasts.
                Item.body.CollisionCategories = Category.None;
                Item.body.CollidesWith = Category.None;
                collisionConfigured = true;
            }
            float step = System.Math.Min(deltaTime, System.Math.Max(0f, Lifetime - age));
            age += deltaTime;
            if (step > 0 && (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient))
            {
                // No Fixable structure entries: this trap damages characters, not submarine walls.
                base.Use(step, null);
            }
            base.Update(deltaTime, cam);
            IsActive = true;
            if (age < Lifetime) return;
            expired = true;
            IsActive = false;
            var light = Item.GetComponent<LightComponent>();
            if (light != null) light.IsOn = false;
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)
                Entity.Spawner?.AddItemToRemoveQueue(Item);
        }
    }
}
