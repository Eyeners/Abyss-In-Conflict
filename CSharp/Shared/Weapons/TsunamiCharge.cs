using Barotrauma;
using Barotrauma.Items.Components;
#if CLIENT
using System;
using System.Collections.Generic;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace AIC.Components
{
    internal sealed partial class TsunamiCharge : ItemComponent
    {
        public TsunamiCharge(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
#if CLIENT
            Visible.Add(this);
#endif
        }
        protected override void RemoveComponentSpecific()
        {
#if CLIENT
            Visible.Remove(this);
            sparks.Clear();
            coreTexture?.Dispose();
            coreTexture = null;
#endif
            base.RemoveComponentSpecific();
        }
        public override void Update(float deltaTime, Camera cam)
        {
            UpdateHeat(deltaTime);
            var weapon = Item.GetComponent<RangedWeapon>();
            var user = Item.ParentInventory?.Owner as Character;
            // SelectedItem is an interacted-with entity, not the weapon in a hand slot.
            bool held = user != null && user.HasEquippedItem(Item, predicate: CharacterInventory.IsHandSlotType);
            // Native RangedWeapon owns charging, cancellation and firing (including AI).
            // Rendering must not write input-dependent state back into the weapon.
            bool active = held && !user.IsDead && !user.IsIncapacitated && Item.Condition > 0;
#if CLIENT
            bool charging = active && weapon != null && weapon.currentChargeTime > 0;
            if (charging)
            {
                coreProgress = MathHelper.Clamp(weapon.currentChargeTime / Math.Max(0.01f, weapon.MaxChargeTime), 0, 1);
                coreOpacity = Math.Min(1, coreOpacity + deltaTime * 12);
            }
            else coreOpacity = Math.Max(0, coreOpacity - deltaTime * 8);
            UpdateSparks(deltaTime, charging);
#endif
        }
#if CLIENT
        internal static readonly List<TsunamiCharge> Visible = new();
        internal bool HasSparks => !Item.Removed && (sparks.Count > 0 || coreOpacity > 0);
        private Texture2D coreTexture;
        private float coreProgress, coreOpacity;
        private sealed class Spark
        {
            internal Vector2 Start, Tangent, End, EndTangent;
            internal EffectAnchor Anchor;
            internal float Age, Duration, CancelAge;
            internal bool Cancelled;
        }
        private readonly List<Spark> sparks = new();
        private float timer;
        private Vector2 Muzzle()
        {
            var weapon = Item.GetComponent<RangedWeapon>();
            return ConvertUnits.ToDisplayUnits(weapon.TransformedBarrelPos + Item.body.SimPosition)
                + (Item.Submarine?.Position ?? Vector2.Zero);
        }
        private Vector2 Direction()
        {
            float a = Item.body.Rotation + (Item.body.Dir < 0 ? MathHelper.Pi : 0);
            return new Vector2((float)Math.Cos(a), (float)Math.Sin(a));
        }
        private void UpdateSparks(float dt, bool charging)
        {
            if (Item.body == null) { sparks.Clear(); coreOpacity = 0; return; }
            Vector2 end = Muzzle(), forward = Direction();
            for (int i = sparks.Count - 1; i >= 0; i--)
            {
                var s = sparks[i];
                s.Age += dt;
                if (!charging && !s.Cancelled)
                {
                    s.Cancelled = true;
                    s.End += new Vector2(Rand.Range(-65f, 65f), Rand.Range(-65f, 65f));
                }
                if (s.Cancelled) s.CancelAge += dt;
                else { s.End = end - s.Anchor.Offset; s.EndTangent = -forward * 45; }
                if (s.Age > s.Duration + 0.2f || s.CancelAge > 0.25f) sparks.RemoveAt(i);
            }
            if (!charging) { timer = 0; return; }
            timer -= dt;
            if (timer > 0 || sparks.Count >= 32) return;
            timer = 0.035f;
            float side = Rand.Range(0, 2) == 0 ? -1 : 1;
            float angle = (float)Math.Atan2(forward.Y, forward.X) + side * Rand.Range(0.52f, 2.62f);
            Vector2 radial = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * Rand.Range(50f, 150f);
            sparks.Add(new Spark { Start = end + radial, End = end, Anchor = EffectAnchor.At(end),
                Tangent = new Vector2(-radial.Y, radial.X) * -side * Rand.Range(1f, 2f),
                EndTangent = -forward * 45, Duration = Rand.Range(0.32f, 0.47f) });
        }
        internal void Draw(SpriteBatch batch, Camera camera)
        {
            foreach (var s in sparks)
            {
                float head = MathHelper.Clamp(s.Age / s.Duration, 0, 1);
                float tail = MathHelper.Clamp((s.Age - 0.2f) / s.Duration, 0, 1);
                float fade = (1 - MathHelper.Clamp((s.Age - s.Duration) / 0.2f, 0, 1))
                    * (1 - MathHelper.Clamp(s.CancelAge / 0.25f, 0, 1));
                Vector2 Point(float t)
                {
                    float t2 = t * t, t3 = t2 * t;
                    Vector2 p = (2*t3-3*t2+1)*s.Start + (t3-2*t2+t)*s.Tangent
                        + (-2*t3+3*t2)*s.End + (t3-t2)*s.EndTangent + s.Anchor.DrawOffset;
                    return camera.WorldToScreen(p);
                }
                Vector2 prev = Point(tail);
                for (int j = 1; j <= 16; j++)
                {
                    float f = j / 16f;
                    Vector2 next = Point(MathHelper.Lerp(tail, head, f));
                    float strength = fade * f;
                    GUI.DrawLine(batch, prev, next, new Color(0, 65, 190, 255) * (strength * 0.35f), width: (7*f+1) * camera.Zoom);
                    GUI.DrawLine(batch, prev, next, new Color(30, 155, 255, 255) * strength, width: (2.8f*f+0.5f) * camera.Zoom);
                    prev = next;
                }
            }
            DrawCore(batch, camera);
        }
        private void DrawCore(SpriteBatch batch, Camera camera)
        {
            if (coreOpacity <= 0 || Item.body == null) return;
            // A small reusable radial gradient, generated once, with no particle allocations.
            if (coreTexture == null)
            {
                const int size = 64;
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float r = new Vector2(x - 31.5f, y - 31.5f).Length() / 31.5f;
                        float a = MathHelper.Clamp(1 - r * r, 0, 1);
                        pixels[y * size + x] = new Color(255, 255, 255, (int)(255 * a * a));
                    }
                coreTexture = new Texture2D(batch.GraphicsDevice, size, size);
                coreTexture.SetData(pixels);
            }
            Vector2 world = Muzzle();
            if (Item.Submarine != null) world += Item.Submarine.DrawPosition - Item.Submarine.Position;
            Vector2 centre = camera.WorldToScreen(world);
            float growth = coreProgress * coreProgress * (3 - 2 * coreProgress);
            float radius = MathHelper.Lerp(5, 17, growth);
            float pulse = 1 + 0.035f * (float)Math.Sin(Timing.TotalTime * 31);
            void Layer(float extent, Color tint, float opacity)
            {
                batch.Draw(coreTexture, centre, null, tint * (coreOpacity * opacity), 0,
                    new Vector2(32), extent * 2 * camera.Zoom / 64, SpriteEffects.None, 0);
            }
            Layer(radius * 1.8f * pulse, new Color(15, 65, 255), 0.48f);
            Layer(radius * pulse, new Color(40, 175, 255), 0.9f);
            Layer(radius * 0.53f, new Color(225, 250, 255), 1);
            Layer(radius * 0.3f, Color.White, 1);
        }
#endif
    }
}
