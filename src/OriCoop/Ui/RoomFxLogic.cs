using System;

namespace OriCoop.Ui
{
    // T-184: the room pages' animated effects (smoke at the ink banners' edges, sparks along the blue frames, a pulse
    // and a shimmer on the selected button), as pure maths without Unity so it can be unit tested. Every particle is
    // stateless: its place at time t comes from its index and a hash, so nothing is stored, allocated or simulated per
    // frame, and the same page looks the same at every resolution and frame rate.
    //
    // Coordinates: U across the target rect (0 = left edge, 1 = right edge), V down it (0 = top, 1 = bottom); a
    // particle may leave the rect (smoke drifts outwards). Size is a fraction of the rect's HEIGHT.
    public struct FxParticle
    {
        public float U, V, Size, Angle, Alpha;
    }

    public static class RoomFxLogic
    {
        // A stable pseudo-random number in [0, 1) for (a, b).
        public static float Hash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 374761393u + (uint)b * 668265263u + 0x9E3779B9u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        // An id's seed (so two banners do not move in step).
        public static int Seed(string id)
        {
            unchecked
            {
                int h = 17;
                if (id != null) foreach (char c in id) h = h * 31 + c;
                return h;
            }
        }

        // Where in its life [0, 1) a particle with this life (seconds) and offset is at time t.
        public static float Phase(float time, float life, float offset)
        {
            if (life <= 0f) return 0f;
            float p = time / life + offset;
            p -= (float)Math.Floor(p);
            return p < 0f ? 0f : p >= 1f ? 0f : p;
        }

        // 0 at birth and death, 1 in the middle (a smooth in-out).
        public static float Fade(float phase)
        {
            if (phase <= 0f || phase >= 1f) return 0f;
            return (float)Math.Sin(Math.PI * phase);
        }

        // Ink smoke at a banner's edges: puffs born just inside the left / right ends (most) or along the top / bottom
        // edge, drifting outwards and a little up, growing, turning, fading in and out. Drawn BEHIND the banner, so only
        // the part outside its ink shows.
        public const int SmokeCount = 14;
        public static FxParticle Smoke(int seed, int i, float time)
        {
            float h1 = Hash(seed, i * 7 + 1), h2 = Hash(seed, i * 7 + 2), h3 = Hash(seed, i * 7 + 3), h4 = Hash(seed, i * 7 + 4);
            float life = 4.5f + 3f * h1;
            float p = Phase(time, life, h2);
            FxParticle f = new FxParticle();
            float drift = 0.35f + 0.25f * h3;            // how far it travels in its life, in rect heights
            float rise = -0.25f - 0.2f * h4;             // smoke climbs a little
            if (i % 3 != 2)
            {
                // The ends: left for even, right for odd.
                float dir = i % 2 == 0 ? -1f : 1f;
                float u0 = i % 2 == 0 ? 0.06f + 0.06f * h3 : 0.94f - 0.06f * h3;
                f.U = u0 + dir * drift * p * 0.2f;        // U is in widths: banners are ~3x wider than high
                f.V = 0.4f + 0.2f * h4 + rise * p * 0.5f;
            }
            else
            {
                // Along the top or bottom edge, drifting away from it.
                bool top = h4 < 0.5f;
                f.U = 0.18f + 0.64f * h3 + (h1 - 0.5f) * 0.08f * p;
                f.V = (top ? 0.22f : 0.78f) + (top ? -1f : 1f) * drift * 0.3f * p;
            }
            f.Size = 0.45f + 0.55f * p + 0.25f * h1;
            f.Angle = h2 * 360f + (h3 - 0.5f) * 80f * p;
            f.Alpha = (float)Math.Pow(Fade(p), 1.4f);
            return f;
        }

        // Sparks gliding along a frame's top and bottom edges (inside its pointed ends), twinkling, with a few motes
        // rising off the top edge. selected = more and brighter.
        public static int SparkCount(bool selected) { return selected ? 14 : 6; }
        public static FxParticle Spark(int seed, int i, float time, bool selected)
        {
            float h1 = Hash(seed, i * 5 + 11), h2 = Hash(seed, i * 5 + 12), h3 = Hash(seed, i * 5 + 13), h4 = Hash(seed, i * 5 + 14);
            FxParticle f = new FxParticle();
            if (i % 4 == 3)
            {
                // A mote rising off the top edge.
                float life = 2.6f + 1.6f * h1;
                float p = Phase(time, life, h2);
                f.U = 0.1f + 0.8f * h3 + (float)Math.Sin((time + h4 * 10f) * 1.7f) * 0.004f;
                f.V = 0.12f - 0.9f * p;
                f.Size = 0.16f + 0.08f * h4;
                f.Alpha = Fade(p) * 0.8f;
            }
            else
            {
                float life = 2.2f + 2.2f * h1;
                float p = Phase(time, life, h2);
                bool top = i % 2 == 0;
                float dir = h4 < 0.5f ? 1f : -1f;
                // Keep it on the straight part of the edge [0.08, 0.92] without wrapping (a wrap jumped a lit spark
                // from one pointed end to the other): it starts where its 0.35 glide fits, moving away from that side.
                float start = dir > 0f ? 0.08f + 0.48f * h3 : 0.92f - 0.48f * h3;   // 0.48 + 0.35 < 0.84: margin for float error
                f.U = start + dir * 0.35f * p;
                f.V = top ? 0.1f : 0.9f;
                f.Size = (0.22f + 0.12f * h4) * (selected ? 1.2f : 1f);
                float twinkle = 0.65f + 0.35f * (float)Math.Sin(time * (5f + 4f * h1) + h2 * 20f);
                f.Alpha = Fade(p) * twinkle;
            }
            f.Angle = h1 * 360f + time * 40f * (h2 - 0.5f);
            if (!selected) f.Alpha *= 0.55f;
            return f;
        }

        // The selected button's slow breathing: 0..1, period about 2.2 s.
        public static float Pulse(float time)
        {
            return 0.5f + 0.5f * (float)Math.Sin(time * 2f * Math.PI / 2.2f);
        }

        // The shimmer streak across the selected button: its centre U (-0.2..1.2, then a rest) and strength 0..1.
        public const float ShimmerPeriod = 2.8f, ShimmerSweep = 1.1f;
        public static float ShimmerU(float time, out float strength)
        {
            float t = time % ShimmerPeriod;
            if (t < 0f) t += ShimmerPeriod;
            if (t >= ShimmerSweep) { strength = 0f; return -1f; }
            float p = t / ShimmerSweep;
            strength = Fade(p);
            return -0.2f + 1.4f * p;
        }

    }
}
