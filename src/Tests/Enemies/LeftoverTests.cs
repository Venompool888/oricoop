using System;
using System.Collections.Generic;
using OriCoop.Enemies;

// T-208: the rules behind the enemy-sync leftovers (the numbers are the ticket's items).
static class LeftoverTests
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    sealed class RecordingLeases : IEnemyOwnerPolicy
    {
        public readonly List<string> Calls = new List<string>();
        public bool UsesLeases { get { return true; } }
        public int DesiredOwner(string key, Func<int> hostDecision) { return 0; }
        public uint EpochOf(string key) { return 0; }
        public void RequestOwnership(string key, float score, bool active) { Calls.Add("request " + key + " " + score); }
        public void Release(string key) { Calls.Add("release " + key); }
    }
    sealed class ScriptedLeases : IEnemyOwnerPolicy
    {
        public int Owner; public uint Epoch;
        public bool UsesLeases { get { return true; } }
        public int DesiredOwner(string key, Func<int> hostDecision) { return Owner; }
        public uint EpochOf(string key) { return Epoch; }
        public void RequestOwnership(string key, float score, bool active) { }
        public void Release(string key) { }
    }
    static EnemyAuthority Owned(int owner, uint epoch)
    { EnemyAuthority a = new EnemyAuthority(); a.Known = true; a.OwnerSeat = owner; a.Epoch = epoch; return a; }

    public static void Run(Action<string, Action> run)
    {
        // (1) A split piece has no handoff of its own: what its simulator did last can arrive after the family moved on.
        run("T-208 1: a piece's death from the PC that simulated it just before is taken after the family changed hands", delegate
        {
            EnemyAuthority a = Owned(2, 5); a.NotePrevious(); a.OwnerSeat = 1; a.Epoch = 6;
            Check(!a.AcceptOwnerMessage(1, 5, 2, true) && !a.AcceptOwnerMessage(3, 5, 2, true), "the owner-message gate refuses the old epoch");
            Check(a.AcceptLateLife(1, 5, 2), "the new owner takes it");
            Check(a.AcceptLateLife(3, 5, 2), "a third PC takes it");
            Check(!a.AcceptLateLife(1, 4, 2) && !a.AcceptLateLife(1, 5, 3), "another epoch or another sender is refused");
            Check(!a.AcceptLateLife(2, 5, 2), "a PC's own echo is refused");
        });
        run("T-208 1: a piece's death is taken while the family is frozen for a handoff", delegate
        {
            EnemyAuthority a = Owned(2, 5); a.Frozen = true;
            Check(!a.AcceptOwnerMessage(1, 5, 2, true), "the owner-message gate refuses a frozen family");
            Check(a.AcceptLateLife(1, 5, 2), "taken from the owner being frozen");
            Check(!a.AcceptLateLife(1, 5, 3) && !a.AcceptLateLife(1, 6, 2), "only from that owner at that epoch");
            a.Frozen = false; Check(!a.AcceptLateLife(1, 5, 2), "not late while the owner is simply the owner");
        });
        run("T-208 1: only the simulator right before the current one counts, across a gap without an owner", delegate
        {
            EnemyAuthority a = new EnemyAuthority(); a.Known = false;
            Check(!a.AcceptLateLife(1, 1, 2), "nothing known, nothing taken");
            a = Owned(2, 5); a.NotePrevious(); a.OwnerSeat = 3; a.Epoch = 6; a.NotePrevious(); a.OwnerSeat = 1; a.Epoch = 7;
            Check(a.AcceptLateLife(1, 6, 3) && !a.AcceptLateLife(1, 5, 2), "two owners back is too old");
            // C-server: the lease is free for a moment (no owner) before the next PC gets it.
            a = Owned(2, 5); a.NotePrevious(); a.Known = false; a.OwnerSeat = 0; a.Epoch = 0; a.NotePrevious();
            a.Known = true; a.OwnerSeat = 3; a.Epoch = 7;
            Check(a.AcceptLateLife(3, 5, 2), "the gap keeps the last real simulator");
        });

        // Review 1: a piece this PC never heard of has no history of its own; its family's history counts.
        run("T-208 review 1: the family remembers its former simulator on every change of hands", delegate
        {
            EnemyAuthority a = Owned(2, 5); a.ApplyGrant(1, 6);
            Check(a.AcceptLateLife(1, 5, 2) && a.AcceptLateLife(3, 5, 2), "a grant");
            a = Owned(2, 5); a.BeginDecision(1, 1, 0f); a.CompleteHandoff(1, 6, delegate { return 1f; });
            Check(a.AcceptLateLife(1, 5, 2), "a completed handoff");
            a = Owned(1, 5); a.BeginDecision(2, 1, 0f); a.CompleteLocalTransfer(delegate { return 1f; });
            Check(a.AcceptLateLife(3, 5, 1), "the host handing it on");
            a = Owned(2, 5); a.SeatLeft(2, true, delegate { return 1f; });
            Check(a.AcceptLateLife(1, 5, 2), "the owner leaving");
            a = Owned(2, 5); a.BeginDecision(3, 1, 0f); a.CancelTakeover();
            Check(a.AcceptLateLife(1, 5, 2), "a handoff given up (same owner, next epoch)");
            a = Owned(2, 5); a.ApplyGrant(2, 5);
            Check(a.PreviousOwner == 0, "a refresh that changes nothing is no change of hands");
        });
        run("T-208 review 1: across a free lease the former simulator is kept", delegate
        {
            IEnemyOwnerPolicy before = EnemyAuthority.Policy; ScriptedLeases leases = new ScriptedLeases();
            try
            {
                EnemyAuthority.Policy = leases; EnemyAuthority a = new EnemyAuthority();
                leases.Owner = 2; leases.Epoch = 5; a.RefreshPolicy("k");
                leases.Owner = 0; leases.Epoch = 0; a.RefreshPolicy("k");
                leases.Owner = 3; leases.Epoch = 7; a.RefreshPolicy("k");
                Check(a.AcceptLateLife(3, 5, 2), "previous simulator across the gap");
            }
            finally { EnemyAuthority.Policy = before; }
        });

        run("T-208 review 4: a simulator frozen for a handoff still simulates; in a free lease, the last one does", delegate
        {
            EnemyAuthority a = Owned(2, 5); a.Frozen = true;
            Check(a.SimulatedHere(2) && !a.SimulatedHere(1) && !a.Mine(2), "frozen owner");
            a = Owned(2, 5); a.NotePrevious(); a.Known = false; a.OwnerSeat = 0;
            Check(a.SimulatedHere(2) && !a.SimulatedHere(3), "free lease");
        });

        // (3) Pieces can outlive their family's scene on the PC that simulates them.
        run("T-208 3: a family whose scene unloaded stays present while this PC still simulates its pieces", delegate
        {
            EnemyAuthority a = Owned(1, 1); a.Loaded = false; a.Holding = true;
            Check(a.Present, "present");
            Check(a.PresenceDue(delegate { return 0f; }), "reported once");
            a.PresenceSent(delegate { return 0f; });
            Check(!a.PresenceDue(delegate { return 1f; }) && a.PresenceDue(delegate { return 2f; }), "refreshed like a loaded enemy");
            a.Holding = false; Check(!a.Present && a.PresenceDue(delegate { return 1f; }), "the unload is reported once the pieces are gone");
        });
        run("T-208 3: the lease of such a family is kept, and given up once no piece is left", delegate
        {
            IEnemyOwnerPolicy before = EnemyAuthority.Policy; RecordingLeases leases = new RecordingLeases();
            try
            {
                EnemyAuthority.Policy = leases;
                EnemyAuthority a = Owned(1, 1); a.Loaded = false; a.Holding = true;
                a.UpdateInterest("swarm", 4f, 60f);
                Check(leases.Calls.Count == 1 && leases.Calls[0] == "request swarm 4", "interest kept: " + string.Join(",", leases.Calls.ToArray()));
                a.Holding = false; a.UpdateInterest("swarm", 4f, 60f);
                Check(leases.Calls.Count == 2 && leases.Calls[1] == "release swarm", "interest withdrawn: " + string.Join(",", leases.Calls.ToArray()));
            }
            finally { EnemyAuthority.Policy = before; }
        });
        // Review 6: the pieces may live near a teammate, far from this PC's Ori; nobody else could take them over.
        run("T-208 review 6: a family held without its scene keeps its lease however far this PC's Ori is", delegate
        {
            IEnemyOwnerPolicy before = EnemyAuthority.Policy; RecordingLeases leases = new RecordingLeases();
            try
            {
                EnemyAuthority.Policy = leases;
                EnemyAuthority a = Owned(1, 1); a.Loaded = false; a.Holding = true;
                a.UpdateInterest("swarm", 200f, 60f);
                Check(leases.Calls.Count == 1 && leases.Calls[0] == "request swarm 200", "interest kept: " + string.Join(",", leases.Calls.ToArray()));
            }
            finally { EnemyAuthority.Policy = before; }
        });

        // (5) The host can grant an enemy it never saw: the state in that grant was never captured from anything.
        run("T-208 5: a state nobody captured is told apart from a real one", delegate
        {
            Check(EnemyOwnership.BlankState(0, 0, false, false), "the default state is blank");
            Check(!EnemyOwnership.BlankState(1, 0, false, false) && !EnemyOwnership.BlankState(0, 7, false, false)
                && !EnemyOwnership.BlankState(0, 0, true, false) && !EnemyOwnership.BlankState(0, 0, false, true), "a captured state is not");
        });

        // (6) Hit ids are (seat, counter); a newly started game in a reused seat must not repeat the old game's ids.
        run("T-208 6: a newly started game in the same seat does not repeat the earlier game's hit ids", delegate
        {
            HitIds first = new HitIds(HitIds.Start(new Guid("6f1c2a52-0b7e-4d0f-9d3a-5a8f3d2c1b01")));
            HitIds second = new HitIds(HitIds.Start(new Guid("b3e0c9d4-7a41-4c1e-8f55-21d6e0a7c902")));
            HashSet<ulong> seen = new HashSet<ulong>();
            for (int i = 0; i < 1000; i++) { ulong id = first.Next(3); Check((id >> 32) == 3 && seen.Add(id), "ids of one game are unique and carry the seat"); }
            for (int i = 0; i < 1000; i++) Check(seen.Add(second.Next(3)), "the second game repeated id " + i);
            Check(HitIds.Start(Guid.NewGuid()) != HitIds.Start(Guid.NewGuid()), "every start is drawn anew");
        });

        // (9) The host waits frozen for the old owner's handoff; a handoff that is refused or lost must not freeze it forever.
        run("T-208 9: a handoff that never arrives is given up after the limit and the old owner simulates again", delegate
        {
            EnemyAuthority host = Owned(2, 1);
            Check(host.BeginDecision(3, 1, 10f) == 1 && host.PendingTakeover && host.Frozen, "waiting for the old owner");
            Check(!host.TakeoverOverdue(10f + EnemyAuthority.TakeoverLimit - 0.1f) && host.TakeoverOverdue(10f + EnemyAuthority.TakeoverLimit), "overdue at the limit");
            host.CancelTakeover();
            Check(!host.PendingTakeover && !host.Frozen && host.OwnerSeat == 2 && host.Epoch == 2 && !host.TakeoverOverdue(99f), "cancelled: the old owner again, one epoch on");
            // The old owner froze itself at epoch 1; the host's new grant (epoch 2, owner 2) wakes it.
            EnemyAuthority old = Owned(2, 1); old.Frozen = true;
            Check(old.CanAcceptGrant(false, 1, host.Epoch) && old.GrantChanged(host.OwnerSeat, host.Epoch), "the old owner takes the new grant");
            old.ApplyGrant(host.OwnerSeat, host.Epoch); Check(old.Mine(2), "and simulates again");
        });
        run("T-208 9: a handoff that arrives in time is not overdue", delegate
        {
            EnemyAuthority host = Owned(2, 1); host.BeginDecision(3, 1, 10f);
            Check(host.CanAcceptHandoff(true, 2, 2, 3), "handoff accepted");
            host.CompleteHandoff(3, 2, delegate { return 10.2f; });
            Check(!host.TakeoverOverdue(99f) && host.OwnerSeat == 3, "nothing pending");
        });
    }
}
