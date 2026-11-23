using System;
using System.Collections;
using System.IO;
using System.Net;
using OriCoop.Net;

namespace OriCoop.Authority
{
    // Unity-side glue for transport "cserver" (docs/c-server-design.md §8, C2). Uses the NetPlay hooks from
    // four-player phase 2: TransportFactory, AssignSeat, HostOverride, SendToOverride, RemoteLeft, RoomCodeOverride.
    // NetPlay treats the returned NetPeer like a guest's single peer; seats 2-4 appear through forwarded messages.
    public static class CServerLink
    {
        static CServerClient s_client;
        static bool s_installed;
        static string s_lastCode = ""; static byte[] s_lastToken;
        static bool s_lastCreated;   // T-173: s_lastCode's room was created by this PC (a token rejoin keeps CServerClient.Created)
        static CServerClient s_stale;
        // T-173: kicked out (CServerClient.RetryAllowed false): no reconnect until NetPlay.End has closed the session.
        static bool s_endedForGood;
        // T-148: this NetPlay session got a seat at least once (in the room or in the game), so a later refusal ends it.
        static bool s_seated;
        // T-148 test hook: connections opened since the game started (a refused menu join must stay at one).
        internal static int ConnectAttempts;

        public static CServerClient Client { get { return s_client; } }
        public static string LastFailure = "";      // L10n key of the last join failure ("" = none)

        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            IAuthority touch = AuthoritySource.Current;  // registers the 0xB0-0xBA handlers now (needed before CsWelcome)
            NetPlay.TransportFactory = Create;
            NetPlay.Stopped = delegate { Teardown(); s_endedForGood = false; s_seated = false; };   // leaving the room: free the socket, routing and arbiter role (T-031)
            // T-113: before NetPlay.End says goodbye, free our seats on the server so a rejoin (or anyone) gets them now.
            NetPlay.Leaving = delegate
            {
                if (s_client == null) return;
                try { s_client.ReleaseAll(); Plugin.Log.LogInfo("cserver: leaving, released seat P" + s_client.PrimarySeat); }
                catch (Exception e) { Plugin.Log.LogWarning("cserver: release on leave: " + e.Message); }
            };
            NetPlay.SendToOverride = delegate(int seat, byte type, bool reliable, Action<BinaryWriter> body)
            {
                if (s_client == null) return false;
                s_client.SendTo(seat, type, reliable, body);
                return true;
            };
        }

        // target = "host:port|CODE" (CODE empty when creating a room).
        static NetPeer Create(string transport, bool isHost, string target)
        {
            if (transport != "cserver") return null;
            if (s_endedForGood) return null;   // T-173: kicked; NetPlay.End (deferred one frame) ends the session
            Teardown();
            // Socket of a dropped session: freed here, not inside its own Disconnected event (NetPlay may still poll it).
            if (s_stale != null) { try { s_stale.Leave(); } catch { } s_stale = null; }
            string server = target ?? "", code = "";
            int bar = server.IndexOf('|');
            if (bar >= 0) { code = server.Substring(bar + 1).Trim().ToUpperInvariant(); server = server.Substring(0, bar); }
            // Started from the environment (tests, no menu): server from CServerConfig, code from the test hooks (ORICOOP_NET_ROOM / _FILE).
            if (server.Trim().Length == 0) server = CServerConfig.Address;
            if (!isHost && code.Length == 0)
            {
                code = (Environment.GetEnvironmentVariable("ORICOOP_NET_ROOM") ?? "").Trim().ToUpperInvariant();
                string codeFile = Environment.GetEnvironmentVariable("ORICOOP_NET_ROOM_FILE");
                if (code.Length == 0 && !string.IsNullOrEmpty(codeFile))
                    try { if (File.Exists(codeFile)) code = File.ReadAllText(codeFile).Trim().ToUpperInvariant(); } catch { }
                if (code.Length == 0) return null;   // NetPlay retries; the host's code is not there yet
            }
            IPEndPoint ep = Resolve(server.Trim());
            if (ep == null) { LastFailure = "server.err.noroom"; Plugin.Log.LogWarning("cserver: cannot resolve the server address"); return null; }
            LastFailure = "";
            // Reconnect (NetPlay reopening the same session after a drop): NetPlay.End clears RoomCodeOverride,
            // Teardown does not, so a surviving override equal to our last room means "rejoin my seat with the token".
            // T-113: a guest entering the code of the room it just left also rejoins with the token (its seat may still
            // be held); if the server no longer knows the token, the client joins fresh on the same connection.
            bool rejoin;
            byte mode = CServerClient.PlanJoin(isHost, code, s_lastCode, s_lastToken, NetPlay.RoomCodeOverride, out rejoin);
            if (rejoin) code = s_lastCode;
            // TODO(four-player): pass all local players' identities/count and map their assigned seats here.
            // T-173: the real Steam account (0 / "" without Steam, e.g. test copies): kicks ban it, the room page and room
            // list show the name. Create runs on the main thread (NetPlay.Update), as SteamTransport requires.
            CsIdentity self = new CsIdentity { Steam = SteamTransport.LocalSteamId,
                Nick = Arbiter.ClampUtf8(SteamTransport.LocalPersonaName ?? "", Arbiter.MaxTextBytes) };
            CServerClient c = new CServerClient(ep, mode, code,
                rejoin ? s_lastToken : null, 1, new CsIdentity[] { self });
            c.RejoinFallsBackToJoin = rejoin;
            c.RejoinCreated = rejoin && s_lastCreated;
            c.Private = RoomServiceSession;   // T-173 (review F10): its room info never reaches the public room list
            c.SeatAssigned += delegate(int seat)
            {
                s_seated = true;
                NetPlay.RoomCodeOverride = c.Code;
                s_lastCode = c.Code; s_lastToken = c.Token; s_lastCreated = c.Created;
                NetPlay.AssignSeat(c.PrimarySeat);
                NetPlay.HostOverride = c.IsArbiter;
                Plugin.Log.LogInfo("cserver: seat P" + seat + ", room code " + c.Code + (c.IsArbiter ? ", legacy arbiter" : ""));
            };
            c.ArbiterChanged += delegate(int seat)
            {
                NetPlay.HostOverride = c.IsArbiter;
                Plugin.Log.LogInfo("cserver: legacy arbiter is now P" + seat);
            };
            c.SeatOffline += delegate(int seat) { if (seat != c.Seat) NetPlay.RemoteLeft(seat); };
            // A seat that came online after my Hello never saw it: introduce myself (Hello + Status) to it.
            c.SeatOnline += delegate(int seat) { if (seat != c.Seat && c.Seat != 0) NetPlay.IntroduceTo(seat); };
            c.JoinFailed += delegate(string key)
            {
                LastFailure = key;
                Plugin.Log.LogWarning("cserver: join failed: " + key + " (" + L10n.T(key) + ")");
                // T-148: the saved seat or the room is gone: forget the token, so the next attempt (NetPlay retries after
                // the client ends the refused connection) joins fresh instead of reusing the dead token forever.
                if (c.PrimarySeat == 0 && (key == "server.err.token" || key == "server.err.noroom")) { s_lastToken = null; s_lastCode = ""; s_lastCreated = false; }
            };
            // T-148 (playtest 4): the server refused this client for good (no such room, full, other version, kicked). Trying
            // again gets the same answer, so the reconnect stops here, inside Receive: NetPlay reopens a dropped connection
            // on its next Update, before this peer's Disconnected event is delivered (the handler below never ran for a
            // refused join, and the refusal was retried about twice a second while the join page showed "connecting").
            //  - no seat yet in this session (a join or create from the menu): the session is held with the reason as
            //    NetPlay.Problem; the join / host page shows it with retry and back;
            //  - seated before (a reconnect in the room or in the game; T-173: kicked): tell the player once and end
            //    online play a frame later, outside Receive.
            c.Refused += delegate(string key)
            {
                bool end = CServerClient.RefusalEndsSession(s_seated);
                NetPlay.HoldRetry(key);
                Plugin.Log.LogInfo("cserver: refused (" + key + "): " + (end ? "ending online play" : "no automatic retry, the page shows the reason"));
                if (!end) return;
                s_endedForGood = true; s_lastToken = null; s_lastCode = ""; s_lastCreated = false;
                NetHud.Toast(L10n.T(key));
                if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(EndForGood(key));
            };
            // Not SessionEnded: that fires when the last partner leaves, but in server mode the room stays open.
            c.Peer.Disconnected += delegate(string why)
            {
                if (s_client != c) return;
                Plugin.Log.LogInfo("cserver: disconnected (" + why + ")");
                Unroute(); s_client = null; s_stale = c;
            };
            s_client = c;
            ConnectAttempts++;
            NetRoute.UseServer(c);
            AuthoritySource.UseServer(c.Authority, c.Receive);
            Plugin.Log.LogInfo("cserver: connecting (" + (rejoin ? "rejoin " + code : isHost ? "new room" : "join " + code) + ")");
            return c.Peer;
        }

