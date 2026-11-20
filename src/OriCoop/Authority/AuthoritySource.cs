using System;
using System.Diagnostics;
using System.IO;

namespace OriCoop.Authority
{
    public static class AuthoritySource
    {
        static bool initialized;
        static HostArbiter host;
        static AuthorityClient guest, single;
        static IAuthority server;
        static Action<byte[]> serverReceive;
        static readonly Stopwatch clock = Stopwatch.StartNew();

        // T-168: committed events of whichever authority is current (P2P host, P2P guest, C-server, single player), so a
        // listener does not have to follow Current switching between them.
        public static event Action<CommittedEvent> AnyCommitted;
        static void Relay(CommittedEvent e) { Action<CommittedEvent> handler = AnyCommitted; if (handler != null) handler(e); }

        public static IAuthority Current
        {
            get
            {
                Initialize();
                if (server != null) return server;
                if (!NetPlay.Enabled) return single;
                if (NetPlay.IsHost) return GetHost().Local;
                return guest;
            }
        }

        static HostArbiter GetHost()
        {
            if (host == null)
            {
                host = new HostArbiter(delegate { return Players.LocalSeat; },
                    delegate(int seat, byte[] message, bool reliable)
                    {
                        NetRoute.SendTo(seat, message[0], reliable,
                            delegate(BinaryWriter writer) { writer.Write(message, 2, message.Length - 2); });
                    }, delegate { return clock.Elapsed.TotalMilliseconds; });
                host.Local.Committed += Relay;
                host.SeatJoined(Players.LocalSeat);
            }
            return host;
        }

        static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            single = new AuthorityClient(AuthorityMode.None, delegate { return Players.LocalSeat; }, null, false);
            guest = new AuthorityClient(AuthorityMode.HostP2P, delegate { return Players.LocalSeat; },
                delegate(byte[] message, bool reliable)
                {
                    NetPlay.Send(message[0], reliable,
                        delegate(BinaryWriter writer) { writer.Write(message, 2, message.Length - 2); });
                }, false);
            single.Committed += Relay; guest.Committed += Relay;
            NetPlay.PlayerJoined += delegate(int seat) { if (NetPlay.IsHost) GetHost().SeatJoined(seat); };
            NetPlay.PlayerLeft += delegate(int seat) { if (host != null && NetPlay.IsHost) host.SeatLeft(seat); };
            NetPlay.SessionStarted += delegate { if (NetPlay.IsHost) GetHost().SeatJoined(Players.LocalSeat); };
            NetPlay.SessionEnded += delegate
            {
                if (host != null) host.Reset();
                guest.ResetSession(); single.ResetSession();
            };
            for (byte type = CsWire.CsJoin; type <= CsWire.CsTo; type++)
            {
                byte registeredType = type;
                NetPlay.On(type, delegate(BinaryReader reader)
                {
                    using (MemoryStream stream = new MemoryStream())
                    {
                        BinaryWriter writer = new BinaryWriter(stream);
                        writer.Write(registeredType); writer.Write((byte)NetPlay.Sender);
                        writer.Write(reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position)));
                        byte[] message = stream.ToArray();
                        // Server mode first: seat 1 there also reports IsHost (legacy arbiter via HostOverride).
                        if (serverReceive != null) serverReceive(message);
                        else if (NetPlay.IsHost) GetHost().FromRemote(NetPlay.Sender, message);
                        else guest.Receive(message);
                    }
                });
            }
        }

        // C-server mode: Current returns the server-backed client; Cs messages go to receive. (null, null) = back to P2P.
        internal static void UseServer(IAuthority server, Action<byte[]> receive)
        {
            Initialize();
            if (AuthoritySource.server != null) AuthoritySource.server.Committed -= Relay;
            AuthoritySource.server = server; serverReceive = receive;
            if (server != null) server.Committed += Relay;
        }
    }
}
