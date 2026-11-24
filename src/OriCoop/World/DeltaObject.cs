using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.World
{
    // One item change: Value = new bytes, null = the item is gone.
    public sealed class ItemOp
    {
        public string Key;
        public byte[] Value;
        public ItemOp(string key, byte[] value) { Key = key; Value = value; }
    }

    // T-168: one aggregate world object (GameController, GameWorld) on one PC, kept in step with an ordering authority
    // (the Arbiter: in the P2P host's game, or in the C-server). Every PC proposes only the items it changed; the arbiter
    // gives each batch a global number and hands it to EVERY PC, the proposer included; each PC applies the batches in that
    // order (last writer wins per item), so all PCs end with the same items.
    //  - Confirmed: the items as the arbiter's order has them so far.
    //  - pending: my batches the arbiter has not handed back yet.
    //  - Visible (the game's live object) = Confirmed + my pending batches + my changes not yet proposed. An incoming batch
    //    never overwrites an item I am still changing (pending or not yet proposed): my change is ordered later and wins
    //    everywhere anyway; writing the older value now would undo it here (and an unproposed one would be lost).
    // Pure logic, Unity-free; WorldSync (WorldSync.Delta.cs) feeds it the game's bytes.
    public sealed class DeltaObject
    {
        // Test-only planted bug (ORICOOP_MUTATE_WORLD_WHOLE_BLOB=1): propose EVERY item (the whole object) on each change,
        // like the pre-T-168 whole-blob replace; two changes in the same RTT then overwrite each other again.
        public static bool MutateWholeBlob;

        sealed class Batch { public uint Id; public List<ItemOp> Ops; }

        readonly SortedDictionary<string, byte[]> m_confirmed = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        readonly List<Batch> m_pending = new List<Batch>();

        public DeltaObject(IDictionary<string, byte[]> confirmed) { Reset(confirmed); }

        public int PendingCount { get { return m_pending.Count; } }
        public IDictionary<string, byte[]> Confirmed { get { return m_confirmed; } }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void Put(IDictionary<string, byte[]> items, ItemOp op)
        {
            if (WorldItems.OnlyGrows(op.Key))   // T-214: the highest level wins, in any order
            {
                byte[] old; items.TryGetValue(op.Key, out old);
                if (WorldItems.Higher(op.Value, old)) items[op.Key] = op.Value;
                return;
            }
            if (op.Value == null) { if (!WorldItems.NeverRemoved(op.Key)) items.Remove(op.Key); }
            else items[op.Key] = op.Value;
        }

        // Confirmed with my pending batches on top: what the arbiter will have once it has ordered all of them.
        SortedDictionary<string, byte[]> Expected()
        {
            SortedDictionary<string, byte[]> expected = new SortedDictionary<string, byte[]>(m_confirmed, StringComparer.Ordinal);
            foreach (Batch b in m_pending) foreach (ItemOp op in b.Ops) Put(expected, op);
            return expected;
        }

        static List<ItemOp> Diff(IDictionary<string, byte[]> from, IDictionary<string, byte[]> to)
        {
            List<ItemOp> ops = new List<ItemOp>();
            foreach (KeyValuePair<string, byte[]> item in to)
            {
                byte[] old;
                bool had = from.TryGetValue(item.Key, out old);
                if (had && Same(old, item.Value)) continue;
                // T-214: a lower level than the agreed one is this PC's own rollback, not a change to propose.
                if (had && WorldItems.OnlyGrows(item.Key) && !WorldItems.Higher(item.Value, old)) continue;
                ops.Add(new ItemOp(item.Key, item.Value));
            }
            foreach (string key in from.Keys)
                if (!to.ContainsKey(key) && !WorldItems.NeverRemoved(key) && !WorldItems.OnlyGrows(key)) ops.Add(new ItemOp(key, null));
            return ops;
        }

        // My local changes not proposed yet (current = the live object's items). Empty = nothing to send.
        public List<ItemOp> LocalChanges(IDictionary<string, byte[]> current)
        {
            SortedDictionary<string, byte[]> expected = Expected();
            List<ItemOp> ops = Diff(expected, current);
            if (!MutateWholeBlob || ops.Count == 0) return ops;
            List<ItemOp> whole = new List<ItemOp>();
            foreach (KeyValuePair<string, byte[]> item in current) whole.Add(new ItemOp(item.Key, item.Value));
            foreach (string key in expected.Keys) if (!current.ContainsKey(key)) whole.Add(new ItemOp(key, null));
            return whole;
        }

        public void AddPending(uint batch, List<ItemOp> ops) { m_pending.Add(new Batch { Id = batch, Ops = ops }); }
        public bool IsPending(uint batch) { return m_pending.Exists(b => b.Id == batch); }

        // The arbiter handed back one of MY batches that a full snapshot already contained: drop it from pending only.
        public void Acknowledge(uint batch) { m_pending.RemoveAll(b => b.Id == batch); }

        // A batch in the arbiter's order (mine: my own batch coming back). current = the live object's items right now.
        // Returns the items the live object must have now (null = nothing visible changes).
        public SortedDictionary<string, byte[]> OnCommitted(bool mine, uint batch, List<ItemOp> ops, IDictionary<string, byte[]> current)
        {
            // Items I am still changing, measured BEFORE this batch moves Confirmed.
            HashSet<string> busy = new HashSet<string>();
            foreach (ItemOp op in Diff(Expected(), current)) busy.Add(op.Key);
            foreach (ItemOp op in ops) Put(m_confirmed, op);
            if (mine) Acknowledge(batch);
            foreach (Batch b in m_pending) foreach (ItemOp op in b.Ops) busy.Add(op.Key);
            SortedDictionary<string, byte[]> visible = new SortedDictionary<string, byte[]>(current, StringComparer.Ordinal);
            bool changed = false;
            foreach (ItemOp op in ops)
            {
                // T-214: not for an item that only grows: there the highest level wins, not the latest writer, so my own
                // lower level (unproposed or pending) must not hide a partner's higher one here.
                if (busy.Contains(op.Key) && !WorldItems.OnlyGrows(op.Key)) continue;
                byte[] now, want;
                bool have = visible.TryGetValue(op.Key, out now), wanted = m_confirmed.TryGetValue(op.Key, out want);
                if (have == wanted && (!have || Same(now, want))) continue;
                if (WorldItems.OnlyGrows(op.Key) && !WorldItems.Higher(wanted ? want : null, have ? now : null)) continue;
                if (wanted) visible[op.Key] = want; else visible.Remove(op.Key);
                changed = true;
            }
            return changed ? visible : null;
        }

        // A full snapshot (join, digest resync, host rollback) replaced the object: it is the new Confirmed. Returns the
        // items to show = the snapshot with my still-pending batches on top (they come back from the arbiter later).
        // T-214: `current` = the live object's items right now (null = not known). Exploration this PC has and the
        // snapshot lacks (walked since the last scan, or while the snapshot was on its way) stays on top as well; it is
        // not in Confirmed, so the next scan proposes it. After this PC's own checkpoint load `current` is the rolled-back
        // map, so a rollback still lowers it.
        public SortedDictionary<string, byte[]> Reset(IDictionary<string, byte[]> snapshot, IDictionary<string, byte[]> current = null)
        {
            m_confirmed.Clear();
            foreach (KeyValuePair<string, byte[]> item in snapshot) m_confirmed[item.Key] = item.Value;
            SortedDictionary<string, byte[]> visible = Expected();
            if (current != null)
                foreach (KeyValuePair<string, byte[]> item in current)
                {
                    if (!WorldItems.OnlyGrows(item.Key)) continue;
                    byte[] shown; visible.TryGetValue(item.Key, out shown);
                    if (WorldItems.Higher(item.Value, shown)) visible[item.Key] = item.Value;
                }
            return visible;
        }

        // Drops pending batches older than `keep` newest (a batch that never came back, e.g. the arbiter changed).
        public void Trim(int keep) { while (m_pending.Count > keep) m_pending.RemoveAt(0); }
    }

    // Wire form of one batch (the Data of an arbiter event, kind WorldDelta.Kind). The arbiter never parses it.
    public sealed class DeltaBatch
    {
        public const byte Kind = 0x57;          // 'W': IAuthority.Propose kind for world item batches
        public const byte Version = 1;
        public const int MaxBytes = 12000;      // one batch stays well inside a NetPlay message (16000)
        public uint Id;                          // proposer's batch number (matched when it comes back)
        public byte[] Scene = new byte[16], Object = new byte[16];
        public byte Schema;
        public string Type = "";
        public List<ItemOp> Ops = new List<ItemOp>();

        public byte[] Encode()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(Version); w.Write(Id); w.Write(Scene); w.Write(Object); w.Write(Schema); w.Write(Type);
                w.Write((ushort)Ops.Count);
                foreach (ItemOp op in Ops)
                {
                    w.Write(op.Key); w.Write(op.Value != null);
                    if (op.Value != null) { w.Write((ushort)op.Value.Length); w.Write(op.Value); }
                }
                w.Flush(); return stream.ToArray();
            }
        }

        public static DeltaBatch Decode(byte[] data)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
            {
                if (r.ReadByte() != Version) throw new InvalidDataException("unknown world delta version");
                DeltaBatch b = new DeltaBatch { Id = r.ReadUInt32(), Scene = r.ReadBytes(16), Object = r.ReadBytes(16), Schema = r.ReadByte(), Type = r.ReadString() };
                if (b.Scene.Length != 16 || b.Object.Length != 16) throw new EndOfStreamException();
                if (!WorldItems.IsAggregate(b.Schema) || b.Type.Length > 256) throw new InvalidDataException("invalid world delta object");
                int count = r.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    string key = r.ReadString();
                    if (key.Length > 512) throw new InvalidDataException("invalid world item key");
                    byte[] value = null;
                    if (r.ReadBoolean())
                    {
                        int length = r.ReadUInt16(); value = r.ReadBytes(length);
                        if (value.Length != length) throw new EndOfStreamException();
                    }
                    b.Ops.Add(new ItemOp(key, value));
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("world delta suffix");
                return b;
            }
        }

        // Splits ops into batches of at most MaxBytes each (ops are independent, any split is valid).
        public static List<List<ItemOp>> Split(List<ItemOp> ops, int typeBytes)
        {
            List<List<ItemOp>> parts = new List<List<ItemOp>>(); List<ItemOp> part = new List<ItemOp>();
            int size = 48 + typeBytes;
            foreach (ItemOp op in ops)
            {
                int opBytes = 4 + System.Text.Encoding.UTF8.GetByteCount(op.Key) + (op.Value == null ? 0 : 2 + op.Value.Length);
                if (part.Count > 0 && (size + opBytes > MaxBytes || part.Count == UInt16.MaxValue))
                { parts.Add(part); part = new List<ItemOp>(); size = 48 + typeBytes; }
                part.Add(op); size += opBytes;
            }
            if (part.Count > 0) parts.Add(part);
            return parts;
        }
    }
}
