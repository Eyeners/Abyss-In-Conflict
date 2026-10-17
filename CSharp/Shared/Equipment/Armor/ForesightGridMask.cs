#if CLIENT
using System;
using Microsoft.Xna.Framework;

namespace AIC.Components
{
    // One texel per world-space grid cell. Reused buffers avoid per-cell objects,
    // rectangle merging and GPU readback. Rows run downwards, world Y runs upwards.
    internal sealed class ForesightGridMask
    {
        internal const int Size = 256;
        internal const int WorkBudget = 262144;
        internal readonly byte[] Cells = new byte[Size * Size];
        internal readonly Color[] Pixels = new Color[Size * Size];
        internal Vector2 Origin;
        internal int Width, Height;
        internal float CellSize;
        internal int Visits;
        private static readonly Color[] Colors = {
            Color.Transparent, new Color(235, 245, 255, 80),
            new Color(100, 190, 255, 90), new Color(255, 75, 75, 95)
        };

        internal void Reset(Vector2 origin, int width, int height, float cellSize)
        {
            Origin = origin; Width = Math.Clamp(width, 1, Size); Height = Math.Clamp(height, 1, Size);
            CellSize = cellSize; Visits = 0;
            Array.Clear(Cells, 0, Cells.Length);
        }

        internal Rectangle Bounds(Vector2 center, Vector2 half, float cosine, float sine)
        {
            float x = Math.Abs(cosine) * half.X + Math.Abs(sine) * half.Y;
            float y = Math.Abs(sine) * half.X + Math.Abs(cosine) * half.Y;
            int left = Math.Clamp((int)Math.Floor((center.X - x - Origin.X) / CellSize), 0, Width);
            int right = Math.Clamp((int)Math.Ceiling((center.X + x - Origin.X) / CellSize), 0, Width);
            int top = Math.Clamp((int)Math.Floor((Origin.Y - center.Y - y) / CellSize), 0, Height);
            int bottom = Math.Clamp((int)Math.Ceiling((Origin.Y - center.Y + y) / CellSize), 0, Height);
            return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
        }

        internal void AddBox(Vector2 center, Vector2 half, float cosine, float sine, byte relation)
        {
            Rectangle bounds = Bounds(center, half, cosine, sine);
            float padding = CellSize * 0.5f * (Math.Abs(cosine) + Math.Abs(sine));
            for (int y = bounds.Top; y < bounds.Bottom; y++)
            {
                float dy = Origin.Y - (y + 0.5f) * CellSize - center.Y;
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    Visits++;
                    int index = y * Size + x;
                    if (Cells[index] >= relation) continue;
                    float dx = Origin.X + (x + 0.5f) * CellSize - center.X;
                    if (Math.Abs(cosine * dx + sine * dy) <= half.X + padding
                        && Math.Abs(-sine * dx + cosine * dy) <= half.Y + padding)
                        Cells[index] = relation;
                }
            }
        }

        internal void Colorize()
        {
            Array.Clear(Pixels, 0, Pixels.Length);
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    int index = y * Size + x;
                    Pixels[index] = Colors[Cells[index]];
                }
        }
    }
}
#endif
