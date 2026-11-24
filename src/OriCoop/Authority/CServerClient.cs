using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using OriCoop.Net;

namespace OriCoop.Authority
{
    public sealed class CServerClient : INetRouteServer
    {
        readonly UdpTransport transport;
        readonly List<int> seats = new List<int>();
        bool[] online = new bool[5];

        public CServerClient(EndPoint server, byte mode, string code, byte[] token, ulong steam, string nick)
            : this(server, mode, code, token, 1, new CsIdentity[] { new CsIdentity { Steam = steam, Nick = nick } }) { }

        public CServerClient(EndPoint server, byte mode, string code, byte[] token, int count, CsIdentity[] identities)
        {
            if (count < 1 || count > 3 || identities == null || identities.Length != count)
                throw new ArgumentException("join needs 1-3 per-seat identities");
            CsMessage request = new CsMessage { Type = CsWire.CsJoin, Mode = mode,
                Code = code, Token = token ?? new byte[16], Count = (byte)count };
            request.Identities.AddRange(identities);
            byte[] join = CsWire.Encode(request);
            m_request = request;
            Code = "";
            Token = new byte[16];
            transport = new UdpTransport(0);
            Peer = new NetPeer(transport);
            Authority = new AuthorityClient(AuthorityMode.Server, delegate { return PrimarySeat; },
                delegate(byte[] message, bool reliable) { SendAs(PrimarySeat, message, reliable); }, false);
            Peer.Connected += delegate { Send(join, true); };
            Peer.Connect(server);
        }

        public NetPeer Peer { get; private set; }
        public IList<int> Seats { get { return seats.AsReadOnly(); } }
        public int PrimarySeat { get { return Arbiter.PrimarySeat(seats); } }
        public int Seat { get { return PrimarySeat; } }   // single-seat compatibility shim
        public string Code { get; private set; }
        public byte[] Token { get; private set; }
        public AuthorityClient Authority { get; private set; }
        public int ArbiterSeat { get; private set; }
        public bool IsArbiter { get { return PrimarySeat != 0 && ArbiterSeat == PrimarySeat; } }

        readonly CsMessage m_request;
        // T-113: a token rejoin (mode 2) that the server no longer knows (result 4) retries as a fresh join (mode 1).
        public bool RejoinFallsBackToJoin { get; set; }
        public string RefusedWith { get; private set; }   // T-148: server.err.* of a refused join (null until then)
        public bool UsedFallback { get; private set; }
        // T-173: true once this client's own mode-0 join created the room (this PC is the room's creator), or once a token
        // rejoin restored the seats of a room this PC created (RejoinCreated, carried over by CServerLink). False for
        // joiners, after a token rejoin into someone else's room and after a rejoin that fell back to a fresh join.
        public bool Created { get; private set; }
        public bool RejoinCreated { get; set; }

        // T-173: a session refused or ended with this L10n key must not reconnect: a kicked PC's token rejoin and fresh
        // join are both refused for the rest of the room's life, so NetPlay's retry loop would only be refused again.
        public static bool RetryAllowed(string refusedWith) { return refusedWith != "server.err.kicked"; }

        // T-148 (playtest 4): the server refused this client for good (a join refused after the fallback, or a kick). Raised
        // inside Receive, with RefusedWith set and before the connection is ended, so the glue can stop the automatic
        // reconnect at once: NetPlay reopens a dropped connection on its next Update, before the peer's Disconnected event
        // would be delivered, and so retried a refused join about twice a second for as long as the page stayed open.
        public event Action<string> Refused;

        // T-148: what follows a refusal. A session that already had a seat (reconnecting in the room or in the game) ends
        // with the reason; a join or create from the menu that never got a seat stays as it is, with the reason shown and
        // no automatic retry, until the player picks retry or back.
        public static bool RefusalEndsSession(bool seatedThisSession) { return seatedThisSession; }

        // T-113: join mode for a connection attempt. A guest joining the room it last sat in (menu re-entry with the
        // same code, or NetPlay reconnecting) rejoins its seat with the saved token, falling back to a fresh join.
        public static byte PlanJoin(bool isHost, string code, string lastCode, byte[] lastToken, string roomCodeOverride, out bool rejoin)
        {
            bool reconnect = !string.IsNullOrEmpty(roomCodeOverride) && roomCodeOverride == lastCode;
            bool reenter = !isHost && !string.IsNullOrEmpty(code) && code == lastCode;
            rejoin = lastToken != null && !string.IsNullOrEmpty(lastCode) && (reconnect || reenter);
            return (byte)(rejoin ? 2 : isHost ? 0 : 1);
        }

