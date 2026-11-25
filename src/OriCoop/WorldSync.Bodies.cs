using System;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using OriCoop.Authority;
using UnityEngine;

namespace OriCoop
{
    public static partial class WorldSync
    {
        // A scene can be loaded by a guest alone. The arbiter keeps the last owner's state even
        // with nobody in the scene, and includes it in the next joining device's snapshot.
        const string BodyGenerationKey = AuthorityClient.BodyStatePrefix + "checkpoint";
        const float BodyInterval = 0.10f, BodyReach = 6f;
        static readonly Dictionary<string, BodyView> s_bodies = new Dictionary<string, BodyView>();
        static readonly Dictionary<RigidbodySerialize, BodyView> s_bodyInstances = new Dictionary<RigidbodySerialize, BodyView>();
        static readonly List<BodyView> s_oldBodies = new List<BodyView>();
        static readonly Dictionary<string, BodyId> s_bodyIds = new Dictionary<string, BodyId>();
        static bool s_indexedBodyIds;
        static readonly Dictionary<string, byte[]> s_restoredBodies = new Dictionary<string, byte[]>();
        static StateUpdate s_generationMarker;
        static IAuthority s_bodySource;
        static uint s_bodyGeneration;
        static bool s_bodyRestorePending;
        static float s_nextBodyTick, s_nextGenerationRequest;

        internal static string BodyKey(MoonGuid scene, MoonGuid id)
        {
            return AuthorityClient.BodyStatePrefix + Key(scene, id);
        }

        class BodyId
        {
            public MoonGuid Scene, Id;
        }

        sealed class BodyView : BodyId
        {
            public string Key;
            public RigidbodySerialize Save;
            public Rigidbody Body;
            public Collider Collider;
            public float RequestAt;
            public bool Requested, Publishing, ForceApply;
            public uint OwnerEpoch;
            public StateUpdate Cached;
            public BodyPose Last;
            public bool HasLast;
            public uint LoadedGeneration;
        }

        // The native serializer has a different layout for each combination of its five flags.
        // The network state always includes all three axes, even for an object whose save omits them.
        struct BodyPose
        {
            const byte Position = 1, Rotation = 2, Kinematic = 4, Velocity = 8, Angular = 16, IsKinematic = 32, Asleep = 64;
            public byte Flags;
            public Vector3 P, V, W;
            public Quaternion Q;
            bool KinematicBody { get { return (Flags & IsKinematic) != 0; } }
            bool Sleeping { get { return (Flags & Asleep) != 0; } }

            public static BodyPose Capture(BodyView view)
            {
                Rigidbody b = view.Body;
                RigidbodySerialize save = view.Save;
                BodyPose p = new BodyPose();
                p.Flags = (byte)((save.SerializePosition ? Position : 0) | (save.SerializeRotation ? Rotation : 0)
                    | (save.SeriailzeIsKinematic ? Kinematic : 0) | (save.SerializeVelocity ? Velocity : 0)
                    | (save.SerializeAngularVelocity ? Angular : 0) | (b.isKinematic ? IsKinematic : 0)
                    | (!b.isKinematic && b.IsSleeping() ? Asleep : 0));
                p.P = b.position; p.Q = b.rotation;
                if (!b.isKinematic) { p.V = b.velocity; p.W = b.angularVelocity; }
                return p;
            }

            public bool Valid()
            {
                return (Flags & 128) == 0 && LeaseMath.Finite(P) && LeaseMath.Finite(Q)
                    && Mathf.Abs(Quaternion.Dot(Q, Q) - 1f) < 0.02f && LeaseMath.Finite(V) && LeaseMath.Finite(W);
            }

            public bool Differs(BodyPose other)
            {
                return Flags != other.Flags || (P - other.P).sqrMagnitude > 0.0025f
                    || Quaternion.Angle(Q, other.Q) > 2f || (V - other.V).sqrMagnitude > 0.0225f
                    || (W - other.W).sqrMagnitude > 0.0225f;
            }

