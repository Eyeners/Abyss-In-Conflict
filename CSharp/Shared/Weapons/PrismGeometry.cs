using System;
using Microsoft.Xna.Framework;
namespace AIC.Components
{
    internal static class PrismGeometry
    {
        internal static Vector2 Branch(Vector2 incoming, Vector2 normal, bool surface, int index, int count, float random, float surfaceSpread = 70f, float surfaceAngleLimit = 80f)
        {
            if (!surface)
            {
                float angle = MathHelper.TwoPi * (index + random) / count;
                return new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            }
            Vector2 reflected = incoming - 2 * Vector2.Dot(incoming, normal) * normal;
            float normalAngle = (float)Math.Atan2(normal.Y, normal.X);
            float relative = MathHelper.WrapAngle((float)Math.Atan2(reflected.Y, reflected.X) - normalAngle);
            float spread = MathHelper.ToRadians(Math.Max(0, surfaceSpread));
            float limit = MathHelper.ToRadians(Math.Clamp(surfaceAngleLimit, 0, 90));
            float offset = spread * (2 * (index + random) / count - 1);
            float result = normalAngle + Math.Clamp(relative + offset, -limit, limit);
            return new Vector2((float)Math.Cos(result), (float)Math.Sin(result));
        }
    }

}
