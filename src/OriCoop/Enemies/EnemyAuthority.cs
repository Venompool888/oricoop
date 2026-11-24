using System;
using System.Collections.Generic;

namespace OriCoop.Enemies
{
    // The host callback preserves the existing decision (including the harness-patched shell).
    // Lease policies ignore it: only the authority's granted owner may simulate.
    public interface IEnemyOwnerPolicy
    {
        bool UsesLeases { get; }
        int DesiredOwner(string key, Func<int> hostDecision);
        uint EpochOf(string key);
        void RequestOwnership(string key, float score, bool active);
        void Release(string key);
    }

    public sealed class HostEnemyOwnerPolicy : IEnemyOwnerPolicy
    {
        public bool UsesLeases { get { return false; } }
        public int DesiredOwner(string key, Func<int> hostDecision) { return hostDecision(); }
        // Legacy epochs belong to EnemyAuthority's freeze/handoff protocol.
        public uint EpochOf(string key) { return 0; }
        public void RequestOwnership(string key, float score, bool active) { }
        public void Release(string key) { }
    }

    internal class EnemyAuthority
    {
        static readonly IEnemyOwnerPolicy defaultPolicy = new HostEnemyOwnerPolicy();
        static IEnemyOwnerPolicy policy = defaultPolicy;
        public static IEnemyOwnerPolicy Policy
        {
            get { return policy; }
            set { policy = value ?? defaultPolicy; }
        }
        // P2P retains the host policy; C-server uses the granted lease owner/epoch.
        internal static void StartSession(bool serverMode)
        { Policy = serverMode ? new OriCoop.Authority.EnemyLeasePolicy() : null; }

        internal void UpdateInterest(string key, float distance, float sceneRadius)
        {
            if (!Policy.UsesLeases) return;
            // T-208 review 6: a family held without its scene keeps its lease while its pieces live, wherever they
            // went (near a teammate, far from this PC's Ori): no other PC has the scene loaded to take it over.
            if (Present && distance >= 0 && (distance <= sceneRadius || Holding) && !float.IsInfinity(distance))
                Policy.RequestOwnership(key, distance, false);
            else Policy.Release(key);
        }

        // Called before simulation/owner-message checks; never synthesize an epoch locally.
        internal bool RefreshPolicy(string key)
        {
            if (!Policy.UsesLeases) return false;
            int owner = Policy.DesiredOwner(key, null);
            uint epoch = Policy.EpochOf(key);
            bool changed = OwnerSeat != owner || Epoch != epoch || Known != (owner != 0);
            Changing(owner, epoch);
            OwnerSeat = owner; Epoch = epoch; Known = owner != 0; Frozen = owner == 0;
            PendingTakeover = false;
            if (changed) ExpectFirstSnapshot = owner != 0;
            return changed;
        }

        // An asynchronous first grant can arrive after Bind ran without an owner.
        // Seed from that native entity, rather than resuming an empty Latest state.
        internal bool NeedsInitialLeaseState(int localSeat)
        { return Policy.UsesLeases && Mine(localSeat) && Generation == 0; }
        internal bool SeedInitialLease(int localSeat, bool hasEntity, Action capture)
        {
            if (!NeedsInitialLeaseState(localSeat) || !hasEntity) return false;
            Generation = 1;
            capture();
            return true;
        }

        internal bool Known, Frozen, Loaded, SentLoaded, ExpectFirstSnapshot, PendingTakeover;
        internal int OwnerSeat = 1, PendingOwner;
        internal readonly HashSet<int> LoadedSeats = new HashSet<int>();
        internal float OwnerChangedAt, NextPresence;
        // T-208 (3): this PC still simulates live pieces of this Swarm family although the family's scene is no longer
        // loaded here (they followed a player into the next area). For presence, ownership and the lease the family
        // is then as good as loaded on this PC.
        internal bool Holding;
        internal bool Present { get { return Loaded || Holding; } }
        // T-208 (1): who simulated this enemy right before the current owner, and in which epoch.
        internal int PreviousOwner; internal uint PreviousEpoch;
        // T-208 (9): when the host began waiting for the old owner's handoff.
        internal float PendingSince; internal const float TakeoverLimit = 3f;
        internal float SceneRadius = float.PositiveInfinity;
        internal uint Epoch = 1, PendingEpoch, Generation = 0;

