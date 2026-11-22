using System;
using System.Collections.Generic;
using System.IO;
using Game;
using OriCoop.Authority;
using OriCoop.World;
using UnityEngine;

namespace OriCoop
{
    // T-168: the aggregate world objects (GameController: world events, string triggers, objectives; GameWorld: the map)
    // are no longer sent whole on a live change. Each PC proposes only the items it changed through IAuthority (the P2P
    // host's Arbiter or the C-server's); every PC applies the ordered batches (World/WorldDeltaPeer). Whole objects still
    // go in full snapshots (join, digest resync, host rollback); the end marker carries the watermark. The digest resync
    // stays as the safety net. Design: docs/tickets/T-168.md.
    public static partial class WorldSync
    {
        sealed class DeltaGame : IWorldDeltaGame
        {
            public bool TryRead(byte[] scene, byte[] id, out byte[] bytes) { return ReadAggregate(new MoonGuid(scene), new MoonGuid(id), out bytes); }
            public void Write(byte[] scene, byte[] id, byte schema, string type, byte[] bytes) { WriteAggregate(new MoonGuid(scene), new MoonGuid(id), (Meaning)schema, type, bytes); }
            public void NeedResync(string why) { RequestResync(why); }
        }

        static readonly WorldDeltaPeer s_delta = new WorldDeltaPeer(delegate { return AuthoritySource.Current; }, new DeltaGame());
        static bool s_versionLogged;
        // Test only (the T-168 same-RTT checks, TestHarness.Enabled): batches committed before this time are applied only
        // then, as if the network took that long. Test copies on one PC have an RTT of a few ms, far below the 250 ms
        // scan, so without it every PC would already have the others' changes when it scans and proposes.
        internal static float TestHoldBatchesUntil;
        static readonly List<CommittedEvent> s_testHeld = new List<CommittedEvent>();
        // Test only: scan on the next Tick (the change is proposed now, not up to 250 ms later).
        internal static void TestScanNow() { s_scanAt = 0; }
        // Aggregates whose bytes WorldItems cannot read (another game build): they go whole, as before T-168.
        static readonly HashSet<string> s_itemsOff = new HashSet<string>();

        static void InitDelta()
        {
            DeltaObject.MutateWholeBlob = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WORLD_WHOLE_BLOB") == "1";
            if (DeltaObject.MutateWholeBlob) Plugin.Log.LogWarning("net: MUTATION world items sent whole (ORICOOP_MUTATE_WORLD_WHOLE_BLOB)");
            WorldItems.MutateShareTriggers = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SHARE_TRIGGERS") == "1";
            if (WorldItems.MutateShareTriggers) Plugin.Log.LogWarning("net: MUTATION string triggers shared between PCs (ORICOOP_MUTATE_SHARE_TRIGGERS, pre-T-183)");
            IAuthority touch = AuthoritySource.Current;   // registers the 0xB0-0xBA handlers now (events may come before our first use)
            AuthoritySource.AnyCommitted += delegate(CommittedEvent e) {
                if (e.Kind != DeltaBatch.Kind || !NetPlay.Connected || VersionRefused()) return;
                if (TestHarness.Enabled && Time.realtimeSinceStartup < TestHoldBatchesUntil) { s_testHeld.Add(e); return; }
                try { s_delta.OnCommitted(e, SaveSync.Ready); }
                catch (Exception ex) { Plugin.Log.LogWarning("net: world item batch " + e.Id + " from P" + e.From + " failed: " + ex.Message); }
            };
        }

        static void ResetDelta()
        {
            s_delta.Reset(); s_versionLogged = false; s_itemsOff.Clear(); s_testHeld.Clear(); TestHoldBatchesUntil = 0;
            // A guest waits for the host's snapshot (s_awaitSnapshot). The host's own save is the snapshot: batches that
            // reach it before it can take world state (a C-server room replays up to 4096 old ones when this PC rejoins)
            // are older than that save and are dropped once it is ready (HostReady).
            s_delta.BeginSnapshot();
        }

        // Host, first Tick with the world loaded: everything received before belongs to the past (see ResetDelta).
        static void HostReady()
        {
            if (NetPlay.IsHost && s_delta.InSnapshot) s_delta.EndSnapshot(uint.MaxValue);
            if (s_testHeld.Count == 0 || Time.realtimeSinceStartup < TestHoldBatchesUntil) return;
            CommittedEvent[] held = s_testHeld.ToArray(); s_testHeld.Clear();
            foreach (CommittedEvent e in held)
                try { s_delta.OnCommitted(e, SaveSync.Ready); }
                catch (Exception ex) { Plugin.Log.LogWarning("net: world item batch " + e.Id + " from P" + e.From + " failed: " + ex.Message); }
        }

