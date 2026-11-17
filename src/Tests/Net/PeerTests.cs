using System;
using System.Collections.Generic;
using System.Net;
using OriCoop.Net;

static partial class NetTests
{
    sealed class PeerRig
    {
        public readonly FakeClock Clock = new FakeClock();
        public readonly FakeNetwork Network;
        public readonly FakeTransport HostTransport, GuestTransport;
        public readonly NetPeer Host, Guest;

        public PeerRig()
        {
            Network = new FakeNetwork(Clock);
            HostTransport = Network.Open(11001); GuestTransport = Network.Open(11002);
            Host = new NetPeer(HostTransport, delegate { return Clock.Milliseconds; });
            Guest = new NetPeer(GuestTransport, delegate { return Clock.Milliseconds; });
        }
        public void Start() { Host.Listen(); Guest.Connect(HostTransport.Endpoint); }
        public void Step()
        {
            Host.Poll(); Guest.Poll(); Clock.Advance(10);
        }
        public void Pump(int milliseconds)
        {
            for (int i = 0; i < milliseconds; i += 10) Step();
        }
        public void Connect()
        {
            Start();
            for (int i = 0; i < 2000 && (Host.State != PeerState.Connected || Guest.State != PeerState.Connected); i++) Step();
            Check(Host.State == PeerState.Connected && Guest.State == PeerState.Connected, "connection failed");
        }
    }

    static byte[] Payload(int size, int value)
    {
        byte[] bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(value + i * 17);
        return bytes;
    }
    static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static void Reliable(NetPeer peer, byte[] bytes) { peer.SendReliable(bytes, 0, bytes.Length); }
    static void Unreliable(NetPeer peer, byte[] bytes) { peer.SendUnreliable(bytes, 0, bytes.Length); }
    static void ExpectMessage(NetPeer peer, byte[] expected, bool expectedReliable)
    {
        byte[] bytes; bool reliable;
        Check(peer.TryReceive(out bytes, out reliable), "message missing");
        Check(reliable == expectedReliable && Same(bytes, expected), "wrong message or reliability");
    }
    static void ExpectEmpty(NetPeer peer)
    {
        byte[] bytes; bool reliable;
        Check(!peer.TryReceive(out bytes, out reliable), "unexpected message");
    }

