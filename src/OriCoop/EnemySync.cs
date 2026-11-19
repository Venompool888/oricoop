using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Core;
using Game;
using HarmonyLib;
using UnityEngine;
using OriCoop.Enemies;
using OriCoop.Seams;
using OriCoop.Authority;

namespace OriCoop
{
    // E3 protocol: 30 presence, 31 authority, 32 frozen handoff, 33 snapshots,
    // 34 lifecycle, 35 symbols, 36 damage, 37 projectile hit, 38 projectile spawn.
    // P2P uses the legacy arbiter; C-server uses the granted lease owner and epoch.
    // A guest NEVER infers authority from silence.
    public static class EnemySync
    {
        const byte Presence = 30, Authority = 31, Handoff = 32, Snapshots = 33,
            Life = 34, Symbols = 35, Hit = 36, OriHit = 37, Shot = 38, EnemyFx = 67;
        const float Budget = 6144f;
        sealed class State : EnemyState
        {
            public new Vector3 Position; public Vector2 Velocity;
        }
        sealed class Actor : EnemyAuthority
        {
            public string Key; public MoonGuid Scene, Id; public Vector3 Origin;
            public string SceneName = "", ClassName = "", LastRemoval = "", LastDeath = "";
            public RespawningPlaceholder Placeholder; public Enemy Entity;
            public GameObject KamikazeExplosion; public float KamikazeDamage;
            public int EntityInstanceId; public fsm.StateMachine Machine;
            public State Latest = new State(); public byte[] RestoreController;
            public float Priority, LastSent, Arrival, DeathTime = -1, NextTrace;
            public float RenderTick;
            public bool TestSettings, TestRespawnOnScreen; public float TestRespawnTime, TestMinDistance;
            public ulong LastHit; public readonly List<State> Buffer = new List<State>();
            public readonly List<Body> Bodies = new List<Body>();
            public readonly HashSet<ulong> Hits = new HashSet<ulong>();
            public readonly HashSet<ulong> Applied = new HashSet<ulong>();
        }
        sealed class Body { public Rigidbody Rigidbody; public bool Kinematic, Gravity; }
        sealed class DamageStamp { public ulong Id; public bool Puppet; }
        sealed class PendingHit { public Actor Actor; public uint Generation; public ulong Id; public Damage Damage; public bool BindGeneration; }
        sealed class PendingShot { public Actor Actor; public Projectile Projectile; public GameObject Prefab; }
        public sealed class ProjectileIdentity : MonoBehaviour { public ulong Id; public bool Cosmetic; public float Expires; }
        public sealed class PuppetAttack : MonoBehaviour { public bool IsPuppet; }
        sealed class Call { public Actor Previous; public bool Attack, PuppetAttack, Entered; }
        sealed class HitObservation { public Actor Actor; public float Before, Amount; public DamageType Type; public ulong Id; }

        static readonly Dictionary<string, Actor> s_actors = new Dictionary<string, Actor>();
        static readonly Dictionary<int, Actor> s_entities = new Dictionary<int, Actor>();
        static readonly Dictionary<fsm.StateMachine, Actor> s_machines = new Dictionary<fsm.StateMachine, Actor>();
        static readonly Dictionary<Damage, DamageStamp> s_damage = new Dictionary<Damage, DamageStamp>();
        static readonly Queue<Damage> s_damageOrder = new Queue<Damage>();
        static readonly Dictionary<string, ushort> s_symbols = new Dictionary<string, ushort>();
        static readonly Dictionary<uint, string> s_remoteSymbols = new Dictionary<uint, string>();
        static readonly Dictionary<string, TextureAnimationWithTransitions> s_animations = new Dictionary<string, TextureAnimationWithTransitions>();
        static readonly Dictionary<string, GameObject> s_prefabs = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, GameObject> s_fxPrefabs = new Dictionary<string, GameObject>();
        static readonly FieldInfo s_shouldStomp = AccessTools.Field(typeof(JumperEnemy), "m_shouldStomp");
        static readonly FieldInfo s_horizontalPlayer = AccessTools.Field(typeof(SeeSeinHorizontalyTrigger), "m_playerTransform");
        static readonly HashSet<string> s_warned = new HashSet<string>();
        static readonly HashSet<MethodBase> s_patched = new HashSet<MethodBase>();
        static readonly List<PendingShot> s_shots = new List<PendingShot>();
        static readonly List<PendingHit> s_pendingHits = new List<PendingHit>();
        static readonly VictimHits s_victimHits = new VictimHits();
        static readonly List<ProjectileIdentity> s_copies = new List<ProjectileIdentity>();
        static readonly Dictionary<SceneManagerScene, bool> s_pins = new Dictionary<SceneManagerScene, bool>();
        static IEnemyHost s_host;
        static Actor s_active; static int s_attackDepth; static bool s_applying, s_spawning, s_session, s_restoringController, s_puppetDeath;
        static float s_fxWindow; static int s_fxCount; static DifficultyMode s_difficulty;
        static uint s_sequence; static float s_scanAt, s_streamAt, s_logAt, s_tokenTime, s_tokens;
        static readonly List<Vector3> s_pinPositions = new List<Vector3>();
        public static long BytesSent, BytesReceived, EstimatedWireBytesSent;
        public static int Unmirrored, KeptScenes, UnresolvedProjectiles;
        public static int DeathEffects, OwnerDeathEffects, FxSent, FxReceived;
        public static float StreamMilliseconds;
        static bool Active { get { return Services.Net.Enabled && Services.Net.Connected && s_host.RemoteHello && s_session; } }
        static bool Mine(Actor a) { RefreshOwnership(a); return a.Mine(s_host.LocalSeat); }
        static string Key(MoonGuid scene, MoonGuid id) { return scene.ToString() + ":" + id.ToString(); }
        static ulong NextId() { return ((ulong)s_host.LocalSeat << 32) | ++s_sequence; }
        static float Now { get { return Services.Clock.RealTime; } }
        static SeinCharacter Local { get { return s_host.Local; } }