        static bool ItemsOff(string key, string type, Exception e)
        {
            if (s_itemsOff.Add(key)) Plugin.Log.LogWarning("net: world items disabled for " + type + " " + key + " (sent whole): " + e.Message);
            return true;
        }

        static bool DeltaActive
        {
            get { IAuthority a = AuthoritySource.Current; return NetPlay.Connected && a != null && a.Mode != AuthorityMode.None; }
        }

        // net5 world sync with a net4 PC would lose changes silently (it sends whole objects, ignores item batches).
        static bool VersionRefused()
        {
            string other = NetPlay.MismatchedVersion;
            if (other == null) return false;
            if (!s_versionLogged)
            {
                s_versionLogged = true;
                Plugin.Log.LogError("net: world sync OFF: " + other + " runs another protocol than ours (" + NetPlay.ModVersion
                    + "); doors, events and pickups are not shared until everyone runs the same build");
            }
            return true;
        }

        // Capture saw an aggregate object for the first time: its baseline for later changes.
        static void ObserveAggregate(Blob blob)
        {
            if (!WorldItems.IsAggregate((byte)blob.Schema)) return;
            string key = Key(blob.Scene, blob.Id);
            try { s_delta.Observe(blob.Scene.ToByteArray(), blob.Id.ToByteArray(), (byte)blob.Schema, blob.Bytes); }
            catch (Exception e) { ItemsOff(key, blob.Type, e); }
        }

        // The scan's changed objects: aggregates become item batches, the rest go on as whole blobs (message 12).
        static List<Blob> RouteAggregates(List<Blob> changed)
        {
            if (!DeltaActive) return changed;
            List<Blob> whole = new List<Blob>(changed.Count);
            foreach (Blob blob in changed)
            {
                string key = Key(blob.Scene, blob.Id);
                if (!WorldItems.IsAggregate((byte)blob.Schema) || s_itemsOff.Contains(key)) { whole.Add(blob); continue; }
                try
                {
                    s_delta.LocalChange(blob.Scene.ToByteArray(), blob.Id.ToByteArray(), (byte)blob.Schema, blob.Type, blob.Bytes);
                    s_known[key] = blob.Hash;
                }
                catch (Exception e) { ItemsOff(key, blob.Type, e); whole.Add(blob); }
            }
            return whole;
        }

        // The host captured a full snapshot: what the receivers get is the agreed state of each aggregate from now on.
        static void SnapshotSent()
        {
            foreach (Blob blob in s_blobs.Values)
                if (WorldItems.IsAggregate((byte)blob.Schema) && !s_itemsOff.Contains(Key(blob.Scene, blob.Id)))
                    try { s_delta.SentSnapshotObject(blob.Scene.ToByteArray(), blob.Id.ToByteArray(), (byte)blob.Schema, blob.Bytes); }
                    catch (Exception e) { ItemsOff(Key(blob.Scene, blob.Id), blob.Type, e); }
        }

        static bool ReadAggregate(MoonGuid scene, MoonGuid id, out byte[] bytes)
        {
            string key = Key(scene, id); bytes = null;
            ISerializable live = Find(scene, id);
            if (live != null && !s_bad.Contains(key) && !Excluded(live))
            {
                try { bytes = CaptureBytes(live); return true; }
                catch (Exception e) { Disable(key, live, e); return false; }
            }
            Blob known;
            if (s_blobs.TryGetValue(key, out known)) { bytes = known.Bytes; return true; }
            return false;
        }

        // An aggregate arrived whole (snapshot): the bytes to apply (the snapshot + my batches still on their way).
        static byte[] SnapshotAggregate(MoonGuid scene, MoonGuid id, Meaning schema, string type, byte[] bytes)
        {
            string key = Key(scene, id);
            if (s_itemsOff.Contains(key)) return bytes;
            try { return s_delta.SnapshotObject(scene.ToByteArray(), id.ToByteArray(), (byte)schema, bytes); }
            catch (Exception e) { ItemsOff(key, type, e); return bytes; }
        }

        static void WriteAggregate(MoonGuid scene, MoonGuid id, Meaning schema, string type, byte[] bytes)
        {
            string key = Key(scene, id);
            StoreCheckpoint(scene, id, bytes);
            ISerializable live = Find(scene, id);
            if (live != null && !s_bad.Contains(key) && NetPlay.LocalInGameplay()) ApplyLive(scene, id, bytes);
            else if (live != null && !s_bad.Contains(key)) s_deferred[key] = MakeBlob(scene, id, bytes, schema, type);
            if (!s_bad.Contains(key)) s_blobs[key] = MakeBlob(scene, id, bytes, schema, type);
            // Not "known": the next scan measures this object again, so a change of mine an incoming batch did not
            // overwrite (DeltaObject keeps it) is still proposed.
            s_known.Remove(key);
        }
    }
}
