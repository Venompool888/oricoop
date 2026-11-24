using System;
using System.Collections.Generic;

namespace OriCoop.World
{
    // Host only: full world snapshots still owed (WorldSync.Tick sends them). T-254: a guest's checkpoint load asked for
    // a resync (RestoreCheckpoint) AND announced its load (SaveSync LoadedMessage); the host sent one snapshot to that
    // seat on the load and a second, broadcast one on the next tick for the queued request. Now a guest's request is
    // owed to that seat only, and any snapshot actually sent pays off what it covers: a broadcast pays off everything,
    // a snapshot to one seat pays off that seat's request and its share of a queued broadcast. A seat still loading a
    // checkpoint is not sent a queued snapshot: its load announcement brings its own (SaveSync), which pays it off.
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

        // What to send now. broadcast = one snapshot to everyone (seats is then empty); otherwise one snapshot per
        // listed seat, ascending. remoteSeats = the seats in the session now (owed seats that left are dropped);
        // loaded(seat) = that seat plays the world (false while it loads a checkpoint: it stays owed, not sent).
        // What is returned counts as sent; whatever is still owed afterwards stays queued.
        public int[] Take(int[] remoteSeats, Func<int, bool> loaded, out bool broadcast)
        {
            broadcast = false;
            List<int> send = new List<int>(), waiting = new List<int>();
            foreach (int s in remoteSeats)
            {
                if (!Pending(s)) continue;
                if (loaded(s)) send.Add(s); else waiting.Add(s);
            }
            if (m_all && m_covered.Count == 0 && waiting.Count == 0 && send.Count == remoteSeats.Length && send.Count != 0)
            {
                broadcast = true; Clear();
                return new int[0];
            }
            send.Sort();
            Clear();
            foreach (int s in waiting) m_seats.Add(s);   // still owed, to that seat alone
            return send.ToArray();
        }

        public void Clear() { m_all = false; m_seats.Clear(); m_covered.Clear(); }
    }
}
