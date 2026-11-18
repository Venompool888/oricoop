using System;

namespace OriCoop.Rules
{
    public sealed class EdgePoint
    {
        public int Seat;
        public bool Local;
        public float X, Y, LastX, LastY;
        public EdgePoint(int seat, bool local, float x, float y, float lastX, float lastY)
        { Seat = seat; Local = local; X = x; Y = y; LastX = lastX; LastY = lastY; }
    }
    // Undo only movement that expands this device's local group, never drag a stationary Ori.
    public static class LocalEdgeWall
    {
        public static float OfflineWidth(float maxViewWidth, float edgeMargin, float leash)
        {
            float width = Math.Min(maxViewWidth, Math.Max(4f, maxViewWidth - 2f * edgeMargin));
            return leash > 0f ? Math.Min(width, leash) : width;
        }

        public static void Apply(EdgePoint[] points, float width, float height)
        {
            if (width <= 0 || height <= 0) return;
            // Pairwise projections converge without introducing new outward movement. Four seats max.
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < points.Length; i++)
                    for (int j = i + 1; j < points.Length; j++)
                    {
                        EdgePoint a = points[i], b = points[j];
                        if (!a.Local || !b.Local) continue;
                        Clamp(ref a.X, ref b.X, a.LastX, b.LastX, width);
                        Clamp(ref a.Y, ref b.Y, a.LastY, b.LastY, height);
                    }
        }
        static void Clamp(ref float a, ref float b, float lastA, float lastB, float max)
        {
            float sep = b - a, over = Math.Abs(sep) - max;
            if (over <= 0) return;
            float dir = Math.Sign(sep), outA = Math.Max(0, -(a - lastA) * dir), outB = Math.Max(0, (b - lastB) * dir);
            float total = outA + outB;
            if (total <= 0.0001f) return;
            float fix = Math.Min(over, total);
            a += fix * outA / total * dir; b -= fix * outB / total * dir;
        }
        public static bool Bounds(EdgePoint[] points, out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = minY = float.PositiveInfinity; maxX = maxY = float.NegativeInfinity;
            bool found = false;
            foreach (EdgePoint p in points)
            {
                if (!p.Local) continue;
                found = true; minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
            }
            return found;
        }
    }
}
