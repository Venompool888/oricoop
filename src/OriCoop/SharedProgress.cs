using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    // Shared resources between the local Oris. P1's save is the source of truth.
    // - Keystones, map stones and energy are one pool: a change on any Ori since the last sync is applied
    //   to P1, then P1's value is copied to every other local Ori (P2, and offline P3/P4, T-134). (Health stays per-Ori.)
    // - Another local Ori touching a pickup collects it for P1 (rewards land in P1's save; the pool sync shares them).
    public static class SharedProgress
    {
        static readonly SharedResources Resources = new SharedResources();
        static readonly List<SeinCharacter> s_extras = new List<SeinCharacter>();
        static readonly HashSet<int> s_pooled = new HashSet<int>();   // the Oris in the last sync (instance ids)

        public static void Reset() { Resources.Reset(); s_pooled.Clear(); }

        // Offline every local Ori is in the pool: P2 and, since T-057, P3/P4 (T-134). Online only this PC's P1 and p2.
        public static void Sync(SeinCharacter p1, SeinCharacter p2)
        {
            s_extras.Clear();
            if (NetPlay.Enabled) s_extras.Add(p2);
            else foreach (Puppet p in Players.Remotes) if (p.Sein != null) s_extras.Add(p.Sein);
            SyncAll(p1, s_extras);
        }

        static bool Valid(SeinCharacter s) { return s != null && s.Inventory != null && s.Energy != null; }

        public static void SyncAll(SeinCharacter p1, List<SeinCharacter> extras)
        {
            if (!Valid(p1) || extras.Count == 0) return;
            foreach (SeinCharacter e in extras) if (!Valid(e)) return;
            // An Ori that was not in the last sync (P3/P4 just joined) starts from the last synced values, so its own
            // starting numbers are not taken for a pickup or a spend.
            foreach (SeinCharacter e in extras) if (!s_pooled.Contains(e.GetInstanceID())) Join(e);
            if (Resources.Initialized)
            {
                SharedValues own = Values(p1);
                SharedValues[] others = new SharedValues[extras.Count];
                for (int i = 0; i < extras.Count; i++) others[i] = Values(extras[i]);
                SharedValues merged = Resources.MergeAll(own, p1.Energy.Max, others);
                p1.Inventory.Keystones = merged.Keys; p1.Inventory.MapStones = merged.Maps; p1.Energy.Current = merged.Energy;
            }
            foreach (SeinCharacter e in extras)
            {
                e.Inventory.Keystones = p1.Inventory.Keystones;
                e.Inventory.MapStones = p1.Inventory.MapStones;
                e.Energy.Current = p1.Energy.Current;
            }
            Resources.Remember(p1.Inventory.Keystones, p1.Inventory.MapStones, p1.Energy.Current);
            s_pooled.Clear();
            foreach (SeinCharacter e in extras) s_pooled.Add(e.GetInstanceID());
        }

        // A local Ori joining the pool (offline P3/P4): start from the last synced values.
        static void Join(SeinCharacter extra)
        {
            if (!Valid(extra) || !Resources.Initialized) return;
            SharedValues v = Resources.Baseline;
            extra.Inventory.Keystones = v.Keys; extra.Inventory.MapStones = v.Maps; extra.Energy.Current = v.Energy;
        }

        static SharedValues Values(SeinCharacter s)
        {
            SharedValues v = new SharedValues();
            v.Keys = s.Inventory.Keystones; v.Maps = s.Inventory.MapStones; v.Energy = s.Energy.Current;
            return v;
        }

        public static int PickupsByP2;
        public static readonly int[] PickupsBySeat = new int[Players.MaxSeats + 1];

        static readonly System.Reflection.FieldInfo s_timeField = AccessTools.Field(typeof(PickupBase), "m_currentTime");

        [HarmonyPatch(typeof(PickupBase), "FixedUpdate")]
        static class PickupPatch
        {
            static void Postfix(PickupBase __instance)
            {
                SeinCharacter p1 = StoryHost.Current.P1;
                if (p1 == null || __instance.IsCollected || !__instance.gameObject.activeInHierarchy) return;
                if ((float)s_timeField.GetValue(__instance) < __instance.DelayBeforeCollectable) return;
                if (NetPlay.Enabled) { TryCollect(__instance, p1, StoryHost.Current.P2, 2); return; }
                // Offline every puppet is a local Ori (P2, and P3/P4 since T-057): any of them collects for P1.
                foreach (Puppet p in Players.Remotes)
                    if (TryCollect(__instance, p1, p.Sein, p.Seat) || __instance.IsCollected) return;
            }
        }

        static bool TryCollect(PickupBase pickup, SeinCharacter p1, SeinCharacter by, int seat)
        {
            if (by == null || !by.gameObject.activeInHierarchy) return false;
            if (Vector3.Distance(pickup.transform.position, by.Position) >= pickup.Radius) return false;
            pickup.OnCollectorCandidateTouch(p1.gameObject);
            if (!pickup.IsCollected) return false;
            PickupsByP2++;
            if (seat >= 1 && seat < PickupsBySeat.Length) PickupsBySeat[seat]++;
            return true;
        }
    }
}
