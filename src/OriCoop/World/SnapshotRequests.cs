using System.Collections.Generic;

namespace OriCoop.World
{
    // Host only: full world snapshots still owed (WorldSync.Tick sends them). T-254: a guest's checkpoint load asked for
    // a resync (RestoreCheckpoint) AND announced its load (SaveSync LoadedMessage); the host sent one snapshot to that
    // seat on the load and a second, broadcast one on the next tick for the queued request. Now a guest's request is
    // owed to that seat only, and any snapshot actually sent pays off what it covers: a broadcast pays off everything,
    // a snapshot to one seat pays off that seat's request and its share of a queued broadcast.
    public sealed class SnapshotRequests
    {
        readonly HashSet<int> m_seats = new HashSet<int>();     // seats owed a snapshot of their own
        readonly HashSet<int> m_covered = new HashSet<int>();   // seats already sent one since the broadcast was queued
        bool m_all;

        public bool AllPending { get { return m_all; } }
        public bool Any { get { return m_all || m_seats.Count != 0; } }
        public bool Pending(int seat) { return m_seats.Contains(seat) || (m_all && !m_covered.Contains(seat)); }

        // seat 0 = every remote seat (the host's own checkpoint rollback); otherwise that guest only.
        public void Request(int seat)
        {
            if (seat != 0) { m_seats.Add(seat); return; }
            m_all = true; m_covered.Clear();
        }

        // A snapshot was sent to seat (0 = everyone).
        public void Sent(int seat)
        {
            if (seat == 0) { Clear(); return; }
            m_seats.Remove(seat);
            if (m_all) m_covered.Add(seat);
        }

        // What to send now, then cleared. broadcast = one snapshot to everyone (seats is then empty); otherwise one
        // snapshot per listed seat, ascending. remoteSeats = the seats in the session now (a queued broadcast that some
        // seats already got turns into per-seat sends to the rest).
        public int[] Take(int[] remoteSeats, out bool broadcast)
        {
            broadcast = false;
            List<int> seats = new List<int>(m_seats);
            if (m_all)
            {
                if (m_covered.Count == 0) { broadcast = true; seats.Clear(); }
                else foreach (int s in remoteSeats) if (!m_covered.Contains(s) && !seats.Contains(s)) seats.Add(s);
            }
            seats.Sort();
            Clear();
            return seats.ToArray();
        }

        public void Clear() { m_all = false; m_seats.Clear(); m_covered.Clear(); }
    }
}
