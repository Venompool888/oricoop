using System;

namespace OriCoop.Authority
{
    internal sealed class StoneLeaseSimulation
    {
        bool observed, interacting, pending;
        int owner, predictedOwner;
        uint epoch, predictedEpoch;
        float readyAt, predictUntil;

        public bool Predicting { get; private set; }

        public static bool WantsStandingLease(bool active, int owner)
        {
            return !active && owner == 0;
        }

        public bool Allows(int nextOwner, uint nextEpoch, int localSeat, float now, float rttMs, bool active)
        {
            if (active && !interacting && nextOwner != 0 && nextOwner != localSeat && localSeat != 0)
            {
                if (nextOwner != predictedOwner || nextEpoch != predictedEpoch || now >= predictUntil)
                {
                    predictedOwner = nextOwner; predictedEpoch = nextEpoch;
                    predictUntil = now + Math.Max(0.02f, Math.Max(0f, rttMs) * 0.001f);
                }
                pending = true;
            }
            if (!active) pending = false;
            interacting = active;

            bool predicting = pending && now < predictUntil && nextOwner == predictedOwner && nextEpoch == predictedEpoch;
            if (!observed || owner != nextOwner || epoch != nextEpoch)
            {
                bool continuing = observed && (owner == 0 || owner == localSeat || (Predicting && active && now < predictUntil));
                observed = true; owner = nextOwner; epoch = nextEpoch;
                readyAt = nextOwner == localSeat && nextOwner != 0 && !continuing ? now + 0.05f : now;
                if (nextOwner != predictedOwner || nextEpoch != predictedEpoch) pending = false;
            }
            Predicting = predicting && pending;
            return owner == 0 || Predicting || (owner == localSeat && localSeat != 0 && now >= readyAt);
        }
    }
}
