using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace OriCoop.Rules
{
    internal sealed class ScreenDebts<TKey, TDebt> where TKey : class where TDebt : class
    {
        // The managed key still identifies its debt after the native screen is destroyed.
        sealed class KeyIdentity : IEqualityComparer<TKey>
        {
            public bool Equals(TKey a, TKey b) { return ReferenceEquals(a, b); }
            public int GetHashCode(TKey key) { return RuntimeHelpers.GetHashCode(key); }
        }

        readonly Dictionary<TKey, TDebt> entries = new Dictionary<TKey, TDebt>(new KeyIdentity());
        internal int Count { get { return entries.Count; } }
        public Dictionary<TKey, TDebt>.Enumerator GetEnumerator() { return entries.GetEnumerator(); }
        internal TDebt Set(TKey key, TDebt debt)
        {
            TDebt previous;
            entries.TryGetValue(key, out previous);
            entries[key] = debt;
            return previous;
        }
        internal bool TryGet(TKey key, out TDebt debt)
        {
            debt = null;
            return key != null && entries.TryGetValue(key, out debt);
        }
        internal bool IsCurrent(TKey key, TDebt expected)
        {
            TDebt current;
            return TryGet(key, out current) && ReferenceEquals(current, expected);
        }
        internal bool Remove(TKey key, TDebt expected)
        {
            return IsCurrent(key, expected) && entries.Remove(key);
        }
        internal IEnumerable<KeyValuePair<TKey, TDebt>> CurrentSnapshot()
        {
            if (entries.Count == 0) yield break;
            foreach (KeyValuePair<TKey, TDebt> pair in new List<KeyValuePair<TKey, TDebt>>(entries))
                if (IsCurrent(pair.Key, pair.Value)) yield return pair;
        }
        internal int Prune(Predicate<TKey> gone, Action<TDebt> retire)
        {
            List<KeyValuePair<TKey, TDebt>> stale = null;
            foreach (KeyValuePair<TKey, TDebt> pair in entries)
            {
                if (!gone(pair.Key)) continue;
                if (stale == null) stale = new List<KeyValuePair<TKey, TDebt>>();
                stale.Add(pair);
            }
            if (stale == null) return 0;
            int removed = 0;
            foreach (KeyValuePair<TKey, TDebt> pair in stale)
            {
                // Retire can reenter this table and remove or replace another debt.
                if (!gone(pair.Key) || !Remove(pair.Key, pair.Value)) continue;
                removed++;
                retire(pair.Value);
            }
            return removed;
        }
    }
}
