using System;
using System.IO;

namespace OriCoop.Enemies
{
    internal struct EnemyVector
    {
        internal float X, Y, Z;
        internal EnemyVector(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
    internal class EnemyState
    {
        internal uint Tick, Generation;
        internal EnemyVector Position;
        internal float VelocityX, VelocityY;
        internal bool Alive, Killed, NeedsRespawn, Left, DealerActive, TargetActive;
        internal float Health, Respawn;
        internal string StateName = "", Animation = "";
        internal float StateTime, AnimationTime;
        internal bool ShouldStomp, HasVisualAngle;
        internal float VisualAngle;
        internal byte[] Controller = new byte[0], TraceMovement = new byte[0];
        internal ulong[] Applied = new ulong[0];
    }
    internal sealed class EnemyDamage
    {
        internal float Amount, ForceX, ForceY;
        internal int Type;
        internal EnemyVector Position;
    }
    internal sealed class EnemyHit
    {
        internal uint Epoch, Generation;
        internal ulong Id;
        internal EnemyDamage Damage;
    }
    internal struct EnemyTransfer
    {
        internal byte Operation;
        internal uint Epoch;
        internal int Owner;
    }
    internal sealed class EnemyShot
    {
        internal uint Epoch;
        internal ulong Id;
        internal string Prefab;
        internal EnemyVector Position, Velocity;
        internal float Gravity, Lifetime, Damage;
        internal int Type;
    }
    internal static class EnemyWire
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static short Q(float v) { return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(v * 100.0))); }
        internal static void WriteVector(BinaryWriter w, EnemyVector v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        internal static EnemyVector ReadVector(BinaryReader r) { return new EnemyVector(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        internal static void WriteAuthority(BinaryWriter w, byte operation, uint epoch, int owner)
        { w.Write(operation); w.Write(epoch); w.Write((byte)owner); }
        internal static EnemyTransfer ReadAuthority(BinaryReader r)
        { EnemyTransfer t = new EnemyTransfer(); t.Operation = r.ReadByte(); t.Epoch = r.ReadUInt32(); t.Owner = r.ReadByte(); return t; }
        internal static void WriteHandoff(BinaryWriter w, uint epoch, int owner) { w.Write(epoch); w.Write((byte)owner); }
        internal static EnemyTransfer ReadHandoff(BinaryReader r)
        { EnemyTransfer t = new EnemyTransfer(); t.Epoch = r.ReadUInt32(); t.Owner = r.ReadByte(); return t; }
        internal static byte Flags(EnemyState s)
        { return (byte)((s.Alive ? 1 : 0) | (s.Killed ? 2 : 0) | (s.Left ? 4 : 0) | (s.NeedsRespawn ? 8 : 0) | (s.DealerActive ? 16 : 0) | (s.TargetActive ? 32 : 0)); }
        static void ReadFlags(EnemyState s, byte f)
        { s.Alive = (f & 1) != 0; s.Killed = (f & 2) != 0; s.Left = (f & 4) != 0; s.NeedsRespawn = (f & 8) != 0; s.DealerActive = (f & 16) != 0; s.TargetActive = (f & 32) != 0; }
        internal static void WriteState(BinaryWriter w, EnemyVector origin, EnemyState s, bool full, Func<string, ushort> symbol)
        {
            w.Write(s.Tick); w.Write(s.Generation);
            EnemyVector delta = new EnemyVector(s.Position.X - origin.X, s.Position.Y - origin.Y, s.Position.Z - origin.Z);
            bool wide = Math.Abs(delta.X) > 327 || Math.Abs(delta.Y) > 327 || Math.Abs(delta.Z) > 327 || Math.Abs(s.VelocityX) > 327 || Math.Abs(s.VelocityY) > 327;
            w.Write(wide);
            if (wide) { WriteVector(w, s.Position); w.Write(s.VelocityX); w.Write(s.VelocityY); }
            else { w.Write(Q(delta.X)); w.Write(Q(delta.Y)); w.Write(Q(delta.Z)); w.Write(Q(s.VelocityX)); w.Write(Q(s.VelocityY)); }
            w.Write(s.Health); w.Write(Flags(s));
            if (!full) { w.Write(symbol(s.StateName)); w.Write(symbol(s.Animation)); w.Write((ushort)(Math.Max(0, s.AnimationTime) * 1000f % 65536f)); }
            else
            {
                w.Write(s.Respawn); w.Write(s.StateName); w.Write(s.Animation); w.Write(s.StateTime); w.Write(s.AnimationTime);
                w.Write((ushort)s.Controller.Length); w.Write(s.Controller);
                w.Write((ushort)s.TraceMovement.Length); w.Write(s.TraceMovement);
                w.Write(s.Applied.Length); foreach (ulong id in s.Applied) w.Write(id);
                w.Write((byte)(s.ShouldStomp ? 1 : 0));
            }
            w.Write((byte)(s.HasVisualAngle ? 1 : 0)); if (s.HasVisualAngle) w.Write(s.VisualAngle);
        }
        internal static EnemyState ReadState(BinaryReader r, EnemyVector origin, bool full, Func<ushort, string> symbol)
        {
            EnemyState s = new EnemyState(); ReadState(r, origin, full, symbol, s); return s;
        }
        internal static void ReadState(BinaryReader r, EnemyVector origin, bool full, Func<ushort, string> symbol, EnemyState s)
        {
            s.Tick = r.ReadUInt32(); s.Generation = r.ReadUInt32(); bool wide = r.ReadBoolean();
            if (wide) { s.Position = ReadVector(r); s.VelocityX = r.ReadSingle(); s.VelocityY = r.ReadSingle(); }
            else { EnemyVector delta = new EnemyVector(r.ReadInt16() / 100f, r.ReadInt16() / 100f, r.ReadInt16() / 100f); s.Position = new EnemyVector(origin.X + delta.X, origin.Y + delta.Y, origin.Z + delta.Z); s.VelocityX = r.ReadInt16() / 100f; s.VelocityY = r.ReadInt16() / 100f; }
            s.Health = r.ReadSingle(); ReadFlags(s, r.ReadByte());
            if (!full) { s.StateName = symbol(r.ReadUInt16()); s.Animation = symbol(r.ReadUInt16()); s.AnimationTime = r.ReadUInt16() / 1000f; }
            else
            {
                s.Respawn = r.ReadSingle(); s.StateName = r.ReadString(); s.Animation = r.ReadString(); s.StateTime = r.ReadSingle(); s.AnimationTime = r.ReadSingle();
                int length = r.ReadUInt16(); if (length > 1024) throw new InvalidDataException("E3 controller too large");
                s.Controller = r.ReadBytes(length); if (s.Controller.Length != length) throw new EndOfStreamException();
                length = r.ReadUInt16(); if (length > 1024) throw new InvalidDataException("E3 movement too large");
                s.TraceMovement = r.ReadBytes(length); if (s.TraceMovement.Length != length) throw new EndOfStreamException();
                int hits = r.ReadInt32(); if (hits < 0 || hits > 65536) throw new InvalidDataException("E3 hit history too large");
                s.Applied = new ulong[hits]; for (int i = 0; i < hits; i++) s.Applied[i] = r.ReadUInt64();
                s.ShouldStomp = (r.ReadByte() & 1) != 0;
            }
            s.HasVisualAngle = (r.ReadByte() & 1) != 0; if (s.HasVisualAngle) s.VisualAngle = r.ReadSingle();
        }
        internal static void WriteDamage(BinaryWriter w, EnemyDamage d)
        { w.Write(d.Amount); w.Write(d.Type); w.Write(d.ForceX); w.Write(d.ForceY); WriteVector(w, d.Position); }
        internal static EnemyDamage ReadDamage(BinaryReader r)
        {
            EnemyDamage d = new EnemyDamage(); d.Amount = r.ReadSingle(); d.Type = r.ReadInt32(); d.ForceX = r.ReadSingle(); d.ForceY = r.ReadSingle(); d.Position = ReadVector(r);
            if (float.IsNaN(d.Amount) || float.IsInfinity(d.Amount) || d.Amount < 0) throw new InvalidDataException("E3 invalid damage");
            return d;
        }
        internal static void WriteHit(BinaryWriter w, EnemyHit hit)
        { w.Write(hit.Epoch); w.Write(hit.Generation); w.Write(hit.Id); WriteDamage(w, hit.Damage); }
        internal static EnemyHit ReadHit(BinaryReader r)
        { EnemyHit hit = new EnemyHit(); hit.Epoch = r.ReadUInt32(); hit.Generation = r.ReadUInt32(); hit.Id = r.ReadUInt64(); hit.Damage = ReadDamage(r); return hit; }
        internal static void WriteShot(BinaryWriter w, EnemyShot shot)
        {
            w.Write(shot.Epoch); w.Write(shot.Id); w.Write(shot.Prefab); WriteVector(w, shot.Position); WriteVector(w, shot.Velocity);
            w.Write(shot.Gravity); w.Write(shot.Lifetime); w.Write(shot.Damage); w.Write(shot.Type);
        }
        internal static EnemyShot ReadShot(BinaryReader r)
        {
            EnemyShot shot = new EnemyShot(); shot.Epoch = r.ReadUInt32(); shot.Id = r.ReadUInt64(); shot.Prefab = r.ReadString();
            shot.Position = ReadVector(r); shot.Velocity = ReadVector(r); shot.Gravity = r.ReadSingle(); shot.Lifetime = r.ReadSingle(); shot.Damage = r.ReadSingle(); shot.Type = r.ReadInt32(); return shot;
        }
    }
}
