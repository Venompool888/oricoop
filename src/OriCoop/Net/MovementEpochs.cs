using System;
using System.IO;

namespace OriCoop.Net
{
    // The reliable Hello advertises one movement incarnation per locally owned seat. A couch player can
    // leave and another take their seat while the primary and its other streams keep playing.
    internal sealed class MovementEpochs
    {
        public const int Seats = 4;
        public readonly ulong[] Id = new ulong[Seats + 1];
        public bool Legacy { get; private set; }

        public bool SetLocal(int seat, bool owned)
        {
            if (owned == (Id[seat] != 0)) return false;
            if (!owned) Id[seat] = 0;
            else do { Id[seat] = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); } while (Id[seat] == 0);
            return true;
        }

        public void Write(BinaryWriter writer)
        {
            for (int seat = 1; seat <= Seats; seat++) writer.Write(Id[seat]);
        }

        public static MovementEpochs ReadHello(BinaryReader reader, string version)
        {
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            MovementEpochs epochs = new MovementEpochs();
            if (remaining == Seats * sizeof(ulong))
            {
                for (int seat = 1; seat <= Seats; seat++) epochs.Id[seat] = reader.ReadUInt64();
            }
            else if (remaining == 0)
            {
                const string prefix = "0.2.0-net";
                int protocol;
                epochs.Legacy = version.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(version.Substring(prefix.Length), out protocol) && protocol > 0 && protocol < 16;
            }
            return epochs;
        }

        // Called after the timestamp and count. The tail belongs to the whole redundancy batch. Check it
        // before mutating the play buffer, the last known position, or the jitter estimator.
        public static bool Accepts(BinaryReader reader, int count, ulong confirmed, bool legacy, bool acceptPrevious = false)
        {
            if (count < 1 || count > 4) return false;
            const int bytesPerFrame = 31;
            Stream stream = reader.BaseStream;
            long framesStart = stream.Position;
            long tail = framesStart + count * bytesPerFrame;
            if (legacy) return stream.Length == tail;
            if (confirmed == 0 || stream.Length != tail + sizeof(ulong)) return false;
            stream.Position = tail;
            ulong incoming = reader.ReadUInt64();
            stream.Position = framesStart;
            return incoming == confirmed || acceptPrevious && incoming != 0;
        }
    }
}
