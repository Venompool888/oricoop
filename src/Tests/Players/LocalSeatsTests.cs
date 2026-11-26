using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using OriCoop;
using OriCoop.Seats;

// Players.LocalSeats / IsLocal and SeatTable.MaskOf: same answers as the old List + ToArray versions, no garbage.
static class LocalSeatsTests
{
    static int failed;
    static void Case(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static string Show(int[] a) { return "{" + string.Join(",", Array.ConvertAll(a, x => x.ToString())) + "}"; }

    // GC.GetAllocatedBytesForCurrentThread exists on Mono and .NET Core, not on .NET Framework 3.5/4.x.
    static readonly MethodInfo AllocatedBytes = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
    static long Allocated() { return (long)AllocatedBytes.Invoke(null, null); }

    // The pre-change offline rule, kept here as the reference.
    static int[] OldOffline()
    {
        Puppet first = Players.First;
        if (first == null || first.Seat != 2 || first.Sein == null) return new int[] { 1 };
        if (Players.Remotes.Count == 1) return new int[] { 1, 2 };
        List<int> seats = new List<int> { 1, 2 };
        foreach (Puppet p in Players.Remotes)
            if (p.Seat > 2 && p.IsLocal && p.Sein != null) seats.Add(p.Seat);
        return seats.ToArray();
    }

    static void Same(int[] expected, int[] actual, string what)
    {
        Check(expected.Length == actual.Length, what + ": expected " + Show(expected) + ", got " + Show(actual));
        for (int i = 0; i < expected.Length; i++) Check(expected[i] == actual[i], what + ": expected " + Show(expected) + ", got " + Show(actual));
    }

    static Puppet P(int seat, bool local, bool sein) { return new Puppet { Seat = seat, IsLocal = local, Sein = sein ? new SeinCharacter() : null }; }

    public static bool Run()
    {
        bool online = NetPlay.Enabled;
        NetPlay.Enabled = false;

        Case("seat masks list their seats ascending, shared per set", delegate {
            Check(SeatMask.Seats(0).Length == 0, "empty set");
            Same(new[] { 1, 3, 4 }, SeatMask.Seats(SeatMask.Bit(4) | SeatMask.Bit(1) | SeatMask.Bit(3)), "1,3,4");
            Check(ReferenceEquals(SeatMask.Seats(SeatMask.Bit(2)), SeatMask.Seats(SeatMask.Bit(2))), "same array for the same set");
            Check(SeatMask.Bit(0) == 0 && SeatMask.Bit(5) == 0 && !SeatMask.Has(~0, 0) && !SeatMask.Has(~0, 5), "seats outside 1..4 are never in a set");
            Same(new[] { 0 }, SeatMask.Single(0), "single 0"); Same(new[] { 7 }, SeatMask.Single(7), "single 7");
        });

        Case("offline LocalSeats and IsLocal match the old rule for every puppet layout", delegate {
            Puppet[][] layouts = {
                new Puppet[0],
                new[] { P(2, true, true) },
                new[] { P(2, true, false) },
                new[] { P(3, true, true) },
                new[] { P(2, true, true), P(3, true, true) },
                new[] { P(2, true, true), P(3, false, true), P(4, true, true) },
                new[] { P(2, true, true), P(4, true, true), P(3, true, true) },
                new[] { P(2, true, true), P(3, true, false), P(4, true, true) },
            };
            foreach (Puppet[] layout in layouts)
            {
                Players.Remotes.Clear(); Players.Remotes.AddRange(layout);
                int[] expected = OldOffline();
                Array.Sort(expected);   // the old list followed Remotes order; ISeatView promises ascending
                Same(expected, Players.LocalSeats, "layout of " + layout.Length);
                for (int seat = -1; seat <= 6; seat++)
                    Check(Players.IsLocal(seat) == Array.IndexOf(expected, seat) >= 0, "IsLocal(" + seat + ") with " + layout.Length + " puppets");
            }
            Players.Remotes.Clear();
        });

        Case("online IsLocal asks NetPlay's seat table", delegate {
            NetPlay.Enabled = true; NetPlay.LocalSeats = new[] { 2, 4 };
            Check(Players.IsLocal(2) && Players.IsLocal(4) && !Players.IsLocal(1) && !Players.IsLocal(3), "seats 2 and 4");
            Check(ReferenceEquals(Players.LocalSeats, NetPlay.LocalSeats), "online LocalSeats is NetPlay's");
            NetPlay.Enabled = false;
        });

        Case("SeatTable.MaskOf holds exactly SeatsOf for every device", delegate {
            Random random = new Random(7);
            for (int round = 0; round < 500; round++)
            {
                SeatTable table = new SeatTable();
                for (int step = 0; step < 6; step++)
                {
                    int device = random.Next(1, 5);
                    if (random.Next(3) == 0) table.Release(device, random.Next(1, 5)); else table.Add(device);
                }
                for (int device = 0; device <= 5; device++)
                    Same(table.SeatsOf(device), SeatMask.Seats(table.MaskOf(device)), "device " + device);
            }
        });

        Case("reading LocalSeats and IsLocal allocates nothing", delegate {
            if (AllocatedBytes == null) { Console.WriteLine("  (no allocation counter on this runtime: skipped)"); return; }
            Players.Remotes.Clear(); Players.Remotes.Add(P(2, true, true)); Players.Remotes.Add(P(3, true, true)); Players.Remotes.Add(P(4, false, true));
            SeatTable table = new SeatTable(); table.Add(1); table.Add(1);
            int sink = 0;
            for (int i = 0; i < 10; i++) { sink += Players.LocalSeats.Length; if (Players.IsLocal(3)) sink++; sink += table.MaskOf(1); }   // warm up
            long before = Allocated();
            for (int i = 0; i < 10000; i++)
            {
                foreach (int seat in Players.LocalSeats) sink += seat;
                if (Players.IsLocal(i & 3)) sink++;
                sink += SeatMask.Seats(table.MaskOf(1)).Length;
            }
            long bytes = Allocated() - before;
            Players.Remotes.Clear();
            Check(bytes < 1024, "10000 reads allocated " + bytes + " bytes (sink " + sink + ")");
        });

        NetPlay.Enabled = online;
        Console.WriteLine("LOCAL SEATS " + (failed == 0 ? "PASS" : "FAIL"));
        return failed == 0;
    }
}
