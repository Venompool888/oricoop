using System.IO;

namespace OriCoop.Saves
{
    public struct SavePosition
    {
        public bool HasPosition;
        public float X, Y, Z;
    }

    public static class SaveWire
    {
        public const byte SaveMessage = 10, LoadedMessage = 11;
        // T-043: guest -> host, a save point the guest touched: [base timeline string][len][bytes].
        public const byte SaveOffer = 0x57;
        public const int MaximumSaveBytes = 4 * 1024 * 1024 - 64;

        public static void WriteSave(BinaryWriter w, bool hasPosition, float x, float y, float z, byte[] bytes)
        {
            w.Write(hasPosition);
            w.Write(x); w.Write(y); w.Write(z);
            w.Write(bytes.Length); w.Write(bytes);
        }

        public static SavePosition ReadPosition(BinaryReader r)
        {
            bool hasPosition = ReadHasPosition(r);
            SavePosition position = ReadCoordinates(r);
            position.HasPosition = hasPosition;
            return position;
        }

        public static bool ReadHasPosition(BinaryReader r) { return r.ReadBoolean(); }

        public static SavePosition ReadCoordinates(BinaryReader r)
        {
            SavePosition position = new SavePosition();
            position.X = r.ReadSingle(); position.Y = r.ReadSingle(); position.Z = r.ReadSingle();
            return position;
        }

        public static byte[] ReadSaveBytes(BinaryReader r)
        {
            int size = r.ReadInt32();
            if (size < 1 || size > MaximumSaveBytes || size != r.BaseStream.Length - r.BaseStream.Position)
                throw new InvalidDataException("invalid checkpoint size " + size);
            return r.ReadBytes(size);
        }

        public static void WriteLoaded(BinaryWriter w) { }
        public static void ReadLoaded(BinaryReader r) { }

        public static void WriteSaveOffer(BinaryWriter w, string timeline, byte[] bytes)
        {
            w.Write(timeline ?? "");
            w.Write(bytes.Length); w.Write(bytes);
        }

        public static byte[] ReadSaveOffer(BinaryReader r, out string timeline)
        {
            timeline = r.ReadString();
            return ReadSaveBytes(r);
        }
    }
}
