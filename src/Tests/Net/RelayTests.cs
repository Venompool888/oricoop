using System;
using System.Net;
using System.Text;
using OriCoop.Net;

static partial class NetTests
{
    sealed class RelayRig
    {
        public static readonly string Code = new string('A', 6);
        public readonly FakeClock Clock = new FakeClock();
        public readonly FakeNetwork Network;
        public readonly FakeTransport Server, HostSocket, GuestSocket;
        public readonly RelayTransport Host, Guest;
        public byte[] HostRequest, JoinRequest;

        public RelayRig()
        {
            Network = new FakeNetwork(Clock);
            Server = Network.Open(12001); HostSocket = Network.Open(12002); GuestSocket = Network.Open(12003);
            Host = new RelayTransport(Server.Endpoint, true, null, HostSocket, delegate { return Clock.Seconds; });
            Host.ForceRelay = true;
            EndPoint from;
            HostRequest = ReceiveServer(1, out from);
            byte[] created = Frame(3, 13);
            Buffer.BlockCopy(HostRequest, 3, created, 3, 4); WriteCode(created, 7);
            Server.SendTo(created, 0, created.Length, from); Host.Update();
            Guest = new RelayTransport(Server.Endpoint, false, Code, GuestSocket, delegate { return Clock.Seconds; });
            Guest.ForceRelay = true;
            JoinRequest = ReceiveServer(2, out from);
            byte[] joined = PeerFrame(4, 2, HostSocket.Endpoint);
            byte[] peerJoined = PeerFrame(5, 2, GuestSocket.Endpoint);
            Server.SendTo(joined, 0, joined.Length, GuestSocket.Endpoint);
            Server.SendTo(peerJoined, 0, peerJoined.Length, HostSocket.Endpoint);
            Host.Update(); Guest.Update();
        }

        public byte[] ReceiveServer(byte op, out EndPoint from)
        {
            byte[] buffer = new byte[2048]; int size;
            while ((size = Server.Receive(buffer, out from)) > 0)
            {
                if (size < 3 || buffer[0] != 0x4f || buffer[1] != 0x53 || buffer[2] != op) continue;
                byte[] copy = new byte[size]; Buffer.BlockCopy(buffer, 0, copy, 0, size); return copy;
            }
            throw new Exception("missing relay operation " + op);
        }

        public void Forward()
        {
            byte[] buffer = new byte[2048]; EndPoint from; int size;
            while ((size = Server.Receive(buffer, out from)) > 0)
            {
                if (size < 12 || buffer[2] != 7) continue;
                bool guest = from.Equals(GuestSocket.Endpoint);
                int length = buffer[10] | (buffer[11] << 8);
                Check(length == size - 12 && buffer[9] == (guest ? 1 : 2), "invalid relay request");
                byte[] relayed = Frame(8, 6 + length);
                relayed[3] = (byte)(guest ? 2 : 1);
                relayed[4] = buffer[10]; relayed[5] = buffer[11];
                Buffer.BlockCopy(buffer, 12, relayed, 6, length);
                Server.SendTo(relayed, 0, relayed.Length, guest ? HostSocket.Endpoint : GuestSocket.Endpoint);
            }
        }
    }

    static byte[] Frame(byte op, int size)
    {
        byte[] packet = new byte[size]; packet[0] = 0x4f; packet[1] = 0x53; packet[2] = op; return packet;
    }
    static void WriteCode(byte[] packet, int offset)
    {
        Encoding.ASCII.GetBytes(RelayRig.Code, 0, 6, packet, offset);
    }
    static byte[] PeerFrame(byte op, byte seat, EndPoint endpoint)
    {
        byte[] packet = Frame(op, 16); WriteCode(packet, 3); packet[9] = seat;
        IPEndPoint ip = (IPEndPoint)endpoint; byte[] address = ip.Address.GetAddressBytes();
        Buffer.BlockCopy(address, 0, packet, 10, 4);
        packet[14] = (byte)ip.Port; packet[15] = (byte)(ip.Port >> 8); return packet;
    }
    static void CheckCode(byte[] packet, int offset)
    {
        Check(Encoding.ASCII.GetString(packet, offset, 6) == RelayRig.Code, "wrong room code");
    }

    static void RelayJoin()
    {
        RelayRig r = new RelayRig();
        Check(r.Host.Ready && r.Guest.Ready && r.Guest.Seat == 2, "join response not applied");
        Check(r.JoinRequest.Length == 20 && r.JoinRequest[2] == 2, "join frame length/op");
        CheckCode(r.JoinRequest, 3);
        Check(r.JoinRequest[9] == 0x49 && r.JoinRequest[10] == 1 && r.JoinRequest[19] == 0, "join identity missing");
        Check(((IPEndPoint)r.Host.RemoteEndPoint).Port == 2 && ((IPEndPoint)r.Guest.RemoteEndPoint).Port == 1, "virtual endpoints");
    }

