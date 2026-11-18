using System;
using System.Globalization;
using System.Threading;
using OriCoop.Net;

// T-067 unit tests: UTC stamp format, local UTC offset format, NTP-style clock offset between two ends.
// Expected values are worked out by hand from the ticket (ISO 8601 UTC with ms and "Z", offset = remote clock minus
// local clock, smallest round trip wins), not taken from the code's output.
static class ClockTests
{
    static int s_failed, s_passed;
    const long Ms = TimeSpan.TicksPerMillisecond;

    static void Eq(string name, object expected, object actual)
    {
        if (Equals(expected, actual)) s_passed++;
        else { s_failed++; Console.WriteLine("FAIL " + name + ": expected <" + expected + "> got <" + actual + ">"); }
    }
    static void Near(string name, double expected, double actual)
    {
        if (Math.Abs(expected - actual) < 1e-6) s_passed++;
        else { s_failed++; Console.WriteLine("FAIL " + name + ": expected " + expected + " got " + actual); }
    }

    static int Main()
    {
        UtcFormat();
        Stamp();
        UtcOffsetFormat();
        HeaderLine();
        OffsetMath();
        InvalidSamples();
        BestSampleTable();
        OffsetLineFormat();
        Console.WriteLine("clock tests: " + s_passed + " passed, " + s_failed + " failed");
        return s_failed == 0 ? 0 : 1;
    }

    static void UtcFormat()
    {
        Eq("utc ms", "2026-11-18T10:44:35.123Z", ClockSync.Utc(new DateTime(2026, 11, 18, 10, 44, 35, 123, DateTimeKind.Utc)));
        Eq("utc zero ms padded", "2026-01-02T03:04:05.000Z", ClockSync.Utc(new DateTime(2026, 1, 2, 3, 4, 5, 0, DateTimeKind.Utc)));
        Eq("utc 999 ms", "2026-12-31T23:59:59.999Z", ClockSync.Utc(new DateTime(2026, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc)));
        // Sub-millisecond ticks are cut, not rounded up into the next second.
        Eq("utc truncates", "2026-12-31T23:59:59.999Z", ClockSync.Utc(new DateTime(2026, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9999)));
        // A friend's PC may run another culture (other calendar, other separators): the stamp must not change.
        CultureInfo saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            foreach (string c in new[] { "ar-SA", "th-TH", "fa-IR", "de-DE", "zh-CN" })
            {
                try { Thread.CurrentThread.CurrentCulture = new CultureInfo(c); } catch { continue; }
                Eq("utc culture " + c, "2026-11-18T10:44:35.123Z", ClockSync.Utc(new DateTime(2026, 11, 18, 10, 44, 35, 123, DateTimeKind.Utc)));
                Eq("offset culture " + c, "+09:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(9)));
            }
        }
        finally { Thread.CurrentThread.CurrentCulture = saved; }
    }

    static void Stamp()
    {
        DateTime t = new DateTime(2026, 11, 18, 10, 44, 35, 123, DateTimeKind.Utc);
        Eq("stamp text", "2026-11-18T10:44:35.123Z net: connected", ClockSync.Stamp(t, "net: connected"));
        Eq("stamp object", "2026-11-18T10:44:35.123Z 42", ClockSync.Stamp(t, 42));
        Eq("stamp null", "2026-11-18T10:44:35.123Z ", ClockSync.Stamp(t, null));
    }

