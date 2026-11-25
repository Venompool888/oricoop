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
        public int SenderSeat;  // supplied by the transport when queued
        public uint Sequence;
        public ActionKind Kind;
        public byte[] Guid;
        public int Amount, Extra;
        // T-214: where the pickup is on the collecting PC (its scene's GUID, zeros = not known; its position).
        public byte[] Scene;
        public bool HasSpot, Registered;
        public float X, Y;
        public PickupPlace Place
        {
            get
            {
                PickupPlace p = new PickupPlace { HasSpot = HasSpot, X = X, Y = Y, Scene = "" };
                if (Scene != null) foreach (byte b in Scene) if (b != 0) { p.Scene = BitConverter.ToString(Scene); break; }
                return p;
            }
        }
        public void Write(BinaryWriter w)
        {
            w.Write(Sequence); w.Write((byte)Kind); w.Write(Guid); w.Write(Amount); w.Write(Extra);
            w.Write(Scene != null && Scene.Length == 16 ? Scene : new byte[16]); w.Write(HasSpot); w.Write(X); w.Write(Y); w.Write(Registered);
        }
        public static ProgressAction Read(BinaryReader r)
        {
            ProgressAction a = new ProgressAction(); a.Sequence = r.ReadUInt32(); a.Kind = (ActionKind)r.ReadByte();
            a.Guid = StoryWire.ReadId(r); a.Amount = r.ReadInt32(); a.Extra = r.ReadInt32();
            a.Scene = StoryWire.ReadId(r); a.HasSpot = r.ReadBoolean(); a.X = r.ReadSingle(); a.Y = r.ReadSingle(); a.Registered = r.ReadBoolean(); return a;
        }
    }

    // T-214: where a pickup is. Its GUID alone is not its identity: spirit light orbs, cells and other pickups are spawned
    // from one prefab in many scenes (decomp PrefabPlaceholder), and every copy carries the prefab's GUID.
    internal struct PickupPlace
    {
        public string Scene;    // the scene's GUID as text; "" or null = not known
        public bool HasSpot;
        public float X, Y;
        public static PickupPlace Unknown { get { return new PickupPlace { Scene = "" }; } }

        public const float SameSpot = 0.5f;
        // Test-only mutation ORICOOP_MUTATE_PICKUP_GUID_ONLY=1: the GUID alone is the identity again (pre-T-214).
        public static bool MutateGuidOnly;
        // Same pickup (for one GUID) = the same scene when both are known, and the same spot when both are known.
        public static bool Same(PickupPlace a, PickupPlace b)
        {
            if (MutateGuidOnly) return true;
            bool sceneA = !string.IsNullOrEmpty(a.Scene), sceneB = !string.IsNullOrEmpty(b.Scene);
            if (sceneA && sceneB && a.Scene != b.Scene) return false;
            if (!a.HasSpot || !b.HasSpot) return true;
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy <= SameSpot * SameSpot;
        }
    }

    // The pickups the team collected since the last checkpoint load: one entry per pickup, so two players touching the
    // same pickup reward once. Before T-214 an entry was the GUID alone: a guest's next pickup of the same prefab
    // anywhere in the world was dropped as a duplicate and its reward was lost.
    internal sealed class PickupLedger<TGuid>
    {
        sealed class Entry { public TGuid Guid; public PickupPlace Place; }
        readonly List<Entry> entries = new List<Entry>();

        public int Count { get { return entries.Count; } }

        public bool Has(TGuid guid, PickupPlace place)
        {
            foreach (Entry e in entries)
                if (EqualityComparer<TGuid>.Default.Equals(e.Guid, guid) && PickupPlace.Same(e.Place, place)) return true;
            return false;
        }

        // true = first time (reward it); false = this very pickup is in the ledger already.
        public bool Record(TGuid guid, PickupPlace place)
        {
            if (Has(guid, place)) return false;
            entries.Add(new Entry { Guid = guid, Place = place });
            return true;
        }

        public void Clear() { entries.Clear(); }
    }
    internal sealed class CellResult
    {
        public const byte Message = 24;
        public uint Sequence;
        public ActionKind Kind;
        public bool Accepted;
        public float Maximum;
        public static bool IsCell(ActionKind kind) { return kind == ActionKind.MaxHealth || kind == ActionKind.MaxEnergy; }
        public void Write(BinaryWriter w) { w.Write(Sequence); w.Write((byte)Kind); w.Write(Accepted); w.Write(Maximum); }
        public static CellResult Read(BinaryReader r)
        {
            CellResult cell = new CellResult { Sequence = r.ReadUInt32(), Kind = (ActionKind)r.ReadByte(), Accepted = r.ReadBoolean(), Maximum = r.ReadSingle() };
            if (!IsCell(cell.Kind) || float.IsNaN(cell.Maximum) || float.IsInfinity(cell.Maximum) || (cell.Accepted && cell.Maximum <= 0f))
                throw new InvalidDataException("invalid cell result");
            return cell;
        }
    }

    internal sealed class ProgressQueue<TGuid>
    {
        readonly HashSet<ulong> actions = new HashSet<ulong>();
        readonly PickupLedger<TGuid> collected = new PickupLedger<TGuid>();
        readonly Queue<ProgressAction> pending = new Queue<ProgressAction>();
        public int Count { get { return pending.Count; } }
        public bool Enqueue(int sender, ProgressAction action)
        {
            if (!actions.Add(((ulong)sender << 32) | action.Sequence)) return false;
            action.SenderSeat = sender;
            pending.Enqueue(action); return true;
        }
        public ProgressAction Peek() { return pending.Peek(); }
        public ProgressAction Dequeue() { return pending.Dequeue(); }
        public bool RecordPickup(TGuid guid) { return collected.Record(guid, PickupPlace.Unknown); }
        public bool RecordPickup(TGuid guid, PickupPlace place) { return collected.Record(guid, place); }
        public bool HasPickup(TGuid guid, PickupPlace place) { return collected.Has(guid, place); }
        public void ClearPickups() { collected.Clear(); }
        // T-143: sequences are per connection. A guest that rejoins (its sequence restarts at 1), or a new player in the
        // freed seat, must not hit the old seat's history. No "seat gone" block: the departed connection is no longer read
        // after the leave (its remote and route are removed), and a device renumbered into a freed seat (NetLocalSeats
        // ReconcileSeats) keeps its Hello, so no PlayerJoined would ever reopen such a block.
        public void SeatLeft(int seat) { Forget(seat); }
        public void SeatJoined(int seat) { Forget(seat); }
        void Forget(int seat)
        {
            actions.RemoveWhere(delegate(ulong key) { return (int)(key >> 32) == seat; });
            // Shared progress may still settle. A new connection in this seat must not receive its predecessor's reward.
            foreach (ProgressAction action in pending) if (action.SenderSeat == seat) action.SenderSeat = 0;
        }
        public void Reset() { actions.Clear(); collected.Clear(); pending.Clear(); }
    }
    // T-214: when the host's unsent progress is a stall worth a warning. Playtest 4 logged "progress sync stalled" eight
    // times; each was a guest inside its normal checkpoint load, or a room with no guest left. Neither is a stall:
    // nobody can receive progress then, and it is sent in full when the guest reports its load.
    internal static class ProgressStall
    {
        public const float SendLimit = 3f;    // a guest is ready: the change should have gone out long ago
        public const float LoadLimit = 60f;   // a guest's checkpoint load takes 4-15 s; its own load times out at 60 s
        // unsentSeconds < 0: nothing unsent. waitingSeat: the seat whose checkpoint load is outstanding longest (0 = none).
        public static bool Host(float unsentSeconds, bool guestReady, int waitingSeat, float waitedSeconds)
        {
            // Reported whoever else is ready (3-4 players: one ready guest must not hide another's stuck load) and
            // whether or not this host has a change to send: that seat gets no progress until it reports.
            if (waitingSeat != 0 && waitedSeconds > LoadLimit) return true;
            return guestReady && unsentSeconds > SendLimit;
        }
    }

    internal static class ProgressWire
    {
        public static void WriteSkillStatus(BinaryWriter w, bool enabled) { w.Write(enabled); }
        public static bool ReadSkillStatus(BinaryReader r) { return r.ReadBoolean(); }
        public static bool ApplySkillStatus(bool host, bool received, bool current) { return host ? current : received; }
    }
}
