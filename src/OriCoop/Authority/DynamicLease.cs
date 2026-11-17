using System;
using System.Collections.Generic;
using UnityEngine;

namespace OriCoop.Authority
{
    internal sealed class DynamicLease
    {
        static IAuthority source;
        static readonly Dictionary<string, DynamicLease> leases = new Dictionary<string, DynamicLease>();
        public readonly string Key;
        public byte[] Data;
        public float ReceivedAt;
        public bool RemoteActive, Active, Blending;
        public uint StateEpoch, StateVersion;
        float requestAt = -10f, predictUntil = -10f;
        bool interacting, predictingInteraction, requested, cancelled;

        DynamicLease(string key) { Key = key; }

        public static DynamicLease For(string key)
        {
            IAuthority current = AuthoritySource.Current;
            if (source != current)
            {
                if (source != null) { source.StateReceived -= Receive; source.Reset -= Reset; }
                source = current; Reset();
                source.StateReceived += Receive; source.Reset += Reset;
            }
            DynamicLease lease;
            if (!leases.TryGetValue(key, out lease)) { lease = new DynamicLease(key); leases.Add(key, lease); }
            if (lease.Data == null)
            {
                StateUpdate cached = source.LatestState(key);
                if (cached != null) lease.Accept(cached);
            }
            return lease;
        }

        static void Reset()
        {
            foreach (DynamicLease lease in leases.Values)
            {
                lease.Data = null; lease.StateEpoch = lease.StateVersion = 0;
                lease.cancelled = lease.interacting = lease.predictingInteraction = lease.requested = lease.Active = lease.RemoteActive = lease.Blending = false;
                lease.requestAt = lease.predictUntil = -10f;
            }
        }

        static void Receive(StateUpdate state)
        {
            if (!state.Key.StartsWith("w:push:", StringComparison.Ordinal)
                && !state.Key.StartsWith("w:dynamic:", StringComparison.Ordinal)) return;
            if (state.Epoch != source.EpochOf(state.Key) || state.From == source.LocalSeat) return;
            DynamicLease lease;
            if (!leases.TryGetValue(state.Key, out lease)) { lease = new DynamicLease(state.Key); leases.Add(state.Key, lease); }
            lease.Accept(state);
        }

        void Accept(StateUpdate state)
        {
            DynamicLease lease = this;
            if (state.Epoch < lease.StateEpoch || (state.Epoch == lease.StateEpoch && state.Ver <= lease.StateVersion)) return;
            lease.Data = state.Data;
            lease.StateEpoch = state.Epoch; lease.StateVersion = state.Ver;
            lease.RemoteActive = state.Active; lease.ReceivedAt = Time.realtimeSinceStartup;
        }

        public bool Owns { get { return source.Owns(Key); } }
        public int Owner { get { return source.OwnerOf(Key); } }
        public uint Epoch { get { return source.EpochOf(Key); } }
        public bool Predicting { get { return !Owns && Time.realtimeSinceStartup < predictUntil; } }

        public void Tick(float distance, bool active, bool predict)
        {
            Tick(distance, active, predict, true);
        }

        public void Tick(float distance, bool active, bool predict, bool acquireIdle)
        {
            float now = Time.realtimeSinceStartup;
            cancelled = false;
            Active = active;
            bool startingPush = active && predict && !predictingInteraction;
            if (startingPush && !Owns)
            {
                predictUntil = now + 0.6f + NetPlay.RttMs * 0.002f;
                Blending = true;
            }
            if (((acquireIdle && Owner == 0) || active) && (now >= requestAt || startingPush || (active && !interacting))
                && (!Owns || !interacting || startingPush))
            {
                requestAt = now + 0.2f;
                requested = true;
                source.RequestLease(Key, distance, active);
            }
            interacting = active;
            predictingInteraction = active && predict;
            if (Owns) { predictUntil = -10f; Blending = false; }
        }

        public void Cancel()
        {
            if (!cancelled && (requested || Owns)) source.ReleaseLease(Key);
            cancelled = true;
            requested = interacting = predictingInteraction = Active = false; predictUntil = -10f;
        }

        public void Publish(byte[] data) { source.PublishState(Key, data, Active); }
    }
}
