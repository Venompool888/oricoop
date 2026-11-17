using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    internal enum ActionKind : byte
    { Keystone, Experience, SkillPoint, MaxEnergy, MaxHealth, MapStone, MarkOnly,
        DirectExperience, KeysDelta, MapsDelta, DoorEnergyDelta, Skill, Pedestal }

    internal sealed class ProgressSnapshot
    {
        public ulong Abilities;
        public int MaxHealth, Keys, Maps, Collected, Experience, Level, Points;
        public float MaxEnergy;
        public void Write(BinaryWriter w)
        {
            w.Write(Abilities); w.Write(MaxHealth); w.Write(MaxEnergy); w.Write(Keys); w.Write(Maps);
            w.Write(Collected); w.Write(Experience); w.Write(Level); w.Write(Points);
        }
        public static ProgressSnapshot Read(BinaryReader r)
        {
            ProgressSnapshot s = new ProgressSnapshot();
            s.Abilities = r.ReadUInt64(); s.MaxHealth = r.ReadInt32(); s.MaxEnergy = r.ReadSingle();
            s.Keys = r.ReadInt32(); s.Maps = r.ReadInt32(); s.Collected = r.ReadInt32();
            s.Experience = r.ReadInt32(); s.Level = r.ReadInt32(); s.Points = r.ReadInt32(); return s;
        }
        public ulong Hash()
        {
            MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); Write(w); w.Flush();
            ulong h = 14695981039346656037UL;
            foreach (byte b in m.ToArray()) { h ^= b; h = unchecked(h * 1099511628211UL); }
            return h;
        }
    }
    internal sealed class ProgressInbox
    {
        public ProgressSnapshot Snapshot;
        public float ReceivedAt = -1f;
        public void Receive(bool host, ProgressSnapshot snapshot, float now)
        {
            if (host) return;
            Snapshot = snapshot;
            if (ReceivedAt < 0) ReceivedAt = now;
        }
        public ProgressSnapshot Take()
        {
            ProgressSnapshot snapshot = Snapshot; Snapshot = null; ReceivedAt = -1f; return snapshot;
        }
    }
    internal sealed class ProgressAction
    {
        public uint Sequence;
        public ActionKind Kind;
        public byte[] Guid;
        public int Amount, Extra;
        public void Write(BinaryWriter w)
        { w.Write(Sequence); w.Write((byte)Kind); w.Write(Guid); w.Write(Amount); w.Write(Extra); }
        public static ProgressAction Read(BinaryReader r)
        {
            ProgressAction a = new ProgressAction(); a.Sequence = r.ReadUInt32(); a.Kind = (ActionKind)r.ReadByte();
            a.Guid = StoryWire.ReadId(r); a.Amount = r.ReadInt32(); a.Extra = r.ReadInt32(); return a;
        }
    }
    internal sealed class ProgressQueue<TGuid>
    {
        readonly HashSet<ulong> actions = new HashSet<ulong>();
        readonly HashSet<TGuid> collected = new HashSet<TGuid>();
        readonly Queue<ProgressAction> pending = new Queue<ProgressAction>();
        public int Count { get { return pending.Count; } }
        public bool Enqueue(int sender, ProgressAction action)
        {
            if (!actions.Add(((ulong)sender << 32) | action.Sequence)) return false;
            pending.Enqueue(action); return true;
        }
        public ProgressAction Peek() { return pending.Peek(); }
        public ProgressAction Dequeue() { return pending.Dequeue(); }
        public bool RecordPickup(TGuid guid) { return collected.Add(guid); }
        public void ClearPickups() { collected.Clear(); }
        public void Reset() { actions.Clear(); collected.Clear(); pending.Clear(); }
    }
    internal static class ProgressWire
    {
        public static void WriteSkillStatus(BinaryWriter w, bool enabled) { w.Write(enabled); }
        public static bool ReadSkillStatus(BinaryReader r) { return r.ReadBoolean(); }
        public static bool ApplySkillStatus(bool host, bool received, bool current) { return host ? current : received; }
    }
}
