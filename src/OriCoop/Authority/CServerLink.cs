using System;
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
        static CServerClient s_stale;

        public static CServerClient Client { get { return s_client; } }
        public static string LastFailure = "";      // L10n key of the last join failure ("" = none)

        public static void Install()
        {
            if (s_installed) return;
            s_installed = true;
            IAuthority touch = AuthoritySource.Current;  // registers the 0xB0-0xBA handlers now (needed before CsWelcome)
            NetPlay.TransportFactory = Create;
            NetPlay.Stopped = delegate { Teardown(); };   // leaving the room: free the socket, routing and arbiter role (T-031)
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
            Teardown();
            // Socket of a dropped session: freed here, not inside its own Disconnected event (NetPlay may still poll it).
            if (s_stale != null) { try { s_stale.Leave(); } catch { } s_stale = null; }
            string server = target ?? "", code = "";
            int bar = server.IndexOf('|');
            if (bar >= 0) { code = server.Substring(bar + 1).Trim().ToUpperInvariant(); server = server.Substring(0, bar); }
            // Started from the environment (tests, no menu): server from CServerConfig, code like relay's test hooks.
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
            CServerClient c = new CServerClient(ep, mode, code,
                rejoin ? s_lastToken : null, 1, new CsIdentity[] { new CsIdentity { Steam = 0, Nick = "" } });
            c.RejoinFallsBackToJoin = rejoin;
            c.SeatAssigned += delegate(int seat)
            {
                NetPlay.RoomCodeOverride = c.Code;
                s_lastCode = c.Code; s_lastToken = c.Token;
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
                if (c.PrimarySeat == 0 && (key == "server.err.token" || key == "server.err.noroom")) { s_lastToken = null; s_lastCode = ""; }
            };
            // Not SessionEnded: that fires when the last partner leaves, but in server mode the room stays open.
            c.Peer.Disconnected += delegate(string why)
            {
                if (s_client != c) return;
                Plugin.Log.LogInfo("cserver: disconnected (" + why + ")");
                Unroute(); s_client = null; s_stale = c;
            };
            s_client = c;
            NetRoute.UseServer(c);
            AuthoritySource.UseServer(c.Authority, c.Receive);
            Plugin.Log.LogInfo("cserver: connecting (" + (rejoin ? "rejoin " + code : isHost ? "new room" : "join " + code) + ")");
            return c.Peer;
        }

        static void Teardown()
        {
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

        static IPEndPoint Resolve(string hostPort)
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
