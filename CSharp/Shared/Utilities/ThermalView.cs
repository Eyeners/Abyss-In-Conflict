using System;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    internal static class ThermalView
    {
        // Test the actual glow/limb bounds, never just its character's centre.
        internal static bool Intersects(Vector2 center, Vector2 half, Vector2 viewCenter, Vector2 viewHalf)
            => Math.Abs(center.X - viewCenter.X) <= half.X + viewHalf.X
                && Math.Abs(center.Y - viewCenter.Y) <= half.Y + viewHalf.Y;
    }
}
