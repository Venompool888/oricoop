using System;
using System.IO;

namespace OriCoop.Authority
{
    // Routing seam for the remaining "the host decides / the host rebroadcasts" code (four-player design).
    // P2P: exactly today's behaviour (NetPlay.IsHost, NetPlay.SendTo, NetPlay.Rebroadcast).
    // C-server (C2): the server already forwards every message to every other seat, so Rebroadcast is a no-op;
    // SendTo wraps in CsTo; IsArbiter is the seat that still arbitrates subsystems not yet moved to IAuthority.
    // New shared-state code should use AuthoritySource.Current (leases / claims / events) instead.
    public static class NetRoute
    {
        static INetRouteServer s_server;

        internal static void UseServer(INetRouteServer server) { s_server = server; }

        public static bool ServerMode { get { return s_server != null; } }

        // T-111: the seat whose host-only messages (gate counts and fires, intro, wipe) everyone obeys. P2P: the host
        // is always seat 1. Server mode: the arbiter moves to the lowest online seat when P1 leaves, so a fixed
        // "Sender == 1" dropped every message of the new host.
        static readonly bool s_mutateSeatOne = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_HOST_SEAT_ONE") == "1";
        public static int HostSeat { get { return s_server != null && s_server.ArbiterSeat > 0 && !s_mutateSeatOne ? s_server.ArbiterSeat : 1; } }

        // Replaces NetPlay.IsHost wherever it means "I arbitrate this".
        public static bool IsArbiter { get { return s_server != null ? s_server.IsArbiter : NetPlay.IsHost; } }

        // Replaces NetPlay.SendTo(seat, ...).
        public static void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            if (s_server != null) s_server.SendTo(seat, type, reliable, body);
            else NetPlay.SendTo(seat, type, reliable, body);
        }

        // True when the P2P host must rebroadcast type-tables (NetPlay.Forward); false in server mode.
        public static bool HostRebroadcasts { get { return s_server == null && NetPlay.IsHost; } }
    }
}
