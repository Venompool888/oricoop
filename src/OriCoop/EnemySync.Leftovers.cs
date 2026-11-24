using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using OriCoop.Enemies;
using OriCoop.Seams;

namespace OriCoop
{
    // T-208: leftovers of enemy sync found in review (the numbers are the ticket's items). The rules are in
    // Enemies\EnemyAuthority.cs and Enemies\HitIds.cs (unit tests: Tests\Enemies\LeftoverTests.cs); this file holds
    // what needs the game, the test-only switches that bring each old behaviour back, and the hooks of the net test
    // (EnemySyncTest.Leftovers.cs, checks "E5k").
    public static partial class EnemySync
    {
        static bool Mutation(string name) { return Environment.GetEnvironmentVariable(name) == "1"; }
        static readonly bool s_mutateNoLateLife = Mutation("ORICOOP_MUTATE_E3_NO_LATE_LIFE");            // (1)
        static readonly bool s_mutateKeepCopies = Mutation("ORICOOP_MUTATE_SWARM_KEEP_COPIES");          // (2)
        static readonly bool s_mutateFollowScene = Mutation("ORICOOP_MUTATE_SWARM_FOLLOW_SCENE");        // (3)
        static readonly bool s_mutateBlankGrant = Mutation("ORICOOP_MUTATE_E3_BLANK_GRANT");             // (5)
        static readonly bool s_mutateIdsFromOne = Mutation("ORICOOP_MUTATE_E3_IDS_FROM_ONE");            // (6)
        static readonly bool s_mutateFreezeAll = Mutation("ORICOOP_MUTATE_E3_FREEZE_ALL");               // (7)
        static readonly bool s_mutateSightNoPlayer = Mutation("ORICOOP_MUTATE_E3_SIGHT_NO_PLAYER");      // (8)
        static readonly bool s_mutateCancelResets = Mutation("ORICOOP_MUTATE_E3_CANCEL_RESETS");         // review 2
        static readonly bool s_mutateLateDeathNeedsOri = Mutation("ORICOOP_MUTATE_E3_LATE_DEATH_NEEDS_ORI"); // review 3
        static readonly bool s_mutateDropFrozenOwn = Mutation("ORICOOP_MUTATE_SWARM_DROP_FROZEN_OWN");   // review 4
        static readonly bool s_mutateNoTakeoverLimit = Mutation("ORICOOP_MUTATE_E3_TAKEOVER_NO_LIMIT");  // (9)

        // (6) Every session draws a new place for its id counter (Enemies\HitIds.cs).
        static HitIds NewIds() { return new HitIds(s_mutateIdsFromOne ? 0u : HitIds.Start(Guid.NewGuid())); }

        // (1) A life message of a split piece from the PC that simulated it until a moment ago (EnemyAuthority.AcceptLateLife).
        static void LateLife(Actor a, State s, ulong hit)
        {
            if (s.Generation < a.Generation) return;   // an earlier life of the family
            bool current = a.Generation == s.Generation && a.Latest.Generation == s.Generation;
            if (current && a.Latest.Killed) return;    // already dead here
            if (!Mine(a))
            {
                // A mirror, or the new simulator still frozen for its handoff (review 1): the former simulator's last
                // word stands, unless the current one has said more about that life since.
                if (!s.Killed && current && a.Latest.Alive) return;
                Trace(s.Killed ? "late death" : "late spawn", a, "from=P" + Services.Net.Sender + " hit=" + hit);
                a.LastHit = hit; ReceiveState(a, s, true);
                if (s.Killed) a.DeathTime = Now;
                return;
            }
            if (s.Killed)
            {
                Trace("late death", a, "from=P" + Services.Net.Sender + " hit=" + hit);
                if (!PieceGone(a.Entity) && a.Entity.DamageReciever != null)
                {
                    // This PC simulates the piece now. It dies here as it did there, so the split runs under this
                    // PC's authority and everyone gets the death and the two new pieces in the current epoch.
                    // Review 3: with no local Ori (a cutscene took it) the piece itself is the damage's sender.
                    SeinCharacter local = TestNoLocalPlayer ? null : Local;
                    GameObject sender = local != null ? local.gameObject : s_mutateLateDeathNeedsOri ? null : a.Entity.gameObject;
                    if (sender != null)
                    {
                        ApplyHit(a, new Damage(a.Entity.DamageReciever.Health + 1000f, Vector2.zero, a.Entity.Position, DamageType.SpiritFlame, sender), NextId());
                        return;
                    }
                }
                a.Generation = s.Generation; a.Latest = s; a.Latest.Tick = s_host.LocalTick; a.DeathTime = Now; a.LastRemoval = "death";
                if (!s_mutateLateDeathNeedsOri && !PieceGone(a.Entity)) { Enemy e = a.Entity; Attach(a, null); InstantiateUtility.Destroy(e.gameObject); }
                SendLife(a, 2);
                return;
            }
            // A piece split off in the old simulator's last moments that this PC, the new one, never heard of: it
            // goes on here (ScanPieces gives it a body, the snapshots show it to the others).
            if (s.Alive && PieceGone(a.Entity) && !(current && a.Latest.Alive))
            {
                Trace("late spawn", a, "from=P" + Services.Net.Sender);
                a.Generation = s.Generation; a.Latest = s; a.Latest.Tick = s_host.LocalTick; a.Buffer.Clear();
            }
        }

