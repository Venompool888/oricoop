using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Dynamic world objects in online co-op (playtest 2026-11-16 P1 follow-up, bug #19). Design in docs/physics-objects.md.
    //
    // The puppet is a full SeinCharacter: on every PC it touches triggers, jumps, stomps and attacks the local copies of
    // the world, but its replayed path is not the partner's real path (lag, corrections, missed frames). So, as for the
    // pushable stones (PushBlockSync), the player whose game it is decides:
    // - Event objects (one-shot or per action): the PC whose OWN Ori caused it performs it and sends message 75; the other
    //   PC performs the same thing on its copy. A puppet's touch / jump / attack never changes the local copy.
    //     stomper shake (StomperTrigger), crumbling floor (CollapsingSolid), jump-flip platforms (JumpFlipPlatform),
    //     breakable walls / floors (non-enemy Entity damage), falling rocks and other hazard PlayerCollisionTriggers.
    // - Clock objects: the host owns them and streams message 76; the guest follows.
    //     sine-moving platforms (phase), rising water (height / speed), jump-flip state heartbeat (join / drift).
    // - WorldSync-applied flags that the game only acts on at load: a rope cut (AttachToRope), a crumbling floor's
    //   m_activated, a stomp post's m_activated now also perform the game's action when WorldSync applies them live.
    // Collision is never switched off for anyone; no object is moved by the puppet.
    //
    // Wire (little endian, BinaryWriter):
    //   75 WorldObjEvent  { byte kind, int id, uint seq, payload }  sent twice: unreliable (fast) + reliable (sure); the
//      receiver drops a seq it has seen. Under 200 ms RTT the reliable copy arrived 1.4-2 s late (queued behind other
//      reliable traffic), long after the toucher's PC had acted.
    //      1 stomper   -       2 collapse  -       3 flip { byte jumpKind 0 jump 1 double 2 wall 3 charge }
    //      4 damage { float amount, int type, float fx, fy, px, py, pz }      5 trigger -      6 rope cut -
    //   76 WorldObjState  host -> guest { byte kind, ushort n, entries }  unreliable
    //      1 sin { int id, float time }   2 water { int id, float y, float speed }   3 flips { int id, bool active }
    public static class WorldObjSync
    {
        const byte MsgEvent = 75, MsgState = 76;
        const byte KStomper = 1, KCollapse = 2, KFlip = 3, KDamage = 4, KTrigger = 5, KRope = 6;
        const byte SSin = 1, SWater = 2, SFlips = 3;

        // Mutation switch for the harness (ORICOOP_WORLDOBJ_OFF=1): the old behaviour, every PC on its own.
        public static bool Enabled = Environment.GetEnvironmentVariable("ORICOOP_WORLDOBJ_OFF") != "1";
        public static int EventsSent, EventsApplied, EventsMissing, PuppetSuppressed, LiveApplies, SinCorrections, WaterFollows, FlipHeartbeatFixes;
        // T-074: events held back until the object's scene has finished starting (see SceneStarted).
        public static int EventsDeferred, EventsReplayed;
        sealed class Deferred { public byte Kind; public int Id; public byte[] Payload; public Component Target; public float Until; }
        static readonly World.HeldEvents<Deferred> s_deferred = new World.HeldEvents<Deferred>();   // replays in arrival order

        // A scene's saved state is loaded in SceneRoot.LateStart, a frame or more after its objects exist
        // (ScenesManager.OnSceneStartCompleted sets HasStartBeenCalled at its end). A change made to one of its objects
        // before that is overwritten by the saved state: a floor broken by the partner's event came back whole on this PC
        // (WO5L / WO6L, 2026-11-18, heavy load: scenes start late). Not inside any scene (DontDestroyOnLoad) = started.
        public static bool SceneStarted(Component c)
        {
            if (c == null || Core.Scenes.Manager == null) return true;
            SceneRoot root = c.GetComponentInParent<SceneRoot>();
            if (root == null) return true;
            foreach (SceneManagerScene s in Core.Scenes.Manager.ActiveScenes)
                if (s != null && s.SceneRoot == root) return s.HasStartBeenCalled;
            return true;
        }

        static bool s_registered, s_applying;
        static float s_sinAt, s_waterAt, s_flipAt, s_lastFlipActivity = -10f;
        static readonly int[] s_flipSentFrame = { -1, -1, -1, -1 };
        static readonly Dictionary<int, Component> s_byId = new Dictionary<int, Component>();
        static readonly Dictionary<int, bool> s_triggerReplicable = new Dictionary<int, bool>();
        static readonly World.OneShotTriggers s_firedOnce = new World.OneShotTriggers();
        static readonly World.SeenEvents s_seen = new World.SeenEvents(4096);
        static uint s_seq;
        // Rising water follower state (guest).
        static float s_waterY, s_waterSpeed, s_waterAt2 = -10f, s_waterWaitSince; static int s_waterId, s_waterWaitId, s_hostWaterId; static float s_hostWaterY, s_hostWaterAt, s_hostWaterRate;

        static readonly FieldInfo F_stomperState = AccessTools.Field(typeof(Stomper), "m_state");
        static readonly FieldInfo F_collapseActivated = AccessTools.Field(typeof(CollapsingSolid), "m_activated");
        static readonly FieldInfo F_flipActive = AccessTools.Field(typeof(JumpFlipPlatform), "m_active");
        static readonly FieldInfo F_sinTime = AccessTools.Field(typeof(SinMovingPlatform), "m_time");
        static readonly FieldInfo F_waterSpeed = AccessTools.Field(typeof(RisingWater), "m_currentSpeed");
        static readonly FieldInfo F_postActivated = AccessTools.Field(typeof(StompPost), "m_activated");
        static readonly FieldInfo F_ropeShould = AccessTools.Field(typeof(AttachToRope), "m_shouldBeAttachedAfterLoading");
        static readonly MethodInfo M_ropeAttached = AccessTools.Method(typeof(AttachToRope), "IsAttached");

        public static bool Online { get { return Enabled && NetPlay.Enabled && NetPlay.Connected && P2Context.P1 != null; } }
        // A puppet's own update / touch runs inside P2Context (Characters.Sein swapped to it).
        static bool PuppetCause { get { return Players.RemoteContext && !s_applying; } }

        static void Log(string msg)
        {
            Plugin.Log.LogInfo("[wobj " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.time.ToString("F2") + "] " + msg);
        }

        static void EnsureRegistered()
        {
            if (s_registered) return;
            s_registered = true;
            NetPlay.On(MsgEvent, ReadEvent);
            NetPlay.On(MsgState, ReadState);
            NetPlay.SessionStarted += Reset;
            NetPlay.SessionEnded += Reset;
            // T-145: a player taking a seat (maybe a new process whose seq restarts) must not inherit its old keys.
            NetPlay.PlayerJoined += delegate(int seat) { s_seen.SeatJoined(seat); };
            Log("registered (Enabled=" + Enabled + ")");
        }

        static void Reset()
        {
            s_byId.Clear(); s_triggerReplicable.Clear(); s_firedOnce.Clear(); s_seen.Clear(); s_deferred.Clear();
            s_waterAt2 = -10f; s_lastFlipActivity = -10f;
        }

        // ---------------------------------------------------------------- identity
        // Same on every PC: the object's save GUID, else scene + hierarchy path.
        public static int IdOf(Component c)
        {
            if (c == null) return 0;
            GuidOwner g = c as GuidOwner;
            if (g == null) g = c.GetComponent<SaveSerialize>();
            string key;
            if (g != null && g.IsGuidInitialized()) key = "guid|" + g.MoonGuid.ToString();
            else
            {
                key = c.gameObject.scene.name + "|" + c.GetType().Name;
                for (Transform t = c.transform; t != null; t = t.parent) key += "/" + t.name;
            }
            unchecked { int h = (int)2166136261; foreach (char ch in key) { h ^= ch; h *= 16777619; } return h == 0 ? 1 : h; }
        }

        static T Find<T>(int id) where T : Component
        {
            Component c;
            if (s_byId.TryGetValue(id, out c) && c != null && c is T) return (T)c;
            foreach (T t in UnityEngine.Object.FindObjectsOfType<T>())
            {
                int i = IdOf(t);
                s_byId[i] = t;
                if (i == id) c = t;
            }
            return c as T;
        }

        static void SendEvent(byte kind, int id, Action<BinaryWriter> payload, string what)
        {
            if (!Online) return;
            uint seq = ++s_seq;
            Action<BinaryWriter> body = w => { w.Write(kind); w.Write(id); w.Write(seq); if (payload != null) payload(w); };
            NetPlay.Send(MsgEvent, false, body);
            NetPlay.Send(MsgEvent, true, body);
            EventsSent++;
            Log("send " + what + " id=" + id);
            CoopTrace.Event("phys", "wobj.send", "kind", what, "id", id);
        }

        static void ReadEvent(BinaryReader r)
        {
            byte kind = r.ReadByte(); int id = r.ReadInt32(); uint seq = r.ReadUInt32();
            if (!Enabled) return;
            if (!s_seen.FirstTime(NetPlay.Sender, seq)) return;
            byte[] payload = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
            ApplyEvent(kind, id, payload, false);
        }

        // Test hook (NetWorldObjTest WO5D): apply a damage event exactly as if the partner had sent it (target = the EntityDamageReciever).
        public static void TestApplyDamage(Component target, float amount)
        {
            MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m);
            Vector3 p = target.transform.position;
            w.Write(amount); w.Write((int)DamageType.Stomp); w.Write(0f); w.Write(0f); w.Write(p.x); w.Write(p.y); w.Write(p.z); w.Flush();
            ApplyEvent(KDamage, IdOf(target), m.ToArray(), false);
        }

        // Defers the event when its object's scene has not started yet; true = deferred (the caller does nothing now).
        // Test-only mutation switch: ORICOOP_WOBJ_NO_DEFER=1 applies events at once (pre-T-074), so WO5D must fail.
        static readonly bool s_mutateNoDefer = Environment.GetEnvironmentVariable("ORICOOP_WOBJ_NO_DEFER") == "1";
        static bool DeferIfNotStarted(byte kind, int id, byte[] payload, Component target)
        {
            if (s_mutateNoDefer || target == null || SceneStarted(target)) return false;
            s_deferred.Add(new Deferred { Kind = kind, Id = id, Payload = payload, Target = target, Until = Time.realtimeSinceStartup + 30f });
            EventsDeferred++;
            Log("defer kind=" + kind + " id=" + id + ": its scene has not finished starting (saved state not loaded yet)");
            return true;
        }

        static void ReplayDeferred()
        {
            if (s_deferred.Count == 0) return;
            int dropped;
            List<Deferred> ready = s_deferred.TakeReady(d => d.Target == null || Time.realtimeSinceStartup > d.Until, d => SceneStarted(d.Target), out dropped);
            EventsMissing += dropped;
            foreach (Deferred d in ready) { EventsReplayed++; ApplyEvent(d.Kind, d.Id, d.Payload, true); }
        }

        static void ApplyEvent(byte kind, int id, byte[] payload, bool replay)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(payload));
            s_applying = true; bool deferred = false;
            string what = "?"; bool found = true;
            try
            {
                switch (kind)
                {
                    case KStomper:
                    {
                        what = "stomper"; Stomper s = Find<Stomper>(id); found = s != null;
                        if (DeferIfNotStarted(kind, id, payload, s)) { deferred = true; break; }
                        if (s != null) s.PlayerTouchedTrigger();
                        break;
                    }
                    case KCollapse:
                    {
                        what = "collapse"; CollapsingSolid c = Find<CollapsingSolid>(id); found = c != null;
                        if (DeferIfNotStarted(kind, id, payload, c)) { deferred = true; break; }
                        if (c != null && !(bool)F_collapseActivated.GetValue(c)) { F_collapseActivated.SetValue(c, true); if (c.Animator != null) c.Animator.AnimatorDriver.Restart(); }
                        break;
                    }
                    case KFlip:
                    {
                        byte jump = r.ReadByte(); what = "flip" + jump;
                        s_lastFlipActivity = Time.time;
                        Action raise = jump == 0 ? JumpFlipPlatform.OnSeinJumpEvent : jump == 1 ? JumpFlipPlatform.OnSeinDoubleJumpEvent
                            : jump == 2 ? JumpFlipPlatform.OnSeinWallJumpEvent : JumpFlipPlatform.OnSeinChargeJumpEvent;
                        if (raise != null) raise();
                        break;
                    }
                    case KDamage:
                    {
                        float amount = r.ReadSingle(); DamageType type = (DamageType)r.ReadInt32();
                        Vector2 force = new Vector2(r.ReadSingle(), r.ReadSingle());
                        Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        what = "damage " + type + " " + amount;
                        EntityDamageReciever dr = Find<EntityDamageReciever>(id); found = dr != null;
                        if (DeferIfNotStarted(kind, id, payload, dr)) { deferred = true; break; }
                        if (dr != null && dr.gameObject.activeInHierarchy) dr.OnRecieveDamage(new Damage(amount, force, pos, type, null));
                        break;
                    }
                    case KTrigger:
                    {
                        what = "trigger"; PlayerCollisionTrigger t = Find<PlayerCollisionTrigger>(id); found = t != null;
                        if (DeferIfNotStarted(kind, id, payload, t)) { deferred = true; break; }
                        if (t != null && !(t.TriggerOnce && s_firedOnce.HasFired(id)) && (t.Active || t.TriggerOnce))
                        {
                            // WorldSync may already have written the partner's Active=false (TriggerOnce): run it once anyway.
                            t.Active = true;
                            if (t.TriggerOnce) s_firedOnce.TryFire(id);
                            t.DoTrigger(false);
                        }
                        break;
                    }
                    case KRope:
                    {
                        what = "rope"; AttachToRope a = Find<AttachToRope>(id); found = a != null;
                        if (DeferIfNotStarted(kind, id, payload, a)) { deferred = true; break; }
                        if (a != null && (bool)M_ropeAttached.Invoke(a, null)) a.BreakAttachment();
                        break;
                    }
                    default: what = "unknown" + kind; break;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[wobj] apply " + what + " id=" + id + " failed: " + e.Message); }
            finally { s_applying = false; }
            if (deferred) return;
            if (found) EventsApplied++; else EventsMissing++;
            Log((replay ? "replay " : "recv ") + what + " id=" + id + (found ? "" : " (object not loaded here)"));
            CoopTrace.Event("phys", "wobj.apply", "kind", what, "id", id, "found", found);
        }

        // ---------------------------------------------------------------- host clock objects
        static void Tick()
        {
            EnsureRegistered();
            ReplayDeferred();
            if (!Online || !NetPlay.IsHost) return;
            float now = Time.realtimeSinceStartup;
            if (now >= s_sinAt)
            {
                s_sinAt = now + 0.25f;   // 4 Hz: game clocks drift apart when one PC drops frames (Unity caps fixed steps per frame)
                SinMovingPlatform[] all = UnityEngine.Object.FindObjectsOfType<SinMovingPlatform>();
                if (all.Length > 0)
                    NetPlay.Send(MsgState, false, w => {
                        w.Write(SSin); w.Write((ushort)all.Length);
                        foreach (SinMovingPlatform p in all) { w.Write(IdOf(p)); w.Write((float)F_sinTime.GetValue(p)); }
                    });
            }
            if (now >= s_waterAt)
            {
                s_waterAt = now + 0.1f;
                RisingWater water = RisingWater.Available ? UnityEngine.Object.FindObjectOfType<RisingWater>() : null;
                if (water != null)
                {
                    int id = IdOf(water); float y = water.transform.position.y, speed = (float)F_waterSpeed.GetValue(water);
                    // Rate per REAL second (the guest extrapolates in real time; under load this game clock runs slower).
                    // Smoothed over ~0.5 s: single 0.1 s windows were noisy (fixed steps bunch up) and the guest multiplies the rate.
                    if (s_hostWaterId == id && now - s_hostWaterAt > 0.05f && now - s_hostWaterAt < 1f)
                        s_hostWaterRate = Mathf.Lerp(s_hostWaterRate, (y - s_hostWaterY) / (now - s_hostWaterAt), 0.2f);
                    else s_hostWaterRate = speed;
                    speed = s_hostWaterRate;
                    s_hostWaterId = id; s_hostWaterY = y; s_hostWaterAt = now;
                    NetPlay.Send(MsgState, false, w => { w.Write(SWater); w.Write((ushort)1); w.Write(id); w.Write(y); w.Write(speed); });
                }
            }
            if (now >= s_flipAt)
            {
                s_flipAt = now + 2f;
                JumpFlipPlatform[] flips = UnityEngine.Object.FindObjectsOfType<JumpFlipPlatform>();
                if (flips.Length > 0 && Time.time - s_lastFlipActivity > 0.5f)
                    NetPlay.Send(MsgState, false, w => {
                        w.Write(SFlips); w.Write((ushort)flips.Length);
                        foreach (JumpFlipPlatform f in flips) { w.Write(IdOf(f)); w.Write((bool)F_flipActive.GetValue(f)); }
                    });
            }
        }

        static void ReadState(BinaryReader r)
        {
            byte kind = r.ReadByte(); int n = r.ReadUInt16();
            if (!Enabled || NetPlay.IsHost) return;
            float halfRtt = NetPlay.RttMs * 0.0005f;
            for (int i = 0; i < n; i++)
            {
                if (kind == SSin)
                {
                    int id = r.ReadInt32(); float time = r.ReadSingle();
                    SinMovingPlatform p = Find<SinMovingPlatform>(id);
                    if (p == null || p.IsSuspended) continue;
                    float mine = (float)F_sinTime.GetValue(p), target = time + halfRtt, err = target - mine;
                    if (Mathf.Abs(err) < 0.02f) continue;
                    // Smooth small drift, snap a pause / join sized offset.
                    float next = Mathf.Abs(err) > 0.3f ? target : mine + err * 0.7f;
                    F_sinTime.SetValue(p, next); p.UpdatePosition();
                    SinCorrections++;
                    if (Mathf.Abs(err) > 0.3f) { Log("sin " + p.name + " id=" + id + " phase snapped by " + err.ToString("F2") + " s"); CoopTrace.Event("phys", "wobj.sin.snap", "id", id, "err", err); }
                }
                else if (kind == SWater)
                {
                    s_waterId = r.ReadInt32(); s_waterY = r.ReadSingle(); s_waterSpeed = r.ReadSingle(); s_waterAt2 = Time.realtimeSinceStartup;
                }
                else if (kind == SFlips)
                {
                    int id = r.ReadInt32(); bool active = r.ReadBoolean();
                    if (Time.time - s_lastFlipActivity < 1.5f) continue;
                    JumpFlipPlatform f = Find<JumpFlipPlatform>(id);
                    if (f == null || (bool)F_flipActive.GetValue(f) == active) continue;
                    F_flipActive.SetValue(f, active); f.UpdateState();
                    FlipHeartbeatFixes++;
                    Log("flip " + f.name + " id=" + id + " set to host state " + active);
                    CoopTrace.Event("phys", "wobj.flip.fix", "id", id, "active", active);
                }
                else return;
            }
        }

        // Read access for the harness (NetWorldObjTest).
        public static int StomperState(Stomper s) { return Convert.ToInt32(F_stomperState.GetValue(s)); }
        public static bool CollapseActivated(CollapsingSolid c) { return (bool)F_collapseActivated.GetValue(c); }
        public static bool FlipActive(JumpFlipPlatform f) { return (bool)F_flipActive.GetValue(f); }
        public static float SinTime(SinMovingPlatform p) { return (float)F_sinTime.GetValue(p); }
        public static void SetSinTime(SinMovingPlatform p, float t) { F_sinTime.SetValue(p, t); p.UpdatePosition(); }
        public static bool PostActivated(StompPost p) { return (bool)F_postActivated.GetValue(p); }
        public static bool RopeAttached(AttachToRope a) { return (bool)M_ropeAttached.Invoke(a, null); }
        public static float WaterSpeed(RisingWater w) { return (float)F_waterSpeed.GetValue(w); }
        // Harness: re-arm a TriggerOnce trigger on this PC (the same scene is used again at 200 ms).
        public static void ForgetTrigger(PlayerCollisionTrigger t) { if (t != null) { s_firedOnce.Forget(IdOf(t)); t.Active = true; } }
        // Harness (R3, T-144): mark an id as fired here / ask whether it still counts as fired.
        public static void TestMarkFired(int id) { s_firedOnce.TryFire(id); }
        public static bool TestHasFired(int id) { return s_firedOnce.HasFired(id); }
        public static bool IsReplicableTrigger(PlayerCollisionTrigger t) { return t != null && Replicable(t, IdOf(t)); }

        // WorldSync: objects whose state this module carries (live hard writes would fight it).
        public static bool OwnsSerializable(ISerializable s)
        {
            // Stomper.Serialize writes nothing and its read resets the crusher to the top (Stomper.cs:236): a full resync
            // (digest mismatch, more frequent over the relay) snapped falling stompers back up on one PC (WO1 on relay).
            return Enabled && (s is JumpFlipPlatform || s is RisingWater || s is Stomper);
        }

        // ---------------------------------------------------------------- puppet damage
        static bool PlayerDamage(DamageType t)
        {
            return t == DamageType.SpiritFlame || t == DamageType.SpiritFlameSplatter || t == DamageType.ChargeFlame
                || t == DamageType.Bash || t == DamageType.Stomp || t == DamageType.StompBlast || t == DamageType.Grenade || t == DamageType.LevelUp;
        }
        // EnemySync's own rule (damage stamp: puppet context, puppet attack depth, puppet-owned senders / projectiles).
        static bool PuppetDamage(Damage d) { return Players.RemoteContext || EnemySync.IsPuppetDamage(d); }
        // Hazard triggers that are safe to run on the partner's PC: no scene load, checkpoint, world event, character /
        // camera / input / story action (StorySync owns story sequences).
        static readonly HashSet<string> DeniedActions = new HashSet<string> {
            "LoadSceneAction", "CreateCheckpointAction", "MarkActiveScenesAsCheckpointKeepLoaded", "SetCharacterPosition",
            "SetWorldEventAction", "LockPlayerInputAction", "LockPlayerInputManualAction", "LockMovementAction", "LetterboxAction",
            "ShowStoryTextAction", "WatchCutsceneAction", "PauseGameAction", "MoveCameraAction", "MoveCameraHereAction",
            "MoveCameraToPlayerAction", "SetCameraTargetAction", "ChangeCutsceneStateAction", "RegisterSkipCutsceneAction",
            "OriFlyToPositionAction", "OriFlyBackToPlayerAction", "ActivateOriAction", "ActivateCharacterAction",
            "PlayCharacterAnimation", "StopCharacterAnimation", "FadeToBlackAction", "FaderBFadeInAction", "FaderBFadeOutAction",
            "ShowSeinUIAction", "PlayerFaceDirectionAction", "SaveGameAction", "RegisterStringTriggerAction",
            "LetterboxActionB", "TeleportAction", "SetSeinPositionAction" };
        static bool Replicable(PlayerCollisionTrigger t, int id)
        {
            bool ok;
            if (s_triggerReplicable.TryGetValue(id, out ok)) return ok;
            ok = t.ActionToRun != null && !Denied(t.ActionToRun, new HashSet<Component>());
            s_triggerReplicable[id] = ok;
            Log("trigger " + t.name + " id=" + id + " action=" + (t.ActionToRun == null ? "none" : t.ActionToRun.name) + " replicated=" + ok);
            return ok;
        }
        static bool Denied(Component c, HashSet<Component> visited)
        {
            if (c == null || !visited.Add(c)) return false;
            if (DeniedActions.Contains(c.GetType().Name)) return true;
            ActionSequence seq = c as ActionSequence;
            if (seq != null && StorySync.IsStorySequence(seq)) return true;
            foreach (FieldInfo field in c.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(ActionMethod).IsAssignableFrom(field.FieldType))
                { if (Denied(field.GetValue(c) as Component, visited)) return true; }
                else if (field.FieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                {
                    IEnumerable items = field.GetValue(c) as IEnumerable;
                    if (items != null) foreach (object o in items) if (Denied(o as ActionMethod, visited)) return true;
                }
            }
            if (seq != null && seq.Actions == null)
                foreach (ActionMethod a in seq.GetComponentsInChildren<ActionMethod>(true)) if (Denied(a, visited)) return true;
            return false;
        }

        // ================================================================ Harmony
        [HarmonyPatch(typeof(GameController), "FixedUpdate")]
        static class TickHook { static void Postfix() { try { Tick(); } catch (Exception e) { Plugin.Log.LogWarning("[wobj] tick: " + e.Message); } } }

        // Stomper: the toucher's PC drops it (StomperTrigger -> PlayerTouchedTrigger, Stomper.cs:224).
        [HarmonyPatch(typeof(Stomper), "PlayerTouchedTrigger")]
        static class StomperTouch
        {
            static bool Prefix(Stomper __instance)
            {
                if (!Online || s_applying) return true;
                if (PuppetCause) { PuppetSuppressed++; return false; }
                if (Convert.ToInt32(F_stomperState.GetValue(__instance)) == 0)
                {
                    int id = IdOf(__instance);
                    SendEvent(KStomper, id, null, "stomper " + __instance.name);
                }
                return true;
            }
        }

        // Crumbling floor: the toucher's PC starts the collapse (CollapsingSolid.cs:9).
        [HarmonyPatch(typeof(CollapsingSolid), "OnCollisionEnter")]
        static class CollapseTouch
        {
            static bool Prefix(CollapsingSolid __instance, out bool __state)
            {
                __state = (bool)F_collapseActivated.GetValue(__instance);
                if (!Online || s_applying) return true;
                if (PuppetCause) { PuppetSuppressed++; return false; }
                return true;
            }
            static void Postfix(CollapsingSolid __instance, bool __state)
            {
                if (!Online || s_applying || Players.RemoteContext || __state || !(bool)F_collapseActivated.GetValue(__instance)) return;
                SendEvent(KCollapse, IdOf(__instance), null, "collapse " + __instance.name);
            }
        }
        // Live WorldSync apply of m_activated (late join / missed event): the game only sets the bool, run the collapse too.
        [HarmonyPatch(typeof(CollapsingSolid), "Serialize")]
        static class CollapseApply
        {
            static void Prefix(CollapsingSolid __instance, out bool __state) { __state = (bool)F_collapseActivated.GetValue(__instance); }
            static void Postfix(CollapsingSolid __instance, bool __state)
            {
                if (!Enabled || !WorldSync.Applying || __state || !(bool)F_collapseActivated.GetValue(__instance) || __instance.Animator == null) return;
                __instance.Animator.AnimatorDriver.Restart(); LiveApplies++;
                Log("collapse " + __instance.name + " started by WorldSync apply");
            }
        }

        // Jump-flip platforms toggle on every Ori's jump (JumpFlipPlatform.OnSeinJumpEvent is raised by any SeinJump).
        // Only the jumper's PC toggles; the partner's PC toggles from message 75.
        static bool FlipGate(int jump)
        {
            if (!Online || s_applying) return true;
            if (PuppetCause) { PuppetSuppressed++; return false; }
            s_lastFlipActivity = Time.time;
            if (s_flipSentFrame[jump] != Time.frameCount)
            {
                s_flipSentFrame[jump] = Time.frameCount;
                byte j = (byte)jump;
                SendEvent(KFlip, 0, w => w.Write(j), "flip" + jump);
            }
            return true;
        }
        [HarmonyPatch(typeof(JumpFlipPlatform), "OnPlayerJump")] static class FlipJump { static bool Prefix() { return FlipGate(0); } }
        [HarmonyPatch(typeof(JumpFlipPlatform), "OnPlayerDoubleJump")] static class FlipDouble { static bool Prefix() { return FlipGate(1); } }
        [HarmonyPatch(typeof(JumpFlipPlatform), "OnPlayerWallJump")] static class FlipWall { static bool Prefix() { return FlipGate(2); } }
        [HarmonyPatch(typeof(JumpFlipPlatform), "OnPlayerChargeJump")] static class FlipCharge { static bool Prefix() { return FlipGate(3); } }

        // Rising water: its speed follows Characters.Sein (RisingWater.cs:56), i.e. a different speed on each PC. The host
        // simulates it, the guest follows the host's height (never moving down except a > 2 unit reset).
        [HarmonyPatch(typeof(RisingWater), "FixedUpdate")]
        static class WaterFollow
        {
            static bool Prefix(RisingWater __instance)
            {
                if (!Online || NetPlay.IsHost || __instance.IsSuspended) return true;
                float age = Time.realtimeSinceStartup - s_waterAt2;
                int id = IdOf(__instance);
                if (age > 1.5f || s_waterId != id)
                {
                    // Water just appeared here: hold it up to 3 s for the host's stream instead of racing ahead at
                    // this PC's own speed. No stream after that (the host does not have it loaded): native.
                    int inst = __instance.GetInstanceID();   // per instance: a reloaded water has the same net id
                    if (s_waterWaitId != inst) { s_waterWaitId = inst; s_waterWaitSince = Time.realtimeSinceStartup; }
                    return Time.realtimeSinceStartup - s_waterWaitSince > 3f;
                }
                Vector3 p = __instance.transform.position;
                // Extrapolate one half RTT only: the host's game clock can run slower than real time under load.
                float target = s_waterY + s_waterSpeed * (Mathf.Min(age, 0.25f) + Mathf.Min(NetPlay.RttMs * 0.0005f, 0.15f));   // RTT estimate spikes on the relay   // relay packets can be 0.2 s apart
                if (target < p.y - 2f) p.y = target;
                else if (target > p.y) p.y = target - p.y < 1f ? target : Mathf.Lerp(p.y, target, 0.3f);   // a lerp alone trails a rising target
                __instance.transform.position = p;
                WaterFollows++;
                return false;
            }
        }

        // Breakable walls / floors (Entity with EntityDamageReciever that is not an enemy; EnemySync owns enemies): the
        // attacker's PC applies its own hits and sends them; the puppet's hits on the local copy are dropped.
        [HarmonyPatch(typeof(EntityDamageReciever), "OnRecieveDamage")]
        static class WorldEntityDamage
        {
            static bool Prefix(EntityDamageReciever __instance, Damage damage)
            {
                if (!Online || s_applying || damage == null || __instance.Entity == null || __instance.Entity is Enemy) return true;
                if (!PlayerDamage(damage.Type)) return true;
                if (PuppetDamage(damage)) { PuppetSuppressed++; return false; }
                if (damage.Amount <= 0f) return true;
                int id = IdOf(__instance);
                float amount = damage.Amount; int type = (int)damage.Type; Vector2 f = damage.Force; Vector3 p = damage.Position;
                SendEvent(KDamage, id, w => { w.Write(amount); w.Write(type); w.Write(f.x); w.Write(f.y); w.Write(p.x); w.Write(p.y); w.Write(p.z); },
                    "damage " + __instance.Entity.name + " " + damage.Type + " " + amount);
                return true;
            }
        }

        // Rope cut: AttachToRope.Serialize only stores the flag; the joint is destroyed at load (AttachToRope.cs:172).
        // A live WorldSync apply of "detached" now breaks the joint (never re-attaches live: a stale write must not undo a cut).
        [HarmonyPatch(typeof(AttachToRope), "Serialize")]
        static class RopeApply
        {
            static void Postfix(AttachToRope __instance, Archive a)
            {
                if (!Enabled || !WorldSync.Applying || !a.Reading || (bool)F_ropeShould.GetValue(__instance)) return;
                if (!(bool)M_ropeAttached.Invoke(__instance, null)) return;
                __instance.BreakAttachment(); LiveApplies++;
                Log("rope " + __instance.name + " detached by WorldSync apply");
                CoopTrace.Event("phys", "wobj.rope.detach", "id", IdOf(__instance));
            }
        }

        // Rope cut on this PC (DetachRopeOnStomp, scripts): send it; the partner breaks the same joint at once.
        [HarmonyPatch(typeof(AttachToRope), "BreakAttachment")]
        static class RopeCut
        {
            static void Prefix(AttachToRope __instance, out bool __state) { __state = (bool)M_ropeAttached.Invoke(__instance, null); }
            static void Postfix(AttachToRope __instance, bool __state)
            {
                if (!Online || s_applying || WorldSync.Applying || Players.RemoteContext || !__state) return;
                SendEvent(KRope, IdOf(__instance), null, "rope " + __instance.name);
            }
        }

        // Stomp post fully in on the partner's PC: run AllTheWayInAction here too (StompPost.cs:60-77 runs it only on a hit).
        public static int PostActionsApplied;
        [HarmonyPatch(typeof(StompPost), "Serialize")]
        static class PostApply
        {
            static void Prefix(StompPost __instance, out bool __state) { __state = (bool)F_postActivated.GetValue(__instance); }
            static void Postfix(StompPost __instance, bool __state)
            {
                if (!Enabled || !WorldSync.Applying || __state || !(bool)F_postActivated.GetValue(__instance)) return;
                if (__instance.AllTheWayInAction != null) __instance.AllTheWayInAction.Perform(null);
                PostActionsApplied++; LiveApplies++;
                Log("stomp post " + __instance.name + " AllTheWayInAction run by WorldSync apply");
                CoopTrace.Event("phys", "wobj.post.in", "id", IdOf(__instance));
            }
        }

        // Carryables (the nightberry): SeinCarry picks up anything in range whose CanBeCarried() is true (SeinCarry.cs:246),
        // including what the partner's puppet is holding on this PC; then both players carry it. Something already carried
        // cannot be taken (single player never needs that). While carried it sits under an Ori, so WorldSync skips it.
        public static int CarryDenied;
        [HarmonyPatch(typeof(CarryableRigidBody), "CanBeCarried")]
        static class CarryTaken
        {
            static bool Prefix(CarryableRigidBody __instance, ref bool __result)
            {
                if (!Online || !__instance.IsCarried) return true;
                __result = false; CarryDenied++;
                if (CoopTrace.Every("wobj.carry.deny", 2f)) Log("carry denied: " + __instance.name + " is already carried");
                return false;
            }
        }

        // T-144: a checkpoint reload (wipe, or a guest reloading the host's checkpoint) puts the game's TriggerOnce triggers
        // back to their saved Active state, so this PC's one-shot record goes too: the next pass fires on every PC again.
        // Test-only mutation ORICOOP_MUTATE_WOBJ_KEEP_FIRED=1 keeps the record (pre-T-144) and must fail R3's check.
        static readonly bool s_mutateKeepFired = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WOBJ_KEEP_FIRED") == "1";
        public static int OneShotResets;
        [HarmonyPatch(typeof(RestoreCheckpointController), "RestoreCheckpoint")]
        static class RestoreForgetsOneShots
        {
            static void Postfix()
            {
                if (s_mutateKeepFired) return;
                if (s_firedOnce.Count > 0) Log("checkpoint restored: forget " + s_firedOnce.Count + " fired one-shot trigger(s)");
                s_firedOnce.CheckpointRestored(); OneShotResets++;
            }
        }

        // Falling rocks and other hazard triggers: PlayerCollisionTrigger only sees Characters.Current (the local Ori;
        // PlayerCollisionTrigger.cs:28-47), so a rock fell only on the PC whose own Ori passed. The passer's PC fires it and
        // sends message 75; a puppet's touch (OnTriggerEnter in its context) is dropped.
        [HarmonyPatch(typeof(Trigger), "DoTrigger")]
        static class HazardTrigger
        {
            static bool Prefix(Trigger __instance)
            {
                PlayerCollisionTrigger t = __instance as PlayerCollisionTrigger;
                if (t == null || !Online || s_applying || !t.Active) return true;
                int id = IdOf(t);
                if (!Replicable(t, id)) return true;
                if (PuppetCause) { PuppetSuppressed++; return false; }
                // Send only what Trigger.DoTrigger (Trigger.cs:29) will really start here; the partner skips the condition.
                if (t.DontTriggerWhileRunning && t.ActionToRun is ActionWithDuration && ((ActionWithDuration)t.ActionToRun).IsPerforming) return true;
                if (t.Condition != null && !t.Condition.Validate(null)) return true;
                if (t.TriggerOnce) { if (!s_firedOnce.TryFire(id)) return true; }
                SendEvent(KTrigger, id, null, "trigger " + t.name);
                return true;
            }
        }
    }
}
