using System;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;
using OriCoop.Authority;

namespace OriCoop
{
    // Per-player collision and one leased simulation of each pushable stone.
    public static class PushBlockSync
    {
        const float SendInterval = 0.05f, SnapDistance = 3f, MaxExtrapolate = 0.25f;

        public sealed class St
        {
            public int Id;
            public string Name;
            public int Epoch;
            public string Key;
            internal DynamicLease Lease;
            internal StoneLeaseSimulation Simulation = new StoneLeaseSimulation();
            public int OwnerSeat;
            public float InteractionUntil = -10f, GrabbedAt = -10f;
            // T-049 two-pusher speed-up: our push direction (-1/0/+1) and the partner's latest report.
            public int LocalPushDir, SentPushDir;
            public readonly int[] SeatPushDir = new int[5];                     // T-114: each local Ori's own push
            public readonly float[] SeatPushAt = { -10f, -10f, -10f, -10f, -10f };
            public float LocalPushAt = -10f, PushSentAt = -10f, BaseMultiplier = -1f;
            // T-115: one report per remote seat (3-4 players), not a single slot every sender overwrites.
            internal readonly Dictionary<int, CoPushReport> Reports = new Dictionary<int, CoPushReport>();
            internal byte SentSeatDirs;
            public bool HasRemote;
            public uint StateEpoch, StateVersion;
            public uint SimEpoch;   // T-197: the lease epoch this PC last simulated the stone in as its owner (kept over session resets)
            public Vector3 RemotePos, RemoteVel;
            public float RemoteRotZ, RemoteAngularZ, RemoteAt;
            public float SentAt = -10f;
            public bool WasLocalOwner, Seeded;
            internal readonly List<PendingHit> Hits = new List<PendingHit>();
        }

        internal sealed class PendingHit { public Damage Damage; public float Until; }

        // Per block instance: which Oris it ignores right now (pusher, basher) and who the bash timer belongs to.
        sealed class Inst
        {
            public St St;
            public readonly StoneSeed Seed = new StoneSeed();
            public readonly List<PlatformMovement> Ignored = new List<PlatformMovement>();
            public PlatformMovement Basher;
            public float PuppetIgnoreAt;
            public Vector3 Origin;
            public bool HasOrigin;
        }

        static readonly Dictionary<int, St> s_byId = new Dictionary<int, St>();
        static readonly Dictionary<PushPullBlock, Inst> s_inst = new Dictionary<PushPullBlock, Inst>();
        static readonly Dictionary<PlatformMovement, List<PushPullBlock>> s_ignoring = new Dictionary<PlatformMovement, List<PushPullBlock>>();
        static readonly Dictionary<Component, int> s_readyFrame = new Dictionary<Component, int>();
        static readonly HashSet<SceneRoot> s_unloading = new HashSet<SceneRoot>();
        static readonly System.Reflection.FieldInfo s_fIgnore = AccessTools.Field(typeof(PushPullBlock), "m_ignoreCollisionRemainingTime");
        static readonly System.Reflection.FieldInfo s_fColliders = AccessTools.Field(typeof(PushPullBlock), "m_colliders");
        static readonly System.Reflection.FieldInfo s_fBody = AccessTools.Field(typeof(PushPullBlock), "m_rigidbody");
        static readonly System.Reflection.FieldInfo s_fOrigin = AccessTools.Field(typeof(PushPullBlock), "m_originalPosition");
        static readonly System.Reflection.FieldInfo s_fGrabbed = AccessTools.Field(typeof(PushPullBlock), "m_isGrabbed");
        static readonly System.Reflection.FieldInfo s_fPushable = AccessTools.Field(typeof(SeinGrabBlock), "m_pushable");
        static readonly System.Reflection.FieldInfo s_fDistance = AccessTools.Field(typeof(SeinGrabBlock), "m_distanceToBlock");
        static readonly System.Reflection.MethodInfo s_mRelease = AccessTools.Method(typeof(SeinGrabBlock), "ReleaseBlock");
        static readonly System.Reflection.MethodInfo s_mGrab = AccessTools.Method(typeof(SeinGrabBlock), "GrabBlock");
        static readonly System.Reflection.MethodInfo s_mSetInRange = AccessTools.PropertySetter(typeof(SeinGrabBlock), "InRange");
        // T-197 mutations (the PE / PF / PG checks of net phase P must fail): the second grabber is dropped as before;
        // a copy follows / is seeded from a state of an older lease epoch; a suspended Ori keeps its grab.
        internal static readonly bool s_mutateDropHelper = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_DROP_HELPER") == "1";
        static readonly bool s_mutateExpireOwnerPush = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_EXPIRE_OWNER_PUSH") == "1";
        internal static readonly bool s_mutateStaleState = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_STALE_STATE") == "1";
        internal static readonly bool s_mutateKeepDownedGrab = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_KEEP_DOWNED_GRAB") == "1";
        static float GetIgnoreTime(PushPullBlock b) { return (float)s_fIgnore.GetValue(b); }
        static void SetIgnoreTime(PushPullBlock b, float t) { s_fIgnore.SetValue(b, t); }
        static Collider[] s_colliders(PushPullBlock b) { return (Collider[])s_fColliders.GetValue(b); }
        static Rigidbody s_body(PushPullBlock b) { return (Rigidbody)s_fBody.GetValue(b); }
        static Vector3 s_origin(PushPullBlock b) { return (Vector3)s_fOrigin.GetValue(b); }
        static bool s_registered;
        static int s_layerDefault = -1, s_layerIgnore = -1;

        // counters for tests / logs
        public static int StatesSent, StatesApplied, PuppetCallsIgnored, Denied, PushOuts, PuppetPushOuts, Snaps, SuspendedReleases;
        public static bool Enabled = true;

        static void Log(string msg)
        {
            Plugin.Log.LogInfo("[phys " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.time.ToString("F2") + "] " + msg);
        }
        static string V(Vector3 v) { return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + ")"; }

        public static bool Online { get { return NetPlay.Enabled && NetPlay.Connected && P2Context.P1 != null; } }

        internal static void EnsureRegistered()
        {
            if (s_registered) return;
            s_registered = true;
            ResetOwnership("connected");
            NetPlay.SessionStarted += delegate { ResetOwnership("connected"); };
            NetPlay.SessionEnded += delegate { ResetOwnership("disconnected", true); };
            NetPlay.On(MsgCoPush, OnCoPush);
            NetPlay.PlayerLeft += ForgetPushReports;
            Players.SeatsRenamed += delegate(int[] renamed) {
                for (int seat = 1; seat < renamed.Length; seat++)
                    if (renamed[seat] != 0) { ForgetPushReports(seat); ForgetPushReports(renamed[seat]); }
            };
            // T-115: guests only talk to the P2P host: it must relay a guest's push to the other guests.
            if (!s_mutateNoForward) NetPlay.Forward(MsgCoPush);
            s_layerDefault = LayerMask.NameToLayer("Default");
            s_layerIgnore = LayerMask.NameToLayer("pushPullBlock");
        }

        // ---------------------------------------------------------------- identity / ownership
        static Inst InstOf(PushPullBlock b)
        {
            Inst i;
            if (s_inst.TryGetValue(b, out i) && i.St != null) return i;
            if (i == null)
            {
                if (s_inst.Count > 64) Prune();
                i = new Inst(); s_inst[b] = i;
            }
            if (!i.HasOrigin) { i.Origin = s_origin(b); i.HasOrigin = true; }
            string scene = b.gameObject.scene.name ?? "";
            // Same on every PC: the block's own save GUID (its RigidbodySerialize / any SaveSerialize on it). Saves move
            // blocks, so the start position is only the fallback.
            GuidOwner g = b.GetComponent<RigidbodySerialize>();
            if (g == null) g = b.GetComponent<SaveSerialize>();
            string key = g != null && g.IsGuidInitialized() ? "guid|" + g.MoonGuid.ToString()
                : scene + "|" + Mathf.RoundToInt(i.Origin.x * 10f) + "," + Mathf.RoundToInt(i.Origin.y * 10f) + "|" + b.name;
            int id = StableHash(key);
            St st;
            if (!s_byId.TryGetValue(id, out st)) { st = new St { Id = id, Key = "w:push:" + id, Name = scene + "/" + b.name }; s_byId[id] = st; }
            i.St = st;
            st.Seeded = false;
            try
            {
                string desc = "";
                Collider[] cols = s_colliders(b);
                if (cols != null) foreach (Collider c in cols) if (c != null)
                    desc += " [" + c.GetType().Name + " " + c.name + " layer=" + LayerMask.LayerToName(c.gameObject.layer) + (c.isTrigger ? " trigger" : "")
                        + (c.enabled ? "" : " off") + " min=" + V(c.bounds.min) + " max=" + V(c.bounds.max) + "]";
                Rigidbody rb = s_body(b);
                Log("block " + st.Name + " id=" + id + " key=" + key + " pos=" + V(b.Position) + " kinematic=" + (rb != null && rb.isKinematic) + desc);
            }
            catch (Exception) { }
            return i;
        }

