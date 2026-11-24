namespace OriCoop.Authority
{
    internal sealed class RoomOwnerNotice
    {
        const double SettleMs = 500;
        int settledOwner, candidate;
        double settleAt;

        public int TakePreviousOwner(int owner, bool localSeatOnline, double nowMs)
        {
            // A token rejoin can receive an offline roster before its own seat comes online.
            if (settledOwner == 0 && !localSeatOnline) return 0;
            if (candidate != owner) { candidate = owner; settleAt = nowMs + SettleMs; }
            if (owner == 0 || owner == settledOwner || nowMs < settleAt) return 0;
            int previous = settledOwner;
            settledOwner = owner;
            return previous;
        }
    }
}
