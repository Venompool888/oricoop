using System;

namespace OriCoop.Rules
{
    // Online co-op camera (T-058, GitHub #33): each device's camera follows its own player, and teammates near that
    // player pull the view toward them. Pure math, no Unity, so it is unit-tested in src\Tests\Camera.
    //
    // The technique is the "weighted multi-target camera" used by co-op platformers and Cinemachine's TargetGroup:
    //   1. Each teammate gets an influence weight from its distance to the local player: full weight inside an inner
    //      radius, a smoothstep falloff to exactly 0 at the outer radius (C1-continuous, so walking across the radius
    //      never makes the picture jump). Distance is elliptical (screens are wider than tall).
    //   2. Goal = weighted average of the local player (weight 1, always the largest) and the teammates.
    //   3. The goal offset from the local player is clamped to a safe box, so the local player always stays well
    //      inside the picture however strongly teammates pull.
    //   4. A dead zone ignores tiny goal changes (network jitter); a critically damped spring with a speed cap moves
    //      the actual offset toward the goal, so entering/leaving the radius eases in and out.
    //   5. Bounded zoom-out: enough to also show the influencing teammates (blended by their weight, so it is
    //      continuous too), never more than MaxZoom.
    // The result is an offset relative to the local player (not an absolute point), so the local player's own motion
    // is followed exactly as in single player; only the teammates' pull is smoothed.
    public struct CamVec
    {
        public float X, Y;
        public CamVec(float x, float y) { X = x; Y = y; }
        public static CamVec operator +(CamVec a, CamVec b) { return new CamVec(a.X + b.X, a.Y + b.Y); }
        public static CamVec operator -(CamVec a, CamVec b) { return new CamVec(a.X - b.X, a.Y - b.Y); }
        public static CamVec operator *(CamVec a, float k) { return new CamVec(a.X * k, a.Y * k); }
        public float Length { get { return (float)Math.Sqrt(X * X + Y * Y); } }
        public override string ToString() { return "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ")"; }
    }

    public sealed class InfluenceCameraSettings
    {
        public bool Enabled = true;
        public float Radius = 20f;          // outer influence radius (world units, horizontal); weight 0 beyond
        public float InnerFraction = 0.35f; // full weight inside Radius * InnerFraction
        public float VerticalScale = 0.6f;  // vertical radius = Radius * this (screen aspect)
        public float TeammateWeight = 0.8f; // a teammate's weight at full influence (local player = 1; clamped to <= 1)
        public float SafeFraction = 0.55f;  // local player stays within this fraction of the half-view from centre
        public float MaxZoom = 1.25f;       // max camera distance multiple caused by teammates (1 = no zoom)
        public float EdgeMargin = 2.5f;     // world units kept around influencing teammates when sizing the zoom
        public float SmoothTime = 0.45f;    // spring time of the offset (s)
        public float MaxSpeed = 30f;        // offset speed cap (world units / s)
        public float DeadZone = 0.3f;       // goal changes smaller than this are ignored (world units)
    }

    public sealed class InfluenceCamera
    {
        public InfluenceCameraSettings S;
        CamVec m_offset, m_vel, m_goal;
        float m_zoom = 1f, m_zoomVel;
        bool m_haveGoal;

        public InfluenceCamera(InfluenceCameraSettings s) { S = s ?? new InfluenceCameraSettings(); }

        public CamVec Offset { get { return m_offset; } }   // camera target = local player + Offset
        public CamVec Goal { get { return m_goal; } }
        public float Zoom { get { return m_zoom; } }        // camera distance multiple (>= 1)

        public void Reset() { m_offset = m_vel = m_goal = new CamVec(); m_zoom = 1f; m_zoomVel = 0f; m_haveGoal = false; }

        // Smooth 1 -> 0 falloff: 1 for r <= inner, 0 for r >= 1, smoothstep in between (zero slope at both ends).
        public static float Falloff(float r, float inner)
        {
            if (r >= 1f) return 0f;
            if (r <= inner) return 1f;
            float t = (r - inner) / Math.Max(1e-4f, 1f - inner);
            return 1f - t * t * (3f - 2f * t);
        }