    static void RelayPayload()
    {
        RelayRig r = new RelayRig();
        byte[] data = new byte[] { 99, 0, 3, 255, 7, 88 };
        r.Guest.SendTo(data, 1, 4, r.Guest.RemoteEndPoint);
        EndPoint from; byte[] packet = r.ReceiveServer(7, out from);
        Check(from.Equals(r.GuestSocket.Endpoint) && packet.Length == 16 && packet[9] == 1 && packet[10] == 4 && packet[11] == 0, "guest relay frame");
        CheckCode(packet, 3); Check(Same(new byte[] { packet[12], packet[13], packet[14], packet[15] }, new byte[] { 0, 3, 255, 7 }), "guest payload");
        byte[] relayed = Frame(8, 10); relayed[3] = 2; relayed[4] = 4; Buffer.BlockCopy(packet, 12, relayed, 6, 4);
        r.Server.SendTo(relayed, 0, relayed.Length, r.HostSocket.Endpoint);
        byte[] received = new byte[100]; int size = r.Host.Receive(received, out from);
        Check(size == 4 && ((IPEndPoint)from).Port == 2 && Same(new byte[] { received[0], received[1], received[2], received[3] }, new byte[] { 0, 3, 255, 7 }), "host relayed decode");
        r.Host.SendTo(data, 0, data.Length, r.Host.RemoteEndPoint);
        packet = r.ReceiveServer(7, out from);
        Check(from.Equals(r.HostSocket.Endpoint) && packet[9] == 2 && packet[10] == data.Length && packet[11] == 0, "host relay frame"); CheckCode(packet, 3);
        relayed = Frame(8, 6 + data.Length); relayed[3] = 1; relayed[4] = (byte)data.Length; Buffer.BlockCopy(packet, 12, relayed, 6, data.Length);
        r.Server.SendTo(relayed, 0, relayed.Length, r.GuestSocket.Endpoint);
        size = r.Guest.Receive(received, out from); byte[] result = new byte[size]; Buffer.BlockCopy(received, 0, result, 0, size);
        Check(((IPEndPoint)from).Port == 1 && Same(result, data), "guest relayed decode");
    }

    static void RelayIdentity()
    {
        RelayTransport.LocalSteamIdSource = delegate { return 0x123456789abcdef0UL; };
        RelayTransport.LocalNameSource = delegate { return new string('\u732b', 10) + "\ud83d\ude00"; };
        try
        {
            byte[] block = RelayTransport.BuildIdentity();
            Check(block[0] == 0x49 && block[1] == 1 && block[10] == 30 && block.Length == 41, "identity header or UTF-8 limit");
            ulong id = 0; for (int i = 7; i >= 0; i--) id = (id << 8) | block[2 + i];
            Check(id == 0x123456789abcdef0UL && Encoding.UTF8.GetString(block, 11, block[10]) == new string('\u732b', 10), "identity round trip");
        }
        finally { RelayTransport.LocalSteamIdSource = null; RelayTransport.LocalNameSource = null; }
    }

    static void RelayJoinFailure()
    {
        FakeClock clock = new FakeClock(); FakeNetwork network = new FakeNetwork(clock);
        FakeTransport server = network.Open(12101), socket = network.Open(12102);
        RelayTransport guest = new RelayTransport(server.Endpoint, false, RelayRig.Code, socket, delegate { return clock.Seconds; });
        byte[] input = new byte[100]; EndPoint from; Check(server.Receive(input, out from) > 0 && input[2] == 2, "join request missing");
        byte[] reason = Encoding.UTF8.GetBytes("room full"); byte[] fail = Frame(6, 4 + reason.Length);
        fail[3] = (byte)reason.Length; Buffer.BlockCopy(reason, 0, fail, 4, reason.Length);
        server.SendTo(fail, 0, fail.Length, from); guest.Update();
        Check(!guest.Ready && guest.Error == "room full", "join failure not decoded");
    }

