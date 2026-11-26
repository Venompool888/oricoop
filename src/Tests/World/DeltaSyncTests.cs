using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Authority;
using OriCoop.World;

// T-168: per-item world sync through the real Arbiter (P2P host's HostArbiter, or a C-server Arbiter) over a simulated
// network: every link is FIFO (reliable ordered, like NetPeer) with its own delay. Oracle (from the ticket): changes to
// DIFFERENT items made within one RTT all survive on every PC; any interleaving converges to identical items on all PCs.
static class DeltaSyncTests
{
    static Action<string, Action> Run;
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    // ---------- fixtures: GameController / GameWorld Archive bytes (layouts in WorldItems) ----------
    static byte[] Guid(int n) { byte[] g = new byte[16]; g[0] = (byte)n; g[15] = 0x5A; return g; }
    static string EKey(int n) { return "e:" + Hex(Guid(n)); }
    static string Hex(byte[] b) { string s = ""; foreach (byte x in b) s += x.ToString("x2"); return s; }

    public static byte[] Controller(int[] events, int[] values, string[] triggers, float clock, int[] objectives)
    {
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(events.Length);
            for (int i = 0; i < events.Length; i++) { w.Write(Guid(events[i])); w.Write(values[i]); }
            w.Write(triggers.Length); foreach (string t in triggers) w.Write(t);
            w.Write(0f); w.Write(clock); w.Write(0); w.Write(0); w.Write(false);
            w.Write(objectives.Length); foreach (int o in objectives) w.Write(o);
            w.Flush(); return m.ToArray();
        }
    }

    static byte[] MapBytes(int areas, int[] revealed, int last)
    {
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(areas);
            for (int a = 0; a < areas; a++)
            {
                w.Write(2); w.Write(7); w.Write(a); w.Write(3); w.Write(1);        // states {7: a, 3: 1}
                w.Write(1); w.Write(Guid(100 + a)); w.Write(4);                     // one icon
            }
            w.Write(revealed.Length); foreach (int r in revealed) w.Write(Guid(r));
            w.Write(last);
            w.Flush(); return m.ToArray();
        }
    }

    // T-214: one map area with the given cells (face id -> WorldMapAreaState), no icons.
    static byte[] MapStates(int[] faces, int[] levels)
    {
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(1);
            w.Write(faces.Length); for (int i = 0; i < faces.Length; i++) { w.Write(faces[i]); w.Write(levels[i]); }
            w.Write(0);
            w.Write(0); w.Write(0);
            w.Flush(); return m.ToArray();
        }
    }
    // Items of that area: Cells(face, level, face, level, ...).
    static SortedDictionary<string, byte[]> Cells(params int[] faceLevel)
    {
        SortedDictionary<string, byte[]> items = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < faceLevel.Length; i += 2) items["a0.s" + faceLevel[i]] = BitConverter.GetBytes(faceLevel[i + 1]);
        return items;
    }
    static int Level(IDictionary<string, byte[]> items, int face)
    {
        byte[] value;
        return items.TryGetValue("a0.s" + face, out value) ? BitConverter.ToInt32(value, 0) : -1;
    }
    sealed class OneObject : IWorldDeltaGame
    {
        public byte[] Bytes;
        public bool TryRead(byte[] scene, byte[] id, out byte[] bytes) { bytes = Bytes; return Bytes != null; }
        public void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes) { Bytes = bytes; }
        public void NeedResync(string why) { }
    }

    static readonly byte[] Scene = new byte[16], Obj = Guid(200);
    const byte Ctl = WorldItems.SchemaController;

    // ---------- simulated network ----------
    sealed class Net
    {
        public double Now;
        sealed class Item { public double At; public long Order; public Action Do; }
        readonly List<Item> m_q = new List<Item>();
        readonly Dictionary<string, double> m_last = new Dictionary<string, double>();
        long m_order;
        public Func<int, int, double> Delay = (a, b) => 50;
        // FIFO per link: never deliver before the previous message on the same link.
        public void Send(int from, int to, Action deliver)
        {
            string link = from + ">" + to; double last;
            double at = Now + Delay(from, to);
            if (m_last.TryGetValue(link, out last) && at < last) at = last;
            m_last[link] = at;
            m_q.Add(new Item { At = at, Order = m_order++, Do = deliver });
        }
        public void RunUntil(double t)
        {
            while (true)
            {
                Item next = null;
                foreach (Item i in m_q) if (i.At <= t && (next == null || i.At < next.At || (i.At == next.At && i.Order < next.Order))) next = i;
                if (next == null) break;
                m_q.Remove(next); Now = next.At; next.Do();
            }
            Now = t;
        }
        public void Drain() { RunUntil(Now + 100000); }
    }

    sealed class Game : IWorldDeltaGame
    {
        public byte[] Bytes; public int Resyncs;
        public bool TryRead(byte[] scene, byte[] id, out byte[] bytes) { bytes = Bytes; return Bytes != null; }
        public void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes) { Bytes = bytes; }
        public void NeedResync(string why) { Resyncs++; }
    }

    sealed class Peer
    {
        public int Seat; public Game Game = new Game(); public IAuthority Authority; public WorldDeltaPeer Delta;
        public bool Ready = true;
        public SortedDictionary<string, byte[]> Items { get { return WorldItems.Decompose(Ctl, Game.Bytes); } }
        public void Attach()
        {
            Delta = new WorldDeltaPeer(() => Authority, Game);
            Authority.Committed += e => Delta.OnCommitted(e, Ready);
        }
        // The player changes items on this PC; `scan` = WorldSync's 250 ms scan runs right away.
        public void Edit(Action<SortedDictionary<string, byte[]>> change, bool scan)
        {
            SortedDictionary<string, byte[]> items = Items; change(items);
            Game.Bytes = WorldItems.Compose(Ctl, Game.Bytes, items);
            if (scan) Scan();
        }
        public void Scan() { Delta.LocalChange(Scene, Obj, Ctl, "GameController", Game.Bytes); }
    }

    sealed class Room
    {
        public Net Net = new Net(); public Peer[] Peers; public bool Server;
        HostArbiter m_host; Arbiter m_server;
        public Peer this[int seat] { get { return Peers[seat - 1]; } }

        public Room(int players, bool server, byte[] start)
        {
            Server = server; Peers = new Peer[players];
            for (int i = 0; i < players; i++) Peers[i] = new Peer { Seat = i + 1 };
            if (server) m_server = new Arbiter("", new ArbiterOptions(), 0);
            else m_host = new HostArbiter(() => 1, (seat, msg, rel) => Net.Send(1, seat, () => Recv(seat, msg)), () => Net.Now);
            foreach (Peer p in Peers)
            {
                int seat = p.Seat;
                if (server) p.Authority = new AuthorityClient(AuthorityMode.Server, () => seat,
                    (msg, rel) => Net.Send(seat, 0, () => Route(m_server.Handle(seat, CsWire.Decode(msg), Net.Now))), false);
                else if (seat == 1) p.Authority = m_host.Local;
                else p.Authority = new AuthorityClient(AuthorityMode.HostP2P, () => seat,
                    (msg, rel) => Net.Send(seat, 1, () => m_host.FromRemote(seat, msg)), false);
                p.Game.Bytes = (byte[])start.Clone();
                p.Attach();
                p.Delta.Observe(Scene, Obj, Ctl, p.Game.Bytes);
            }
            foreach (Peer p in Peers) Join(p.Seat);
            Net.Drain();
        }

        int Recv(int seat, byte[] msg) { ((AuthorityClient)this[seat].Authority).Receive(msg); return 0; }

        public void Join(int seat)
        {
            if (Server) Route(m_server.JoinAt(seat, 0, "", Net.Now));
            else m_host.SeatJoined(seat);
        }

        void Route(List<Outgoing> output)
        {
            foreach (Outgoing o in output)
                for (int seat = 1; seat <= Peers.Length; seat++)
                    if (m_server.IsOnline(seat, Net.Now) && (o.To == seat || o.To == 0 || (o.To < 0 && -o.To != seat)))
                    {
                        int s = seat; byte[] msg = o.Message;
                        Net.Send(0, s, () => ((AuthorityClient)this[s].Authority).Receive(msg));
                    }
        }

        // WorldSync's full snapshot from the host (seat 1) to `seat`: start marker, the object, end marker + watermark.
        // P2P: one hop; C-server: through the server (host -> server -> guest), FIFO with the arbiter's stream.
        public void Snapshot(int seat)
        {
            Peer host = this[1], guest = this[seat];
            byte[] bytes = (byte[])host.Game.Bytes.Clone(); uint mark = host.Delta.Watermark;
            host.Delta.SentSnapshotObject(Scene, Obj, Ctl, bytes);
            Action deliver = () => {
                guest.Game.Bytes = guest.Delta.SnapshotObject(Scene, Obj, Ctl, bytes);
                guest.Delta.EndSnapshot(mark);
            };
            if (Server) Net.Send(1, 0, () => Net.Send(0, seat, deliver)); else Net.Send(1, seat, deliver);
        }

        public void AssertConverged(string what)
        {
            string first = Dump(Peers[0].Items);
            foreach (Peer p in Peers)
            {
                string mine = Dump(p.Items);
                Check(mine == first, what + ": P" + p.Seat + " has {" + mine + "} but P1 has {" + first + "}");
                Check(p.Delta.HeldCount == 0, what + ": P" + p.Seat + " still holds " + p.Delta.HeldCount);
            }
        }
    }

    static string Dump(IDictionary<string, byte[]> items)
    {
        List<string> parts = new List<string>();
        foreach (KeyValuePair<string, byte[]> i in items) parts.Add(i.Key.Substring(0, Math.Min(i.Key.Length, 6)) + "=" + BitConverter.ToInt32(i.Value, 0));
        return string.Join(",", parts.ToArray());
    }

    static byte[] Start() { return Controller(new int[] { 1, 2, 3, 4, 5 }, new int[] { 0, 0, 0, 0, 0 }, new string[] { "intro" }, 3f, new int[] { 2 }); }
    static Action<SortedDictionary<string, byte[]>> SetEvent(int n, int value) { return items => items[EKey(n)] = BitConverter.GetBytes(value); }
    static int Count(Peer p, string trigger) { return WorldItems.Triggers(p.Game.Bytes).FindAll(t => t == trigger).Count; }
    // TriggerByString.Register / Deregister on this PC (List.Add / List.Remove), then WorldSync's scan.
    static void AddTrigger(Peer p, string trigger)
    { List<string> l = WorldItems.Triggers(p.Game.Bytes); l.Add(trigger); p.Game.Bytes = WorldItems.WithTriggers(p.Game.Bytes, l); p.Scan(); }
    static void RemoveTrigger(Peer p, string trigger)
    { List<string> l = WorldItems.Triggers(p.Game.Bytes); l.Remove(trigger); p.Game.Bytes = WorldItems.WithTriggers(p.Game.Bytes, l); p.Scan(); }
    static int EventOf(Peer p, int n) { byte[] v; return p.Items.TryGetValue(EKey(n), out v) ? BitConverter.ToInt32(v, 0) : -1; }

    // Everyone in `editors` changes its own event (seat s sets event s to 10+s), all within `spreadMs` (< one RTT).
    static void SameRttScenario(int players, bool server, int[] editors, double spreadMs)
    {
        Room room = new Room(players, server, Start());
        room.Net.Delay = (a, b) => 100;   // RTT 200 ms P2P, 400 ms host-guest via the C-server
        double t0 = room.Net.Now;
        for (int i = 0; i < editors.Length; i++)
        {
            room.Net.RunUntil(t0 + spreadMs * i / Math.Max(1, editors.Length - 1));
            int seat = editors[i];
            room[seat].Edit(SetEvent(seat, 10 + seat), true);
        }
        room.Net.Drain();
        string mode = (server ? "C-server " : "P2P ") + players + "P";
        foreach (Peer p in room.Peers) mode += " [P" + p.Seat + " sent " + p.Delta.BatchesSent + " applied " + p.Delta.BatchesApplied + " held " + p.Delta.BatchesHeld + " bad " + p.Delta.BadBatches + "]";
        foreach (int seat in editors)
            foreach (Peer p in room.Peers)
                Check(EventOf(p, seat) == 10 + seat, mode + ": P" + seat + "'s change lost on P" + p.Seat + " (event " + seat + " = " + EventOf(p, seat) + ")");
        room.AssertConverged(mode);
    }

    public static void Register(Action<string, Action> run)
    {
        Run = run;
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WORLD_WHOLE_BLOB") == "1") DeltaObject.MutateWholeBlob = true;
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SHARE_TRIGGERS") == "1") WorldItems.MutateShareTriggers = true;
        if (Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MAP_DOWNGRADE") == "1") WorldItems.MutateMapDowngrade = true;

        // ----- item split / rebuild -----
        Run("controller items: compose(template, decompose(template)) is byte-identical", delegate {
            byte[] c = Controller(new int[] { 9, 1, 4 }, new int[] { 3, 1, 2 }, new string[] { "b", "a" }, 12.5f, new int[] { 5, 1 });
            byte[] again = WorldItems.Compose(Ctl, c, WorldItems.Decompose(Ctl, c));
            Check(DeltaObject.Same(c, again), "rebuilt " + again.Length + " bytes vs " + c.Length);
        });
        // T-183 changed these two (T-168 had the string triggers as items): the trigger list is each PC's own now.
        Run("controller items: events and objectives are items; the string-trigger list (T-183) and the local clock tail are not", delegate {
            SortedDictionary<string, byte[]> a = WorldItems.Decompose(Ctl, Controller(new int[] { 1 }, new int[] { 7 }, new string[] { "x", "x" }, 1f, new int[] { 3 }));
            SortedDictionary<string, byte[]> b = WorldItems.Decompose(Ctl, Controller(new int[] { 1 }, new int[] { 7 }, new string[] { "y" }, 999f, new int[] { 3 }));
            Check(Dump(a) == Dump(b), "GameTime or the trigger list changed the items: {" + Dump(a) + "} vs {" + Dump(b) + "}");
            Check(a.Count == 2 && !a.ContainsKey("t:x") && BitConverter.ToInt32(a[EKey(1)], 0) == 7 && a.ContainsKey("o:3"), "items " + Dump(a));
        });
        Run("controller compose keeps THIS PC's clock tail and trigger list and applies the items", delegate {
            byte[] mine = Controller(new int[] { 1 }, new int[] { 0 }, new string[] { "mine", "mine" }, 42f, new int[0]);
            SortedDictionary<string, byte[]> items = WorldItems.Decompose(Ctl, mine);
            items[EKey(2)] = BitConverter.GetBytes(5); items["t:door"] = BitConverter.GetBytes(1); items.Remove("t:mine");
            byte[] rebuilt = WorldItems.Compose(Ctl, mine, items);
            Check(DeltaObject.Same(rebuilt, Controller(new int[] { 1, 2 }, new int[] { 0, 5 }, new string[] { "mine", "mine" }, 42f, new int[0])),
                "rebuilt triggers " + string.Join(",", WorldItems.Triggers(rebuilt).ToArray()));
        });
        Run("T-183: a partner's whole GameController keeps the receiver's own trigger list, takes the partner's events", delegate {
            byte[] incoming = Controller(new int[] { 1, 2 }, new int[] { 5, 6 }, new string[] { "kuroAttackCutsceneFinished" }, 7f, new int[] { 4 });
            byte[] local = Controller(new int[] { 1 }, new int[] { 0 }, new string[] { "returnFromWorldMap" }, 9f, new int[0]);
            byte[] applied = WorldItems.KeepLocalTriggers(incoming, local);
            Check(DeltaObject.Same(applied, Controller(new int[] { 1, 2 }, new int[] { 5, 6 }, new string[] { "returnFromWorldMap" }, 7f, new int[] { 4 })),
                "applied triggers " + string.Join(",", WorldItems.Triggers(applied).ToArray()) + ", items " + Dump(WorldItems.Decompose(Ctl, applied)));
        });
        Run("map items: byte-identical rebuild; revealed icons are monotonic", delegate {
            byte[] m = MapBytes(2, new int[] { 31, 30 }, 6);
            Check(DeltaObject.Same(m, WorldItems.Compose(WorldItems.SchemaMap, m, WorldItems.Decompose(WorldItems.SchemaMap, m))), "map rebuild");
            DeltaObject o = new DeltaObject(WorldItems.Decompose(WorldItems.SchemaMap, m));
            List<ItemOp> ops = o.LocalChanges(WorldItems.Decompose(WorldItems.SchemaMap, MapBytes(2, new int[] { 30 }, 6)));
            Check(ops.Count == 0, "un-revealing an icon proposed " + ops.Count + " ops");
            SortedDictionary<string, byte[]> after = o.OnCommitted(false, 1, new List<ItemOp> { new ItemOp("r:" + Hex(Guid(31)), null) }, WorldItems.Decompose(WorldItems.SchemaMap, m));
            Check(after == null && o.Confirmed.ContainsKey("r:" + Hex(Guid(31))), "a removal of a revealed icon was applied");
        });
        Run("map rebuild keeps native area and icon order and appends only keys belonging to each area", delegate {
            byte[] original;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(12);
                for (int area = 0; area < 12; area++)
                {
                    w.Write(2); w.Write(5); w.Write(2); w.Write(1); w.Write(1);
                    w.Write(1); w.Write(Guid(area)); w.Write(area);
                }
                w.Write(1); w.Write(Guid(100)); w.Write(-1); w.Flush();
                original = stream.ToArray();
            }
            var items = WorldItems.Decompose(WorldItems.SchemaMap, original);
            Check(DeltaObject.Same(original, WorldItems.Compose(WorldItems.SchemaMap, original, items)), "native order changed");
            items.Remove("a1.s5"); items["a10.s7"] = BitConverter.GetBytes(2);
            items["a10.i" + Hex(Guid(80))] = BitConverter.GetBytes(8);
            items["r:" + Hex(Guid(101))] = BitConverter.GetBytes(1);
            var result = WorldItems.Decompose(WorldItems.SchemaMap, WorldItems.Compose(WorldItems.SchemaMap, original, items));
            Check(result.Count == items.Count, "wrong number of keys");
            foreach (var pair in items) Check(result.ContainsKey(pair.Key) && DeltaObject.Same(result[pair.Key], pair.Value), "wrong " + pair.Key);
        });
        // ----- T-214: map exploration (area states "a<i>.s<face>": Hidden 0 < Discovered 1 < Visited 2) only grows -----
        Run("T-214 map: a late batch with a lower level, or a removal, does not take an explored cell back", delegate {
            DeltaObject o = new DeltaObject(Cells(5, 2));
            SortedDictionary<string, byte[]> shown = o.OnCommitted(false, 1, new List<ItemOp> { new ItemOp("a0.s5", BitConverter.GetBytes(1)) }, Cells(5, 2));
            Check(shown == null && Level(o.Confirmed, 5) == 2, "a partner's older 'discovered' lowered a visited cell: shown "
                + (shown == null ? "unchanged" : Dump(shown)) + ", agreed " + Level(o.Confirmed, 5));
            shown = o.OnCommitted(false, 2, new List<ItemOp> { new ItemOp("a0.s5", null) }, Cells(5, 2));
            Check(shown == null && Level(o.Confirmed, 5) == 2, "a removal hid a visited cell: agreed " + Level(o.Confirmed, 5));
        });
        Run("T-214 map: every PC ends with the highest level, whatever order the batches are in", delegate {
            foreach (int[] order in new int[][] { new int[] { 2, 1 }, new int[] { 1, 2 } })
            {
                DeltaObject o = new DeltaObject(Cells());
                SortedDictionary<string, byte[]> live = Cells();
                uint id = 0;
                foreach (int level in order)
                {
                    SortedDictionary<string, byte[]> shown = o.OnCommitted(false, ++id, new List<ItemOp> { new ItemOp("a0.s5", BitConverter.GetBytes(level)) }, live);
                    if (shown != null) live = shown;
                }
                Check(Level(live, 5) == 2 && Level(o.Confirmed, 5) == 2, "order " + order[0] + "," + order[1] + ": shown " + Level(live, 5) + ", agreed " + Level(o.Confirmed, 5));
            }
        });
        Run("T-214 map: a partner's higher level reaches a cell this PC raised itself and has not proposed, or not got back, yet", delegate {
            List<ItemOp> visited = new List<ItemOp> { new ItemOp("a0.s5", BitConverter.GetBytes(2)) };
            // Discovered here a moment ago (not proposed yet); the partner walked through it.
            DeltaObject o = new DeltaObject(Cells());
            SortedDictionary<string, byte[]> live = Cells(5, 1);
            SortedDictionary<string, byte[]> shown = o.OnCommitted(false, 1, visited, live);
            Check(shown != null && Level(shown, 5) == 2, "not proposed yet: the partner's 'visited' is not shown here: " + (shown == null ? "unchanged" : Dump(shown)));
            Check(o.LocalChanges(shown ?? live).Count == 0, "the agreed 'visited' leaves nothing to propose: " + o.LocalChanges(shown ?? live).Count + " ops");
            // The same with my 'discovered' already on its way to the arbiter.
            o = new DeltaObject(Cells()); live = Cells(5, 1);
            o.AddPending(7, o.LocalChanges(live));
            shown = o.OnCommitted(false, 1, visited, live);
            Check(shown != null && Level(shown, 5) == 2, "pending: the partner's 'visited' is not shown here: " + (shown == null ? "unchanged" : Dump(shown)));
            SortedDictionary<string, byte[]> back = o.OnCommitted(true, 7, new List<ItemOp> { new ItemOp("a0.s5", BitConverter.GetBytes(1)) }, shown ?? live);
            Check(back == null && Level(o.Confirmed, 5) == 2, "my older 'discovered' coming back lowered the cell: agreed " + Level(o.Confirmed, 5));
            // The other way round: my unproposed 'visited' stays and is still proposed after a partner's 'discovered'.
            o = new DeltaObject(Cells()); live = Cells(5, 2);
            shown = o.OnCommitted(false, 1, new List<ItemOp> { new ItemOp("a0.s5", BitConverter.GetBytes(1)) }, live);
            List<ItemOp> ops = o.LocalChanges(shown ?? live);
            Check(shown == null && ops.Count == 1 && BitConverter.ToInt32(ops[0].Value, 0) == 2, "my unproposed 'visited' was lost: " + ops.Count + " ops");
        });
        Run("T-214 map: a full snapshot keeps a cell this PC explored but has not proposed yet, and the next scan proposes it", delegate {
            DeltaObject o = new DeltaObject(Cells(3, 1));
            SortedDictionary<string, byte[]> live = Cells(3, 1, 5, 2);              // walked through cell 5 while the snapshot was on its way
            SortedDictionary<string, byte[]> shown = o.Reset(Cells(3, 1), live);
            Check(Level(shown, 5) == 2, "the snapshot took the unproposed cell back: {" + Dump(shown) + "}");
            List<ItemOp> ops = o.LocalChanges(shown);
            Check(ops.Count == 1 && ops[0].Key == "a0.s5" && BitConverter.ToInt32(ops[0].Value, 0) == 2, "the kept cell is not proposed: " + ops.Count + " ops");
        });
        Run("T-214 map: a checkpoint rollback still lowers the map (this PC loaded the checkpoint, the snapshot agrees)", delegate {
            DeltaObject o = new DeltaObject(Cells(3, 1, 5, 2));                     // agreed before the rollback
            SortedDictionary<string, byte[]> live = Cells(3, 1);                    // this PC's own checkpoint load put the map back
            Check(o.LocalChanges(live).Count == 0, "the rolled-back cell was proposed as a change: " + o.LocalChanges(live).Count + " ops");
            SortedDictionary<string, byte[]> shown = o.Reset(Cells(3, 1), live);
            Check(!shown.ContainsKey("a0.s5") && !o.Confirmed.ContainsKey("a0.s5"), "the rolled-back cell came back: {" + Dump(shown) + "}");
        });
        Run("T-214 map: a snapshot object is merged with the live map of this PC (WorldDeltaPeer.SnapshotObject)", delegate {
            OneObject game = new OneObject { Bytes = MapStates(new int[] { 3, 5 }, new int[] { 1, 2 }) };
            WorldDeltaPeer peer = new WorldDeltaPeer(delegate { return null; }, game);
            foreach (string pass in new string[] { "first sight", "known object" })
            {
                byte[] applied = peer.SnapshotObject(Scene, Obj, WorldItems.SchemaMap, MapStates(new int[] { 3, 9 }, new int[] { 2, 1 }));
                SortedDictionary<string, byte[]> items = WorldItems.Decompose(WorldItems.SchemaMap, applied);
                Check(Level(items, 5) == 2 && Level(items, 3) == 2 && Level(items, 9) == 1,
                    pass + ": cells 3/5/9 = " + Level(items, 3) + "/" + Level(items, 5) + "/" + Level(items, 9) + ", want 2/2/1");
            }
        });
        Run("batch wire form round-trips (removal and value ops)", delegate {
            DeltaBatch b = new DeltaBatch { Id = 77, Scene = Guid(1), Object = Guid(2), Schema = Ctl, Type = "GameController",
                Ops = new List<ItemOp> { new ItemOp(EKey(3), BitConverter.GetBytes(9)), new ItemOp("t:gone", null) } };
            DeltaBatch c = DeltaBatch.Decode(b.Encode());
            Check(c.Id == 77 && c.Type == "GameController" && c.Ops.Count == 2 && c.Ops[1].Value == null
                && BitConverter.ToInt32(c.Ops[0].Value, 0) == 9 && Hex(c.Object) == Hex(Guid(2)), "decoded differently");
            bool threw = false;
            byte[] bad = b.Encode(); bad[0] = 9;
            try { DeltaBatch.Decode(bad); } catch (InvalidDataException) { threw = true; }
            Check(threw, "unknown version accepted");
        });
        Run("big change splits into batches under the size limit", delegate {
            List<ItemOp> ops = new List<ItemOp>();
            for (int i = 0; i < 2000; i++) ops.Add(new ItemOp("e:" + i.ToString("x32"), BitConverter.GetBytes(i)));
            List<List<ItemOp>> parts = DeltaBatch.Split(ops, 14);
            int total = 0;
            foreach (List<ItemOp> p in parts)
            {
                total += p.Count;
                Check(new DeltaBatch { Type = "GameController", Ops = p }.Encode().Length <= DeltaBatch.MaxBytes, "a part exceeds the limit");
            }
            Check(parts.Count > 1 && total == 2000, parts.Count + " parts, " + total + " ops");
        });

        // ----- the acceptance scenarios: different items within one RTT all survive -----
        foreach (bool server in new bool[] { false, true })
        {
            string m = server ? "C-server" : "P2P";
            bool s = server;
            Run(m + " 2P: host and guest change different world events in the same RTT, both survive", () => SameRttScenario(2, s, new int[] { 2, 1 }, 5));
            Run(m + " 3P: two guests change different world events in the same RTT, both survive", () => SameRttScenario(3, s, new int[] { 2, 3 }, 10));
            Run(m + " 3P: host and a guest in the same RTT, both survive", () => SameRttScenario(3, s, new int[] { 3, 1 }, 10));
            Run(m + " 4P: three guests and the host all in the same RTT, all survive", () => SameRttScenario(4, s, new int[] { 2, 3, 4, 1 }, 30));
            Run(m + " 4P: two guests in the same RTT, both survive", () => SameRttScenario(4, s, new int[] { 4, 2 }, 1));
        }

        Run("P2P 3P: same item changed by two guests in one RTT converges everywhere", delegate {
            Room room = new Room(3, false, Start()); room.Net.Delay = (a, b) => a == 3 || b == 3 ? 140 : 60;
            room[2].Edit(SetEvent(1, 21), true); room[3].Edit(SetEvent(1, 31), true);
            room.Net.Drain(); room.AssertConverged("same item");
            int v = EventOf(room[1], 1);
            Check(v == 21 || v == 31, "value " + v);
        });
        // T-183 (replaces T-168's "trigger added on one PC and removed on another: both"): every PC runs its own copy of a
        // sequence chain (the starter natively, the others as StorySync mirrors), and the chain's string triggers are
        // its continuation across a scene load. Oracle (ticket): the host's own chain still continues (04 starts) when
        // the guest's mirror registered and consumed the same trigger first; a consumed trigger never comes back.
        foreach (bool server in new bool[] { false, true })
        {
            bool s = server; string m = s ? "C-server" : "P2P";
            Run("T-183 " + m + ": the guest's mirror registers and consumes returnFromWorldMap first; the host's own registration survives", delegate {
                Room room = new Room(2, s, Controller(new int[] { 1 }, new int[] { 0 }, new string[0], 0f, new int[0]));
                room.Net.Delay = (a, b) => 100;
                double t0 = room.Net.Now;
                AddTrigger(room[2], "returnFromWorldMap"); room.Net.RunUntil(t0 + 20);      // guest's world-map sequence, step 27
                RemoveTrigger(room[2], "returnFromWorldMap"); room.Net.RunUntil(t0 + 40);   // guest's 04 TriggerByString fires
                AddTrigger(room[1], "returnFromWorldMap");                                   // host's own world-map sequence, later
                room[1].Edit(SetEvent(1, 3), true);                                          // a real world event still travels
                room.Net.Drain();
                Check(Count(room[1], "returnFromWorldMap") == 1, "host's trigger count " + Count(room[1], "returnFromWorldMap") + ": its 04 would not start");
                Check(Count(room[2], "returnFromWorldMap") == 0, "guest got its consumed trigger back: " + Count(room[2], "returnFromWorldMap"));
                Check(EventOf(room[2], 1) == 3, "the host's world event did not reach the guest: " + EventOf(room[2], 1));
            });
            Run("T-183 " + m + ": the host consumed kuroAttackCutsceneFinished; the guest's later registration never re-arms it on the host", delegate {
                Room room = new Room(2, s, Controller(new int[0], new int[0], new string[0], 0f, new int[0]));
                room.Net.Delay = (a, b) => 100;
                double t0 = room.Net.Now;
                AddTrigger(room[1], "kuroAttackCutsceneFinished"); room.Net.RunUntil(t0 + 20);    // host: Kuro scene onFinish
                RemoveTrigger(room[1], "kuroAttackCutsceneFinished"); room.Net.RunUntil(t0 + 40); // host: 03's TriggerByString
                AddTrigger(room[2], "kuroAttackCutsceneFinished");                                // guest's mirror, later
                int worst = 0;
                for (int i = 0; i < 40; i++)
                { room.Net.RunUntil(room.Net.Now + 25); worst = Math.Max(worst, Count(room[1], "kuroAttackCutsceneFinished")); }
                Check(worst == 0, "host had kuroAttackCutsceneFinished re-registered (" + worst + "): 03 would run again on its next tree load");
                Check(Count(room[2], "kuroAttackCutsceneFinished") == 1, "guest lost its own registration: " + Count(room[2], "kuroAttackCutsceneFinished"));
            });
        }
        Run("a change not yet scanned is not undone by an incoming batch for the same item; it wins after the scan", delegate {
            Room room = new Room(2, false, Start()); room.Net.Delay = (a, b) => 100;
            room[1].Edit(SetEvent(4, 1), true);          // host proposes 1 (committed at once)
            room[2].Edit(SetEvent(4, 2), false);         // guest changes it locally, scan not run yet
            room.Net.RunUntil(room.Net.Now + 150);       // the host's batch reaches the guest
            Check(EventOf(room[2], 4) == 2, "guest's unscanned change was overwritten: " + EventOf(room[2], 4));
            room[2].Scan(); room.Net.Drain();
            room.AssertConverged("unscanned"); Check(EventOf(room[1], 4) == 2, "later change did not win: " + EventOf(room[1], 4));
        });
        Run("C-server: my own batch coming back after another seat's batch on the same item keeps the arbiter's order", delegate {
            Room room = new Room(3, true, Start());
            room.Net.Delay = (a, b) => (a == 2 && b == 0) ? 20 : (a == 3 && b == 0) ? 150 : 100;
            room[3].Edit(SetEvent(5, 3), true); room.Net.RunUntil(room.Net.Now + 5);
            room[2].Edit(SetEvent(5, 2), true);          // reaches the server first, ordered first
            room.Net.Drain(); room.AssertConverged("order");
            Check(EventOf(room[2], 5) == 3, "last ordered (seat 3) should win, got " + EventOf(room[2], 5));
        });
        foreach (bool server in new bool[] { false, true })
        {
            bool s = server;
            Run((s ? "C-server" : "P2P") + " late join: snapshot taken while a guest's batch is in flight, joiner converges", delegate {
                Room room = new Room(4, s, Start()); room.Net.Delay = (a, b) => 100;
                Peer late = room[4];
                late.Delta.BeginSnapshot(); late.Game.Bytes = Start(); late.Ready = false;   // re-joins: old copy, waiting
                room[2].Edit(SetEvent(2, 12), true);
                room.Net.RunUntil(room.Net.Now + 30);
                room[1].Edit(SetEvent(1, 11), true);
                room.Snapshot(4);                             // host has its own change, not seat 2's yet
                room.Net.RunUntil(room.Net.Now + 20);
                room[3].Edit(SetEvent(3, 13), true);
                late.Ready = true;
                room.Net.Drain(); late.Delta.Flush();
                room.AssertConverged("late join");
                for (int n = 1; n <= 3; n++) Check(EventOf(late, n) == 10 + n, "joiner misses event " + n + ": " + EventOf(late, n));
            });
        }
        Run("C-server rollback replays changes the guest saw before the snapshot start", delegate {
            Room room = new Room(3, true, Start());
            room.Net.Delay = (from, to) => from == 0 && to == 1 ? 300 : 10;
            room[3].Edit(SetEvent(3, 17), true);
            room.Net.RunUntil(room.Net.Now + 30);
            Check(EventOf(room[2], 3) == 17 && EventOf(room[1], 3) == 0, "fixture must put the guest ahead");
            room[2].Delta.BeginSnapshot();
            room.Snapshot(2);
            room[3].Edit(SetEvent(4, 19), true);
            room.Net.Drain();
            room.AssertConverged("snapshot sent behind the guest");
            Check(EventOf(room[2], 3) == 17 && EventOf(room[2], 4) == 19, "lost changes across the watermark");
        });
        Run("seeded fuzz: 4P P2P and C-server, random edits and link delays: own items keep their last write, all converge", delegate {
            Random rng = new Random(168);
            for (int round = 0; round < 12; round++)
            {
                bool server = round % 2 == 1;
                Room room = new Room(4, server, Controller(new int[0], new int[0], new string[0], 0f, new int[0]));
                Dictionary<string, int> delays = new Dictionary<string, int>();
                room.Net.Delay = (a, b) => { string k = a + ">" + b; int d; if (!delays.TryGetValue(k, out d)) delays[k] = d = 10 + rng.Next(200); return d; };
                Dictionary<int, int> last = new Dictionary<int, int>();
                for (int step = 0; step < 60; step++)
                {
                    room.Net.RunUntil(room.Net.Now + rng.Next(40));
                    int seat = 1 + rng.Next(4), item = seat * 10 + rng.Next(3), value = rng.Next(1000);
                    bool shared = rng.Next(5) == 0;
                    if (shared) item = 99;
                    else last[item] = value;
                    room[seat].Edit(SetEvent(item, value), rng.Next(3) != 0);
                    if (rng.Next(4) == 0) room[1 + rng.Next(4)].Scan();
                }
                foreach (Peer p in room.Peers) p.Scan();
                room.Net.Drain();
                room.AssertConverged("round " + round + (server ? " C-server" : " P2P"));
                foreach (KeyValuePair<int, int> want in last)
                    Check(EventOf(room[1], want.Key) == want.Value, "round " + round + ": item " + want.Key + " = " + EventOf(room[1], want.Key) + ", last write " + want.Value);
            }
        });
    }
}
