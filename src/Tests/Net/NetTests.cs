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

    static void PairedLan()
    {
        byte[] key = new byte[16];
        new Random(7).NextBytes(key);
        FakeClock clock = new FakeClock();
        FakeNetwork network = new FakeNetwork(clock);
        FakeTransport receiver = network.Open(10001);
        using (PairedLanTransport host = new PairedLanTransport(receiver, key))
        using (PairedLanTransport guest = new PairedLanTransport(network.Open(10002), key))
        using (PairedLanTransport otherRoom = new PairedLanTransport(network.Open(10003), new byte[16]))
        {
            byte[] data = { 7, 8, 9 };
            byte[] buffer = new byte[16];
            EndPoint from;
            otherRoom.SendTo(data, 0, 3, receiver.Endpoint);
            guest.SendTo(data, 1, 2, receiver.Endpoint);
            Check(host.Receive(buffer, out from) == 2 && buffer[0] == 8 && buffer[1] == 9, "paired LAN failed to deliver the matching room's data");
        }
    }

    static int Main()
    {
        Run("handshake connects both peers", Handshake);
        Run("paired LAN delivers only matching room traffic", PairedLan);
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
        Run("deferred poll: ack rides with the reply", AckRidesWithReply);
        Run("deferred poll sends nothing until flushed", PollWithoutFlushSendsNothing);
        Run("deferred poll keeps reliable delivery", DeferredPollKeepsReliableFlow);
        return failures == 0 ? 0 : 1;
    }
}
