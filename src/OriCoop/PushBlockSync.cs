using System;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Pushable stones (PushPullBlock) in co-op (playtest 2026-11-16, P1: "the stone merges with the other player").
    //
    // What went wrong (decomp PushPullBlock.cs):
    // - IgnoreCollisionWithPlayer (:269) moves the WHOLE block to layer "pushPullBlock" while someone grabs/bashes it.
    //   Both Oris share the player layer, so the OTHER Ori lost collision with the stone too: it fell in, passed through,
    //   got stuck, and could stand on it only while nobody held it.
    // - Online the puppet replays the partner's input with the native SeinGrabBlock, so each PC's puppet also pushed its
    //   own local copy; both PCs simulated the stone, and WorldSync hard-wrote its RigidbodySerialize every 250 ms in
    //   both directions (1 RTT old), teleporting the stone into players.
    // - OnEnterBash (:311) uses Characters.Sein; a puppet's bash flipped the layer for the local Ori for 20 s.
    //
    // Now:
    // - Collision is ignored per pair: Physics.IgnoreCollision(block, that Ori's capsule) for the physics step, and the
    //   block is on the "pushPullBlock" layer only while that one Ori runs its own movement step (its sweeps / rays).
    //   Everyone else always collides with it and can stand on it.
    // - Online, the Ori pushing a block owns it (claim with an epoch; ties: lower seat, the host is seat 1). The owner's
    //   game simulates it and streams its state; every other game drives it kinematically to that state, so the pusher
    //   feels no lag and everyone else sees the stone move together with the pusher's puppet. Idle blocks belong to the
    //   host. A puppet's grab/push/bash/hit never moves the local copy (its owner's game does that).
    // - An Ori found inside a block it does not ignore is pushed out (smallest of up / left / right / down).
    //   70 PhysState  owner -> others  { int id, int epoch, byte seat, byte flags(1 grabbed), float px, py, rz, vx, vy }  unreliable, 20 Hz moving / 1 Hz idle
    //   71 PhysClaim  claimer -> others { int id, int epoch }  reliable
    public static class PushBlockSync
    {
        public const byte MsgState = 70, MsgClaim = 71;
        const int HostSeat = 1;
        const float SendInterval = 0.05f, IdleInterval = 1f, SnapDistance = 3f, MaxExtrapolate = 0.06f, GrabTimeout = 0.5f;

        public sealed class St
        {
            public int Id;
            public string Name;
            public int Epoch;
            public int OwnerSeat = HostSeat;
            public bool RemoteGrabbing;
            public float RemoteGrabAt = -10f;
            public bool HasRemote;
            public Vector3 RemotePos, RemoteVel;
            public float RemoteRotZ, RemoteAt;
            public float SentAt = -10f;
            public Vector3 SentPos;
            public bool WasLocalOwner;
        }

        // Per block instance: which Oris it ignores right now (pusher, basher) and who the bash timer belongs to.
        sealed class Inst
        {
            public St St;
            public readonly List<PlatformMovement> Ignored = new List<PlatformMovement>();
            public PlatformMovement Basher;
            public float PuppetIgnoreAt;
            public Vector3 Origin;
            public bool HasOrigin;
        }

        static readonly Dictionary<int, St> s_byId = new Dictionary<int, St>();
        static readonly Dictionary<PushPullBlock, Inst> s_inst = new Dictionary<PushPullBlock, Inst>();
        static readonly Dictionary<PlatformMovement, List<PushPullBlock>> s_ignoring = new Dictionary<PlatformMovement, List<PushPullBlock>>();
        static readonly System.Reflection.FieldInfo s_fIgnore = AccessTools.Field(typeof(PushPullBlock), "m_ignoreCollisionRemainingTime");
        static readonly System.Reflection.FieldInfo s_fColliders = AccessTools.Field(typeof(PushPullBlock), "m_colliders");
        static readonly System.Reflection.FieldInfo s_fBody = AccessTools.Field(typeof(PushPullBlock), "m_rigidbody");
        static readonly System.Reflection.FieldInfo s_fOrigin = AccessTools.Field(typeof(PushPullBlock), "m_originalPosition");
        static float GetIgnoreTime(PushPullBlock b) { return (float)s_fIgnore.GetValue(b); }
        static void SetIgnoreTime(PushPullBlock b, float t) { s_fIgnore.SetValue(b, t); }
        static Collider[] s_colliders(PushPullBlock b) { return (Collider[])s_fColliders.GetValue(b); }
        static Rigidbody s_body(PushPullBlock b) { return (Rigidbody)s_fBody.GetValue(b); }
        static Vector3 s_origin(PushPullBlock b) { return (Vector3)s_fOrigin.GetValue(b); }
        static bool s_registered, s_wasOnline;
        static int s_layerDefault = -1, s_layerIgnore = -1;

        // counters for tests / logs
        public static int StatesSent, StatesApplied, ClaimsSent, ClaimsAccepted, ClaimsRejected, PuppetCallsIgnored, Denied, PushOuts, PuppetPushOuts, Snaps;
        public static bool Enabled = true;

        static void Log(string msg)
        {
            Plugin.Log.LogInfo("[phys " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.time.ToString("F2") + "] " + msg);
        }
        static string V(Vector3 v) { return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + ")"; }

        public static bool Online { get { return NetPlay.Enabled && NetPlay.Connected && P2Context.P1 != null; } }

        static void EnsureRegistered()
        {
            if (s_registered) return;
            s_registered = true;
            NetPlay.On(MsgState, ReadState);
            NetPlay.On(MsgClaim, ReadClaim);
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
            if (!s_byId.TryGetValue(id, out st)) { st = new St { Id = id, Name = scene + "/" + b.name }; s_byId[id] = st; }
            i.St = st;
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
            foreach (PlatformMovement k in gone) s_ignoring.Remove(k);
        }

        static int StableHash(string s)
        {
            unchecked { int h = (int)2166136261; foreach (char c in s) { h ^= c; h *= 16777619; } return h; }
        }

        static bool LocalOwns(St st) { return !Online || st.OwnerSeat == Players.LocalSeat; }

        public static St StateOf(PushPullBlock b) { return b == null ? null : InstOf(b).St; }
        public static bool IsLocalOwner(PushPullBlock b) { return b != null && LocalOwns(InstOf(b).St); }
        public static bool Ignores(PushPullBlock b, PlatformMovement pm) { Inst i; return b != null && s_inst.TryGetValue(b, out i) && i.Ignored.Contains(pm); }

        static bool Wins(int epoch, int seat, St st)
        {
            if (epoch != st.Epoch) return epoch > st.Epoch;
            return seat < st.OwnerSeat;
        }

        static void Claim(PushPullBlock b, Inst i, string why)
        {
            St st = i.St;
            st.Epoch++;
            st.OwnerSeat = Players.LocalSeat;
            st.RemoteGrabbing = false;
            Rigidbody rb = s_body(b);
            if (rb != null && st.HasRemote) { rb.isKinematic = false; rb.velocity = st.RemoteVel; }
            ClaimsSent++;
            int id = st.Id, epoch = st.Epoch;
            NetPlay.Send(MsgClaim, true, w => { w.Write(id); w.Write(epoch); });
            Log("claim " + st.Name + " id=" + id + " epoch=" + epoch + " seat=" + st.OwnerSeat + " (" + why + ") pos=" + V(b.Position));
            CoopTrace.Event("phys", "owner.claim", "id", id, "owner", "P" + st.OwnerSeat, "epoch", epoch, "why", why, "pos", V(b.Position));
            SendState(b, st, true);
        }

        static void ReadClaim(BinaryReader r)
        {
            int id = r.ReadInt32(), epoch = r.ReadInt32(), seat = NetPlay.Sender;
            St st;
            if (!s_byId.TryGetValue(id, out st)) { st = new St { Id = id, Name = "id" + id }; s_byId[id] = st; }
            if (Wins(epoch, seat, st))
            {
                Log("claim from P" + seat + " accepted " + st.Name + " epoch " + st.Epoch + "->" + epoch + " (owner was P" + st.OwnerSeat + ")");
                st.Epoch = epoch; st.OwnerSeat = seat; st.RemoteGrabbing = true; st.RemoteGrabAt = Time.time;
                CoopTrace.Event("phys", "owner.change", "id", id, "owner", "P" + seat, "epoch", epoch, "via", "claim");
                ClaimsAccepted++;
            }
            else
            {
                ClaimsRejected++;
                Log("claim from P" + seat + " rejected " + st.Name + " epoch=" + epoch + " mine=" + st.Epoch + " owner=P" + st.OwnerSeat);
                CoopTrace.Event("phys", "owner.reject", "id", id, "from", "P" + seat, "epoch", epoch, "owner", "P" + st.OwnerSeat, "mine", st.Epoch);
                // Tell the loser who really owns it.
                if (st.OwnerSeat == Players.LocalSeat) foreach (KeyValuePair<PushPullBlock, Inst> kv in s_inst) if (kv.Value.St == st && kv.Key != null) SendState(kv.Key, st, true);
            }
        }

        static void SendState(PushPullBlock b, St st, bool force)
        {
            Rigidbody rb = s_body(b);
            if (rb == null) return;
            Vector3 p = b.Position, v = rb.isKinematic ? Vector3.zero : rb.velocity;
            float now = Time.time;
            bool moving = v.sqrMagnitude > 0.0025f || (p - st.SentPos).sqrMagnitude > 0.0001f || b.IsGrabbed;
            if (!force && now - st.SentAt < (moving ? SendInterval : IdleInterval)) return;
            st.SentAt = now; st.SentPos = p;
            int id = st.Id, epoch = st.Epoch; byte seat = (byte)st.OwnerSeat, flags = (byte)(b.IsGrabbed ? 1 : 0);
            float rz = b.transform.eulerAngles.z;
            StatesSent++;
            NetPlay.Send(MsgState, false, w => { w.Write(id); w.Write(epoch); w.Write(seat); w.Write(flags); w.Write(p.x); w.Write(p.y); w.Write(rz); w.Write(v.x); w.Write(v.y); });
        }

        static void ReadState(BinaryReader r)
        {
            int id = r.ReadInt32(), epoch = r.ReadInt32(); int seat = r.ReadByte(); byte flags = r.ReadByte();
            Vector3 p = new Vector3(r.ReadSingle(), r.ReadSingle(), 0f); float rz = r.ReadSingle();
            Vector3 v = new Vector3(r.ReadSingle(), r.ReadSingle(), 0f);
            if (seat != NetPlay.Sender) return;
            St st;
            if (!s_byId.TryGetValue(id, out st)) { st = new St { Id = id, Name = "id" + id }; s_byId[id] = st; }
            if (epoch < st.Epoch || (epoch == st.Epoch && seat != st.OwnerSeat && !Wins(epoch, seat, st))) return;
            if (epoch > st.Epoch || seat != st.OwnerSeat)
            {
                Log("owner of " + st.Name + " is now P" + seat + " (state epoch " + epoch + ", was P" + st.OwnerSeat + " epoch " + st.Epoch + ")");
                st.Epoch = epoch; st.OwnerSeat = seat;
                CoopTrace.Event("phys", "owner.change", "id", id, "owner", "P" + seat, "epoch", epoch, "via", "state");
            }
            st.HasRemote = true; st.RemotePos = p; st.RemoteVel = v; st.RemoteRotZ = rz; st.RemoteAt = Time.time;
            st.RemoteGrabbing = (flags & 1) != 0; if (st.RemoteGrabbing) st.RemoteGrabAt = Time.time;
            StatesApplied++;
            if (CoopTrace.SampleDue("phys") && CoopTrace.Every("phys.apply." + id, 0.5f))
                CoopTrace.Sample("phys", "snapshot.apply", "id", id, "owner", "P" + seat, "pos", V(p), "vel", V(v), "rz", rz.ToString("F1"), "grabbed", st.RemoteGrabbing);
        }

        // ---------------------------------------------------------------- per-pair collision
        static PlatformMovement LocalPm { get { SeinCharacter s = P2Context.P1; return s == null ? null : s.PlatformBehaviour.PlatformMovement; } }

        static bool IsPuppet(PlatformMovement pm)
        {
            if (!NetPlay.Enabled || pm == null) return false;
            foreach (Puppet p in Players.Remotes) if (p.Sein != null && p.Sein.PlatformBehaviour.PlatformMovement == pm) return true;
            return false;
        }

        static void SetIgnore(PushPullBlock b, PlatformMovement pm, bool ignore)
        {
            if (b == null || pm == null) return;
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
            if (cols != null)
                foreach (Collider c in cols)
                    if (c != null && c.enabled) c.material = i.Ignored.Count > 0 ? b.PushPullMaterialMoving : b.PushPullMaterial;
        }

        // The game's own version: the whole block on the pushPullBlock layer (for every Ori). Ours: per Ori.
        [HarmonyPatch(typeof(PushPullBlock), "IgnoreCollisionWithPlayer")]
        static class PerPairIgnore
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement, bool ignore)
            {
                if (!Enabled) return true;
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
            if (b == null) return;
            Collider[] cols = s_colliders(b);
            if (cols == null) return;
            foreach (Collider c in cols) if (c != null && c.enabled) c.gameObject.layer = layer;
        }

        // ---------------------------------------------------------------- grab / push / release / bash / hit
        // Returns false when the call must not touch this block (a puppet online, or a block someone else is holding).
        static bool Allow(PushPullBlock b, PlatformMovement pm, string what)
        {
            if (!Enabled || !Online) return true;
            Inst i = InstOf(b);
            if (IsPuppet(pm) || P2Context.Inside)
            {
                PuppetCallsIgnored++;
                if (what != "push") Log("puppet " + what + " " + i.St.Name + " ignored here (its owner moves it)");
                return false;
            }
            if (pm != LocalPm) return true;
            St st = i.St;
            if (LocalOwns(st)) return true;
            if (st.RemoteGrabbing && Time.time - st.RemoteGrabAt < GrabTimeout)
            {
                Denied++;
                if (CoopTraceEvery("deny" + st.Id, 1f)) { Log("deny " + what + " " + st.Name + ": P" + st.OwnerSeat + " is holding it"); CoopTrace.Event("phys", "grab.denied", "id", st.Id, "owner", "P" + st.OwnerSeat, "what", what); }
                return false;
            }
            Claim(b, i, what);
            return true;
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
                try
                {
                    bool puppet = Online && (IsPuppet(platformMovement) || P2Context.Inside);
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
                try
                {
                    if (Online && Enabled && (IsPuppet(platformMovement) || P2Context.Inside)) { SetIgnore(__instance, platformMovement, true); PuppetCallsIgnored++; return false; }
                    return Allow(__instance, platformMovement, "push");
                }
                catch (Exception e) { Plugin.Log.LogWarning("[phys] push: " + e.Message); return true; }
            }
        }

        [HarmonyPatch(typeof(PushPullBlock), "OnReleased")]
        static class ReleasePatch
        {
            static bool Prefix(PushPullBlock __instance, PlatformMovement platformMovement)
            {
                try
                {
                    if (Online && Enabled && (IsPuppet(platformMovement) || P2Context.Inside)) { SetIgnore(__instance, platformMovement, false); return false; }
                    if (Online && Enabled && platformMovement == LocalPm && !LocalOwns(InstOf(__instance).St))
                    {
                        // We were denied: just drop our own per-pair ignore, do not touch the owner's velocity.
                        SetIgnore(__instance, platformMovement, false);
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
                if (!Enabled) return true;
                try
                {
                    // The game uses Characters.Sein: inside P2Context that is the bashing puppet / local P2.
                    SeinCharacter s = Characters.Sein;
                    PlatformMovement pm = s == null ? null : s.PlatformBehaviour.PlatformMovement;
                    Inst i = InstOf(__instance);
                    if (pm == null) return true;
                    if (Online && (IsPuppet(pm) || P2Context.Inside)) { PuppetCallsIgnored++; }
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
                if (!Enabled || !Online || damage == null) return true;
                try
                {
                    Inst i = InstOf(__instance);
                    if (P2Context.Inside) { PuppetCallsIgnored++; return false; }
                    if (LocalOwns(i.St)) return true;
                    if (!PlayerAttack(damage.Type)) return false;   // enemies / world: the owner's game applies it
                    return Allow(__instance, LocalPm, "hit " + damage.Type);
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
                // A copy another game owns: skip the game's step (it would flip isKinematic back every step); just follow.
                if (Online && !__instance.IsSuspended && !LocalOwns(InstOf(__instance).St))
                {
                    try { Step(__instance); }
                    catch (Exception e) { if (CoopTraceEvery("steperr", 5f)) Plugin.Log.LogWarning("[phys] step: " + e); }
                    __state = -1f;
                    return false;
                }
                // Run the bash ignore timer ourselves, for the Ori that bashed (the game would use Characters.Sein).
                float t = GetIgnoreTime(__instance);
                if (t <= 0f || __instance.IsSuspended) return true;
                Inst i = InstOf(__instance);
                t -= Time.deltaTime;
                if (t <= 0f) { t = 0f; if (i.Basher != null) SetIgnore(__instance, i.Basher, false); i.Basher = null; }
                SetIgnoreTime(__instance, 0f);
                __state = t;
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
            if (b.IsSuspended) return;
            Inst i = InstOf(b);
            St st = i.St;
            bool online = Online;
            if (online != s_wasOnline) { s_wasOnline = online; ResetOwnership(online ? "connected" : "disconnected"); }
            if (online)
            {
                EnsureRegistered();
                Rigidbody rb = s_body(b);
                if (LocalOwns(st))
                {
                    if (!st.WasLocalOwner) { st.WasLocalOwner = true; Log("simulating " + st.Name + " here (owner P" + st.OwnerSeat + " epoch " + st.Epoch + ")"); }
                    SendState(b, st, false);
                }
                else if (rb != null)
                {
                    if (st.WasLocalOwner) { st.WasLocalOwner = false; Log("following " + st.Name + " (owner P" + st.OwnerSeat + " epoch " + st.Epoch + ")"); }
                    rb.isKinematic = true;
                    if (st.HasRemote)
                    {
                        float age = Mathf.Min(Time.time - st.RemoteAt, MaxExtrapolate);
                        Vector3 target = st.RemotePos + st.RemoteVel * age;
                        target.z = rb.position.z;
                        if ((target - rb.position).magnitude > SnapDistance)
                        {
                            Snaps++;
                            Log("snap " + st.Name + " " + V(rb.position) + " -> " + V(target));
                            CoopTrace.Event("phys", "block.snap", "id", st.Id, "from", V(rb.position), "to", V(target));
                            rb.position = target; b.transform.position = target;
                        }
                        else rb.MovePosition(Vector3.Lerp(rb.position, target, 0.5f));
                        rb.MoveRotation(Quaternion.Euler(0f, 0f, Mathf.LerpAngle(b.transform.eulerAngles.z, st.RemoteRotZ, 0.5f)));
                    }
                }
            }
            if (online) KeepPuppetsOut(b, i);
            PushOutOris(b, i);
        }

        // A puppet's position is its owner's truth plus corrections written every step; letting it touch the stone in
        // the physics step made the stone stick / get dragged (the owner's game) and jitter. Puppets ignore stones in
        // the simulation; push-out keeps them on top visually, and their ground rays still see the stone.
        static void KeepPuppetsOut(PushPullBlock b, Inst i)
        {
            float now = Time.realtimeSinceStartup;
            if (now < i.PuppetIgnoreAt) return;
            i.PuppetIgnoreAt = now + 1f;   // IgnoreCollision is lost when a collider is re-enabled: renew every second
            Collider[] cols = s_colliders(b);
            if (cols == null) return;
            foreach (Puppet p in Players.Remotes)
            {
                if (p.Sein == null || !p.Sein.gameObject.activeInHierarchy) continue;
                Collider cap = p.Sein.PlatformBehaviour.PlatformMovement.GetComponent<CapsuleCollider>();
                if (cap == null || !cap.enabled) continue;
                foreach (Collider c in cols) if (c != null && c.enabled && c.gameObject.activeInHierarchy) Physics.IgnoreCollision(c, cap, true);
            }
        }

        static void ResetOwnership(string why)
        {
            foreach (St st in s_byId.Values) { st.Epoch = 0; st.OwnerSeat = HostSeat; st.HasRemote = false; st.RemoteGrabbing = false; st.WasLocalOwner = false; }
            Log("ownership reset (" + why + "): " + s_byId.Count + " blocks back to the host");
            CoopTrace.Event("phys", "owner.reset", "why", why, "blocks", s_byId.Count);
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
            bool any; Bounds bb = BlockBounds(b, out any);
            RaycastHit h;
            if (!any || !Hit(b, new Vector3(x, bb.max.y + 1f, bb.center.z), Vector3.down, bb.size.y + 2f, out h)) return float.NaN;
            return h.point.y;
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
                if (pm == null || i.Ignored.Contains(pm)) continue;
                Vector3 move; string dir;
                float depth = Depth(b, s, out move, out dir);
                if (depth <= 0f) continue;
                pm.Position = pm.Position + move;
                // Also move the physics body: a transform write alone was undone by the next physics step (seen in a
                // test: 400 push-outs in a row, the Ori back in the same spot every step).
                Rigidbody orb = pm.GetComponent<Rigidbody>();
                if (orb != null) orb.position = pm.Position;
                // SessionRules.HoldLocal pins the own Ori to an anchor while locked (menu / respawn / story watch); move the
                // anchor too, or the push-out is undone next frame and the Ori stays inside the stone.
                if (s == P2Context.P1) SessionRules.PositionChanged();
                Vector2 sp = pm.WorldSpeed;
                if (move.y > 0f && sp.y < 0f) sp.y = 0f;
                if (move.y < 0f && sp.y > 0f) sp.y = 0f;
                if (move.x != 0f && Mathf.Sign(sp.x) != Mathf.Sign(move.x)) sp.x = 0f;
                pm.WorldSpeed = sp;
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

        // For WorldSync: the block's own RigidbodySerialize is ours, not part of the shared save stream.
        public static bool OwnsSerializable(ISerializable s)
        {
            Component c = s as Component;
            return Enabled && c != null && s is RigidbodySerialize && c.GetComponent<PushPullBlock>() != null;
        }
    }
}
