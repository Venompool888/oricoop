using System;
using System.Collections.Generic;
using OriCoop.Authority;
using OriCoop.Enemies;

// The production AuthoritySource needs the game transport. Only its source getter is replaced here.
namespace OriCoop.Authority
{
    public static class AuthoritySource { public static IAuthority Current; }
}

static class EnemyOwnerPolicyTests
{
    // Use the real arbitration and client protocol: a fake grant cannot reveal lease churn.
    sealed class LeaseRig
    {
        public double Now;
        public readonly Arbiter Arbiter = new Arbiter("enemies", new ArbiterOptions(), 0);
        public readonly AuthorityClient[] Clients = new AuthorityClient[3];
        public readonly EnemyLeasePolicy[] Policies = new EnemyLeasePolicy[3];
        public readonly int[] DropUnreliable = new int[3];   // T-146: lose the next N unreliable sends of a seat
        public LeaseRig()
        {
            for (int seat = 1; seat <= 2; seat++)
            {
                int localSeat = seat;
                Clients[seat] = new AuthorityClient(AuthorityMode.Server, delegate { return localSeat; },
                    delegate(byte[] bytes, bool reliable) {
                        if (!reliable && DropUnreliable[localSeat] > 0) { DropUnreliable[localSeat]--; return; }
                        Deliver(Arbiter.Handle(localSeat, CsWire.Decode(bytes), Now)); }, false);
                Policies[seat] = new EnemyLeasePolicy(delegate { return Clients[localSeat]; });
                Deliver(Arbiter.JoinAt(seat, (ulong)seat, "seat" + seat, Now));
            }
        }
        void Deliver(List<Outgoing> output)
        {
            foreach (Outgoing message in output)
                for (int seat = 1; seat <= 2; seat++)
                    if (Clients[seat] != null && (message.To == 0 || message.To == seat || (message.To < 0 && message.To != -seat)))
                        Clients[seat].Receive(message.Message);
        }
    }
    sealed class FakeAuthority : IAuthority
    {
        public int Owner, Requests, Releases;
        public uint Epoch;
        public string LastKey;
        public float Score;
        public bool Active;
        public AuthorityMode Mode { get { return AuthorityMode.Server; } }
        public int LocalSeat { get { return 2; } }
        public bool ArbiterIsLocal { get { return false; } }
        public int OwnerOf(string key) { LastKey = key; return Owner; }
        public uint EpochOf(string key) { LastKey = key; return Epoch; }
        public bool Owns(string key) { return OwnerOf(key) == LocalSeat; }
        public StateUpdate LatestState(string key) { return null; }
        public void RequestLease(string key, float score, bool active)
        { LastKey = key; Score = score; Active = active; Requests++; }
        public void ReleaseLease(string key)
        { LastKey = key; Releases++; if (Owner == LocalSeat) { Owner = 3; Epoch++; } }
        public void PublishState(string key, byte[] data, bool active) { }
        public void Propose(byte kind, string key, byte[] data, bool claim, bool release) { }
        public event Action<LeaseChange> LeaseChanged { add { } remove { } }
        public event Action<StateUpdate> StateReceived { add { } remove { } }
        public event Action<CommittedEvent> Committed { add { } remove { } }
        public event Action Reset { add { } remove { } }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> run)
    {
        run("P2P session resets to legacy owner decision after a lease session", delegate {
            FakeAuthority authority = new FakeAuthority(); authority.Owner = 2; authority.Epoch = 7;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return authority; });
            EnemyAuthority.StartSession(false);
            Check(!EnemyAuthority.Policy.UsesLeases, "session still uses the server lease policy");
            Check(EnemyAuthority.Policy.DesiredOwner("actor", delegate { return 1; }) == 1, "legacy arbiter decision ignored");
            EnemyAuthority a = new EnemyAuthority();
            Check(!a.RefreshPolicy("actor") && a.Epoch == 1, "lease epoch leaked into legacy session");
            Check(a.BeginDecision(2, 1) == 2, "legacy handoff disabled");
        });
        run("server session installs lease policy and maps current grant", delegate {
            FakeAuthority authority = new FakeAuthority(); authority.Owner = 2; authority.Epoch = 7;
            AuthoritySource.Current = authority;
            try {
                EnemyAuthority.StartSession(true);
                Check(EnemyAuthority.Policy.UsesLeases, "server fell back to legacy policy");
                EnemyAuthority a = new EnemyAuthority(); a.RefreshPolicy("actor");
                Check(a.Mine(2) && a.Epoch == 7, "server grant not mapped");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("server scenario accepts either agreed owner", delegate {
            Check(EnemyOwnership.PeerAgreement(true, true, 1, false, 2, 7, 2, true, 2, 7), "guest owner rejected on host");
            Check(EnemyOwnership.PeerAgreement(true, false, 2, true, 2, 7, 1, false, 2, 7), "guest owner rejected on guest");
            Check(EnemyOwnership.PeerAgreement(true, true, 1, true, 1, 8, 2, false, 1, 8), "host owner rejected");
            Check(EnemyOwnership.PeerAgreement(true, true, 3, false, 4, 9, 4, true, 4, 9), "higher seat owner rejected");
        });
        run("guest granted first stays the sole simulator for both scenario reports", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[2].RequestOwnership("actor", 4, false);
            EnemyAuthority host = new EnemyAuthority(), guest = new EnemyAuthority();
            try {
                for (int tick = 1; tick <= 24; tick++) {
                    rig.Now = tick * 250;
                    rig.Policies[1].RequestOwnership("actor", 4, false);
                    rig.Policies[2].RequestOwnership("actor", 4, false);
                    EnemyAuthority.Policy = rig.Policies[1]; host.RefreshPolicy("actor");
                    EnemyAuthority.Policy = rig.Policies[2]; guest.RefreshPolicy("actor");
                    Check(!host.Mine(1) && guest.Mine(2) && EnemyOwnership.PeerAgreement(true, true, 1,
                        host.Mine(1), host.OwnerSeat, host.Epoch, 2, guest.Mine(2), guest.OwnerSeat, guest.Epoch), "guest scenario loses owner agreement");
                    Check(host.AcceptOwnerMessage(1, guest.Epoch, 2, true), "host mirror rejects guest snapshot epoch");
                }
            } finally { EnemyAuthority.Policy = null; }
        });
        run("server scenario rejects absent split or stale ownership", delegate {
            Check(!EnemyOwnership.PeerAgreement(true, true, 1, false, 0, 0, 2, false, 0, 0), "no owner accepted");
            Check(!EnemyOwnership.PeerAgreement(true, true, 1, true, 1, 7, 2, true, 2, 7), "split owner accepted");
            Check(!EnemyOwnership.PeerAgreement(true, true, 1, false, 2, 7, 2, true, 2, 6), "epoch disagreement accepted");
            Check(!EnemyOwnership.PeerAgreement(true, false, 2, true, 2, 6, 1, false, 2, 7), "peer accepted same mismatched barrier reports");
            Check(!EnemyOwnership.PeerAgreement(true, true, 1, true, 2, 7, 2, true, 2, 7), "two simulators accepted");
            Check(!EnemyOwnership.PeerAgreement(true, true, 1, false, 3, 7, 2, false, 3, 7), "nonparticipating owner accepted");
        });
        run("first lease owner captures its loaded entity before any resume", delegate {
            FakeAuthority source = new FakeAuthority(); source.Owner = 2; source.Epoch = 1;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return source; });
            try {
                EnemyAuthority a = new EnemyAuthority(); a.RefreshPolicy("actor");
                EnemyState latest = new EnemyState();
                Check(a.SeedInitialLease(2, true, delegate {
                    latest.Health = 8; latest.Alive = latest.DealerActive = latest.TargetActive = true; latest.Generation = a.Generation;
                }), "first owner resumes uncaptured defaults");
                Check(latest.Health == 8 && latest.TargetActive && latest.DealerActive && latest.Generation == 1, "native initial state lost");
                Check(!a.SeedInitialLease(2, true, delegate { throw new Exception("recaptured established owner"); }), "existing state overwritten");
                source.Owner = 1; source.Epoch = 2; a.RefreshPolicy("actor");
                Check(!a.SeedInitialLease(2, true, delegate { throw new Exception("mirror captured"); }), "mirror seeded authoritative state");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("first lease defers resume while native placeholder has no entity", delegate {
            FakeAuthority source = new FakeAuthority(); source.Owner = 2; source.Epoch = 1;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return source; });
            try {
                EnemyAuthority a = new EnemyAuthority(); a.RefreshPolicy("actor");
                Check(a.NeedsInitialLeaseState(2), "empty initial state treated as resumable");
                Check(!a.SeedInitialLease(2, false, delegate { throw new Exception("captured an absent entity"); }), "absent entity seeded");
                Check(a.NeedsInitialLeaseState(2) && a.Generation == 0, "native respawn should wait for initial capture");
                a.SeedInitialLease(2, true, delegate { });
                Check(!a.NeedsInitialLeaseState(2), "captured entity still awaiting initialization");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("P2P scenario still requires host ownership", delegate {
            Check(EnemyOwnership.PeerAgreement(false, true, 1, true, 1, 1, 2, false, 1, 1), "host owner rejected");
            Check(!EnemyOwnership.PeerAgreement(false, true, 1, false, 2, 1, 2, true, 2, 1), "P2P guest owner accepted");
            Check(!EnemyOwnership.ExpectedRole(false, true, 1, false, 2), "P2P host readiness relaxed");
            Check(EnemyOwnership.ExpectedRole(true, true, 1, false, 2), "server mirror readiness rejected");
        });
        run("lagged mirror compares against the same owner tick instead of the final pose", delegate {
            List<EnemyTickPosition> samples = new List<EnemyTickPosition>();
            samples.Add(new EnemyTickPosition(100, new EnemyVector(0, 0, 0)));
            samples.Add(new EnemyTickPosition(106, new EnemyVector(0, 8, 0)));
            samples.Add(new EnemyTickPosition(112, new EnemyVector(0, 0, 0)));
            EnemyVector matched;
            Check(EnemyOwnership.TryPosition(samples, 106, out matched) && matched.Y == 8, "mirror at apex compared with owner's later landing");
            Check(EnemyOwnership.TryPosition(samples, 103, out matched) && matched.Y == 4, "owner tick interpolation");
        });
        run("owner tick comparison waits for a covered sample without extrapolating", delegate {
            List<EnemyTickPosition> samples = new List<EnemyTickPosition>(); EnemyVector matched;
            Check(!EnemyOwnership.TryPosition(samples, 100, out matched), "empty history accepted");
            samples.Add(new EnemyTickPosition(100, new EnemyVector(1, 2, 0)));
            Check(!EnemyOwnership.TryPosition(samples, 99, out matched) && !EnemyOwnership.TryPosition(samples, 101, out matched), "uncovered tick extrapolated");
            samples.Add(new EnemyTickPosition(106, new EnemyVector(7, 8, 0)));
            Check(EnemyOwnership.TryPosition(samples, 106, out matched) && matched.X == 7, "covered final tick rejected");
        });
        run("loaded enemy keeps one real arbiter lease throughout E3 measurement", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("scene:actor", 10, false);
            for (int tick = 1; tick <= 40; tick++)
            {
                rig.Now = tick * 250;
                rig.Policies[2].RequestOwnership("scene:actor", 10, false);
                rig.Policies[1].RequestOwnership("scene:actor", 10, false);
                Check(rig.Clients[1].OwnerOf("e:scene:actor") == 1 && rig.Clients[2].EpochOf("e:scene:actor") == 1,
                    "loaded owner changed after " + rig.Now + "ms: owner=" + rig.Clients[1].OwnerOf("e:scene:actor")
                    + " epoch=" + rig.Clients[2].EpochOf("e:scene:actor"));
            }
            StateUpdate heartbeat = rig.Clients[2].LatestState("e:scene:actor");
            Check(heartbeat != null && heartbeat.Active && heartbeat.Data.Length == 0, "ownership heartbeat contains gameplay state");
        });
        run("real arbiter hands enemy to interested seat on unload then stays stable", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 10, false);
            rig.Now = 250; rig.Policies[2].RequestOwnership("actor", 5, false);
            rig.Policies[1].RequestOwnership("actor", 10, false);
            rig.Policies[1].Release("actor");
            Check(rig.Clients[2].OwnerOf("e:actor") == 2 && rig.Clients[2].EpochOf("e:actor") == 2, "release handover");
            for (int tick = 2; tick <= 24; tick++)
            {
                rig.Now = tick * 250;
                rig.Policies[2].RequestOwnership("actor", 5, false);
                Check(rig.Clients[2].OwnerOf("e:actor") == 2 && rig.Clients[2].EpochOf("e:actor") == 2, "new owner churn");
            }
            rig.Policies[2].Release("actor");
            Check(rig.Clients[1].OwnerOf("e:actor") == 0 && rig.Clients[1].EpochOf("e:actor") == 3, "unloaded seat retained interest");
        });
        run("real arbiter may recover enemy when owner stops renewing", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 10, false);
            rig.Now = 250; rig.Policies[1].RequestOwnership("actor", 10, false);
            rig.Now = 1000; rig.Policies[2].RequestOwnership("actor", 5, false);
            Check(rig.Clients[2].OwnerOf("e:actor") == 2 && rig.Clients[2].EpochOf("e:actor") == 2, "stalled owner held lease forever");
        });
        // T-146: the near seat (score 1) owns; the far seat (score 20) asks every poll. Real arbiter and client protocol.
        run("a lost heartbeat does not hand the enemy to the far seat (T-146)", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 1, false);
            for (int tick = 1; tick <= 30; tick++)
            {
                rig.Now = tick * 267;
                if (tick % 7 == 0) rig.DropUnreliable[1] = 1;      // this poll's heartbeat is lost
                rig.Policies[2].RequestOwnership("actor", 20, false);
                rig.Policies[1].RequestOwnership("actor", 1, false);
                Check(rig.Clients[2].OwnerOf("e:actor") == 1, "far seat took the enemy after a lost heartbeat at " + rig.Now + "ms");
            }
        });
        run("a 400 ms hitch of the near owner keeps the enemy (T-146)", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 1, false);
            rig.Now = 267; rig.Policies[1].RequestOwnership("actor", 1, false);
            rig.Now = 534; rig.Policies[1].RequestOwnership("actor", 1, false);
            rig.Now = 934; rig.Policies[2].RequestOwnership("actor", 20, false);   // owner silent for 400 ms
            Check(rig.Clients[2].OwnerOf("e:actor") == 1, "hitch handed the enemy to the far seat");
            rig.Policies[1].RequestOwnership("actor", 1, false);
            Check(rig.Clients[1].OwnerOf("e:actor") == 1 && rig.Clients[1].EpochOf("e:actor") == 1, "owner churned");
        });
        run("a stalled owner still hands over, even to a worse score (T-146)", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 1, false);
            rig.Now = 267; rig.Policies[1].RequestOwnership("actor", 1, false);
            rig.Now = 1500; rig.Policies[2].RequestOwnership("actor", 20, false);
            Check(rig.Clients[2].OwnerOf("e:actor") == 1, "far seat took over before the stall window");
            rig.Now = 2400; rig.Policies[2].RequestOwnership("actor", 20, false);
            Check(rig.Clients[2].OwnerOf("e:actor") == 2, "stalled owner kept the enemy forever");
        });
        run("new owner's first heartbeat counts across epochs, no bounce back (T-146)", delegate {
            LeaseRig rig = new LeaseRig();
            rig.Policies[1].RequestOwnership("actor", 10, false);
            for (int tick = 1; tick <= 8; tick++) { rig.Now = tick * 250; rig.Policies[1].RequestOwnership("actor", 10, false); }
            rig.Now = 2100; rig.Policies[2].RequestOwnership("actor", 5, false);
            rig.Policies[1].Release("actor");
            Check(rig.Clients[2].OwnerOf("e:actor") == 2, "release handover");
            long dropped = rig.Arbiter.DroppedStates;
            rig.Now = 2350; rig.Policies[2].RequestOwnership("actor", 5, false);      // first heartbeat, version 1
            Check(rig.Arbiter.DroppedStates == dropped, "new owner's first heartbeat dropped as stale");
            rig.Now = 2700; rig.Policies[1].RequestOwnership("actor", 10, false);
            Check(rig.Clients[1].OwnerOf("e:actor") == 2, "old owner took the enemy back");
        });
        run("default owner policy delegates unchanged host decision", delegate {
            EnemyAuthority.Policy = null;
            Check(!EnemyAuthority.Policy.UsesLeases, "default must be legacy");
            Check(EnemyAuthority.Policy.DesiredOwner("scene:actor", delegate { return 3; }) == 3, "host choice");
            EnemyAuthority a = new EnemyAuthority();
            Check(!a.RefreshPolicy("scene:actor") && a.Epoch == 1 && a.OwnerSeat == 1, "legacy epoch");
        });
        run("lease request waits for grant and namespaces actor identity", delegate {
            FakeAuthority authority = new FakeAuthority();
            EnemyLeasePolicy policy = new EnemyLeasePolicy(delegate { return authority; });
            policy.RequestOwnership("scene:actor", 12.5f, false);
            Check(authority.Requests == 1 && authority.LastKey == "e:scene:actor" && authority.Score == 12.5f && !authority.Active, "request");
            Check(policy.DesiredOwner("scene:actor", delegate { throw new Exception("legacy chosen"); }) == 0, "optimistic ownership");
            authority.Owner = 2; authority.Epoch = 7;
            Check(policy.DesiredOwner("scene:actor", null) == 2 && policy.EpochOf("scene:actor") == 7, "grant");
        });
        run("lease epoch maps to enemy and rejects former owner", delegate {
            FakeAuthority authority = new FakeAuthority(); authority.Owner = 2; authority.Epoch = 7;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return authority; });
            try {
                EnemyAuthority a = new EnemyAuthority();
                Check(a.RefreshPolicy("scene:actor") && a.Mine(2) && a.Epoch == 7, "grant mapping");
                EnemyAuthority.Policy.Release("scene:actor");
                Check(a.RefreshPolicy("scene:actor") && a.OwnerSeat == 3 && a.Epoch == 8 && !a.Mine(2), "handover mapping");
                Check(!a.AcceptOwnerMessage(1, 7, 2, true) && a.AcceptOwnerMessage(1, 8, 3, true), "stale snapshot accepted");
                Check(EnemyOwnership.HitDecision(0, 0, false, a.Frozen, a.Mine(3), 7, a.Epoch, false) != 2, "stale hit applied");
                authority.Owner = 0; authority.Epoch = 9; a.RefreshPolicy("scene:actor");
                Check(!a.Known && a.Frozen && !a.Mine(1) && !a.Mine(3) && a.Epoch == 9, "ungranted simulation");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("lease interest releases on unload and outside scene radius", delegate {
            FakeAuthority authority = new FakeAuthority(); authority.Owner = 2; authority.Epoch = 4;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return authority; });
            try {
                EnemyAuthority a = new EnemyAuthority(); a.Loaded = true;
                a.UpdateInterest("scene:actor", 10, 50);
                Check(authority.Requests == 1, "loaded interest");
                a.UpdateInterest("scene:actor", 51, 50);
                Check(authority.Releases == 1 && authority.Owner == 3 && authority.Epoch == 5, "radius release");
                a.Loaded = true; a.UpdateInterest("scene:actor", 8, 50);
                a.Loaded = false; a.UpdateInterest("scene:actor", 8, 50);
                Check(authority.Releases == 2, "unload release");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("lease follows AuthoritySource replacement", delegate {
            FakeAuthority first = new FakeAuthority(), second = new FakeAuthority();
            first.Owner = 2; first.Epoch = 10; second.Owner = 3; second.Epoch = 1;
            EnemyLeasePolicy policy = new EnemyLeasePolicy(); AuthoritySource.Current = first;
            Check(policy.DesiredOwner("actor", null) == 2 && policy.EpochOf("actor") == 10, "first source");
            AuthoritySource.Current = second;
            Check(policy.DesiredOwner("actor", null) == 3 && policy.EpochOf("actor") == 1, "replacement source");
        });
        run("lease ignores legacy host grants and seat leave epochs", delegate {
            FakeAuthority authority = new FakeAuthority(); authority.Owner = 2; authority.Epoch = 7;
            EnemyAuthority.Policy = new EnemyLeasePolicy(delegate { return authority; });
            try {
                EnemyAuthority a = new EnemyAuthority(); a.RefreshPolicy("actor");
                Check(!a.CanFreeze(1, 2, 8) && !a.CanAcceptGrant(false, 1, 8), "legacy host can override lease");
                a.PendingTakeover = true; a.PendingEpoch = 8; a.PendingOwner = 3;
                Check(!a.CanAcceptHandoff(true, 2, 8, 3), "legacy handoff overrides lease");
                Check(a.SeatLeft(2, true, delegate { return 9; }) == 0 && a.Epoch == 7 && a.OwnerSeat == 2, "synthetic leave epoch");
            } finally { EnemyAuthority.Policy = null; }
        });
        run("lease invalid scores withdraw interest without repeat releases", delegate {
            FakeAuthority authority = new FakeAuthority();
            EnemyLeasePolicy policy = new EnemyLeasePolicy(delegate { return authority; });
            policy.RequestOwnership("actor", 3, false);
            policy.RequestOwnership("actor", float.NaN, false);
            policy.RequestOwnership("actor", float.PositiveInfinity, false);
            policy.Release("actor");
            Check(authority.Requests == 1 && authority.Releases == 1, "invalid score or repeat release");
        });
    }
}
