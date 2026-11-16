using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using OriCoop.Net;

namespace OriCoop.NetTest
{
    // Inspect and perturb the wire while retaining real loopback UDP sockets.
    internal sealed class InspectTransport : IDatagramTransport
    {
        private readonly UdpTransport inner = new UdpTransport(0);
        internal bool DropAccept, HoldData, DropAllData;
        internal byte[] AcceptPacket, DataPacket, RequestPacket;
        internal int Largest, DataPackets;
        public int LocalPort { get { return inner.LocalPort; } }
        public void SendTo(byte[] bytes, int offset, int length, EndPoint to)
        {
            Largest = Math.Max(Largest, length);
            byte[] copy = new byte[length]; Buffer.BlockCopy(bytes, offset, copy, 0, length);
            if (length >= 7 && copy[2] == 1) RequestPacket = copy;
            if (length >= 7 && copy[2] == 2)
            {
                AcceptPacket = copy;
                if (DropAccept) { DropAccept = false; return; }
            }
            if (length >= 19 && copy[2] == 4)
            {
                DataPacket = copy; DataPackets++;
                if (DropAllData) return;
                if (HoldData) { HoldData = false; return; }
            }
            inner.SendTo(bytes, offset, length, to);
        }
        internal void Inject(byte[] bytes, EndPoint to) { inner.SendTo(bytes, 0, bytes.Length, to); }
        public int Receive(byte[] buffer, out EndPoint from) { return inner.Receive(buffer, out from); }
        public void Dispose() { inner.Dispose(); }
    }

    internal static class ProtocolChecks
    {
        private static void Set(NetPeer peer, string name, object value)
        {
            typeof(NetPeer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(peer, value);
        }

        private static bool Connect(NetPeer host, NetPeer guest, EndPoint address)
        {
            host.Listen(); guest.Connect(address);
            return Program.Wait(2000, delegate { return host.State == PeerState.Connected && guest.State == PeerState.Connected; }, host, guest);
        }

        internal static void Run()
        {
            HandshakeAndSession();
            Malformed();
            Wrap();
            Limits();
            NoAnswer();
            Liveness();
            AckWindow();
        }

        private static void HandshakeAndSession()
        {
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint ha = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                EndPoint ga = new IPEndPoint(IPAddress.Loopback, gs.LocalPort);
                int hc = 0, gc = 0, dc = 0;
                host.Connected += delegate { hc++; }; guest.Connected += delegate { gc++; };
                guest.Disconnected += delegate(string reason) { dc++; };
                hs.DropAccept = true;
                bool ok = Connect(host, guest, ha) && hc == 1 && gc == 1;
                byte[] oldAccept = hs.AcceptPacket;
                byte[] data = Program.Payload(11, 2100);
                guest.SendReliable(data, 0, data.Length); guest.Flush();
                byte[] oldData = gs.DataPacket;
                hs.Inject(oldAccept, ga); hs.Inject(oldAccept, ga);
                bool valid = true; List<byte[]> received = new List<byte[]>();
                Program.Wait(1500, delegate
                {
                    received.AddRange(Program.Drain(host, true, ref valid));
                    return received.Count == 1 && guest.Stats.ReliablePending == 0;
                }, host, guest);
                ok = ok && valid && received.Count == 1 && Program.Equal(data, received[0]) && gc == 1;
                guest.Disconnect("restart");
                bool deferred = dc == 0;
                Program.Pump(10, host, guest);
                guest.Connect(ha);
                hs.Inject(oldAccept, ga); guest.Poll();
                bool ignoredAccept = guest.State == PeerState.Connecting;
                Program.Wait(1500, delegate { return guest.State == PeerState.Connected && hc == 2; }, host, guest);
                uint oldId = BitConverter.ToUInt32(oldAccept, 7), newId = BitConverter.ToUInt32(hs.AcceptPacket, 7);
                gs.Inject(oldData, ha); host.Poll();
                byte[] ignored; bool reliable;
                bool staleIgnored = !host.TryReceive(out ignored, out reliable);
                guest.SendReliable(data, 0, data.Length); received.Clear();
                Program.Wait(1500, delegate { received.AddRange(Program.Drain(host, true, ref valid)); return received.Count != 0; }, host, guest);
                Program.Check("k handshake/session", ok && deferred && ignoredAccept && oldId != newId && staleIgnored &&
                    received.Count == 1 && Program.Equal(data, received[0]) && hc == 2 && gc == 2 && dc == 1,
                    String.Format("lost accept recovered, events={0}/{1}/{2}, new session={3}, stale ignored={4}, events deferred={5}", hc, gc, dc, oldId != newId, staleIgnored && ignoredAccept, deferred));
            }
        }

