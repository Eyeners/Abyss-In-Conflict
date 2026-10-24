using Barotrauma;
using Barotrauma.Items.Components;
namespace AIC.Components
{
    internal sealed class DisintegrationRefraction : ItemComponent
    {
        private float timer;
        public DisintegrationRefraction(Item item, ContentXElement element) : base(item, element)
        { IsActive = true; }
        public override void Update(float deltaTime, Camera cam)
        {
            if (Item.Condition >= 30) { timer = 0.5f; return; }
            timer -= deltaTime;
            if (timer > 0) return;
            timer = 1f;
#if CLIENT
            SonicRefraction.AddRing(Item.WorldPosition, 600f);
#endif
        }
    }
}
