using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Authority;
using OriCoop.Seams;
using OriCoop.Ui;

namespace OriCoop
{
    // T-171: the C-server room page's shared state between PCs (ready, each player's server RTT, the room's save,
    // who is already in the game) and the manager's "start". Ticked every frame from OnlineRoomScreen.Tick; it only
    // does anything in a C-server session. Wire format: Ui/CsLobbyWire.cs (msgs 94 / 95).
    internal static class CsLobby
    {
        // Pure state (Ui/CsLobbyState.cs, unit-tested): lives as long as the NetPlay session, survives a short link drop,
        // forgets a seat that leaves the roster, takes a seat's state only from the PC that owns it.
        static readonly CsLobbyState s_state = new CsLobbyState();
        static bool s_registered, s_dirty;
        static CServerClient s_client;   // the link the state was last sent on (a reconnect makes a new client)
        static float s_nextSend;
        static int s_seatCount;
        static readonly List<CsLobbySeatState> s_scratch = new List<CsLobbySeatState>();
        internal static int StartsReceived;

        // Set by the page when this PC created the room (its save slot); "" on a PC that joined.
        internal static string LocalSummary { get { return s_state.LocalSummary; } set { s_state.LocalSummary = value ?? ""; } }
        // The room's name on the creator's PC: two random words until the player names it (English only, RoomNames).
        internal static string RoomName { get { if (s_state.RoomName.Length == 0) s_state.RoomName = RoomNames.Random(new Random()); return s_state.RoomName; } }
        // T-173: a 我的房间 room keeps the name it has on the room service.
        internal static void SetRoomName(string name) { s_state.RoomName = RoomNames.Clean(name); }

        // T-173 (room-v2-a, [E] 改名): the room's manager renamed the room. The name lives in the room's save line, so the
        // line is rebuilt with it: every PC gets it with the next LobbyState (sent at once) and Tick puts it in the
        // C-server's room info (the server's room list). A manager that did not create the room (the creator left) has no
        // line of its own: it takes the room's line as the creator last sent it, so the new name still goes out
        // (Ui/CsLobbyState.Rename). false = no line to carry the name yet: nothing changed.
        internal static bool Rename(string name)
        {
            if (!Active || !s_state.Rename(name, IsManager)) return false;
            s_dirty = true;
            return true;
        }

        // The page may offer [E] 改名 (with CsRoomModel.CanRename): this PC has a save line that carries the name out.
        internal static bool CanRename { get { return Active && s_state.CanCarryName(IsManager); } }

        static bool Session { get { return NetPlay.Enabled && NetPlay.TransportName == "cserver"; } }
        public static bool Active { get { return Session && CServerLink.Client != null; } }
        public static bool LocalReady { get { return s_state.LocalReady; } }
        public static string RoomSummary { get { return Active && IsManager && LocalSummary.Length > 0 ? LocalSummary : s_state.RoomSummary; } }
        public static int ManagerSeat { get { return CServerLink.Client != null ? CServerLink.Client.ArbiterSeat : 0; } }
        public static bool IsManager { get { CServerClient c = CServerLink.Client; return c != null && c.ArbiterSeat != 0 && c.Seats.Contains(c.ArbiterSeat); } }
        // This PC made the room with its own join (the room's creator), whoever is the arbiter now.
        public static bool CreatedRoom { get { CServerClient c = CServerLink.Client; return c != null && c.Created; } }

        public static void SetReady(bool ready) { if (s_state.LocalReady != ready) { s_state.LocalReady = ready; s_dirty = true; } }

        public static bool RemoteReady(int seat) { return s_state.Ready(seat); }
        public static bool RemotePlaying(int seat) { return s_state.Playing(seat); }
        public static int RemoteRtt(int seat) { return s_state.Rtt(seat); }

        // The room page opened: publish this PC's save only when it created the room.
        public static void PageOpened(string slotSummary) { s_state.PageOpened(CreatedRoom, slotSummary); s_dirty = true; }

