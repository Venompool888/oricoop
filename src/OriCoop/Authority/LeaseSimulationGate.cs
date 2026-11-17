using System;

namespace OriCoop.Authority
{
    internal sealed class LeaseSimulationGate
    {
        bool observed;
        int owner;
        uint epoch;
        float readyAt;

        public bool Allows(int nextOwner, uint nextEpoch, int localSeat, float now, float rttMs)
        {
            if (!observed || owner != nextOwner || epoch != nextEpoch)
            {
                observed = true; owner = nextOwner; epoch = nextEpoch;
                readyAt = nextOwner != 0 && nextOwner == localSeat ? now + Math.Max(0.25f, rttMs * 0.001f + 0.25f) : now;
            }
            return owner != 0 && owner == localSeat && now >= readyAt;
        }
    }
}
