using System;

namespace OriCoop.Enemies
{
    // Hit, shot and effect ids are (seat, counter): the seat in the upper half, a counter in the lower.
    // T-208 (6): the counter used to start at 1 in every game. In a 3-4 player room a newly started game that took
    // over a seat then repeated the ids of the player who had that seat before; every PC still remembered those
    // (an enemy keeps the ids of the hits it took until it respawns), so the newcomer's first hits on an enemy the
    // earlier player had hit did nothing. Now every session starts its counter at a place drawn anew.
    public sealed class HitIds
    {
        uint counter;
        public HitIds(uint start) { counter = start; }
        public ulong Next(int seat) { return ((ulong)seat << 32) | ++counter; }
        public static uint Start(Guid entropy)
        {
            byte[] b = entropy.ToByteArray();
            return BitConverter.ToUInt32(b, 0) ^ BitConverter.ToUInt32(b, 4) ^ BitConverter.ToUInt32(b, 8) ^ BitConverter.ToUInt32(b, 12);
        }
    }
}
