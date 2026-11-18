using System.Collections.Generic;

namespace OriCoop.Rules
{
    public sealed class SpectateState
    {
        int m_alive;
        public bool Active { get { return Target != 0; } }
        public int Target { get; private set; }

        // The first local seat is this device's own seat.
        public void Tick(IEnumerable<int> local, IEnumerable<int> present, IEnumerable<int> downed)
        {
            int localMask = 0, ownSeat = 0;
            foreach (int seat in local)
            {
                if (ownSeat == 0) ownSeat = seat;
                localMask |= 1 << seat;
            }
            int downMask = Mask(downed);
            m_alive = Mask(present) & ~downMask & ~localMask;
            if (localMask == 0 || (localMask & downMask) != localMask || m_alive == 0)
            {
                Target = 0;
                return;
            }
            if (Target == 0) Target = Find(ownSeat, 1);
            else if ((m_alive & (1 << Target)) == 0) Target = Find(Target, 1);
        }

        static int Mask(IEnumerable<int> seats)
        {
            int mask = 0;
            foreach (int seat in seats) mask |= 1 << seat;
            return mask;
        }

        int Find(int from, int direction)
        {
            for (int i = 0; i < ReviveLogic.MaxSeats; i++)
            {
                from = (from - 1 + direction + ReviveLogic.MaxSeats) % ReviveLogic.MaxSeats + 1;
                if ((m_alive & (1 << from)) != 0) return from;
            }
            return 0;
        }

        public void Next() { if (Active) Target = Find(Target, 1); }
        public void Prev() { if (Active) Target = Find(Target, -1); }
    }
}