        static void Prune()
        {
            List<PushPullBlock> dead = new List<PushPullBlock>();
            foreach (PushPullBlock k in s_inst.Keys) if (k == null) dead.Add(k);
            foreach (PushPullBlock k in dead) s_inst.Remove(k);
            List<PlatformMovement> gone = new List<PlatformMovement>();
            foreach (KeyValuePair<PlatformMovement, List<PushPullBlock>> kv in s_ignoring)
            {
                kv.Value.RemoveAll(x => x == null);
                if (kv.Key == null) gone.Add(kv.Key);
            }
            foreach (PlatformMovement k in gone) ForgetGrip(k);
            List<Component> expired = new List<Component>();
            foreach (Component component in s_readyFrame.Keys) if (component == null) expired.Add(component);
            foreach (Component component in expired) s_readyFrame.Remove(component);
            s_unloading.RemoveWhere(delegate(SceneRoot root) { return root == null; });
        }

        static int StableHash(string s)
        {
            unchecked { int h = (int)2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h; }
        }

        static bool LocalOwns(St st) { return !Online || DynamicLease.For(st.Key).Owns; }
        static bool Simulates(St st)
        {
            if (!Online) return true;
            DynamicLease lease = DynamicLease.For(st.Key);
            float now = Time.realtimeSinceStartup;
            bool pushing = now < st.InteractionUntil;
            // T-197: no takeover prediction while the owner itself pushes. The arbiter keeps an active owner, so the
            // prediction only let this PC's copy run ahead for 2 RTT and jump back when the lease was refused; the Ori
            // lost the stone in that jump. This Ori holds the stone the owner moves (Allow) until the owner stops.
            // The last accepted state describes the owner until it reports a stop or the lease changes.
            if (pushing && !s_mutateDropHelper && lease.Owner != 0 && !lease.Owns && lease.RemoteActive
                && lease.StateEpoch == lease.Epoch && (!s_mutateExpireOwnerPush || now - lease.ReceivedAt < 0.3f)) pushing = false;
            return st.Simulation.Allows(lease.Owner, lease.Epoch, AuthoritySource.Current.LocalSeat,
                now, NetPlay.RttMs, pushing);
        }

        public static St StateOf(PushPullBlock b) { return b == null ? null : InstOf(b).St; }
        public static bool IsLocalOwner(PushPullBlock b) { return b != null && LocalOwns(InstOf(b).St) && Simulates(InstOf(b).St); }
        public static bool Ignores(PushPullBlock b, PlatformMovement pm) { Inst i; return b != null && s_inst.TryGetValue(b, out i) && i.Ignored.Contains(pm); }

        static void SendState(PushPullBlock b, St st)
        {
            Rigidbody rb = s_body(b);
            if (rb == null || Time.realtimeSinceStartup - st.SentAt < SendInterval) return;
            st.SentAt = Time.realtimeSinceStartup;
            Vector3 p = b.Position, v = rb.isKinematic ? Vector3.zero : rb.velocity;
            float rotation = b.transform.eulerAngles.z, angular = rb.isKinematic ? 0f : rb.angularVelocity.z;
            if (!LeaseMath.Finite(p.x) || !LeaseMath.Finite(p.y) || !LeaseMath.Finite(rotation)
                || !LeaseMath.Finite(v.x) || !LeaseMath.Finite(v.y) || !LeaseMath.Finite(angular)) return;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(p.x); w.Write(p.y); w.Write(rotation); w.Write(v.x); w.Write(v.y); w.Write(angular);
                byte[] data = stream.ToArray();
                st.Lease.Publish(data); StatesSent++;
            }
        }

        static bool Standing(PushPullBlock b)
        {
            PlatformMovement pm = LocalPm;
            if (pm == null || !pm.IsOnGround) return false;
            CapsuleCollider cap = pm.GetComponent<CapsuleCollider>();
            if (cap == null) return false;
            float surface = SurfaceY(b, cap.bounds.center.x);
            return !float.IsNaN(surface) && Mathf.Abs(cap.bounds.min.y - surface) < 0.25f;
        }

        static void LeaseStep(PushPullBlock b, St st)
        {
            st.Lease = DynamicLease.For(st.Key);
            bool pushing = Time.realtimeSinceStartup < st.InteractionUntil;
            float distance = LocalPm == null ? float.MaxValue : Vector3.Distance(LocalPm.Position, b.Position);
            if (!LeaseMath.Finite(distance)) distance = float.MaxValue;
            // Riders follow an owned stone; only a push keeps its owner's lease active.
            bool standing = StoneLeaseSimulation.WantsStandingLease(pushing, st.Lease.Owner) && Standing(b);
            st.Lease.Tick(distance, pushing || standing, pushing, false);
            st.Lease.Active = pushing;
            st.OwnerSeat = st.Lease.Owner; st.Epoch = (int)st.Lease.Epoch;
            st.Hits.RemoveAll(hit => hit.Until < Time.realtimeSinceStartup);
            if (st.Lease.Data == null) return;
            if (st.HasRemote && st.StateEpoch == st.Lease.StateEpoch && st.StateVersion == st.Lease.StateVersion) return;
            Pose pose;
            if (!Decode(st.Lease.Data, b.Position.z, out pose)) return;
            st.RemotePos = pose.Position; st.RemoteRotZ = pose.RotZ; st.RemoteVel = pose.Velocity; st.RemoteAngularZ = pose.AngularZ;
            st.HasRemote = true; st.RemoteAt = st.Lease.ReceivedAt;
            st.StateEpoch = st.Lease.StateEpoch; st.StateVersion = st.Lease.StateVersion; StatesApplied++;
        }

        struct Pose { public Vector3 Position, Velocity; public float RotZ, AngularZ; }