        // (2) The session ends: the pieces this PC only showed (another PC simulated them) go, before the index that
        // knows them is cleared. Before, they stayed as live enemies nobody tracked any more. Pieces this PC simulates
        // stay: they are its own enemies.
        static int DropCopies()
        {
            if (s_mutateKeepCopies) return 0;
            int dropped = 0;
            foreach (Actor a in s_actors.Values)
            {
                // Review 4: a simulator frozen for a handoff (or the last one, in a free lease) keeps its own pieces.
                if (a.Root == null || PieceGone(a.Entity) || (s_mutateDropFrozenOwn ? a.Mine(s_host.LocalSeat) : a.Root.SimulatedHere(s_host.LocalSeat))) continue;
                InstantiateUtility.Destroy(a.Entity.gameObject); dropped++;
            }
            return dropped;
        }

        // (5) This PC became the owner without a captured state to go on from (the host granted an enemy it never
        // saw, or took over one it never got a state of). It goes on from its own enemy as it stands here and tells
        // the others; before, the empty state moved that enemy to the world origin with no health and its damage and
        // targeting switched off, without a death.
        static bool Unseeded(Actor a)
        { return !s_mutateBlankGrant && a.Root == null && EnemyOwnership.BlankState(a.Latest.Generation, a.Latest.Tick, a.Latest.Alive, a.Latest.Killed); }
        static void Seed(Actor a)
        {
            if (a.Placeholder == null) return;   // nothing of it here: the first Bind captures it (Attach)
            Bind(a);
            if (a.Entity != null && a.Generation == 0) a.Generation = 1;
            a.Hits.Clear(); a.Applied.Clear(); a.Buffer.Clear(); a.RestoreController = null; RestoreBodies(a);
            a.Latest = Capture(a, true); SendLife(a, 1);
            Trace("owner change", a, "no state came with it: this PC's own enemy goes on");
        }