            public void Apply(Rigidbody b)
            {
                // Preserve the authoritative native kinematic mode. A replica can still collide
                // with its local player until a nearer player acquires the lease.
                if (b.isKinematic != KinematicBody) b.isKinematic = KinematicBody;
                b.position = P; b.rotation = Q;
                if (!KinematicBody)
                {
                    b.velocity = V; b.angularVelocity = W;
                    if (Sleeping) b.Sleep();
                }
            }

            static void Write(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
            static void Write(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
            static Vector3 Vector(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

            public byte[] Encode(uint generation)
            {
                using (MemoryStream bytes = new MemoryStream(57))
                using (BinaryWriter w = new BinaryWriter(bytes))
                {
                    w.Write(generation); w.Write(Flags); Write(w, P); Write(w, Q); Write(w, V); Write(w, W);
                    return bytes.ToArray();
                }
            }

            public static bool TryRead(StateUpdate state, uint generation, out BodyPose p)
            {
                p = new BodyPose();
                if (state == null || state.Data == null || state.Data.Length != 57 || BitConverter.ToUInt32(state.Data, 0) != generation) return false;
                using (BinaryReader r = new BinaryReader(new MemoryStream(state.Data)))
                {
                    r.ReadUInt32(); p.Flags = r.ReadByte(); p.P = Vector(r);
                    p.Q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    p.V = Vector(r); p.W = Vector(r);
                }
                return p.Valid();
            }

            public byte[] NativeArchive()
            {
                using (MemoryStream bytes = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(bytes))
                {
                    if ((Flags & Position) != 0) Write(w, P);
                    if ((Flags & Rotation) != 0) Write(w, Q);
                    if ((Flags & Kinematic) != 0) w.Write(KinematicBody);
                    if (!KinematicBody && (Flags & Velocity) != 0) Write(w, V);
                    if (!KinematicBody && (Flags & Angular) != 0) Write(w, W);
                    return bytes.ToArray();
                }
            }

            public bool ReadNative(byte[] data)
            {
                // Validate the complete layout before changing any Unity state. Only the fields the
                // game elected to save roll back. Omitted fields retain their native values.
                int poseBytes = ((Flags & Position) != 0 ? 12 : 0) + ((Flags & Rotation) != 0 ? 16 : 0);
                bool writeKind = (Flags & Kinematic) != 0;
                if (data.Length < poseBytes + (writeKind ? 1 : 0)) return false;
                bool kinematic = writeKind ? data[poseBytes] != 0 : KinematicBody;
                int motionBytes = ((Flags & Velocity) != 0 ? 12 : 0) + ((Flags & Angular) != 0 ? 12 : 0);
                if (data.Length != poseBytes + (writeKind ? 1 : 0) + (kinematic ? 0 : motionBytes)) return false;
                using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
                {
                    if ((Flags & Position) != 0) P = Vector(r);
                    if ((Flags & Rotation) != 0) Q = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    if (writeKind) { r.ReadBoolean(); Flags = (byte)(kinematic ? Flags | IsKinematic : Flags & ~IsKinematic); }
                    if (!kinematic && (Flags & Velocity) != 0) V = Vector(r);
                    if (!kinematic && (Flags & Angular) != 0) W = Vector(r);
                    if (kinematic) { V = W = Vector3.zero; }
                    Flags = (byte)(Flags & ~Asleep);
                }
                return Valid();
            }
        }

        static void RegisterBody(MoonGuid scene, MoonGuid id, RigidbodySerialize save)
        {
            if (save == null || !save.isActiveAndEnabled || s_bodyInstances.ContainsKey(save)) return;
            Rigidbody body = save.GetComponent<Rigidbody>();
            if (body == null) return;
            string key = BodyKey(scene, id);
            BodyView old;
            if (s_bodies.TryGetValue(key, out old)) s_bodyInstances.Remove(old.Save);
            BodyView view = new BodyView { Key = key, Scene = scene, Id = id, Save = save, Body = body, Collider = save.GetComponent<Collider>(), ForceApply = true };
            s_bodies[key] = view; s_bodyInstances.Add(save, view);
            s_bodyIds[key] = new BodyId { Scene = scene, Id = id };
        }

        static void ResetBodies()
        {
            if (s_bodySource != null) s_bodySource.LeaseChanged -= BodyLeaseChanged;
            s_bodySource = null;
            s_bodies.Clear(); s_bodyInstances.Clear(); s_oldBodies.Clear();
            s_restoredBodies.Clear(); s_generationMarker = null;
            s_bodyGeneration = 0; s_bodyRestorePending = false; s_nextBodyTick = s_nextGenerationRequest = 0f;
        }

        static void RestoreBodies()
        {
            if (s_mutateUnsyncedBodies || !NetPlay.Connected || !SaveSync.Ready || s_awaitSnapshot || VersionRefused()) return;
            // A guest can restore just its own game. In that case the exact same owner version
            // must be applied again, even when this device owns it. Only the room host rolls back time.
            if (NetPlay.IsHost) s_bodyRestorePending = true;
            foreach (BodyView view in s_bodies.Values) { view.ForceApply = true; view.Publishing = false; }
        }

        static bool BodyClock(IAuthority source, float now)
        {
            StateUpdate marker = source.LatestState(BodyGenerationKey);
            uint generation = marker != null && marker.Data != null && marker.Data.Length >= 4 ? BitConverter.ToUInt32(marker.Data, 0) : 0;
            if (s_bodyRestorePending && NetPlay.IsHost)
            {
                if (!source.Owns(BodyGenerationKey))
                {
                    if (now >= s_nextGenerationRequest)
                    {
                        source.RequestLease(BodyGenerationKey, 0f, true); s_nextGenerationRequest = now + 0.25f;
                    }
                    if (!source.Owns(BodyGenerationKey)) return false;
                }
                generation = Math.Max(generation, s_bodyGeneration) + 1;
                source.PublishState(BodyGenerationKey, RestoreMarker(source, generation), false);
                marker = source.LatestState(BodyGenerationKey);
                s_bodyRestorePending = false;
            }
            if (marker != s_generationMarker)
            {
                s_generationMarker = marker;
                s_restoredBodies.Clear();
                if (marker != null && marker.Data != null && marker.Data.Length >= 4)
                    ReadRestoreMarker(marker.Data);
            }
            if (generation != s_bodyGeneration)
            {
                s_bodyGeneration = generation;
                foreach (BodyView view in s_bodies.Values)
                {
                    view.Cached = null; view.HasLast = false; view.Publishing = false; view.ForceApply = true;
                }
            }
            return true;
        }

        static float DistanceTo(BodyView view, Vector3 point)
        {
            Collider c = view.Collider;
            return c != null && c.enabled ? Vector3.Distance(c.bounds.ClosestPoint(point), point) : Vector3.Distance(view.Body.position, point);
        }

        static float LocalBodyDistance(BodyView view)
        {
            SeinCharacter local = P2Context.P1 != null ? P2Context.P1 : Characters.Sein;
            float distance = local != null ? DistanceTo(view, local.Position) : float.MaxValue;
            foreach (Puppet p in Players.Remotes)
                if (p.IsLocal && p.Sein != null) distance = Mathf.Min(distance, DistanceTo(view, p.Sein.Position));
            return distance;
        }

        static void TickBodies()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextBodyTick) return;
            s_nextBodyTick = now + BodyInterval;
            IAuthority source = AuthoritySource.Current;
            if (source != s_bodySource)
            {
                if (s_bodySource != null) s_bodySource.LeaseChanged -= BodyLeaseChanged;
                s_bodySource = source;
                source.LeaseChanged += BodyLeaseChanged;
            }
            if (source.Mode == AuthorityMode.None || !BodyClock(source, now)) return;
            foreach (BodyView view in s_bodies.Values)
            {
                if (view.Save == null || view.Body == null || !view.Save.isActiveAndEnabled)
                {
                    if (source.Owns(view.Key) || view.Requested) source.ReleaseLease(view.Key);
                    s_oldBodies.Add(view);
                }
                else if (!view.Save.IsSuspended) TickBody(view, source, now);
            }
            foreach (BodyView view in s_oldBodies) { s_bodyInstances.Remove(view.Save); s_bodies.Remove(view.Key); }
            s_oldBodies.Clear();
        }

