using System;
using System.Net;
using System.Text;
using OriCoop.Net;

static partial class NetTests
{
    static int failures;

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    static void Handshake()
    {
        FakeClock clock = new FakeClock();
        FakeNetwork network = new FakeNetwork(clock);
        FakeTransport hostTransport = network.Open(10001), guestTransport = network.Open(10002);
        NetPeer host = new NetPeer(hostTransport, delegate { return clock.Milliseconds; });
        NetPeer guest = new NetPeer(guestTransport, delegate { return clock.Milliseconds; });
        int hostEvents = 0, guestEvents = 0;
        host.Connected += delegate { hostEvents++; };
        guest.Connected += delegate { guestEvents++; };
        host.Listen(); guest.Connect(hostTransport.Endpoint);
        for (int i = 0; i < 20; i++) { host.Poll(); guest.Poll(); clock.Advance(10); }
        Check(host.State == PeerState.Connected && guest.State == PeerState.Connected, "peers did not connect");
        Check(hostEvents == 1 && guestEvents == 1, "wrong connection event count");
        Check(host.Remote.Equals(guestTransport.Endpoint) && guest.Remote.Equals(hostTransport.Endpoint), "wrong peer endpoint");
    }

    static void RelayHostIdentity()
    {
        FakeClock clock = new FakeClock();
        FakeNetwork network = new FakeNetwork(clock);
        FakeTransport server = network.Open(10003), socket = network.Open(10004);
        RelayTransport.LocalSteamIdSource = delegate { return 0x0102030405060708UL; };
        RelayTransport.LocalNameSource = delegate { return "A\u732b"; };
        try
        {
            RelayTransport host = new RelayTransport(server.Endpoint, true, null, socket, delegate { return clock.Seconds; });
            byte[] packet = new byte[1024]; EndPoint from;
            int length = server.Receive(packet, out from);
            Check(length == 22 && packet[0] == 0x4f && packet[1] == 0x53 && packet[2] == 1, "host framing");
            Check(packet[7] == 0x49 && packet[8] == 1, "identity tag");
            for (int i = 0; i < 8; i++) Check(packet[9 + i] == 8 - i, "identity ID byte " + i);
            Check(packet[17] == 4 && Encoding.UTF8.GetString(packet, 18, 4) == "A\u732b", "identity name");
            byte[] created = new byte[13];
            created[0] = 0x4f; created[1] = 0x53; created[2] = 3;
            Buffer.BlockCopy(packet, 3, created, 3, 4);
            string code = new string('A', 6);
            Encoding.ASCII.GetBytes(code, 0, 6, created, 7);
            server.SendTo(created, 0, created.Length, from);
            host.Update();
            Check(host.Ready && host.RoomCode == code, "host response was not decoded");
            host.Dispose();
        }
        finally { RelayTransport.LocalSteamIdSource = null; RelayTransport.LocalNameSource = null; }
    }

    static int Main()
    {
        Run("handshake connects both peers", Handshake);
        Run("peer handshake and data wire bytes", PeerWireBytes);
        Run("connection request is retried after loss", ConnectRetry);
        Run("protocol version mismatch is rejected", ProtocolMismatch);
        Run("repeated handshake is delivered once", DuplicateHandshake);
        Run("reliable delivery in both directions", ReliableBothWays);
        Run("reliable fragmentation and reassembly", ReliableFragmentation);
        Run("reliable messages wait for earlier messages", ReliableOrdering);
        Run("reliable delivery under loss and reordering", ReliableChaos);
        Run("reliable packet duplicates are suppressed", ReliableDuplicates);
        Run("reliable retransmissions are delivered once", ReliableRetransmissionDuplicates);
        Run("unreliable packet duplicates are suppressed", UnreliableDuplicates);
        Run("unreliable loss does not retransmit", UnreliableLoss);
        Run("queued payload copies the requested slice", PayloadCopy);
        Run("unanswered connection times out", ConnectTimeout);
        Run("connected peers time out after silence", ConnectedTimeout);
        Run("goodbye carries the disconnect reason", Goodbye);
        Run("partner reconnecting from the same address ends the stale session at once", PartnerReconnects);
        Run("a different address is refused while connected", StrangerIsRefused);
        Run("RTT and traffic statistics are sane", Statistics);
        Run("packet loss is reflected in statistics", LossStatistics);
        Run("relay host identity and room creation", RelayHostIdentity);
        Run("relay join and peer joined frames", RelayJoin);
        Run("relay payload frames round trip in both directions", RelayPayload);
        Run("relay identity UTF-8 boundary round trip", RelayIdentity);
        Run("relay join failure frame", RelayJoinFailure);
        Run("relay retry and keepalive timing", RelayTimers);
        Run("relay leave and peer left frames", RelayLeave);
        Run("relay rejects frames from other endpoints", RelaySpoof);
        Run("relay direct punch and data frames", RelayDirect);
        Run("relay hub routes and removes peers", RelayHubRouting);
        return failures == 0 ? 0 : 1;
    }
}
