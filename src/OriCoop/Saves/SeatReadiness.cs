using System;
using System.Collections.Generic;
using OriCoop.Seams;

namespace OriCoop.Saves
{
    public sealed class SeatReadiness
    {
        sealed class GuestState { public bool Sent, Ready; public float SentAt; }
        readonly Dictionary<int, GuestState> m_guests = new Dictionary<int, GuestState>();

        // T-242: a checkpoint round (the host's team wipe restore, or the host adopting the partner's progress) runs
        // from ResetGuests until every seat it covers has reported its load or left. One seat reloading first must
        // not end it: that seat's game saves natively right after its reload, and the host took that save as the
        // room's checkpoint while the other seats were still loading the old one (net-4p: their skill trees and
        // points came back wrong). While the round runs, no seat's save offer becomes the room's save.
        bool m_restoring;
        public bool Restoring { get { return m_restoring; } }
        void SettleRestore()
        {
            if (!m_restoring) return;
            foreach (GuestState guest in m_guests.Values) if (!guest.Ready) return;
            m_restoring = false;
        }

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
        static readonly bool s_skipEarlyHello = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SKIP_EARLY_SAVE_HELLO") == "1";

        public void Observe(INetBus net, Func<bool> localLoaded, Action<int> checkpointLoaded)
        {
            net.PlayerJoined += seat => {
                if (net.IsHost) { Join(seat); return; }
                if (seat == net.HostSeat) return;
                bool playing = localLoaded();
                if ((!s_skipEarlyHello || playing) && !m_peerLoaded.ContainsKey(seat)) SawJoin(seat);
                if (playing) net.SendTo(seat, SaveWire.PeerPlaying, true, SaveWire.WriteLoaded);
            };
            net.On(SaveWire.PeerPlaying, r => { if (!net.IsHost) SawLoaded(net.Sender); });
            net.On(SaveWire.LoadedMessage, r => {
                if (!net.IsHost) { SawLoaded(net.Sender); return; }
                if (Loaded(net.Sender)) checkpointLoaded(net.Sender);
            });
        }
        // T-126: a seat already playing the co-op world when this player became the host (no checkpoint to send).
        public void JoinReady(int seat) { GuestState g = new GuestState(); g.Sent = g.Ready = true; m_guests[seat] = g; }
        public void Leave(int seat) { m_guests.Remove(seat); m_peerLoaded.Remove(seat); SettleRestore(); }
        public void Clear() { m_guests.Clear(); m_peerLoaded.Clear(); m_restoring = false; }

        // Another guest's Hello can arrive during our own load. Its load broadcast or introduction can also
        // precede its Hello. Keep the completed state until this occupant leaves.
        readonly Dictionary<int, bool> m_peerLoaded = new Dictionary<int, bool>();
        public void SawJoin(int seat) { m_peerLoaded[seat] = false; }
        public void SawLoaded(int seat) { m_peerLoaded[seat] = true; }
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
                bool playing;
                if (m_peerLoaded.TryGetValue(seat, out playing) && !playing) Join(seat); else JoinReady(seat);
            }
            m_peerLoaded.Clear();
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
            SettleRestore();
            return true;
        }

        public void ResetGuests()
        {
            foreach (GuestState guest in m_guests.Values) guest.Sent = guest.Ready = false;
            m_restoring = m_guests.Count > 0;
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