        static void BodyLeaseChanged(LeaseChange change)
        {
            BodyView view;
            if (change.Owner != s_bodySource.LocalSeat || !s_bodies.TryGetValue(change.Key, out view)
                || view.Body == null || view.Save == null || !view.Save.isActiveAndEnabled || view.Save.IsSuspended
                || !SaveSync.Ready || s_awaitSnapshot || s_bodyRestorePending || VersionRefused()) return;
            // Grant() has already made Owns true. Complete the handoff synchronously here:
            // waiting for the next sampling tick would erase collisions that happened as owner.
            StateUpdate seed = s_bodySource.LatestState(change.Key);
            BodyPose pose;
            if (BodyPose.TryRead(seed, s_bodyGeneration, out pose))
            {
                pose.Apply(view.Body); view.Last = pose; view.HasLast = true; view.Cached = seed;
            }
            view.OwnerEpoch = change.Epoch; view.Publishing = true; view.ForceApply = false;
        }

        static void TickBody(BodyView view, IAuthority source, float now)
        {
            string key = view.Key;
            int owner = source.OwnerOf(key);
            float distance = LocalBodyDistance(view);
            Vector3 ownerPosition;
            bool near = distance < BodyReach;
            bool claim = owner == 0 || (near && (!NetPlay.RemotePos(owner, out ownerPosition) || distance + 0.5f < DistanceTo(view, ownerPosition)));
            if (!source.Owns(key) && claim && now >= view.RequestAt)
            {
                source.RequestLease(key, distance, near); view.Requested = true; view.RequestAt = now + 0.35f;
            }
            else if (!claim && view.Requested && !source.Owns(key))
            {
                source.ReleaseLease(key); view.Requested = false;
            }

            StateUpdate state = source.LatestState(key);
            if (view.Cached != state || view.ForceApply)
            {
                view.Cached = state;
                view.HasLast = BodyPose.TryRead(state, s_bodyGeneration, out view.Last);
            }
            bool owns = source.Owns(key);
            uint epoch = source.EpochOf(key);
            bool take = owns && (!view.Publishing || epoch != view.OwnerEpoch);
            BodyPose current = BodyPose.Capture(view);
            if (!current.Valid()) return;
            if (view.LoadedGeneration != s_bodyGeneration)
            {
                // A room marker can precede this guest's native checkpoint transfer. A guest who
                // owned this body must never publish its old position under the new generation.
                byte[] checkpoint;
                if (!NetPlay.IsHost && !view.HasLast && s_restoredBodies.TryGetValue(key, out checkpoint))
                {
                    BodyPose restored = current;
                    if (restored.ReadNative(checkpoint)) { restored.Apply(view.Body); current = restored; }
                }
                view.LoadedGeneration = s_bodyGeneration;
            }
            if (view.HasLast && (take || view.ForceApply || (!owns && view.Last.Differs(current))))
            {
                view.Last.Apply(view.Body); current = view.Last;
            }
            if (owns && (take || state == null || state.Epoch != epoch || !view.HasLast || view.Last.Differs(current)))
            {
                source.PublishState(key, current.Encode(s_bodyGeneration), near);
                view.Cached = source.LatestState(key); view.HasLast = true; view.Last = current;
            }
            view.OwnerEpoch = epoch; view.Publishing = owns; view.ForceApply = false;
        }