        // ---- test-only hooks (net test E5k) ----
        public static string TestOwnerKey = ""; public static int TestOwnerSeat;   // host: the owner its decision asks for
        public static string TestSwallowHandoff = "";                              // old owner: its next handoff of this enemy is lost
        public static string TestKillAtFreeze = "";                                // old owner: the enemy dies just before the freeze arrives
        public static bool TestNoLocalPlayer;                                      // a late piece death finds no local Ori (a cutscene took it)
        static readonly HashSet<string> s_testHidden = new HashSet<string>();
        static string s_testUnsupported;
        // As if the enemy's scene were not loaded on this PC (what PlaceholderUnloaded does when the game unloads it).
        public static void TestHide(string key, bool hidden)
        {
            if (!hidden) { s_testHidden.Remove(key); return; }
            Actor a; if (!s_actors.TryGetValue(key, out a) || !s_testHidden.Add(key) || a.Placeholder == null) return;
            if (Mine(a)) a.Latest = Capture(a, true);
            a.Loaded = false; a.Placeholder = null; Announce(a);
            if (Services.Net.IsHost) Decide(a);
        }
        // As if this PC had never seen the enemy; hide = and does not have its scene loaded either.
        public static void TestForget(string key, bool hide)
        {
            Actor a; if (!s_actors.TryGetValue(key, out a)) return;
            RestoreBodies(a);
            if (a.EntityInstanceId != 0) s_entities.Remove(a.EntityInstanceId);
            if (a.Machine != null) s_machines.Remove(a.Machine);
            s_actors.Remove(key);
            if (hide) s_testHidden.Add(key);
        }
        // What a newly started game does to its id counter.
        public static void TestNewIds() { s_ids = NewIds(); }
        // This PC simulated the piece until a moment ago and killed it in its last frame as the simulator; the death
        // message, stamped with the epoch of that time, only reaches the others now.
        public static bool TestLateDeath(string key, uint epoch)
        {
            Actor a;
            if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) || a.Root == null || Mine(a)) return false;
            State s = Capture(a, true); s.Killed = true; s.Alive = false; s.Health = 0;
            ulong hit = NextId(); Enemy e = a.Entity;
            a.Latest = s; a.DeathTime = Now; a.LastRemoval = "death";
            Attach(a, null); if (!PieceGone(e)) InstantiateUtility.Destroy(e.gameObject);
            Send(Life, true, delegate(BinaryWriter w) { WriteId(w, a); w.Write(epoch); w.Write((byte)2); w.Write(hit); WriteVector(w, a.Origin); WriteState(w, a, s, true); });
            return true;
        }
        // Review 1: this PC simulated the family until a moment ago and split off, in its last frame as the simulator,
        // a piece nobody else has heard of; the spawn message, stamped with the epoch of that time, only arrives now.
        public static bool TestLateSpawn(string key, uint epoch, string atKey)
        {
            Piece piece; Actor a = null;
            if (string.IsNullOrEmpty(key) || !s_pieces.TryGetValue(key, out piece)) return false;
            if (s_actors.TryGetValue(key, out a) && (a.Entity != null || a.Latest.Alive)) return false;   // heard of already
            if (a == null) { a = new Actor(); a.Key = key; a.Scene = new MoonGuid(piece.Root.Scene); a.Id = PieceId(piece.Root, piece.Node); s_actors.Add(key, a); }
            Adopt(a, piece);
            State s = new State(); s.Tick = s_host.LocalTick; s.Generation = Math.Max(1u, piece.Root.Generation); s.Alive = true;
            s.Position = Inspect(atKey).Position;
            ulong hit = NextId();
            Send(Life, true, delegate(BinaryWriter w) { WriteId(w, a); w.Write(epoch); w.Write((byte)1); w.Write(hit); WriteVector(w, a.Origin); WriteState(w, a, s, true); });
            return true;
        }
        // The key of the piece `node` of the family `rootKey` ("" when the family is unknown here).
        public static string SwarmPieceKey(string rootKey, int node)
        { Actor root; return string.IsNullOrEmpty(rootKey) || !s_actors.TryGetValue(rootKey, out root) || !SwarmFamily.Valid(node) ? "" : PieceKey(root, node); }
        // Seconds since this PC last got a state of the enemy from its simulator (0 on the simulator itself).
        public static float TestStateAge(string key)
        { Actor a; return string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) ? float.PositiveInfinity : Mine(a) ? 0f : Now - a.Arrival; }
        // What the end of a session does to the pieces this PC only shows.
        // frozen: as on a simulator frozen for a handoff at that moment (review 4).
        public static int TestDropCopies(bool frozen = false)
        {
            if (!frozen) return DropCopies();
            List<Actor> held = new List<Actor>();
            foreach (Actor a in s_actors.Values) if (a.Root != null && a.Known && !a.Frozen && a.OwnerSeat == s_host.LocalSeat) { a.Frozen = true; held.Add(a); }
            try { return DropCopies(); } finally { foreach (Actor a in held) a.Frozen = false; }
        }
        // The freeze every non-simulating PC applies on an ownership message, on an enemy of a class that is not
        // mirrored (each PC runs its own). Returns how many of its rigidbodies ended up frozen, -1 without rigidbodies.
        public static int TestFreezeUnmirrored(string key)
        {
            Actor a; if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) || a.Entity == null) return -1;
            if (a.Entity.GetComponentsInChildren<Rigidbody>(true).Length == 0) return -1;
            RestoreBodies(a); s_testUnsupported = key;
            try { FreezeBodies(a); int frozen = a.Bodies.Count; RestoreBodies(a); return frozen; }
            finally { s_testUnsupported = null; }
        }
        // An enemy with a sideways look-out looks for a player while this PC has none (a cutscene took the local Ori
        // away) and no partner is near. Without such an enemy loaded, the enemy `key` gets a look-out for the call
        // (removed again at once). -1: nothing this PC simulates to look with, 0: fine, 1: the look-out threw.
        public static int TestSightNoPlayer(string key)
        {
            SeeSeinHorizontalyTrigger found = null; bool temporary = false;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(typeof(SeeSeinHorizontalyTrigger)))
            {
                SeeSeinHorizontalyTrigger t = o as SeeSeinHorizontalyTrigger; Actor a = t == null ? null : For(t);
                if (a != null && a.Entity != null && (!Supported(a.Entity) || Mine(a))) { found = t; break; }
            }
            Actor own;
            if (found == null && !string.IsNullOrEmpty(key) && s_actors.TryGetValue(key, out own) && own.Entity != null && (!Supported(own.Entity) || Mine(own)))
            { found = own.Entity.gameObject.AddComponent<SeeSeinHorizontalyTrigger>(); temporary = true; }
            if (found == null) return -1;
            IEnemyHost real = s_host; s_host = new NoPlayerHost(real);
            try { bool entered; EnemySeesPartnerHorizontally.Prefix(found, out entered); if (entered) real.Exit(); return 0; }
            catch (NullReferenceException) { return 1; }
            finally { s_host = real; if (temporary) UnityEngine.Object.DestroyImmediate(found); }
        }
        sealed class NoPlayerHost : IEnemyHost
        {
            readonly IEnemyHost real;
            public NoPlayerHost(IEnemyHost real) { this.real = real; }
            public SeinCharacter Local { get { return null; } }
            public object NearestPuppet(Vector3 position) { return null; }
            public bool P2IsNearer(Vector3 position) { return false; }
            public bool RemoteHello { get { return real.RemoteHello; } }
            public int RemoteCount { get { return real.RemoteCount; } }
            public int LocalSeat { get { return real.LocalSeat; } }
            public uint LocalTick { get { return real.LocalTick; } }
            public IEnumerable<KeyValuePair<int, Vector3>> RemotePositions { get { return real.RemotePositions; } }
            public bool RemotePos(int seat, out Vector3 position) { return real.RemotePos(seat, out position); }
            public SeinCharacter LocalAt(int seat) { return real.LocalAt(seat); }
            public int VictimSeat(Component component) { return real.VictimSeat(component); }
            public bool Inside { get { return real.Inside; } }
            public SeinCharacter PuppetSein(object puppet) { return real.PuppetSein(puppet); }
            public void Enter(object puppet) { real.Enter(puppet); }
            public void Exit() { real.Exit(); }
            public bool BelongsToP2(Component component) { return real.BelongsToP2(component); }
            public bool IsRemoteSein(SeinCharacter sein) { return real.IsRemoteSein(sein); }
            public int SeatOf(Component component) { return real.SeatOf(component); }
            public void Patch(MethodBase method, HarmonyMethod prefix, HarmonyMethod finalizer) { real.Patch(method, prefix, finalizer); }
            public bool TraceSampleDue() { return real.TraceSampleDue(); }
            public void TraceEvent(string name, params object[] values) { real.TraceEvent(name, values); }
        }
    }
}
