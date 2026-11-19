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
            if (Loaded && distance >= 0 && distance <= sceneRadius && !float.IsInfinity(distance))
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

        internal bool Known, Frozen, Loaded, SentLoaded, SentPresence, ExpectFirstSnapshot, PendingTakeover;
        internal int OwnerSeat = 1, PendingOwner;
        internal readonly HashSet<int> LoadedSeats = new HashSet<int>();
        internal float OwnerChangedAt, NextPresence;
        internal float SceneRadius = float.PositiveInfinity;
        internal uint Epoch = 1, PendingEpoch, Generation = 0;

        internal bool Mine(int localSeat) { return Known && !Frozen && OwnerSeat == localSeat; }
        internal void Presence(int seat, bool loaded) { if (loaded) LoadedSeats.Add(seat); else LoadedSeats.Remove(seat); }
        internal bool PresenceDue(Func<float> now) { return !SentPresence || SentLoaded != Loaded || now() >= NextPresence; }
        internal void PresenceSent(Func<float> now) { SentLoaded = Loaded; SentPresence = true; NextPresence = now() + 2f; }

        // 0: refresh, 1: await old owner, 2: transfer from local owner.
        internal int BeginDecision(int desired, int localSeat)
        {
            if (!Known) { Known = true; OwnerSeat = 1; }
            if (OwnerSeat == desired) return 0;
            PendingEpoch = Epoch + 1; PendingOwner = desired;
            if (OwnerSeat != localSeat) { PendingTakeover = true; Frozen = true; return 1; }
            return 2;
        }
        internal void CompleteLocalTransfer(Func<float> now)
        {
            Frozen = true; OwnerSeat = PendingOwner; Epoch = PendingEpoch; OwnerChangedAt = now(); ExpectFirstSnapshot = true;
        }
        internal bool CanFreeze(int sender, int localSeat, uint epoch) { return !Policy.UsesLeases && sender == 1 && Mine(localSeat) && epoch > Epoch; }
        internal bool CanAcceptGrant(bool host, int sender, uint epoch) { return !Policy.UsesLeases && !host && sender == 1 && epoch >= Epoch; }
        internal bool GrantChanged(int owner, uint epoch) { return !Known || OwnerSeat != owner || Epoch != epoch; }
        internal void ApplyGrant(int owner, uint epoch) { Known = true; OwnerSeat = owner; Epoch = epoch; Frozen = false; }
        internal bool CanAcceptHandoff(bool host, int sender, uint epoch, int owner)
        { return !Policy.UsesLeases && host && PendingTakeover && epoch == PendingEpoch && sender == OwnerSeat && owner == PendingOwner; }
        internal void CompleteHandoff(int owner, uint epoch, Func<float> now)
        { OwnerSeat = owner; Epoch = epoch; PendingTakeover = false; OwnerChangedAt = now(); }
        // 1: owner left, 2: pending target left.
        internal int SeatLeft(int seat, bool host, Func<float> now)
        {
            LoadedSeats.Remove(seat);
            if (Policy.UsesLeases) return 0; // The arbiter advances the lease epoch when a seat leaves.
            if (!host) return 0;
            if (OwnerSeat == seat)
            { OwnerSeat = 1; Epoch++; PendingTakeover = false; Frozen = true; OwnerChangedAt = now(); return 1; }
            if (PendingTakeover && PendingOwner == seat)
            { Epoch = PendingEpoch; PendingTakeover = false; Frozen = false; return 2; }
            return 0;
        }
        internal bool AcceptOwnerMessage(int localSeat, uint epoch, int sender, bool checkFrozen)
        { return Known && !Mine(localSeat) && (!checkFrozen || !Frozen) && Epoch == epoch && OwnerSeat == sender; }
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
