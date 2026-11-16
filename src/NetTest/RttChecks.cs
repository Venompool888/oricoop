using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using OriCoop.Net;

namespace OriCoop.NetTest
{
    internal static class RttChecks
    {
        internal static void Run()
        {
            GameTraffic(144, 60);
            GameTraffic(60, 144);
        }

        internal static void RunVariations()
        {
            GameTraffic(144, 60, "poll/flush first", true, true);
            GameTraffic(60, 144, "poll/flush first", true, true);
            GameTraffic(200, 60, "host data; guest ack-only", false, true, 1, 1);
            GameTraffic(60, 200, "guest data; host ack-only", false, true, 1, 2);
            GameTraffic(144, 60, "history overwrite + sequence wrap", false, false, 20, 3, true);
            GameTraffic(60, 144, "history overwrite + sequence wrap", true, true, 20, 3, true);
            LateAckBits();
        }

        private static void LateAckBits()
        {
            using (InspectTransport hs = new InspectTransport())
            using (InspectTransport gs = new InspectTransport())
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint ha = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                EndPoint ga = new IPEndPoint(IPAddress.Loopback, gs.LocalPort);
                host.Listen(); guest.Connect(ha);
                bool connected = Program.Wait(2000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);
                host.KeepaliveMs = guest.KeepaliveMs = 100000;
                Configure(host); Configure(guest);
                // Force delivery of just these two datagrams; retain simulated delay,
                // and withhold all returning ACKs until both are hundreds of ms old.
                host.Sim.LossPercent = guest.Sim.LossPercent = 0;
                host.Sim.DupPercent = guest.Sim.DupPercent = 0;
                hs.DropAllData = true;
                byte[] payload = Program.Payload(5, 130);
                guest.SendUnreliable(payload, 0, payload.Length); guest.Flush();
                System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Array sent = (Array)typeof(NetPeer).GetField("sent", flags).GetValue(guest);
                object first = sent.GetValue(1);
                double firstTime = (double)first.GetType().GetField("Time", flags).GetValue(first);
                Program.Pump(100, host, guest);
                guest.SendUnreliable(payload, 0, payload.Length); guest.Flush();
                object second = sent.GetValue(2);
                double secondTime = (double)second.GetType().GetField("Time", flags).GetValue(second);
                Program.Pump(450, host, guest);
                byte[] ack = hs.DataPacket;
                bool bits = ack != null && BitConverter.ToUInt16(ack, 9) == 2 && (BitConverter.ToUInt32(ack, 11) & 1) != 0;
                float before = guest.Stats.RttMs;
                double now = guest.NowMs;
                double expected = before + (now - secondTime - before) * 0.125;
                expected += (now - firstTime - expected) * 0.125;
                if (ack != null) hs.Inject(ack, ga);
                guest.Poll();
                float after = guest.Stats.RttMs;
                if (ack != null) { hs.Inject(ack, ga); hs.Inject(ack, ga); }
                guest.Poll();
                Program.Check("s late ackBits/duplicates", connected && bits && Math.Abs(after - expected) < 2 && guest.Stats.RttMs == after,
                    String.Format("ages={0:F1}/{1:F1}ms, RTT={2:F1}->{3:F1}ms, expected={4:F1}ms, bit acknowledged={5}, duplicates unchanged={6}",
                        now - firstTime, now - secondTime, before, after, expected, bits, guest.Stats.RttMs == after));
            }
        }

        private static void Configure(NetPeer peer)
        {
            peer.Sim.LagMs = 60; peer.Sim.JitterMs = 20;
            peer.Sim.LossPercent = 10; peer.Sim.DupPercent = 2;
        }

        private static void FixedUpdate(NetPeer peer, byte[] payload, int tick)
        {
            peer.SendUnreliable(payload, 0, payload.Length);
            if (tick % 60 == 0) peer.SendReliable(payload, 0, 16);
            peer.Flush();
        }