        private static void Malformed()
        {
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint ha = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                bool ok = Connect(host, guest, ha);
                gs.HoldData = true;
                byte[] data = Program.Payload(9, 50);
                guest.SendReliable(data, 0, data.Length); guest.Flush();
                byte[] good = gs.DataPacket;
                byte[] malformed = new byte[good.Length + 1]; Buffer.BlockCopy(good, 0, malformed, 0, good.Length);
                malformed[malformed.Length - 1] = 255;
                gs.Inject(malformed, ha);
                gs.Inject(new byte[] { 0x43, 0x4F, 4 }, ha);
                gs.Inject(new byte[2000], ha);
                host.Poll();
                byte[] msg; bool reliable;
                ok = ok && !host.TryReceive(out msg, out reliable) && host.State == PeerState.Connected;
                gs.Inject(good, ha); gs.Inject(good, ha);
                bool valid = true; List<byte[]> received = new List<byte[]>();
                Program.Wait(1000, delegate { received.AddRange(Program.Drain(host, true, ref valid)); return received.Count != 0; }, host, guest);
                Program.Pump(20, host, guest); received.AddRange(Program.Drain(host, true, ref valid));
                Program.Check("l malformed/duplicate", ok && valid && received.Count == 1 && Program.Equal(received[0], data),
                    String.Format("3 malformed ignored; valid retransmit delivered={0}, exact={1}", received.Count, valid && received.Count == 1 && Program.Equal(received[0], data)));
            }
        }