        // Normalised elliptical distance of a teammate from the local player (1 = on the influence radius).
        public float NormDistance(CamVec local, CamVec mate)
        {
            float rx = Math.Max(0.01f, S.Radius), ry = Math.Max(0.01f, S.Radius * S.VerticalScale);
            float dx = (mate.X - local.X) / rx, dy = (mate.Y - local.Y) / ry;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public float Weight(CamVec local, CamVec mate)
        {
            if (!S.Enabled) return 0f;
            return Clamp(S.TeammateWeight, 0f, 1f) * Falloff(NormDistance(local, mate), Clamp(S.InnerFraction, 0f, 0.95f));
        }

        // Safe box half-size (offset limit) for a view of the given half size at normal zoom.
        public CamVec SafeHalf(CamVec viewHalf)
        {
            float f = Clamp(S.SafeFraction, 0f, 0.95f);
            return new CamVec(viewHalf.X * f, viewHalf.Y * f);
        }

        // The instantaneous goal offset (before dead zone and smoothing), clamped to the safe box.
        public CamVec GoalOffset(CamVec local, CamVec[] mates, CamVec viewHalf)
        {
            float wsum = 1f; CamVec acc = new CamVec();
            if (mates != null)
                foreach (CamVec m in mates)
                {
                    float w = Weight(local, m);
                    if (w <= 0f) continue;
                    acc = acc + (m - local) * w; wsum += w;
                }
            CamVec g = acc * (1f / wsum);
            CamVec safe = SafeHalf(viewHalf);
            return new CamVec(Clamp(g.X, -safe.X, safe.X), Clamp(g.Y, -safe.Y, safe.Y));
        }

        // Zoom multiple needed so influencing teammates also fit (each blended by its falloff), capped at MaxZoom.
        public float GoalZoom(CamVec local, CamVec[] mates, CamVec goalOffset, CamVec viewHalf)
        {
            float max = Math.Max(1f, S.MaxZoom);
            if (!S.Enabled || mates == null || max <= 1f) return 1f;
            CamVec centre = local + goalOffset;
            float need = 1f;
            foreach (CamVec m in mates)
            {
                float f = Weight(local, m) / Math.Max(1e-4f, Clamp(S.TeammateWeight, 0f, 1f));
                if (f <= 0f) continue;
                float zx = (Math.Abs(m.X - centre.X) + S.EdgeMargin) / Math.Max(0.01f, viewHalf.X);
                float zy = (Math.Abs(m.Y - centre.Y) + S.EdgeMargin) / Math.Max(0.01f, viewHalf.Y);
                float z = 1f + (Math.Max(1f, Math.Max(zx, zy)) - 1f) * f;
                if (z > need) need = z;
            }
            return Math.Min(need, max);
        }

        // One frame. local/mates in world units, viewHalf = half the visible size at normal zoom, dt in seconds.
        public void Step(CamVec local, CamVec[] mates, CamVec viewHalf, float dt)
        {
            if (dt <= 0f) return;
            CamVec g = GoalOffset(local, mates, viewHalf);
            if (!m_haveGoal || (g - m_goal).Length > S.DeadZone || IsZero(g)) { m_goal = g; m_haveGoal = true; }
            float t = Math.Max(0.01f, S.SmoothTime);
            m_offset = new CamVec(SmoothDamp(m_offset.X, m_goal.X, ref m_vel.X, t, dt), SmoothDamp(m_offset.Y, m_goal.Y, ref m_vel.Y, t, dt));
            // Hard guarantee: the smoothed offset never leaves the safe box either.
            CamVec safe = SafeHalf(viewHalf);
            m_offset = new CamVec(Clamp(m_offset.X, -safe.X, safe.X), Clamp(m_offset.Y, -safe.Y, safe.Y));
            float zGoal = GoalZoom(local, mates, m_goal, viewHalf);
            m_zoom = SmoothDamp(m_zoom, zGoal, ref m_zoomVel, t * 1.5f, dt);
            if (m_zoom < 1f) { m_zoom = 1f; m_zoomVel = 0f; }
            float maxZ = Math.Max(1f, S.MaxZoom);
            if (m_zoom > maxZ) { m_zoom = maxZ; m_zoomVel = 0f; }
        }

        static bool IsZero(CamVec v) { return v.X == 0f && v.Y == 0f; }

        // Critically damped spring (same formula as Unity's Mathf.SmoothDamp), with the speed cap of the settings.
        float SmoothDamp(float cur, float target, ref float vel, float smoothTime, float dt)
        {
            float omega = 2f / smoothTime, x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = cur - target, orig = target;
            float maxChange = Math.Max(0.0001f, S.MaxSpeed) * smoothTime;
            change = Clamp(change, -maxChange, maxChange);
            target = cur - change;
            float temp = (vel + omega * change) * dt;
            vel = (vel - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            if ((orig - cur > 0f) == (output > orig)) { output = orig; vel = (output - orig) / dt; }
            return output;
        }

        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
