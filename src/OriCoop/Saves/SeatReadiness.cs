using System.Collections.Generic;

namespace OriCoop.Saves
{
    public sealed class SeatReadiness
    {
        sealed class GuestState { public bool Sent, Ready; }
        readonly Dictionary<int, GuestState> m_guests = new Dictionary<int, GuestState>();

        public IEnumerable<int> Seats { get { return m_guests.Keys; } }
        public void Join(int seat) { m_guests[seat] = new GuestState(); }
        public void Leave(int seat) { m_guests.Remove(seat); }
        public void Clear() { m_guests.Clear(); }
        public void MarkSent(int seat) { m_guests[seat].Sent = true; }
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