        private static void Wrap()
        {
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                bool ok = Connect(host, guest, new IPEndPoint(IPAddress.Loopback, hs.LocalPort));
                // Seed just before rollover instead of spending time sending 65536 packets.
                Set(guest, "nextSequence", (ushort)65520); Set(host, "nextSequence", (ushort)65520);
                Set(host, "haveReceived", false); Set(guest, "haveReceived", false);
                Set(guest, "nextMessage", UInt32.MaxValue - 1); Set(host, "expectedMessage", UInt32.MaxValue - 1);
                guest.Sim.LagMs = host.Sim.LagMs = 5;
                guest.Sim.JitterMs = host.Sim.JitterMs = 15;
                guest.Sim.LossPercent = host.Sim.LossPercent = 10;
                guest.Sim.DupPercent = host.Sim.DupPercent = 10;
                for (int i = 0; i < 128; i++) { byte[] data = Program.Payload(i, 900); guest.SendReliable(data, 0, data.Length); }
                bool valid = true; List<byte[]> actual = new List<byte[]>();
                Program.Wait(5000, delegate
                {
                    actual.AddRange(Program.Drain(host, true, ref valid));
                    return actual.Count == 128 && guest.Stats.ReliablePending == 0;
                }, host, guest);
                Program.Pump(200, host, guest); actual.AddRange(Program.Drain(host, true, ref valid));
                for (int i = 0; i < actual.Count; i++) if (!Program.Equal(actual[i], Program.Payload(i, 900))) valid = false;
                Program.Check("m sequence/message wrap", ok && valid && actual.Count == 128 && guest.Stats.ReliablePending == 0 && gs.Largest <= 1200,
                    String.Format("ordered/exact={0}, received={1}/128, pending={2}, max packet={3}", valid, actual.Count, guest.Stats.ReliablePending, gs.Largest));
            }
        }

        private static void Limits()
        {
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                bool ok = Connect(host, guest, new IPEndPoint(IPAddress.Loopback, hs.LocalPort));
                byte[] maximum = Program.Payload(7, 4 * 1024 * 1024);
                bool reliableLimit = false, unreliableLimit = false;
                try { guest.SendReliable(new byte[maximum.Length + 1], 0, maximum.Length + 1); }
                catch (ArgumentOutOfRangeException) { reliableLimit = true; }
                try { guest.SendUnreliable(new byte[1179], 0, 1179); }
                catch (ArgumentOutOfRangeException) { unreliableLimit = true; }
                guest.SendReliable(maximum, 0, maximum.Length);
                guest.SendReliable(new byte[0], 0, 0);
                byte[] source = Program.Payload(55, 100), slice = new byte[70]; Buffer.BlockCopy(source, 13, slice, 0, 70);
                host.SendReliable(source, 13, 70); Array.Clear(source, 0, source.Length);
                bool valid = true, window = true;
                List<byte[]> forward = new List<byte[]>(), reverse = new List<byte[]>();
                Program.Wait(6000, delegate
                {
                    forward.AddRange(Program.Drain(host, true, ref valid)); reverse.AddRange(Program.Drain(guest, true, ref valid));
                    object flight = typeof(NetPeer).GetField("inFlight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(guest);
                    if (((System.Collections.ICollection)flight).Count > 256) window = false;
                    return forward.Count == 2 && reverse.Count == 1 && guest.Stats.ReliablePending == 0 && host.Stats.ReliablePending == 0;
                }, host, guest);
                byte[] plain = Program.Payload(88, 1178);
                guest.SendUnreliable(plain, 0, plain.Length); guest.SendUnreliable(new byte[0], 0, 0);
                List<byte[]> plainReceived = new List<byte[]>();
                Program.Wait(1000, delegate { plainReceived.AddRange(Program.Drain(host, false, ref valid)); return plainReceived.Count == 2; }, host, guest);
                bool plainOk = plainReceived.Count == 2 && Program.Equal(plainReceived[0], plain) && plainReceived[1].Length == 0;
                Program.Check("n limits/bidirectional", ok && valid && plainOk && reliableLimit && unreliableLimit && window &&
                    forward.Count == 2 && forward[0].Length == maximum.Length && Program.Equal(maximum, forward[0]) && forward[1].Length == 0 &&
                    reverse.Count == 1 && Program.Equal(reverse[0], slice) && gs.Largest <= 1200 && hs.Largest <= 1200,
                    String.Format("4MiB intact={0}, empty reliable={1}, reverse slice copied={2}, window<=256={3}, MTU={4}, oversize rejected={5}, max/empty unreliable={6}",
                        forward.Count > 0 && Program.Equal(maximum, forward[0]), forward.Count == 2 && forward[1].Length == 0,
                        reverse.Count == 1 && Program.Equal(reverse[0], slice), window, Math.Max(gs.Largest, hs.Largest), reliableLimit && unreliableLimit, plainOk));
            }
        }

        private static void Liveness()
        {
            bool flood, handshake;
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            using (UdpTransport third = new UdpTransport(0))
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint ha = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                Connect(host, guest, ha);
                host.TimeoutMs = guest.TimeoutMs = 500; host.KeepaliveMs = guest.KeepaliveMs = 50;
                byte[] request;
                using (MemoryStream stream = new MemoryStream())
                {
                    BinaryWriter writer = new BinaryWriter(stream);
                    writer.Write((ushort)0x4F43); writer.Write((byte)1); writer.Write(123456U);
                    writer.Write((ushort)1); writer.Write(123456U); request = stream.ToArray();
                }
                double end = guest.NowMs + 1100;
                while (guest.NowMs < end)
                {
                    third.SendTo(request, 0, request.Length, ha); Program.Pump(10, host, guest);
                }
                flood = host.State == PeerState.Connected && guest.State == PeerState.Connected;
            }
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint ha = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                Connect(host, guest, ha);
                host.TimeoutMs = guest.TimeoutMs = 500;
                hs.DropAllData = gs.DropAllData = true;
                double end = guest.NowMs + 1100;
                while (guest.NowMs < end)
                {
                    gs.Inject(gs.RequestPacket, ha); Program.Pump(20, host, guest);
                }
                handshake = host.State == PeerState.Connected && guest.State == PeerState.Connected;
            }
            Program.Check("p keepalive liveness", flood && handshake,
                String.Format("full-reject flood survives={0}, matching duplicate handshakes refresh={1}, duration=1100ms each, timeout=500ms", flood, handshake));
        }
        private static void AckWindow()
        {
            using (UdpTransport hs = new UdpTransport(0))
            using (UdpTransport gs = new UdpTransport(0))
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                bool ok = Connect(host, guest, new IPEndPoint(IPAddress.Loopback, hs.LocalPort));
                guest.Sim.LagMs = host.Sim.LagMs = 60;
                guest.Sim.JitterMs = host.Sim.JitterMs = 20;
                for (int i = 0; i < 500; i++) { byte[] data = Program.Payload(i, 900); guest.SendReliable(data, 0, data.Length); }
                bool valid = true; List<byte[]> actual = new List<byte[]>();
                Program.Wait(10000, delegate
                {
                    actual.AddRange(Program.Drain(host, true, ref valid));
                    return actual.Count == 500 && guest.Stats.ReliablePending == 0;
                }, host, guest);
                Program.Pump(600, host, guest);
                for (int i = 0; i < actual.Count; i++) if (!Program.Equal(actual[i], Program.Payload(i, 900))) valid = false;
                Program.Check("q ack window", ok && valid && actual.Count == 500 && guest.Stats.LossPercent <= 5,
                    String.Format("zero simulated loss, received={0}/500, reported loss={1:F1}%, resends={2}", actual.Count, guest.Stats.LossPercent, guest.Stats.ReliableResends));
            }
        }
        private static void NoAnswer()
        {
            using (UdpTransport sink = new UdpTransport(0))
            using (UdpTransport gs = new UdpTransport(0))
            {
                NetPeer guest = new NetPeer(gs); int events = 0;
                guest.Disconnected += delegate(string reason) { events++; };
                guest.Connect(new IPEndPoint(IPAddress.Loopback, sink.LocalPort));
                double start = guest.NowMs;
                Program.Wait(10500, delegate { return guest.State == PeerState.Disconnected; }, guest);
                double elapsed = guest.NowMs - start;
                Program.Check("o no answer", guest.DisconnectReason == "no answer" && events == 1 && elapsed >= 10000 && elapsed < 10500,
                    String.Format("reason={0}, events={1}, elapsed={2:F0}ms, requests={3}", guest.DisconnectReason, events, elapsed, guest.Stats.PacketsSent));
            }
        }
    }
}



