using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    // T-067: measures the wall-clock offset between this PC and each connected end, so logs and recordings from two
    // PCs line up. After a partner says hello, sends a burst of 8 requests (one every 0.25 s) and again every 5 min;
    // the reply with the smallest round trip wins. Writes "clock: offset vs Pn = ..." to LogOutput.log (UTC stamped)
    // and a "net clock.offset" trace event. Message 0x52 ClockSync (docs\net-msg-ids.md):
    //   request: [1][t0]                       reply: [2][requester seat][t0][t1][t2]    (DateTime.UtcNow ticks)
    // Timestamps are taken when the game loop handles the message, so each side adds up to one frame of error.
    public static class CoopClock
    {
        public const byte MsgClock = 0x52;
        const int Burst = 8;
        const float Interval = 0.25f, Repeat = 300f, Settle = 2f;
        const double ReportChangeMs = 20.0;

        static readonly ClockSync.Table s_table = new ClockSync.Table();
        static readonly Dictionary<int, double> s_reported = new Dictionary<int, double>();
        static bool s_registered, s_hadPartner, s_joined;
        static int s_left;
        static float s_nextSend, s_reportAt = -1f, s_nextBurst;

        public static void Tick()
        {
            if (!s_registered)
            {
                s_registered = true; NetPlay.On(MsgClock, OnMessage);
                // A third / fourth player (or a new PC on a freed seat) says hello while a partner is already here: it is
                // measured now, not at the next 5-minute burst, and a freed seat's old best sample is not kept for it.
                NetPlay.PlayerJoined += delegate(int seat) { s_joined = true; };
            }
            float now = Time.realtimeSinceStartup;
            bool partner = NetPlay.Connected && NetPlay.RemoteHello;
            if (partner && (!s_hadPartner || s_joined)) { s_table.Clear(); s_reported.Clear(); s_reportAt = -1f; StartBurst(now); }
            s_hadPartner = partner; s_joined = false;
            if (!partner) return;
            if (s_left > 0 && now >= s_nextSend)
            {
                s_left--; s_nextSend = now + Interval;
                long t0 = DateTime.UtcNow.Ticks;
                NetPlay.Send(MsgClock, false, delegate(BinaryWriter w) { w.Write((byte)1); w.Write(t0); });
                if (s_left == 0) s_reportAt = now + Settle;
            }
            if (s_reportAt >= 0f && now >= s_reportAt) { s_reportAt = -1f; Report(); }
            if (s_left == 0 && s_reportAt < 0f && now >= s_nextBurst) StartBurst(now);
        }

        static void StartBurst(float now) { s_left = Burst; s_nextSend = now; s_nextBurst = now + Repeat; }

        static void OnMessage(BinaryReader r)
        {
            long received = DateTime.UtcNow.Ticks;
            int sender = NetPlay.Sender;
            byte kind = r.ReadByte();
            if (kind == 1)
            {
                long t0 = r.ReadInt64();
                Action<BinaryWriter> body = delegate(BinaryWriter w)
                {
                    w.Write((byte)2); w.Write((byte)sender); w.Write(t0); w.Write(received); w.Write(DateTime.UtcNow.Ticks);
                };
                if (NetPlay.IsHost) NetPlay.SendTo(sender, MsgClock, false, body); else NetPlay.Send(MsgClock, false, body);
            }
            else if (kind == 2)
            {
                int requester = r.ReadByte();
                long t0 = r.ReadInt64(), t1 = r.ReadInt64(), t2 = r.ReadInt64();
                if (requester != Players.LocalSeat) return;
                s_table.Add(sender, ClockSync.Compute(t0, t1, t2, received));
            }
        }

        static void Report()
        {
            foreach (int seat in new List<int>(s_table.Seats))
            {
                ClockSync.Sample best;
                if (!s_table.TryBest(seat, out best)) continue;
                double last;
                if (s_reported.TryGetValue(seat, out last) && Math.Abs(last - best.OffsetMs) < ReportChangeMs) continue;
                s_reported[seat] = best.OffsetMs;
                Plugin.Log.LogInfo(ClockSync.OffsetLine(seat, best, s_table.Count(seat)));
                CoopTrace.Event("net", "clock.offset", "seat", seat, "offset_ms", best.OffsetMs, "rtt_ms", best.RttMs, "n", s_table.Count(seat));
            }
        }
    }
}
