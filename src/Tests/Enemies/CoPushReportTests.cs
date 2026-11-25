using System;
using System.IO;
using OriCoop.Authority;

static class CoPushReportTests
{
    static void Check(bool pass, string explanation) { if (!pass) throw new Exception(explanation); }
    static BinaryReader Tail(uint sequence, byte seats, params uint[] secondaryBirths)
    {
        MemoryStream bytes = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(bytes);
        writer.Write(sequence); writer.Write(seats);
        foreach (uint birth in secondaryBirths) writer.Write(birth);
        bytes.Position = 0;
        return new BinaryReader(bytes);
    }

    public static void Register(Action<string, Action> run)
    {
        run("stone push: different couch players hold or push independently of their PC's total", delegate {
            int[] dirs = { 0, 0, 1, -1, 1 };
            float[] times = { 0f, 0f, 1f, 1f, 0f };
            byte seats = CoPushReport.Pack(dirs, times, 1f, 0.15f);
            int device = CoPushRule.DeviceDir(dirs, times, 1f, 0.15f);
            CoPushReport report;
            Check(CoPushReport.TryRead(device, Tail(10, seats), 1f, new CoPushReport(), false, out report), "received the full snapshot");
            Check(report.DeviceDir == 0, "opposing players do not boost the PC");
            Check(report.Direction(2, 2, 1.1f, false) == 1 && report.Direction(3, 2, 1.1f, false) == -1, "both actors keep their own push directions");
            Check(report.Direction(4, 2, 1.1f, false) == 0 && report.Direction(3, 2, 1.31f, false) == 0, "old seat input and old reports expire");
        });
        run("stone push: stopped report wins over lagging and duplicate packets across the sequence wrap", delegate {
            CoPushReport first, stop, stale;
            Check(CoPushReport.TryRead(1, Tail(uint.MaxValue, 4), 1f, new CoPushReport(), false, out first), "push");
            Check(CoPushReport.TryRead(0, Tail(0, 0), 1.1f, first, false, out stop), "next sequence wraps to zero");
            Check(!CoPushReport.TryRead(1, Tail(uint.MaxValue, 4), 1.2f, stop, false, out stale), "older push ignored");
            Check(!CoPushReport.TryRead(0, Tail(0, 0), 1.3f, stop, false, out stale), "duplicate cannot refresh age");
            Check(stop.Direction(2, 2, 1.25f, false) == 0, "stop still clears the grab target");
        });
        run("stone push: secondary seat reuse retains the other pusher and rejects the departed Ori's newer packet", delegate {
            uint?[,] released = new uint?[5, 5];
            CoPushReport first, delayed, replacement;
            // P2 pushes right, its couch P3 pushes left.
            Check(CoPushReport.TryRead(0, Tail(10, 36, 4), 1f, new CoPushReport(), false, out first, 2, released), "two pushers initially cancel");
            released[2, 3] = first.Sequence; first.ForgetSeat(3);
            Check(first.DeviceDir == 1 && first.Direction(2, 2, 1f, false) == 1 && first.Direction(3, 2, 1f, false) == 0, "primary still boosts while the released seat has no pose");
            Check(CoPushReport.TryRead(0, Tail(11, 36, 4), 1.1f, first, false, out delayed, 2, released), "old occupant's delayed update also carries continuing primary input");
            Check(delayed.DeviceDir == 1 && delayed.Direction(3, 2, 1.1f, false) == 0, "late update cannot re-grab a recycled secondary");
            Check(CoPushReport.TryRead(0, Tail(14, 36, 12), 1.2f, delayed, false, out replacement, 2, released), "new occupant reports");
            Check(replacement.Direction(3, 2, 1.2f, false) == -1 && replacement.DeviceDir == 0, "new secondary can oppose its primary immediately");
        });
        run("stone push: old senders can still advertise their primary and newer senders keep their ordering", delegate {
            CoPushReport legacy, ordered, ignored;
            Check(CoPushReport.TryRead(-1, new BinaryReader(new MemoryStream()), 1f, new CoPushReport(), false, out legacy), "old message");
            Check(legacy.Direction(2, 2, 1f, false) == -1 && legacy.Direction(3, 2, 1f, false) == 0, "legacy has only the primary's direction");
            Check(CoPushReport.TryRead(1, Tail(1, 4), 1.1f, legacy, false, out ordered), "first extended message");
            Check(!CoPushReport.TryRead(-1, new BinaryReader(new MemoryStream()), 1.2f, ordered, false, out ignored), "late legacy copy ignored");
            Check(!CoPushReport.TryRead(-1, new BinaryReader(new MemoryStream(new byte[4])), 1.2f, ordered, false, out ignored), "incomplete tail ignored");
        });
    }
}
