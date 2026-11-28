using System;

namespace OriCoop.Authority
{
    // #424: when the owner of a push stone publishes its state. Before, every owner sent 20 states a second for as long
    // as it held the lease, also for a stone lying still. Now:
    // - at most every Interval (20 a second), as before, while the stone moves, is pushed, its lease epoch is new, or
    //   it has moved in the last Settle seconds. States are not sent reliably, so the burst after the stone comes to
    //   rest makes sure the resting pose arrives even when a packet or two is lost;
    // - otherwise one heartbeat every Heartbeat seconds, so a partner that loads the scene, or missed a packet, still
    //   gets the current pose within half a second.
    // Movement is measured against the last pose sent, so a slow drift below the thresholds still goes out once it adds
    // up to PositionEpsilon.
    internal sealed class StoneSendPacer
    {
        public const float Interval = 0.05f, Heartbeat = 0.5f, Settle = 0.5f;
        public const float PositionEpsilon = 0.01f, RotationEpsilon = 0.2f, SpeedEpsilon = 0.02f, AngularEpsilon = 0.5f;

        bool sent, sentActive;
        uint sentEpoch;
        float sentAt = -10f, movedAt = -10f;
        float x, y, rotation;

        // Whether a state is due now. When it is, the pose counts as sent (the caller publishes it).
        public bool Take(float now, uint epoch, bool active, float px, float py, float rotZ, float vx, float vy, float angularZ)
        {
            if (sent && now - sentAt < Interval) return false;
            bool moving = Math.Abs(vx) > SpeedEpsilon || Math.Abs(vy) > SpeedEpsilon || Math.Abs(angularZ) > AngularEpsilon;
            bool moved = !sent || Math.Abs(px - x) > PositionEpsilon || Math.Abs(py - y) > PositionEpsilon
                || Math.Abs(AngleDelta(rotZ, rotation)) > RotationEpsilon;
            if (moving || moved) movedAt = now;
            bool due = !sent || epoch != sentEpoch || active || active != sentActive || moving || moved
                || now - movedAt < Settle || now - sentAt >= Heartbeat;
            if (!due) return false;
            sent = true; sentActive = active; sentEpoch = epoch; sentAt = now;
            x = px; y = py; rotation = rotZ;
            return true;
        }

        public void Reset()
        {
            sent = sentActive = false; sentEpoch = 0;
            sentAt = movedAt = -10f;
        }

        // Degrees from b to a, in -180..180 (eulerAngles.z wraps at 360).
        static float AngleDelta(float a, float b)
        {
            float d = (a - b) % 360f;
            if (d > 180f) d -= 360f; else if (d < -180f) d += 360f;
            return d;
        }
    }
}
