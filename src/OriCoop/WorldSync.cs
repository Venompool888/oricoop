using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game;
using HarmonyLib;
using UnityEngine;
using OriCoop.Authority;
using OriCoop.World;

namespace OriCoop
{
    // SaveSceneManager's registry is the wire schema: scene GUID + object GUID + its normal Archive.
    // The cache includes objects seen on either peer, so unloaded scenes remain part of the shared world.
    public static partial class WorldSync
    {
        // The game's checkpoint reader deliberately refunds/normalizes Highlighted doors.
        // Until a dedicated transient-door protocol exists, only stable states are applied live.
        public const bool ReplicateHighlightedDoors = false;
        // ScenesManager's archive is a local camera/streaming target, not shared world state.
        public const bool ReplicateSceneLoader = false;
        // Unlike the other serializers, this one stops live coroutines even in WriteMode.
        public const bool ReplicateCoroutinePlatforms = false;
        const byte MsgBlobs = 12, MsgResync = 13, MsgDigest = 14, MsgManifest = 15;
        const int MaxMessage = 16000, ChunkSize = 14000, MaxBlob = 4 * 1024 * 1024, MaxParts = 8;
        const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;
        // The same schema is carried on the wire, so unloaded bytes have exactly the live hash meaning.
        enum Meaning : byte { Raw, Pickup, Controller, Door, Pedestal, Clock, BaseAnimation, LegacyAnimation,
            SpriteAnimation, Map, AmbientPhase, Path, Vent, SequencePlatform, RespawnPickup, Respawner, Body }

        sealed class Blob
        {
            public MoonGuid Scene, Id;
            public byte[] Bytes;
            public ulong Hash;
            public string Type;
            public Meaning Schema;
        }
        sealed class Partial
        {
            public byte[] Bytes;
            public int Received;
            public string Type;
            public Meaning Schema;
        }
        static readonly Dictionary<string, Blob> s_blobs = new Dictionary<string, Blob>();
        static readonly Dictionary<string, ulong> s_known = new Dictionary<string, ulong>();
        static readonly Dictionary<string, float> s_changed = new Dictionary<string, float>();
        static readonly HashSet<string> s_bad = new HashSet<string>();
        static readonly HashSet<string> s_excluded = new HashSet<string>();
        static readonly Dictionary<string, Partial> s_parts = new Dictionary<string, Partial>();
        static readonly Dictionary<string, Blob> s_deferred = new Dictionary<string, Blob>();
        static readonly Dictionary<string, DigestObject> s_remoteManifest = new Dictionary<string, DigestObject>();
        static readonly Dictionary<string, DigestObject> s_compareManifest = new Dictionary<string, DigestObject>();
        static readonly DigestRepair s_digestRepair = new DigestRepair();
        static readonly HashSet<string> s_loggedLocalTypes = new HashSet<string>();
        static readonly HashSet<int> s_pendingSnapshots = new HashSet<int>();
        // T-172: changes inside a story this PC only mirrors stay local (World/MirrorHold.cs).
        static readonly World.MirrorHold s_mirrorHold = new World.MirrorHold();
        static readonly HashSet<string> s_mirrorKeys = new HashSet<string>();
        public static int MirrorHeldTotal { get { return s_mirrorHold.HeldTotal; } }
        static bool s_init, s_awaitSnapshot, s_restorePending, s_fullPending, s_loggedHighlightedDoors, s_loggedSceneLoader;
        static float s_scanAt, s_digestAt, s_requestAt;
        static float s_bandwidthAt;
        static long s_deltaBytes, s_snapshotBytes, s_digestBytes;
        static bool s_sendingSnapshot;
        static uint s_digestSequence, s_receivedDigest;
        static int s_digestSender;
        static bool s_digestProgressDiffers;
        static float s_digestResyncAt, s_digestBackoff = DigestBackoffMin;
        const float DigestBackoffMin = 10f, DigestBackoffMax = 60f;
        public static int DigestResyncs, DigestResyncsDeferred;
        public static int DigestMismatchesIgnored;
        public static bool Applying;
        public static bool LastDigestMatch;
        public static float LastDigestAt;
        public static int LastRemoteCount, LastLocalCount;
        public static ulong LastRemoteHash, LastLocalHash;

        public static void Init()
        {
            if (s_init) return;
            s_init = true;
            NetPlay.On(MsgBlobs, Receive);
            InitDelta();   // T-168: GameController / GameWorld go item by item (WorldSync.Delta.cs)
            // T-115: the stone co-push handler and its P2P relay at load, not when this PC first runs a stone (a host
            // without that scene loaded never relayed a guest's push).
            PushBlockSync.EnsureRegistered();
            NetPlay.On(MsgResync, delegate(BinaryReader r) {
                string why = r.ReadString();
                if (!NetPlay.IsHost || !SaveSync.Ready || VersionRefused()) return;
                Plugin.Log.LogInfo("net: full resync requested: " + why);
                s_fullPending = true;
            });
            NetPlay.On(MsgDigest, ReceiveDigest);
            NetPlay.On(MsgManifest, ReceiveManifest);
            NetPlay.SessionStarted += Reset;
            NetPlay.SessionEnded += Reset;
        }

        static void Reset()
        {
            s_blobs.Clear(); s_known.Clear(); s_changed.Clear(); s_bad.Clear();
            s_excluded.Clear(); s_parts.Clear(); s_deferred.Clear();
            s_remoteManifest.Clear(); s_compareManifest.Clear(); s_loggedLocalTypes.Clear();
            s_awaitSnapshot = !NetPlay.IsHost; s_restorePending = s_fullPending = false;
            s_scanAt = 0; s_digestAt = Time.realtimeSinceStartup + 5f; s_requestAt = 0;
            Applying = false; LastDigestMatch = false; LastDigestAt = 0;
            s_loggedHighlightedDoors = false;
            s_loggedSceneLoader = false;
            s_pendingSnapshots.Clear();
            s_mirrorHold.Clear(); s_mirrorKeys.Clear(); s_storyProgress.Clear();
            s_bandwidthAt = Time.realtimeSinceStartup; s_deltaBytes = s_snapshotBytes = s_digestBytes = 0;
            s_digestSequence = s_receivedDigest = 0;
            ResetDigestRepair();
            ResetDelta();
        }

        public static void Tick()
        {
            if (!NetPlay.Connected || !SaveSync.Ready || !NetPlay.LocalInGameplay() || VersionRefused()) return;
            HostReady();
            if (!s_awaitSnapshot) s_delta.Flush();
            if (s_deferred.Count != 0 && !TestDeferApply)
            {
                Blob[] waiting = new List<Blob>(s_deferred.Values).ToArray(); s_deferred.Clear();
                foreach (Blob blob in waiting)
                {
                    ApplyLive(blob.Scene, blob.Id, blob.Bytes);
                    if (KeepsOwnCells(blob.Schema)) s_known.Remove(Key(blob.Scene, blob.Id));   // T-214, as in Apply
                }
            }
            if (s_restorePending)
            {
                s_restorePending = false;
                RequestResync("guest RestoreCheckpoint");
            }
            if (s_fullPending)
            {
                s_fullPending = false; SendSnapshot(); ProgressSync.SendSnapshot();
            }
            if (s_pendingSnapshots.Count > 0)
            {
                int[] seats = new int[s_pendingSnapshots.Count]; s_pendingSnapshots.CopyTo(seats); s_pendingSnapshots.Clear();
                foreach (int seat in seats) { SendSnapshot(seat); ProgressSync.SendSnapshot(seat); }
            }
            if (!NetPlay.AnyRemoteInGameplay || s_awaitSnapshot) return;
            float now = Time.realtimeSinceStartup;
            if (now >= s_scanAt) { s_scanAt = now + 0.25f; SendBlobs(RouteAggregates(Capture(false))); }
            if (now >= s_digestAt) { s_digestAt = now + 5f; SendDigest(); }
            if (now - s_bandwidthAt >= 5f)
            {
                float elapsed = now - s_bandwidthAt;
                Plugin.Log.LogInfo("net: world bytes/sec delta=" + (s_deltaBytes / elapsed).ToString("F1")
                    + " snapshot=" + (s_snapshotBytes / elapsed).ToString("F1")
                    + " digest=" + (s_digestBytes / elapsed).ToString("F1"));
                s_bandwidthAt = now; s_deltaBytes = s_snapshotBytes = s_digestBytes = 0;
            }
        }

