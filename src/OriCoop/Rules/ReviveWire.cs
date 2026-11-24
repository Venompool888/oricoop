using System;
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
        public struct Marker { public float X, Y, Z; public Guid Scene; }
        public struct Link
        {
            public int Seat;
            public float X, Y, Z, Health;
            // A well or ordinary checkpoint may have its own position and no native flame.
            public bool MarkerKnown, OfferRejected;
            public Marker? Flame;
        }
        public const int LinkBytes = 17;
        const int MarkerBytes = 28;
        const byte HasFlame = 1, RejectedOffer = 2, UnknownMarker = 4;

        // A position, health or progress that is not a number came from a broken sender: the message is dropped (the bus
        // logs "bad message"), never applied (a NaN skull / teleport target, NaN health, a ring that never fills).
        static float Finite(BinaryReader r)
        {
            float v = r.ReadSingle();
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("revive message: value not finite");
            return v;
        }

        public static void WriteLink(BinaryWriter w, int seat, float x, float y, float z, float health)
        { w.Write((byte)seat); w.Write(x); w.Write(y); w.Write(z); w.Write(health); }
        public static void WriteLink(BinaryWriter w, Link l)
        {
            WriteLink(w, l.Seat, l.X, l.Y, l.Z, l.Health);
            if (!l.MarkerKnown && !l.OfferRejected) return;
            bool hasFlame = l.MarkerKnown && l.Flame.HasValue;
            w.Write((byte)((hasFlame ? HasFlame : 0) | (l.OfferRejected ? RejectedOffer : 0) | (l.MarkerKnown ? 0 : UnknownMarker)));
            if (hasFlame)
            {
                Marker m = l.Flame.Value;
                w.Write(m.X); w.Write(m.Y); w.Write(m.Z); w.Write(m.Scene.ToByteArray());
            }
        }
        public static Link ReadLink(BinaryReader r)
        {
            Link l = new Link { Seat = r.ReadByte(), X = Finite(r), Y = Finite(r), Z = Finite(r), Health = Finite(r) };
            long remaining = r.BaseStream.Length - r.BaseStream.Position;
            if (remaining != 1 && remaining != 1 + MarkerBytes) return l;
            byte flags = r.ReadByte();
            bool hasFlame = (flags & HasFlame) != 0;
            if (remaining != (hasFlame ? 1 + MarkerBytes : 1)) return l;
            l.OfferRejected = (flags & RejectedOffer) != 0;
            l.MarkerKnown = (flags & UnknownMarker) == 0;
            if (hasFlame) l.Flame = new Marker { X = Finite(r), Y = Finite(r), Z = Finite(r), Scene = new Guid(r.ReadBytes(16)) };
            return l;
        }

        public static void WritePosition(BinaryWriter w, int seat, float x, float y, float z)
        { w.Write((byte)seat); w.Write(x); w.Write(y); w.Write(z); }
        public static Position ReadPosition(BinaryReader r)
        {
            Position p; p.Seat = r.ReadByte(); p.X = Finite(r); p.Y = Finite(r); p.Z = Finite(r); return p;
        }
        public static void WriteRevive(BinaryWriter w, int seat, float health) { w.Write((byte)seat); w.Write(health); }
        public static Revive ReadRevive(BinaryReader r)
        {
            Revive p; p.Seat = r.ReadByte(); p.Health = Finite(r); return p;
        }
        public static void WriteProgress(BinaryWriter w, int seat, int rescuer, float progress, float given)
        { w.Write((byte)seat); w.Write((byte)rescuer); w.Write(progress); w.Write(given); }
        public static Progress ReadProgress(BinaryReader r)
        {
            Progress p; p.Seat = r.ReadByte(); p.Rescuer = r.ReadByte(); p.Value = Finite(r); p.Given = Finite(r); return p;
        }
    }
}