        // Store unloaded bodies only when the game actually accepts a checkpoint. A native rollback
        // discards work since then, including states that the arbiter still has for unloaded scenes.
        [HarmonyPatch(typeof(GameController), "CreateCheckpoint")]
        static class SaveBodyCheckpoint
        {
            static void Prefix()
            {
                if (s_mutateUnsyncedBodies || !NetPlay.Connected || !SaveSync.Ready || s_awaitSnapshot || VersionRefused()
                    || Game.Checkpoint.SaveGameData == null) return;
                IAuthority source = AuthoritySource.Current;
                if (source.Mode == AuthorityMode.None || !BodyClock(source, Time.realtimeSinceStartup)) return;
                foreach (StateUpdate state in source.CachedStates)
                {
                    if (!state.Key.StartsWith(AuthorityClient.BodyStatePrefix, StringComparison.Ordinal)) continue;
                    BodyPose pose;
                    if (!BodyPose.TryRead(state, s_bodyGeneration, out pose)) continue;
                    BodyView view;
                    if (s_bodies.TryGetValue(state.Key, out view) && view.Body != null && view.Save != null && view.Save.isActiveAndEnabled)
                    {
                        if (!source.Owns(view.Key)) pose.Apply(view.Body);
                    }
                    else
                    {
                        BodyId id = KnownBody(state.Key);
                        if (id != null) StoreSceneProgress(id.Scene, id.Id, pose.NativeArchive());
                    }
                }
            }
        }