        public static void Init()
        {
            s_host = new GameEnemyHost();
            Services.Net.On(Presence, ReadPresence); Services.Net.On(Authority, ReadAuthority); Services.Net.On(Handoff, ReadHandoff);
            Services.Net.On(Snapshots, ReadSnapshots); Services.Net.On(Life, ReadLife); Services.Net.On(Symbols, ReadSymbol);
            Services.Net.On(Hit, ReadHit); Services.Net.On(OriHit, ReadOriHit); Services.Net.On(Shot, ReadShot); Services.Net.On(EnemyFx, ReadEnemyFx);
            Services.Net.Forward(Snapshots, Life, Symbols, Shot);
            Services.Net.SessionStarted += Begin; Services.Net.SessionEnded += Reset;
            Services.Net.PlayerLeft += RemoveSeat;
            Services.Net.PlayerJoined += delegate(int seat)
            {
                foreach (KeyValuePair<string, ushort> symbol in s_symbols)
                    Send(Symbols, true, w => { w.Write(symbol.Value); w.Write(symbol.Key); });
            };
            // Patch only game simulation callbacks. Render/animation and damage dealers keep ticking.
            MethodInfo guard = typeof(EnemySync).GetMethod("SimulationPrefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo finish = typeof(EnemySync).GetMethod("SimulationFinalizer", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo start = typeof(EnemySync).GetMethod("StartPrefix", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo startFinish = typeof(EnemySync).GetMethod("ContextFinalizer", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (Type t in typeof(Enemy).Assembly.GetTypes())
            {
                bool actor = typeof(Entity).IsAssignableFrom(t) || typeof(EntityController).IsAssignableFrom(t)
                    || typeof(PlatformMovement).IsAssignableFrom(t) || typeof(FlyMovement).IsAssignableFrom(t)
                    || typeof(RigidbodyMovement).IsAssignableFrom(t) || typeof(TraceGroundMovement).IsAssignableFrom(t)
                    || typeof(RollingMovement).IsAssignableFrom(t);
                bool attack = t == typeof(SpiritFlameProjectile) || t == typeof(ChargeFlameBurst) || t == typeof(GrenadeBurst)
                    || t.Name.IndexOf("SpiritGrenade", StringComparison.Ordinal) == 0;
                if (!actor && !attack) continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (actor && m.Name == "Start" && !m.IsAbstract && !m.IsGenericMethodDefinition && s_patched.Add(m))
                    {
                        HarmonyMethod context = new HarmonyMethod(start); context.priority = Priority.First;
                        s_host.Patch(m, context, new HarmonyMethod(startFinish)); continue;
                    }
                    bool callback = m.Name == "FixedUpdate" || m.Name == "Update" || m.Name == "LateUpdate"
                        || (t == typeof(KamikazeSootEnemy) && m.Name == "SelfDestruct")
                        || (actor && (m.Name == "OnRecieveDamage" || m.Name == "OnAnimationEnd" || m.Name == "OnDamageDealt" || m.Name == "OnCollisionEnter" || m.Name == "OnCollisionStay" || m.Name == "OnCollisionExit"))
                        || (attack && (m.Name == "Start" || m.Name == "DealDamage" || m.Name == "Detonate"
                            || m.Name == "Explode" || m.Name == "OnDamageDealt" || m.Name == "OnRecieveDamage"));
                    if (!callback || m.IsAbstract || m.IsGenericMethodDefinition || !s_patched.Add(m)) continue;
                    HarmonyMethod first = new HarmonyMethod(guard); first.priority = Priority.First;
                    s_host.Patch(m, first, new HarmonyMethod(finish));
                }
            }
        }
        static void Begin()
        {
            Reset();
            EnemyAuthority.StartSession(NetRoute.ServerMode);
            s_session = true; s_tokens = Budget / 4f; s_tokenTime = Now;
            if (DifficultyController.Instance != null)
            { s_difficulty = DifficultyController.Instance.Difficulty; Services.Log.Info("E3T difficulty=" + s_difficulty); }
        }
        static void Reset()
        {
            foreach (Actor a in s_actors.Values) { EnemyAuthority.Policy.Release(a.Key); RestoreBodies(a); }
            EnemyAuthority.Policy = null;
            foreach (ProjectileIdentity p in s_copies) if (p != null) InstantiateUtility.Destroy(p.gameObject);
            ReleasePins(); s_actors.Clear(); s_entities.Clear(); s_machines.Clear(); s_damage.Clear(); s_damageOrder.Clear();
            s_symbols.Clear(); s_remoteSymbols.Clear(); s_pendingHits.Clear(); s_shots.Clear(); s_victimHits.Clear(); s_copies.Clear();
            s_session = false; s_active = null; s_attackDepth = 0; s_spawning = s_applying = s_puppetDeath = false;
            s_fxPrefabs.Clear(); s_fxWindow = 0; s_fxCount = 0; DeathEffects = OwnerDeathEffects = FxSent = FxReceived = 0;
            s_scanAt = s_streamAt = s_logAt = 0; BytesSent = BytesReceived = EstimatedWireBytesSent = 0; Unmirrored = KeptScenes = UnresolvedProjectiles = 0;
        }
        static void Send(byte type, bool reliable, Action<BinaryWriter> body, int seat = 0)
        {
            Action<BinaryWriter> write = delegate(BinaryWriter w) {
                long before = w.BaseStream.Position; body(w); long bytes = w.BaseStream.Position - before + 1; BytesSent += bytes;
                // NetPeer data header + message framing; retransmits and ACK-only datagrams are transport stats.
                EstimatedWireBytesSent += bytes + (reliable ? 32 * Math.Max(1, (bytes + 999) / 1000) : 22);
            };
            if (seat == 0) Services.Net.Send(type, reliable, write); else Services.Net.SendTo(seat, type, reliable, write);
        }
        static Actor ReadId(BinaryReader r)
        {
            MoonGuid scene = new MoonGuid(r), id = new MoonGuid(r); string key = Key(scene, id); Actor a;
            if (!s_actors.TryGetValue(key, out a)) { a = new Actor(); a.Key = key; a.Scene = scene; a.Id = id; s_actors.Add(key, a); }
            RefreshOwnership(a);
            return a;
        }
        static void WriteId(BinaryWriter w, Actor a) { w.Write(a.Scene.ToByteArray()); w.Write(a.Id.ToByteArray()); }
        static Vector3 ReadVector(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static ushort Symbol(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0; ushort id;
            if (s_symbols.TryGetValue(value, out id)) return id;
            if (s_symbols.Count >= 65534) return 0;
            id = (ushort)(s_symbols.Count + 1); s_symbols.Add(value, id);
            Send(Symbols, true, delegate(BinaryWriter w) { w.Write(id); w.Write(value); }); return id;
        }
        static uint SymbolKey(int seat, ushort id) { return ((uint)seat << 16) | id; }
        static void ReadSymbol(BinaryReader r) { ushort id = r.ReadUInt16(); string value = r.ReadString(); if (value.Length > 1024) throw new InvalidDataException("E3 symbol too long"); s_remoteSymbols[SymbolKey(Services.Net.Sender, id)] = value; }
        static string RemoteSymbol(ushort id) { string s; return s_remoteSymbols.TryGetValue(SymbolKey(Services.Net.Sender, id), out s) ? s : ""; }
        static void Warn(string s) { if (s_warned.Add(s)) Services.Log.Warn("E3 " + s); }
        static void Trace(string evt, Actor a, string extra)
        {
            Probe p = Inspect(a.Key); string state = p.StateName ?? "";
            int dot = state.LastIndexOf('.'); if (dot >= 0) state = state.Substring(dot + 1);
            Services.Log.Info("E3T " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Services.Clock.RealTime.ToString("F2")
                + " seat=" + s_host.LocalSeat + " " + evt + " key=" + a.Key.Substring(Math.Max(0, a.Key.Length - 8))
                + " class=" + p.ClassName + " pos=" + p.Position.ToString("F1") + " hp=" + p.Health
                + " owner=P" + a.OwnerSeat + " epoch=" + a.Epoch + " generation=" + a.Generation
                + " state=" + state + " " + extra);
            // Same events on the shared coop-trace timeline; id= is the E3 key on both ends so merge-coop-logs pairs them.
            if (evt != "state" || s_host.TraceSampleDue())
                s_host.TraceEvent("e3." + evt.Replace(' ', '.'), "id", a.Key, "class", p.ClassName, "pos", p.Position, "hp", p.Health,
                    "owner", "P" + a.OwnerSeat, "mine", Mine(a), "epoch", a.Epoch, "gen", a.Generation, "state", state, "info", extra);
        }

        static readonly Type[] MirroredTypes = { typeof(JumperEnemy), typeof(RammingEnemy), typeof(SpitterEnemy),
            typeof(AcidSlugEnemy), typeof(StarSlugEnemy), typeof(DropSlugEnemy), typeof(DashOwlEnemy),
            typeof(FishEnemy), typeof(KamikazeSootEnemy) };
        // Owner-only classes run a local copy on each peer, with nearest-player targeting and death bursts:
        // ShootingSpider has rope joints; FloatingRockLaserEnemy/TurretEnemy have three animators and a laser;
        // MortarWormEnemy/JumpShootShark have hidden groups and private shot flags; ArmouredRammingEnemy
        // replaces itself with RammingEnemy. SwarmEnemy runtime children have no baked identity and are excluded.
        // Other classes remain opt-in. Jumper stomp state is included in full handoffs.
        static bool Supported(Enemy e)
        {
            if (e.Animation == null || e.Animation.SpriteMirror == null || e.Controller == null) return false;
            return Array.IndexOf(MirroredTypes, e.GetType()) >= 0;
        }
        static bool EnemyPlaceholder(RespawningPlaceholder p)
        {
            string n = p.GetType().Name;
            return (n.IndexOf("EnemyPlaceholder", StringComparison.Ordinal) >= 0 || p is JumpShootSharkPlaceholder || p is ShootingSpiderPlaceholder)
                && n.IndexOf("Swarm", StringComparison.Ordinal) < 0;
        }
        static Actor Track(RespawningPlaceholder p)
        {
            if (p == null || p.MoonGuid == null || !EnemyPlaceholder(p)) return null;
            SceneRoot root = SceneRoot.FindFromTransform(p.transform); if (root == null || root.MetaData == null) return null;
            string key = Key(root.MetaData.SceneMoonGuid, p.MoonGuid); Actor a;
            if (!s_actors.TryGetValue(key, out a))
            {
                a = new Actor(); a.Key = key; a.Id = new MoonGuid(p.MoonGuid); a.Scene = new MoonGuid(root.MetaData.SceneMoonGuid);
                a.Known = Services.Net.IsHost; a.OwnerSeat = 1; a.Latest.Position = p.Position; s_actors.Add(key, a);
            }
            a.Placeholder = p; a.Origin = p.Position; a.Loaded = true; a.SceneName = root.MetaData.SceneName;
            if (EnemyAuthority.Policy.UsesLeases)
            {
                Rect bounds = root.MetaData.SceneBounds;
                if (bounds.width > 0 || bounds.height > 0) a.SceneRadius = bounds.size.magnitude * 0.5f;
            }
            // ReadId can create this record before the first local scan/Bind. A loaded host
            // placeholder establishes initial host authority before any body can be frozen.
            if (!EnemyAuthority.Policy.UsesLeases && Services.Net.IsHost && !a.Known) { a.Known = true; a.OwnerSeat = 1; }
            RefreshOwnership(a);
            Bind(a); return a;
        }
        static Actor For(Component component)
        {
            if (component == null) return null;
            EntityDamageReciever dr = component as EntityDamageReciever;
            EntityController ec = component as EntityController;
            Enemy e = dr != null ? dr.Entity as Enemy : ec != null ? ec.Entity as Enemy : component as Enemy;
            if (e == null) e = component.GetComponentInParent<Enemy>();
            if (e == null) return null; Actor a;
            if (s_entities.TryGetValue(e.GetInstanceID(), out a) && a.Entity == e) return a;
            foreach (RespawningPlaceholder p in RespawningPlaceholder.All) if (p != null && p.CurrentEntity == e) return Track(p);
            return null;
        }
        static void Bind(Actor a)
        {
            Enemy e = a.Placeholder == null ? null : a.Placeholder.CurrentEntity as Enemy;
            if (object.ReferenceEquals(a.Entity, e)) return;
            RestoreBodies(a);
            if (a.EntityInstanceId != 0) s_entities.Remove(a.EntityInstanceId);
            if (a.Machine != null) s_machines.Remove(a.Machine);
            a.EntityInstanceId = 0; a.Machine = null;
            a.Entity = e;
            if (e == null) return;
            a.ClassName = e.GetType().Name;
            KamikazeSootEnemy kamikaze = e as KamikazeSootEnemy;
            a.KamikazeExplosion = kamikaze == null ? null : kamikaze.KamikazeExplosion;
            a.KamikazeDamage = kamikaze == null ? 0f : kamikaze.Settings.ExplosionDamage;
            a.EntityInstanceId = e.GetInstanceID(); s_entities[a.EntityInstanceId] = a;
            if (e.Controller != null) { a.Machine = e.Controller.StateMachine; s_machines[a.Machine] = a; }
            if (!Supported(e)) Warn("owner-only class=" + e.GetType().Name + " (outside mirrored class/variant allowlist or missing animation/controller)");
            if (Mine(a) && !s_spawning)
            {
                a.Generation++; a.Hits.Clear(); a.Applied.Clear(); a.Latest = Capture(a, true);
                SendLife(a, a.Generation == 1 ? (byte)1 : (byte)3);
            }
            else if (Supported(e)) FreezeBodies(a);
            Trace("spawn", a, "mirrored=" + Supported(e));
        }
        static void FreezeBodies(Actor a)
        {
            if (a.Entity == null || a.Bodies.Count > 0) return;
            foreach (Rigidbody rb in a.Entity.GetComponentsInChildren<Rigidbody>(true))
            {
                Body b = new Body(); b.Rigidbody = rb; b.Kinematic = rb.isKinematic; b.Gravity = rb.useGravity; a.Bodies.Add(b);
                rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.useGravity = false; rb.isKinematic = true;
            }
        }
        static void RestoreBodies(Actor a)
        {
            foreach (Body b in a.Bodies) if (b.Rigidbody != null) { b.Rigidbody.isKinematic = b.Kinematic; b.Rigidbody.useGravity = b.Gravity; }
            a.Bodies.Clear();
        }
        static bool SimulationPrefix(Component __instance, out Call __state)
        {
            __state = new Call(); __state.Previous = s_active;
            if (!Active) return true;
            __state.Attack = __instance is SpiritFlameProjectile || __instance is ChargeFlameBurst || __instance is GrenadeBurst
                || (__instance != null && __instance.GetType().Name.IndexOf("SpiritGrenade", StringComparison.Ordinal) == 0);
            PuppetAttack tag = __instance == null ? null : __instance.GetComponentInParent<PuppetAttack>();
            if (tag != null && tag.IsPuppet) { s_attackDepth++; __state.PuppetAttack = true; }
            Actor a = For(__instance);
            if (a == null) return true;
            s_active = a;
            bool run = s_applying || a.Entity == null || !Supported(a.Entity) || (Mine(a) && a.RestoreController == null);
            if (run && !__state.Attack && a.Entity != null && !s_host.Inside && s_host.P2IsNearer(a.Entity.Position))
            { s_host.Enter(s_host.NearestPuppet(a.Entity.Position)); __state.Entered = true; }
            return run;
        }
        static void StartPrefix(Component __instance, out bool __state)
        {
            __state = false; if (!Active) return; Actor a = For(__instance);
            if (a != null && a.Entity != null && !s_host.Inside && s_host.P2IsNearer(a.Entity.Position))
            { s_host.Enter(s_host.NearestPuppet(a.Entity.Position)); __state = true; }
        }
        static Exception ContextFinalizer(Exception __exception, bool __state)
        { if (__state) s_host.Exit(); return __exception; }
        [HarmonyPatch(typeof(fsm.StateMachine), "ChangeState")]
        static class PuppetStateMachine
        {
            static bool Prefix(fsm.StateMachine __instance, fsm.IState state)
            {
                Actor a;
                if (!Active || !s_machines.TryGetValue(__instance, out a) || a.Entity == null || !Supported(a.Entity)) return true;
                if (!s_restoringController && (s_applying || (Mine(a) && a.RestoreController == null))) return true;
                // Register real state objects in Start, without attacking from the puppet's OnEnter.
                __instance.CurrentState = state; __instance.CurrentStateTime = 0; return false;
            }
        }
        static Exception SimulationFinalizer(Exception __exception, Call __state)
        {
            if (__state != null)
            {
                if (__state.Entered) s_host.Exit();
                s_active = __state.Previous; if (__state.PuppetAttack) s_attackDepth--;
            }
            return __exception;
        }
        static State Capture(Actor a, bool controller)
        {
            State s = new State(); s.Tick = s_host.LocalTick; s.Generation = a.Generation;
            if (controller) { s.Applied = new ulong[a.Applied.Count]; a.Applied.CopyTo(s.Applied); }
            s.Position = a.Latest.Position; s.Health = a.Latest.Health;
            RespawningPlaceholder p = a.Placeholder;
            if (p != null)
            {
                Archive ar = new Archive(); ar.WriteMode(); p.Serialize(ar); ar.ReadMode();
                BinaryReader r = new BinaryReader(ar.MemoryStream); s.Killed = r.ReadBoolean(); s.NeedsRespawn = r.ReadBoolean();
                s.Respawn = Mathf.Max(0, r.ReadSingle() - GameController.Instance.GameTime);
            }
            Enemy e = a.Entity;
            s.Alive = e != null && e.DamageReciever != null && !e.DamageReciever.NoHealthLeft && !s.Killed;
            if (e != null)
            {
                if (controller && e is JumperEnemy) s.ShouldStomp = (bool)s_shouldStomp.GetValue(e);
                FishEnemy fish = e as FishEnemy; DashOwlEnemy owl = e as DashOwlEnemy;
                // T-027: surface crawlers (star slugs; TraceGroundMovement) turn the whole body to the wall/ceiling they walk on.
                Transform visual = fish != null ? fish.Rotation : owl != null && owl.SpriteRotation != null ? owl.SpriteRotation.transform
                    : e.GetComponent<TraceGroundMovement>() != null ? e.transform : null;
                if (visual != null) { s.HasVisualAngle = true; s.VisualAngle = visual.eulerAngles.z; }
                s.Position = e.Position; s.Left = e.Animation != null && e.Animation.SpriteMirror != null && e.FaceLeft; s.Health = e.DamageReciever == null ? 0 : e.DamageReciever.Health;
                s.DealerActive = e.DamageDealer != null && e.DamageDealer.Activated; s.TargetActive = e.Targetting != null && e.Targetting.Activated;
                PlatformMovement ground = e.GetComponent<PlatformMovement>(); FlyMovement fly = e.GetComponent<FlyMovement>(); Rigidbody rb = e.GetComponent<Rigidbody>();
                RollingMovement rolling = e.GetComponent<RollingMovement>();
                TraceGroundMovement trace = e.GetComponent<TraceGroundMovement>();
                s.Velocity = rolling != null && rolling.enabled ? (Vector2)rolling.Speed : ground != null ? ground.WorldSpeed : fly != null ? fly.Velocity : trace != null ? (Vector2)(trace.Right * trace.Speed) : rb != null ? (Vector2)rb.velocity : Vector2.zero;
                if (controller && trace != null) { Archive movement = new Archive(); movement.WriteMode(); trace.Serialize(movement); s.TraceMovement = movement.MemoryStream.ToArray(); }
                if (e.Controller != null && e.Controller.StateMachine.CurrentState != null)
                {
                    s.StateName = StateName(e); s.StateTime = e.Controller.StateMachine.CurrentStateTime;
                    Archive ar = new Archive(); ar.WriteMode(); e.Controller.Serialize(ar); byte[] bytes = ar.MemoryStream.ToArray();
                    if (bytes.Length >= 4) s.StateName += "#" + BitConverter.ToInt32(bytes, 0);
                    if (controller) s.Controller = bytes;
                }
                if (e.Animation != null && e.Animation.Animator != null)
                {
                    TextureAnimationWithTransitions anim = e.Animation.Animator.CurrentTextureAnimationTransitions;
                    if (anim != null) { s.Animation = AnimationKey(anim); s_animations[s.Animation] = anim; }
                    s.AnimationTime = e.Animation.Animator.CurrentAnimationTime;
                }
            }
            return s;
        }
        static EnemyVector ToWire(Vector3 v) { return new EnemyVector(v.x, v.y, v.z); }
        static Vector3 FromWire(EnemyVector v) { return new Vector3(v.X, v.Y, v.Z); }
        static readonly Func<string, ushort> s_wireSymbol = Symbol;
        static readonly Func<ushort, string> s_wireRemoteSymbol = RemoteSymbol;
        static byte Flags(State s) { return EnemyWire.Flags(s); }
        static void WriteState(BinaryWriter w, Actor a, State s, bool full)
        {
            EnemyState wire = s; wire.Position = ToWire(s.Position); wire.VelocityX = s.Velocity.x; wire.VelocityY = s.Velocity.y;
            EnemyWire.WriteState(w, ToWire(a.Origin), wire, full, s_wireSymbol);
        }
        static State ReadState(BinaryReader r, Actor a, bool full)
        {
            State s = new State(); EnemyWire.ReadState(r, ToWire(a.Origin), full, s_wireRemoteSymbol, s);
            s.Position = FromWire(((EnemyState)s).Position); s.Velocity = new Vector2(s.VelocityX, s.VelocityY); return s;
        }
        static void SetPlaceholder(Actor a, State s)
        {
            if (a.Placeholder == null) return;
            Archive ar = new Archive(); ar.WriteMode(); BinaryWriter w = new BinaryWriter(ar.MemoryStream);
            w.Write(s.Killed); w.Write(s.NeedsRespawn); w.Write(GameController.Instance.GameTime + s.Respawn); w.Flush(); ar.ReadMode();
            a.Placeholder.Serialize(ar);
        }
        static void ReceiveState(Actor a, State s, bool reset, bool newSource = false)
        {
            if (!reset && !AcceptState(a.Latest, s, newSource)) return;
            if (reset || newSource || s.Generation != a.Generation) a.Buffer.Clear();
            a.ExpectFirstSnapshot = false;
            if (s.Generation != a.Generation) { a.Hits.Clear(); a.Applied.Clear(); }
            if (reset) foreach (ulong id in s.Applied) a.Applied.Add(id);
            a.Generation = s.Generation; a.Latest = s; a.Arrival = Now;
            a.Buffer.Add(s); while (a.Buffer.Count > 12) a.Buffer.RemoveAt(0);
            if (s.Killed && a.DeathTime < 0) a.DeathTime = Now;
            if (!s.Killed) a.DeathTime = -1;
            if (reset) a.RestoreController = s.Controller;
            if (a.Placeholder == null || Mine(a)) return;
            SetPlaceholder(a, s);
            if (s.Killed)
            {
                if (a.Entity != null && a.Entity.gameObject.activeInHierarchy) PlayPuppetDeath(a);
                a.Placeholder.DestroyCurrentInstance(); Bind(a);
            }
            else if (s.Alive && a.Placeholder.CurrentEntity == null)
            {
                s_spawning = true; try { a.Placeholder.Spawn(); } finally { s_spawning = false; }
                Bind(a);
            }
        }
        static void PlayPuppetDeath(Actor a)
        {
            bool applying = s_applying, puppetDeath = s_puppetDeath;
            try
            {
                s_puppetDeath = true; s_applying = true;
                DamageReciever receiver = a.Entity.DamageReciever;
                bool hasAction = receiver != null && receiver.DeathAction != null;
                Trace("puppet death-fx", a, "DeathAction=" + hasAction);
                if (receiver != null)
                {
                    receiver.Health = 0;
                    Damage d = new Damage(1f, Vector2.zero, a.Entity.Position, DamageType.SpiritFlame, Local == null ? null : Local.gameObject);
                    if (hasAction) receiver.DeathAction.Perform(new DamageContext(d));
                    DeathEffects++;
                }
            }
            catch (Exception e) { Services.Log.Warn("E3 puppet death effect failed: " + e); }
            finally { s_puppetDeath = puppetDeath; s_applying = applying; }
        }
        [HarmonyPatch(typeof(OrbSpawner), "SpawnOrbs")]
        static class PuppetDeathLoot { static bool Prefix() { return !s_puppetDeath; } }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool AcceptState(State prior, State next, bool newSource = false)
        {
            return EnemyOwnership.AcceptState(prior.Generation, prior.Tick, prior.Killed, next.Generation, next.Tick, next.Killed, newSource);
        }

        static void Announce(Actor a)
        {
            // Only the host reads presence reports (every other receiver dropped them after creating an entry, which made
            // every guest track, and report, every enemy the host ever saw). (T-147)
            if (Services.Net.IsHost) return;
            Send(Presence, true, delegate(BinaryWriter w) { WriteId(w, a); WriteVector(w, a.Origin); w.Write(a.Loaded); });
            a.PresenceSent(delegate { return Now; });
        }
        static void ReadPresence(BinaryReader r)
        {
            Actor a = ReadId(r); a.Origin = ReadVector(r); bool loaded = r.ReadBoolean();
            if (!Services.Net.IsHost) return;
            // Position alone never grants authority: the chosen seat must report the scene loaded.
            // T-147: no Scan per report (Tick scans every 0.25 s), and an unchanged owner is not re-granted to everyone.
            int sender = Services.Net.Sender;
            int action = a.OnPresence(sender, loaded, delegate { return PolicyDesiredOwner(a); });
            if (action == 1) Grant(a, false, sender);
            else if (action == 2) Decide(a);
        }
        static void Decide(Actor a)
        {
            if (EnemyAuthority.Policy.UsesLeases) { RefreshOwnership(a); return; }
            if (!Services.Net.IsHost || a.PendingTakeover) return;
            int desired = PolicyDesiredOwner(a);
            int decision = a.BeginDecision(desired, s_host.LocalSeat);
            if (decision == 0) { Grant(a, false); return; }
            if (decision == 1)
            {
                Send(Authority, true, delegate(BinaryWriter w) { WriteId(w, a); EnemyWire.WriteAuthority(w, 0, a.PendingEpoch, desired); }, a.OwnerSeat);
            }
            else
            {
                a.Latest = a.Loaded ? Capture(a, true) : a.Latest;
                a.CompleteLocalTransfer(delegate { return Now; }); a.Buffer.Clear();
                FreezeBodies(a); Grant(a, true); a.Frozen = false;
            }
            Trace("owner change", a, "Decide desired=P" + desired);
        }
        static int PolicyDesiredOwner(Actor a)
        { return EnemyAuthority.Policy.DesiredOwner(a.Key, delegate { return DesiredOwner(a); }); }
        static void RefreshOwnership(Actor a)
        {
            if (!a.RefreshPolicy(a.Key)) return;
            a.Buffer.Clear();
            if (a.NeedsInitialLeaseState(s_host.LocalSeat))
            {
                if (a.SeedInitialLease(s_host.LocalSeat, a.Entity != null, delegate {
                    a.Hits.Clear(); a.Applied.Clear(); a.Latest = Capture(a, true); SendLife(a, 1);
                })) RestoreBodies(a);
                // With no entity yet, preserve native checkpoint/respawn data until Bind captures it.
                return;
            }
            if (a.Mine(s_host.LocalSeat)) Resume(a); else FreezeBodies(a);
        }
        static float OwnerDistance(Actor a, int seat, Vector3 position)
        {
            if (seat == s_host.LocalSeat) return a.Loaded && Local != null ? Vector3.Distance(position, Local.Position) : float.PositiveInfinity;
            Vector3 remote;
            return a.LoadedSeats.Contains(seat) && s_host.RemotePos(seat, out remote) ? Vector3.Distance(position, remote) : float.PositiveInfinity;
        }
        static int DesiredOwner(Actor a)
        {
            int remoteCount = s_host.RemoteCount;
            if (remoteCount <= 1)
                return EnemyOwnership.DesiredOwner(remoteCount, a.Loaded, a.LoadedSeats, a.OwnerSeat, 1, null, a.OwnerChangedAt, null, null, null);
            return DesiredMultiOwner(a, remoteCount);
        }
        static int DesiredMultiOwner(Actor a, int remoteCount)
        {
            Vector3 position = Mine(a) && a.Entity != null ? a.Entity.Position : a.Latest.Position;
            return EnemyOwnership.DesiredOwner(remoteCount, a.Loaded, a.LoadedSeats, a.OwnerSeat, s_host.LocalSeat,
                delegate { return Now; }, a.OwnerChangedAt, delegate(int seat) { return OwnerDistance(a, seat, position); },
                delegate { return Local != null ? Vector3.Distance(position, Local.Position) : float.PositiveInfinity; }, RemoteDistances(a, position));
        }
        static IEnumerable<KeyValuePair<int, float>> RemoteDistances(Actor a, Vector3 position)
        {
            foreach (KeyValuePair<int, Vector3> remote in s_host.RemotePositions)
                if (a.LoadedSeats.Contains(remote.Key))
                    yield return new KeyValuePair<int, float>(remote.Key, Vector3.Distance(position, remote.Value));
        }
        static void Grant(Actor a, bool transfer, int seat = 0)
        {
            State s = Mine(a) && a.Placeholder != null ? Capture(a, true) : a.Latest;
            Send(Authority, true, delegate(BinaryWriter w) {
                WriteId(w, a); EnemyWire.WriteAuthority(w, 1, a.Epoch, a.OwnerSeat); WriteVector(w, a.Origin); WriteState(w, a, s, true);
            }, seat);
        }
        static void ReadAuthority(BinaryReader r)
        {
            Actor a = ReadId(r); EnemyTransfer transfer = EnemyWire.ReadAuthority(r);
            byte operation = transfer.Operation; uint epoch = transfer.Epoch; int owner = transfer.Owner;
            if (operation == 0)
            {
                if (!a.CanFreeze(Services.Net.Sender, s_host.LocalSeat, epoch)) return;
                State s = a.Placeholder != null ? Capture(a, true) : a.Latest;
                a.Frozen = true; FreezeBodies(a);
                Send(Handoff, true, delegate(BinaryWriter w) { WriteId(w, a); EnemyWire.WriteHandoff(w, epoch, owner); WriteState(w, a, s, true); }); return;
            }
            a.Origin = ReadVector(r); State state = ReadState(r, a, true);
            if (!a.CanAcceptGrant(Services.Net.IsHost, Services.Net.Sender, epoch)) return;
            bool changed = a.GrantChanged(owner, epoch);
            if (!changed && owner == s_host.LocalSeat) return; // A presence refresh cannot rewind the owner's simulation.
            a.ApplyGrant(owner, epoch);
            ReceiveState(a, state, changed);
            if (changed && !Mine(a)) a.ExpectFirstSnapshot = true;
            if (Mine(a)) Resume(a); else FreezeBodies(a);
            if (changed) Trace("owner change", a, "ReadAuthority");
        }
        static void ReadHandoff(BinaryReader r)
        {
            Actor a = ReadId(r); EnemyTransfer transfer = EnemyWire.ReadHandoff(r); uint epoch = transfer.Epoch; int owner = transfer.Owner; State s = ReadState(r, a, true);
            if (!a.CanAcceptHandoff(Services.Net.IsHost, Services.Net.Sender, epoch, owner)) return;
            a.CompleteHandoff(owner, epoch, delegate { return Now; });
            ReceiveState(a, s, true); a.Frozen = false;
            if (Mine(a)) Resume(a); else { a.ExpectFirstSnapshot = true; FreezeBodies(a); }
            Grant(a, true);
            Trace("owner change", a, "ReadHandoff");
        }
        static void RemoveSeat(int seat)
        {
            List<uint> symbols = new List<uint>();
            foreach (uint key in s_remoteSymbols.Keys) if ((key >> 16) == seat) symbols.Add(key);
            foreach (uint key in symbols) s_remoteSymbols.Remove(key);
            foreach (Actor a in new List<Actor>(s_actors.Values))
            {
                int left = a.SeatLeft(seat, Services.Net.IsHost, delegate { return Now; });
                if (EnemyAuthority.Policy.UsesLeases) { RefreshOwnership(a); continue; }
                if (!Services.Net.IsHost) continue;
                if (left == 1)
                {
                    ReceiveState(a, a.Latest, true); a.Frozen = false; Resume(a); Grant(a, true);
                }
                else if (left == 2) Grant(a, true);
                if (a.OwnerSeat != PolicyDesiredOwner(a)) Decide(a);
            }
        }
        static void Resume(Actor a)
        {
            SetPlaceholder(a, a.Latest);
            if (a.Placeholder != null && a.Latest.Killed) { a.Placeholder.DestroyCurrentInstance(); Bind(a); }
            else if (a.Placeholder != null && a.Latest.Alive && a.Placeholder.CurrentEntity == null)
            {
                s_spawning = true; try { a.Placeholder.Spawn(); } finally { s_spawning = false; }
                Bind(a);
            }
            Enemy e = a.Entity; if (e == null) return;
            if (a.RestoreController != null && a.RestoreController.Length > 0)
            {
                if (e.Controller == null || e.Controller.StateMachine.CurrentState == null) { FreezeBodies(a); return; }
                Archive ar = new Archive(); ar.MemoryStream.Write(a.RestoreController, 0, a.RestoreController.Length); ar.ReadMode();
                s_applying = true; s_restoringController = true;
                try { e.Controller.Serialize(ar); } finally { s_applying = false; s_restoringController = false; }
            }
            a.RestoreController = null; RestoreBodies(a); ApplyPose(a, a.Latest, a.Latest.Position);
            if (e is JumperEnemy) s_shouldStomp.SetValue(e, a.Latest.ShouldStomp);
            TraceGroundMovement trace = e.GetComponent<TraceGroundMovement>();
            if (trace != null && a.Latest.TraceMovement.Length > 0)
            {
                Archive movement = new Archive(); movement.MemoryStream.Write(a.Latest.TraceMovement, 0, a.Latest.TraceMovement.Length); movement.ReadMode(); trace.Serialize(movement);
            }
            PlatformMovement ground = e.GetComponent<PlatformMovement>(); FlyMovement fly = e.GetComponent<FlyMovement>(); Rigidbody rb = e.GetComponent<Rigidbody>();
            if (ground != null) ground.WorldSpeed = a.Latest.Velocity;
            RollingMovement rolling = e.GetComponent<RollingMovement>(); if (rolling != null) rolling.Speed = a.Latest.Velocity;
            if (fly != null) fly.Velocity = a.Latest.Velocity;
            if (trace != null) trace.Speed = Vector3.Dot(trace.Right, a.Latest.Velocity);
            if (rb != null && !rb.isKinematic) rb.velocity = a.Latest.Velocity;
        }
        static void ReadSnapshots(BinaryReader r)
        {
            long before = r.BaseStream.Position; int count = r.ReadByte();
            if (count > 16) throw new InvalidDataException("E3 snapshot count");
            for (int i = 0; i < count; i++)
            {
                Actor a = ReadId(r); uint epoch = r.ReadUInt32(); State s = ReadState(r, a, false);
                if (a.AcceptOwnerMessage(s_host.LocalSeat, epoch, Services.Net.Sender, true)) ReceiveState(a, s, false, a.ExpectFirstSnapshot);
            }
            BytesReceived += r.BaseStream.Position - before + 1;
        }
        static void SendLife(Actor a, byte kind)
        {
            State s = a.Latest;
            Send(Life, true, delegate(BinaryWriter w) { WriteId(w, a); w.Write(a.Epoch); w.Write(kind); w.Write(a.LastHit); WriteVector(w, a.Origin); WriteState(w, a, s, true); });
        }
        static void ReadLife(BinaryReader r)
        {
            Actor a = ReadId(r); uint epoch = r.ReadUInt32(); byte kind = r.ReadByte(); ulong hit = r.ReadUInt64(); a.Origin = ReadVector(r); State s = ReadState(r, a, true);
            if (!a.AcceptOwnerMessage(s_host.LocalSeat, epoch, Services.Net.Sender, true)) return;
            if (!EnemyOwnership.AcceptLife(a.Generation, a.Latest.Tick, a.Latest.Killed, a.ExpectFirstSnapshot, s.Generation, s.Tick, s.Killed)) return;
            a.LastHit = hit; ReceiveState(a, s, true);
            if (kind == 2) a.DeathTime = Now;
        }
        static string AnimationKey(TextureAnimationWithTransitions a)
        {
            if (a.Animation == null || a.Animation.FrameGuids.Count == 0) return a.name;
            return a.name + "|" + a.Animation.FrameGuids[0].ToString() + "|" + a.Animation.FrameGuids.Count;
        }
        static TextureAnimationWithTransitions Animation(string name)
        {
            TextureAnimationWithTransitions result;
            if (string.IsNullOrEmpty(name)) return null;
            if (s_animations.TryGetValue(name, out result) && result != null) return result;
            s_animations.Remove(name);
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(TextureAnimationWithTransitions)))
            {
                TextureAnimationWithTransitions a = o as TextureAnimationWithTransitions;
                if (a != null && !s_animations.ContainsKey(AnimationKey(a))) s_animations.Add(AnimationKey(a), a);
            }
            s_animations.TryGetValue(name, out result); if (result == null) Warn("animation unresolved " + name); return result;
        }
        static void ApplyPose(Actor a, State s, Vector3 position)
        {
            Enemy e = a.Entity; if (e == null) return;
            e.Position = position; e.FaceLeft = s.Left;
            if (s.HasVisualAngle)
            {
                FishEnemy fish = e as FishEnemy; DashOwlEnemy owl = e as DashOwlEnemy;
                if (fish != null && fish.Rotation != null)
                { Vector3 angles = fish.Rotation.eulerAngles; angles.z = s.VisualAngle; fish.Rotation.eulerAngles = angles; }
                if (owl != null && owl.SpriteRotation != null)
                { owl.SpriteRotation.TargetAngle = s.VisualAngle; owl.SpriteRotation.RotateToTargetImmediately(); }
                if (fish == null && owl == null && e.GetComponent<TraceGroundMovement>() != null)
                { Vector3 angles = e.transform.eulerAngles; angles.z = s.VisualAngle; e.transform.eulerAngles = angles; }
            }
            if (e.DamageReciever != null) e.DamageReciever.Health = s.Health;
            if (e.DamageDealer != null) e.DamageDealer.Activated = s.DealerActive;
            if (e.Targetting != null) e.Targetting.Activated = s.TargetActive;
            if (e.Animation != null && e.Animation.Animator != null)
            {
                TextureAnimationWithTransitions anim = Animation(s.Animation);
                if (anim != null && e.Animation.Animator.CurrentTextureAnimationTransitions != anim)
                {
                    if (Mine(a)) e.Animation.PlayLoop(anim, 0, null, false);
                    e.Animation.Animator.SetAnimation(anim); e.Animation.Animator.CurrentAnimationTime = s.AnimationTime;
                }
            }
        }
        [HarmonyPatch(typeof(CharacterAnimationSystem), "UpdateStates")]
        static class PuppetAnimationStates
        {
            static bool Prefix(CharacterAnimationSystem __instance)
            {
                if (!Active) return true; Actor a = For(__instance);
                return a == null || a.Entity == null || !Supported(a.Entity) || Mine(a);
            }
        }
        [HarmonyPatch(typeof(SpriteRotationController), "FixedUpdate")]
        static class PuppetSpriteRotation
        {
            static bool Prefix(SpriteRotationController __instance)
            {
                Actor a = Active ? For(__instance) : null;
                return a == null || a.Entity == null || !Supported(a.Entity) || Mine(a);
            }
        }
        static void Puppet(Actor a)
        {
            if (a.Entity == null || a.Buffer.Count == 0) return;
            if (!Supported(a.Entity)) { if (a.Entity.DamageReciever != null) a.Entity.DamageReciever.Health = a.Latest.Health; return; }
            FreezeBodies(a);
            State newest = a.Buffer[a.Buffer.Count - 1];
            // Sender ticks are 60 Hz; arrival anchors the remote clock without requiring clock sync.
            float target = newest.Tick + Mathf.Min(0.1f, Now - a.Arrival) * 60f - 6f;
            State left = a.Buffer[0], right = left;
            for (int i = 1; i < a.Buffer.Count; i++) { right = a.Buffer[i]; if (right.Tick >= target) break; left = right; }
            float t = right.Tick == left.Tick ? 0 : Mathf.Clamp01((target - left.Tick) / (right.Tick - left.Tick));
            Vector3 pos = Vector3.Distance(left.Position, right.Position) > 6f ? right.Position : Vector3.Lerp(left.Position, right.Position, t);
            a.RenderTick = Vector3.Distance(left.Position, right.Position) > 6f ? right.Tick : Mathf.Lerp(left.Tick, right.Tick, t);
            ApplyPose(a, t < 0.5f ? left : right, pos);
        }
        [HarmonyPatch(typeof(Entity), "FixedUpdate")]
        static class PartnerEnemyFrustum
        {
            static bool Prefix(Entity __instance)
            {
                Actor a = Active ? For(__instance) : null;
                return a == null || !Mine(a) || RemoteDistance(__instance.Position) >= 28f;
            }
        }
        [HarmonyPatch(typeof(NearSeinTrigger), "FixedUpdate")]
        static class EnemyNearPartner
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(NearSeinTrigger __instance, out bool __state)
            {
                __state = false; if (!Active) return true; Actor a = For(__instance);
                if (a == null) return true;
                if (a.Entity != null && Supported(a.Entity) && !Mine(a)) return false;
                if (!s_host.Inside && s_host.P2IsNearer(__instance.transform.position)) { s_host.Enter(s_host.NearestPuppet(__instance.transform.position)); __state = true; }
                return true;
            }
            static Exception Finalizer(Exception __exception, bool __state) { if (__state) s_host.Exit(); return __exception; }
        }
        [HarmonyPatch(typeof(SeeSeinTrigger), "FixedUpdate")]
        static class EnemySeesPartner
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(SeeSeinTrigger __instance, out bool __state)
            {
                __state = false; if (!Active) return true; Actor a = For(__instance);
                if (a == null) return true;
                if (a.Entity != null && Supported(a.Entity) && !Mine(a)) return false;
                if (!s_host.Inside && s_host.P2IsNearer(__instance.transform.position)) { s_host.Enter(s_host.NearestPuppet(__instance.transform.position)); __state = true; }
                return true;
            }
            static Exception Finalizer(Exception __exception, bool __state) { return ContextFinalizer(__exception, __state); }
        }
        [HarmonyPatch(typeof(SeeSeinHorizontalyTrigger), "FixedUpdate")]
        static class EnemySeesPartnerHorizontally
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(SeeSeinHorizontalyTrigger __instance, out bool __state)
            {
                __state = false; if (!Active) return true; Actor a = For(__instance); if (a == null) return true;
                if (a.Entity != null && Supported(a.Entity) && !Mine(a)) return false;
                object nearest = s_host.NearestPuppet(__instance.transform.position);
                SeinCharacter player = nearest != null ? s_host.PuppetSein(nearest) : Local;
                s_horizontalPlayer.SetValue(__instance, player.Controller.Transform);
                if (nearest != null && !s_host.Inside) { s_host.Enter(nearest); __state = true; }
                return true;
            }
            static Exception Finalizer(Exception __exception, bool __state) { return ContextFinalizer(__exception, __state); }
        }

        static bool PlayerDamage(DamageType t)
        {
            return t == DamageType.SpiritFlame || t == DamageType.SpiritFlameSplatter || t == DamageType.ChargeFlame
                || t == DamageType.Bash || t == DamageType.Stomp || t == DamageType.StompBlast || t == DamageType.Grenade || t == DamageType.LevelUp;
        }
        static DamageStamp Stamp(Damage d)
        {
            DamageStamp s;
            if (s_damage.TryGetValue(d, out s)) return s;
            s = new DamageStamp(); s.Id = NextId();
            GameObject sender = d.Sender; SpiritFlameProjectile flame = sender == null ? null : sender.GetComponent<SpiritFlameProjectile>();
            s.Puppet = PlayerDamage(d.Type) && (s_attackDepth > 0 || s_host.Inside
                || (sender != null && (s_host.BelongsToP2(sender.transform) || IsPuppetAttack(sender)))
                || (flame != null && s_host.IsRemoteSein(flame.Sein)));
            s_damage[d] = s; s_damageOrder.Enqueue(d);
            while (s_damageOrder.Count > 8192) s_damage.Remove(s_damageOrder.Dequeue());
            return s;
        }
        // For other modules (WorldObjSync): true when this damage came from a remote player's puppet.
        public static bool IsPuppetDamage(Damage d) { return Active && d != null && Stamp(d).Puppet; }
        static bool IsPuppetAttack(GameObject go) { PuppetAttack tag = go.GetComponentInParent<PuppetAttack>(); return tag != null && tag.IsPuppet; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool Remember(Actor a, ulong hit)
        {
            if (!a.Hits.Add(hit)) return false;
            // Hit ids are monotonically increasing per peer; old generations are rejected separately.
            return true;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool AcceptOwnerHit(Actor a, ulong hit) { return a.Applied.Add(hit); }
        static OriCoop.Enemies.EnemyDamage ToWire(Damage d)
        {
            OriCoop.Enemies.EnemyDamage value = new OriCoop.Enemies.EnemyDamage(); value.Amount = d.Amount; value.Type = (int)d.Type;
            value.ForceX = d.Force.x; value.ForceY = d.Force.y; value.Position = ToWire(d.Position); return value;
        }
        static void WriteDamage(BinaryWriter w, Damage d) { EnemyWire.WriteDamage(w, ToWire(d)); }
        static Damage ReadDamage(BinaryReader r)
        { return ReadDamageValue(r, Local == null ? null : Local.gameObject); }
        static Damage ReadDamageValue(BinaryReader r, GameObject sender)
        {
            return FromWire(EnemyWire.ReadDamage(r), sender);
        }
        static Damage FromWire(OriCoop.Enemies.EnemyDamage d, GameObject sender)
        { return new Damage(d.Amount, new Vector2(d.ForceX, d.ForceY), FromWire(d.Position), (DamageType)d.Type, sender); }
        static bool Route(Actor a, Damage d)
        {
            if (!Active || a == null) return true;
            DamageStamp stamp = Stamp(d); if (stamp.Puppet) return false;
            if (Mine(a)) { if (!AcceptOwnerHit(a, stamp.Id)) return false; a.LastHit = stamp.Id; return true; }
            if (!PlayerDamage(d.Type) || !Remember(a, stamp.Id)) return false;
            // Unknown authority and frozen transfers queue rather than lose an attack.
            PendingHit pending = new PendingHit(); pending.Actor = a; pending.Generation = a.Generation; pending.Id = stamp.Id; pending.Damage = d;
            pending.BindGeneration = !a.Known;
            s_pendingHits.Add(pending); return false;
        }
        static void FlushHits()
        {
            for (int i = s_pendingHits.Count - 1; i >= 0; i--)
            {
                PendingHit p = s_pendingHits[i]; Actor a = p.Actor;
                if (!a.Known || a.Frozen) continue;
                if (p.BindGeneration) { p.Generation = a.Generation; p.BindGeneration = false; }
                if (p.Generation != a.Generation) { s_pendingHits.RemoveAt(i); continue; }
                if (Mine(a)) ApplyHit(a, p.Damage, p.Id);
                else
                {
                    SendHit(a, p.Generation, p.Id, p.Damage);
                    Trace("hit-send", a, "hit=" + p.Id + " sourceSeat=" + (p.Id >> 32) + " amount=" + p.Damage.Amount);
                }
                s_pendingHits.RemoveAt(i);
            }
        }
        static void ReadHit(BinaryReader r)
        {
            Actor a = ReadId(r); EnemyHit hit = EnemyWire.ReadHit(r); uint epoch = hit.Epoch, generation = hit.Generation; ulong id = hit.Id;
            Damage d = FromWire(hit.Damage, Local == null ? null : Local.gameObject);
            Trace("hit-recv", a, "hit=" + id + " sourceSeat=" + (id >> 32) + " amount=" + d.Amount);
            int decision = EnemyOwnership.HitDecision(a.Generation, generation, a.Applied.Contains(id), a.Frozen, Mine(a), epoch, a.Epoch, Services.Net.IsHost);
            if (decision == 1)
            { if (!Remember(a, id)) return; PendingHit p = new PendingHit(); p.Actor = a; p.Generation = generation; p.Id = id; p.Damage = d; s_pendingHits.Add(p); return; }
            if (decision == 2) ApplyHit(a, d, id);
            else if (decision == 3 && Remember(a, id)) SendHit(a, generation, id, d);
        }
        static void SendHit(Actor a, uint generation, ulong id, Damage damage)
        {
            Send(Hit, true, delegate(BinaryWriter w) { WriteId(w, a); EnemyHit hit = new EnemyHit(); hit.Epoch = a.Epoch; hit.Generation = generation; hit.Id = id; hit.Damage = ToWire(damage); EnemyWire.WriteHit(w, hit); },
                Services.Net.IsHost ? a.OwnerSeat : 0);
        }
        static void ApplyHit(Actor a, Damage damage, ulong id)
        {
            if (a.Entity == null || a.Entity.DamageReciever == null || a.Latest.Killed)
            { Services.Log.Warn("net: E3 hit unavailable id=" + id + " " + Describe(a.Key)); return; }
            if (a.Applied.Contains(id)) return;
            // Applying a network hit suppresses echoing, but never receiver deduplication. The
            // game's damage broadcast may re-enter this receiver through other components.
            DamageStamp stamp = Stamp(damage); stamp.Id = id; stamp.Puppet = false;
            a.LastHit = id; bool applying = s_applying; s_applying = true;
            try { damage.DealToComponents(a.Entity.DamageReciever.gameObject); }
            finally { s_applying = applying; }
            a.Latest = Capture(a, true);
        }
        [HarmonyPatch(typeof(Damage), MethodType.Constructor, new Type[] { typeof(float), typeof(Vector2), typeof(Vector3), typeof(DamageType), typeof(GameObject) })]
        static class DamageOrigin { static void Postfix(Damage __instance) { if (Active && !s_applying) Stamp(__instance); } }
        [HarmonyPatch(typeof(Damage), "DealToComponents")]
        static class DamageDispatch
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(Damage __instance, GameObject target)
            {
                if (!Active || target == null) return true;
                EntityDamageReciever dr = target.GetComponent<EntityDamageReciever>();
                Actor a = For(dr != null ? (Component)dr : target.transform);
                if (a == null) return !Stamp(__instance).Puppet || target.GetComponentInParent<Enemy>() == null;
                // Owner receiver stamps/deduplicates. Suppress the whole SendMessage on puppets so
                // controller, kickback, and sibling listeners cannot react to an unaccepted hit.
                if (Mine(a)) return !Stamp(__instance).Puppet;
                Route(a, __instance); return false;
            }
        }
        [HarmonyPatch(typeof(EntityDamageReciever), "OnRecieveDamage")]
        static class EnemyDamage
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(EntityDamageReciever __instance, Damage damage, out HitObservation __state)
            {
                __state = null; Actor a = Active ? For(__instance) : null;
                bool allowed = !(Active && __instance.Entity is Enemy && Stamp(damage).Puppet) && Route(a, damage);
                if (a != null && allowed)
                {
                    __state = new HitObservation(); __state.Actor = a; __state.Before = __instance.Health;
                    __state.Amount = damage.Amount; __state.Type = damage.Type; __state.Id = s_applying ? a.LastHit : Stamp(damage).Id;
                }
                return allowed;
            }
            static void Postfix(EntityDamageReciever __instance, HitObservation __state)
            {
                if (__state == null) return;
                float after = __instance == null ? float.NaN : __instance.Health;
                Trace("damage", __state.Actor, "hit=" + __state.Id + " sourceSeat=" + (__state.Id >> 32)
                    + " amount=" + __state.Amount + " before=" + __state.Before + " after=" + after);
                Services.Log.Info("net: E3 damage hit=" + __state.Id + " amount=" + __state.Amount + " type=" + __state.Type
                    + " before=" + __state.Before + " after=" + after + " delta=" + (__state.Before - after) + " " + Describe(__state.Actor.Key));
            }
        }
        // The native camera covers only P1. Mirror the same padded visibility policy around P2
        // for spawn checks, scene-disable decisions, and the independent boundary-destroy script.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool InPartnerScreen(Vector3 position, Vector3 partner)
        { return Math.Abs(position.x - partner.x) <= 26f && Math.Abs(position.y - partner.y) <= 18f; }
        static bool NearPartner(Vector3 position)
        {
            foreach (KeyValuePair<int, Vector3> remote in s_host.RemotePositions) if (InPartnerScreen(position, remote.Value)) return true;
            return false;
        }
        static float RemoteDistance(Vector3 position)
        {
            float distance = float.PositiveInfinity;
            foreach (KeyValuePair<int, Vector3> remote in s_host.RemotePositions) distance = Mathf.Min(distance, Vector3.Distance(position, remote.Value));
            return distance;
        }
        static bool Protect(Actor a)
        { return a != null && Mine(a) && a.Entity != null && Supported(a.Entity) && !a.Latest.Killed && NearPartner(a.Entity.Position); }
        [HarmonyPatch(typeof(RespawningPlaceholder), "get_IsOnScreen")]
        static class PartnerPlaceholderScreen
        {
            static void Postfix(RespawningPlaceholder __instance, ref bool __result)
            {
                if (__result || !Active || !EnemyPlaceholder(__instance) || !NearPartner(__instance.Position)) return;
                Actor a = Track(__instance);
                if (a != null && Mine(a) && (a.Entity == null || Supported(a.Entity))) __result = true;
            }
        }
        [HarmonyPatch(typeof(Entity), "IsOnScreen")]
        static class PartnerEntityScreen
        {
            static void Postfix(Entity __instance, ref bool __result) { if (!__result && Active && Protect(For(__instance))) __result = true; }
        }
        [HarmonyPatch(typeof(DestroyWhenOutsideActiveBoundaries), "FixedUpdate")]
        static class PartnerBoundaryProtection
        {
            static bool Prefix(DestroyWhenOutsideActiveBoundaries __instance) { return !Active || !Protect(For(__instance)); }
        }
        [HarmonyPatch(typeof(Entity), "OnSceneUnloaded")]
        static class PartnerSceneProtection
        {
            static bool Prefix(Entity __instance) { return !Active || !Protect(For(__instance)); }
        }
        [HarmonyPatch(typeof(RespawningPlaceholder), "DestroyCurrentInstance")]
        static class RemovalAudit
        {
            static void Prefix(RespawningPlaceholder __instance)
            {
                if (!Active || __instance.CurrentEntity == null) return;
                Actor a = Track(__instance); if (a == null) return;
                a.LastRemoval = __instance.EntityIsDead ? "death" : "destroy-instance alive";
                Trace("despawn", a, a.LastRemoval);
                Services.Log.Info("net: E3 remove " + Describe(a.Key));
            }
        }
        [HarmonyPatch(typeof(RespawningPlaceholder), "UpdateSpawnState")]
        static class PlaceholderUpdate
        {
            static bool Prefix(RespawningPlaceholder __instance)
            { if (!Active) return true; Actor a = Track(__instance); return a == null || Mine(a); }
        }
        [HarmonyPatch(typeof(RespawningPlaceholder), "Spawn")]
        static class PlaceholderSpawn
        {
            static bool Prefix(RespawningPlaceholder __instance)
            { if (!Active || s_spawning) return true; Actor a = Track(__instance); return a == null || Mine(a) || (a.Known && a.Latest.Alive && !a.Latest.Killed); }
            static void Postfix(RespawningPlaceholder __instance) { if (Active) Track(__instance); }
        }
        [HarmonyPatch(typeof(RespawningPlaceholder), "OnDeath")]
        static class PlaceholderDeath
        {
            static void Prefix(RespawningPlaceholder __instance, Damage damage, out bool __state)
            {
                __state = false;
                if (!Active) return; Actor a = Track(__instance); if (a == null) return;
                __state = Mine(a) && a.Entity != null && a.Entity.DamageReciever != null && a.Entity.DamageReciever.DeathAction != null;
                a.LastDeath = damage.Type + ":" + damage.Amount;
                Trace("death", a, "hit=" + a.LastHit + " type=" + damage.Type + " amount=" + damage.Amount);
                Services.Log.Info("net: E3 died hit=" + a.LastHit + " type=" + damage.Type + " amount=" + damage.Amount + " " + Describe(a.Key));
            }
            static void Postfix(RespawningPlaceholder __instance, bool __state)
            {
                if (!Active) return; Actor a = Track(__instance); if (a == null || !Mine(a)) return;
                if (__state) OwnerDeathEffects++;
                a.Latest = Capture(a, true); a.Latest.Killed = true; a.Latest.Alive = false; a.Latest.Health = 0;
                a.DeathTime = Now; SendLife(a, 2);
            }
        }
        [HarmonyPatch(typeof(RespawningPlaceholder), "OnDisable")]
        static class PlaceholderUnloaded
        {
            static void Prefix(RespawningPlaceholder __instance)
            {
                if (!Active) return; Actor a = Track(__instance); if (a == null) return;
                if (Mine(a)) a.Latest = Capture(a, true);
                a.Loaded = false; a.Placeholder = null; Announce(a);
                if (Services.Net.IsHost) Decide(a);
            }
        }

        static string PrefabKey(GameObject prefab) { Projectile p = prefab == null ? null : prefab.GetComponent<Projectile>(); return p == null ? "" : prefab.name + "|" + p.GetType().FullName; }
        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) })]
        static class InstantiationAt
        {
            static void Postfix(UnityEngine.Object original, UnityEngine.Object __result) { Spawned(original as GameObject, __result as GameObject); }
        }
        [HarmonyPatch(typeof(InstantiateUtility), "Instantiate", new Type[] { typeof(UnityEngine.Object) })]
        static class Instantiation
        {
            static void Postfix(UnityEngine.Object original, UnityEngine.Object __result) { Spawned(original as GameObject, __result as GameObject); }
        }
        static void Spawned(GameObject prefab, GameObject instance)
        {
            if (!Active || s_spawning || instance == null) return;
            bool playerAttack = instance.GetComponent<SpiritFlameProjectile>() != null || instance.GetComponent<ChargeFlameBurst>() != null
                || instance.GetComponent<GrenadeBurst>() != null || instance.GetComponent<SpiritGrenade>() != null;
            if (playerAttack)
            {
                PuppetAttack tag = instance.GetComponent<PuppetAttack>(); if (tag == null) tag = instance.AddComponent<PuppetAttack>();
                tag.IsPuppet = s_attackDepth > 0 || (s_host.Inside && s_active == null);
            }
            Projectile projectile = instance.GetComponent<Projectile>();
            if (s_active == null || !Mine(s_active) || prefab == null) return;
            bool kamikazeExplosion = prefab == s_active.KamikazeExplosion;
            if (!kamikazeExplosion && (s_active.Entity == null || !Supported(s_active.Entity))) return;
            if (projectile == null)
            {
                if (playerAttack || instance.GetComponentInChildren<Projectile>() != null || instance.GetComponentInChildren<Entity>() != null
                    || instance.GetComponentInChildren<PickupBase>() != null || instance.GetComponentInChildren<RespawningPlaceholder>() != null
                    || instance.GetComponentInChildren<SpiritFlameProjectile>() != null || instance.GetComponentInChildren<ChargeFlameBurst>() != null
                    || instance.GetComponentInChildren<GrenadeBurst>() != null || instance.GetComponentInChildren<SpiritGrenade>() != null
                    || (!kamikazeExplosion && s_active.Entity.DamageReciever != null && s_active.Entity.DamageReciever.NoHealthLeft)
                    || !NearPartner(instance.transform.position)) return;
                if (Now - s_fxWindow >= 1f) { s_fxWindow = Now; s_fxCount = 0; }
                if (s_fxCount >= 12) return;
                s_fxCount++;
                Actor actor = s_active; ProjectileIdentity tag = instance.GetComponent<ProjectileIdentity>();
                if (tag == null) tag = instance.AddComponent<ProjectileIdentity>();
                tag.Id = NextId(); tag.Cosmetic = false;
                Vector3 position = instance.transform.position; Quaternion rotation = instance.transform.rotation;
                DamageDealer dealer = instance.GetComponentInChildren<DamageDealer>(); float damage = dealer == null ? -1f : dealer.Damage;
                if (kamikazeExplosion) damage = actor.KamikazeDamage;
                Send(EnemyFx, true, delegate(BinaryWriter w) {
                    WriteId(w, actor); w.Write(actor.Epoch); w.Write(tag.Id); w.Write(prefab.name); WriteVector(w, position);
                    w.Write(rotation.x); w.Write(rotation.y); w.Write(rotation.z); w.Write(rotation.w); w.Write(damage);
                });
                FxSent++; Trace("fx-send", actor, "id=" + tag.Id + " prefab=" + prefab.name + " damage=" + damage);
                return;
            }
            ProjectileIdentity identity = projectile.GetComponent<ProjectileIdentity>(); if (identity == null) identity = instance.AddComponent<ProjectileIdentity>();
            identity.Id = NextId(); identity.Cosmetic = false;
            PendingShot shot = new PendingShot(); shot.Actor = s_active; shot.Projectile = projectile; shot.Prefab = prefab; s_shots.Add(shot);
        }
        static void ReadEnemyFx(BinaryReader r)
        {
            Actor a = ReadId(r); uint epoch = r.ReadUInt32(); ulong id = r.ReadUInt64(); string name = r.ReadString();
            Vector3 position = ReadVector(r); Quaternion rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float damage = r.ReadSingle();
            if (!a.AcceptOwnerMessage(s_host.LocalSeat, epoch, Services.Net.Sender, false)) return;
            GameObject prefab;
            if (!s_fxPrefabs.TryGetValue(name, out prefab) || prefab == null)
            {
                foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll(typeof(GameObject)))
                {
                    GameObject go = obj as GameObject;
                    if (go != null && go.name == name && (string.IsNullOrEmpty(go.scene.name) || !go.scene.IsValid()))
                    { prefab = go; s_fxPrefabs[name] = prefab; break; }
                }
            }
            if (prefab == null) { Warn("fx prefab unresolved " + name); return; }
            s_spawning = true;
            try
            {
                GameObject go = InstantiateUtility.Instantiate(prefab, position, rotation) as GameObject;
                ProjectileIdentity tag = go.GetComponent<ProjectileIdentity>(); if (tag == null) tag = go.AddComponent<ProjectileIdentity>();
                tag.Id = id; tag.Cosmetic = true; tag.Expires = Now + 4f; s_copies.Add(tag);
                DamageDealer dealer = go.GetComponentInChildren<DamageDealer>(); if (dealer != null && damage >= 0) dealer.Damage = damage;
                FxReceived++; Trace("fx-recv", a, "id=" + id + " prefab=" + name + " damage=" + damage);
            }
            finally { s_spawning = false; }
        }
        [HarmonyPatch(typeof(Projectile), "OnPoolSpawned")]
        static class ProjectileReuse
        {
            static void Prefix(Projectile __instance)
            {
                ProjectileIdentity tag = __instance.GetComponent<ProjectileIdentity>();
                if (tag != null) { s_copies.Remove(tag); tag.Id = 0; tag.Cosmetic = false; tag.Expires = 0; }
            }
        }
        [HarmonyPatch(typeof(Projectile), "OnRecieveDamage")]
        static class PuppetProjectileAttack
        {
            static bool Prefix(Damage damage) { return !Active || s_applying || !Stamp(damage).Puppet; }
        }
        [HarmonyPatch(typeof(ProjectileSpawner), "SpawnProjectile")]
        static class SpawnerContext
        {
            static void Prefix(ProjectileSpawner __instance, out Actor __state) { __state = s_active; if (Active) { Actor a = For(__instance); if (a != null) s_active = a; } }
            static Exception Finalizer(Exception __exception, Actor __state) { s_active = __state; return __exception; }
        }
        static void FlushShots()
        {
            foreach (PendingShot shot in s_shots)
            {
                Projectile p = shot.Projectile; if (p == null || !Mine(shot.Actor)) continue;
                ProjectileIdentity tag = p.GetComponent<ProjectileIdentity>(); if (tag == null) tag = p.gameObject.AddComponent<ProjectileIdentity>();
                tag.Cosmetic = false; string prefab = PrefabKey(shot.Prefab); s_prefabs[prefab] = shot.Prefab;
                DamageDealer dealer = p.GetComponent<DamageDealer>(); float damage = dealer == null ? 0 : dealer.Damage; DamageType type = dealer == null ? DamageType.Projectile : dealer.DamageType;
                Send(Shot, true, delegate(BinaryWriter w) { WriteId(w, shot.Actor); EnemyShot value = new EnemyShot();
                    value.Epoch = shot.Actor.Epoch; value.Id = tag.Id; value.Prefab = prefab; value.Position = ToWire(p.Position);
                    value.Velocity = ToWire(p.SpeedVector); value.Gravity = p.Gravity; value.Lifetime = p.MaximumLiveTime;
                    value.Damage = damage; value.Type = (int)type; EnemyWire.WriteShot(w, value); });
            }
            s_shots.Clear();
        }
        static GameObject FindPrefab(string key)
        {
            GameObject prefab; if (s_prefabs.TryGetValue(key, out prefab) && prefab != null) return prefab;
            foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(typeof(Projectile)))
            {
                Projectile p = o as Projectile; if (p == null || p.name.IndexOf("(Clone)", StringComparison.Ordinal) >= 0 || p.GetComponent<ProjectileIdentity>() != null) continue;
                if (PrefabKey(p.gameObject) != key) continue;
                if (prefab != null && prefab != p.gameObject) { Warn("ambiguous projectile prefab " + key); return null; }
                prefab = p.gameObject;
            }
            if (prefab != null) s_prefabs[key] = prefab; return prefab;
        }
        static void ReadShot(BinaryReader r)
        {
            Actor a = ReadId(r); EnemyShot shot = EnemyWire.ReadShot(r); uint epoch = shot.Epoch; ulong id = shot.Id; string key = shot.Prefab;
            Vector3 pos = FromWire(shot.Position), velocity = FromWire(shot.Velocity);
            float gravity = shot.Gravity, lifetime = shot.Lifetime, damage = shot.Damage; DamageType type = (DamageType)shot.Type;
            if (!a.AcceptOwnerMessage(s_host.LocalSeat, epoch, Services.Net.Sender, false)) return;
            GameObject prefab = FindPrefab(key);
            if (prefab == null) { UnresolvedProjectiles++; Warn("projectile prefab unresolved " + key + "; owner hit forwarding remains active"); return; }
            s_spawning = true;
            try
            {
                GameObject go = InstantiateUtility.Instantiate(prefab, pos, Quaternion.identity) as GameObject; if (go == null) return;
                Projectile p = go.GetComponent<Projectile>(); p.SpeedVector = velocity; p.Gravity = gravity; p.MaximumLiveTime = lifetime; p.Owner = a.Entity == null ? null : a.Entity.gameObject;
                ProjectileIdentity tag = go.GetComponent<ProjectileIdentity>(); if (tag == null) tag = go.AddComponent<ProjectileIdentity>();
                tag.Id = id; tag.Cosmetic = true; tag.Expires = Now + Mathf.Max(0.1f, lifetime); s_copies.Add(tag);
                DamageDealer dealer = go.GetComponent<DamageDealer>(); if (dealer != null) { dealer.Damage = damage; dealer.DamageType = type; }
            }
            finally { s_spawning = false; }
        }
        static ProjectileIdentity ProjectileTag(Damage d) { return d.Sender == null ? null : d.Sender.GetComponentInParent<ProjectileIdentity>(); }
        // Called by the narrow P2 touch exception. All other online trigger policy remains unchanged.
        public static bool ProjectileTouch(Component c) { return Active && c != null && c.GetComponentInParent<ProjectileIdentity>() != null; }
        [HarmonyPatch(typeof(SeinDamageReciever), "OnRecieveDamage")]
        static class ProjectileVictim
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(SeinDamageReciever __instance, Damage damage)
            {
                if (!Active || s_applying) return true;
                ProjectileIdentity tag = ProjectileTag(damage); if (tag == null || tag.Id == 0) return true;
                if (s_host.BelongsToP2(__instance))
                {
                    int victim = s_host.SeatOf(__instance);
                    if (!tag.Cosmetic) Send(OriHit, true, delegate(BinaryWriter w) { w.Write((byte)victim); w.Write(tag.Id); WriteDamage(w, damage); }, Services.Net.IsHost ? victim : 0);
                    return false; // NetPlay's existing invulnerability stays in place.
                }
                return s_victimHits.Add(s_host.VictimSeat(__instance), tag.Id);
            }
        }
        [HarmonyPatch(typeof(DamageDealer), "DealDamage")]
        static class CosmeticDamage
        {
            static bool Prefix(DamageDealer __instance, GameObject target)
            {
                if (!Active) return true; ProjectileIdentity tag = __instance.GetComponentInParent<ProjectileIdentity>();
                if (tag == null || !tag.Cosmetic) return true;
                SeinDamageReciever victim = target == null ? null : target.GetComponent<SeinDamageReciever>();
                return victim != null && !s_host.BelongsToP2(victim);
            }
        }
        static void ReadOriHit(BinaryReader r)
        {
            int victim = r.ReadByte(); ulong id = r.ReadUInt64(); Damage d = ReadDamage(r);
            SeinCharacter localVictim = s_host.LocalAt(victim);
            if (localVictim == null)
            {
                if (Services.Net.IsHost) Send(OriHit, true, w => { w.Write((byte)victim); w.Write(id); WriteDamage(w, d); }, victim);
                return;
            }
            if (!s_victimHits.Add(victim, id)) return;
            s_applying = true; try { localVictim.Mortality.DamageReciever.OnRecieveDamage(d); } finally { s_applying = false; }
        }

        static void Scan()
        {
            foreach (Actor a in s_actors.Values) a.Loaded = false;
            foreach (RespawningPlaceholder p in RespawningPlaceholder.All.ToArray()) if (p != null) Track(p);
            int count = 0;
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(typeof(Enemy)))
            {
                Enemy e = o as Enemy; if (e != null && !s_entities.ContainsKey(e.GetInstanceID())) count++;
            }
            Unmirrored = count;
        }
        // T-022: a cutscene removed the local Ori; partner scenes must not be held through it (the game only gives
        // Ori back when the scene it returns to is loaded again).
        public static void ReleasePartnerPins() { ReleasePins(); }
        static void ReleasePins()
        {
            if (Scenes.Manager != null && s_pinPositions.Count > 0)
            {
                Dictionary<SceneManagerScene, bool> before = new Dictionary<SceneManagerScene, bool>();
                foreach (SceneManagerScene scene in Scenes.Manager.ActiveScenes) before[scene] = scene.PreventUnloading;
                foreach (Vector3 position in s_pinPositions) Scenes.Manager.AllowUnloadingOnScenes(position);
                foreach (KeyValuePair<SceneManagerScene, bool> pair in before) pair.Key.PreventUnloading = pair.Value;
            }
            foreach (KeyValuePair<SceneManagerScene, bool> pair in s_pins) if (pair.Key != null) pair.Key.PreventUnloading = pair.Value;
            s_pins.Clear(); s_pinPositions.Clear(); KeptScenes = 0;
        }
        static void StreamPartner()
        {
            if (!Services.Net.IsHost || Scenes.Manager == null) return;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            ReleasePins();
            Dictionary<SceneManagerScene, bool> before = new Dictionary<SceneManagerScene, bool>();
            foreach (SceneManagerScene scene in Scenes.Manager.ActiveScenes) before[scene] = scene.PreventUnloading;
            foreach (KeyValuePair<int, Vector3> remote in s_host.RemotePositions)
            {
                s_pinPositions.Add(remote.Value);
                Rect rect = new Rect(remote.Value.x - 22f, remote.Value.y - 14f, 44f, 28f);
                Scenes.Manager.AdditivelyLoadScenesInsideRect(rect, true, true, true);
                foreach (SceneManagerScene scene in Scenes.Manager.ActiveScenes)
                {
                    if (scene.MetaData == null || !scene.MetaData.IsInTotal(rect) || s_pins.ContainsKey(scene)) continue;
                    bool original; if (!before.TryGetValue(scene, out original)) original = false;
                    s_pins[scene] = original; scene.PreventUnloading = true;
                }
            }
            // Included background/collision scenes must stay active as well as their parent scene.
            foreach (SceneManagerScene scene in new List<SceneManagerScene>(s_pins.Keys))
                foreach (MoonGuid id in scene.MetaData.IncludedScenes)
                    foreach (SceneManagerScene included in Scenes.Manager.ActiveScenes)
                        if (included.MetaData != null && included.MetaData.SceneMoonGuid == id && !s_pins.ContainsKey(included))
                        { bool original; if (!before.TryGetValue(included, out original)) original = false; s_pins[included] = original; included.PreventUnloading = true; }
            Vector3 localTarget = Scenes.Manager.CurrentCameraTargetPosition;
            try
            {
                foreach (Vector3 position in s_pinPositions)
                { Scenes.Manager.SetTargetPositions(position); Scenes.Manager.EnableDisabledScenesAtPosition(false); }
            }
            finally { Scenes.Manager.SetTargetPositions(localTarget); Scenes.Manager.UpdatePosition(); }
            foreach (SceneManagerScene scene in s_pins.Keys) scene.PreventUnloading = true;
            KeptScenes = s_pins.Count;
            watch.Stop(); StreamMilliseconds = (float)watch.Elapsed.TotalMilliseconds;
        }
        [HarmonyPatch(typeof(ScenesManager), "UpdatePosition")]
        static class PartnerSceneFocus
        {
            static void Postfix(List<Vector3> ___m_cameraPositions)
            {
                if (Active && Services.Net.IsHost && (Local != null || CutsceneRespawn.Off))   // no local Ori (cutscene): stream like single player
                    foreach (KeyValuePair<int, Vector3> remote in s_host.RemotePositions) ___m_cameraPositions.Add(remote.Value);
            }
        }
        [HarmonyPatch(typeof(ScenesManager), "UnloadScene")]
        static class PartnerSceneKeepActive
        {
            static bool Prefix(SceneManagerScene scene, ref bool __result)
            {
                if (!Active || !Services.Net.IsHost || !s_pins.ContainsKey(scene) || scene.MetaData == null || !scene.MetaData.CanBeLoaded) return true;
                __result = false; return false;
            }
        }
        public static void Tick()
        {
            if (!Active) return;
            if (DifficultyController.Instance != null && s_difficulty != DifficultyController.Instance.Difficulty)
            { s_difficulty = DifficultyController.Instance.Difficulty; Services.Log.Info("E3T difficulty=" + s_difficulty); }
            if (Local == null && s_pins.Count > 0 && !CutsceneRespawn.Off) ReleasePins();
            if (!Services.Session.Ready || Local == null) return;
            if (Now >= s_streamAt) { s_streamAt = Now + 0.5f; StreamPartner(); }
            if (Now >= s_scanAt)
            {
                s_scanAt = Now + 0.25f; Scan();
                foreach (Actor a in new List<Actor>(s_actors.Values))
                {
                    if (!a.Loaded && a.Placeholder != null)
                    {
                        if (Mine(a)) a.Latest = Capture(a, true);
                        a.Placeholder = null;
                    }
                    if (a.PresenceDue(delegate { return Now; })) Announce(a);
                    if (EnemyAuthority.Policy.UsesLeases)
                    {
                        Vector3 position = a.Entity != null ? a.Entity.Position : a.Origin;
                        a.UpdateInterest(a.Key, Vector3.Distance(position, Local.Position), a.SceneRadius);
                        RefreshOwnership(a);
                    }
                    else if (Services.Net.IsHost && a.OwnerSeat != PolicyDesiredOwner(a)) Decide(a);
                }
            }
            float dt = Mathf.Max(0, Now - s_tokenTime); s_tokenTime = Now; s_tokens = Mathf.Min(Budget / 4f, s_tokens + dt * Budget);
            List<Actor> candidates = new List<Actor>();
            foreach (Actor a in s_actors.Values)
            {
                if (a.Entity != null && Now >= a.NextTrace && (Vector3.Distance(a.Entity.Position, Local.Position) <= 30f
                    || RemoteDistance(a.Entity.Position) <= 30f))
                {
                    a.NextTrace = Now + 1f;
                    Trace("state", a, Mine(a) ? "velocity=" + Capture(a, false).Velocity.ToString("F1") : "age=" + (Now - a.Arrival).ToString("F2"));
                }
                if (Mine(a))
                {
                    if (a.RestoreController != null) Resume(a);
                    if (a.Loaded && a.Placeholder != null && a.Placeholder.CurrentEntity == null && !a.Placeholder.EntityIsDead
                        && RemoteDistance(a.Origin) < 26f)
                    { a.Placeholder.Spawn(); Bind(a); }
                    if (a.Entity == null || a.Placeholder == null || !a.Loaded) continue;
                    float distance = RemoteDistance(a.Entity.Position);
                    if (float.IsPositiveInfinity(distance)) distance = 50f;
                    if (distance < 28f && !a.Entity.InsideFrustum) a.Entity.OnFrustumEnter();
                    a.Priority += dt * (1f + 40f / (distance + 2f) + (a.Entity.IsOnScreen() ? 2f : 0f));
                    if (Now - a.LastSent >= 0.049f) candidates.Add(a);
                }
                else Puppet(a);
            }
            candidates.Sort(delegate(Actor x, Actor y) { return y.Priority.CompareTo(x.Priority); });
            foreach (Actor a in candidates)
            {
                if (s_tokens < 96f) break;
                State s = Capture(a, false); a.Latest = s;
                long bytes = EstimatedWireBytesSent;
                // Intern before opening the snapshot writer: symbol packets may arrive later; next state repairs it.
                Symbol(s.StateName); Symbol(s.Animation);
                Send(Snapshots, false, delegate(BinaryWriter w) { w.Write((byte)1); WriteId(w, a); w.Write(a.Epoch); WriteState(w, a, s, false); });
                s_tokens -= EstimatedWireBytesSent - bytes; a.Priority = 0; a.LastSent = Now;
            }
            FlushHits(); FlushShots();
            for (int i = s_copies.Count - 1; i >= 0; i--)
            { ProjectileIdentity p = s_copies[i]; if (p == null || Now >= p.Expires || !p.gameObject.activeInHierarchy) { if (p != null && p.gameObject.activeInHierarchy) InstantiateUtility.Destroy(p.gameObject); s_copies.RemoveAt(i); } }
            if (Now >= s_logAt) { s_logAt = Now + 5f; Services.Log.Info("E3 " + Diagnostics()); }
        }
        public static string Diagnostics() { return "actors=" + s_actors.Count + " ownerOnlyNoPlaceholder=" + Unmirrored + " scenesKept=" + KeptScenes + " streamMs=" + StreamMilliseconds.ToString("F2") + " txBytes=" + BytesSent + " rxSnapshotBytes=" + BytesReceived + " projectileUnresolved=" + UnresolvedProjectiles; }
        public struct Probe
        {
            public bool Present, Known, Owner, Dead, HasSnapshot, Mirrored, PlaceholderLoaded; public uint Epoch, Generation;
            public int OwnerSeat;
            public string ClassName, StateName, LiveState, SceneName, LastRemoval, LastDeath;
            public float Health, DeathTime, RenderTick, Angle; public Vector3 Position, OwnerPosition, Origin;
        }
        public static string[] LoadedEnemies()
        {
            Scan(); List<string> loaded = new List<string>();
            foreach (Actor a in s_actors.Values)
            {
                if (!a.Loaded || a.Placeholder == null) continue;
                string cls = a.ClassName != "" ? a.ClassName : a.Placeholder.GetType().Name.Replace("Placeholder", "");
                bool mirrored = a.Entity != null ? Supported(a.Entity) : Array.IndexOf(MirroredTypes, AccessTools.TypeByName(cls)) >= 0;
                loaded.Add(a.Key + "|" + cls + "|" + mirrored);
            }
            return loaded.ToArray();
        }
        // Every enemy this session has seen (loaded or not), as "key|className|mirrored"; origin via Inspect(key).Origin.
        public static string[] KnownEnemies()
        {
            List<string> known = new List<string>();
            foreach (Actor a in s_actors.Values)
            {
                string cls = a.ClassName != "" ? a.ClassName : a.Placeholder != null ? a.Placeholder.GetType().Name.Replace("Placeholder", "") : "";
                if (cls == "") continue;
                Type t = AccessTools.TypeByName(cls);
                known.Add(a.Key + "|" + cls + "|" + (t != null && Array.IndexOf(MirroredTypes, t) >= 0));
            }
            return known.ToArray();
        }
        // Test sweep: enable the (already loading) scenes around `origin`, list their enemies, then restore the camera
        // target in the same frame. LoadTestArea alone re-disables far scenes in its UpdatePosition before a caller can scan.
        public static string[] LoadedEnemiesAt(Vector3 origin)
        {
            if (Scenes.Manager == null) return LoadedEnemies();
            Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(origin.x - 26, origin.y - 18, 52, 36), true, true, false);
            Vector3 target = Scenes.Manager.CurrentCameraTargetPosition;
            try { Scenes.Manager.SetTargetPositions(origin); Scenes.Manager.EnableDisabledScenesAtPosition(false); return LoadedEnemies(); }
            finally { Scenes.Manager.SetTargetPositions(target); Scenes.Manager.UpdatePosition(); }
        }
        static string StateName(Enemy e)
        {
            if (e == null || e.Controller == null || e.Controller.StateMachine.CurrentState == null) return "";
            fsm.IState current = e.Controller.StateMachine.CurrentState;
            if (current.GetType() == typeof(fsm.State))
            {
                FieldInfo state = AccessTools.Field(e.GetType(), "State");
                if (state != null)
                {
                    object states = state.GetValue(e);
                    foreach (FieldInfo field in states.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                        if (object.ReferenceEquals(field.GetValue(states), current)) return field.Name;
                }
            }
            return current.GetType().FullName;
        }
        // Test helper: the owner (re)spawns a tracked enemy whose placeholder is loaded but has no live instance.
        public static void EnsureSpawned(string key)
        {
            Actor a; if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) || !Mine(a) || a.Placeholder == null || a.Placeholder.CurrentEntity != null) return;
            a.Placeholder.PerformRespawn(); a.Placeholder.Spawn(); Bind(a);
        }
        public static string LiveStateName(string key)
        {
            Actor a; if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a)) return "";
            string name = Mine(a) ? StateName(a.Entity) : a.Latest.StateName;
            int index = name.IndexOf('#'); return index < 0 ? name : name.Substring(0, index);
        }
        public static bool Engaged(string key)
        {
            string name = LiveStateName(key);
            if (name.StartsWith("DashOwl", StringComparison.Ordinal)) name = name.Substring("DashOwl".Length);
            foreach (string state in new[] { "Charging", "Shooting", "Alert", "Running", "Dash", "Attack", "Rolling",
                "Swim", "Walk", "Emerging", "Jumping", "Charge", "Laser" })
                if (name.IndexOf(state, StringComparison.Ordinal) >= 0) return true;
            Actor a; return name.IndexOf("Fall", StringComparison.Ordinal) >= 0 && s_actors.TryGetValue(key, out a) && (a.ClassName == "JumperEnemy" || a.ClassName == "DropSlugEnemy");
        }
        public static Probe Inspect(string key)
        {
            Probe p = new Probe(); p.Health = float.NaN; Actor a;
            if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a)) return p;
            p.Present = a.Entity != null && a.Entity.gameObject.activeInHierarchy; p.Known = a.Known; p.Owner = Mine(a); p.OwnerSeat = a.OwnerSeat; p.Epoch = a.Epoch;
            p.Dead = a.Latest.Killed; p.Health = a.Entity != null && a.Entity.DamageReciever != null ? a.Entity.DamageReciever.Health : a.Latest.Health;
            if (!p.Present && !p.Dead) p.Health = float.NaN;
            p.Origin = a.Origin; p.ClassName = a.ClassName; p.SceneName = a.SceneName; p.LastRemoval = a.LastRemoval; p.LastDeath = a.LastDeath;
            p.Mirrored = a.Entity != null && Supported(a.Entity); p.PlaceholderLoaded = a.Loaded && a.Placeholder != null; p.Generation = a.Generation;
            p.StateName = Mine(a) && a.Entity != null && a.Entity.Controller != null && a.Entity.Controller.StateMachine.CurrentState != null ? a.Entity.Controller.StateMachine.CurrentState.GetType().Name : a.Latest.StateName;
            p.LiveState = a.Entity != null && a.Entity.Controller != null && a.Entity.Controller.StateMachine.CurrentState != null
                ? a.Entity.Controller.StateMachine.CurrentState.GetType().Name : "";
            p.Position = a.Entity == null ? a.Latest.Position : a.Entity.Position; p.OwnerPosition = a.Latest.Position; p.HasSnapshot = a.Buffer.Count > 0; p.DeathTime = a.DeathTime; p.RenderTick = a.RenderTick;
            p.Angle = a.Entity != null ? a.Entity.transform.eulerAngles.z : 0f; return p;
        }
        public static string Describe(string key)
        {
            Probe p = Inspect(key);
            return "enemy=" + key + " class=" + p.ClassName + " scene=" + p.SceneName + " pos=" + p.Position + " origin=" + p.Origin
                + " hp=" + p.Health + " present=" + p.Present + " dead=" + p.Dead + " owner=" + p.Owner + " generation=" + p.Generation
                + " state=" + p.StateName + " lastDeath=" + p.LastDeath + " lastRemoval=" + p.LastRemoval;
        }
        public static void LoadTestArea(Vector3 origin)
        {
            if (Scenes.Manager == null) return;
            Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(origin.x - 26, origin.y - 18, 52, 36), true, true, false);
            Vector3 target = Scenes.Manager.CurrentCameraTargetPosition;
            try { Scenes.Manager.SetTargetPositions(origin); Scenes.Manager.EnableDisabledScenesAtPosition(false); }
            finally { Scenes.Manager.SetTargetPositions(target); Scenes.Manager.UpdatePosition(); }
        }
        public static string FindTestEnemy(Vector3 position) { return FindTestEnemy(position, null); }
        static readonly FieldInfo s_nearSein = AccessTools.Field(typeof(EntityController), "m_nearSein");
        public static string DebugAi(string key)
        {
            Actor a; if (string.IsNullOrEmpty(key) || !s_actors.TryGetValue(key, out a) || a.Entity == null) return "no entity";
            Enemy e = a.Entity; JumperEnemy j = e as JumperEnemy;
            object near = s_host.NearestPuppet(e.Position);
            return "active=" + e.gameObject.activeInHierarchy + " suspended=" + e.IsSuspended + " frustum=" + e.InsideFrustum
                + " mNearSein=" + (e.Controller == null ? "-" : s_nearSein.GetValue(e.Controller).ToString())
                + " p2Nearer=" + s_host.P2IsNearer(e.Position) + " remoteDist=" + (float.IsPositiveInfinity(RemoteDistance(e.Position)) ? "-" : RemoteDistance(e.Position).ToString("F1"))
                + " localDist=" + (Local == null ? "-" : Vector3.Distance(e.Position, Local.Position).ToString("F1"))
                + " puppetCanMove=" + (near == null || s_host.PuppetSein(near) == null ? "-" : s_host.PuppetSein(near).Controller.CanMove.ToString())
                + (j == null ? "" : " chargeRange=" + j.Settings.ChargeRange + " outOfZone=" + j.OutOfJumpingZone() + " stomp=" + j.Settings.HasStompExplosion);
        }
        public static string FindTestEnemyOfClass(Vector3 near, string className, HashSet<string> rejected)
        {
            Scan(); List<Actor> candidates = new List<Actor>();
            foreach (Actor a in s_actors.Values)
            {
                if (!a.Loaded || a.Placeholder == null || !EnemyPlaceholder(a.Placeholder)) continue;
                if (rejected != null && rejected.Contains(a.Key)) continue;
                if (a.Entity != null ? a.Entity.GetType().Name != className || !Supported(a.Entity)
                    : a.Placeholder.GetType().Name != className + "Placeholder") continue;
                candidates.Add(a);
            }
            candidates.Sort(delegate(Actor a, Actor b) { return Vector3.Distance(a.Origin, near).CompareTo(Vector3.Distance(b.Origin, near)); });
            foreach (Actor a in candidates)
            {
                if (a.Placeholder.CurrentEntity == null) { a.Placeholder.PerformRespawn(); a.Placeholder.Spawn(); Bind(a); }
                if (a.Entity != null && a.Entity.GetType().Name == className && Supported(a.Entity)) return a.Key;
            }
            return "";
        }
        public static string FindTestEnemy(Vector3 position, ICollection<string> rejected)
        {
            Scan(); List<Actor> candidates = new List<Actor>();
            foreach (Actor a in s_actors.Values)
            {
                if (!a.Loaded || a.Placeholder == null || !EnemyPlaceholder(a.Placeholder)) continue;
                if (rejected != null && rejected.Contains(a.Key)) continue;
                if (a.Entity != null && !Supported(a.Entity)) continue;
                candidates.Add(a);
            }
            candidates.Sort(delegate(Actor a, Actor b) {
                bool groundA = a.Placeholder is JumperEnemyPlaceholder || a.Placeholder is RammingEnemyPlaceholder || a.Placeholder is SpitterEnemyPlaceholder;
                bool groundB = b.Placeholder is JumperEnemyPlaceholder || b.Placeholder is RammingEnemyPlaceholder || b.Placeholder is SpitterEnemyPlaceholder;
                if (groundA != groundB) return groundA ? -1 : 1;
                return Vector3.Distance(a.Origin, position).CompareTo(Vector3.Distance(b.Origin, position)); });
            foreach (Actor a in candidates)
            {
                if (a.Placeholder.CurrentEntity == null) { a.Placeholder.PerformRespawn(); a.Placeholder.Spawn(); Bind(a); }
                if (a.Entity != null && Supported(a.Entity)) return a.Key;
            }
            return "";
        }
        public static void PrepareTestEnemy(string key, float health)
        {
            Actor a; if (!s_actors.TryGetValue(key, out a) || !Mine(a) || a.Placeholder == null) throw new InvalidOperationException("E3 test enemy not owned/loaded");
            if (!a.TestSettings && a.Placeholder != null)
            {
                a.TestSettings = true; a.TestRespawnTime = a.Placeholder.RespawnTime; a.TestRespawnOnScreen = a.Placeholder.RespawnOnScreen; a.TestMinDistance = a.Placeholder.MinDistanceFromPlayer;
                a.Placeholder.RespawnTime = 3f; a.Placeholder.RespawnOnScreen = true; a.Placeholder.MinDistanceFromPlayer = 0f;
            }
            if (a.Entity == null) { a.Placeholder.PerformRespawn(); a.Placeholder.Spawn(); Bind(a); }
            a.Entity.DamageReciever.Health = health; a.Latest = Capture(a, true); SendLife(a, 1);
        }
        // Test only (T-027): the owner holds a surface crawler (TraceGroundMovement) at a body angle; null lets it go.
        public static void TestHoldAngle(string key, float? angle)
        {
            Actor a; if (!s_actors.TryGetValue(key, out a) || !Mine(a) || a.Entity == null) return;
            TraceGroundMovement trace = a.Entity.GetComponent<TraceGroundMovement>(); if (trace == null) return;
            trace.IsSuspended = angle.HasValue;
            if (angle.HasValue) a.Entity.transform.eulerAngles = new Vector3(0f, 0f, angle.Value);
        }
        public static void EndTestEnemy(string key)
        {
            Actor a; if (!s_actors.TryGetValue(key, out a) || !a.TestSettings) return;
            if (a.Placeholder != null) { a.Placeholder.RespawnTime = a.TestRespawnTime; a.Placeholder.RespawnOnScreen = a.TestRespawnOnScreen; a.Placeholder.MinDistanceFromPlayer = a.TestMinDistance; }
            a.TestSettings = false;
        }
        public static string ProtocolSelfTestFailure = "";
        public static bool ProtocolSelfTest()
        {
            try
            {
                Actor a = new Actor(); a.Scene = new MoonGuid(1, 2, 3, 4); a.Id = new MoonGuid(5, 6, 7, 8);
                a.Origin = new Vector3(-500, -20, 0);
                State s = new State(); s.Tick = 123; s.Generation = 7; s.Position = new Vector3(-497.123f, -18.456f, 0);
                s.Velocity = new Vector2(-12.345f, 9.876f); s.Health = 17.5f; s.Alive = s.Left = s.DealerActive = true;
                s.StateName = "JumperEnemyFallState"; s.Animation = "asset|frame-guid"; s.Respawn = 1.25f;
                s.Controller = new byte[] { 3, 0, 0, 0, 0, 0, 64, 63 }; s.Applied = new ulong[] { 0x100000002UL, 0x200000003UL };
                s.TraceMovement = new byte[] { 1, 2, 3, 4 }; s.ShouldStomp = true;
                for (int mode = 0; mode < 4; mode++)
                {
                    s.HasVisualAngle = mode < 2; s.VisualAngle = 237.5f;
                    if (mode == 1) { s.StateName = s.Animation = ""; s.Controller = new byte[0]; s.Applied = new ulong[0]; }
                    if (mode == 2) { s.Position = new Vector3(900, -20, 0); s.Velocity = new Vector2(400, 0); }
                    MemoryStream ms = new MemoryStream(); BinaryWriter w = new BinaryWriter(ms);
                    bool full = mode == 0 || mode == 2;
                    WriteId(w, a); WriteState(w, a, s, full); w.Flush();
                    ms.Position = 0; BinaryReader r = new BinaryReader(ms);
                    MoonGuid scene = new MoonGuid(r), id = new MoonGuid(r); State copy = ReadState(r, a, full);
                    if (Key(scene, id) != Key(a.Scene, a.Id) || Key(scene, id) == Key(id, scene)) throw new Exception("scene-qualified identity");
                    if (Math.Abs(copy.Position.x - s.Position.x) > 0.006f || Math.Abs(copy.Position.y - s.Position.y) > 0.006f
                        || Math.Abs(copy.Velocity.x - s.Velocity.x) > 0.006f || Math.Abs(copy.Velocity.y - s.Velocity.y) > 0.006f) throw new Exception("quantization/range escape");
                    if (copy.Health != s.Health || Flags(copy) != Flags(s) || copy.Tick != s.Tick || copy.Generation != s.Generation || ms.Position != ms.Length) throw new Exception("wire alignment/flags");
                    if (full && copy.ShouldStomp != s.ShouldStomp) throw new Exception("handoff lost stomp flag");
                    if (copy.HasVisualAngle != s.HasVisualAngle || (s.HasVisualAngle && copy.VisualAngle != s.VisualAngle)) throw new Exception("wire lost visual angle");
                    if (mode == 0 && (copy.StateName != s.StateName || copy.Controller.Length != 8 || copy.TraceMovement.Length != 4 || copy.TraceMovement[3] != 4 || copy.Applied.Length != 2 || copy.Applied[1] != s.Applied[1] || copy.Respawn != s.Respawn)) throw new Exception("handoff lost state/movement/hit history");
                    if (mode == 1 && ms.Length > 77) throw new Exception("snapshot exceeds bandwidth estimate");
                }
                State old = new State(); old.Tick = 100; old.Generation = 1; old.Killed = true;
                State incoming = new State(); incoming.Tick = 101; incoming.Generation = 1; incoming.Alive = true;
                if (AcceptState(old, incoming)) throw new Exception("late alive snapshot resurrected death");
                incoming.Generation = 2; if (!AcceptState(old, incoming)) throw new Exception("respawn generation rejected");
                incoming.Generation = 0; if (AcceptState(old, incoming)) throw new Exception("old generation accepted");
                incoming.Generation = 1; incoming.Killed = true; incoming.Tick = 99; if (AcceptState(old, incoming)) throw new Exception("reordered state accepted");
                old.Killed = false; old.Tick = uint.MaxValue - 1; incoming.Tick = 1;
                if (!AcceptState(old, incoming)) throw new Exception("tick wrap rejected");
                old.Tick = 1000; incoming.Tick = 100;
                if (AcceptState(old, incoming) || !AcceptState(old, incoming, true)) throw new Exception("ownership transfer mixed peer tick clocks");
                if (!Remember(a, 23) || Remember(a, 23) || !Remember(a, 24)) throw new Exception("hit deduplication");
                if (!AcceptOwnerHit(a, 50) || AcceptOwnerHit(a, 50) || !AcceptOwnerHit(a, 51)) throw new Exception("reentrant owner hit applied twice");
                if (!InPartnerScreen(new Vector3(126, 218, 0), new Vector3(100, 200, 0))
                    || InPartnerScreen(new Vector3(127, 200, 0), new Vector3(100, 200, 0))
                    || InPartnerScreen(new Vector3(100, 219, 0), new Vector3(100, 200, 0))) throw new Exception("partner-screen spawn/despawn protection");
                MemoryStream damageBytes = new MemoryStream(); BinaryWriter damageWriter = new BinaryWriter(damageBytes);
                Damage attack = new Damage(3f, new Vector2(1, 2), new Vector3(-206.4f, -288.4f, 0), DamageType.SpiritFlame, null);
                WriteDamage(damageWriter, attack); damageWriter.Flush(); damageBytes.Position = 0;
                Damage received = ReadDamageValue(new BinaryReader(damageBytes), null);
                if (received.Amount != 3f || received.Type != DamageType.SpiritFlame || received.Force.x != 1 || received.Force.y != 2
                    || damageBytes.Position != damageBytes.Length) throw new Exception("damage wire amount/type/force");
                ProtocolSelfTestFailure = ""; return true;
            }
            catch (Exception e) { ProtocolSelfTestFailure = e.GetType().Name + ": " + e.Message; return false; }
        }
        public static void TestAttack(string key, float amount, bool duplicate)
        {
            Actor a; if (!s_actors.TryGetValue(key, out a) || a.Entity == null || Local == null) throw new InvalidOperationException("E3 test enemy unavailable: " + Describe(key));
            DamageType type = a.Entity is KamikazeSootEnemy ? DamageType.LevelUp : DamageType.SpiritFlame;
            Damage d = new Damage(amount, Vector2.zero, a.Entity.Position, type, Local.gameObject);
            d.DealToComponents(a.Entity.DamageReciever.gameObject); if (duplicate) d.DealToComponents(a.Entity.DamageReciever.gameObject);
        }
    }
}