        public event Action<int> SeatAssigned;
        public event Action<string> JoinFailed;
        public event Action<int> SeatOnline;
        public event Action<int> SeatOffline;
        public event Action<int> ArbiterChanged;

        void Send(byte[] message, bool reliable)
        {
            if (reliable) Peer.SendReliable(message, 0, message.Length);
            else Peer.SendUnreliable(message, 0, message.Length);
        }

        public void Receive(byte[] csMessage)
        {
            CsMessage message = CsWire.Decode(csMessage);
            switch (message.Type)
            {
                case CsWire.CsWelcome:
                    if (message.Result == 0)
                    {
                        seats.Clear(); seats.AddRange(message.Seats);
                        Code = message.Code;
                        Token = message.Token;
                        Created = m_request.Mode == 0 || (m_request.Mode == 2 && RejoinCreated);
                        if (SeatAssigned != null) SeatAssigned(Seat);
                    }
                    else if (message.Result == 4 && RejoinFallsBackToJoin && m_request.Mode == 2 && !string.IsNullOrEmpty(m_request.Code))
                    {
                        // T-113: the saved seat is gone (held time over, or released when we left): join the room fresh,
                        // once, on the same connection (the server accepts another CsJoin until one succeeds).
                        RejoinFallsBackToJoin = false; UsedFallback = true;
                        m_request.Mode = 1; m_request.Token = new byte[16];
                        Send(CsWire.Encode(m_request), true);
                    }
                    else
                    {
                        // L10n keys (the Unity glue shows L10n.T(key)); this file stays free of Unity code.
                        string[] reasons = { "", "server.err.noroom", "server.err.full", "server.err.version", "server.err.token", "server.err.kicked" };
                        string key = message.Result < reasons.Length ? reasons[message.Result] : "server.err.version";
                        if (JoinFailed != null) JoinFailed(key);
                        // T-148: a refused join left a connected, seatless peer: NetPlay stayed "connecting" (never retried,
                        // no reason shown). End it now with the reason as the disconnect text; NetPlay shows it and retries.
                        if (PrimarySeat == 0) { RefusedWith = key; if (Refused != null) Refused(key); Peer.Disconnect(key); }
                    }
                    break;
                case CsWire.CsRoster:
                    bool[] previous = online;
                    online = new bool[5];
                    taken = new bool[5]; nicks = new string[5];   // T-171: the room page shows every seat's name
                    foreach (CsRosterEntry entry in message.Roster)
                    {
                        // Seats are 1-4; a roster entry outside them (a bad or newer server) is ignored instead of throwing
                        // half-way, which left the roster half-replaced and skipped every online/offline/arbiter event.
                        if (entry.Seat < 1 || entry.Seat > 4) continue;
                        online[entry.Seat] = entry.Online;
                        taken[entry.Seat] = true; nicks[entry.Seat] = entry.Nick ?? "";
                    }
                    int oldArbiter = ArbiterSeat;
                    ArbiterSeat = 0;
                    for (int seat = 1; seat <= 4; seat++)
                        if (online[seat]) { ArbiterSeat = seat; break; }
                    for (int seat = 1; seat <= 4; seat++)
                    {
                        if (online[seat] && !previous[seat] && SeatOnline != null) SeatOnline(seat);
                        if (!online[seat] && previous[seat] && SeatOffline != null) SeatOffline(seat);
                    }
                    if (oldArbiter != ArbiterSeat && ArbiterChanged != null) ArbiterChanged(ArbiterSeat);
                    break;
                case CsWire.CsError:
                    if (message.Text == "server.err.kicked") RefusedWith = message.Text;
                    if (JoinFailed != null) JoinFailed(message.Text);
                    if (message.Text == "server.err.kicked") { if (Refused != null) Refused(message.Text); Peer.Disconnect(message.Text); }
                    break;
                default:
                    Authority.Receive(csMessage);
                    break;
            }
        }

        void RequireLocal(int seat)
        {
            if (!seats.Contains(seat)) throw new ArgumentException("sender seat is not local", "seat");
        }