        // T-173 (review F10): this session plays a room-service room (我的房间 or a password room): RoomSession is active
        // (set before it starts NetPlay, and still active on a reconnect). Its rooms stay off the C-server's room list.
        static bool RoomServiceSession
        {
            get
            {
                OriCoop.Rooms.RoomsPanel p = OriCoop.Rooms.RoomsPanel.Instance;
                return p != null && p.Session != null && p.Session.Active;
            }
        }

        // T-173: one frame later, outside the peer's Disconnected event (NetPlay is still reading that peer then).
        static IEnumerator EndForGood(string why)
        {
            yield return null;
            if (s_endedForGood && NetPlay.Enabled) NetPlay.End(why);   // End -> Stopped clears s_endedForGood
            s_endedForGood = false;
        }

        public static bool HasStale { get { return s_stale != null; } }   // T-152 test hook
        static readonly bool s_mutateKeepStale = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_KEEP_STALE") == "1";

        static void Teardown()
        {
            // T-152: a dropped session's client is parked in s_stale (NetPlay may still poll it until the next Create).
            // Leaving online play (NetPlay.End -> Stopped, after Close) must free its UDP socket too.
            if (s_stale != null && !s_mutateKeepStale) { try { s_stale.Leave(); } catch { } s_stale = null; }
            if (s_client == null) return;
            CServerClient c = s_client;
            s_client = null;
            Unroute();
            try { c.Leave(); } catch (Exception e) { Plugin.Log.LogWarning("cserver: leave: " + e.Message); }
        }

        static void Unroute()
        {
            NetRoute.UseServer(null);
            AuthoritySource.UseServer(null, null);
            NetPlay.HostOverride = false;   // RoomCodeOverride stays: NetPlay.End clears it; a reconnect reuses it
        }

        internal static IPEndPoint Resolve(string hostPort)
        {
            if (hostPort.Length == 0) return null;
            string host = hostPort; int port = CServerConfig.DefaultPort;
            int colon = hostPort.LastIndexOf(':');
            if (colon > 0 && hostPort.IndexOf(':') == colon) { host = hostPort.Substring(0, colon); int.TryParse(hostPort.Substring(colon + 1), out port); }
            IPAddress ip;
            if (IPAddress.TryParse(host, out ip)) return new IPEndPoint(ip, port);
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return new IPEndPoint(a, port);
            }
            catch (Exception e) { Plugin.Log.LogWarning("cserver: dns: " + e.Message); }
            return null;
        }
    }
}