        internal bool BeginEntityLife(bool sameLife, uint minimumGeneration)
        {
            if (sameLife && Generation != 0) return false;
            Generation = Math.Max(Generation + 1, minimumGeneration);
            return true;
        }

        internal bool Mine(int localSeat) { return Known && !Frozen && OwnerSeat == localSeat; }
        internal void Presence(int seat, bool loaded) { if (loaded) LoadedSeats.Add(seat); else LoadedSeats.Remove(seat); }
        // T-147: report a load or an unload once; refresh only enemies this PC has loaded. An enemy it only heard of
        // (s_actors keeps every enemy anyone saw for the whole session) costs no traffic.
        internal bool PresenceDue(Func<float> now) { return SentLoaded != Present || Present && now() >= NextPresence; }
        internal void PresenceSent(Func<float> now) { SentLoaded = Present; NextPresence = now() + 2f; }
        // Host: a guest reported whether it has this enemy loaded. (T-147)
        // 0: nothing to send, 1: send the owner and state to that guest only, 2: run the owner decision.
        // An unchanged owner is not re-broadcast: only a guest that just loaded the enemy needs the owner and state.
        internal int OnPresence(int sender, bool loaded, Func<int> desired)
        {
            bool changed = LoadedSeats.Contains(sender) != loaded;
            Presence(sender, loaded);
            if (Policy.UsesLeases || !Known || PendingTakeover || OwnerSeat != desired()) return 2;
            return changed && loaded ? 1 : 0;
        }