        static string Key(MoonGuid scene, MoonGuid id) { return scene.ToString() + ":" + id.ToString(); }
        static bool Under(Component c, Component root)
        {
            return root != null && (c.transform == root.transform || c.transform.IsChildOf(root.transform));
        }

        public static bool Excluded(ISerializable serializable)
        {
            Component c = serializable as Component;
            if (c == null) return true;
            if (!ReplicateCoroutinePlatforms && serializable is SequencePlatform)
            {
                if (s_loggedLocalTypes.Add("SequencePlatform")) Plugin.Log.LogWarning("net: SequencePlatform sync DISABLED "
                    + "(ReplicateCoroutinePlatforms=false): SequencePlatform.cs:170-179 calls StopAllCoroutines in WriteMode");
                return true;
            }
            if (serializable is CameraPivotZone || serializable is CameraOffsetAnimationZone || serializable is GameStateMachine)
            {
                string type = serializable.GetType().Name;
                if (s_loggedLocalTypes.Add(type)) Plugin.Log.LogInfo("net: world excludes local presentation " + type
                    + " (CameraPivotZone.Serialize:108, CameraOffsetAnimationZone.Serialize:105, GameStateMachine.Serialize:98)");
                return true;
            }
            if (!ReplicateSceneLoader && serializable is ScenesManager)
            {
                if (!s_loggedSceneLoader)
                {
                    s_loggedSceneLoader = true;
                    Plugin.Log.LogWarning("net: scene loader live sync DISABLED (ReplicateSceneLoader=false): "
                        + "ScenesManager.cs:337-343 stores only the local camera/streaming target; join save still transfers it");
                }
                return true;
            }
            // Pushable stones: PushBlockSync streams them from the pushing player's game (playtest 2026-11-16 P1).
            // Rope chains / PhysicalSystemManager (hanging plants, joints): continuous local physics; hard-writing every
            // link 250 ms late made them jitter and kept the digest from ever matching (lag-test ①).
            if (PushBlockSync.OwnsSerializable(serializable) || WorldObjSync.OwnsSerializable(serializable) || serializable is Rope || serializable is PhysicalSystemManager)
            {
                string type = serializable is Rope ? "Rope" : serializable is PhysicalSystemManager ? "PhysicalSystemManager" : WorldObjSync.OwnsSerializable(serializable) ? serializable.GetType().Name : "PushPullBlock RigidbodySerialize";
                if (s_loggedLocalTypes.Add(type)) Plugin.Log.LogInfo("net: world keeps local physics " + type
                    + (type.StartsWith("Push") ? " (streamed by PushBlockSync, msgs 70/71)" : WorldObjSync.OwnsSerializable(serializable) ? " (carried by WorldObjSync, msgs 75/76)" : " (Rope.Serialize:189, PhysicalSystemManager)"));
                return true;
            }
            if (Under(c, P2Context.P1) || Under(c, Characters.Sein) || Under(c, P2Context.P2)
                || Under(c, Characters.Ori) || Under(c, P2Context.P2Companion)) return true;
            if (serializable is PlayerAbilities || serializable is SeinHealthController || serializable is SeinEnergy
                || serializable is SeinInventory || serializable is SeinLevel) return true;
            // Also exclude saveable child components of enemies (not only Entity's own save entry).
            for (Transform t = c.transform; t != null; t = t.parent)
                if (t.GetComponent<Entity>() != null || t.GetComponent<EntityController>() != null
                    || t.GetComponent<RespawningPlaceholder>() != null) return true;
            return false;
        }

        static void Visit(Action<MoonGuid, SaveSceneManager> visitor)
        {
            if (SaveSceneManager.Master != null) visitor(MoonGuid.Empty, SaveSceneManager.Master);
            if (Core.Scenes.Manager == null) return;
            // Copy because Serialize may enable/disable objects and indirectly change the registry.
            SceneManagerScene[] scenes = Core.Scenes.Manager.ActiveScenes.ToArray();
            foreach (SceneManagerScene scene in scenes)
                if (scene != null && scene.HasStartBeenCalled && scene.SceneRoot != null
                    && scene.MetaData != null && scene.SceneRoot.SaveSceneManager != null)
                    visitor(scene.MetaData.SceneMoonGuid, scene.SceneRoot.SaveSceneManager);
        }

        public static byte[] CaptureBytes(ISerializable serializable)
        {
            Archive ar = new Archive();
            try
            {
                ar.WriteMode(); serializable.Serialize(ar);
                // New archive per call: WriteMode resets position but does not truncate an old stream.
                return ar.MemoryStream.ToArray();
            }
            finally { ar.MemoryStream.Close(); }
        }

        static Meaning Describe(ISerializable value)
        {
            if (value is PickupBase) return Meaning.Pickup;
            if (value is GameController) return Meaning.Controller;
            if (value is DoorWithSlots || value is EnergyDoor) return Meaning.Door;
            if (value is GetAbilityPedestal || value is MapStone) return Meaning.Pedestal;
            if (value is GameTimer) return Meaning.Clock;
            if (value is BaseAnimatorSerializer) return Meaning.BaseAnimation;
            if (value is LegacyAnimatorSerializer) return Meaning.LegacyAnimation;
            if (value is SpriteAnimatorSerializer) return Meaning.SpriteAnimation;
            if (value is GameWorld) return Meaning.Map;
            if (value is SinMovement || value is SinMovingPlatform || value is LaserPulsator
                || value is OnTimerPerformAction || value is TimedTrigger) return Meaning.AmbientPhase;
            if (value.GetType().Name == "PathPositionFollower") return Meaning.Path;
            if (value is WindVent) return Meaning.Vent;
            if (value is SequencePlatformB) return Meaning.SequencePlatform;
            if (value is CollectablePlaceholder) return Meaning.RespawnPickup;
            if (value is Respawner) return Meaning.Respawner;
            if (value is RigidbodySerialize) return Meaning.Body;
            return Meaning.Raw;
        }

        static Blob MakeBlob(MoonGuid scene, MoonGuid id, byte[] bytes, Meaning schema, string type)
        {
            return new Blob { Scene = scene, Id = id, Bytes = bytes, Schema = schema, Type = type,
                Hash = CanonicalHash(schema, bytes) };
        }

        public static ulong CanonicalHash(ISerializable live, byte[] bytes)
        {
            if (live == null) throw new ArgumentNullException("live");
            return CanonicalHash(Describe(live), bytes);
        }

        static int ReadCount(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > 200000) throw new InvalidDataException("invalid canonical collection count");
            return count;
        }

        static void SortedGuidValues(BinaryReader r, BinaryWriter w)
        {
            int count = ReadCount(r);
            List<byte[]> entries = new List<byte[]>();
            for (int i = 0; i < count; i++)
            {
                byte[] entry = r.ReadBytes(20);
                if (entry.Length != 20) throw new EndOfStreamException();
                entries.Add(entry);
            }
            entries.Sort(delegate(byte[] a, byte[] b) {
                for (int j = 0; j < 16; j++) { int compare = a[j].CompareTo(b[j]); if (compare != 0) return compare; }
                return 0;
            });
            w.Write(count); foreach (byte[] entry in entries) w.Write(entry);
        }

        static void SortedStrings(BinaryReader r, BinaryWriter w)
        {
            int count = ReadCount(r); List<string> values = new List<string>();
            for (int i = 0; i < count; i++) values.Add(r.ReadString());
            values.Sort(StringComparer.Ordinal); w.Write(count);
            foreach (string value in values) w.Write(value);
        }

