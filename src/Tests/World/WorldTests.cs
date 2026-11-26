using System;
using System.Collections.Generic;
using OriCoop.World;

static class WorldTests
{
    static int cases, failures;
    static void Run(string name, Action body)
    {
        cases++;
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    sealed class Ev { public string Name; public string Scene; public bool Gone; }

    static int Main()
    {
        // T-074 review: two held events for the same object must replay as the partner sent them.
        Run("held events for one object come out in arrival order", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "damage", Scene = "a" }); q.Add(new Ev { Name = "collapse", Scene = "a" });
            Check(q.Any(e => e.Name == "damage" && e.Scene == "a"), "deferred damage is visible before replay");
            List<Ev> got = q.TakeReady(e => e.Gone, e => e.Scene == "a", out dropped);
            Check(got.Count == 2 && got[0].Name == "damage" && got[1].Name == "collapse", "order " + string.Join(",", got.ConvertAll(e => e.Name).ToArray()));
            Check(q.Count == 0 && dropped == 0 && !q.Any(e => e.Name == "damage"), "no deferred damage remains");
        });
        Run("events of a scene still loading stay held, in order", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "trigger", Scene = "b" }); q.Add(new Ev { Name = "rope", Scene = "a" }); q.Add(new Ev { Name = "stomper", Scene = "b" });
            List<Ev> first = q.TakeReady(e => e.Gone, e => e.Scene == "a", out dropped);
            Check(first.Count == 1 && first[0].Name == "rope" && q.Count == 2, "first pass");
            List<Ev> second = q.TakeReady(e => e.Gone, e => true, out dropped);
            Check(second.Count == 2 && second[0].Name == "trigger" && second[1].Name == "stomper", "second pass order");
        });
        Run("events whose object is gone are dropped and counted", delegate {
            HeldEvents<Ev> q = new HeldEvents<Ev>(); int dropped;
            q.Add(new Ev { Name = "x", Scene = "a", Gone = true }); q.Add(new Ev { Name = "y", Scene = "a" });
            List<Ev> got = q.TakeReady(e => e.Gone, e => true, out dropped);
            Check(got.Count == 1 && got[0].Name == "y" && dropped == 1, "dropped " + dropped);
        });
        // T-141: 3 seats over P2P / relay (guests connect only to the host). Guest A (seat 2) opens a door; guest B (seat 3)
        // must get it from the host's relay of the ACCEPTED blob, not only from a digest resync seconds later.
        Run("3P host relays an accepted guest blob to the other guest, not back to the proposer", delegate {
            Dictionary<int, Dictionary<string, int>> world = new Dictionary<int, Dictionary<string, int>>();
            for (int seat = 1; seat <= 3; seat++) world[seat] = new Dictionary<string, int>();
            int[] hostRemotes = { 2, 3 };
            List<int[]> deliveries = new List<int[]>();   // { from, to }
            // A changes the door and proposes it to the host (its only peer).
            world[2]["door"] = 1;
            RelayPlan host = WorldRelay.OnRemoteBlob(true, false, 2, hostRemotes, 10f, WorldRelay.Never);
            Check(host.Apply && !host.SendOwnToAll, "host accepts");
            world[1]["door"] = world[2]["door"];
            foreach (int to in host.ForwardTo) deliveries.Add(new int[] { 1, to });
            foreach (int[] d in deliveries)
            {
                RelayPlan guest = WorldRelay.OnRemoteBlob(false, false, d[0], new int[] { 1 }, 10.1f, WorldRelay.Never);
                Check(guest.Apply && guest.ForwardTo.Length == 0 && !guest.SendOwnToAll, "guest applies and relays nothing");
                world[d[1]]["door"] = world[d[0]]["door"];
            }
            Check(world[3].ContainsKey("door") && world[3]["door"] == 1, "guest B sees the door A opened");
            Check(Array.IndexOf(host.ForwardTo, 2) < 0, "no echo to the proposer");
            Check(host.ForwardTo.Length == 1 && host.ForwardTo[0] == 3, "forwarded to " + string.Join(",", Array.ConvertAll(host.ForwardTo, s => s.ToString())));
        });
        Run("4P host relays to both other guests once each", delegate {
            RelayPlan p = WorldRelay.OnRemoteBlob(true, false, 3, new int[] { 2, 3, 4, 4 }, 5f, WorldRelay.Never);
            Check(p.Apply && p.ForwardTo.Length == 2 && p.ForwardTo[0] == 2 && p.ForwardTo[1] == 4, "targets " + string.Join(",", Array.ConvertAll(p.ForwardTo, s => s.ToString())));
        });
        Run("2P host has no one else to relay to", delegate {
            RelayPlan p = WorldRelay.OnRemoteBlob(true, false, 2, new int[] { 2 }, 5f, WorldRelay.Never);
            Check(p.Apply && p.ForwardTo.Length == 0, "targets " + p.ForwardTo.Length);
        });
        Run("host's own edit within 0.5 s wins: nothing applied or relayed, host blob to all", delegate {
            RelayPlan p = WorldRelay.OnRemoteBlob(true, false, 2, new int[] { 2, 3 }, 10f, 9.7f);
            Check(!p.Apply && p.SendOwnToAll && p.ForwardTo.Length == 0, "apply=" + p.Apply + " own=" + p.SendOwnToAll + " fwd=" + p.ForwardTo.Length);
            RelayPlan later = WorldRelay.OnRemoteBlob(true, false, 2, new int[] { 2, 3 }, 10f, 9.4f);
            Check(later.Apply && later.ForwardTo.Length == 1, "an older own edit no longer blocks");
        });
        Run("C-server session: the server already relays, host sends no extra copy", delegate {
            RelayPlan p = WorldRelay.OnRemoteBlob(true, true, 2, new int[] { 2, 3, 4 }, 10f, WorldRelay.Never);
            Check(p.Apply && p.ForwardTo.Length == 0 && !p.SendOwnToAll, "fwd=" + p.ForwardTo.Length);
        });
        // T-144: rock trigger fires on A (A sends, B runs it) -> wipe, both PCs reload the checkpoint -> A passes again:
        // A must send again and B must run it again (before: A skipped the send, B refused the id; rock only on A).
        // T-207 (1): the partner's Sein pickup script runs (step 27, running); its progress reaches this PC, which
        // then dies and reloads. Expected from the ticket: this PC's checkpoint does not get the partner's step.
        Run("T-207.1 a partner's story-sequence progress is not written into this PC's checkpoint", delegate {
            StoryProgressKeys keys = new StoryProgressKeys();
            Check(!keys.StoreInCheckpoint("scene:pickup", true, true), "loaded story progress stays out of the checkpoint");
            Check(keys.Known("scene:pickup") && keys.Count == 1, "its key is remembered");
            Check(!keys.StoreInCheckpoint("scene:pickup", false, false), "the same object after its scene unloaded: still out");
            Check(!keys.StoreInCheckpoint("scene:pickup", true, true), "and again once it is loaded");
        });
        Run("T-207.1 other world objects go into the checkpoint as before", delegate {
            StoryProgressKeys keys = new StoryProgressKeys();
            Check(keys.StoreInCheckpoint("scene:door", true, false), "a loaded door is stored");
            Check(keys.StoreInCheckpoint("scene:ambient-sequence", true, false), "a loaded non-story sequence's progress is stored (world state)");
            Check(keys.StoreInCheckpoint("far:unknown", false, false), "an object never seen loaded is stored (cannot be told apart)");
            Check(keys.Count == 0, "none of them is remembered as story progress");
            keys.StoreInCheckpoint("scene:pickup", true, true); keys.Clear();
            Check(keys.StoreInCheckpoint("scene:pickup", false, false), "after a clear (new session) nothing is remembered");
        });
        // Review finding 7: this PC ran the story itself (its progress object was loaded and seen by the scan), left the
        // scene, and only then the partner's progress for it arrives for the first time.
        Run("T-207 review 7: story progress seen loaded in the scan stays out of the checkpoint after its scene unloaded", delegate {
            StoryProgressKeys keys = new StoryProgressKeys();
            keys.Remember("scene:pickup");                                   // WorldSync.Capture saw it loaded
            Check(!keys.StoreInCheckpoint("scene:pickup", false, false), "the first partner blob arrives after the scene unloaded: not stored");
            Check(keys.StoreInCheckpoint("scene:other", false, false), "an object never seen loaded is still stored");
        });
        Run("one-shot hazard fires on both PCs again after a checkpoint reload", delegate {
            OneShotTriggers a = new OneShotTriggers(), b = new OneShotTriggers();
            const int rock = 77;
            Check(a.TryFire(rock), "A's first pass sends");
            Check(!b.HasFired(rock) && b.TryFire(rock), "B runs the first event");
            Check(!a.TryFire(rock) && b.HasFired(rock), "same life: no second send, B refuses a repeat");
            a.CheckpointRestored(); b.CheckpointRestored();
            Check(a.TryFire(rock), "after the reload A's pass sends again");
            Check(!b.HasFired(rock), "after the reload B runs the event again");
        });        // T-145: seat 2 sends events 1..3 and leaves; seat 3 stays (no session Reset). A new process takes seat 2 and its
        // seq starts at 1 again: once it has joined, its events must be applied, while seat 3's history stays intact.
        Run("new process in a freed seat: its first events are applied after it joins", delegate {
            SeenEvents seen = new SeenEvents(4096);
            for (uint s = 1; s <= 3; s++) Check(seen.FirstTime(2, s), "old seat-2 event " + s);
            Check(seen.FirstTime(3, 1), "seat-3 event 1");
            // Seat 2 leaves: a late (forwarded / unreliable) copy of its last event is still a duplicate.
            Check(!seen.FirstTime(2, 3), "late copy of the old process's event after the leave is dropped");
            seen.SeatJoined(2);   // the new process's Hello
            Check(seen.FirstTime(2, 1) && seen.FirstTime(2, 2), "new seat-2 events 1 and 2 applied");
            Check(!seen.FirstTime(2, 1), "the new process's own duplicate copy is still dropped");
            Check(!seen.FirstTime(3, 1), "seat 3's history is kept");
        });
        Run("dedup window keeps the newest keys after a seat is purged", delegate {
            SeenEvents seen = new SeenEvents(4);
            seen.FirstTime(2, 1); seen.FirstTime(3, 1); seen.FirstTime(2, 2); seen.FirstTime(3, 2);
            seen.SeatJoined(2);
            Check(seen.Count == 2, "count " + seen.Count);
            seen.FirstTime(4, 1); seen.FirstTime(4, 2); seen.FirstTime(4, 3);   // 5 offered, window 4: (3,1) goes
            Check(seen.Count == 4 && !seen.FirstTime(3, 2) && seen.FirstTime(3, 1), "oldest evicted first");
        });
        DeltaSyncTests.Register(Run);   // T-168
        MirrorHoldTests.Register(Run);  // T-172
        DigestRepairTests.Register(Run);
        TimerPhaseTests.Register(Run);  // T-250
        ShooterFollowTests.Register(Run);   // T-250 / #444
        SnapshotRequestsTests.Register(Run);   // T-254
        Console.WriteLine("World: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