        // 0: refresh, 1: await old owner, 2: transfer from local owner.
        internal int BeginDecision(int desired, int localSeat, float now = 0f)
        {
            if (!Known) { Known = true; OwnerSeat = 1; }
            if (OwnerSeat == desired) return 0;
            PendingEpoch = Epoch + 1; PendingOwner = desired;
            if (OwnerSeat != localSeat) { PendingTakeover = true; Frozen = true; PendingSince = now; return 1; }
            return 2;
        }
        // T-208 (9): the handoff the host waits for can be refused (the target's seat was renumbered meanwhile) or lost.
        // Before, the host then waited for ever: the enemy stood frozen on its screen and took no hits. Now it gives
        // up after TakeoverLimit, hands the enemy back to the old owner one epoch on (the same step as when the target
        // leaves, SeatLeft 2) and decides again with the seats as they are then.
        internal bool TakeoverOverdue(float now) { return PendingTakeover && now - PendingSince >= TakeoverLimit; }
        internal void CancelTakeover() { Changing(OwnerSeat, PendingEpoch); Epoch = PendingEpoch; PendingTakeover = false; Frozen = false; }
        internal void CompleteLocalTransfer(Func<float> now)
        {
            Changing(PendingOwner, PendingEpoch);
            Frozen = true; OwnerSeat = PendingOwner; Epoch = PendingEpoch; OwnerChangedAt = now(); ExpectFirstSnapshot = true;
        }
        internal bool CanFreeze(int sender, int localSeat, uint epoch) { return !Policy.UsesLeases && sender == 1 && Mine(localSeat) && epoch > Epoch; }
        internal bool CanAcceptGrant(bool host, int sender, uint epoch) { return !Policy.UsesLeases && !host && sender == 1 && epoch >= Epoch; }
        internal bool GrantChanged(int owner, uint epoch) { return !Known || OwnerSeat != owner || Epoch != epoch; }
        internal void ApplyGrant(int owner, uint epoch) { Changing(owner, epoch); Known = true; OwnerSeat = owner; Epoch = epoch; Frozen = false; }
        internal bool CanAcceptHandoff(bool host, int sender, uint epoch, int owner)
        { return !Policy.UsesLeases && host && PendingTakeover && epoch == PendingEpoch && sender == OwnerSeat && owner == PendingOwner; }
        internal void CompleteHandoff(int owner, uint epoch, Func<float> now)
        { Changing(owner, epoch); OwnerSeat = owner; Epoch = epoch; PendingTakeover = false; OwnerChangedAt = now(); }
        // 1: owner left, 2: pending target left.
        internal int SeatLeft(int seat, bool host, Func<float> now)
        {
            LoadedSeats.Remove(seat);
            if (Policy.UsesLeases) return 0; // The arbiter advances the lease epoch when a seat leaves.
            if (!host) return 0;
            if (OwnerSeat == seat)
            { Changing(1, Epoch + 1); OwnerSeat = 1; Epoch++; PendingTakeover = false; Frozen = true; OwnerChangedAt = now(); return 1; }
            if (PendingTakeover && PendingOwner == seat)
            { Changing(OwnerSeat, PendingEpoch); Epoch = PendingEpoch; PendingTakeover = false; Frozen = false; return 2; }
            return 0;
        }
        internal bool AcceptOwnerMessage(int localSeat, uint epoch, int sender, bool checkFrozen)
        { return Known && !Mine(localSeat) && (!checkFrozen || !Frozen) && Epoch == epoch && OwnerSeat == sender; }
        // T-208 (1): a split Swarm piece has no handoff of its own (only its family's placeholder has), so what its
        // simulator did in its last moments (killed it, split it) can arrive while the family is frozen for the
        // handoff, or after it changed hands. AcceptOwnerMessage refuses both; for a piece's life message they count
        // when they come from that simulator in its own epoch.
        internal void NotePrevious() { if (Known && OwnerSeat != 0) { PreviousOwner = OwnerSeat; PreviousEpoch = Epoch; } }
        // T-208 review 1: every change of hands keeps the simulator before it. A piece the new owner never heard of has
        // no history of its own; its family's history (the placeholder actor's) is what counts for its late messages.
        void Changing(int owner, uint epoch) { if (owner != OwnerSeat || epoch != Epoch) NotePrevious(); }
        // T-208 review 4: this PC simulates the enemy, frozen for a handoff or not; in a free lease, the PC that
        // simulated it last.
        internal bool SimulatedHere(int localSeat) { return Known ? OwnerSeat == localSeat : PreviousOwner == localSeat; }
        internal bool AcceptLateLife(int localSeat, uint epoch, int sender)
        {
            if (!Known || sender == 0 || sender == localSeat) return false;
            if (OwnerSeat == sender && Epoch == epoch) return Frozen;
            return PreviousOwner == sender && PreviousEpoch == epoch;
        }
        // T-133: a device's primary seat was renumbered (renamed[old] = new, 0 = unchanged). The device keeps simulating
        // its enemies: owner, pending owner and presence move to the new number together (applied at once, so a chain
        // like 4->3 with 3->2 cannot mix them up).
        internal void RenameSeats(int[] renamed)
        {
            OwnerSeat = RenamedSeat(renamed, OwnerSeat);
            PendingOwner = RenamedSeat(renamed, PendingOwner);
            PreviousOwner = RenamedSeat(renamed, PreviousOwner);
            List<int> loaded = new List<int>(LoadedSeats);
            LoadedSeats.Clear();
            foreach (int seat in loaded) LoadedSeats.Add(RenamedSeat(renamed, seat));
        }
        internal static int RenamedSeat(int[] renamed, int seat)
        { return seat > 0 && seat < renamed.Length && renamed[seat] != 0 ? renamed[seat] : seat; }
    }

    internal struct EnemyTickPosition
    {
        internal readonly float Tick;
        internal readonly EnemyVector Position;
        internal EnemyTickPosition(float tick, EnemyVector position) { Tick = tick; Position = position; }
    }

    internal static class EnemyOwnership
    {
        // Compare a rendered mirror at the owner tick it represents, without
        // extrapolating beyond the sampled owner history while a grant is in flight.
        internal static bool TryPosition(IList<EnemyTickPosition> samples, float tick, out EnemyVector position)
        {
            position = new EnemyVector();
            if (samples.Count == 0 || float.IsNaN(tick) || float.IsInfinity(tick)
                || tick < samples[0].Tick || tick > samples[samples.Count - 1].Tick) return false;
            if (tick == samples[0].Tick) { position = samples[0].Position; return true; }
            for (int i = 1; i < samples.Count; i++)
            {
                EnemyTickPosition left = samples[i - 1], right = samples[i];
                if (right.Tick < tick || right.Tick <= left.Tick) continue;
                float t = (tick - left.Tick) / (right.Tick - left.Tick);
                position = new EnemyVector(left.Position.X + (right.Position.X - left.Position.X) * t,
                    left.Position.Y + (right.Position.Y - left.Position.Y) * t,
                    left.Position.Z + (right.Position.Z - left.Position.Z) * t);
                return true;
            }
            return false;
        }