        // A published stone state (SendState's bytes); false when it is short or not finite.
        static bool Decode(byte[] data, float z, out Pose pose)
        {
            pose = new Pose();
            if (data == null || data.Length < 24) return false;
            using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
            {
                pose.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), z); pose.RotZ = r.ReadSingle();
                pose.Velocity = new Vector3(r.ReadSingle(), r.ReadSingle(), 0f);
                pose.AngularZ = r.ReadSingle();
            }
            return LeaseMath.Finite(pose.Position) && LeaseMath.Finite(pose.RotZ) && LeaseMath.Finite(pose.Velocity) && LeaseMath.Finite(pose.AngularZ);
        }

        // T-197: where a copy that starts to simulate (scene loaded, session reset) is put: the newest state anyone
        // published, this PC's own included. Before, only the partner's last state counted, so the PC that pushed the
        // stone last put it back where the partner had left it earlier (playtest 4: both PCs saw the stone go from
        // -246 to -279 when the guest rejoined, and the two PCs disagreed by 12 units after walking back in).
        static bool SeedPose(Inst instance, Rigidbody body, out Pose pose)
        {
            St st = instance.St;
            pose = new Pose();
            if (s_mutateStaleState)
            {
                pose.Position = st.RemotePos; pose.RotZ = st.RemoteRotZ; pose.Velocity = st.RemoteVel; pose.AngularZ = st.RemoteAngularZ;
                return st.HasRemote;
            }
            Vector3 live = body.position;
            StateUpdate latest = instance.Seed.ForReseed(AuthoritySource.Current, st.Key, live.x, live.y, body.rotation.eulerAngles.z);
            return latest != null && Decode(latest.Data, live.z, out pose);
        }

        // ---------------------------------------------------------------- per-pair collision
        static PlatformMovement LocalPm { get { SeinCharacter s = P2Context.Current != null && P2Context.Current.IsLocal ? P2Context.Current.Sein : P2Context.P1; return s == null ? null : s.PlatformBehaviour.PlatformMovement; } }

        static bool IsPuppet(PlatformMovement pm)
        {
            if (!NetPlay.Enabled || pm == null) return false;
            foreach (Puppet p in Players.Remotes) if (!p.IsLocal && p.Sein != null && p.Sein.PlatformBehaviour.PlatformMovement == pm) return true;
            return false;
        }

        static bool Ready(PushPullBlock block)
        {
            return SceneReady(block) && ValidBody(s_body(block));
        }

        static bool SceneReady(Component component)
        {
            if (component == null) return false;
            if (!component.gameObject.activeInHierarchy) { s_readyFrame.Remove(component); return false; }
            SceneRoot root = SceneRoot.FindFromTransform(component.transform);
            if (root != null)
            {
                if (s_unloading.Contains(root) || Core.Scenes.Manager == null)
                { s_readyFrame.Remove(component); return false; }
                bool ready = false;
                foreach (SceneManagerScene scene in Core.Scenes.Manager.ActiveScenes)
                    if (scene != null && scene.SceneRoot == root)
                    { ready = scene.CurrentState == SceneManagerScene.State.Loaded && scene.HasStartBeenCalled; break; }
                if (!ready) { s_readyFrame.Remove(component); return false; }
            }
            int frame;
            if (!s_readyFrame.TryGetValue(component, out frame))
            { s_readyFrame[component] = Time.frameCount; return false; }
            return Time.frameCount > frame;
        }

        static bool ValidRotation(Quaternion q)
        {
            float length = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return LeaseMath.Finite(q) && LeaseMath.Finite(length) && length > 0.000001f;
        }

        static bool ValidBody(Rigidbody body)
        {
            if (!SceneReady(body) || !LeaseMath.Finite(body.position) || !ValidRotation(body.rotation)) return false;
            return body.isKinematic || (LeaseMath.Finite(body.velocity) && LeaseMath.Finite(body.angularVelocity));
        }

        static void SetIgnore(PushPullBlock b, PlatformMovement pm, bool ignore)
        {
            if (b == null || pm == null || !Ready(b)) return;
            // An Ori that is switched off (a downed partner's hidden copy) can still be taken off the list (T-197).
            if (ignore && !pm.gameObject.activeInHierarchy) return;
            Inst i = InstOf(b);
            bool has = i.Ignored.Contains(pm);
            if (has == ignore) return;
            Collider[] cols = s_colliders(b);
            Collider cap = pm.GetComponent<CapsuleCollider>();
            // Online puppets never collide with stones (KeepPuppetsOut), so only the bookkeeping changes for them.
            if (cols != null && cap != null && !(Online && IsPuppet(pm)))
                foreach (Collider c in cols) if (c != null && c.enabled && c.gameObject.activeInHierarchy && cap.gameObject.activeInHierarchy) Physics.IgnoreCollision(c, cap, ignore);
            List<PushPullBlock> list;
            if (!s_ignoring.TryGetValue(pm, out list)) { list = new List<PushPullBlock>(); s_ignoring[pm] = list; }
            if (ignore) { i.Ignored.Add(pm); list.Add(b); }
            else { i.Ignored.Remove(pm); list.Remove(b); }
            SetGripMaterial(cols, i.Ignored.Count > 0 ? b.PushPullMaterialMoving : b.PushPullMaterial);
        }

        static void SetGripMaterial(Collider[] cols, PhysicMaterial material)
        {
            if (cols == null) return;
            foreach (Collider c in cols)
                if (c != null && c.enabled) c.sharedMaterial = material;
        }

        static readonly bool s_mutateKeepDepartedGrip = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STONE_KEEP_DEPARTED_GRIP") == "1";

        // The disappearing collider needs no collision pair restored. Other live holders still need moving friction.
        internal static void ForgetGrip(PlatformMovement pm)
        {
            List<PushPullBlock> held;
            if (object.ReferenceEquals(pm, null) || !s_ignoring.TryGetValue(pm, out held)) return;
            if (!s_mutateKeepDepartedGrip)
                foreach (PushPullBlock block in held)
                {
                    Inst instance;
                    if (block == null || !s_inst.TryGetValue(block, out instance) || !instance.Ignored.Remove(pm)) continue;
                    if (object.ReferenceEquals(instance.Basher, pm)) { instance.Basher = null; SetIgnoreTime(block, 0f); }
                    SetGripMaterial(s_colliders(block), instance.Ignored.Count > 0 ? block.PushPullMaterialMoving : block.PushPullMaterial);
                }
            s_ignoring.Remove(pm);
        }

        // The game's own version: the whole block on the pushPullBlock layer (for every Ori). Ours: per Ori.
        [HarmonyPatch(typeof(PushPullBlock), "IgnoreCollisionWithPlayer")]
        static class PerPairIgnore
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement, bool ignore)
            {
                if (!Enabled || !Ready(__instance)) return true;
                try { SetIgnore(__instance, platformMovement, ignore); }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] ignore failed: " + e.Message); return true; }
                return false;
            }
        }

        // While an Ori that ignores a block runs its own movement step, its sweeps / rays must not see the block.
        [HarmonyPatch(typeof(PlatformMovementRigidbodyMoonCharacterControllerPenetrate), "FixedUpdate")]
        static class MovementWindow
        {
            static void Prefix(PlatformMovementRigidbodyMoonCharacterControllerPenetrate __instance, out List<PushPullBlock> __state)
            {
                __state = null;
                List<PushPullBlock> list;
                if (!Enabled || s_layerIgnore < 0 || !s_ignoring.TryGetValue(__instance, out list) || list.Count == 0) return;
                __state = list;
                foreach (PushPullBlock b in list) SetLayer(b, s_layerIgnore);
            }
            static void Postfix(List<PushPullBlock> __state)
            {
                if (__state == null) return;
                foreach (PushPullBlock b in __state) SetLayer(b, s_layerDefault);
            }
        }

        static void SetLayer(PushPullBlock b, int layer)
        {
            if (b == null || !Ready(b)) return;
            Collider[] cols = s_colliders(b);
            if (cols == null) return;
            foreach (Collider c in cols) if (c != null && c.enabled && c.gameObject.activeInHierarchy) c.gameObject.layer = layer;
        }

        // ---------------------------------------------------------------- grab / push / release / bash / hit
        // Returns false when the call must not touch this block (a puppet online, or a block someone else is holding).
        static bool Allow(PushPullBlock b, PlatformMovement pm, string what)
        {
            if (!Enabled || !Online || !Ready(b)) return true;
            Inst i = InstOf(b);
            if (IsPuppet(pm) || Players.RemoteContext)
            {
                PuppetCallsIgnored++;
                if (what != "push") Log("puppet " + what + " " + i.St.Name + " ignored here (its owner moves it)");
                return false;
            }
            if (pm != LocalPm) return true;
            St st = i.St;
            // T-023: the game calls this every frame while an Ori grabs, also when it only holds the stone. The grab itself
            // (its first 0.5 s) and real pushing / pulling count as interaction (keep the lease and ask for it); a holder
            // standing still longer no longer locks the stone, so the other player can take it over and push.
            float now = Time.realtimeSinceStartup;
            if (what == "grab") st.GrabbedAt = now;
            if (Moving(pm) || now - st.GrabbedAt < 0.5f) st.InteractionUntil = now + 0.15f;
            Simulates(st);
            LeaseStep(b, st);
            Rigidbody rb = s_body(b);
            if (Simulates(st))
            {
                st.Seeded = true;
                if (rb != null && rb.isKinematic) { rb.isKinematic = false; if (HandedOver(st) && LeaseMath.Finite(st.RemoteVel)) rb.velocity = st.RemoteVel; }
                return true;
            }
            Denied++;
            // T-197: the owner is pushing, so this Ori's grab does not move the stone (the owner's simulation does, faster
            // when both push the same way: CoPush). It still holds the stone: keep ignoring it, as the game does for a
            // grabbing Ori, so the copy that follows the owner cannot shove it out of its hold. Before, the hold was
            // dropped here and the player fell out of the push state (playtest 4).
            SetIgnore(b, pm, !s_mutateDropHelper && (what == "grab" || what == "push"));
            return false;
        }

        // The last remote state is the one the lease came from (the previous epoch, or this one while predicting): its
        // velocity carries over when we start to simulate. An older state's velocity is not the stone's any more (T-197).
        static bool HandedOver(St st)
        {
            return st.HasRemote && (s_mutateStaleState || st.StateEpoch + 1 >= DynamicLease.For(st.Key).Epoch);
        }

        // Is this Ori holding this stone (game state: SeinGrabBlock)?
        static bool HeldBy(SeinCharacter s, PushPullBlock b)
        {
            if (s == null || s.Abilities == null || s.Abilities.GrabBlock == null || !s.Abilities.GrabBlock.IsGrabbing) return false;
            return ReferenceEquals(s_fPushable.GetValue(s.Abilities.GrabBlock), b);
        }
        static bool HeldByLocal(PushPullBlock b)
        {
            if (HeldBy(P2Context.P1, b)) return true;
            foreach (Puppet p in Players.Remotes) if (p.IsLocal && HeldBy(p.Sein, b)) return true;
            return false;
        }

        // T-197: the game's own "which stone is in front of me" ray (SeinGrabBlock.DetectPushPullBlock), with two changes
        // for co-op. 1) Another Ori (or its light spirit) between this one and the stone does not hide the stone: two
        // players at the same face stand inside each other, and the game's ray stopped at the partner's capsule, so
        // the grab ended as soon as the two drifted apart. 2) An Ori that already holds a stone someone else moves (a
        // helper, or any puppet) may be a little further away than the game's 0.9: its copy of the stone lags the
        // owner's by the network delay.
        const float HoldSlack = 0.8f;
        static readonly List<Transform> s_oriRoots = new List<Transform>();
        static bool s_grabRayFailed;

        static bool OriCollider(Collider c)
        {
            foreach (Transform root in s_oriRoots) if (root != null && c.transform.IsChildOf(root)) return true;
            // Every Ori's light spirit (the game's class Ori) floats around it and has a collider of its own; the
            // partner's spirit drifting in front of the stone face ended a grab the same way as the partner itself.
            return c.GetComponentInParent<global::Ori>() != null;
        }

        static IPushable Detect(SeinGrabBlock grab) { return Detect(grab, false); }

        static IPushable Detect(SeinGrabBlock grab, bool holdingReach)
        {
            s_fDistance.SetValue(grab, 0f);
            PlatformMovement pm = grab.PlatformMovement;
            float x = pm.transform.localScale.x;
            bool left = grab.FaceLeft;
            Ray ray = new Ray(pm.HeadPosition, pm.LocalToWorld(left ? -Vector2.right : Vector2.right));
            bool near = false;
            for (int n = 0; n < PushPullBlock.All.Count; n++)
                if (Vector3.Distance(PushPullBlock.All[n].Position, ray.origin) < 10f) near = true;
            if (!near) return null;
            float reach = x * grab.ReachDistance;
            PushPullBlock held = grab.IsGrabbing ? s_fPushable.GetValue(grab) as PushPullBlock : null;
            if (holdingReach || (Online && held != null && Ready(held) && (Players.RemoteContext || IsPuppet(pm) || !Simulates(InstOf(held).St)))) reach += HoldSlack;
            s_oriRoots.Clear();
            if (P2Context.P1 != null) s_oriRoots.Add(P2Context.P1.transform);
            foreach (Puppet p in Players.Remotes) if (p.Sein != null) s_oriRoots.Add(p.Sein.transform);
            if (P2Context.P2 != null) s_oriRoots.Add(P2Context.P2.transform);
            RaycastHit best = new RaycastHit(); bool found = false;
            foreach (RaycastHit hit in Physics.RaycastAll(ray, reach))
            {
                if (hit.collider == null || OriCollider(hit.collider)) continue;
                if (!found || hit.distance < best.distance) { best = hit; found = true; }
            }
            if (!found || !best.collider.attachedRigidbody
                || Vector3.Dot(left ? Vector3.right : Vector3.left, best.normal) <= Mathf.Cos(Mathf.PI / 4f)) return null;
            PushPullBlock block = best.collider.attachedRigidbody.GetComponent<PushPullBlock>();
            if (!block) return null;
            s_fDistance.SetValue(grab, best.distance);
            return block;
        }

        [HarmonyPatch(typeof(SeinGrabBlock), "DetectPushPullBlock")]
        static class GrabRay
        {
            static bool Prefix(SeinGrabBlock __instance, ref IPushable __result)
            {
                if (!Enabled || s_mutateDropHelper || s_grabRayFailed || __instance == null || __instance.Sein == null) return true;
                try { __result = Detect(__instance); return false; }
                catch (Exception e)
                {
                    s_grabRayFailed = true;   // the game's own ray from now on
                    Plugin.Log.LogWarning("[phys] grab ray: " + e);
                    return true;
                }
            }
        }

        // T-197: a puppet's grab is the game's own state, replayed from its owner's inputs, and the game starts a grab only
        // on a new press of the grab key. A puppet that lost its hold for one frame (a position correction, a late
        // landing) therefore stayed out of the push pose until its owner let go and grabbed again: "the other player
        // does not see me pushing". While its owner reports that it pushes this stone (CoPush, every 0.1 s), a puppet
        // that stands at the stone takes hold of it again.
        public static int PuppetRegrabs;

        [HarmonyPatch(typeof(SeinGrabBlock), "UpdateCharacterState")]
        static class PuppetRegrab
        {
            static void Postfix(SeinGrabBlock __instance)
            {
                if (!Enabled || !Online || __instance == null || __instance.Sein == null) return;
                try
                {
                    if (__instance.IsGrabbing)
                    {
                        // Report the native actor's completed state. A couch player's physics callback can
                        // happen outside its input swap, where the current context points to the primary.
                        SeinCharacter actor = LocalOri(__instance.PlatformMovement);
                        if (actor == null) return;
                        PushPullBlock block = s_fPushable.GetValue(__instance) as PushPullBlock;
                        if (block != null && Ready(block))
                            NoteOwnPush(InstOf(block).St, Players.SeatOf(actor), PushDir(__instance.PlatformMovement), Time.realtimeSinceStartup);
                    }
                    else if (!s_mutateDropHelper) Regrab(__instance);
                }
                catch (Exception e) { if (CoopTraceEvery("regrab", 5f)) Plugin.Log.LogWarning("[phys] regrab: " + e.Message); }
            }
        }

        static void Regrab(SeinGrabBlock grab)
        {
            SeinCharacter s = grab.Sein;
            PlatformMovement pm = grab.PlatformMovement;
            if (s.IsSuspended || pm == null || !pm.IsOnGround || !IsPuppet(pm)) return;
            // Only while its owner holds the grab key (Core.Input is the puppet's own input here): the game lets go on
            // the key's release edge, and a hold taken after that edge would never end.
            if (!Core.Input.Glide.IsPressed && !Core.Input.Grab.IsPressed) return;
            int seat = Players.SeatOf(s);
            float now = Time.realtimeSinceStartup;
            PushPullBlock pushed = null;
            foreach (KeyValuePair<PushPullBlock, Inst> pair in s_inst)
            {
                St st = pair.Value.St;
                if (pair.Key == null || st == null || RecentSeatPush(st, seat, now) == 0) continue;
                pushed = pair.Key; break;
            }
            if (pushed == null || !Ready(pushed) || !ReferenceEquals(Detect(grab, true), pushed)) return;
            s_fPushable.SetValue(grab, pushed);
            s_mSetInRange.Invoke(grab, new object[] { true });
            s_mGrab.Invoke(grab, null);
            PuppetRegrabs++;
            if (CoopTraceEvery("regrab" + seat, 1f)) Log("puppet P" + seat + " takes hold of " + InstOf(pushed).St.Name + " again (its owner reports pushing it)");
        }

        // T-197: SeinGrabBlock stops updating while its Ori is suspended (downed; in co-op the world keeps running), so
        // the stone stayed "grabbed": slippery moving material, no release friction. On a slope it slid away faster
        // and faster with nobody pushing (playtest 4: "it falls really fast"). The game itself lets go when a
        // checkpoint is restored; do the same when the holder is suspended and the stone is not.
        static void ReleaseSuspended(PushPullBlock b)
        {
            if (s_mutateKeepDownedGrab || b.IsSuspended) return;
            s_oris.Clear();
            if (P2Context.P1 != null) s_oris.Add(P2Context.P1);
            foreach (Puppet p in Players.Remotes) if (p.Sein != null) s_oris.Add(p.Sein);
            if (!NetPlay.Enabled && P2Context.P2 != null && !s_oris.Contains(P2Context.P2)) s_oris.Add(P2Context.P2);
            foreach (SeinCharacter s in s_oris)
            {
                // Suspended, switched off (a downed partner's copy is hidden) or downed: its grab state is frozen.
                if (s == null || !HeldBy(s, b) || !(s.IsSuspended || !s.gameObject.activeInHierarchy || Revive.IsDowned(s))) continue;
                s_mRelease.Invoke(s.Abilities.GrabBlock, null);
                SuspendedReleases++;
                Log("release " + InstOf(b).St.Name + ": its holder " + Who(s.PlatformBehaviour.PlatformMovement) + " is suspended");
                CoopTrace.Event("phys", "block.release.suspended", "id", InstOf(b).St.Id, "by", Who(s.PlatformBehaviour.PlatformMovement));
            }
        }

        // T-049: which way our Ori moves the stone: Push goes the way it faces, Pull the other way; 0 = only holding.
        public const byte MsgCoPush = 89;   // docs/net-msg-ids.md
        public static int CoPushSent, CoPushReceived, CoPushBoosted, CoPushHeard;
        static uint s_pushSequence;
        static readonly Puppet[] s_pushActors = new Puppet[5];
        static readonly uint[] s_pushBirths = new uint[5];
        static readonly uint?[,] s_releasedPushSeats = new uint?[5, 5];
        static readonly bool s_devicePose = TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_COPUSH_DEVICE_POSE") == "1";
        static readonly bool s_arrivalOrder = TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_MUTATE_COPUSH_ARRIVAL_ORDER") == "1";
        // T-115 mutation: the P2P host does not relay 89 / 80 (the 3P test must fail).
        internal static readonly bool s_mutateNoForward = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_FX_FORWARD") == "1";
        static readonly List<int> s_pushDirs = new List<int>();
        static readonly List<float> s_pushAges = new List<float>();
        // T-115 net test: a push report for a stone nobody has (id 0); receivers count it before the id lookup.
        internal static void TestSendCoPush() { NetPlay.Send(MsgCoPush, true, w => { w.Write(0); w.Write((sbyte)1); }); }
        internal static int PushDir(PlatformMovement pm)
        {
            SeinCharacter s = LocalOri(pm);
            if (s == null || s.Abilities.GrabBlock == null || !s.Abilities.GrabBlock.IsGrabbing) return 0;
            SeinGrabBlock.State state = s.Abilities.GrabBlock.CurrentState;
            int facing = s.FaceLeft ? -1 : 1;
            return state == SeinGrabBlock.State.Push ? facing : state == SeinGrabBlock.State.Pull ? -facing : 0;
        }

        // T-114: the Ori on this PC (P1 or an extra local player) that this movement component belongs to; null otherwise.
        static SeinCharacter LocalOri(PlatformMovement pm)
        {
            if (pm == null) return null;
            SeinCharacter p1 = P2Context.P1;
            if (p1 != null && p1.PlatformBehaviour.PlatformMovement == pm) return p1;
            foreach (Puppet p in Players.Remotes)
                if (p.IsLocal && p.Sein != null && p.Sein.PlatformBehaviour.PlatformMovement == pm) return p.Sein;
            return null;
        }

        // seat: whose call this is; this PC reports one direction for all its Oris on the stone (CoPushRule.DeviceDir).
        internal static void NoteOwnPush(St st, int seat, int dir, float now)
        {
            int device = Players.LocalSeat;
            bool newPusher = false;
            if (seat >= 1 && seat <= 4 && seat != device)
            {
                Puppet actor = null;
                foreach (Puppet p in Players.Remotes) if (p.IsLocal && p.Seat == seat) { actor = p; break; }
                if (!ReferenceEquals(actor, s_pushActors[seat]) || s_pushBirths[seat] == 0)
                {
                    s_pushActors[seat] = actor;
                    do { s_pushBirths[seat] = unchecked(++s_pushSequence); } while (s_pushBirths[seat] == 0);
                    newPusher = dir != 0;
                }
            }
            if (seat >= 1 && seat < st.SeatPushDir.Length) { st.SeatPushDir[seat] = dir; st.SeatPushAt[seat] = now; }
            dir = CoPushRule.DeviceDir(st.SeatPushDir, st.SeatPushAt, now, 0.15f);
            byte seats = CoPushReport.Pack(st.SeatPushDir, st.SeatPushAt, now, 0.15f);
            st.LocalPushDir = dir; st.LocalPushAt = now;
            if (seats == st.SentSeatDirs && (seats == 0 || !newPusher && now - st.PushSentAt < 0.1f)) return;
            st.SentSeatDirs = seats; st.SentPushDir = dir; st.PushSentAt = now;
            int id = st.Id; sbyte d = (sbyte)dir;
            uint sequence = unchecked(++s_pushSequence);
            Action<BinaryWriter> write = w => {
                w.Write(id); w.Write(d); w.Write(sequence); w.Write(seats);
                for (int actorSeat = 1; actorSeat <= 4; actorSeat++)
                    if (actorSeat != device && ((seats >> (2 * (actorSeat - 1))) & 3) != 0) w.Write(s_pushBirths[actorSeat]);
            };
            if (TestObserveReport != null) TestObserveReport(id, write);
            NetPlay.Send(MsgCoPush, false, write);
            CoPushSent++;
        }

        static void OnCoPush(BinaryReader r)
        {
            int id = r.ReadInt32(); int dir = r.ReadSByte(); CoPushHeard++;
            St st; if (!s_byId.TryGetValue(id, out st)) return;
            ApplyReport(st, NetPlay.Sender, dir, r, Time.realtimeSinceStartup);
        }

        static void ApplyReport(St st, int sender, int dir, BinaryReader tail, float now)
        {
            CoPushReport previous, incoming;
            st.Reports.TryGetValue(sender, out previous);
            if (!CoPushReport.TryRead(dir, tail, now, previous, s_arrivalOrder, out incoming, sender, s_releasedPushSeats)) return;
            if (incoming.Ordered)
                for (int seat = 1; seat <= 4; seat++)
                    if (seat != sender && NetPlay.DeviceSeat(seat) != sender) incoming.ForgetSeat(seat);
            st.Reports[sender] = incoming; CoPushReceived++;
        }

        static void ForgetPushReports(int seat)
        {
            foreach (St st in s_byId.Values)
            {
                st.Reports.Remove(seat);
                for (int sender = 1; sender <= 4; sender++)
                {
                    CoPushReport report;
                    if (!st.Reports.TryGetValue(sender, out report) || !report.Ordered) continue;
                    uint? floor = s_releasedPushSeats[sender, seat];
                    if (!floor.HasValue || CoPushReport.Newer(report.Sequence, floor.Value))
                        s_releasedPushSeats[sender, seat] = report.Sequence;
                    report.ForgetSeat(seat); st.Reports[sender] = report;
                }
            }
            for (int s = 1; s <= 4; s++) s_releasedPushSeats[seat, s] = null;
        }

        internal static Action<int, Action<BinaryWriter>> TestObserveReport;

        // The stone this specific Ori can re-grab while its grab input remains held.
        internal static int RecentSeatPush(St st, int seat, float now)
        {
            int device = NetPlay.DeviceSeat(seat);
            CoPushReport report;
            return st.Reports.TryGetValue(device, out report) ? report.Direction(seat, device, now, s_devicePose) : 0;
        }

        // Every step on every PC: both pushing the same way -> the game's own push/pull speed ratio x1.5 (Oris and stone).
        static void ApplyCoPush(PushPullBlock b, St st, bool online)
        {
            if (st.BaseMultiplier < 0f) st.BaseMultiplier = b.MoveSpeedMultiplier;
            float now = Time.realtimeSinceStartup;
            int local = online && now - st.LocalPushAt < 0.15f ? st.LocalPushDir : 0;
            float m = 1f;
            if (online)
            {
                s_pushDirs.Clear(); s_pushAges.Clear();
                foreach (CoPushReport report in st.Reports.Values)
                { s_pushDirs.Add(report.DeviceDir); s_pushAges.Add(now - report.At); }
                m = CoPushRule.Multiplier(local, s_pushDirs, s_pushAges);
            }
            float target = st.BaseMultiplier * m;
            if (b.MoveSpeedMultiplier != target)
            {
                if (m > 1f) { CoPushBoosted++; Log("both push " + st.Name + " " + (local > 0 ? "right" : "left") + ": speed x" + m); }
                b.MoveSpeedMultiplier = target;
            }
        }

        internal static bool Moving(PlatformMovement pm)
        {
            SeinCharacter s = LocalOri(pm);
            if (s == null || s.Abilities.GrabBlock == null) return true;
            SeinGrabBlock grab = s.Abilities.GrabBlock;
            return grab.IsGrabbing && (grab.CurrentState == SeinGrabBlock.State.Push || grab.CurrentState == SeinGrabBlock.State.Pull);
        }

        static readonly Dictionary<string, float> s_every = new Dictionary<string, float>();
        static bool CoopTraceEvery(string key, float seconds)
        {
            float t; float now = Time.realtimeSinceStartup;
            if (s_every.TryGetValue(key, out t) && now - t < seconds) return false;
            s_every[key] = now; return true;
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnGrabbed")]
        static class GrabPatch
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement)
            {
                if (!Ready(__instance)) return true;
                try
                {
                    bool puppet = Online && (IsPuppet(platformMovement) || Players.RemoteContext);
                    // A puppet still ignores the block it holds, so it can stand next to it without being stopped by it.
                    if (puppet && Enabled) { SetIgnore(__instance, platformMovement, true); PuppetCallsIgnored++; return false; }
                    if (!Allow(__instance, platformMovement, "grab")) return false;
                    Log("grab " + InstOf(__instance).St.Name + " by " + Who(platformMovement) + " pos=" + V(__instance.Position));
                    CoopTrace.Event("phys", "block.grab", "id", InstOf(__instance).St.Id, "by", Who(platformMovement), "pos", V(__instance.Position));
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] grab: " + e.Message); }
                return true;
            }
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnPushOrPull")]
        static class PushPatch
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement)
            {
                if (!Ready(__instance)) return true;
                try
                {
                    if (Online && Enabled && (IsPuppet(platformMovement) || Players.RemoteContext)) { SetIgnore(__instance, platformMovement, true); PuppetCallsIgnored++; return false; }
                    if (!Allow(__instance, platformMovement, "push")) return false;
                    return true;
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] push: " + e.Message); return true; }
            }
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnReleased")]
        static class ReleasePatch
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement)
            {
                if (!Ready(__instance)) return true;
                try
                {
                    if (Online && Enabled)
                    {
                        SeinCharacter actor = LocalOri(platformMovement);
                        if (actor != null) NoteOwnPush(InstOf(__instance).St, Players.SeatOf(actor), 0, Time.realtimeSinceStartup);
                    }
                    if (Online && Enabled && (IsPuppet(platformMovement) || Players.RemoteContext)) { SetIgnore(__instance, platformMovement, false); return false; }
                    if (Online && Enabled && platformMovement == LocalPm) InstOf(__instance).St.InteractionUntil = -10f;
                    if (Online && Enabled && platformMovement == LocalPm && !Simulates(InstOf(__instance).St))
                    {
                        // We were denied: just drop our own per-pair ignore, do not touch the owner's velocity.
                        SetIgnore(__instance, platformMovement, false);
                        s_fGrabbed.SetValue(__instance, false);
                        return false;
                    }
                    Log("release " + InstOf(__instance).St.Name + " by " + Who(platformMovement) + " pos=" + V(__instance.Position));
                    CoopTrace.Event("phys", "block.release", "id", InstOf(__instance).St.Id, "by", Who(platformMovement), "pos", V(__instance.Position));
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] release: " + e.Message); }
                return true;
            }
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnEnterBash")]
        static class BashPatch
        {
            static bool Prefix(PushPullBlock __instance)
            {
                if (!Enabled || !Ready(__instance)) return true;
                try
                {
                    // The game uses Characters.Sein: inside P2Context that is the bashing puppet / local P2.
                    SeinCharacter s = Characters.Sein;
                    PlatformMovement pm = s == null ? null : s.PlatformBehaviour.PlatformMovement;
                    Inst i = InstOf(__instance);
                    if (pm == null) return true;
                    if (Online && (IsPuppet(pm) || Players.RemoteContext)) { PuppetCallsIgnored++; }
                    else if (!Allow(__instance, pm, "bash")) return false;
                    i.Basher = pm;
                    SetIgnoreTime(__instance, 20f);
                    SetIgnore(__instance, pm, true);
                    return false;
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] bash: " + e.Message); return true; }
            }
        }

        static bool PlayerAttack(DamageType t)
        {
            return t == DamageType.Bash || t == DamageType.SpiritFlame || t == DamageType.SpiritFlameSplatter || t == DamageType.ChargeFlame
                || t == DamageType.Grenade || t == DamageType.GrenadeSplatter || t == DamageType.Stomp || t == DamageType.StompBlast
                || t == DamageType.Nova || t == DamageType.LevelUp;
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnRecieveDamage")]
        static class HitPatch
        {
            static bool Prefix(PushPullBlock __instance, Damage damage)
            {
                if (!Enabled || !Online || damage == null || !Ready(__instance)) return true;
                try
                {
                    Inst i = InstOf(__instance);
                    if (Players.RemoteContext) { PuppetCallsIgnored++; return false; }
                    if (!PlayerAttack(damage.Type)) return Simulates(i.St);
                    if (!Allow(__instance, LocalPm, "hit " + damage.Type)) return false;
                    if (Simulates(i.St)) return true;
                    i.St.Hits.Add(new PendingHit {
                        Damage = new Damage(damage.Amount, damage.Force, damage.Position, damage.Type, damage.Sender),
                        Until = Time.realtimeSinceStartup + 0.6f + NetPlay.RttMs * 0.002f });
                    return false;
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] hit: " + e.Message); return true; }
            }
        }

        // ---------------------------------------------------------------- per step
        [HarmonyPatch(typeof(PushPullBlock), "FixedUpdate")]
        static class StepPatch
        {
            static bool Prefix(PushPullBlock __instance, out float __state)
            {
                __state = 0f;
                if (!Enabled) return true;
                if (!Ready(__instance))
                {
                    Inst existing;
                    if (s_inst.TryGetValue(__instance, out existing) && existing.St != null && existing.St.Lease != null)
                        existing.St.Lease.Cancel();
                    __state = -1f; return true;
                }
                if (Online) { EnsureRegistered(); LeaseStep(__instance, InstOf(__instance).St); }
                float t = GetIgnoreTime(__instance);
                if (t > 0f && !__instance.IsSuspended)
                {
                    Inst i = InstOf(__instance);
                    t = Mathf.Max(0f, t - Time.deltaTime);
                    if (t == 0f) { if (i.Basher != null) SetIgnore(__instance, i.Basher, false); i.Basher = null; }
                    SetIgnoreTime(__instance, 0f); __state = t;
                }
                if (Online && !__instance.IsSuspended && !Simulates(InstOf(__instance).St))
                {
                    if (t > 0f) SetIgnoreTime(__instance, t);
                    try { Step(__instance); }
                    catch (Exception e) { if (CoopTraceEvery("steperr", 5f)) Plugin.Log.LogWarning("[phys] step: " + e); }
                    __state = -1f;
                    return false;
                }
                return true;
            }

            static void Postfix(PushPullBlock __instance, float __state)
            {
                if (!Enabled || __state < 0f) return;
                if (__state > 0f) SetIgnoreTime(__instance, __state);
                try { Step(__instance); }
                catch (Exception e) { if (CoopTraceEvery("steperr", 5f)) Plugin.Log.LogWarning("[phys] step: " + e); }
            }
        }

        static void Step(PushPullBlock b)
        {
            if (b == null || !Ready(b) || b.IsSuspended) return;
            Inst i = InstOf(b);
            St st = i.St;
            bool online = Online;
            ReleaseSuspended(b);
            ApplyCoPush(b, st, online);
            if (online)
            {
                EnsureRegistered();
                Rigidbody rb = s_body(b);
                if (Simulates(st))
                {
                    Pose seed;
                    if (!st.Seeded && rb != null && SeedPose(i, rb, out seed))
                    {
                        Quaternion rotation = Quaternion.Euler(0f, 0f, seed.RotZ);
                        if (LeaseMath.Finite(seed.Position) && LeaseMath.Finite(rotation))
                        {
                            rb.position = seed.Position; b.transform.position = seed.Position;
                            rb.rotation = rotation;
                        }
                    }
                    st.Seeded = true;
                    if (!st.WasLocalOwner)
                    {
                        st.WasLocalOwner = true;
                        if (rb != null && rb.isKinematic)
                        {
                            rb.isKinematic = false;
                            if (HandedOver(st) && LeaseMath.Finite(st.RemoteVel) && LeaseMath.Finite(st.RemoteAngularZ))
                            { rb.velocity = st.RemoteVel; rb.angularVelocity = new Vector3(0f, 0f, st.RemoteAngularZ); }
                        }
                        Log("simulating " + st.Name + " here (owner P" + st.OwnerSeat + " epoch " + st.Epoch + ")");
                    }
                    if (LocalOwns(st))
                    {
                        st.SimEpoch = st.Lease.Epoch;
                        while (st.Hits.Count > 0)
                        {
                            PendingHit hit = st.Hits[0]; st.Hits.RemoveAt(0);
                            if (hit.Until >= Time.realtimeSinceStartup) b.OnRecieveDamage(hit.Damage);
                        }
                        SendState(b, st);
                    }
                }
                else if (rb != null)
                {
                    // owns: the lease is ours, in the short wait before we simulate (StoneLeaseSimulation's readyAt). Our
                    // Ori's grab stays as it is (T-197: it was cleared here, at the moment the lease arrived).
                    bool owns = LocalOwns(st);
                    if (st.WasLocalOwner && (!owns || s_mutateStaleState))
                    {
                        // T-197: an own Ori that still holds the stone keeps ignoring it (see Allow).
                        if (s_mutateDropHelper || !HeldByLocal(b)) SetIgnore(b, LocalPm, false);
                        s_fGrabbed.SetValue(b, false);
                        st.WasLocalOwner = false; Log("following " + st.Name + " (owner P" + st.OwnerSeat + " epoch " + st.Epoch + ")");
                    }
                    rb.isKinematic = true;
                    // T-197: only a state of the lease as it is now moves the copy; with an older one the stone jumped
                    // to where the partner had left it earlier and back when the new owner's first state arrived.
                    if (st.HasRemote && (s_mutateStaleState || StoneLeaseSimulation.FollowsState(st.StateEpoch, st.Lease.Epoch, owns, st.SimEpoch)))
                    {
                        float age = FollowAge(Time.realtimeSinceStartup - st.RemoteAt, NetPlay.RttMs);
                        Vector3 target = st.RemotePos + st.RemoteVel * age;
                        target.z = rb.position.z;
                        bool snap = !st.Lease.Blending && (target - rb.position).magnitude > SnapDistance;
                        Vector3 position = snap ? target : Vector3.Lerp(rb.position, target, 0.5f);
                        Quaternion rotation = Quaternion.Euler(0f, 0f, Mathf.LerpAngle(b.transform.eulerAngles.z, st.RemoteRotZ, 0.5f));
                        if (LeaseMath.Finite(position) && LeaseMath.Finite(rotation))
                        {
                            if (snap)
                            {
                                Snaps++;
                                Log("snap " + st.Name + " " + V(rb.position) + " -> " + V(target));
                                CoopTrace.Event("phys", "block.snap", "id", st.Id, "from", V(rb.position), "to", V(target));
                                rb.position = target; b.transform.position = target;
                            }
                            else rb.MovePosition(position);
                            rb.MoveRotation(rotation);
                        }
                    }
                }
            }
            if (online) KeepPuppetsOut(b, i);
            PushOutOris(b, i);
            if (online) StandPuppets(b, i);
        }

        internal static float FollowAge(float age, float rttMs)
        {
            return Mathf.Min(MaxExtrapolate, Mathf.Max(0f, age) + Mathf.Max(0f, rttMs) * 0.0005f);
        }

        // A puppet's position is its owner's truth plus corrections written every step; letting it touch the stone in
        // the physics step made the stone stick / get dragged (the owner's game) and jitter. Puppets ignore stones in
        // the simulation; push-out keeps them on top visually, and StandPuppets tells them when they stand on one
        // (their own ground detection is the ignored sweep / contact: T-198).
        static void KeepPuppetsOut(PushPullBlock b, Inst i)
        {
            float now = Time.realtimeSinceStartup;
            if (now < i.PuppetIgnoreAt) return;
            i.PuppetIgnoreAt = now + 1f;   // IgnoreCollision is lost when a collider is re-enabled: renew every second
            Collider[] cols = s_colliders(b);
            if (cols == null) return;
            foreach (Puppet p in Players.Remotes)
            {
                if (p.IsLocal || p.Sein == null || !p.Sein.gameObject.activeInHierarchy) continue;
                Collider cap = p.Sein.PlatformBehaviour.PlatformMovement.GetComponent<CapsuleCollider>();
                if (cap == null || !cap.enabled) continue;
                foreach (Collider c in cols) if (c != null && c.enabled && c.gameObject.activeInHierarchy) Physics.IgnoreCollision(c, cap, true);
            }
        }

        // T-198: puppets ignore stones in the physics step (KeepPuppetsOut), and the game finds an Ori's ground with that
        // same sweep / contact. A partner standing on a stone was therefore never "on ground" on this PC: gravity kept
        // pulling it, the position correction kept lifting it back, and its state machine played the falling pose (on its
        // own PC it stood). Report the stone under a puppet's feet to its movement the way the game's own ground hit does
        // (moving platform + ground contact), once per step.
        internal static readonly bool s_mutatePuppetAir = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PUPPET_STONE_AIR") == "1";
        public static int PuppetStands;
        const float StandAbove = 0.15f, StandBelow = 0.3f, StandGroundAngle = 60f;

        static void StandPuppets(PushPullBlock b, Inst i)
        {
            if (s_mutatePuppetAir) return;
            foreach (Puppet p in Players.Remotes)
            {
                if (p.IsLocal || p.Sein == null || !p.Sein.gameObject.activeInHierarchy) continue;
                PlatformMovement pm = p.Sein.PlatformBehaviour.PlatformMovement;
                // An Ori that grabs or bashes this stone is beside it, not on it.
                if (pm == null || !pm.gameObject.activeInHierarchy || pm.IsSuspended || i.Ignored.Contains(pm)) continue;
                CapsuleCollider cap = pm.GetComponent<CapsuleCollider>();
                if (cap == null) continue;
                Bounds ob = cap.bounds;
                RaycastHit top;
                if (!Top(b, ob.center.x, out top) || top.collider == null) continue;
                float feet = ob.min.y - top.point.y;
                // Standing band: resting on the surface (the two PCs' copies of the stone differ a little). A puppet that
                // moves up (jumping off) is left alone; PlatformMovement.IsOnGround says no for it anyway.
                if (!LeaseMath.Finite(feet) || feet > StandAbove || feet < -StandBelow || pm.LocalSpeedY > 0.0001f) continue;
                Vector2 normal = pm.WorldToLocal(top.normal);
                if (!PlatformMovement.IsGround(normal, top.collider, StandGroundAngle)) continue;
                if (pm.MovingPlatforms != null) pm.MovingPlatforms.OnGroundMovingPlatform(top.collider.transform);
                pm.OnCollisionGround(normal, top.collider);
                PuppetStands++;
            }
        }

        // Net test (PF): what a partner joining or leaving does to this PC's stones.
        internal static void TestSessionReset() { ResetOwnership("test"); }

        static void ResetOwnership(string why, bool lastPeerLeft = false)
        {
            Prune();
            Array.Clear(s_releasedPushSeats, 0, s_releasedPushSeats.Length);
            if (lastPeerLeft && NetRoute.ServerMode && NetPlay.IsHost && NetPlay.Connected)
            {
                // With no puppet the native game moves this instance without the network FixedUpdate path.
                foreach (var pair in s_inst)
                {
                    if (pair.Key == null || !pair.Key.isActiveAndEnabled || pair.Value.St == null) continue;
                    Rigidbody body = s_body(pair.Key);
                    if (body == null) continue;
                    Vector3 live = body.position;
                    pair.Value.Seed.LastPeerLeft(AuthoritySource.Current, pair.Value.St.Key, live.x, live.y, body.rotation.eulerAngles.z);
                }
            }
            foreach (St st in s_byId.Values)
            {
                st.Epoch = st.OwnerSeat = 0; st.HasRemote = st.WasLocalOwner = st.Seeded = false;
                st.Reports.Clear(); st.SentSeatDirs = 0;
                st.Simulation = new StoneLeaseSimulation();
                st.InteractionUntil = st.SentAt = -10f;
                st.Hits.Clear();
            }
            Log("ownership reset (" + why + ")");
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnDisable")]
        static class UnloadLease
        {
            static void Postfix(PushPullBlock __instance)
            {
                Inst i;
                if (!s_inst.TryGetValue(__instance, out i)) return;
                i.Seed.Unloaded();
                if (Online && i.St != null) DynamicLease.For(i.St.Key).Cancel();
            }
        }

        internal static void StopScene(SceneRoot root)
        {
            foreach (KeyValuePair<PushPullBlock, Inst> pair in s_inst)
                if (pair.Key != null && pair.Value.St != null && pair.Value.St.Lease != null
                    && SceneRoot.FindFromTransform(pair.Key.transform) == root)
                    pair.Value.St.Lease.Cancel();
        }

        [HarmonyPatch]
        static class SceneUnload
        {
            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SceneRoot), "SaveAndUnload");
                yield return AccessTools.Method(typeof(SceneRoot), "Unload");
            }

            static void Prefix(SceneRoot __instance)
            {
                if (__instance != null && s_unloading.Add(__instance)) StopScene(__instance);
            }
        }

        // ---------------------------------------------------------------- push out of overlap
        static readonly List<SeinCharacter> s_oris = new List<SeinCharacter>();

        static Bounds BlockBounds(PushPullBlock b, out bool any)
        {
            any = false; Bounds bb = new Bounds();
            Collider[] cols = s_colliders(b);
            if (cols != null)
                foreach (Collider c in cols)
                {
                    if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                    if (!any) { bb = c.bounds; any = true; } else bb.Encapsulate(c.bounds);
                }
            return bb;
        }

        static bool Hit(PushPullBlock b, Vector3 from, Vector3 dir, float length, out RaycastHit best)
        {
            best = new RaycastHit(); bool found = false;
            Collider[] cols = s_colliders(b);
            if (cols == null) return false;
            foreach (Collider c in cols)
            {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
                RaycastHit h;
                if (c.Raycast(new Ray(from, dir), out h, length) && (!found || h.distance < best.distance)) { best = h; found = true; }
            }
            return found;
        }

        // Inside the block's real (convex) shape, not its bounding box: the stones are rounded MeshColliders, and an Ori
        // standing on the curved top is inside the box. A point is inside when rays from all four sides reach the
        // surface before reaching it.
        static bool InsideShape(PushPullBlock b, Bounds bb, Vector3 p)
        {
            if (!bb.Contains(new Vector3(p.x, p.y, bb.center.z))) return false;
            float far = bb.size.magnitude + 1f; RaycastHit h;
            p.z = bb.center.z;
            return Hit(b, p + Vector3.up * far, Vector3.down, far, out h) && Hit(b, p + Vector3.down * far, Vector3.up, far, out h)
                && Hit(b, p + Vector3.left * far, Vector3.right, far, out h) && Hit(b, p + Vector3.right * far, Vector3.left, far, out h);
        }

        // Is the Ori's capsule free of other solid colliders after moving by delta? (Ignores itself and this stone.)
        static bool Free(PushPullBlock b, SeinCharacter s, CapsuleCollider cap, Vector3 delta)
        {
            Bounds ob = cap.bounds;
            float r = Mathf.Max(0.05f, Mathf.Min(ob.extents.x, ob.extents.y) * 0.9f);
            Vector3 lo = new Vector3(ob.center.x, ob.min.y + r + 0.03f, ob.center.z) + delta, hi = new Vector3(ob.center.x, ob.max.y - r - 0.03f, ob.center.z) + delta;
            foreach (Vector3 p in new Vector3[] { lo, hi })
                foreach (Collider o in Physics.OverlapSphere(p, r))
                {
                    if (o == null || o.isTrigger || o.transform.IsChildOf(s.transform)) continue;
                    if (o.attachedRigidbody != null && o.attachedRigidbody.GetComponent<PushPullBlock>() == b) continue;
                    return false;
                }
            return true;
        }
        // Surface height of the block's top at x (NaN if x is not over the block).
        public static float SurfaceY(PushPullBlock b, float x)
        {
            RaycastHit h;
            return Top(b, x, out h) ? h.point.y : float.NaN;
        }

        // The point of the block's top surface at x (false if x is not over the block).
        static bool Top(PushPullBlock b, float x, out RaycastHit h)
        {
            bool any; Bounds bb = BlockBounds(b, out any);
            h = new RaycastHit();
            return any && Hit(b, new Vector3(x, bb.max.y + 1f, bb.center.z), Vector3.down, bb.size.y + 2f, out h);
        }

        // How far an Ori is inside the block (0 = not inside) and the smallest move that gets it out.
        public static float Depth(PushPullBlock b, SeinCharacter s, out Vector3 move, out string dir)
        {
            move = Vector3.zero; dir = "";
            if (b == null || s == null) return 0f;
            bool any; Bounds bb = BlockBounds(b, out any);
            CapsuleCollider cap = s.PlatformBehaviour.PlatformMovement.GetComponent<CapsuleCollider>();
            if (!any || cap == null) return 0f;
            Bounds ob = cap.bounds;
            Vector3 center = ob.center, low = new Vector3(ob.center.x, ob.min.y + 0.25f, 0f);
            Vector3 probe;
            if (InsideShape(b, bb, center)) probe = center;
            else if (InsideShape(b, bb, low)) probe = low;
            else return 0f;
            float far = bb.size.magnitude + 1f, z = bb.center.z; RaycastHit h;
            // Candidate escapes (never down: under the stone is the floor), cheapest first; take the first whose
            // destination is free, so an Ori is never shoved into a wall or another Ori (which shoves it back).
            List<KeyValuePair<float, Vector3>> c = new List<KeyValuePair<float, Vector3>>();
            if (Hit(b, new Vector3(probe.x, bb.max.y + 1f, z), Vector3.down, far, out h))
            {
                float up = h.point.y - ob.min.y + 0.02f;
                // Prefer up when the Ori is in the upper half (it was standing / landing on it).
                c.Add(new KeyValuePair<float, Vector3>(center.y >= bb.center.y ? up * 0.5f : up, new Vector3(0f, up, 0f)));
            }
            if (Hit(b, new Vector3(bb.min.x - 1f, probe.y, z), Vector3.right, far, out h))
            {
                float left = ob.max.x - h.point.x + 0.02f;
                c.Add(new KeyValuePair<float, Vector3>(left, new Vector3(-left, 0f, 0f)));
            }
            if (Hit(b, new Vector3(bb.max.x + 1f, probe.y, z), Vector3.left, far, out h))
            {
                float right = h.point.x - ob.min.x + 0.02f;
                c.Add(new KeyValuePair<float, Vector3>(right, new Vector3(right, 0f, 0f)));
            }
            if (c.Count == 0) return 0f;
            c.Sort((x, y) => x.Key.CompareTo(y.Key));
            move = c[0].Value;
            foreach (KeyValuePair<float, Vector3> k in c)
                if (Free(b, s, cap, k.Value)) { move = k.Value; break; }
            dir = move.y > 0f ? "up" : move.x < 0f ? "left" : "right";            return move.magnitude;
        }

        static void PushOutOris(PushPullBlock b, Inst i)
        {
            s_oris.Clear();
            if (P2Context.P1 != null) s_oris.Add(P2Context.P1);
            foreach (Puppet p in Players.Remotes) if (p.Sein != null) s_oris.Add(p.Sein);
            if (!NetPlay.Enabled && P2Context.P2 != null && !s_oris.Contains(P2Context.P2)) s_oris.Add(P2Context.P2);
            foreach (SeinCharacter s in s_oris)
            {
                if (s == null || !s.gameObject.activeInHierarchy) continue;
                PlatformMovement pm = s.PlatformBehaviour.PlatformMovement;
                if (pm == null || !pm.gameObject.activeInHierarchy || i.Ignored.Contains(pm)) continue;
                Vector3 move; string dir;
                float depth = Depth(b, s, out move, out dir);
                Rigidbody orb = pm.GetComponent<Rigidbody>();
                Vector3 position = pm.Position + move;
                if (depth <= 0f || !LeaseMath.Finite(depth) || !LeaseMath.Finite(move) || !LeaseMath.Finite(position)
                    || (orb != null && !ValidBody(orb))) continue;
                pm.Position = position;
                // Also move the physics body: a transform write alone was undone by the next physics step (seen in a
                // test: 400 push-outs in a row, the Ori back in the same spot every step).
                if (orb != null) orb.position = position;
                // SessionRules.HoldLocal pins the own Ori to an anchor while locked (menu / respawn / story watch); move the
                // anchor too, or the push-out is undone next frame and the Ori stays inside the stone.
                if (s == P2Context.P1) SessionRules.PositionChanged();
                Vector2 sp = pm.WorldSpeed;
                if (LeaseMath.Finite(sp.x) && LeaseMath.Finite(sp.y))
                {
                    if (move.y > 0f && sp.y < 0f) sp.y = 0f;
                    if (move.y < 0f && sp.y > 0f) sp.y = 0f;
                    if (move.x != 0f && Mathf.Sign(sp.x) != Mathf.Sign(move.x)) sp.x = 0f;
                    pm.WorldSpeed = sp;
                }
                bool puppet = s != P2Context.P1;
                if (puppet) PuppetPushOuts++; else PushOuts++;
                if (CoopTraceEvery("out" + i.St.Id + (puppet ? "p" : "l"), 0.5f))
                {
                    CoopTrace.Event("phys", "pushout", "id", i.St.Id, "who", puppet ? "puppet" : "own", "dir", dir, "depth", depth.ToString("F2"), "block", V(b.Position), "ori", V(pm.Position));
                    Rigidbody rb = s_body(b);
                    string body = orb == null ? "none" : "kin=" + orb.isKinematic + " interp=" + orb.interpolation + " rbpos=" + V(orb.position)
                        + " detect=" + orb.detectCollisions;
                    string grab = "";
                    try { grab = " grab=" + s.Abilities.GrabBlock.IsGrabbing + "/" + s.Abilities.GrabBlock.CurrentState; } catch (Exception) { }
                    Log("push out " + (puppet ? "puppet" : "own Ori") + " " + dir + " " + depth.ToString("F2") + " from " + i.St.Name
                        + " block=" + V(b.Position) + " ori=" + V(pm.Position) + " [" + body + " kinMode=" + pm.KinematicMode + " susp=" + pm.IsSuspended + grab + "]"
                        + " grounded=" + pm.IsOnGround
                        + " kin=" + (rb != null && rb.isKinematic) + " owner=P" + i.St.OwnerSeat + " ignored=" + i.Ignored.Count);
                }
            }
        }
        static string Who(PlatformMovement pm)
        {
            if (pm == LocalPm) return "own Ori P" + Players.LocalSeat;
            foreach (Puppet p in Players.Remotes) if (p.Sein != null && p.Sein.PlatformBehaviour.PlatformMovement == pm) return "puppet P" + p.Seat;
            return "Ori";
        }

        // For WorldSync: the block's pose comes from its lease stream.
        public static bool OwnsSerializable(ISerializable s)
        {
            Component c = s as Component;
            return Enabled && c != null && (s is RigidbodySerialize || s is TransformSaveSerialize)
                && c.GetComponent<PushPullBlock>() != null;
        }
    }
}
