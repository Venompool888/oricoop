using System;
using System.IO;

namespace OriCoop.Seats
{
    public sealed class SeatStream { public int Seat; public byte[] Body; }
    // Msg 9: primary sender byte remains device-level; each length-delimited body is a legacy Frames body.
    public static class FrameStreams
    {
        public static byte[] Encode(int primary, int[] seats, byte[][] bodies)
        {
            if (primary < 1 || primary > 4 || seats.Length < 1 || seats.Length > 4 || seats.Length != bodies.Length)
                throw new ArgumentException("Invalid frame streams");
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter w = new BinaryWriter(ms); w.Write((byte)9); w.Write((byte)primary); w.Write((byte)seats.Length);
                int mask = 0;
                for (int i = 0; i < seats.Length; i++)
                {
                    if (seats[i] < 1 || seats[i] > 4 || (mask & (1 << seats[i])) != 0 || bodies[i].Length > 512)
                        throw new ArgumentException("Invalid seat stream");
                    mask |= 1 << seats[i]; w.Write((byte)seats[i]); w.Write((ushort)bodies[i].Length); w.Write(bodies[i]);
                }
                return ms.ToArray();
            }
        }
        public static SeatStream[] Decode(BinaryReader reader)
        {
            int count = reader.ReadByte(), mask = 0;
            if (count < 1 || count > 4) throw new InvalidDataException("Invalid stream count");
            SeatStream[] result = new SeatStream[count];
            for (int i = 0; i < count; i++)
            {
                int seat = reader.ReadByte(), size = reader.ReadUInt16();
                if (seat < 1 || seat > 4 || (mask & (1 << seat)) != 0 || size > 512)
                    throw new InvalidDataException("Invalid seat stream");
                mask |= 1 << seat;
                byte[] body = reader.ReadBytes(size);
                if (body.Length != size) throw new EndOfStreamException();
                result[i] = new SeatStream { Seat = seat, Body = body };
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing frame bytes");
            return result;
        }
    }
}
