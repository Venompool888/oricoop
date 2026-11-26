using System;
using System.Collections.Generic;
using System.Text;
using OriCoop.Authority;

namespace OriCoop.World
{
    // The game side of world item sync, as seen by WorldDeltaPeer (WorldSync.Delta.cs implements it on top of the live
    // save objects; the unit tests use a dictionary of bytes).
    public interface IWorldDeltaGame
    {
        // The object's current bytes (live, or the last known copy of an unloaded one). False = unknown here.
        bool TryRead(byte[] scene, byte[] id, out byte[] bytes);
        // Show these bytes (live + checkpoint). The peer only calls it when an item really changes.
        void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes);
        // The held queue overflowed: only a full snapshot can catch up.
        void NeedResync(string why);
    }

    // T-168: per-item sync of the aggregate world objects through IAuthority's ordered events (Propose -> Committed): the
    // P2P host's Arbiter or the C-server's. See DeltaObject for the merge rule and docs/tickets/T-168.md for the design.
    // Snapshots: while a full world snapshot is coming (guest join, digest resync), committed batches are HELD; the
    // snapshot's end marker carries the sender's watermark (the newest batch id it had applied when it captured), and
    // held batches up to it are already inside the snapshot (only my own are acknowledged), newer ones are applied.
    public sealed class WorldDeltaPeer
    {
        public const int MaxHeld = 4096, MaxPending = 64;

        readonly Func<IAuthority> m_authority;
        readonly IWorldDeltaGame m_game;
        readonly Dictionary<string, DeltaObject> m_objects = new Dictionary<string, DeltaObject>();
        // Objects the snapshot on its way has replaced so far (SnapshotObject): my pending batches are shown on top there.
        readonly HashSet<string> m_snapshotObjects = new HashSet<string>();
        readonly List<CommittedEvent> m_held = new List<CommittedEvent>();
        // Batches this PC received before the host's snapshot start can be newer than its end watermark.
        // Keep wire bytes only, bounded by both count and size; a larger gap requests a fresh snapshot.
        const int MaxHistoryBytes = 1024 * 1024, MaxHistoryBatches = 256;
        readonly Queue<CommittedEvent> m_history = new Queue<CommittedEvent>();
        int m_historyBytes;
        uint m_historyFloor;
        uint m_nextBatch, m_applied;
        uint m_reportLimit = uint.MaxValue;
        // Newest batch id dropped from a full held queue while a snapshot was on its way (0 = none). The snapshot already
        // holds every batch up to its watermark, so only a dropped batch newer than that needs another full resync.
        uint m_droppedHeld;
        bool m_snapshot;
        public int BatchesSent, BatchesApplied, BatchesHeld, BadBatches;

        public WorldDeltaPeer(Func<IAuthority> authority, IWorldDeltaGame game) { m_authority = authority; m_game = game; }

        // Newest committed batch id applied here (sent in the snapshot end marker).
        public uint Watermark { get { return m_applied; } }
        // A missing local aggregate cannot be promised to a fresh joiner's future snapshot.
        public uint CoveredWatermark { get { return Math.Min(m_applied, m_reportLimit); } }
        public bool InSnapshot { get { return m_snapshot; } }
        public int HeldCount { get { return m_held.Count; } }

        static string Hex(byte[] b)
        {
            StringBuilder s = new StringBuilder(b.Length * 2);
            foreach (byte x in b) s.Append(x.ToString("x2"));
            return s.ToString();
        }
        static string Key(byte[] scene, byte[] id) { return Hex(scene) + ":" + Hex(id); }

        byte[] m_lastMapBytes;
        SortedDictionary<string, byte[]> m_lastMapItems;

        SortedDictionary<string, byte[]> Items(byte schema, byte[] bytes)
        {
            if (schema != WorldItems.SchemaMap) return WorldItems.Decompose(schema, bytes);
            if (m_lastMapItems == null || !DeltaObject.Same(bytes, m_lastMapBytes))
            {
                m_lastMapItems = WorldItems.Decompose(schema, bytes);
                m_lastMapBytes = (byte[])bytes.Clone();
            }
            return m_lastMapItems;
        }

        public void Reset()
        {
            m_lastMapBytes = null; m_lastMapItems = null;
            m_history.Clear(); m_historyBytes = 0; m_historyFloor = 0; m_reportLimit = uint.MaxValue; m_snapshotObjects.Clear();
            m_objects.Clear(); m_held.Clear(); m_nextBatch = 0; m_applied = 0; m_droppedHeld = 0; m_snapshot = false;
        }

        DeltaObject Object(byte[] scene, byte[] id, byte schema, byte[] currentBytes)
        {
            string key = Key(scene, id); DeltaObject o;
            if (!m_objects.TryGetValue(key, out o))
            {
                // First sight with no snapshot: what this PC has now is its best guess of the agreed state.
                o = new DeltaObject(Items(schema, currentBytes)); m_objects[key] = o;
            }
            return o;
        }

        public bool Known(byte[] scene, byte[] id) { return m_objects.ContainsKey(Key(scene, id)); }

        // The object as this PC first saw it (session start / first scan): the baseline its later changes are measured
        // against when no snapshot set one. Does nothing for an object already known.
        public void Observe(byte[] scene, byte[] id, byte schema, byte[] bytes) { Object(scene, id, schema, bytes); }

        // The scan saw the object change on this PC: propose the changed items. Returns the number of batches sent.
        // previous: the bytes before this change, the baseline if the object was never observed (null = none known).
        public int LocalChange(byte[] scene, byte[] id, byte schema, string type, byte[] bytes, byte[] previous = null)
        {
            if (m_snapshot) return 0;   // a snapshot replaces this state anyway (checkpoint restore, join)
            IAuthority authority = m_authority();
            if (authority == null || authority.Mode == AuthorityMode.None) return 0;
            DeltaObject o = Object(scene, id, schema, previous ?? bytes);
            List<ItemOp> ops = o.LocalChanges(Items(schema, bytes));
            if (ops.Count == 0) return 0;
            int sent = 0;
            foreach (List<ItemOp> part in DeltaBatch.Split(ops, Encoding.UTF8.GetByteCount(type)))
            {
                DeltaBatch batch = new DeltaBatch { Id = ++m_nextBatch, Scene = scene, Object = id, Schema = schema, Type = type, Ops = part };
                o.AddPending(batch.Id, part); o.Trim(MaxPending);
                // HostP2P: the host's own proposal is committed (and comes back through OnCommitted) inside this call.
                authority.Propose(DeltaBatch.Kind, "w", batch.Encode(), false, false);
                sent++; BatchesSent++;
            }
            return sent;
        }

        // IAuthority.Committed (any kind; others are ignored). canApply: the game can take world state now.
        public void OnCommitted(CommittedEvent e, bool canApply)
        {
            if (e.Kind != DeltaBatch.Kind || e.Rejected) return;
            if (m_snapshot || !canApply)
            {
                m_held.Add(e); BatchesHeld++;
                if (m_held.Count > MaxHeld)
                {
                    uint dropped = m_held[0].Id; m_held.RemoveAt(0);
                    // During a snapshot the dropped (oldest) batch is normally inside it: decided at EndSnapshot. Before,
                    // a joiner whose queue the arbiter's replay of old batches had filled asked for a second full
                    // snapshot on the first live batch.
                    if (m_snapshot) { if (dropped > m_droppedHeld) m_droppedHeld = dropped; }
                    else m_game.NeedResync("world delta queue overflow");
                }
                return;
            }
            // Readiness can change between two commits before the next gameplay tick.
            // A newer live batch must not advance the cursor ahead of the older held batches.
            Flush();
            Apply(e);
        }

        // Applies held batches once the game can take them (no snapshot pending).
        public void Flush()
        {
            if (m_snapshot || m_held.Count == 0) return;
            CommittedEvent[] held = m_held.ToArray(); m_held.Clear();
            foreach (CommittedEvent e in held) Apply(e);
        }

        void Apply(CommittedEvent e, bool replay = false)
        {
            DeltaBatch batch;
            try { batch = DeltaBatch.Decode(e.Data); }
            catch (Exception) { BadBatches++; return; }
            if (replay)
            {
                if (!m_snapshotObjects.Contains(Key(batch.Scene, batch.Object))) return;
            }
            else
            {
                if (e.Id <= m_applied) return;
                m_applied = e.Id;
                m_history.Enqueue(e);
                m_historyBytes += e.Data.Length;
                while (m_history.Count > MaxHistoryBatches || m_historyBytes > MaxHistoryBytes)
                {
                    CommittedEvent oldest = m_history.Dequeue();
                    m_historyFloor = Math.Max(m_historyFloor, oldest.Id);
                    m_historyBytes -= oldest.Data.Length;
                }
            }
            byte[] current;
            if (!m_game.TryRead(batch.Scene, batch.Object, out current))
            {
                if (e.Id > 0) m_reportLimit = Math.Min(m_reportLimit, e.Id - 1);
                return;
            }
            DeltaObject o = Object(batch.Scene, batch.Object, batch.Schema, current);
            IAuthority authority = m_authority();
            bool mine = authority != null && e.From == authority.LocalSeat && o.IsPending(batch.Id);
            SortedDictionary<string, byte[]> visible = o.OnCommitted(mine, batch.Id, batch.Ops, Items(batch.Schema, current));
            BatchesApplied++;
            if (visible != null) m_game.Write(batch.Scene, batch.Object, batch.Schema, batch.Type, WorldItems.Compose(batch.Schema, current, visible));
        }

        // ---------- full snapshots ----------
        public void BeginSnapshot() { if (!m_snapshot) m_snapshotObjects.Clear(); m_snapshot = true; }

        // A snapshot blob of an aggregate object arrived: returns the bytes to show (snapshot + my pending batches).
        public byte[] SnapshotObject(byte[] scene, byte[] id, byte schema, byte[] bytes)
        {
            SortedDictionary<string, byte[]> snapshot = Items(schema, bytes);
            // T-214: the map as this PC has it now; what it explored and has not proposed yet stays (DeltaObject.Reset).
            IDictionary<string, byte[]> current = null; byte[] live;
            if (WorldItems.HasGrowing(schema) && m_game.TryRead(scene, id, out live) && live != null) current = Items(schema, live);
            DeltaObject o;
            m_snapshotObjects.Add(Key(scene, id));
            if (!m_objects.TryGetValue(Key(scene, id), out o))
            {
                o = new DeltaObject(snapshot); m_objects[Key(scene, id)] = o;
                if (current == null) return bytes;
            }
            return WorldItems.Compose(schema, bytes, o.Reset(snapshot, current));
        }

        // The snapshot sender captured this object (it is what the receivers get): it is the agreed state from now on.
        public void SentSnapshotObject(byte[] scene, byte[] id, byte schema, byte[] bytes)
        {
            DeltaObject o;
            if (!m_objects.TryGetValue(Key(scene, id), out o)) m_objects[Key(scene, id)] = new DeltaObject(WorldItems.Decompose(schema, bytes));
            else o.Reset(WorldItems.Decompose(schema, bytes));
        }

        public void EndSnapshot(uint watermark)
        {
            m_snapshot = false;
            uint dropped = m_droppedHeld; m_droppedHeld = 0;
            CommittedEvent[] held = m_held.ToArray(); m_held.Clear();
            bool missingHistory = watermark < m_historyFloor;
            // The host uses MaxValue at startup to discard batches superseded by its loaded save.
            if (watermark != uint.MaxValue)
            {
                m_applied = Math.Max(m_applied, watermark);
                m_historyFloor = Math.Max(m_historyFloor, watermark);
            }
            IAuthority authority = m_authority();
            foreach (CommittedEvent e in held)
            {
                if (e.Id > watermark) continue;
                if (e.Id > m_applied) m_applied = e.Id;
                if (authority == null || e.From != authority.LocalSeat) continue;
                try
                {
                    DeltaBatch batch = DeltaBatch.Decode(e.Data); DeltaObject o; string key = Key(batch.Scene, batch.Object);
                    if (!m_objects.TryGetValue(key, out o) || !o.IsPending(batch.Id)) continue;
                    o.Acknowledge(batch.Id);
                    // SnapshotObject showed this batch on top of the snapshot, which already has it, and maybe a partner's
                    // later change of the same item: show the agreed value again.
                    byte[] current;
                    if (!m_snapshotObjects.Contains(key) || !m_game.TryRead(batch.Scene, batch.Object, out current)) continue;
                    SortedDictionary<string, byte[]> visible = o.Superseded(batch.Ops, Items(batch.Schema, current));
                    if (visible != null) m_game.Write(batch.Scene, batch.Object, batch.Schema, batch.Type, WorldItems.Compose(batch.Schema, current, visible));
                }
                catch (Exception) { BadBatches++; }
            }
            foreach (CommittedEvent e in m_history)
                if (e.Id > watermark) Apply(e, true);
            foreach (CommittedEvent e in held)
                if (e.Id > watermark) Apply(e);
            if (dropped > watermark || missingHistory) m_game.NeedResync("world snapshot behind retained batches");
        }
    }
}
