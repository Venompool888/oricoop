using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using OriCoop.Net;

namespace OriCoop.NetTest
{
    internal static class Program
    {
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint timeEndPeriod(uint period);
        private static int checks, passed;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        internal static void Check(string name, bool ok, string detail)
        {
            checks++;
            if (ok) passed++;
            Console.WriteLine("{0} {1}: {2}", ok ? "PASS" : "FAIL", name, detail);
        }

        internal static void Pump(int ms, params NetPeer[] peers)
        {
            double end = Clock.Elapsed.TotalMilliseconds + ms;
            do
            {
                foreach (NetPeer peer in peers) peer.Poll();
                Thread.Sleep(1);
            } while (Clock.Elapsed.TotalMilliseconds < end);
        }

        internal static bool Wait(int ms, Func<bool> done, params NetPeer[] peers)
        {
            double end = Clock.Elapsed.TotalMilliseconds + ms;
            while (!done() && Clock.Elapsed.TotalMilliseconds < end) Pump(1, peers);
            return done();
        }

        internal static byte[] Payload(int id, int size)
        {
            byte[] bytes = new byte[size];
            uint value = (uint)id + 12345U;
            for (int i = 0; i < bytes.Length; i++)
            {
                value = value * 1664525U + 1013904223U;
                bytes[i] = (byte)(value >> 24);
            }
            return bytes;
        }

        internal static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        internal static List<byte[]> Drain(NetPeer peer, bool expectedReliable, ref bool valid)
        {
            List<byte[]> result = new List<byte[]>();
            byte[] data; bool reliable;
            while (peer.TryReceive(out data, out reliable))
            {
                if (reliable != expectedReliable) valid = false;
                result.Add(data);
            }
            return result;
        }

        private static void Profile(NetPeer peer, int lag, int jitter, float loss, float dup)
        {
            peer.Sim.LagMs = lag; peer.Sim.JitterMs = jitter;
            peer.Sim.LossPercent = loss; peer.Sim.DupPercent = dup;
        }

