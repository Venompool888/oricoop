namespace OriCoop.Seats
{
    // A set of seats 1..4 packed into bits 1..4 of an int, and one shared ascending int[] per set.
    // Players.LocalSeats / NetPlay.LocalSeats are read 10-30 times a frame (revive, escape, gates, local players);
    // building a fresh List + ToArray each time was steady per-frame garbage on the game's non-generational GC.
    // The returned arrays are SHARED: callers only read them (enumerate, Length, IndexOf), never write into them.
    public static class SeatMask
    {
        static readonly int[][] s_sets = Build();
        static readonly int[][] s_single = { new int[] { 0 }, new int[] { 1 }, new int[] { 2 }, new int[] { 3 }, new int[] { 4 } };

        public static int Bit(int seat) { return seat >= 1 && seat <= 4 ? 1 << seat : 0; }
        public static bool Has(int mask, int seat) { return (mask & Bit(seat)) != 0; }

        // The seats of `mask`, ascending (empty for 0).
        public static int[] Seats(int mask) { return s_sets[mask & 0x1E]; }

        // { seat }, shared for 0..4 (NetPlay's "no table yet" fallback can be seat 0).
        public static int[] Single(int seat) { return seat >= 0 && seat <= 4 ? s_single[seat] : new int[] { seat }; }

        static int[][] Build()
        {
            int[][] sets = new int[32][];
            for (int mask = 0; mask < 32; mask++)
            {
                int n = 0;
                for (int seat = 1; seat <= 4; seat++) if ((mask & (1 << seat)) != 0) n++;
                int[] seats = new int[n];
                n = 0;
                for (int seat = 1; seat <= 4; seat++) if ((mask & (1 << seat)) != 0) seats[n++] = seat;
                sets[mask] = seats;
            }
            return sets;
        }
    }
}
