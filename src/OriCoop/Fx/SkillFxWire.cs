using System.IO;

namespace OriCoop.Fx
{
    internal enum SkillFxKind : byte
    {
        ChargeStart, ChargeStop, ChargeBurst, SpiritFlame, StompLand, Dash, ChargeDashImpact, BashFrom, BashOff, BashRelease
    }

    internal struct SkillFxEvent
    {
        public byte Seat, Variant;
        public SkillFxKind Kind;
        public float X, Y, Z, A, B, C;

        const float MaxBashTrailScale = 12f;
        const float MaxDistance = 40f;

        static bool BashTrail(SkillFxKind kind) { return kind == SkillFxKind.BashFrom || kind == SkillFxKind.BashOff; }

        public bool IsNear(float x, float y, float z)
        {
            return Kind == SkillFxKind.ChargeStop || (Near(X - x, Y - y, Z - z)
                && (Kind != SkillFxKind.SpiritFlame || Near(A - x, B - y, C - z)));
        }

        static bool Near(float dx, float dy, float dz) { return dx * dx + dy * dy + dz * dz <= MaxDistance * MaxDistance; }

        public static void Write(BinaryWriter w, SkillFxEvent e)
        {
            w.Write(e.Seat); w.Write((byte)e.Kind); w.Write(e.Variant);
            w.Write(e.X); w.Write(e.Y); w.Write(e.Z);
            if (e.Kind == SkillFxKind.SpiritFlame || BashTrail(e.Kind)) { w.Write(e.A); w.Write(e.B); }
            if (e.Kind == SkillFxKind.SpiritFlame) w.Write(e.C);
        }

        public static SkillFxEvent Read(BinaryReader r)
        {
            SkillFxEvent e = new SkillFxEvent { Seat = r.ReadByte(), Kind = (SkillFxKind)r.ReadByte(), Variant = r.ReadByte() };
            if (e.Seat < 1 || e.Seat > 4 || e.Kind > SkillFxKind.BashRelease) throw new InvalidDataException("skill effect header");
            int maxVariant = e.Kind == SkillFxKind.SpiritFlame ? 255 : e.Kind == SkillFxKind.StompLand ? 1
                : e.Kind == SkillFxKind.ChargeStart || e.Kind == SkillFxKind.ChargeBurst || e.Kind == SkillFxKind.Dash ? 2 : 0;
            if (e.Variant > maxVariant) throw new InvalidDataException("skill effect variant");
            e.X = ReadFloat(r); e.Y = ReadFloat(r); e.Z = ReadFloat(r);
            if (e.Kind == SkillFxKind.SpiritFlame || BashTrail(e.Kind)) { e.A = ReadFloat(r); e.B = ReadFloat(r); }
            if (e.Kind == SkillFxKind.SpiritFlame) e.C = ReadFloat(r);
            else if (BashTrail(e.Kind) && (e.B < 0f || e.B > MaxBashTrailScale || e.A < -360f || e.A > 360f))
                throw new InvalidDataException("skill effect bash trail");
            return e;
        }

        static float ReadFloat(BinaryReader r)
        {
            float value = r.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("skill effect coordinate");
            return value;
        }
    }
}