        private static void Run()
        {
            using (UdpTransport hs = new UdpTransport(0))
            using (UdpTransport gs = new UdpTransport(0))
            {
                NetPeer host = new NetPeer(hs), guest = new NetPeer(gs);
                EndPoint address = new IPEndPoint(IPAddress.Loopback, hs.LocalPort);
                int hc = 0, gc = 0; string reason = null;
                host.Connected += delegate { hc++; };
                guest.Connected += delegate { gc++; };
                host.Disconnected += delegate(string r) { reason = r; };
                host.Listen(); guest.Connect(address);
                bool connected = Wait(2000, delegate { return hc == 1 && gc == 1; }, host, guest);
                Pump(300, host, guest);
                Check("a connect", connected && hc == 1 && gc == 1, String.Format("events host={0}, guest={1}", hc, gc));

                using (UdpTransport bs = new UdpTransport(0))
                using (UdpTransport vs = new UdpTransport(0))
                {
                    NetPeer vh = new NetPeer(vs), bad = new NetPeer(bs);
                    vh.Listen(); bad.ProtocolVersionOverride = 2;
                    bad.Connect(new IPEndPoint(IPAddress.Loopback, vs.LocalPort));
                    Wait(1500, delegate { return bad.State == PeerState.Disconnected; }, vh, bad, host, guest);
                    Check("b version", bad.DisconnectReason != null && bad.DisconnectReason.IndexOf("version") >= 0, bad.DisconnectReason ?? "no rejection");
                }
                using (UdpTransport ts = new UdpTransport(0))
                {
                    NetPeer third = new NetPeer(ts); third.Connect(address);
                    Wait(1500, delegate { return third.State == PeerState.Disconnected; }, host, guest, third);
                    Check("c full", third.DisconnectReason == "full", third.DisconnectReason ?? "no rejection");
                }
                bool valid = true; int clean = 0; HashSet<int> cleanIds = new HashSet<int>();
                for (int i = 0; i < 1000; i++)
                {
                    byte[] data = Payload(i, 64); Buffer.BlockCopy(BitConverter.GetBytes(i), 0, data, 0, 4); guest.SendUnreliable(data, 0, data.Length);
                    if (i % 20 == 19) Pump(1, guest, host);
                }
                Pump(300, host, guest);
                foreach (byte[] data in Drain(host, false, ref valid))
                {
                    int id = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : -1;
                    if (id < 0 || id >= 1000 || !cleanIds.Add(id)) { valid = false; continue; }
                    byte[] correct = Payload(id, 64); Buffer.BlockCopy(BitConverter.GetBytes(id), 0, correct, 0, 4);
                    if (!Equal(data, correct)) valid = false;
                    clean++;
                }
                Check("d clean unreliable", clean >= 990 && valid && guest.Stats.RttMs < 20,
                    String.Format("received={0}/1000, RTT={1:F1}ms", clean, guest.Stats.RttMs));

                Profile(host, 60, 20, 20, 5); Profile(guest, 60, 20, 20, 5);
                for (int i = 0; i < 180; i++)
                {
                    byte[] data = Payload(i, 8);
                    guest.SendUnreliable(data, 0, data.Length); host.SendUnreliable(data, 0, data.Length);
                    Pump(10, host, guest);
                }
                Pump(600, host, guest); Drain(host, false, ref valid); Drain(guest, false, ref valid);
                List<byte[]> expected = new List<byte[]>(), actual = new List<byte[]>();
                for (int i = 0; i < 500; i++)
                {
                    byte[] data = Payload(i, 1 + (i * 977) % 3000);
                    expected.Add(data); guest.SendReliable(data, 0, data.Length);
                }
                valid = true; double start = Clock.Elapsed.TotalMilliseconds;
                Wait(25000, delegate
                {
                    actual.AddRange(Drain(host, true, ref valid));
                    return actual.Count >= 500 && guest.Stats.ReliablePending == 0;
                }, host, guest);
                Pump(600, host, guest); actual.AddRange(Drain(host, true, ref valid));
                for (int i = 0; i < Math.Min(actual.Count, expected.Count); i++) if (!Equal(actual[i], expected[i])) valid = false;
                float loss = guest.Stats.LossPercent, rtt = guest.Stats.RttMs;
                Check("e lossy reliable", actual.Count == 500 && valid && guest.Stats.ReliablePending == 0 && rtt >= 120 && rtt <= 220 && loss >= 10 && loss <= 30,
                    String.Format("received={0}/500, ordered/exact={1}, RTT={2:F1}ms, loss={3:F1}%, resends={4}, elapsed={5:F0}ms",
                        actual.Count, valid, rtt, loss, guest.Stats.ReliableResends, Clock.Elapsed.TotalMilliseconds - start));

                Profile(host, 60, 20, 10, 5); Profile(guest, 60, 20, 10, 5);
                byte[] large = Payload(999, 300 * 1024); guest.SendReliable(large, 0, large.Length);
                actual.Clear(); valid = true; start = Clock.Elapsed.TotalMilliseconds;
                Wait(15000, delegate
                {
                    actual.AddRange(Drain(host, true, ref valid));
                    return actual.Count != 0 && guest.Stats.ReliablePending == 0;
                }, host, guest);
                Check("f 300KB", actual.Count == 1 && valid && Equal(actual[0], large),
                    String.Format("received={0}, bytes={1}, intact={2}, elapsed={3:F0}ms", actual.Count, actual.Count == 0 ? 0 : actual[0].Length,
                        actual.Count == 1 && Equal(actual[0], large), Clock.Elapsed.TotalMilliseconds - start));

                Profile(host, 60, 20, 20, 0); Profile(guest, 60, 20, 20, 0); Pump(600, host, guest);
                int count = 0; HashSet<int> ids = new HashSet<int>(); valid = true;
                for (int i = 0; i < 2000; i++)
                {
                    byte[] data = Payload(i, 256); Buffer.BlockCopy(BitConverter.GetBytes(i), 0, data, 0, 4);
                    guest.SendUnreliable(data, 0, data.Length); guest.Flush(); Pump(1, host, guest);
                }
                Pump(700, host, guest);
                foreach (byte[] data in Drain(host, false, ref valid))
                {
                    int id = data.Length >= 4 ? BitConverter.ToInt32(data, 0) : -1;
                    if (id < 0 || id >= 2000 || !ids.Add(id)) { valid = false; continue; }
                    byte[] correct = Payload(id, 256); Buffer.BlockCopy(BitConverter.GetBytes(id), 0, correct, 0, 4);
                    if (!Equal(data, correct)) valid = false;
                    count++;
                }
                Check("g lossy unreliable", valid && count >= 1400 && count <= 1800,
                    String.Format("received={0}/2000 ({1:F1}%), intact/unique={2}", count, count / 20.0, valid));

                Profile(host, 0, 0, 0, 0); Profile(guest, 0, 0, 0, 0); Pump(700, host, guest);
                host.TimeoutMs = 2000; start = Clock.Elapsed.TotalMilliseconds;
                Wait(3000, delegate { return reason != null; }, host);
                double elapsed = Clock.Elapsed.TotalMilliseconds - start;
                Check("h timeout", reason == "timed out" && elapsed <= 3000 && host.Stats.LastReceiveAgeMs >= 2000,
                    String.Format("reason={0}, elapsed={1:F0}ms, receive age={2:F0}ms", reason, elapsed, host.Stats.LastReceiveAgeMs));
                using (UdpTransport fs = new UdpTransport(0))
                {
                    NetPeer fresh = new NetPeer(fs); fresh.Connect(address);
                    bool again = Wait(2000, delegate { return fresh.State == PeerState.Connected && hc == 2; }, host, fresh);
                    byte[] data = Payload(42, 1400); fresh.SendReliable(data, 0, data.Length);
                    actual.Clear(); valid = true;
                    Wait(2000, delegate { actual.AddRange(Drain(host, true, ref valid)); return actual.Count != 0; }, host, fresh);
                    Check("i reconnect", again && actual.Count == 1 && valid && Equal(actual[0], data),
                        String.Format("host connect events={0}, received={1}, bytes={2}", hc, actual.Count, actual.Count == 0 ? 0 : actual[0].Length));
                    reason = null; start = Clock.Elapsed.TotalMilliseconds; fresh.Disconnect("bye");
                    Wait(1000, delegate { return reason != null; }, host, fresh);
                    Check("j disconnect", reason != null && reason.IndexOf("bye") >= 0,
                        String.Format("reason={0}, elapsed={1:F0}ms", reason, Clock.Elapsed.TotalMilliseconds - start));
                }
            }
        }

        public static int Main(string[] args)
        {
            bool timer = timeBeginPeriod(1) == 0;
            try
            {
                if (args.Length != 0 && args[0] == "--rtt-variations") RttChecks.RunVariations();
                else if (args.Length != 0 && args[0] == "--rtt") RttChecks.Run();
                else
                {
                    if (args.Length == 0 || args[0] != "--edges") Run();
                    RttChecks.Run();
                    ProtocolChecks.Run();
                    LagFixChecks.Run();
                }
            }
            catch (Exception ex) { Check("unhandled exception", false, ex.ToString()); }
            finally { if (timer) timeEndPeriod(1); }
            Console.WriteLine("RESULT {0} {1}/{2}", passed == checks ? "PASS" : "FAIL", passed == checks ? passed : checks - passed, checks);
            return passed == checks ? 0 : 1;
        }
    }
}