        static byte[] GameMessage(int seat, byte type, Action<BinaryWriter> body)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                BinaryWriter writer = new BinaryWriter(stream);
                writer.Write(type); writer.Write((byte)seat);
                if (body != null) body(writer);
                return stream.ToArray();
            }
        }

        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            SendAs(PrimarySeat, type, reliable, body);
        }

        public void SendAs(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            RequireLocal(seat);
            Send(GameMessage(seat, type, body), reliable);
        }

        public void SendAs(int seat, byte[] message, bool reliable)
        {
            RequireLocal(seat);
            if (message == null || message.Length < 2) throw new ArgumentException("message needs type and seat");
            byte[] stamped = (byte[])message.Clone(); stamped[1] = (byte)seat;
            Send(stamped, reliable);
        }

        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            SendToAs(PrimarySeat, seat, type, reliable, body);
        }

        public void SendToAs(int sender, int target, byte type, bool reliable, Action<BinaryWriter> body)
        {
            RequireLocal(sender);
            Send(CsWire.Encode(new CsMessage { Type = CsWire.CsTo, Seat = (byte)sender,
                Target = (byte)target, Inner = GameMessage(sender, type, body) }), reliable);
        }

        public void Kick(int seat)
        {
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsKick, Target = (byte)seat }), true);
        }

        // T-171: seats in the room (online or held for a reconnect) and their names, from the latest roster.
        bool[] taken = new bool[5];
        string[] nicks = new string[5];
        public bool SeatTaken(int seat) { return seat >= 1 && seat <= 4 && taken[seat]; }
        public bool SeatOnlineNow(int seat) { return seat >= 1 && seat <= 4 && online[seat]; }
        public string SeatNick(int seat) { return seat >= 1 && seat <= 4 && nicks[seat] != null ? nicks[seat] : ""; }
        public string RoomInfo { get; private set; }   // the room info text this client last sent (or kept, see Private)

        // T-173 (review F10): a room the room service runs (我的房间 / password rooms, RoomSession) is private: the server
        // lists only rooms whose manager set a room info text, so SetRoomInfo keeps the text here and sends nothing, and
        // the room's code never shows on the server's public room list.
        public bool Private { get; set; }

        public void SetRoomInfo(string text)
        {
            RoomInfo = text;
            if (Private) return;
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsRoomInfo, Text = text }), true);
        }

        public void AddSeat(ulong steam, string nick)
        {
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsSeatAdd, Steam = steam, Nick = nick }), true);
        }

        public void ReleaseSeat(int seat)
        {
            RequireLocal(seat);
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsSeatRelease, Target = (byte)seat }), true);
        }

        // T-113: leaving on purpose: ask the server to free every seat of this device now (Target 0) instead of holding
        // them for a reconnect. Older servers reply CsError and keep the hold; the token rejoin still works there.
        public void ReleaseAll()
        {
            if (PrimarySeat == 0 || Peer.State != PeerState.Connected) return;
            SendAs(PrimarySeat, CsWire.Encode(new CsMessage { Type = CsWire.CsSeatRelease, Target = 0 }), true);
            Peer.Flush();
        }

        // T-213 (3): NetPlay.End releases its seats and says goodbye in the same instant, so both datagrams reach the
        // server in one poll: the goodbye marks the seat offline first, and the release, read after it, is ignored (an
        // offline seat cannot release). The seat then stays held for the whole reconnect window (60 s) and a full room
        // stays "full". A deliberate leave therefore waits, for at most waitMs, until the server has acknowledged the
        // release: the server handles a message in the step it arrives in, so a goodbye sent after the acknowledgement
        // is read after the release. idle runs between polls (null: sleep a moment; a test steps its server there).
        // Returns true when the release was acknowledged.
        internal static bool TestSkipReleaseWait;   // test only: release without waiting, as before
        public bool ReleaseAll(int waitMs, Action idle)
        {
            if (PrimarySeat == 0 || Peer.State != PeerState.Connected) return false;
            ReleaseAll();
            if (TestSkipReleaseWait) return false;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            while (Peer.State == PeerState.Connected && Peer.Stats.ReliablePending > 0 && watch.ElapsedMilliseconds < waitMs)
            {
                if (idle != null) idle(); else System.Threading.Thread.Sleep(2);
                Peer.Poll(); Peer.Flush();
            }
            return Peer.State == PeerState.Connected && Peer.Stats.ReliablePending == 0;
        }

        public void Leave()
        {
            Peer.Disconnect("leave");
            transport.Dispose();
        }
    }
}