    static void ConnectRetry()
    {
        PeerRig r = new PeerRig(); int requests = 0;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (p.Length == 13 && p[2] == 1) return ++requests == 1;
            return false;
        };
        r.Connect();
        Check(requests >= 2 && r.Network.Dropped == 1, "request was not retried");
        Check(r.Clock.Milliseconds >= 250, "retry sent too early");
    }

    static void PeerWireBytes()
    {
        PeerRig r = new PeerRig(); byte[] request = null, accept = null, data = null;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (p.Length >= 3 && p[2] == 1) request = p;
            if (p.Length >= 3 && p[2] == 2) accept = p;
            if (p.Length > 19 && p[2] == 4 && from.Equals(r.GuestTransport.Endpoint)) data = p;
            return false;
        };
        r.Connect();
        byte[] payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        Reliable(r.Guest, payload); r.Guest.Flush();
        Check(request != null && request.Length == 13 && request[0] == 0x43 && request[1] == 0x4f &&
            request[2] == 1 && request[7] == 1 && request[8] == 0, "request header/version");
        Check(accept != null && accept.Length == 11 && accept[0] == 0x43 && accept[1] == 0x4f && accept[2] == 2, "accept header");
        for (int i = 0; i < 4; i++)
        {
            Check(request[3 + i] == request[9 + i] && request[3 + i] == accept[3 + i], "handshake nonce");
            Check(data != null && data[3 + i] == accept[7 + i], "data session identifier");
        }
        Check(data.Length == 34 && data[0] == 0x43 && data[1] == 0x4f && data[2] == 4, "data header");
        Check(data[19] == 1 && data[20] == 0 && data[21] == 0 && data[22] == 0 && data[23] == 0 &&
            data[24] == 0 && data[25] == 0 && data[26] == 1 && data[27] == 0 && data[28] == 4 && data[29] == 0,
            "reliable fragment header");
        for (int i = 0; i < payload.Length; i++) Check(data[30 + i] == payload[i], "reliable payload byte");
    }

    static void ProtocolMismatch()
    {
        PeerRig r = new PeerRig();
        r.Guest.ProtocolVersionOverride = NetPeer.ProtocolVersion + 1;
        r.Start(); r.Pump(500);
        Check(r.Guest.State == PeerState.Disconnected, "guest was not rejected");
        Check(r.Guest.DisconnectReason.IndexOf("protocol version mismatch") >= 0, "wrong rejection reason");
        Check(r.Host.State == PeerState.Listening, "host stopped listening");
    }

    static void DuplicateHandshake()
    {
        PeerRig r = new PeerRig(); int hosts = 0, guests = 0;
        r.Network.DuplicatePercent = 100;
        r.Host.Connected += delegate { hosts++; }; r.Guest.Connected += delegate { guests++; };
        r.Connect(); r.Pump(500);
        Check(hosts == 1 && guests == 1 && r.Network.Duplicated > 0, "duplicate connection event");
        byte[] message = Payload(10, 7); Reliable(r.Guest, message); r.Pump(500);
        ExpectMessage(r.Host, message, true); ExpectEmpty(r.Host);
    }

    static void ReliableBothWays()
    {
        PeerRig r = new PeerRig(); r.Connect();
        byte[] a = Payload(31, 4), b = Payload(57, 9);
        Reliable(r.Host, a); Reliable(r.Guest, b); r.Pump(500);
        ExpectMessage(r.Guest, a, true); ExpectMessage(r.Host, b, true);
        Check(r.Host.Stats.ReliablePending == 0 && r.Guest.Stats.ReliablePending == 0, "acks did not clear pending");
    }

    static void ReliableFragmentation()
    {
        PeerRig r = new PeerRig(); r.Connect();
        byte[] message = Payload(5501, 23); Reliable(r.Guest, message); r.Pump(1000);
        ExpectMessage(r.Host, message, true); ExpectEmpty(r.Host);
        Check(r.Guest.Stats.ReliablePending == 0, "fragments were not acknowledged");
        int have, total; Check(!r.Host.IncomingProgress(out have, out total), "assembly remained after delivery");
    }

    static void ReliableOrdering()
    {
        PeerRig r = new PeerRig(); r.Connect(); bool dropped = false;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (!dropped && from.Equals(r.GuestTransport.Endpoint) && p.Length > 19 && p[2] == 4)
            { dropped = true; return true; }
            return false;
        };
        byte[] first = Payload(800, 1), second = Payload(800, 2);
        Reliable(r.Guest, first); Reliable(r.Guest, second);
        r.Guest.Flush(); r.Host.Poll();
        Check(dropped, "first message was not dropped"); ExpectEmpty(r.Host);
        r.Pump(1500); ExpectMessage(r.Host, first, true); ExpectMessage(r.Host, second, true); ExpectEmpty(r.Host);
    }

    static void ReliableChaos()
    {
        PeerRig r = new PeerRig(); r.Connect();
        r.Network.LossPercent = 20; r.Network.DelayMs = 15; r.Network.JitterMs = 95;
        for (int i = 0; i < 100; i++) Reliable(r.Guest, Payload(800, i));
        int received = 0;
        for (int i = 0; i < 6000 && (received < 100 || r.Guest.Stats.ReliablePending != 0); i++)
        {
            r.Step(); byte[] bytes; bool reliable;
            while (r.Host.TryReceive(out bytes, out reliable))
            {
                Check(received < 100 && reliable && Same(bytes, Payload(800, received)), "out of order at " + received);
                received++;
            }
        }
        Check(received == 100 && r.Guest.Stats.ReliablePending == 0, "lost messages or missing acks: " + received);
        Check(r.Network.Dropped > 0 && r.Network.Reordered > 0 && r.Guest.Stats.ReliableResends > 0, "chaos did not exercise loss, reordering and retries");
        r.Pump(1000); ExpectEmpty(r.Host);
    }

    static void ReliableDuplicates()
    {
        PeerRig r = new PeerRig(); r.Connect(); r.Network.DuplicatePercent = 100;
        byte[] message = Payload(4000, 8); Reliable(r.Guest, message); r.Pump(1000);
        ExpectMessage(r.Host, message, true); ExpectEmpty(r.Host);
        Check(r.Network.Duplicated > 0 && r.Guest.Stats.ReliablePending == 0, "duplicate scenario incomplete");
    }

    static void ReliableRetransmissionDuplicates()
    {
        PeerRig r = new PeerRig(); r.Connect();
        double releaseAt = r.Clock.Milliseconds + 300;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            return r.Clock.Milliseconds < releaseAt && from.Equals(r.HostTransport.Endpoint) &&
                p.Length == 19 && p[2] == 4;
        };
        byte[] message = Payload(300, 8); Reliable(r.Guest, message); r.Pump(1500);
        ExpectMessage(r.Host, message, true); ExpectEmpty(r.Host);
        Check(r.Network.Dropped > 0 && r.Guest.Stats.ReliableResends > 0 && r.Guest.Stats.ReliablePending == 0,
            "ack loss did not produce an acknowledged retransmission");
    }

    static void UnreliableDuplicates()
    {
        PeerRig r = new PeerRig(); r.Connect(); r.Network.DuplicatePercent = 100;
        byte[] message = Payload(32, 4); Unreliable(r.Guest, message); r.Pump(500);
        ExpectMessage(r.Host, message, false); ExpectEmpty(r.Host);
    }

    static void UnreliableLoss()
    {
        PeerRig r = new PeerRig(); r.Connect(); int dropped = 0;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (from.Equals(r.GuestTransport.Endpoint) && p.Length > 19 && p[2] == 4) { dropped++; return true; }
            return false;
        };
        Unreliable(r.Guest, Payload(45, 1)); r.Pump(2000);
        Check(dropped == 1, "unreliable payload was retried or not sent"); ExpectEmpty(r.Host);
        Check(r.Guest.Stats.ReliableResends == 0 && r.Guest.State == PeerState.Connected, "unexpected reliable retry or disconnect");
    }

    static void PayloadCopy()
    {
        PeerRig r = new PeerRig(); r.Connect();
        byte[] bytes = new byte[] { 1, 2, 3, 4, 5 };
        r.Guest.SendReliable(bytes, 1, 3); r.Guest.SendUnreliable(bytes, 2, 2);
        for (int i = 0; i < bytes.Length; i++) bytes[i] = 0;
        r.Pump(500);
        ExpectMessage(r.Host, new byte[] { 2, 3, 4 }, true);
        ExpectMessage(r.Host, new byte[] { 3, 4 }, false);
    }

    static void ConnectTimeout()
    {
        PeerRig r = new PeerRig(); int events = 0;
        r.Guest.Disconnected += delegate(string reason) { events++; };
        r.Guest.Connect(r.HostTransport.Endpoint); r.Pump(9990);
        Check(r.Guest.State == PeerState.Connecting, "connection timed out too early");
        r.Pump(30);
        Check(r.Guest.State == PeerState.Disconnected && r.Guest.DisconnectReason == "no answer" && events == 1, "wrong connection timeout");
    }

    static void ConnectedTimeout()
    {
        PeerRig r = new PeerRig(); r.Connect(); int hostEvents = 0, guestEvents = 0;
        r.Host.Disconnected += delegate(string reason) { if (reason == "timed out") hostEvents++; };
        r.Guest.Disconnected += delegate(string reason) { if (reason == "timed out") guestEvents++; };
        r.Host.TimeoutMs = r.Guest.TimeoutMs = 500; r.Network.LossPercent = 100;
        r.Pump(800);
        Check(r.Host.State == PeerState.Disconnected && r.Guest.State == PeerState.Disconnected, "silence did not disconnect");
        Check(hostEvents == 1 && guestEvents == 1, "wrong timeout event count");
    }

    static void Goodbye()
    {
        PeerRig r = new PeerRig(); r.Connect(); int events = 0; string reason = null;
        r.Host.Disconnected += delegate(string text) { events++; reason = text; };
        r.Guest.Disconnect("see you"); r.Pump(200);
        Check(r.Host.State == PeerState.Disconnected && r.Guest.State == PeerState.Disconnected, "goodbye did not disconnect");
        Check(events == 1 && reason == "see you" && r.Host.DisconnectReason == reason, "wrong goodbye event");
    }

    static void Statistics()
    {
        PeerRig r = new PeerRig(); r.Network.DelayMs = 40; r.Connect();
        for (int i = 0; i < 5; i++) { Reliable(r.Guest, Payload(60, i)); r.Pump(180); }
        NetStats s = r.Guest.Stats;
        Check(s.RttMs >= 70 && s.RttMs <= 300 && s.JitterMs >= 0 && s.JitterMs < 300, "RTT/jitter outside expected range");
        Check(s.LossPercent >= 0 && s.LossPercent <= 100 && s.PacketsSent > 0 && s.PacketsReceived > 0, "invalid packet stats");
        Check(s.SendPps > 0 && s.RecvPps > 0 && s.SendKBps > 0 && s.RecvKBps > 0 && s.LastReceiveAgeMs >= 0, "invalid traffic stats");
        Check(s.ReliablePending == 0, "pending never cleared");
    }

    static void LossStatistics()
    {
        PeerRig r = new PeerRig(); r.Connect(); bool dropped = false; float maxLoss = 0;
        r.Network.Drop = delegate(byte[] p, EndPoint from, EndPoint to)
        {
            if (!dropped && from.Equals(r.GuestTransport.Endpoint) && p.Length > 19 && p[2] == 4)
            { dropped = true; return true; }
            return false;
        };
        byte[] message = Payload(80, 5); Reliable(r.Guest, message);
        for (int i = 0; i < 300; i++) { r.Step(); maxLoss = Math.Max(maxLoss, r.Guest.Stats.LossPercent); }
        ExpectMessage(r.Host, message, true); ExpectEmpty(r.Host);
        Check(dropped && maxLoss > 0 && maxLoss <= 100 && r.Guest.Stats.ReliableResends > 0, "loss not reflected in stats");
    }
}
