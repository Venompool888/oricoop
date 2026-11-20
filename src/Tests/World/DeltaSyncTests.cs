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

        // ----- item split / rebuild -----
        Run("controller items: compose(template, decompose(template)) is byte-identical", delegate {
            byte[] c = Controller(new int[] { 9, 1, 4 }, new int[] { 3, 1, 2 }, new string[] { "b", "a" }, 12.5f, new int[] { 5, 1 });
            byte[] again = WorldItems.Compose(Ctl, c, WorldItems.Decompose(Ctl, c));
            Check(DeltaObject.Same(c, again), "rebuilt " + again.Length + " bytes vs " + c.Length);
        });
        Run("controller items: events, trigger counts and objectives are items; the local clock tail is not", delegate {
            SortedDictionary<string, byte[]> a = WorldItems.Decompose(Ctl, Controller(new int[] { 1 }, new int[] { 7 }, new string[] { "x", "x" }, 1f, new int[] { 3 }));
            SortedDictionary<string, byte[]> b = WorldItems.Decompose(Ctl, Controller(new int[] { 1 }, new int[] { 7 }, new string[] { "x", "x" }, 999f, new int[] { 3 }));
            Check(Dump(a) == Dump(b), "GameTime changed the items");
            Check(a.Count == 3 && BitConverter.ToInt32(a["t:x"], 0) == 2 && BitConverter.ToInt32(a[EKey(1)], 0) == 7 && a.ContainsKey("o:3"), "items " + Dump(a));
        });
        Run("controller compose keeps THIS PC's clock tail and applies the items", delegate {
            byte[] mine = Controller(new int[] { 1 }, new int[] { 0 }, new string[0], 42f, new int[0]);
            SortedDictionary<string, byte[]> items = WorldItems.Decompose(Ctl, mine);
            items[EKey(2)] = BitConverter.GetBytes(5); items["t:door"] = BitConverter.GetBytes(1);
            byte[] rebuilt = WorldItems.Compose(Ctl, mine, items);
            Check(DeltaObject.Same(rebuilt, Controller(new int[] { 1, 2 }, new int[] { 0, 5 }, new string[] { "door" }, 42f, new int[0])), "rebuilt bytes differ");
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
        Run("C-server 3P: trigger added on one PC and another trigger removed on another, same RTT: both", delegate {
            Room room = new Room(3, true, Controller(new int[] { 1 }, new int[] { 0 }, new string[] { "old" }, 0f, new int[0]));
            room.Net.Delay = (a, b) => 100;
            room[2].Edit(items => items["t:lever"] = BitConverter.GetBytes(1), true);
            room[3].Edit(items => items.Remove("t:old"), true);
            room.Net.Drain(); room.AssertConverged("triggers");
            Check(room[1].Items.ContainsKey("t:lever") && !room[1].Items.ContainsKey("t:old"), "triggers " + Dump(room[1].Items));
        });
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
