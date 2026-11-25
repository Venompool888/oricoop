using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Core;
using Game;
using HarmonyLib;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop
{
    public static class CoopTraceHooks
    {
        static bool s_init;
        static readonly List<SeinCharacter> s_players = new List<SeinCharacter>();
        static readonly Dictionary<int, Collider> s_ground = new Dictionary<int, Collider>();
        static readonly Dictionary<int, Transform> s_standing = new Dictionary<int, Transform>();
        static readonly Dictionary<int, Component> s_pushing = new Dictionary<int, Component>();
        static readonly Dictionary<int, EnemyLife> s_enemies = new Dictionary<int, EnemyLife>();
        static readonly List<Nearby> s_objects = new List<Nearby>();
        static readonly Dictionary<int, ActionSequence> s_abilitySequences = new Dictionary<int, ActionSequence>();
        static readonly FieldInfo Pushable = AccessTools.Field(typeof(SeinGrabBlock), "m_pushable");
        static readonly FieldInfo GroundPlatform = AccessTools.Field(typeof(MovingPlatformsController), "m_oldGroundPlatform");
        static readonly FieldInfo HudOpacity = AccessTools.Field(typeof(SeinUI), "m_lastOpacity");
        static readonly FieldInfo AbilitySein = AccessTools.Field(typeof(PlayerAbilities), "m_sein");
        static readonly FieldInfo OriDestination = AccessTools.Field(typeof(Ori), "m_moveToPositionEndPosition");
        static NetPeer s_peer;

        sealed class EnemyLife
        {
            public Enemy Enemy;
            public string Name, State;
            public bool SeenAlive, Dead;
        }

        sealed class Nearby
        {
            public Component Object;
            public Enemy Enemy;
            public float Distance;
            public Vector3 LastPosition;
            public float LastTime;
        }

        sealed class Hit
        {
            public float Amount, Health;
            public GameObject Sender;
            public DamageType Type;
            public Enemy Enemy;
            public string Name;
        }

        static void Patch(List<string> failed, Type type, string method, string prefix, string postfix)
        {
            try
            {
                MethodInfo target = AccessTools.Method(type, method);
                if (target == null) throw new MissingMethodException();
                HarmonyMethod before = prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(CoopTraceHooks), prefix));
                if (before != null) before.priority = Priority.First + 1;
                Plugin.H.Patch(target,
                    before,
                    postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(CoopTraceHooks), postfix)));
            }
            catch { failed.Add(type.Name + "." + method); }
        }

        static void Init()
        {
            s_init = true;
            List<string> failed = new List<string>();
            Patch(failed, typeof(SeinDamageReciever), "OnRecieveDamage", "PlayerHitBefore", "PlayerHitAfter");
            Patch(failed, typeof(PlayerAbilities), "SetAbility", "AbilityBefore", "AbilityAfter");
            Patch(failed, typeof(PlatformMovement), "OnCollisionGround", null, "GroundAfter");
            Patch(failed, typeof(Entity), "Awake", null, "EnemySpawn");
            Patch(failed, typeof(Entity), "OnDestroy", "EnemyDestroy", null);
            Patch(failed, typeof(EntityDamageReciever), "OnPoolSpawned", null, "EnemyRespawn");
            Patch(failed, typeof(EntityDamageReciever), "OnRecieveDamage", "EnemyHitBefore", "EnemyHitAfter");
            Patch(failed, typeof(ActionSequence), "Perform", "SequenceBefore", "SequenceAfter");
            Patch(failed, typeof(ActionSequence), "UpdateActions", "SequenceAfter", "SequenceAfter");
            Patch(failed, typeof(ActionSequence), "Stop", null, "SequenceAfter");
            Patch(failed, typeof(CutsceneController), "ChangeState", "CutsceneBefore", "CutsceneAfter");
            Patch(failed, typeof(GetAbilityPedestal), "ActivatePedestal", "PedestalBefore", "PedestalAfter");
            Patch(failed, typeof(GameplayCamera), "FixedUpdate", null, "CameraAfter");
            Patch(failed, typeof(CameraPuppetController), "UpdatePuppet", null, "CameraPuppetAfter");
            Patch(failed, typeof(SeinUI), "FixedUpdate", null, "HudAfter");
            Patch(failed, typeof(MessageBoxVisibility), "Advance", null, "MessageAfter");
            Patch(failed, typeof(MessageBoxVisibility), "OnDestroy", "MessageDestroyed", null);
            Patch(failed, typeof(ShowStoryTextAction), "Perform", null, "StoryTextAfter");
            Patch(failed, typeof(ShowStoryTextAction), "OnMessageScreenHide", null, "StoryTextAfter");
            // Ori (the spirit companion) is polled from Tick, not patched: a FixedUpdate patch on it coincided with the
            // harness's "companion far from P2" visual check failing.
            Patch(failed, typeof(SaveGameController), "SaveToFile", null, "SaveAfter");
            Patch(failed, typeof(SaveGameController), "LoadFromReader", null, "LoadAfter");
            Patch(failed, typeof(GameController), "SuspendGameplay", null, "PauseAfter");
            Patch(failed, typeof(GameController), "ResumeGameplay", null, "PauseAfter");
            Patch(failed, typeof(DifficultyController), "ChangeDifficulty", null, "DifficultyAfter");
            Patch(failed, typeof(DifficultyController), "SetDifficulty", null, "DifficultyAfter");
            Patch(failed, typeof(SceneRoot), "LateStart", null, "SceneLoaded");
            Patch(failed, typeof(SceneRoot), "Unload", "SceneUnloading", null);
            ShooterProbe.Init(failed);   // T-250 probe: projectile-shooter sequences (trace only)
            if (failed.Count != 0) Plugin.Log.LogWarning("trace hooks failed: " + string.Join(", ", failed.ToArray()));
        }

        public static void Tick()
        {
            if (!s_init && Plugin.H != null) Init();
            try
            {
                if (CoopTrace.On("player", 1) || CoopTrace.On("phys", 1) || CoopTrace.On("enemy", 2))
                {
                    s_players.Clear();
                    SeinCharacter local = P2Context.P1 != null ? P2Context.P1 : Characters.Sein;
                    if (local != null) s_players.Add(local);
                    foreach (Puppet p in Players.Remotes)
                        if (p.Sein != null && !s_players.Contains(p.Sein)) s_players.Add(p.Sein);
                    bool sample = CoopTrace.SampleDue("player") && CoopTrace.Every("hooks.player.sample", 0.25f);
                    foreach (SeinCharacter s in s_players)
                    {
                        try { ObservePlayer(s, sample); } catch { }
                    }
                }
                if (CoopTrace.On("enemy", 1)) ObserveEnemies();
                if ((CoopTrace.SampleDue("phys") || CoopTrace.SampleDue("enemy")) && CoopTrace.Every("hooks.world.sample", 0.45f)) SampleWorld();
                if (CoopTrace.On("story", 1))
                {
                    List<int> ended = new List<int>();
                    foreach (KeyValuePair<int, ActionSequence> entry in s_abilitySequences)
                    {
                        if (entry.Value != null) SequenceAfter(entry.Value);
                        else
                        {
                            if (CoopTrace.Changed("hooks.sequence." + entry.Key, "False"))
                                CoopTrace.Event("story", "ability.cutscene.end", "id", entry.Key, "reason", "destroy");
                            ended.Add(entry.Key);
                        }
                    }
                    foreach (int id in ended) s_abilitySequences.Remove(id);
                }
                if (CoopTrace.On("hud", 1) && Game.UI.SeinUI != null) HudAfter(Game.UI.SeinUI);
                if (CoopTrace.On("story", 1))
                {
                    if (Characters.Ori != null) OriAfter(Characters.Ori);
                    Ori companion = P2Context.P2Companion;
                    if (companion != null && companion != Characters.Ori) OriAfter(companion);
                }
                if (CoopTrace.On("save", 1))
                {
                    if (GameController.Instance != null) PauseAfter(GameController.Instance);
                    if (DifficultyController.Instance != null) DifficultyAfter(DifficultyController.Instance);
                }
                ObserveNet();
            }
            catch { }
            try { CoopTraceAudio.Tick(); } catch { }   // T-252: what the sound depends on (trace only)
        }

        static string Id(UnityEngine.Object o)
        {
            return o == null ? "-" : o.name + "#" + o.GetInstanceID();
        }

        static string Who(SeinCharacter s)
        {
            if (s == null) return "-";
            Puppet p = Players.OfSein(s);
            return "P" + (p == null ? Math.Max(1, Players.LocalSeat) : p.Seat);
        }

        static string Animation(CharacterAnimationSystem animation)
        {
            return animation == null || animation.Animator == null ? "-" : Id(animation.Animator.CurrentTextureAnimationTransitions);
        }

        static string PlayerState(SeinCharacter s)
        {
            SeinController c = s.Controller;
            StringBuilder text = new StringBuilder();
            if (c.IsPlayingAnimation) text.Append("animation,");
            if (c.IsBashing) text.Append("bash,");
            if (c.IsStomping) text.Append("stomp,");
            if (c.IsSwimming) text.Append("swim,");
            if (c.IsGliding) text.Append("glide,");
            if (c.IsPushPulling) text.Append("grab:" + s.Abilities.GrabBlock.CurrentState + ",");
            if (s.CutsceneBlocked != null && s.CutsceneBlocked.Active) text.Append("cutscene:" + s.CutsceneBlocked.CurrentState + ",");
            if (s.IsSuspended) text.Append("suspended,");
            if (c.LockMovementInput) text.Append("movementLocked,");
            return text.Length == 0 ? (s.IsOnGround ? "ground" : "air") : text.ToString();
        }

        static void ObservePlayer(SeinCharacter s, bool sample)
        {
            if (CoopTrace.On("player", 1))
            {
                string key = "hooks.player." + s.GetInstanceID();
                string who = Who(s);
                bool grounded = s.IsOnGround;
                if (CoopTrace.Changed(key + ".ground", grounded.ToString()))
                    CoopTrace.Event("player", grounded ? "land" : "air", "who", who, "pos", s.Position);
                bool dead = s.Mortality.Health.Amount <= 0 || Revive.IsDowned(s);
                bool first = CoopTrace.Changed(key + ".seen", "1");
                if (CoopTrace.Changed(key + ".dead", dead.ToString()) && (!first || dead))
                    CoopTrace.Event("player", dead ? "death" : "revive", "who", who, "hp", s.Mortality.Health.Amount, "active", s.Active);
                if (sample && CoopTrace.On("player", 2))
                {
                    Puppet p = Players.OfSein(s);
                    Ori ori = p == null ? Characters.Ori : p.Companion;
                    CoopTrace.Sample("player", "state", "who", who, "id", s, "pos", s.Position, "vel", s.PhysicsSpeed,
                        "grounded", grounded, "state", PlayerState(s), "anim", Animation(s.Animation), "hp", s.Mortality.Health.Amount,
                        "active", s.gameObject.activeInHierarchy, "ori", ori, "ori_pos", ori == null ? (object)null : ori.Position,
                        "ori_state", ori == null ? "-" : ori.CurrentState.ToString());
                }
            }
            if (!CoopTrace.On("phys", 1)) return;
            string id = s.GetInstanceID().ToString();
            SeinGrabBlock grab = s.Abilities.GrabBlock;
            Component pushed = grab != null && grab.IsGrabbing ? Pushable.GetValue(grab) as Component : null;
            Component oldPush;
            s_pushing.TryGetValue(s.GetInstanceID(), out oldPush);
            if (CoopTrace.Changed("hooks.push." + id, Id(pushed)))
            {
                if (oldPush != null) CoopTrace.Event("phys", "push.end", "who", Who(s), "object", oldPush);
                if (pushed != null) CoopTrace.Event("phys", "push.start", "who", Who(s), "object", pushed, "state", grab.CurrentState);
                s_pushing[s.GetInstanceID()] = pushed;
            }
            Collider ground;
            s_ground.TryGetValue(s.GetInstanceID(), out ground);
            Transform standing = null;
            if (s.IsOnGround)
            {
                standing = ground == null ? null : ground.transform;
                if (standing == null && s.PlatformBehaviour.PlatformMovement.MovingPlatforms != null)
                    standing = GroundPlatform.GetValue(s.PlatformBehaviour.PlatformMovement.MovingPlatforms) as Transform;
            }
            Transform oldStanding;
            s_standing.TryGetValue(s.GetInstanceID(), out oldStanding);
            if (CoopTrace.Changed("hooks.stand." + id, Id(standing)))
            {
                if (oldStanding != null) CoopTrace.Event("phys", "stand.leave", "who", Who(s), "object", oldStanding);
                if (standing != null) CoopTrace.Event("phys", "stand.enter", "who", Who(s), "object", standing);
                s_standing[s.GetInstanceID()] = standing;
            }
            if (!s.IsOnGround) s_ground.Remove(s.GetInstanceID());
        }

        static void GroundAfter(PlatformMovement __instance, Collider __1)
        {
            try
            {
                if (!CoopTrace.On("phys", 1)) return;
                SeinCharacter s = __instance.GetComponentInParent<SeinCharacter>();
                if (s != null) s_ground[s.GetInstanceID()] = __1;
            }
            catch { }
        }

        static void PlayerHitBefore(SeinDamageReciever __instance, Damage __0, out Hit __state)
        {
            __state = null;
            try
            {
                if (!CoopTrace.On("player", 1)) return;
                __state = new Hit { Amount = __0.Amount, Health = __instance.Sein.Mortality.Health.Amount, Sender = __0.Sender, Type = __0.Type };
            }
            catch { }
        }

        static void PlayerHitAfter(SeinDamageReciever __instance, Damage __0, Hit __state)
        {
            try
            {
                if (!CoopTrace.On("player", 1) || __state == null) return;
                float hp = __instance.Sein.Mortality.Health.Amount;
                CoopTrace.Event("player", "damage", "who", Who(__instance.Sein), "damage", __state.Amount, "final_damage", __0.Amount,
                    "source", __state.Sender, "type", __state.Type, "hp_before", __state.Health, "hp", hp,
                    "applied", __state.Health - hp, "effective_ratio", __state.Amount == 0 ? 0 : (__state.Health - hp) / __state.Amount,
                    "difficulty", DifficultyController.Instance == null ? "-" : DifficultyController.Instance.Difficulty.ToString());
                ObservePlayer(__instance.Sein, false);
            }
            catch { }
        }

        static void AbilityBefore(PlayerAbilities __instance, AbilityType __0, out bool __state)
        {
            __state = false;
            try { if (!CoopTrace.On("player", 1)) return; __state = __instance.HasAbility(__0); } catch { }
        }

        static void AbilityAfter(PlayerAbilities __instance, AbilityType __0, bool __state)
        {
            try
            {
                if (!CoopTrace.On("player", 1)) return;
                bool has = __instance.HasAbility(__0);
                string key = "hooks.ability." + __instance.GetInstanceID() + "." + __0;
                if (CoopTrace.Changed(key, has.ToString()) && has && !__state)
                    CoopTrace.Event("player", "ability", "who", Who(AbilitySein.GetValue(__instance) as SeinCharacter), "ability", __0);
            }
            catch { }
        }

        static void EnemySpawn(Entity __instance)
        {
            try { if (!CoopTrace.On("enemy", 1)) return; TrackEnemy(__instance as Enemy, "awake"); } catch { }
        }

        static void EnemyRespawn(EntityDamageReciever __instance)
        {
            try { if (!CoopTrace.On("enemy", 1)) return; TrackEnemy(__instance.Entity as Enemy, "pool"); } catch { }
        }

        static void TrackEnemy(Enemy enemy, string source)
        {
            if (enemy == null) return;
            int id = enemy.GetInstanceID();
            EnemyLife life;
            bool fresh = !s_enemies.TryGetValue(id, out life);
            if (fresh) { life = new EnemyLife { Enemy = enemy, Name = Id(enemy) }; s_enemies[id] = life; }
            if (enemy.DamageReciever != null && enemy.DamageReciever.Health > 0f) { life.SeenAlive = true; life.Dead = false; }
            string state = enemy.gameObject.activeInHierarchy ? "active" : "disabled";
            if (fresh || life.State != state || source == "pool")
                CoopTrace.Event("enemy", fresh || source == "pool" ? "spawn" : state, "id", life.Name, "pos", enemy.Position, "source", source);
            life.State = state;
        }

        static void EnemyDestroy(Entity __instance)
        {
            try
            {
                if (!CoopTrace.On("enemy", 1) || !(__instance is Enemy)) return;
                CoopTrace.Event("enemy", "destroy", "id", __instance, "pos", __instance.Position);
                s_enemies.Remove(__instance.GetInstanceID());
            }
            catch { }
        }

        static void ObserveEnemies()
        {
            List<int> gone = new List<int>();
            foreach (KeyValuePair<int, EnemyLife> entry in s_enemies)
            {
                EnemyLife life = entry.Value;
                if (life.Enemy == null)
                {
                    CoopTrace.Event("enemy", "disappear", "id", life.Name, "previous", life.State);
                    gone.Add(entry.Key);
                }
                else
                {
                    EntityDamageReciever receiver = life.Enemy.DamageReciever;
                    if (receiver != null)
                    {
                        bool dead = receiver.Health <= 0f;
                        if (life.SeenAlive && dead && !life.Dead)
                            CoopTrace.Event("enemy", "death", "id", life.Name, "hp", receiver.Health, "source", "observed",
                                "death_action", receiver.DeathAction, "disable", receiver.DisableWhenNoHealthLeft, "destroy", receiver.DestroyWhenNoHealthLeft);
                        if (!dead) life.SeenAlive = true;
                        life.Dead = dead;
                    }
                    string state = life.Enemy.gameObject.activeInHierarchy ? "active" : "disabled";
                    if (state != life.State)
                    {
                        CoopTrace.Event("enemy", state, "id", life.Name, "pos", life.Enemy.Position);
                        life.State = state;
                    }
                }
            }
            foreach (int id in gone) s_enemies.Remove(id);
        }

        static void EnemyHitBefore(EntityDamageReciever __instance, Damage __0, out Hit __state)
        {
            __state = null;
            try
            {
                if (!CoopTrace.On("enemy", 1)) return;
                Enemy enemy = __instance.Entity as Enemy;
                if (enemy == null) return;
                __state = new Hit { Enemy = enemy, Name = Id(enemy), Amount = __0.Amount, Health = __instance.Health, Sender = __0.Sender, Type = __0.Type };
            }
            catch { }
        }

        static void EnemyHitAfter(EntityDamageReciever __instance, Damage __0, Hit __state)
        {
            try
            {
                if (!CoopTrace.On("enemy", 1) || __state == null) return;
                CoopTrace.Event("enemy", "damage", "id", __state.Name, "damage", __state.Amount, "final_damage", __0.Amount,
                    "attacker", __state.Sender, "type", __state.Type, "hp_before", __state.Health, "hp", __instance.Health,
                    "applied", __state.Health - __instance.Health);
                if (__state.Health > 0 && __instance.Health <= 0)
                {
                    EnemyLife life;
                    if (__state.Enemy != null && s_enemies.TryGetValue(__state.Enemy.GetInstanceID(), out life)) life.Dead = true;
                    PerformingAction action = __instance.DeathAction as PerformingAction;
                    CoopTrace.Event("enemy", "death", "id", __state.Name, "type", __state.Type, "attacker", __state.Sender,
                        "death_action", __instance.DeathAction, "action_running", action == null ? (object)null : action.IsPerforming,
                        "disable", __instance.DisableWhenNoHealthLeft, "destroy", __instance.DestroyWhenNoHealthLeft,
                        "active", __state.Enemy != null && __state.Enemy.gameObject.activeInHierarchy);
                }
            }
            catch { }
        }

        static float Distance(Vector3 position)
        {
            float distance = float.MaxValue;
            foreach (SeinCharacter s in s_players) distance = Mathf.Min(distance, (s.Position - position).sqrMagnitude);
            return distance;
        }

        static void AddNearby(Dictionary<int, Nearby> found, Component component, Enemy enemy, float radius)
        {
            if (component == null || !component.gameObject.activeInHierarchy) return;
            float distance = Distance(component.transform.position);
            if (distance > radius * radius) return;
            if (enemy == null && (component.GetComponentInParent<Enemy>() != null || component.GetComponentInParent<SeinCharacter>() != null)) return;
            int id = component.gameObject.GetInstanceID();
            if (!found.ContainsKey(id)) found[id] = new Nearby { Object = component, Enemy = enemy, Distance = distance };
        }

        static readonly List<Component> s_physCandidates = new List<Component>();
        static readonly List<Enemy> s_enemyCandidates = new List<Enemy>();

        static void SampleWorld()
        {
            bool phys = CoopTrace.SampleDue("phys"), enemies = CoopTrace.SampleDue("enemy");
            Dictionary<int, Nearby> found = new Dictionary<int, Nearby>();
            // FindObjectsOfType is slow in big scenes: refresh the candidate lists every 3 s, sample them every call.
            if (CoopTrace.Every("hooks.world.scan", 3f))
            {
                s_physCandidates.Clear();
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<PushPullBlock>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<Rope>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<Rigidbody>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<SinMovingPlatform>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<SequencePlatform>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<SequencePlatformB>());
                s_physCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<RiseSinkPlatform>());
                s_enemyCandidates.Clear();
                s_enemyCandidates.AddRange(UnityEngine.Object.FindObjectsOfType<Enemy>());
                if (CoopTrace.On("enemy", 1)) foreach (Enemy enemy in s_enemyCandidates) TrackEnemy(enemy, "scan");
            }
            if (phys)
            {
                SamplePlayerColliders();
                foreach (Component o in s_physCandidates) if (o != null) AddNearby(found, o, null, 15f);
            }
            if (enemies)
                foreach (Enemy enemy in s_enemyCandidates) if (enemy != null) AddNearby(found, enemy, enemy, 20f);
            Dictionary<int, Nearby> previous = new Dictionary<int, Nearby>();
            foreach (Nearby n in s_objects) if (n.Object != null) previous[n.Object.gameObject.GetInstanceID()] = n;
            s_objects.Clear();
            s_objects.AddRange(found.Values);
            s_objects.Sort(delegate(Nearby a, Nearby b) { return a.Distance.CompareTo(b.Distance); });
            int count = Math.Min(12, s_objects.Count);
            for (int i = 0; i < count; i++)
            {
                Nearby n = s_objects[i];
                try
                {
                    Nearby old;
                    Vector3 pos = n.Object.transform.position;
                    object velocity = null;
                    float now = Time.realtimeSinceStartup;
                    if (previous.TryGetValue(n.Object.gameObject.GetInstanceID(), out old) && old.LastTime < now)
                        velocity = (pos - old.LastPosition) / (now - old.LastTime);
                    n.LastPosition = pos; n.LastTime = now;
                    if (n.Enemy != null) SampleEnemy(n.Enemy, velocity);
                    else SamplePhysics(n.Object, velocity);
                }
                catch { }
            }
            if (s_objects.Count > count) s_objects.RemoveRange(count, s_objects.Count - count);
        }

        static void SampleEnemy(Enemy enemy, object observedVelocity)
        {
            if (!CoopTrace.On("enemy", 2)) return;
            Rigidbody rb = enemy.GetComponent<Rigidbody>();
            PlatformMovement movement = enemy.GetComponent<PlatformMovement>();
            FlyMovement fly = enemy.GetComponent<FlyMovement>();
            TraceGroundMovement trace = enemy.GetComponent<TraceGroundMovement>();
            object velocity = movement != null ? (object)movement.WorldSpeed : fly != null ? (object)fly.Velocity :
                trace != null ? (object)(trace.Right * trace.Speed) : rb != null ? (object)rb.velocity : observedVelocity;
            string state = enemy.Controller == null || enemy.Controller.StateMachine.CurrentState == null ? "-" : enemy.Controller.StateMachine.CurrentState.GetType().Name;
            List<object> fields = new List<object>(new object[] { "id", enemy, "pos", enemy.Position, "vel", velocity, "state", state,
                "anim", Animation(enemy.Animation), "suspended", enemy.IsSuspended, "kinematic", rb == null ? (object)null : rb.isKinematic,
                "sleeping", rb == null ? (object)null : rb.IsSleeping(), "movement_suspended", movement == null ? (object)null : movement.IsSuspended,
                "state_time", enemy.Controller == null ? (object)null : enemy.Controller.StateMachine.CurrentStateTime,
                "hp", enemy.DamageReciever == null ? (object)null : enemy.DamageReciever.Health });
            foreach (RespawningPlaceholder p in RespawningPlaceholder.All)
            {
                if (p == null || p.CurrentEntity != enemy || p.MoonGuid == null) continue;
                SceneRoot root = SceneRoot.FindFromTransform(p.transform);
                if (root == null || root.MetaData == null) break;
                EnemySync.Probe probe = EnemySync.Inspect(root.MetaData.SceneMoonGuid.ToString() + ":" + p.MoonGuid.ToString());
                if (probe.Known) { fields.Add("owner"); fields.Add(probe.Owner); fields.Add("owner_scope"); fields.Add("local_simulation"); }
                break;
            }
            CoopTrace.Sample("enemy", "state", fields.ToArray());
        }

        static void SamplePlayerColliders()
        {
            if (!CoopTrace.On("phys", 2)) return;
            List<object> fields = new List<object>();
            foreach (SeinCharacter s in s_players)
            {
                PlatformMovement movement = s.PlatformBehaviour.PlatformMovement;
                Component pushed; Transform ground;
                s_pushing.TryGetValue(s.GetInstanceID(), out pushed);
                s_standing.TryGetValue(s.GetInstanceID(), out ground);
                fields.Add(Who(s) + ".movement");
                fields.Add("kinematic=" + movement.KinematicMode + ",suspended=" + movement.IsSuspended
                    + ",pushing=" + Id(pushed) + ",standing=" + Id(ground));
                foreach (Collider collider in s.GetComponentsInChildren<Collider>(true))
                {
                    Rigidbody rb = collider.attachedRigidbody;
                    fields.Add(Who(s) + ".collider." + collider.GetInstanceID());
                    fields.Add("id=" + Id(collider) + ",enabled=" + collider.enabled + ",active=" + collider.gameObject.activeInHierarchy
                        + ",trigger=" + collider.isTrigger + ",layer=" + collider.gameObject.layer
                        + ",body=" + Id(rb) + ",kinematic=" + (rb == null ? "-" : rb.isKinematic.ToString())
                        + ",detect=" + (rb == null ? "-" : rb.detectCollisions.ToString()));
                }
            }
            if (fields.Count != 0) CoopTrace.Sample("phys", "players.colliders", fields.ToArray());
        }

        static void SamplePhysics(Component component, object observedVelocity)
        {
            if (!CoopTrace.On("phys", 2)) return;
            Rigidbody rb = component as Rigidbody;
            if (rb == null) rb = component.GetComponent<Rigidbody>();
            List<object> fields = new List<object>(new object[] { "id", component, "kind", component.GetType().Name,
                "pos", component.transform.position, "vel", rb == null ? observedVelocity : rb.velocity,
                "kinematic", rb == null ? (object)null : rb.isKinematic, "layer", component.gameObject.layer,
                "detectCollisions", rb == null ? (object)null : rb.detectCollisions });
            StringBuilder pushing = new StringBuilder(), standing = new StringBuilder();
            Collider[] colliders = component.GetComponentsInChildren<Collider>(true);
            foreach (SeinCharacter s in s_players)
            {
                Component pushed; Transform ground;
                s_pushing.TryGetValue(s.GetInstanceID(), out pushed);
                s_standing.TryGetValue(s.GetInstanceID(), out ground);
                if (pushed != null && (pushed.transform == component.transform || pushed.transform.IsChildOf(component.transform))) pushing.Append(Who(s) + ",");
                if (ground != null && (ground == component.transform || ground.IsChildOf(component.transform))) standing.Append(Who(s) + ",");
                foreach (Collider playerCollider in s.GetComponentsInChildren<Collider>(true))
                {
                    foreach (Collider other in colliders)
                    {
                        fields.Add(Who(s) + ".pair." + playerCollider.GetInstanceID() + "." + other.GetInstanceID());
                        fields.Add("object=" + Id(other) + ",enabled=" + other.enabled + ",active=" + other.gameObject.activeInHierarchy
                            + ",trigger=" + other.isTrigger + ",layer=" + other.gameObject.layer
                            + ",ignoreLayer=" + Physics.GetIgnoreLayerCollision(playerCollider.gameObject.layer, other.gameObject.layer)
                            + ",ignorePair=unavailable");
                    }
                }
            }
            fields.Add("pushing"); fields.Add(pushing.ToString()); fields.Add("standing"); fields.Add(standing.ToString());
            CoopTrace.Sample("phys", "state", fields.ToArray());
        }

        static void SequenceBefore(ActionSequence __instance)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                CoopTrace.Event("story", "sequence.request", "id", __instance, "who", Who(Characters.Sein));
                CoopTrace.Changed("hooks.sequence." + __instance.GetInstanceID(), "False");
            }
            catch { }
        }

        static void SequenceAfter(ActionSequence __instance)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                int id = __instance.GetInstanceID();
                if (CoopTrace.Changed("hooks.sequence.seen." + id, "1") && !__instance.IsRunning)
                {
                    CoopTrace.Changed("hooks.sequence." + id, "False");
                    return;
                }
                if (!CoopTrace.Changed("hooks.sequence." + id, __instance.IsRunning.ToString())) return;
                bool ability = s_abilitySequences.ContainsKey(id);
                CoopTrace.Event("story", ability ? (__instance.IsRunning ? "ability.cutscene.start" : "ability.cutscene.end") :
                    (__instance.IsRunning ? "sequence.start" : "sequence.end"), "id", __instance, "index", __instance.Index, "who", Who(Characters.Sein));
            }
            catch { }
        }

        static void CutsceneBefore(CutsceneController __instance, out string __state)
        {
            __state = null;
            try { if (!CoopTrace.On("story", 1)) return; __state = Id(__instance.CurrentState); } catch { }
        }

        static void CutsceneAfter(CutsceneController __instance, string __state)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                string state = Id(__instance.CurrentState);
                if (CoopTrace.Changed("hooks.cutscene." + __instance.GetInstanceID(), state))
                {
                    bool first = CoopTrace.Changed("hooks.cutscene.seen." + __instance.GetInstanceID(), "1");
                    if (!first && __state != null && __state != "-")
                        CoopTrace.Event("story", "cutscene.end", "id", __instance, "name", __state, "who", Who(Characters.Sein));
                    if (state != "-")
                        CoopTrace.Event("story", "cutscene.start", "id", __instance, "name", state, "who", Who(Characters.Sein));
                    CoopTrace.Event("story", "cutscene.state", "id", __instance, "from", __state, "to", state,
                        "type", __instance.CurrentState == null ? "-" : __instance.CurrentState.GetType().Name, "who", Who(Characters.Sein));
                }
            }
            catch { }
        }

        static void PedestalBefore(GetAbilityPedestal __instance)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                ActionSequence sequence = __instance.ActivatePedestalSequence as ActionSequence;
                if (sequence != null) s_abilitySequences[sequence.GetInstanceID()] = sequence;
                CoopTrace.Event("story", "ability.activate", "id", __instance, "ability", __instance.Ability,
                    "sequence", __instance.ActivatePedestalSequence, "who", Who(Characters.Sein));
            }
            catch { }
        }

        static void PedestalAfter(GetAbilityPedestal __instance)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                CoopTrace.Event("story", "ability.activated", "id", __instance, "ability", __instance.Ability, "state", __instance.CurrentState,
                    "running", __instance.ActivatePedestalSequence != null && __instance.ActivatePedestalSequence.IsPerforming);
            }
            catch { }
        }

        static void CameraAfter(GameplayCamera __instance)
        {
            try
            {
                if (!CoopTrace.On("cam", 1)) return;
                Transform target = __instance.Target;
                string state = __instance.Motion + ":" + __instance.IsSuspended + ":" + Id(target);
                if (CoopTrace.Changed("hooks.camera." + __instance.GetInstanceID(), state))
                    CoopTrace.Event("cam", __instance.IsSuspended || __instance.Motion != GameplayCamera.MotionType.Chase ? "lock" : "unlock",
                        "id", __instance, "motion", __instance.Motion, "suspended", __instance.IsSuspended, "target", target, "pos", __instance.transform.position);
            }
            catch { }
        }

        static void CameraPuppetAfter(CameraPuppetController __instance)
        {
            try
            {
                if (!CoopTrace.On("cam", 1)) return;
                bool cinematic = __instance.CinematicPuppet != null && __instance.Tween > 0f;
                string state = Id(__instance.CinematicPuppet) + ":" + cinematic;
                if (CoopTrace.Changed("hooks.camera.puppet." + __instance.GetInstanceID(), state))
                    CoopTrace.Event("cam", cinematic ? "script.takeover" : "script.release", "id", __instance,
                        "cinematic", __instance.CinematicPuppet, "gameplay", __instance.GameplayPuppet, "tween", __instance.Tween);
            }
            catch { }
        }

        static void HudAfter(SeinUI __instance)
        {
            try
            {
                if (!CoopTrace.On("hud", 1)) return;
                float opacity = (float)HudOpacity.GetValue(__instance);
                bool visible = __instance.gameObject.activeInHierarchy && __instance.UI != null && __instance.UI.activeInHierarchy && opacity > 0.0001f;
                string state = visible + ":" + __instance.ShowUI + ":" + SeinUI.DebugHideUI + ":" + Letterbox.ShowLetterboxes;
                if (CoopTrace.Changed("hooks.hud." + __instance.GetInstanceID(), state))
                    CoopTrace.Event("hud", visible ? "show" : "hide", "id", __instance, "showUI", __instance.ShowUI,
                        "active", __instance.UI != null && __instance.UI.activeInHierarchy, "opacity", opacity,
                        "fader", __instance.FaderTransparency, "debugHide", SeinUI.DebugHideUI, "letterbox", Letterbox.ShowLetterboxes);
            }
            catch { }
        }

        static void MessageAfter(MessageBoxVisibility __instance)
        {
            try
            {
                if (!CoopTrace.On("hud", 1)) return;
                bool visible = __instance.Visible && __instance.gameObject.activeInHierarchy;
                if (CoopTrace.Changed("hooks.message." + __instance.GetInstanceID(), visible.ToString()))
                {
                    MessageBox box = __instance.GetComponent<MessageBox>();
                    CoopTrace.Event("hud", visible ? "dialog.show" : "dialog.hide", "id", __instance,
                        "provider", box == null ? null : box.MessageProvider, "index", box == null ? -1 : box.MessageIndex);
                }
            }
            catch { }
        }

        static void MessageDestroyed(MessageBoxVisibility __instance)
        {
            try
            {
                if (!CoopTrace.On("hud", 1)) return;
                if (CoopTrace.Changed("hooks.message." + __instance.GetInstanceID(), "False"))
                    CoopTrace.Event("hud", "dialog.hide", "id", __instance, "reason", "destroy");
            }
            catch { }
        }

        static void StoryTextAfter(ShowStoryTextAction __instance, MethodBase __originalMethod)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                bool show = __originalMethod.Name == "Perform";
                if (CoopTrace.Changed("hooks.subtitle." + __instance.GetInstanceID(), show.ToString()))
                    CoopTrace.Event("story", show ? "subtitle.show.request" : "subtitle.hide", "id", __instance,
                        "provider", __instance.StoryMessage, "created", __instance.IsPerforming, "who", Who(Characters.Sein));
            }
            catch { }
        }

        static void OriAfter(Ori __instance)
        {
            try
            {
                if (!CoopTrace.On("story", 1)) return;
                Transform target = Characters.Sein == null ? null : Characters.Sein.Transform;
                object destination = __instance.CurrentState == Ori.State.Hovering ? null : OriDestination.GetValue(__instance);
                string state = Id(target) + ":" + __instance.CurrentState + ":" + __instance.IsSuspended + ":" + destination;
                if (CoopTrace.Changed("hooks.ori." + __instance.GetInstanceID(), state))
                    CoopTrace.Event("story", "spirit.follow", "id", __instance, "target", target, "who", Who(Characters.Sein),
                        "state", __instance.CurrentState, "destination", destination, "suspended", __instance.IsSuspended, "pos", __instance.Position);
            }
            catch { }
        }

        static void SaveAfter(SaveGameController __instance)
        {
            try { if (!CoopTrace.On("save", 1)) return; CoopTrace.Event("save", "save", "slot", __instance.CurrentSlotIndex, "backup", __instance.CurrentBackupIndex); } catch { }
        }

        static void LoadAfter(SaveGameController __instance, bool __result)
        {
            try { if (!CoopTrace.On("save", 1)) return; CoopTrace.Event("save", "load", "slot", __instance.CurrentSlotIndex, "ok", __result); } catch { }
        }

        static void PauseAfter(GameController __instance)
        {
            try
            {
                if (!CoopTrace.On("save", 1)) return;
                string state = __instance.GameplaySuspended + ":" + __instance.GameplaySuspendedForUI;
                if (CoopTrace.Changed("hooks.pause", state))
                    CoopTrace.Event("save", __instance.GameplaySuspended || __instance.GameplaySuspendedForUI ? "pause" : "resume",
                        "gameplay", __instance.GameplaySuspended, "ui", __instance.GameplaySuspendedForUI, "inputLocked", __instance.InputLocked);
            }
            catch { }
        }

        static void DifficultyAfter(DifficultyController __instance)
        {
            try
            {
                if (!CoopTrace.On("save", 1)) return;
                if (CoopTrace.Changed("hooks.difficulty", __instance.Difficulty.ToString()))
                    CoopTrace.Event("save", "difficulty", "value", __instance.Difficulty, "lowest", __instance.LowestDifficulty,
                        "enemy_hp_ratio", Enemy.ScaleHealth(100f) / 100f, "player_damage_ratio", "see player.damage effective_ratio");
            }
            catch { }
        }

        static void SceneLoaded(SceneRoot __instance)
        {
            try { if (__instance.MetaData != null) ShooterProbe.SceneLoaded(__instance.MetaData.SceneName); } catch { }
            try { if (!CoopTrace.On("scene", 1)) return; CoopTrace.Event("scene", "load", "id", __instance, "scene", __instance.MetaData.SceneName, "guid", __instance.MetaData.SceneMoonGuid); } catch { }
        }

        static void SceneUnloading(SceneRoot __instance)
        {
            try { if (!CoopTrace.On("scene", 1)) return; CoopTrace.Event("scene", "unload", "id", __instance, "scene", __instance.MetaData.SceneName, "guid", __instance.MetaData.SceneMoonGuid); } catch { }
        }

        static void Disconnected(string reason)
        {
            try
            {
                if (!CoopTrace.On("net", 1)) return;
                reason = NetPlay.OwnDisconnectReason(s_peer, reason);   // T-201: a local leave reads "left: <why>", not the text sent to the partner
                if (CoopTrace.Changed("hooks.net.disconnect", reason)) CoopTrace.Event("net", "disconnect", "reason", reason);
            }
            catch { }
        }

        static void ObserveNet()
        {
            if (!CoopTrace.On("net", 1)) return;
            NetPeer peer = NetPlay.Peer;
            if (peer != s_peer)
            {
                if (s_peer != null)
                {
                    if (!string.IsNullOrEmpty(s_peer.DisconnectReason)) Disconnected(s_peer.DisconnectReason);
                    s_peer.Disconnected -= Disconnected;
                }
                s_peer = peer;
                if (peer != null) { CoopTrace.Forget("hooks.net.disconnect"); peer.Disconnected += Disconnected; }
            }
            string state = NetPlay.CurrentPhase + ":" + (peer == null ? "-" : peer.State.ToString());
            if (CoopTrace.Changed("hooks.net.state", state))
                CoopTrace.Event("net", "connection", "phase", NetPlay.CurrentPhase, "state", peer == null ? "-" : peer.State.ToString(),
                    "reason", peer == null ? "" : NetPlay.OwnDisconnectReason(peer, peer.DisconnectReason));
            if (!NetPlay.Enabled || !CoopTrace.On("net", 2) || !CoopTrace.Every("net.sample", 1f)) return;
            CoopTrace.Sample("net", "state", "rtt_ms", NetPlay.RttMs, "loss_percent", NetPlay.LossPercent,
                "ReliablePending", peer == null ? 0 : peer.Stats.ReliablePending, "ReliableResends", peer == null ? 0L : peer.Stats.ReliableResends,
                "RemoteScene", NetPlay.RemoteScene);
        }
    }
}
