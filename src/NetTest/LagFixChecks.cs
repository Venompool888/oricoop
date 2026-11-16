using System;
using System.Diagnostics;
using System.Net;
using OriCoop.Net;

namespace OriCoop.NetTest
{
    // Lag-test fixes ② (link timeout) and ④ (reliable resend timer), docs/lag-test-200-400.md.
    internal static class LagFixChecks
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        internal static void Run()
        {
            using (UdpTransport hs = new UdpTransport(0))
            using (UdpTransport gs = new UdpTransport(0))
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                host.Listen(); guest.Connect(new IPEndPoint(IPAddress.Loopback, hs.LocalPort));
                bool connected = Program.Wait(2000, delegate { return host.State == PeerState.Connected && guest.State == PeerState.Connected; }, host, guest);

                // ② A Wi-Fi hiccup of ~5-8 s must not drop the link: the default timeout is at least 10 s.
                Program.Check("t default timeout >= 10 s", host.TimeoutMs >= 10000 && guest.TimeoutMs >= 10000,
                    String.Format("host={0}ms guest={1}ms", host.TimeoutMs, guest.TimeoutMs));

                // ④ RTT ~400 ms (200 ms each way, no jitter). Drop the first send of one reliable message;
                // the resend must come about one RTT later, not two (old: 2xRTT+2xjitter ~ 800 ms).
                host.Sim.LagMs = 200; guest.Sim.LagMs = 200; host.Sim.JitterMs = guest.Sim.JitterMs = 0;
                double warm = Clock.Elapsed.TotalMilliseconds;
                while (Clock.Elapsed.TotalMilliseconds - warm < 4000)
                {
                    byte[] ping = Program.Payload(1, 8);
                    host.SendUnreliable(ping, 0, ping.Length); guest.SendUnreliable(ping, 0, ping.Length);
                    Program.Pump(16, host, guest);
                }
                bool valid = true; Program.Drain(host, false, ref valid); Program.Drain(guest, false, ref valid);
                float rtt = host.Stats.RttMs, jitter = host.Stats.JitterMs;
                long resends = host.Stats.ReliableResends;

                byte[] data = Program.Payload(77, 200);
                host.Sim.LossPercent = 100;
                host.SendReliable(data, 0, data.Length); host.Flush();
                host.Sim.LossPercent = 0;
                double sent = Clock.Elapsed.TotalMilliseconds; double arrived = -1;
                Program.Wait(4000, delegate
                {
                    byte[] got; bool reliable;
                    while (guest.TryReceive(out got, out reliable))
                        if (reliable && arrived < 0) arrived = Clock.Elapsed.TotalMilliseconds;
                    return arrived >= 0;
                }, host, guest);
                double delivery = arrived < 0 ? -1 : arrived - sent;
                // One-way 200 ms + resend wait. Old timer: ~200 + 800 = 1000 ms. New: ~200 + 420 = 620 ms.
                Program.Check("u resend after ~1xRTT", connected && arrived >= 0 && delivery < 800 && host.Stats.ReliableResends > resends,
                    String.Format("RTT={0:F0}ms jitter={1:F0} delivery={2:F0}ms resends={3}", rtt, jitter, delivery, host.Stats.ReliableResends - resends));
                host.Disconnect("done"); guest.Disconnect("done"); Program.Pump(100, host, guest);
            }
        }
    }
}