    static void RelayTimers()
    {
        FakeClock clock = new FakeClock(); FakeNetwork network = new FakeNetwork(clock);
        FakeTransport server = network.Open(12201), socket = network.Open(12202);
        RelayTransport host = new RelayTransport(server.Endpoint, true, null, socket, delegate { return clock.Seconds; });
        byte[] buffer = new byte[100]; EndPoint from; int length = server.Receive(buffer, out from);
        byte[] initial = new byte[length]; Buffer.BlockCopy(buffer, 0, initial, 0, length);
        clock.Advance(499); host.Update(); Check(server.Receive(buffer, out from) == 0, "retry too early");
        clock.Advance(1); host.Update(); length = server.Receive(buffer, out from);
        byte[] retry = new byte[length]; Buffer.BlockCopy(buffer, 0, retry, 0, length);
        Check(Same(initial, retry), "retry framing changed");
        byte[] created = Frame(3, 13); Buffer.BlockCopy(initial, 3, created, 3, 4); WriteCode(created, 7);
        server.SendTo(created, 0, created.Length, socket.Endpoint); host.Update();
        length = server.Receive(buffer, out from); Check(length == 9 && buffer[2] == 9, "first keepalive"); CheckCode(buffer, 3);
        byte[] ack = Frame(13, 3); server.SendTo(ack, 0, ack.Length, socket.Endpoint); host.Update();
        clock.Advance(4999); host.Update(); Check(server.Receive(buffer, out from) == 0, "keepalive too early");
        clock.Advance(1); host.Update(); length = server.Receive(buffer, out from);
        Check(length == 9 && buffer[2] == 9, "keepalive missing"); CheckCode(buffer, 3);
    }

    static void RelayLeave()
    {
        RelayRig r = new RelayRig(); int events = 0; EndPoint departed = null;
        r.Host.PeerLeft += delegate(EndPoint peer) { events++; departed = peer; };
        r.Guest.Dispose(); EndPoint from;
        for (int i = 0; i < 3; i++)
        {
            byte[] leave = r.ReceiveServer(14, out from);
            Check(leave.Length == 9 && from.Equals(r.GuestSocket.Endpoint), "leave frame"); CheckCode(leave, 3);
        }
        byte[] left = Frame(10, 10); WriteCode(left, 3); left[9] = 2;
        r.Server.SendTo(left, 0, left.Length, r.HostSocket.Endpoint); r.Host.Update();
        Check(events == 1 && ((IPEndPoint)departed).Port == 2, "peer left event");
    }

    static void RelaySpoof()
    {
        RelayRig r = new RelayRig(); FakeTransport other = r.Network.Open(12300);
        byte[] injected = Frame(8, 7); injected[3] = 2; injected[4] = 1; injected[6] = 42;
        other.SendTo(injected, 0, injected.Length, r.HostSocket.Endpoint);
        byte[] buffer = new byte[100]; EndPoint from;
        Check(r.Host.Receive(buffer, out from) == 0, "accepted forged relay frame");
    }

    static void RelayDirect()
    {
        RelayRig r = new RelayRig(); r.Host.ForceRelay = false; r.Guest.ForceRelay = false;
        for (int i = 0; i < 5; i++) { r.Host.Update(); r.Guest.Update(); r.Clock.Advance(10); }
        Check(r.Host.Direct && r.Guest.Direct, "punch/ack did not establish direct path");
        byte[] data = Payload(32, 7), buffer = new byte[100]; EndPoint from;
        r.Guest.SendTo(data, 0, data.Length, r.Guest.RemoteEndPoint);
        int size = r.Host.Receive(buffer, out from); byte[] result = new byte[size]; Buffer.BlockCopy(buffer, 0, result, 0, size);
        Check(Same(result, data) && ((IPEndPoint)from).Port == 2, "direct frame did not round trip");
    }

    static void RelayHubRouting()
    {
        RelayRig r = new RelayRig(); RelayHub hub = new RelayHub(r.Host);
        NetPeer client = new NetPeer(r.Guest, delegate { return r.Clock.Milliseconds; });
        NetPeer hostPeer = null; int connected = 0, disconnected = 0;
        hub.Connected += delegate(NetPeer peer) { hostPeer = peer; connected++; };
        hub.Disconnected += delegate(NetPeer peer, string reason) { if (reason == "relay peer left") disconnected++; };
        client.Connect(r.Guest.RemoteEndPoint);
        for (int i = 0; i < 20 && client.State != PeerState.Connected; i++)
        { client.Poll(); r.Forward(); hub.Poll(); r.Forward(); r.Clock.Advance(10); }
        Check(client.State == PeerState.Connected && hostPeer != null && connected == 1, "relay hub handshake");
        byte[] message = Payload(80, 33); Reliable(client, message);
        for (int i = 0; i < 10; i++) { client.Poll(); r.Forward(); hub.Poll(); r.Forward(); r.Clock.Advance(10); }
        ExpectMessage(hostPeer, message, true);
        byte[] left = Frame(10, 10); WriteCode(left, 3); left[9] = 2;
        r.Server.SendTo(left, 0, left.Length, r.HostSocket.Endpoint); hub.Poll();
        Check(disconnected == 1, "relay hub did not remove departed peer"); hub.Dispose();
    }
}
