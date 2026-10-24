using Barotrauma;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // Capture once at emission. Crossing a hull boundary later must not change frames.
    internal readonly record struct EffectAnchor(Submarine Submarine, Vector2 InitialPosition)
    {
        internal static EffectAnchor At(Vector2 worldPosition)
        {
            var sub = Hull.FindHull(worldPosition, useWorldCoordinates: true)?.Submarine;
            return new EffectAnchor(sub, sub?.Position ?? Vector2.Zero);
        }
        internal Vector2 Offset => Submarine?.Removed == false ? Submarine.Position - InitialPosition : Vector2.Zero;
#if CLIENT
        internal Vector2 DrawOffset => Submarine?.Removed == false ? Submarine.DrawPosition - InitialPosition : Vector2.Zero;
#endif
    }
}
