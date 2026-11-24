using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Authority;
using OriCoop.World;

// A guest asks for a full resync (digest mismatch) and holds committed batches from that moment (WorldSync.
// TryRequestResync -> BeginSnapshot). Its own batch for an item comes back held, then a partner's later batch for the
// same item. The snapshot holds both (the partner's value, ordered last). SnapshotObject shows the guest's pending batch
// on top of the snapshot; once EndSnapshot learns that batch is inside the snapshot, the agreed value must show again.
static class CloudSnapshotPendingTests
{
    static int failures;
    static void Run(string name, Action body)
    {
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    static byte[] Guid(int n) { byte[] g = new byte[16]; g[0] = (byte)n; g[15] = 0x5A; return g; }
    static readonly byte[] Scene = new byte[16], Obj = Guid(200);
    const byte Ctl = WorldItems.SchemaController;

    // GameController with world events 1 and 2.
    static byte[] Controller(int one, int two)
    {
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(2); w.Write(Guid(1)); w.Write(one); w.Write(Guid(2)); w.Write(two);
            w.Write(0);
            w.Write(0f); w.Write(0f); w.Write(0); w.Write(0); w.Write(false);
            w.Write(0);
            w.Flush(); return m.ToArray();
        }
    }
    static int Event(byte[] bytes, int n) { return BitConverter.ToInt32(bytes, n == 1 ? 20 : 40); }

