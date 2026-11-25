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

        public static bool TryRead(int deviceDir, BinaryReader tail, float now, CoPushReport previous,
            bool arrivalOrder, out CoPushReport report)
        {
            report = new CoPushReport { DeviceDir = Math.Sign(deviceDir), At = now };
            long remaining = tail.BaseStream.Length - tail.BaseStream.Position;
            if (remaining == 0) return !previous.Ordered;
            if (remaining != sizeof(uint) + 1) return false;
            report.Sequence = tail.ReadUInt32(); report.Seats = tail.ReadByte(); report.Ordered = true;
            return arrivalOrder || !previous.Ordered || unchecked((int)(report.Sequence - previous.Sequence)) > 0;
        }
    }
}