        private static void Update(NetPeer peer)
        {
            peer.Poll();
            byte[] message; bool reliable;
            while (peer.TryReceive(out message, out reliable)) { }
        }

        private static void GameTraffic(int hostHz, int guestHz, string mode = "", bool pollFirst = false, bool extraFlush = false, int packetsPerTick = 1, int senders = 3, bool wrap = false)
        {
            using (UdpTransport hs = new UdpTransport(0))
            using (UdpTransport gs = new UdpTransport(0))
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                host.Listen(); guest.Connect(new IPEndPoint(IPAddress.Loopback, hs.LocalPort));
                bool connected = Program.Wait(2000, delegate
                {
                    return host.State == PeerState.Connected && guest.State == PeerState.Connected;
                }, host, guest);
                // Enable both simulations together, as in the game after connecting.
                Configure(host); Configure(guest);
                if (wrap)
                {
                    System.Reflection.FieldInfo field = typeof(NetPeer).GetField("nextSequence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    field.SetValue(host, (ushort)60000); field.SetValue(guest, (ushort)60000);
                }
                byte[] payload = Program.Payload(130, 130);
                Stopwatch clock = Stopwatch.StartNew();
                double nextFixed = 0, nextHost = 0, nextGuest = 0;
                double hostTotal = 0, guestTotal = 0; int samples = 0, tick = 0;
                while (clock.Elapsed.TotalMilliseconds < 8000)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (pollFirst)
                    {
                        if (now >= nextGuest) { Update(guest); if (extraFlush) guest.Flush(); nextGuest += 1000.0 / guestHz; }
                        if (now >= nextHost) { Update(host); if (extraFlush) host.Flush(); nextHost += 1000.0 / hostHz; }
                    }
                    if (now >= nextFixed)
                    {
                        for (int packet = 0; packet < packetsPerTick; packet++)
                        {
                            if ((senders & 1) != 0) FixedUpdate(host, payload, tick);
                            if ((senders & 2) != 0) FixedUpdate(guest, payload, tick);
                        }
                        tick++;
                        nextFixed += 1000.0 / 60;
                        if (now >= 6000) { hostTotal += host.Stats.RttMs; guestTotal += guest.Stats.RttMs; samples++; }
                    }
                    if (!pollFirst)
                    {
                        if (now >= nextHost) { Update(host); if (extraFlush) host.Flush(); nextHost += 1000.0 / hostHz; }
                        if (now >= nextGuest) { Update(guest); if (extraFlush) guest.Flush(); nextGuest += 1000.0 / guestHz; }
                    }
                    Thread.Sleep(1);
                }
                bool historyWrapped = true;
                if (wrap)
                {
                    foreach (NetPeer peer in new NetPeer[] { host, guest })
                    {
                        System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                        historyWrapped &= (int)typeof(NetPeer).GetField("historyCount", flags).GetValue(peer) == 8192;
                        historyWrapped &= (ushort)typeof(NetPeer).GetField("nextSequence", flags).GetValue(peer) < 60000;
                    }
                }
                float hr = host.Stats.RttMs, gr = guest.Stats.RttMs;
                Program.Check("r game RTT " + hostHz + "/" + guestHz + " " + mode,
                    connected && historyWrapped && host.State == PeerState.Connected && guest.State == PeerState.Connected &&
                    ((senders & 1) == 0 || (hr >= 120 && hr <= 200)) &&
                    ((senders & 2) == 0 || (gr >= 120 && gr <= 200)) &&
                    (senders != 3 || Math.Max(hr, gr) <= 1.25f * Math.Min(hr, gr)),
                    String.Format("host={0:F1}ms, guest={1:F1}ms, tail mean={2:F1}/{3:F1}ms, ticks={4}, loss={5:F1}/{6:F1}%",
                        hr, gr, hostTotal / samples, guestTotal / samples, tick, host.Stats.LossPercent, guest.Stats.LossPercent));
            }
        }
    }
}
