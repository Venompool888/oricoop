using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Authority;
using OriCoop.World;

// A world snapshot on its way already holds every batch up to its watermark. A full held queue during that wait (the
// arbiter replays up to 4096 old batches to a seat that joins) must not ask for ANOTHER full snapshot; only a dropped
// batch newer than the watermark needs one.
static class CloudDeltaHeldTests
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

    static byte[] Controller(int value)
    {
        using (MemoryStream m = new MemoryStream())
        using (BinaryWriter w = new BinaryWriter(m))
        {
            w.Write(1); w.Write(Guid(1)); w.Write(value);
            w.Write(0);
            w.Write(0f); w.Write(0f); w.Write(0); w.Write(0); w.Write(false);
            w.Write(0);
            w.Flush(); return m.ToArray();
        }
    }

    sealed class Game : IWorldDeltaGame
    {
        public byte[] Bytes = Controller(0); public int Resyncs;
        public bool TryRead(byte[] scene, byte[] id, out byte[] bytes) { bytes = Bytes; return true; }
        public void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes) { Bytes = bytes; }
        public void NeedResync(string why) { Resyncs++; }
    }

    static byte[] Batch(uint id, int value)
    {
        DeltaBatch b = new DeltaBatch { Id = id, Scene = Scene, Object = Obj, Schema = Ctl, Type = "GameController" };
        b.Ops.Add(new ItemOp("e:" + BitConverter.ToString(Guid(1)).Replace("-", "").ToLowerInvariant(), BitConverter.GetBytes(value)));
        return b.Encode();
    }
    static CommittedEvent Event(uint id, int from, int value)
    { return new CommittedEvent { Id = id, From = from, Seq = id, Kind = DeltaBatch.Kind, Key = "w", Data = Batch(id, value) }; }

    static int Value(Game g) { return BitConverter.ToInt32(g.Bytes, 20); }

    static int Main()
    {
        AuthorityClient authority = new AuthorityClient(AuthorityMode.HostP2P, () => 2, (m, r) => { }, false);

        Run("a held queue that overflows while a snapshot is on its way asks for no second snapshot", delegate {
            Game game = new Game(); WorldDeltaPeer peer = new WorldDeltaPeer(() => authority, game);
            peer.BeginSnapshot();
            uint total = WorldDeltaPeer.MaxHeld + 10;
            for (uint id = 1; id <= total; id++) peer.OnCommitted(Event(id, 1, (int)id), true);
            Check(game.Resyncs == 0, "asked for " + game.Resyncs + " resyncs while the snapshot was still coming");
            peer.EndSnapshot(total);
            Check(game.Resyncs == 0, "asked for " + game.Resyncs + " resyncs although the snapshot held every dropped batch");
            Check(peer.HeldCount == 0 && peer.Watermark == total, "held " + peer.HeldCount + ", watermark " + peer.Watermark);
        });

        Run("a batch dropped from the queue that the snapshot does not hold still asks for a resync", delegate {
            Game game = new Game(); WorldDeltaPeer peer = new WorldDeltaPeer(() => authority, game);
            peer.BeginSnapshot();
            for (uint id = 101; id <= 100 + WorldDeltaPeer.MaxHeld + 1; id++) peer.OnCommitted(Event(id, 1, (int)id), true);
            peer.EndSnapshot(50);   // the snapshot was captured before batch 101 (the oldest, dropped one)
            Check(game.Resyncs == 1, "resyncs " + game.Resyncs);
        });

        Run("an overflow while the game cannot take world state (no snapshot coming) still asks for a resync", delegate {
            Game game = new Game(); WorldDeltaPeer peer = new WorldDeltaPeer(() => authority, game);
            for (uint id = 1; id <= WorldDeltaPeer.MaxHeld + 1; id++) peer.OnCommitted(Event(id, 1, (int)id), false);
            Check(game.Resyncs == 1, "resyncs " + game.Resyncs);
        });

        // The real path: a long session's arbiter holds 4096 batches; a new seat joins (P2P HostArbiter.SeatJoined ->
        // Arbiter.JoinAt replays them all), its world sync waits for the host's snapshot (WorldSync.ResetDelta), and the
        // partners keep playing during its save join.
        Run("a seat joining a long session asks for no second full snapshot", delegate {
            Arbiter arbiter = new Arbiter("", new ArbiterOptions(), 0);
            arbiter.JoinAt(1, 0, "", 0);
            for (uint seq = 1; seq <= 5000; seq++)
                arbiter.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = seq, Kind = DeltaBatch.Kind, Key = "w", Data = Batch(seq, (int)seq) }, 0);
            Game game = new Game();
            AuthorityClient joiner = new AuthorityClient(AuthorityMode.HostP2P, () => 3, (m, r) => { }, false);
            WorldDeltaPeer peer = new WorldDeltaPeer(() => joiner, game);
            joiner.Committed += e => peer.OnCommitted(e, true);
            peer.BeginSnapshot();
            Action<List<Outgoing>> deliver = output => {
                foreach (Outgoing o in output) if (o.To == 3 || o.To == 0 || (o.To < 0 && -o.To != 3)) joiner.Receive(o.Message);
            };
            deliver(arbiter.JoinAt(3, 0, "", 0));
            for (uint seq = 5001; seq <= 5005; seq++)   // the host keeps exploring while the joiner loads
                deliver(arbiter.Handle(1, new CsMessage { Type = CsWire.CsAction, Seq = seq, Kind = DeltaBatch.Kind, Key = "w", Data = Batch(seq, (int)seq) }, 0));
            peer.EndSnapshot(5005);   // the host's snapshot, captured after all of them
            Check(game.Resyncs == 0, "the joiner asked for " + game.Resyncs + " more full snapshot(s)");
        });

        if (failures == 0) Console.WriteLine("ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
