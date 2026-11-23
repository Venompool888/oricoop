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

        // T-197: may a copy that is not simulated here move to the last state it received? stateEpoch: the lease epoch
        // that state was published in; leaseEpoch: the lease now; ownsLease: the lease is this PC's own and it is in
        // the short wait before it simulates; simulatedEpoch: the epoch this PC last simulated the stone in as owner.
        // - Following another owner: only what that owner published in the current epoch. Anything older is where the
        //   stone was before this PC (or anyone) moved it since.
        // - Own lease, waiting: only the state handed over by the owner right before us (its epoch + 1 is ours), and
        //   only before we simulated in this epoch ourselves (after a session reset the wait happens again, and by
        //   then that state is old). After a release (owner 0 takes an epoch of its own) or an older owner, this PC's
        //   own copy is the newer one.
        public static bool FollowsState(uint stateEpoch, uint leaseEpoch, bool ownsLease, uint simulatedEpoch)
        {
            return ownsLease ? stateEpoch + 1 == leaseEpoch && simulatedEpoch != leaseEpoch : stateEpoch == leaseEpoch;
        }

        public bool Allows(int nextOwner, uint nextEpoch, int localSeat, float now, float rttMs, bool active)
        {
            if (active && !interacting && nextOwner != 0 && nextOwner != localSeat && localSeat != 0)
            {
                if (nextOwner != predictedOwner || nextEpoch != predictedEpoch || now >= predictUntil)
                {
                    predictedOwner = nextOwner; predictedEpoch = nextEpoch;
                    // T-023: the grant needs the request's trip plus the grant's (one RTT) and server/send delays.
                    predictUntil = now + Math.Max(0.02f, Math.Max(0f, rttMs) * 0.002f + 0.1f);
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