        // An authority key is not evidence of an object's native type. Verify never-loaded bodies
        // against the game's shipped scene/object GUID index before writing any checkpoint record.
        static BodyId KnownBody(string key)
        {
            BodyId known;
            if (s_bodyIds.TryGetValue(key, out known)) return known;
            if (s_indexedBodyIds || Core.Scenes.Manager == null) return null;
            s_indexedBodyIds = true;
            string path = Path.Combine(Application.dataPath, "guids.txt");
            try
            {
                if (!File.Exists(path)) return null;
                using (StreamReader reader = File.OpenText(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.IndexOf(" (RigidbodySerialize)\t", StringComparison.Ordinal) < 0) continue;
                        int slash = line.IndexOf('\\'); string[] fields = line.Split('\t');
                        int a, b, c, d;
                        if (slash <= 0 || fields.Length != 5 || !int.TryParse(fields[1], out a) || !int.TryParse(fields[2], out b)
                            || !int.TryParse(fields[3], out c) || !int.TryParse(fields[4], out d)) continue;
                        RuntimeSceneMetaData scene = Core.Scenes.Manager.GetSceneInformation(line.Substring(0, slash));
                        if (scene == null) continue;
                        BodyId item = new BodyId { Scene = scene.SceneMoonGuid, Id = new MoonGuid(a, b, c, d) };
                        s_bodyIds[BodyKey(item.Scene, item.Id)] = item;
                    }
                }
            }
            catch (IOException) { } // Known live registrations still work on distributions without the index.
            catch (UnauthorizedAccessException) { }
            return s_bodyIds.TryGetValue(key, out known) ? known : null;
        }

        // The host's native archived fields travel with the rare room rollback. A peer may hold
        // the lease for a scene the host has unloaded or still be waiting for its own reload.
        static byte[] RestoreMarker(IAuthority source, uint generation)
        {
            using (MemoryStream bytes = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(bytes))
            {
                w.Write(generation);
                foreach (StateUpdate state in source.CachedStates)
                {
                    if (state.Key == BodyGenerationKey || !state.Key.StartsWith(AuthorityClient.BodyStatePrefix, StringComparison.Ordinal)) continue;
                    BodyId id = KnownBody(state.Key);
                    if (id == null) continue;
                    SaveScene scene = Game.Checkpoint.SaveGameData.GetScene(id.Scene);
                    if (scene == null) continue;
                    SaveObject saved = scene.SaveObjects.Find(value => value.Id == id.Id);
                    if (saved.Data == null) continue;
                    byte[] data = saved.Data.MemoryStream.ToArray();
                    if (data.Length > 53 || bytes.Length + 33 + data.Length > ushort.MaxValue) continue;
                    w.Write(id.Scene.ToByteArray()); w.Write(id.Id.ToByteArray()); w.Write((byte)data.Length); w.Write(data);
                }
                return bytes.ToArray();
            }
        }

        static void ReadRestoreMarker(byte[] data)
        {
            using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
            {
                r.ReadUInt32();
                while (r.BaseStream.Length - r.BaseStream.Position >= 33)
                {
                    MoonGuid scene = new MoonGuid(r), id = new MoonGuid(r);
                    int length = r.ReadByte();
                    if (length > 53 || length > r.BaseStream.Length - r.BaseStream.Position) return;
                    s_restoredBodies[BodyKey(scene, id)] = r.ReadBytes(length);
                }
            }
        }
    }
}