        internal static bool ExpectedRole(bool serverMode, bool host, int seat, bool mine, int owner)
        { return serverMode ? owner >= 1 && owner <= 4 && mine == (owner == seat) : mine == host; }

        // Shared by E3/E5/E6: server tests require one simulator among the two
        // participants and identical grants. P2P keeps its strict host expectation.
        internal static bool PeerAgreement(bool serverMode, bool host, int seat, bool mine, int owner, uint epoch,
            int remoteSeat, bool remoteMine, int remoteOwner, uint remoteEpoch)
        {
            if (!serverMode) return mine == host && remoteMine != host;
            return seat != remoteSeat && mine != remoteMine && epoch != 0 && epoch == remoteEpoch && owner == remoteOwner
                && ExpectedRole(true, host, seat, mine, owner) && ExpectedRole(true, !host, remoteSeat, remoteMine, remoteOwner);
        }

        internal static int DesiredOwner(int remoteCount, bool loaded, HashSet<int> loadedSeats, int owner, int localSeat,
            Func<float> now, float changedAt, Func<int, float> ownerDistance, Func<float> localDistance,
            IEnumerable<KeyValuePair<int, float>> remoteDistances)
        {
            if (remoteCount <= 1)
            {
                if (loaded || loadedSeats.Count == 0) return 1;
                foreach (int seat in loadedSeats) return seat;
            }
            int desired = owner;
            float current = ownerDistance(desired), distance = current;
            if (loaded)
            {
                float d = localDistance();
                if (d < distance) { desired = localSeat; distance = d; }
            }
            foreach (KeyValuePair<int, float> remote in remoteDistances)
            {
                if (!loadedSeats.Contains(remote.Key)) continue;
                float d = remote.Value;
                if (d < distance) { desired = remote.Key; distance = d; }
            }
            if (float.IsPositiveInfinity(distance)) return 1;
            if (!float.IsPositiveInfinity(current) && (now() - changedAt < 2f || distance + 1f >= current)) return owner;
            return desired;
        }
        internal static bool AcceptState(uint priorGeneration, uint priorTick, bool priorKilled,
            uint nextGeneration, uint nextTick, bool nextKilled, bool newSource)
        {
            return nextGeneration > priorGeneration || (nextGeneration == priorGeneration
                && (newSource || (int)(nextTick - priorTick) > 0) && (!priorKilled || nextKilled));
        }
        // T-208 (5): a state nobody captured (the host grants an enemy it never tracked: only a guest has it loaded).
        // Every captured state carries the sender's tick; a default one has no tick, no generation and no life.
        internal static bool BlankState(uint generation, uint tick, bool alive, bool killed)
        { return generation == 0 && tick == 0 && !alive && !killed; }
        internal static bool AcceptLife(uint generation, uint priorTick, bool priorKilled, bool newSource,
            uint nextGeneration, uint nextTick, bool nextKilled)
        {
            if (nextGeneration < generation || (!newSource && nextGeneration == generation && (int)(nextTick - priorTick) < 0)) return false;
            return nextGeneration != generation || !priorKilled || nextKilled;
        }
        // 0: drop, 1: queue, 2: apply locally, 3: forward from host.
        internal static int HitDecision(uint generation, uint incomingGeneration, bool applied, bool frozen, bool mine,
            uint incomingEpoch, uint epoch, bool host)
        {
            if (incomingGeneration != generation || applied) return 0;
            // T-151: a hit aimed in the previous epoch is queued on every end, the new owner included (the host taking
            // over used to drop it): FlushHits then applies it once on the owner (Applied set dedups it against the
            // old owner's last state) or forwards it with the current epoch.
            if (frozen || incomingEpoch < epoch) return 1;
            if (mine && incomingEpoch == epoch) return 2;
            if (host && incomingEpoch == epoch) return 3;
            return 0;
        }
    }
}