        static bool Playing { get { return OnlineRoomScreen.Confirmed && !OnlineRoomScreen.Visible; } }

        static void Register()
        {
            if (s_registered) return;
            s_registered = true;
            Services.Net.On(CsLobbyWire.MsgLobbyState, OnState);
            Services.Net.On(CsLobbyWire.MsgLobbyStart, OnStart);
        }

        public static void Tick()
        {
            // Only the end of the session wipes the room; while the link is down (it reconnects) nothing changes.
            if (s_state.Session(Session)) s_client = null;
            if (!Active) return;
            Register();
            CServerClient c = CServerLink.Client;
            if (c != s_client) { s_client = c; s_dirty = true; }   // a new link (first, or after a reconnect): send again
            if (c.Seats.Count != s_seatCount) { s_seatCount = c.Seats.Count; s_dirty = true; }   // a seat was added / released
            for (int seat = 1; seat <= CsLobbyState.Seats; seat++) s_state.Roster(seat, c.SeatTaken(seat) || c.Seats.Contains(seat));
            if (IsManager && LocalSummary.Length > 0 && c.RoomInfo != LocalSummary) c.SetRoomInfo(LocalSummary);
            float now = Services.Clock.RealTime;
            if (!s_dirty && now < s_nextSend) return;
            s_dirty = false; s_nextSend = now + 2f;
            // No seat yet (the join has not been welcomed): stay dirty, so the first state goes out as soon as there is one
            // instead of up to 2 s later (until then another PC's claim could stand for this PC's new seat).
            if (!Send()) s_dirty = true;
        }

        static bool Send()
        {
            CServerClient c = CServerLink.Client;
            s_scratch.Clear();
            int rtt = NetPlay.RttMs > 0f ? (int)(NetPlay.RttMs + 0.5f) : -1;
            foreach (int seat in c.Seats) s_scratch.Add(new CsLobbySeatState { Seat = (byte)seat, Ready = s_state.LocalReady, RttMs = rtt });
            if (s_scratch.Count == 0) return false;
            string summary = IsManager ? LocalSummary : "";
            bool playing = Playing;
            Services.Net.Send(CsLobbyWire.MsgLobbyState, true, w => CsLobbyWire.WriteState(w, s_scratch, summary, playing));
            return true;
        }

        public static void Changed() { s_dirty = true; }

        static void OnState(BinaryReader r)
        {
            List<CsLobbySeatState> seats = new List<CsLobbySeatState>();
            string summary; bool playing;
            if (!Active || !CsLobbyWire.TryReadState(r, seats, out summary, out playing)) return;
            int sender = Services.Net.Sender;
            foreach (CsLobbySeatState s in seats)
            {
                if (CServerLink.Client.Seats.Contains(s.Seat)) continue;
                if (!s_state.Apply(sender, s, playing)) Services.Log.Info("cs room: P" + sender + " sent a state for P" + s.Seat + ", not its seat: ignored");
            }
            // The manager's line; its name (a rename) is also taken into this PC's own name and line (Ui/CsLobbyState).
            if (summary.Length > 0 && sender == ManagerSeat && !CServerLink.Client.Seats.Contains(sender)) s_state.ManagerSummary(summary);
        }

        // The manager pressed 开始游戏: every PC still on the page goes into the game.
        static void OnStart(BinaryReader r)
        {
            if (!Active || Services.Net.Sender != ManagerSeat) return;
            StartsReceived++;
            Services.Log.Info("cs room: start from P" + Services.Net.Sender);
            if (OnlineRoomScreen.Visible) OnlineRoomScreen.Continue();
        }

        public static void SendStart()
        {
            if (!Active || !IsManager) return;
            Services.Net.Send(CsLobbyWire.MsgLobbyStart, true, w => { });   // the bus needs a body writer
        }

        // A seat left the room (kicked, quit, timed out): forget what it said. (The roster check in Tick does the same on
        // every PC; the kicker calls this at once.)
        public static void Forget(int seat) { s_state.Forget(seat); }
    }
}
