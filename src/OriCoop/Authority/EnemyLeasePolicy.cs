using System;
using System.Collections.Generic;
using OriCoop.Enemies;

namespace OriCoop.Authority
{
    public sealed class EnemyLeasePolicy : IEnemyOwnerPolicy
    {
        readonly Func<IAuthority> current;
        static readonly byte[] heartbeat = new byte[0];
        readonly HashSet<string> requested = new HashSet<string>();
        IAuthority source;

        public EnemyLeasePolicy() : this(delegate { return AuthoritySource.Current; }) { }
        public EnemyLeasePolicy(Func<IAuthority> current) { this.current = current; }

        IAuthority Source
        {
            get
            {
                IAuthority next = current();
                if (!object.ReferenceEquals(source, next)) { requested.Clear(); source = next; }
                return source;
            }
        }
        static string Key(string key) { return "e:" + key; }
        public bool UsesLeases { get { return true; } }
        public int DesiredOwner(string key, Func<int> hostDecision) { return Source.OwnerOf(Key(key)); }
        public uint EpochOf(string key) { return Source.EpochOf(Key(key)); }

        // IAuthority does not expose other seats' scores. Rely on the arbiter's interest
        // handover instead of inventing client-side 30% / 1s hysteresis from puppet positions.
        // EnemyAuthority withdraws interest when the scene unloads or Ori leaves its radius.
        public void RequestOwnership(string key, float score, bool active)
        {
            if (float.IsNaN(score) || float.IsInfinity(score) || score < 0) { Release(key); return; }
            IAuthority authority = Source;
            requested.Add(key);
            authority.RequestLease(Key(key), score, active);
            // Lease requests only register interest; the arbiter renews LastActive on state.
            // Keep the loaded simulator active between 250ms interest polls, otherwise
            // another loaded seat steals it every MinHoldMs (500ms), regardless of score.
            // This empty ownership heartbeat leaves EnemySync's snapshots/life/hits alone.
            if (authority.Owns(Key(key))) authority.PublishState(Key(key), heartbeat, true);
        }
        public void Release(string key)
        {
            IAuthority authority = Source;
            bool hadInterest = requested.Remove(key);
            if (hadInterest || authority.Owns(Key(key))) authority.ReleaseLease(Key(key));
        }
    }
}