    static void UtcOffsetFormat()
    {
        Eq("offset +9", "+09:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(9)));
        Eq("offset +8", "+08:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(8)));
        Eq("offset zero", "+00:00", ClockSync.FormatUtcOffset(TimeSpan.Zero));
        Eq("offset -3:30", "-03:30", ClockSync.FormatUtcOffset(new TimeSpan(-3, -30, 0)));
        Eq("offset +5:45", "+05:45", ClockSync.FormatUtcOffset(new TimeSpan(5, 45, 0)));
        Eq("offset -0:30 keeps sign", "-00:30", ClockSync.FormatUtcOffset(TimeSpan.FromMinutes(-30)));
        Eq("offset +14 max", "+14:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(14)));
        Eq("offset -12 min", "-12:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(-12)));
        // local - utc taken from two DateTime.Now/UtcNow reads can be a few ms off a whole minute.
        Eq("offset rounds", "+09:00", ClockSync.FormatUtcOffset(TimeSpan.FromHours(9) - TimeSpan.FromMilliseconds(3)));
    }

    static void HeaderLine()
    {
        DateTime utc = new DateTime(2026, 11, 18, 10, 44, 35, 123, DateTimeKind.Utc);
        Eq("header +9", "clock: utc_offset=+09:00 local=2026-11-18T19:44:35.123 utc=2026-11-18T10:44:35.123Z",
            ClockSync.Header(utc.AddHours(9), utc));
        Eq("header +8 crosses day", "clock: utc_offset=+08:00 local=2026-11-19T02:44:35.123 utc=2026-11-18T18:44:35.123Z",
            ClockSync.Header(utc.AddHours(16), utc.AddHours(8)));
    }

    static void OffsetMath()
    {
        // Remote clock 500 ms ahead, 50 ms each way, remote takes 10 ms to answer.
        ClockSync.Sample s = ClockSync.Compute(0, 550 * Ms, 560 * Ms, 110 * Ms);
        Eq("ahead valid", true, s.Valid);
        Near("ahead offset", 500, s.OffsetMs);
        Near("ahead rtt", 100, s.RttMs);
        // Remote clock 2 h behind (a PC whose clock is set wrong by two hours), 30 ms each way.
        long behind = -2L * 3600 * 1000;
        long t0 = 1000000 * Ms;
        s = ClockSync.Compute(t0, t0 + (30 + behind) * Ms, t0 + (30 + behind) * Ms, t0 + 60 * Ms);
        Near("behind offset", behind, s.OffsetMs);
        Near("behind rtt", 60, s.RttMs);
        // Same clocks: offset 0 whatever the delay, as long as it is symmetric.
        s = ClockSync.Compute(0, 120 * Ms, 125 * Ms, 245 * Ms);
        Near("same clock offset", 0, s.OffsetMs);
        Near("same clock rtt", 240, s.RttMs);
        // Asymmetric path (80 ms out, 20 ms back), same clocks: the error is (out - back) / 2 = 30 ms, within rtt / 2.
        s = ClockSync.Compute(0, 80 * Ms, 80 * Ms, 100 * Ms);
        Near("asymmetric offset", 30, s.OffsetMs);
        Eq("asymmetric error within rtt/2", true, Math.Abs(s.OffsetMs) <= s.RttMs / 2);
        // Zero delay, zero processing: rtt 0 is valid (LAN on one PC).
        s = ClockSync.Compute(5 * Ms, 5 * Ms, 5 * Ms, 5 * Ms);
        Eq("zero rtt valid", true, s.Valid);
        Near("zero rtt offset", 0, s.OffsetMs);
        // Symmetry: measuring from the other end gives the negated offset and the same rtt.
        // The ahead end (R = L + 500) asks at R=1000 (L=500); 50 ms out, answered at L=560, back at L=610 (R=1110).
        ClockSync.Sample a = ClockSync.Compute(0, 550 * Ms, 560 * Ms, 110 * Ms);
        ClockSync.Sample b = ClockSync.Compute(1000 * Ms, 550 * Ms, 560 * Ms, 1110 * Ms);
        Near("symmetric offsets", -a.OffsetMs, b.OffsetMs);
        Near("symmetric rtt", a.RttMs, b.RttMs);
    }

    static void InvalidSamples()
    {
        Eq("reply before request", false, ClockSync.Compute(100 * Ms, 50 * Ms, 60 * Ms, 90 * Ms).Valid);
        Eq("remote sent before it received", false, ClockSync.Compute(0, 60 * Ms, 50 * Ms, 100 * Ms).Valid);
        Eq("remote held it longer than the round trip", false, ClockSync.Compute(0, 10 * Ms, 200 * Ms, 100 * Ms).Valid);
    }

    static void BestSampleTable()
    {
        ClockSync.Table t = new ClockSync.Table();
        ClockSync.Sample bad = ClockSync.Compute(100 * Ms, 0, 0, 0);
        Eq("invalid not added", false, t.Add(2, bad));
        Eq("invalid not counted", 0, t.Count(2));
        Eq("first sample is best", true, t.Add(2, ClockSync.Compute(0, 550 * Ms, 560 * Ms, 210 * Ms)));   // rtt 200
        Eq("worse sample not best", false, t.Add(2, ClockSync.Compute(0, 600 * Ms, 600 * Ms, 300 * Ms)));    // rtt 300
        Eq("better sample wins", true, t.Add(2, ClockSync.Compute(0, 530 * Ms, 530 * Ms, 60 * Ms)));        // rtt 60, offset 500
        Eq("equal rtt keeps first", false, t.Add(2, ClockSync.Compute(0, 900 * Ms, 900 * Ms, 60 * Ms)));
        ClockSync.Sample best;
        Eq("best found", true, t.TryBest(2, out best));
        Near("best offset", 500, best.OffsetMs);
        Near("best rtt", 60, best.RttMs);
        Eq("count all valid", 4, t.Count(2));
        Eq("other seat empty", false, t.TryBest(3, out best));
        t.Add(3, ClockSync.Compute(0, 0, 0, 40 * Ms));
        Eq("seats independent", 1, t.Count(3));
        Eq("seat 2 unchanged", 4, t.Count(2));
        t.Clear();
        Eq("clear", false, t.TryBest(2, out best));
        Eq("clear count", 0, t.Count(2));
    }

    static void OffsetLineFormat()
    {
        ClockSync.Sample s = new ClockSync.Sample { OffsetMs = 12.34, RttMs = 114, Valid = true };
        Eq("offset line", "clock: offset vs P2 = +12.3 ms (P2 clock minus ours; rtt 114.0 ms, 8 samples)", ClockSync.OffsetLine(2, s, 8));
        s.OffsetMs = -7200001.25;
        Eq("offset line negative", "clock: offset vs P1 = -7200001.3 ms (P1 clock minus ours; rtt 114.0 ms, 3 samples)", ClockSync.OffsetLine(1, s, 3));
    }
}
