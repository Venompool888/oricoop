using System.IO;

namespace OriCoop.Rules
{
    public static class ReviveWire
    {
        public struct Position { public int Seat; public float X, Y, Z; }
        public struct Revive { public int Seat; public float Health; }
        public struct Progress { public int Seat, Rescuer; public float Value, Given; }
        // T-041 msg 69 RoomLink: the room's latest save point (saver seat, position) and the saver's health right after
        // saving. Host -> all: the room's save changed. Guest -> host: where the save point it is offering is.
        public struct Link { public int Seat; public float X, Y, Z, Health; }
        public const int LinkBytes = 17;

        public static void WriteLink(BinaryWriter w, int seat, float x, float y, float z, float health)
        { w.Write((byte)seat); w.Write(x); w.Write(y); w.Write(z); w.Write(health); }
        public static Link ReadLink(BinaryReader r)
        {
            Link l; l.Seat = r.ReadByte(); l.X = r.ReadSingle(); l.Y = r.ReadSingle(); l.Z = r.ReadSingle(); l.Health = r.ReadSingle(); return l;
        }

        public static void WritePosition(BinaryWriter w, int seat, float x, float y, float z)
        { w.Write((byte)seat); w.Write(x); w.Write(y); w.Write(z); }
        public static Position ReadPosition(BinaryReader r)
        {
            Position p; p.Seat = r.ReadByte(); p.X = r.ReadSingle(); p.Y = r.ReadSingle(); p.Z = r.ReadSingle(); return p;
        }
        public static void WriteRevive(BinaryWriter w, int seat, float health) { w.Write((byte)seat); w.Write(health); }
        public static Revive ReadRevive(BinaryReader r)
        {
            Revive p; p.Seat = r.ReadByte(); p.Health = r.ReadSingle(); return p;
        }
        public static void WriteProgress(BinaryWriter w, int seat, int rescuer, float progress, float given)
        { w.Write((byte)seat); w.Write((byte)rescuer); w.Write(progress); w.Write(given); }
        public static Progress ReadProgress(BinaryReader r)
        {
            Progress p; p.Seat = r.ReadByte(); p.Rescuer = r.ReadByte(); p.Value = r.ReadSingle(); p.Given = r.ReadSingle(); return p;
        }
    }
}