        static void SortedInts(BinaryReader r, BinaryWriter w, bool pairs)
        {
            int count = ReadCount(r); List<KeyValuePair<int, int>> values = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < count; i++) { int key = r.ReadInt32(); values.Add(new KeyValuePair<int, int>(key, pairs ? r.ReadInt32() : 0)); }
            values.Sort(delegate(KeyValuePair<int, int> a, KeyValuePair<int, int> b) { return a.Key.CompareTo(b.Key); });
            w.Write(count); foreach (KeyValuePair<int, int> value in values) { w.Write(value.Key); if (pairs) w.Write(value.Value); }
        }

        static ulong CanonicalHash(Meaning schema, byte[] bytes)
        {
            using (MemoryStream input = new MemoryStream(bytes))
            using (BinaryReader r = new BinaryReader(input))
            using (MemoryStream output = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(output))
            {
                w.Write((byte)schema);
                if (schema == Meaning.Controller)
                {
                    // GameController.cs:426-443: GUID/value dictionary, trigger multiset, clock/bootstrap, objectives.
                    // T-183: the string-trigger list is each PC's own (World/WorldItems.cs); it is not compared.
                    SortedGuidValues(r, w);
                    if (World.WorldItems.MutateShareTriggers) SortedStrings(r, w);
                    else using (BinaryWriter skip = new BinaryWriter(new MemoryStream())) SortedStrings(r, skip);
                    r.ReadSingle(); r.ReadSingle(); r.ReadInt32(); r.ReadInt32(); r.ReadBoolean();
                    SortedInts(r, w, false);
                }
                else if (schema == Meaning.Map)
                {
                    // RuntimeGameWorldArea.Serialize: state dictionary and icon GUID/value list, then revealed GUID set.
                    int areas = ReadCount(r); w.Write(areas);
                    for (int i = 0; i < areas; i++) { SortedInts(r, w, true); SortedGuidValues(r, w); }
                    int revealed = ReadCount(r); List<string> guids = new List<string>();
                    for (int i = 0; i < revealed; i++) guids.Add(Convert.ToBase64String(r.ReadBytes(16)));
                    guids.Sort(StringComparer.Ordinal); w.Write(revealed); foreach (string guid in guids) w.Write(guid);
                    w.Write(r.ReadInt32());
                }
                else if (schema == Meaning.Pickup)
                {
                    if (bytes.Length != 5) throw new InvalidDataException("unknown PickupBase Archive layout");
                    r.ReadSingle(); w.Write(r.ReadBoolean()); // PickupBase.cs:134-140: timer is collectability delay only.
                }
                else if (schema == Meaning.Door)
                {
                    if (bytes.Length != 16) throw new InvalidDataException("unknown door Archive layout");
                    r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                    w.Write(r.ReadInt32() == (int)DoorWithSlots.State.Opened); // proximity/highlight/insert animation are local.
                }
                else if (schema == Meaning.Pedestal)
                    w.Write(r.ReadInt32() == (int)GetAbilityPedestal.States.Completed);
                else if (schema == Meaning.Clock || schema == Meaning.AmbientPhase)
                {
                    if (bytes.Length != 4) throw new InvalidDataException("unknown local phase Archive layout");
                    r.ReadSingle(); // elapsed game time or autonomous per-frame phase; initial raw snapshot still transfers it.
                }
                else if (schema == Meaning.BaseAnimation)
                {
                    while (input.Position < input.Length) { bool playing = r.ReadBoolean(); float time = r.ReadSingle(); w.Write(playing); w.Write(playing ? 0f : time); }
                }
                else if (schema == Meaning.LegacyAnimation)
                {
                    while (input.Position < input.Length) { float time = r.ReadSingle(); bool stopped = r.ReadBoolean(); bool reversed = r.ReadBoolean(); w.Write(stopped); w.Write(reversed); w.Write(stopped ? time : 0f); }
                }
                else if (schema == Meaning.SpriteAnimation)
                {
                    float time = r.ReadSingle(); bool ended = r.ReadBoolean(), playing = r.ReadBoolean();
                    w.Write(ended); w.Write(playing); w.Write(playing ? 0f : time);
                }
                else if (schema == Meaning.Path)
                {
                    float speed = r.ReadSingle(); int node = r.ReadInt32(); float distance = r.ReadSingle();
                    w.Write(speed); w.Write(speed == 0f ? node : 0); w.Write(speed == 0f ? distance : 0f);
                }
                else if (schema == Meaning.Vent) { r.ReadSingle(); w.Write(r.ReadInt32()); w.Write(r.ReadBoolean()); }
                else if (schema == Meaning.SequencePlatform)
                {
                    w.Write(r.ReadBoolean()); r.ReadSingle(); w.Write(r.ReadBoolean()); r.ReadSingle();
                    w.Write(r.ReadSingle() > 0f); // pending activation, not the advancing delay.
                    w.Write(r.ReadSingle()); w.Write(r.ReadSingle()); w.Write(r.ReadSingle()); r.ReadSingle(); // Max/Min/Speed, not interpolation Value.
                }
                else if (schema == Meaning.RespawnPickup) { w.Write(r.ReadBoolean()); r.ReadSingle(); }
                else if (schema == Meaning.Respawner) { r.ReadInt32(); r.ReadInt32(); w.Write(r.ReadBoolean()); r.ReadSingle(); }
                else if (schema == Meaning.Body)
                {
                    // RigidbodySerialize.Serialize: pose / velocities of a simulated body. Both games simulate it, so the
                    // floats never agree exactly; only the layout counts for the digest (the bytes still sync).
                    w.Write(bytes.Length); input.Position = input.Length;
                }
                else { w.Write(bytes); input.Position = input.Length; }
                if (input.Position != input.Length) throw new InvalidDataException("unrecognized canonical Archive suffix");
                w.Flush(); return HashBytes(output.ToArray());
            }
        }

        // Pure Archive fixtures: safe before Unity/gameplay exists, and used by phase W.
        public static string CanonicalSelfTestFailure = "";
        public static bool CanonicalSelfTest()
        {
            try
            {
                byte[] early = { 0, 0, 0, 0, 0 }, late = { 0, 0, 128, 63, 0 }, collected = { 0, 0, 128, 63, 1 };
                CanonicalSelfTestFailure = "pickup timer must not change meaning";
                if (CanonicalHash(Meaning.Pickup, early) != CanonicalHash(Meaning.Pickup, late)) return false;
                CanonicalSelfTestFailure = "pickup collection must change meaning";
                if (CanonicalHash(Meaning.Pickup, late) == CanonicalHash(Meaning.Pickup, collected)) return false;
                byte[] normal = new byte[16], highlighted = new byte[16], opened = new byte[16];
                highlighted[4] = 1; highlighted[12] = 1; opened[4] = 2; opened[12] = 2;
                CanonicalSelfTestFailure = "door proximity/insertion must not change durable meaning";
                if (CanonicalHash(Meaning.Door, normal) != CanonicalHash(Meaning.Door, highlighted)) return false;
                CanonicalSelfTestFailure = "completed door must change meaning";
                if (CanonicalHash(Meaning.Door, normal) == CanonicalHash(Meaning.Door, opened)) return false;
                byte[] first = ControllerFixture(false, 1f, 7), reordered = ControllerFixture(true, 912f, 7);
                CanonicalSelfTestFailure = "dictionary/list order and GameTime must not change controller meaning";
                if (CanonicalHash(Meaning.Controller, first) != CanonicalHash(Meaning.Controller, reordered)) return false;
                CanonicalSelfTestFailure = "world event value must change controller meaning";
                if (CanonicalHash(Meaning.Controller, first) == CanonicalHash(Meaning.Controller, ControllerFixture(false, 1f, 8))) return false;
                CanonicalSelfTestFailure = "the string-trigger list must not change controller meaning (T-183: each PC's own)";
                if (!World.WorldItems.MutateShareTriggers && CanonicalHash(Meaning.Controller, first)
                    != CanonicalHash(Meaning.Controller, World.WorldItems.WithTriggers(first, new string[] { "returnFromWorldMap" }))) return false;
                CanonicalSelfTestFailure = ""; return true;
            }
            catch (Exception e) { CanonicalSelfTestFailure += ": " + e.Message; return false; }
        }

        static byte[] ControllerFixture(bool reverse, float clock, int value)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(2);
                for (int i = 0; i < 2; i++)
                {
                    int id = reverse ? 2 - i : i + 1;
                    w.Write(new MoonGuid(id, 0, 0, 0).ToByteArray()); w.Write(id == 1 ? value : 9);
                }
                w.Write(2); w.Write(reverse ? "beta" : "alpha"); w.Write(reverse ? "alpha" : "beta");
                w.Write(0f); w.Write(clock); w.Write(0); w.Write(0); w.Write(reverse);
                w.Write(2); w.Write(reverse ? 9 : 4); w.Write(reverse ? 4 : 9);
                w.Flush(); return stream.ToArray();
            }
        }

        static List<Blob> Capture(bool full)
        {
            List<Blob> changed = new List<Blob>();
            s_mirrorKeys.Clear();
            Visit(delegate(MoonGuid scene, SaveSceneManager manager) {
                SaveSceneManager.SaveId[] entries = manager.SaveData.ToArray();
                foreach (SaveSceneManager.SaveId entry in entries)
                {
                    if (entry == null || entry.Id == null || entry.SaveObject == null) continue;
                    string key = Key(scene, entry.Id);
                    ISerializable save = entry.Save;
                    if (Excluded(save)) { s_excluded.Add(key); s_blobs.Remove(key); continue; }
                    s_excluded.Remove(key);
                    if (s_bad.Contains(key)) continue;
                    if (StorySync.MirrorOwns(save as Component)) s_mirrorKeys.Add(key);
                    if (save is ActionSequenceSerializer && StorySequenceOf(save) != null) s_storyProgress.Remember(key);   // T-207 review 7
                    try
                    {
                        byte[] bytes = CaptureBytes(save);
                        if (bytes.Length > MaxBlob) throw new InvalidDataException("Archive exceeds 4 MB limit");
                        Blob blob = MakeBlob(scene, entry.Id, bytes, Describe(save), save.GetType().FullName);
                        Blob old;
                        bool seen = s_blobs.TryGetValue(key, out old);
                        if (seen && old.Hash != blob.Hash)
                            s_changed[key] = Time.realtimeSinceStartup;
                        else if (!seen) ObserveAggregate(blob);
                        s_blobs[key] = blob;
                    }
                    catch (Exception e)
                    {
                        s_bad.Add(key); s_blobs.Remove(key);
                        Plugin.Log.LogWarning("net: world Serialize disabled " + save.GetType().Name + " " + key + ": " + e.Message);
                    }
                }
            });
            // Include known unloaded objects edited by a progress event or a host checkpoint rollback.
            foreach (KeyValuePair<string, Blob> pair in s_blobs)
            {
                ulong known;
                if (!s_bad.Contains(pair.Key) && !s_excluded.Contains(pair.Key)
                    && (full || !s_known.TryGetValue(pair.Key, out known) || known != pair.Value.Hash))
                {
                    // T-172: a mirror's change of the story's own script state is not proposed (see MirrorHold).
                    bool wasHeld = s_mirrorHold.IsHeld(pair.Key);
                    if (!full && s_mirrorHold.Hold(pair.Key, s_mirrorKeys.Contains(pair.Key)))
                    {
                        if (!wasHeld) CoopTrace.Event("story", "mirror.state.held", "key", pair.Key, "type", pair.Value.Type);
                        continue;
                    }
                    if (wasHeld) CoopTrace.Event("story", "mirror.state.released", "key", pair.Key, "type", pair.Value.Type);
                    changed.Add(pair.Value);
                }
            }
            return changed;
        }

        public static void SendSnapshot(int seat = 0)
        {
            if (!NetPlay.Connected || !SaveSync.Ready || !NetPlay.IsHost) return;
            if (!NetPlay.LocalInGameplay())
            {
                if (seat == 0) s_fullPending = true; else s_pendingSnapshots.Add(seat);
                return;
            }
            float start = Time.realtimeSinceStartup;
            Capture(true);
            SnapshotSent();
            uint watermark = s_delta.Watermark;
            SendBlobsMessage(seat, delegate(BinaryWriter w) { w.Write((byte)1); });
            List<Blob> blobs = new List<Blob>(s_blobs.Values);
            s_sendingSnapshot = true;
            try { SendBlobs(blobs, seat); }
            finally { s_sendingSnapshot = false; }
            SendBlobsMessage(seat, delegate(BinaryWriter w) {
                w.Write((byte)2); w.Write(watermark); StorySync.WriteWellActivations(w);
            });
            Plugin.Log.LogInfo("net: full world snapshot " + blobs.Count + " objects in "
                + ((Time.realtimeSinceStartup - start) * 1000f).ToString("F1") + " ms");
        }

        static void SendBlobsMessage(int seat, Action<BinaryWriter> body)
        {
            if (seat == 0) NetPlay.Send(MsgBlobs, true, body);
            else NetPlay.SendTo(seat, MsgBlobs, true, body);
        }

        static void SendBlobs(List<Blob> blobs, int seat = 0)
        {
            MemoryStream batch = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(batch);
            int count = 0;
            foreach (Blob blob in blobs)
            {
                int offset = 0;
                do
                {
                    int length = Math.Min(ChunkSize, blob.Bytes.Length - offset);
                    int metadata = 48 + Encoding.UTF8.GetByteCount(blob.Type);
                    if (batch.Length + metadata + length > MaxMessage - 4)
                    { FlushBatch(batch, writer, count, seat); count = 0; }
                    writer.Write(blob.Scene.ToByteArray()); writer.Write(blob.Id.ToByteArray());
                    writer.Write((byte)blob.Schema); writer.Write(blob.Type);
                    writer.Write(blob.Bytes.Length); writer.Write(offset); writer.Write((ushort)length);
                    writer.Write(blob.Bytes, offset, length); count++;
                    offset += length;
                } while (offset < blob.Bytes.Length);
                if (seat == 0) s_known[Key(blob.Scene, blob.Id)] = blob.Hash;
            }
            FlushBatch(batch, writer, count, seat);
            writer.Close(); batch.Close();
        }

        static void FlushBatch(MemoryStream batch, BinaryWriter writer, int count, int seat)
        {
            if (count == 0) return;
            writer.Flush(); byte[] bytes = batch.ToArray();
            SendBlobsMessage(seat, delegate(BinaryWriter w) {
                w.Write((byte)0); w.Write((ushort)count); w.Write(bytes);
            });
            if (s_sendingSnapshot) s_snapshotBytes += bytes.Length + 4;
            else s_deltaBytes += bytes.Length + 4;
            batch.SetLength(0); batch.Position = 0;
        }

        static void Receive(BinaryReader reader)
        {
            if (!SaveSync.Ready || VersionRefused()) return;
            byte kind = reader.ReadByte();
            if (kind == 1)
            {
                if (!NetPlay.IsHost) { s_awaitSnapshot = true; s_parts.Clear(); s_delta.BeginSnapshot(); }
                return;
            }
            if (kind == 2)
            {
                uint watermark = reader.ReadUInt32();
                if (!NetPlay.IsHost)
                {
                    StorySync.MergeWellActivations(reader);
                    s_awaitSnapshot = false; s_scanAt = Time.realtimeSinceStartup + 0.25f;
                    s_delta.EndSnapshot(watermark); s_digestRepair.SnapshotReceived(); SnapshotsEnded++;
                }
                return;
            }
            if (kind != 0) throw new InvalidDataException("unknown world batch");
            int count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                MoonGuid scene = new MoonGuid(reader), id = new MoonGuid(reader);
                Meaning schema = (Meaning)reader.ReadByte(); string type = reader.ReadString();
                // Bound = the LAST Meaning. It said Respawner after Body was appended, so every batch holding a Body blob
                // was rejected whole ("bad message: invalid world type descriptor") and the guest missed the rest of it.
                if ((byte)schema > (byte)Meaning.Body || type.Length > 256) throw new InvalidDataException("invalid world type descriptor");
                int total = reader.ReadInt32(), offset = reader.ReadInt32(), length = reader.ReadUInt16();
                if (total < 0 || total > MaxBlob || offset < 0 || offset > total || length > total - offset)
                    throw new InvalidDataException("invalid world blob size");
                byte[] bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                string key = Key(scene, id);
                if (offset == 0 && length == total) { Apply(scene, id, bytes, schema, type); continue; }
                Partial partial;
                if (offset == 0)
                {
                    // Remote input: at most MaxParts unfinished blobs (each up to MaxBlob) are buffered at once.
                    if (!s_parts.ContainsKey(key) && s_parts.Count >= MaxParts) throw new InvalidDataException("too many partial world blobs");
                    partial = new Partial { Bytes = new byte[total], Type = type, Schema = schema };
                    s_parts[key] = partial;
                }
                if (!s_parts.TryGetValue(key, out partial) || partial.Bytes.Length != total || partial.Received != offset
                    || partial.Type != type || partial.Schema != schema)
                    throw new InvalidDataException("out of order world blob fragment");
                Buffer.BlockCopy(bytes, 0, partial.Bytes, offset, length); partial.Received += length;
                if (partial.Received == total) { s_parts.Remove(key); Apply(scene, id, partial.Bytes, schema, type); }
            }
        }

        public static ISerializable FindAny(MoonGuid id)
        {
            ISerializable found = null;
            Visit(delegate(MoonGuid scene, SaveSceneManager manager) {
                if (found == null) found = manager.IdToSaveSerialize(id);
            });
            return found;
        }

        static ISerializable Find(MoonGuid scene, MoonGuid id)
        {
            ISerializable found = null;
            Visit(delegate(MoonGuid sceneId, SaveSceneManager manager) {
                if (sceneId == scene) found = manager.IdToSaveSerialize(id);
            });
            return found;
        }

        static Archive ReadArchive(byte[] bytes)
        {
            Archive archive = new Archive();
            archive.MemoryStream.Write(bytes, 0, bytes.Length); archive.ReadMode();
            return archive;
        }

        static void Store(SaveScene saveScene, MoonGuid id, byte[] bytes)
        {
            SaveObject value = new SaveObject(id);
            value.Data.MemoryStream.Write(bytes, 0, bytes.Length); value.Data.ReadMode();
            for (int i = 0; i < saveScene.SaveObjects.Count; i++)
                if (saveScene.SaveObjects[i].Id == id) { saveScene.SaveObjects[i] = value; return; }
            saveScene.SaveObjects.Add(value);
        }

        static void StoreCheckpoint(MoonGuid scene, MoonGuid id, byte[] bytes)
        {
            SaveGameData data = Game.Checkpoint.SaveGameData;
            Store(scene == MoonGuid.Empty ? data.Master : data.InsertScene(scene), id, bytes);
            // PendingScenes overrides Scenes during SceneRoot.LateStart, and later ApplyPendingScenes.
            SaveScene pending;
            if (data.PendingScenes.TryGetValue(scene, out pending)) Store(pending, id, bytes);
        }

        static void RememberLocal(MoonGuid scene, MoonGuid id, byte[] bytes, Meaning fallback, string fallbackType)
        {
            string key = Key(scene, id);
            ISerializable live = Find(scene, id); Blob prior;
            Meaning schema = live != null ? Describe(live) : s_blobs.TryGetValue(key, out prior) ? prior.Schema : fallback;
            string type = live != null ? live.GetType().FullName : s_blobs.TryGetValue(key, out prior) ? prior.Type : fallbackType;
            s_blobs[key] = MakeBlob(scene, id, bytes, schema, type);
            s_changed[key] = Time.realtimeSinceStartup;
        }

        static void Apply(MoonGuid scene, MoonGuid id, byte[] bytes, Meaning schema, string type)
        {
            string key = Key(scene, id);
            ISerializable live = Find(scene, id);
            if (s_excluded.Contains(key) || (live != null && Excluded(live))) return;
            if (live != null && Describe(live) != schema) throw new InvalidDataException("world schema differs for " + key);
            int[] forwardTo = null;
            if (NetPlay.IsHost)
            {
                // Detect a local edit even if it happened since our previous 250 ms scan.
                Blob previous;
                if (live != null && !s_bad.Contains(key) && s_blobs.TryGetValue(key, out previous))
                {
                    try
                    {
                        byte[] current = CaptureBytes(live); ulong hash = CanonicalHash(schema, current);
                        if (hash != previous.Hash)
                        {
                            previous = MakeBlob(scene, id, current, schema, type);
                            s_blobs[key] = previous; s_changed[key] = Time.realtimeSinceStartup;
                        }
                    }
                    catch (Exception e) { Disable(key, live, e); }
                }
                float changed;
                if (!s_changed.TryGetValue(key, out changed)) changed = World.WorldRelay.Never;
                World.RelayPlan plan = World.WorldRelay.OnRemoteBlob(true, NetPlay.ServerForwardsAll, NetPlay.Sender,
                    RemoteDevices(), Time.realtimeSinceStartup, changed);
                if (!plan.Apply)
                {
                    Blob authoritative;
                    if (plan.SendOwnToAll && s_blobs.TryGetValue(key, out authoritative)) SendBlobs(new List<Blob> { authoritative });
                    return;
                }
                forwardTo = plan.ForwardTo;
            }
            // T-168: an aggregate object only comes whole in a snapshot; it becomes the agreed state and my own batches
            // still on their way to the arbiter stay on top of it.
            else if (DeltaActive && World.WorldItems.IsAggregate((byte)schema))
                bytes = SnapshotAggregate(scene, id, schema, type, bytes);
            // T-183: a partner's GameController never replaces this PC's string-trigger list (World/WorldItems.cs).
            if (schema == Meaning.Controller) bytes = LocalTriggers(live, key, bytes);
            // T-207 (1): a partner's story-sequence progress stays out of this PC's checkpoint too (StoryProgressKeys).
            if (s_storyProgress.StoreInCheckpoint(key, live != null, StorySequenceOf(live) != null)) StoreCheckpoint(scene, id, bytes);
            else { StoryProgressNotStored++; CoopTrace.Event("story", "sequence.progress.not-stored", "key", key); }
            if (live != null && !s_bad.Contains(key) && NetPlay.LocalInGameplay() && !TestDeferApply) ApplyLive(scene, id, bytes);
            else if (live != null && !s_bad.Contains(key))
            {
                s_deferred[key] = MakeBlob(scene, id, bytes, schema, type);
            }
            ulong incoming = CanonicalHash(schema, bytes);
            if (!s_bad.Contains(key)) s_blobs[key] = MakeBlob(scene, id, bytes, schema, type);
            else s_blobs.Remove(key);
            s_known[key] = incoming; // Remote application is never a new local proposal.
            // T-214: except the map cells this PC kept on top of a snapshot (DeltaObject.Reset): the next scan measures
            // the map again and proposes them.
            if (KeepsOwnCells(schema)) s_known.Remove(key);
            s_mirrorHold.Applied(key);
            // T-141: the other guests (P2P / relay: connected only to us) get the accepted blob now, not after a digest
            // resync 5-60 s later, during which their own whole-GameController blob could overwrite it again.
            if (forwardTo != null && forwardTo.Length > 0 && !s_mutateNoRebroadcast)
            {
                List<Blob> accepted = new List<Blob> { MakeBlob(scene, id, bytes, schema, type) };
                foreach (int seat in forwardTo) SendBlobs(accepted, seat);
            }
        }

        // Test-only mutation (net-3p world check): the host keeps an accepted guest blob to itself again (pre-T-141).
        static readonly bool s_mutateNoRebroadcast = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WORLD_NO_REBROADCAST") == "1";

        // Device seats this PC is connected to (a guest device's extra local seats share its peer: one copy per device).
        static int[] RemoteDevices()
        {
            List<int> devices = new List<int>();
            foreach (int seat in NetPlay.RemoteSeats)
            {
                int device = NetPlay.DeviceSeat(seat);
                if (!devices.Contains(device)) devices.Add(device);
            }
            return devices.ToArray();
        }

        // T-214: a guest's map after a full snapshot may hold cells of its own on top (DeltaObject.Reset). Applying the
        // snapshot records the map as known; forgetting that makes the next scan measure it and propose those cells.
        static bool KeepsOwnCells(Meaning schema)
        { return !NetPlay.IsHost && schema == Meaning.Map && World.WorldItems.HasGrowing((byte)schema); }

        static void ApplyLive(MoonGuid scene, MoonGuid id, byte[] bytes)
        {
            string key = Key(scene, id);
            ISerializable live = Find(scene, id);
            if (live == null || Excluded(live) || s_bad.Contains(key)) return;
            if (!CanApplyDoor(live, bytes)) return;
            ActionSequence story = StorySequenceOf(live);
            if (story != null && story.IsRunning) { ProgressKept(story, key, "sequence.progress.kept"); return; }
            int storyIndex = story != null ? story.Index : 0;
            // T-183: a deferred or item-rewritten GameController carries the trigger list of when it was made.
            if (live is GameController) bytes = LocalTriggers(live, key, bytes);
            Archive archive = ReadArchive(bytes); Applying = true;
            try { live.Serialize(archive); }
            catch (Exception e) { Disable(key, live, e); }
            finally { Applying = false; archive.MemoryStream.Close(); }
            if (story != null && story.IsRunning)
            { story.Index = storyIndex; story.IsRunning = false; ProgressKept(story, key, "sequence.progress.not-started"); }
            s_known[key] = CanonicalHash(live, bytes);
        }

        // T-196: ActionSequenceSerializer saves only "which step, running or not". Applied to a story sequence that runs
        // here, a partner's value moved it to the partner's step without running the steps in between, or stopped it
        // (playtest 4, Sein's pickup: the guest's resumed copy got past "29. Stop Character Animation" first, the host
        // jumped there and its Ori stood in the character animation for 55 s). Two rules for a story sequence:
        // - while it runs here it keeps its own progress;
        // - the partner's progress never starts it here. Only its own trigger or StorySync's mirror does: a copy that
        //   had ended (the mirror was cut, or its player closed the story text first) was started again at the
        //   partner's step and ran the tail of the script a second time.
        // A partner's "not running" value is still taken by a copy that is not running here.
        // Test-only mutation ORICOOP_MUTATE_SEQ_PROGRESS_SHARED: the partner's progress replaces it again.
        static readonly bool s_mutateSharedProgress = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SEQ_PROGRESS_SHARED") == "1";
        public static int SequenceProgressKept, StoryProgressNotStored;
        static readonly World.StoryProgressKeys s_storyProgress = new World.StoryProgressKeys();
        // Test only (NetSeinTest, T-207 item 1): what this PC's checkpoint holds for a sequence's progress.
        // False: the checkpoint has no entry for it.
        internal static bool TestCheckpointProgress(ActionSequenceSerializer progress, out int index, out bool running)
        {
            index = -1; running = false;
            if (progress == null || progress.MoonGuid == null) return false;
            MoonGuid scene = MoonGuid.Empty;
            for (Transform t = progress.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null && root.MetaData != null) { scene = root.MetaData.SceneMoonGuid; break; }
            }
            SaveGameData data = Game.Checkpoint.SaveGameData;
            SaveScene saved = scene == MoonGuid.Empty ? data.Master : data.InsertScene(scene);
            foreach (SaveObject o in saved.SaveObjects)
            {
                if (o.Id != progress.MoonGuid) continue;
                byte[] bytes = o.Data.MemoryStream.ToArray();
                if (bytes.Length < 5) return false;
                index = BitConverter.ToInt32(bytes, 0); running = bytes[4] != 0;
                return true;
            }
            return false;
        }
        static ActionSequence StorySequenceOf(ISerializable live)
        {
            ActionSequenceSerializer progress = live as ActionSequenceSerializer;
            if (progress == null || s_mutateSharedProgress) return null;
            ActionSequence sequence = progress.GetComponent<ActionSequence>();
            return sequence != null && StorySync.IsStorySequence(sequence) ? sequence : null;
        }
        static void ProgressKept(ActionSequence sequence, string key, string what)
        {
            SequenceProgressKept++;
            CoopTrace.Event("story", what, "id", sequence.name, "index", sequence.Index, "key", key);
        }

        static byte[] LocalTriggers(ISerializable live, string key, byte[] bytes)
        {
            if (live == null || s_bad.Contains(key) || World.WorldItems.MutateShareTriggers) return bytes;
            try { return World.WorldItems.KeepLocalTriggers(bytes, CaptureBytes(live)); }
            catch (Exception e) { Plugin.Log.LogWarning("net: world trigger list kept as received " + key + ": " + e.Message); return bytes; }
        }

        static bool CanApplyDoor(ISerializable live, byte[] bytes)
        {
            DoorWithSlots keystone = live as DoorWithSlots;
            EnergyDoor energy = live as EnergyDoor;
            if (ReplicateHighlightedDoors || (keystone == null && energy == null) || bytes.Length < 16) return true;
            // Both verified serializers write three Int32 fields before their Int32 State.
            int incoming = BitConverter.ToInt32(bytes, 12);
            bool localHighlighted = keystone != null ? keystone.CurrentState == DoorWithSlots.State.Highlighted
                : energy.CurrentState == EnergyDoor.State.Highlighted;
            if (incoming != (int)DoorWithSlots.State.Highlighted
                && (!localHighlighted || incoming == (int)DoorWithSlots.State.Opened)) return true;
            if (!s_loggedHighlightedDoors)
            {
                s_loggedHighlightedDoors = true;
                Plugin.Log.LogWarning("net: highlighted door live sync DISABLED (ReplicateHighlightedDoors=false): "
                    + "DoorWithSlots.cs:181-197 / EnergyDoor.cs:164-180 normalize transient state; checkpoint bytes retained");
            }
            return false;
        }

        static void Disable(string key, ISerializable live, Exception e)
        {
            if (s_bad.Add(key)) Plugin.Log.LogWarning("net: world Serialize disabled " + live.GetType().Name + " " + key + ": " + e.Message);
        }

        public static bool MarkCollected(MoonGuid id)
        {
            bool marked = false;
            Visit(delegate(MoonGuid scene, SaveSceneManager manager) {
                PickupBase pickup = manager.IdToSaveSerialize(id) as PickupBase;
                if (pickup == null || Excluded(pickup)) return;
                // Reward has already been applied by ProgressSync; do not replay Collected actions.
                pickup.IsCollected = true; pickup.gameObject.SetActive(false);
                byte[] bytes = CaptureBytes(pickup);
                StoreCheckpoint(scene, id, bytes);
                RememberLocal(scene, id, bytes, Meaning.Pickup, typeof(PickupBase).FullName);
                marked = true;
            });
            if (marked) return true;
            // PickupBase.Serialize is exactly float m_currentTime + bool IsCollected (PickupBase.cs:134).
            // Only edit a checkpoint record with that exact schema; unknown custom schemas stay disabled.
            foreach (SaveScene scene in Game.Checkpoint.SaveGameData.Scenes.Values)
                for (int i = 0; i < scene.SaveObjects.Count; i++)
                {
                    SaveObject save = scene.SaveObjects[i];
                    if (save.Id != id || save.Data.MemoryStream.Length != 5) continue;
                    byte[] bytes = save.Data.MemoryStream.ToArray(); bytes[4] = 1;
                    StoreCheckpoint(scene.SceneGUID, id, bytes);
                    RememberLocal(scene.SceneGUID, id, bytes, Meaning.Pickup, typeof(PickupBase).FullName); return true;
                }
            Plugin.Log.LogWarning("net: unloaded pickup collected flag unavailable for " + id);
            return false;
        }

        // ---- T-214: a pickup's GUID is shared by every copy of its prefab; its scene and spot pick the copy ----
        // The scene (GUID) an object belongs to; Empty = none of the loaded scenes.
        public static MoonGuid SceneOf(Component c)
        {
            MoonGuid found = MoonGuid.Empty;
            if (c == null) return found;
            Transform root = c.transform.root;
            Visit(delegate(MoonGuid scene, SaveSceneManager manager) {
                if (scene != MoonGuid.Empty && manager != null && manager.transform.root == root) found = scene;
            });
            return found;
        }

        static bool PickupAt(PickupBase p, bool hasSpot, Vector2 spot)
        {
            if (!hasSpot) return true;
            Vector3 at = p.transform.position;
            float dx = at.x - spot.x, dy = at.y - spot.y;
            return dx * dx + dy * dy <= Story.PickupPlace.SameSpot * Story.PickupPlace.SameSpot;
        }

        // The copy with this GUID in that scene (Empty = scene not known) at that spot (hasSpot false = spot not known).
        public static PickupBase FindPickup(MoonGuid id, MoonGuid scene, bool hasSpot, Vector2 spot, PickupBase[] active = null)
        {
            PickupBase found = null;
            Visit(delegate(MoonGuid loaded, SaveSceneManager manager) {
                if (found != null || (scene != MoonGuid.Empty && loaded != scene)) return;
                PickupBase p = manager.IdToSaveSerialize(id) as PickupBase;
                if (p != null && PickupAt(p, hasSpot, spot)) found = p;
            });
            if (found != null || !hasSpot) return found;
            // A copy the save registry does not have: it was inactive when its scene loaded (SaveSceneManager registers
            // active objects only), or a second copy of the prefab replaced it there (AddSaveObject keeps one per GUID).
            // Active scene objects only, so never a prefab asset.
            foreach (PickupBase p in active ?? ActivePickups())
                if (p != null && !p.IsCollected && p.MoonGuid == id && PickupAt(p, true, spot)
                    && (scene == MoonGuid.Empty || SceneOf(p) == scene)) return p;
            return null;
        }

        public static PickupBase[] ActivePickups() { return UnityEngine.Object.FindObjectsOfType<PickupBase>(); }

        // A partner collected this pickup (reward already applied by ProgressSync; Collected actions are not replayed).
        // false = that copy is not live here and its scene has no checkpoint record of it.
        // active != null: a later retry, which only looks for the live copy among these.
        public static bool MarkCollected(MoonGuid id, MoonGuid scene, bool hasSpot, Vector2 spot, PickupBase[] active = null)
        {
            PickupBase live = FindPickup(id, scene, hasSpot, spot, active);
            if (live != null)
            {
                live.IsCollected = true; live.gameObject.SetActive(false);
                MoonGuid liveScene = SceneOf(live);
                if (!Excluded(live) && ReferenceEquals(Find(liveScene, id), live))
                {
                    byte[] bytes = CaptureBytes(live);
                    StoreCheckpoint(liveScene, id, bytes);
                    RememberLocal(liveScene, id, bytes, Meaning.Pickup, typeof(PickupBase).FullName);
                }
                return true;
            }
            if (active != null) return false;
            // Its scene is not loaded here: edit that scene's checkpoint record, if it has one in the PickupBase layout
            // (float m_currentTime + bool IsCollected, PickupBase.cs:134; no subclass overrides it).
            foreach (SaveScene saved in Game.Checkpoint.SaveGameData.Scenes.Values)
            {
                if (scene != MoonGuid.Empty && saved.SceneGUID != scene) continue;
                if (Find(saved.SceneGUID, id) != null) continue;   // loaded, but not the copy at that spot: not this record
                for (int i = 0; i < saved.SaveObjects.Count; i++)
                {
                    SaveObject save = saved.SaveObjects[i];
                    if (save.Id != id || save.Data.MemoryStream.Length != 5) continue;
                    byte[] bytes = save.Data.MemoryStream.ToArray(); bytes[4] = 1;
                    StoreCheckpoint(saved.SceneGUID, id, bytes);
                    RememberLocal(saved.SceneGUID, id, bytes, Meaning.Pickup, typeof(PickupBase).FullName); return true;
                }
            }
            return false;
        }

        public static bool MarkPedestal(MoonGuid id)
        {
            bool marked = false;
            Visit(delegate(MoonGuid scene, SaveSceneManager manager) {
                GetAbilityPedestal pedestal = manager.IdToSaveSerialize(id) as GetAbilityPedestal;
                if (pedestal == null || Excluded(pedestal)) return;
                // Use its ReadMode path so ChangeState also closes its in-range hint.
                byte[] completed = BitConverter.GetBytes((int)GetAbilityPedestal.States.Completed);
                Archive ar = ReadArchive(completed);
                try { pedestal.Serialize(ar); }
                finally { ar.MemoryStream.Close(); }
                StoreCheckpoint(scene, id, completed);
                RememberLocal(scene, id, completed, Meaning.Pedestal, typeof(GetAbilityPedestal).FullName); marked = true;
            });
            if (marked) return true;
            foreach (SaveScene scene in Game.Checkpoint.SaveGameData.Scenes.Values)
                foreach (SaveObject saved in scene.SaveObjects)
                    if (saved.Id == id && saved.Data.MemoryStream.Length == 4)
                    {
                        byte[] bytes = BitConverter.GetBytes((int)GetAbilityPedestal.States.Completed);
                        StoreCheckpoint(scene.SceneGUID, id, bytes);
                        RememberLocal(scene.SceneGUID, id, bytes, Meaning.Pedestal, typeof(GetAbilityPedestal).FullName);
                        return true;
                    }
            Plugin.Log.LogWarning("net: unloaded pedestal state unavailable for " + id);
            return false;
        }

        public static void RequestResync(string reason) { TryRequestResync(reason); }

        // false = not sent (this PC is the host, is not ready, or asked less than a second ago).
        internal static bool TryRequestResync(string reason)
        {
            if (!NetPlay.Connected || NetPlay.IsHost || !SaveSync.Ready) return false;
            float now = Time.realtimeSinceStartup;
            if (now < s_requestAt) return false;
            s_requestAt = now + 1f; s_awaitSnapshot = true; s_delta.BeginSnapshot();
            Plugin.Log.LogInfo("net: request full resync: " + reason);
            NetPlay.Send(MsgResync, true, delegate(BinaryWriter w) { w.Write(reason); });
            return true;
        }
        public static int SnapshotsEnded;   // guest: full snapshots received to their end marker
        // Test only (NetTestHarness, T-214): incoming world blobs wait as they do while this PC is not in gameplay (a
        // checkpoint load); they are applied on the first Tick after it is cleared.
        internal static bool TestDeferApply;

        public static ulong HashBytes(byte[] bytes)
        {
            ulong hash = Offset;
            unchecked { foreach (byte b in bytes) { hash ^= b; hash *= Prime; } }
            return hash;
        }

        // T-172: a change held back by MirrorHold is not shared yet; the digest compares what the partners know of it
        // (the running PC's value), or a guest would call a held mirror change a mismatch and request a full resync.
        // Held objects are story script state (Raw schema), whose digest hash equals the canonical hash in s_known.
        static ulong SharedHash(string key, Blob blob)
        {
            ulong known;
            if (s_mirrorHold.IsHeld(key) && blob.Schema == Meaning.Raw && s_known.TryGetValue(key, out known)) return known;
            return DigestHash(blob);
        }

        // Hash used by the world digest and manifest (not by live change detection, which keeps blob.Hash).
        // Respawner's m_needsToRespawn (decomp Respawner.cs:188) follows the host-owned enemy (EnemySync), so the
        // guest's copy differs after every kill; only its presence is compared, or each kill re-triggers a full resync.
        // CarryableRigidBody (the nightberry, decomp CarryableRigidBody.cs Serialize) = bool carried, localPosition,
        // RespawnPosition. Once dropped both games simulate it, so localPosition never agrees bit for bit; compare the
        // carried flag and respawn point only (like Meaning.Body). Its bytes still sync live.
        static ulong DigestHash(Blob blob)
        {
            if (blob.Schema == Meaning.Respawner) return 0UL;
            if (blob.Type == "CarryableRigidBody" && blob.Bytes != null && blob.Bytes.Length == 25)
            {
                byte[] kept = new byte[13]; kept[0] = blob.Bytes[0]; Buffer.BlockCopy(blob.Bytes, 13, kept, 1, 12);
                return HashBytes(kept);
            }
            return blob.Hash;
        }

        public static ulong Digest(out int count)
        {
            Capture(false);
            List<string> keys = new List<string>(s_blobs.Keys); keys.Sort(StringComparer.Ordinal);
            MemoryStream stream = new MemoryStream(); BinaryWriter writer = new BinaryWriter(stream);
            count = 0;
            foreach (string key in keys)
            {
                Blob blob = s_blobs[key];
                if (!DigestRepair.Includes(blob.Type)) continue;
                writer.Write(blob.Scene.ToByteArray()); writer.Write(blob.Id.ToByteArray());
                writer.Write((byte)blob.Schema); writer.Write(SharedHash(key, blob)); count++;
            }
            writer.Flush(); ulong hash = HashBytes(stream.ToArray());
            writer.Close(); stream.Close(); return hash;
        }

        public static void SendDigest()
        {
            if (!NetPlay.Connected || !SaveSync.Ready || !ProgressSync.Ready || !NetPlay.LocalInGameplay() || s_awaitSnapshot) return;
            int count; ulong hash = Digest(out count), progress = ProgressSync.Hash();
            uint sequence = ++s_digestSequence;
            NetPlay.Send(MsgDigest, true, delegate(BinaryWriter w) { w.Write(sequence); w.Write(count); w.Write(hash); w.Write(progress); });
            s_digestBytes += 25;
            SendManifest(sequence);
        }

        static void ReceiveDigest(BinaryReader reader)
        {
            uint sequence = reader.ReadUInt32();
            int count = reader.ReadInt32(); ulong hash = reader.ReadUInt64(), progress = reader.ReadUInt64();
            if (!SaveSync.Ready || !ProgressSync.Ready || !NetPlay.LocalInGameplay() || s_awaitSnapshot || VersionRefused()) return;
            int sender = NetPlay.Sender;
            if (!NetPlay.IsHost && sender != NetRoute.HostSeat) return;
            int localCount; ulong localHash = Digest(out localCount), localProgress = ProgressSync.Hash();
            s_digestSender = sender; s_receivedDigest = sequence;
            s_remoteManifest.Clear(); CopyManifest(s_compareManifest);
            LastRemoteCount = count; LastLocalCount = localCount; LastRemoteHash = hash; LastLocalHash = localHash;
            s_digestProgressDiffers = progress != localProgress;
            LastDigestMatch = count == localCount && hash == localHash && !s_digestProgressDiffers;
            LastDigestAt = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo(LastDigestMatch ? "net: world digest match" : "net: world digest MISMATCH "
                + localCount + " objects (remote=" + count + ", world=" + localHash.ToString("X16") + "/"
                + hash.ToString("X16") + ", progress=" + localProgress.ToString("X16") + "/" + progress.ToString("X16") + ")");
        }

        static void ResetDigestRepair()
        {
            s_receivedDigest = 0; s_digestRepair.Clear();
            s_digestResyncAt = 0; s_digestBackoff = DigestBackoffMin;
        }

        static void CheckDigestRepair(Dictionary<string, DigestDifference> differences)
        {
            if (NetPlay.IsHost || s_digestSender != NetRoute.HostSeat || s_awaitSnapshot) return;
            DigestRepair.Decision decision = s_digestRepair.Observe(differences, s_digestProgressDiffers);
            if (LastDigestMatch) { s_digestResyncAt = 0; s_digestBackoff = DigestBackoffMin; }
            if (decision == DigestRepair.Decision.Wait)
            {
                DigestMismatchesIgnored++;
                Plugin.Log.LogInfo("net: digest mismatch once; resync only if the next round still differs");
            }
            if (decision != DigestRepair.Decision.Request) return;
            float now = Time.realtimeSinceStartup;
            if (now >= s_digestResyncAt && TryRequestResync("digest mismatch"))
            {
                s_digestRepair.SnapshotRequested(differences); DigestResyncs++;
                s_digestResyncAt = now + s_digestBackoff; s_digestBackoff = Mathf.Min(DigestBackoffMax, s_digestBackoff * 2f);
            }
            else
            {
                DigestResyncsDeferred++;
                Plugin.Log.LogInfo("net: digest mismatch resync deferred (backoff)");
            }
        }

        static void CopyManifest(Dictionary<string, DigestObject> target)
        {
            target.Clear();
            foreach (KeyValuePair<string, Blob> pair in s_blobs)
            {
                Blob blob = pair.Value;
                if (DigestRepair.Includes(blob.Type)) target.Add(pair.Key, new DigestObject(blob.Type, SharedHash(pair.Key, blob)));
            }
        }

        static void SendManifest(uint sequence)
        {
            List<string> keys = new List<string>(s_blobs.Keys); keys.Sort(StringComparer.Ordinal);
            MemoryStream batch = new MemoryStream(); BinaryWriter writer = new BinaryWriter(batch); int count = 0;
            foreach (string key in keys)
            {
                Blob blob = s_blobs[key];
                if (!DigestRepair.Includes(blob.Type)) continue;
                if (batch.Length + 48 + Encoding.UTF8.GetByteCount(blob.Type) > MaxMessage - 8)
                { FlushManifest(sequence, false, batch, writer, count); count = 0; }
                writer.Write(blob.Scene.ToByteArray()); writer.Write(blob.Id.ToByteArray());
                writer.Write(SharedHash(key, blob)); writer.Write(blob.Type); count++;
            }
            FlushManifest(sequence, true, batch, writer, count); writer.Close(); batch.Close();
        }

        static void FlushManifest(uint sequence, bool final, MemoryStream stream, BinaryWriter writer, int count)
        {
            writer.Flush(); byte[] bytes = stream.ToArray();
            NetPlay.Send(MsgManifest, true, delegate(BinaryWriter w) {
                w.Write(sequence); w.Write(final); w.Write((ushort)count); w.Write(bytes);
            });
            s_digestBytes += bytes.Length + 8; stream.SetLength(0); stream.Position = 0;
        }

        static void ReceiveManifest(BinaryReader reader)
        {
            uint sequence = reader.ReadUInt32(); bool final = reader.ReadBoolean(); int count = reader.ReadUInt16();
            if (sequence == 0 || sequence != s_receivedDigest || NetPlay.Sender != s_digestSender) return;
            for (int i = 0; i < count; i++)
            {
                MoonGuid scene = new MoonGuid(reader), id = new MoonGuid(reader);
                ulong hash = reader.ReadUInt64(); string type = reader.ReadString();
                if (type.Length > 256) throw new InvalidDataException("invalid digest type label");
                if (DigestRepair.Includes(type)) s_remoteManifest[Key(scene, id)] = new DigestObject(type, hash);
            }
            if (!final) return;
            s_receivedDigest = 0;
            var differences = DigestRepair.Differences(s_compareManifest, s_remoteManifest);
            List<string> keys = new List<string>(differences.Keys); keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < Math.Min(5, keys.Count); i++)
            {
                string key = keys[i]; DigestDifference diff = differences[key];
                Plugin.Log.LogInfo("net: world digest diff scene/object=" + key + " type=" + (diff.Local.Type ?? diff.Remote.Type)
                    + " local=" + (diff.Local.Type != null ? diff.Local.Hash.ToString("X16") : "missing")
                    + " remote=" + (diff.Remote.Type != null ? diff.Remote.Hash.ToString("X16") : "missing"));
            }
            LastDigestMatch = differences.Count == 0 && !s_digestProgressDiffers;
            if (differences.Count != 0) Plugin.Log.LogInfo("net: world digest manifest " + differences.Count + " differing GUIDs (first 5 shown)");
            else if (s_digestProgressDiffers) Plugin.Log.LogInfo("net: world digest manifest world GUIDs match; progress differs");
            CheckDigestRepair(differences);
        }

        [HarmonyPatch(typeof(RestoreCheckpointController), "RestoreCheckpoint")]
        static class RestorePatch
        {
            static void Postfix()
            {
                if (!NetPlay.Connected || !SaveSync.Ready) return;
                s_known.Clear();
                ResetDigestRepair();
                if (!NetPlay.IsHost) s_restorePending = true;
                else
                {
                    s_fullPending = true;
                    // Known unloaded objects must follow the host's checkpoint rollback as well.
                    foreach (Blob blob in s_blobs.Values)
                    {
                        if (Find(blob.Scene, blob.Id) != null) continue;
                        SaveScene scene = Game.Checkpoint.SaveGameData.GetScene(blob.Scene);
                        if (scene == null) continue;
                        foreach (SaveObject saved in scene.SaveObjects)
                            if (saved.Id == blob.Id)
                            { blob.Bytes = saved.Data.MemoryStream.ToArray(); blob.Hash = CanonicalHash(blob.Schema, blob.Bytes); break; }
                    }
                }
            }
        }
    }
}
