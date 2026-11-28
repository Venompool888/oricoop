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
    // up to PositionEpsilon. SpeedEpsilon sits above the small residual velocity the solver can leave on a resting body
    // (2026-11-28: a full net run sent 19 states in 2 s for a stone at rest, the likely cause); a real start shows in
    // the position within a frame or two anyway. Why counts the reason for each state sent, for the in-game check.
    internal sealed class StoneSendPacer
    {
        public const float Interval = 0.05f, Heartbeat = 0.5f, Settle = 0.5f;
        public const float PositionEpsilon = 0.01f, RotationEpsilon = 0.2f, SpeedEpsilon = 0.1f, AngularEpsilon = 0.5f;

        bool sent, sentActive;
        uint sentEpoch;
        float sentAt = -10f, movedAt = -10f;
        float x, y, rotation;
        // Why each state went out (first reason that applies), and the largest speeds seen, since ResetStats.
        public int First, NewEpoch, Active, Moving, Moved, Settling, Heartbeats;
        public float MaxSpeed, MaxAngular, MaxStep;

        // Whether a state is due now. When it is, the pose counts as sent (the caller publishes it).
        public bool Take(float now, uint epoch, bool active, float px, float py, float rotZ, float vx, float vy, float angularZ)
        {
            if (sent && now - sentAt < Interval) return false;
            bool moving = Math.Abs(vx) > SpeedEpsilon || Math.Abs(vy) > SpeedEpsilon || Math.Abs(angularZ) > AngularEpsilon;
            float step = Math.Max(Math.Abs(px - x), Math.Abs(py - y));
            bool moved = !sent || step > PositionEpsilon || Math.Abs(AngleDelta(rotZ, rotation)) > RotationEpsilon;
            if (moving || moved) movedAt = now;
            MaxSpeed = Math.Max(MaxSpeed, Math.Max(Math.Abs(vx), Math.Abs(vy))); MaxAngular = Math.Max(MaxAngular, Math.Abs(angularZ));
            if (sent) MaxStep = Math.Max(MaxStep, step);
            if (!sent) First++;
            else if (epoch != sentEpoch) NewEpoch++;
            else if (active || active != sentActive) Active++;
            else if (moving) Moving++;
            else if (moved) Moved++;
            else if (now - movedAt < Settle) Settling++;
            else if (now - sentAt >= Heartbeat) Heartbeats++;
            else return false;
            sent = true; sentActive = active; sentEpoch = epoch; sentAt = now;
            x = px; y = py; rotation = rotZ;
            return true;
        }

        public void Reset()
        {
            sent = sentActive = false; sentEpoch = 0;
            sentAt = movedAt = -10f;
        }

        public void ResetStats()
        {
            First = NewEpoch = Active = Moving = Moved = Settling = Heartbeats = 0;
            MaxSpeed = MaxAngular = MaxStep = 0f;
        }

        public string Why()
        {
            return "first " + First + ", epoch " + NewEpoch + ", active " + Active + ", moving " + Moving + ", moved " + Moved
                + ", settling " + Settling + ", heartbeat " + Heartbeats + "; max speed " + MaxSpeed.ToString("0.000")
                + ", max angular " + MaxAngular.ToString("0.000") + ", max step " + MaxStep.ToString("0.0000");
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
