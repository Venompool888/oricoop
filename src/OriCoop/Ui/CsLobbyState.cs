using System;

namespace OriCoop.Ui
{
    // T-171 review: what CsLobby remembers about the room page, as plain data (no Unity, no network; unit-tested). CsLobby
    // feeds it from NetPlay, the C-server link and the LobbyState messages.
    //  - The room's save / name live as long as the NetPlay session: a short C-server link drop (the client reconnects
    //    with a new CServerClient) keeps them, so the creator's page still shows the save and re-sends it.
    //  - A remote seat's ready / playing / ping is forgotten when that seat leaves the roster (quit, timeout, kick), on
    //    every PC, so the next player on that seat does not inherit it.
    //  - A LobbyState entry is taken only for a seat its sender owns: the sender's own seat (the server checks who sent
    //    it), or another seat the sender claimed first (a split-screen seat); the claim holds until that seat leaves.
    //    A claim is only possible for a seat that is taken in the roster (an empty seat has nobody to speak for; a claim
    //    made on it would carry over to the next player) and that is above the sender's seat: a PC sends as its primary
    //    seat, the lowest of its seats (Arbiter.PrimarySeat), so its split-screen seats are always higher.
    //  - The room's save is published by the PC that created the room (CServerClient.Created), not by whoever is the
    //    arbiter now (a late joiner on a freed lower seat).
    internal sealed class CsLobbyState
    {
        public const int Seats = 4;

        sealed class Remote { public bool Ready, Playing; public int RttMs = -1; }

        readonly Remote[] m_remote = new Remote[Seats + 1];
        readonly int[] m_owner = new int[Seats + 1];     // the sender seat whose LobbyState speaks for this seat; 0 = nobody
        readonly bool[] m_taken = new bool[Seats + 1];   // the roster as last seen
        bool m_session;

        public bool LocalReady;
        public string LocalSummary = "";   // this PC's room save, only when it created the room
        public string RoomSummary = "";    // the room's save as the manager last sent it
        public string RoomName = "";

        // Every frame: is a C-server NetPlay session running? true = the session just ended and everything was cleared.
        public bool Session(bool running)
        {
            if (running) { m_session = true; return false; }
            if (!m_session) return false;
            Clear();
            return true;
        }

        // Every frame while the link is up, per seat: is it taken in the C-server roster? A seat that was taken and is free
        // now left the room: forget what it said.
        public void Roster(int seat, bool taken)
        {
            if (seat < 1 || seat > Seats) return;
            if (m_taken[seat] && !taken) Forget(seat);
            m_taken[seat] = taken;
        }

        // One seat entry of a LobbyState from sender (the sender's seat). false = refused (not the sender's seat).
        public bool Apply(int sender, CsLobbySeatState s, bool playing)
        {
            int seat = s.Seat;
            if (seat < 1 || seat > Seats || sender < 1 || sender > Seats) return false;
            if (seat != sender)
            {
                if (seat < sender || !m_taken[seat]) return false;
                if (m_owner[seat] != 0 && m_owner[seat] != sender) return false;
            }
            m_owner[seat] = sender;
            Remote remote = m_remote[seat] ?? (m_remote[seat] = new Remote());
            remote.Ready = s.Ready; remote.RttMs = s.RttMs; remote.Playing = playing;
            return true;
        }

        // The room page opened: this PC publishes its save slot only when it created the room.
        public void PageOpened(bool createdRoom, string slotSummary) { LocalSummary = createdRoom ? (slotSummary ?? "") : ""; }

        // T-173 (room-v2-a, [E] 改名): the room's save line as the manager sent it (a LobbyState from the manager's seat).
        // The name in it is the room's name now: this PC takes it as well, so a PC that manages the room later (the
        // creator back after its seat was held for a reconnect, or a stand-in) publishes the latest name, not its own old one.
        public void ManagerSummary(string summary)
        {
            if (string.IsNullOrEmpty(summary)) return;
            RoomSummary = summary;
            string name = CsRoomSummary.Parse(summary).Name;   // already cleaned (RoomNames.Clean)
            if (name.Length == 0 || name == RoomName) return;
            RoomName = name;
            if (LocalSummary.Length > 0) LocalSummary = WithName(LocalSummary, name);
        }

        // The room's name can go out from this PC: it has a save line to carry it (its own as the creator, or as the
        // manager the line the creator last sent).
        public bool CanCarryName(bool isManager) { return LocalSummary.Length > 0 || isManager && RoomSummary.Length > 0; }

        // T-173: the manager renamed the room. The name lives in the room's save line: this PC's own line, or (a manager
        // that did not create the room) the line the creator last sent, which becomes this PC's line to publish. This
        // PC's copy of the room's line takes the name too, so its page keeps showing it after the page reopens (a
        // stand-in's own line is dropped then). false = no line to carry the name: nothing changes (the page refuses).
        public bool Rename(string name, bool isManager)
        {
            string clean = RoomNames.Clean(name);
            if (clean.Length == 0 || !CanCarryName(isManager)) return false;
            string from = LocalSummary.Length > 0 ? LocalSummary : RoomSummary;
            RoomName = clean;
            LocalSummary = WithName(from, clean);
            if (RoomSummary.Length > 0) RoomSummary = WithName(RoomSummary, clean);
            return true;
        }

        static string WithName(string summary, string name)
        {
            CsRoomSummary s = CsRoomSummary.Parse(summary);
            s.Name = name;
            return s.Encode();
        }

        public bool Ready(int seat) { return Valid(seat) && m_remote[seat] != null && m_remote[seat].Ready; }
        public bool Playing(int seat) { return Valid(seat) && m_remote[seat] != null && m_remote[seat].Playing; }
        public int Rtt(int seat) { return Valid(seat) && m_remote[seat] != null ? m_remote[seat].RttMs : -1; }

        public void Forget(int seat)
        {
            if (!Valid(seat)) return;
            m_remote[seat] = null; m_owner[seat] = 0;
        }

        public void Clear()
        {
            m_session = false; LocalReady = false; LocalSummary = ""; RoomSummary = ""; RoomName = "";
            for (int i = 0; i <= Seats; i++) { m_remote[i] = null; m_owner[i] = 0; m_taken[i] = false; }
        }

        static bool Valid(int seat) { return seat >= 1 && seat <= Seats; }
    }
}
