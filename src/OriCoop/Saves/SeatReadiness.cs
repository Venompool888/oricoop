using System.Collections.Generic;

namespace OriCoop.Saves
{
    public sealed class SeatReadiness
    {
        sealed class GuestState { public bool Sent, Ready; public float SentAt; }
        readonly Dictionary<int, GuestState> m_guests = new Dictionary<int, GuestState>();

        public IEnumerable<int> Seats { get { return m_guests.Keys; } }
        public void Rebind(int previous, int primary)
        {
            if (previous == primary) return;
            GuestState device;
            if (!m_guests.TryGetValue(previous, out device)) return;
            m_guests.Remove(previous); m_guests[primary] = device;
        }
        // (A waiting mark is a guest's note about a server session, where seats are not renumbered: nothing to rebind.)
        public void Join(int seat) { m_guests[seat] = new GuestState(); }
        // T-126: a seat already playing the co-op world when this player became the host (no checkpoint to send).
        public void JoinReady(int seat) { GuestState g = new GuestState(); g.Sent = g.Ready = true; m_guests[seat] = g; }
        public void Leave(int seat) { m_guests.Remove(seat); m_awaiting.Remove(seat); }
        public void Clear() { m_guests.Clear(); m_awaiting.Clear(); }

        // T-213 (10): what a guest notes about the other guests, for the day the server makes it the host. A seat that
        // said hello after this guest finished its own join has no save until it announces its load (the announcement
        // is a broadcast, and a server hands every broadcast to every seat). A seat that was here before is taken as
        // playing: whether it had loaded cannot be known.
        readonly HashSet<int> m_awaiting = new HashSet<int>();
        public void SawJoin(int seat) { m_awaiting.Add(seat); }
        public void SawLoaded(int seat) { m_awaiting.Remove(seat); }
        // This guest became the host. A seat still waiting for its first save is unsent (the new host sends its
        // checkpoint; marked sent and ready, it waited for the old host's save for ever). Every other seat already
        // plays the co-op world (T-126: ready, nothing to send, no forced reload).
        // A seat this player already holds as unsent said hello after the server made this player the host, before the
        // promotion ran (both can arrive in one receive loop): the host path registered it, and it stays unsent.
        // Overwritten as ready, that new guest waited for its first save for ever.
        public void Promote(IEnumerable<int> seats)
        {
            foreach (int seat in seats)
            {
                GuestState known;
                if (m_guests.TryGetValue(seat, out known) && !known.Sent) continue;
                if (m_awaiting.Contains(seat)) Join(seat); else JoinReady(seat);
            }
            m_awaiting.Clear();
        }
        public void MarkSent(int seat) { MarkSent(seat, 0f); }
        public void MarkSent(int seat, float now) { GuestState guest = m_guests[seat]; guest.Sent = true; guest.SentAt = now; }
        // T-214: the seat whose checkpoint has been out longest without its load being reported (0 = none), and how long.
        public int LongestWait(float now, out float seconds)
        {
            int seat = 0; seconds = 0f;
            foreach (KeyValuePair<int, GuestState> guest in m_guests)
                if (guest.Value.Sent && !guest.Value.Ready && (seat == 0 || now - guest.Value.SentAt > seconds))
                { seat = guest.Key; seconds = now - guest.Value.SentAt; }
            return seat;
        }
        public bool IsSent(int seat) { return m_guests[seat].Sent; }

        public bool HasUnsent
        {
            get { foreach (GuestState guest in m_guests.Values) if (!guest.Sent) return true; return false; }
        }

        public bool AnyReady
        {
            get { foreach (GuestState guest in m_guests.Values) if (guest.Ready) return true; return false; }
        }

        public bool Loaded(int seat)
        {
            GuestState guest;
            if (!m_guests.TryGetValue(seat, out guest) || !guest.Sent || guest.Ready) return false;
            guest.Ready = true;
            return true;
        }

        public void ResetGuests()
        {
            foreach (GuestState guest in m_guests.Values) guest.Sent = guest.Ready = false;
        }

        public bool ReadyForSeat(int seat, bool isHost, bool ready)
        {
            if (!ready) return false;
            if (!isHost) return ready;
            GuestState guest;
            return m_guests.TryGetValue(seat, out guest) && guest.Ready;
        }
    }
}
