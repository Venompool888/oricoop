using System;
using System.IO;

namespace OriCoop.Authority
{
    internal struct CoPushReport
    {
        public int DeviceDir;
        public uint Sequence;
        public byte Seats;
        public bool Ordered;
        public float At;

        public static byte Pack(int[] directions, float[] last, float now, float window)
        {
            int bits = 0;
            for (int seat = 1; seat <= 4; seat++)
            {
                if (now - last[seat] >= window) continue;
                int code = directions[seat] > 0 ? 1 : directions[seat] < 0 ? 2 : 0;
                bits |= code << (2 * (seat - 1));
            }
            return (byte)bits;
        }

        public int Direction(int seat, int sender, float now, bool devicePose)
        {
            if (now - At > CoPushRule.Fresh || seat < 1 || seat > 4) return 0;
            if (!Ordered || devicePose) return seat == sender ? DeviceDir : 0;
            int code = (Seats >> (2 * (seat - 1))) & 3;
            return code == 1 ? 1 : code == 2 ? -1 : 0;
        }

        public void ForgetSeat(int seat)
        {
            int mask = 3 << (2 * (seat - 1));
            if ((Seats & mask) == 0) return;
            Seats = (byte)(Seats & ~mask);
            bool right = (Seats & 0x55) != 0, left = (Seats & 0xaa) != 0;
            DeviceDir = right == left ? 0 : right ? 1 : -1;
        }

        internal static bool Newer(uint value, uint previous) { return unchecked((int)(value - previous)) > 0; }

        public static bool TryRead(int deviceDir, BinaryReader tail, float now, CoPushReport previous,
            bool arrivalOrder, out CoPushReport report, int sender = 0, uint?[,] released = null)
        {
            report = new CoPushReport { DeviceDir = Math.Sign(deviceDir), At = now };
            long remaining = tail.BaseStream.Length - tail.BaseStream.Position;
            if (remaining == 0) return !previous.Ordered;
            if (remaining < sizeof(uint) + 1) return false;
            report.Sequence = tail.ReadUInt32(); report.Seats = tail.ReadByte(); report.Ordered = true;
            if (!arrivalOrder && previous.Ordered && !Newer(report.Sequence, previous.Sequence)) return false;
            // A secondary's birth is drawn from the same sender sequence as its reports. After a seat is
            // recycled, a still-in-flight report may have a newer packet number but an older player.
            bool births = tail.BaseStream.Position < tail.BaseStream.Length;
            for (int seat = 1; seat <= 4; seat++)
            {
                if (seat == sender || ((report.Seats >> (2 * (seat - 1))) & 3) == 0) continue;
                if (births && tail.BaseStream.Length - tail.BaseStream.Position < sizeof(uint)) return false;
                uint birth = births ? tail.ReadUInt32() : 0;
                uint? floor = released != null ? released[sender, seat] : null;
                if (floor.HasValue && (!births || !Newer(birth, floor.Value))) report.ForgetSeat(seat);
            }
            return tail.BaseStream.Position == tail.BaseStream.Length;
        }
    }
}
