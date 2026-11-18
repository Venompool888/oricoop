using System;
using System.Collections.Generic;
using System.Globalization;

namespace OriCoop.Net
{
    // T-067: UTC stamps for log lines and the wall-clock offset between two ends (Unity-free, unit tested).
    // Offset is measured NTP style from one request/reply: t0 local send, t1 remote receive, t2 remote send,
    // t3 local receive (all DateTime.UtcNow ticks of the clock that took them).
    //   rtt    = (t3 - t0) - (t2 - t1)
    //   offset = ((t1 - t0) + (t2 - t3)) / 2      = remote clock minus local clock
    // The sample with the smallest rtt is the most accurate (least queueing on either path).
    public static class ClockSync
    {
        public struct Sample
        {
            public double OffsetMs, RttMs;
            public bool Valid;
        }

        public static Sample Compute(long t0, long t1, long t2, long t3)
        {
            Sample s = new Sample();
            long rtt = (t3 - t0) - (t2 - t1);
            if (t3 < t0 || t2 < t1 || rtt < 0) return s;            // clock went backwards or a forged reply
            s.RttMs = rtt / (double)TimeSpan.TicksPerMillisecond;
            s.OffsetMs = ((t1 - t0) + (t2 - t3)) / 2.0 / TimeSpan.TicksPerMillisecond;
            s.Valid = true;
            return s;
        }

        // "2026-11-18T10:44:35.123Z"
        public static string Utc(DateTime utc)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        // The UTC stamp put in front of every OriCoop line in BepInEx's LogOutput.log.
        public static string Stamp(DateTime utc, object data)
        {
            return Utc(utc) + " " + (data == null ? "" : data.ToString());
        }

        // "+09:00", "-03:30", "+00:00"
        public static string FormatUtcOffset(TimeSpan offset)
        {
            int minutes = (int)Math.Round(offset.TotalMinutes);
            char sign = minutes < 0 ? '-' : '+';
            minutes = Math.Abs(minutes);
            return sign + (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        // Header written once at start: "clock: utc_offset=+09:00 local=2026-11-18T19:44:35.123 utc=2026-11-18T10:44:35.123Z"
        public static string Header(DateTime local, DateTime utc)
        {
            return "clock: utc_offset=" + FormatUtcOffset(local - utc)
                + " local=" + local.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) + " utc=" + Utc(utc);
        }

        // "clock: offset vs P2 = +12.3 ms (P2 clock minus ours; rtt 114.0 ms, 8 samples)"
        public static string OffsetLine(int seat, Sample best, int samples)
        {
            return "clock: offset vs P" + seat + " = " + Signed(best.OffsetMs) + " ms (P" + seat + " clock minus ours; rtt "
                + best.RttMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms, " + samples + " samples)";
        }

        static string Signed(double ms)
        {
            return (ms < 0 ? "-" : "+") + Math.Abs(ms).ToString("0.0", CultureInfo.InvariantCulture);
        }

        // Best (smallest rtt) sample per remote seat.
        public sealed class Table
        {
            readonly Dictionary<int, Sample> best = new Dictionary<int, Sample>();
            readonly Dictionary<int, int> counts = new Dictionary<int, int>();

            public bool Add(int seat, Sample s)
            {
                if (!s.Valid) return false;
                int n; counts.TryGetValue(seat, out n); counts[seat] = n + 1;
                Sample old;
                if (best.TryGetValue(seat, out old) && old.RttMs <= s.RttMs) return false;
                best[seat] = s; return true;
            }
            public bool TryBest(int seat, out Sample s) { return best.TryGetValue(seat, out s); }
            public int Count(int seat) { int n; counts.TryGetValue(seat, out n); return n; }
            public IEnumerable<int> Seats { get { return best.Keys; } }
            public void Clear() { best.Clear(); counts.Clear(); }
        }
    }
}