    sealed class Game : IWorldDeltaGame
    {
        public byte[] Bytes; public int Resyncs;
        public bool TryRead(byte[] scene, byte[] id, out byte[] bytes) { bytes = Bytes; return Bytes != null; }
        public void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes) { Bytes = bytes; }
        public void NeedResync(string why) { Resyncs++; }
    }

    // P2P: seat 1 hosts (HostArbiter), seat 2 is the guest. Messages are delivered when the test says so (FIFO).
    sealed class Pair
    {
        public readonly Queue<byte[]> ToGuest = new Queue<byte[]>(), ToHost = new Queue<byte[]>();
        public readonly HostArbiter Host;
        public readonly AuthorityClient GuestAuthority;
        public readonly Game HostGame = new Game(), GuestGame = new Game();
        public readonly WorldDeltaPeer HostDelta, GuestDelta;
        public Pair()
        {
            Host = new HostArbiter(() => 1, (seat, msg, rel) => ToGuest.Enqueue(msg), () => 0);
            GuestAuthority = new AuthorityClient(AuthorityMode.HostP2P, () => 2, (msg, rel) => ToHost.Enqueue(msg), false);
            HostGame.Bytes = Controller(0, 0); GuestGame.Bytes = Controller(0, 0);
            HostDelta = new WorldDeltaPeer(() => Host.Local, HostGame);
            GuestDelta = new WorldDeltaPeer(() => GuestAuthority, GuestGame);
            Host.Local.Committed += e => HostDelta.OnCommitted(e, true);
            GuestAuthority.Committed += e => GuestDelta.OnCommitted(e, true);
            Host.SeatJoined(1); Host.SeatJoined(2);
            HostDelta.Observe(Scene, Obj, Ctl, HostGame.Bytes); GuestDelta.Observe(Scene, Obj, Ctl, GuestGame.Bytes);
            Drain();
        }
        public void ToHostAll() { while (ToHost.Count > 0) Host.FromRemote(2, ToHost.Dequeue()); }
        public void ToGuestAll() { while (ToGuest.Count > 0) GuestAuthority.Receive(ToGuest.Dequeue()); }
        public void Drain() { while (ToHost.Count > 0 || ToGuest.Count > 0) { ToHostAll(); ToGuestAll(); } }
        public void Edit(bool host, int one, int two)
        {
            Game g = host ? HostGame : GuestGame; WorldDeltaPeer d = host ? HostDelta : GuestDelta;
            g.Bytes = Controller(one, two); d.LocalChange(Scene, Obj, Ctl, "GameController", g.Bytes);
        }
        // WorldSync.SendSnapshot on the host, then the guest's Receive of it (start marker, the object, end marker).
        public void Snapshot()
        {
            byte[] bytes = (byte[])HostGame.Bytes.Clone(); uint mark = HostDelta.Watermark;
            HostDelta.SentSnapshotObject(Scene, Obj, Ctl, bytes);
            ToGuestAll();                                   // the batches the host sent before its snapshot
            GuestDelta.BeginSnapshot();
            GuestGame.Bytes = GuestDelta.SnapshotObject(Scene, Obj, Ctl, bytes);
            GuestDelta.EndSnapshot(mark);
        }
    }

    static int Main()
    {
        Run("P2P: a partner's later value for an item survives a resync that also holds my older value", delegate {
            Pair p = new Pair();
            p.Edit(false, 1, 0);           // the guest sets event 1 to 1 and proposes it
            p.GuestDelta.BeginSnapshot();  // digest mismatch: the guest asks for a resync and holds batches from now on
            p.ToHostAll();                 // the host commits the guest's batch (id n), then the resync request
            p.Edit(true, 2, 0);            // the host sets event 1 to 2 right after (id n+1, ordered last)
            p.Snapshot();
            Check(Event(p.HostGame.Bytes, 1) == 2, "host " + Event(p.HostGame.Bytes, 1));
            Check(Event(p.GuestGame.Bytes, 1) == 2, "guest shows its own older value " + Event(p.GuestGame.Bytes, 1) + " after the snapshot, host has 2");
            int sent = p.GuestDelta.LocalChange(Scene, Obj, Ctl, "GameController", p.GuestGame.Bytes);
            p.Drain();
            Check(sent == 0, "the guest's next scan proposed " + sent + " batch(es) of its old value");
            Check(Event(p.HostGame.Bytes, 1) == 2 && Event(p.GuestGame.Bytes, 1) == 2,
                "after the next scan: host " + Event(p.HostGame.Bytes, 1) + ", guest " + Event(p.GuestGame.Bytes, 1));
        });

        Run("my batch still on its way to the arbiter stays on top of the snapshot", delegate {
            Pair p = new Pair();
            p.Edit(true, 2, 0); p.ToGuestAll();   // the host's change, applied by the guest
            p.Edit(false, 2, 7);           // the guest proposes event 2 = 7; it has not reached the host yet
            p.Snapshot();                  // a host snapshot without it (rollback, another seat's join)
            Check(Event(p.GuestGame.Bytes, 2) == 7, "the guest's pending change was hidden by the snapshot: " + Event(p.GuestGame.Bytes, 2));
            p.Drain();
            Check(Event(p.HostGame.Bytes, 2) == 7 && Event(p.GuestGame.Bytes, 2) == 7, "host " + Event(p.HostGame.Bytes, 2) + ", guest " + Event(p.GuestGame.Bytes, 2));
        });

        Run("an item I changed again while the snapshot came in is left as I have it", delegate {
            DeltaObject o = new DeltaObject(WorldItems.Decompose(Ctl, Controller(2, 0)));
            List<ItemOp> mine = new List<ItemOp> { new ItemOp(new List<string>(WorldItems.Decompose(Ctl, Controller(0, 0)).Keys)[0], BitConverter.GetBytes(1)) };
            o.AddPending(1, mine); o.Acknowledge(1);
            SortedDictionary<string, byte[]> shown = o.Superseded(mine, WorldItems.Decompose(Ctl, Controller(5, 0)));
            Check(shown == null, "a newer local value was replaced");
            shown = o.Superseded(mine, WorldItems.Decompose(Ctl, Controller(1, 0)));
            Check(shown != null && Event(WorldItems.Compose(Ctl, Controller(1, 0), shown), 1) == 2, "the old batch value stayed on top");
        });

        if (failures == 0) Console.WriteLine("ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
