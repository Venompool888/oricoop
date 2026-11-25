using System;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    // T-169 (#249; T-041 rules 6 and 7, design in docs\tickets\T-169.md): escape sequences (Ginso Tree water, Mount Horu).
    // - Dying in the water / lava is the ordinary downed state (Plugin's OnKill prefix); only a team wipe reloads the
    //   checkpoint, i.e. restarts the escape. Nothing here changes that path.
    // - One player reaching the escape's end trigger = the whole team is through: that PC sends EscapeDone (msg 92) and
    //   every PC puts its own players that are not there yet (downed ones too, revived with full health) into the end
    //   trigger, so the game's own end (exit placement, world state, water off) runs on every PC. If the trigger does not
    //   fire there (already used), they go beside the finisher instead.
    // - Downed inside an escape: mash Y / Q to fill a ring; when full, come back with full health next to a random
    //   teammate who is alive, not downed and in the same escape. The T-041 "hold Y at the soul link" is hidden there.
    public static partial class Revive
    {
        // Planted bugs (docs/testing-rules.md): each one must make the escape game tests fail.
        static readonly bool s_mutateEscapeNoTeam = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ESCAPE_NO_TEAM") == "1";
        static readonly bool s_mutateMashNearest = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ESCAPE_MASH_NEAREST") == "1";

        const string MashPadY = "<icon>i</>", MashKeyQ = "<icon>I</>";   // the game's own button icons (ButtonIconUtility)
        static readonly MashState[] s_mash = new MashState[ReviveLogic.MaxSeats + 1];
        static readonly GlowRing[] s_mashRing = new GlowRing[ReviveLogic.MaxSeats + 1];
        static readonly GameText[] s_mashHint = new GameText[ReviveLogic.MaxSeats + 1];
        static readonly float[] s_escapeDone = new float[8];          // escape id -> real time of its last finish
        static readonly bool[] s_exitFiredHere = new bool[8];         // this PC's own end trigger ran since that finish
        static readonly System.Random s_escapeRandom = new System.Random();
        static readonly Dictionary<int, string> s_triggerPaths = new Dictionary<int, string>();
        static readonly Dictionary<string, Rect> s_sceneRects = new Dictionary<string, Rect>();
        static readonly System.Reflection.FieldInfo s_waterInstance = AccessTools.Field(typeof(RisingWater), "m_instance");
        static readonly System.Reflection.FieldInfo s_triggerBounds = AccessTools.Field(typeof(PlayerCollisionTrigger), "m_bounds");
        static bool s_escapeNetRegistered;
        static readonly int[] s_lastEscape = new int[ReviveLogic.MaxSeats + 1];
        static readonly float[] s_lastEscapeAt = new float[ReviveLogic.MaxSeats + 1];
        const float LastEscapeSeconds = 4f;   // the running escape each local Ori was last inside while alive
        // Pending "bring the team" work on this PC after a finish.
        static readonly PendingFinish s_pending = new PendingFinish();
        static Vector3 s_pendingExit;
        // T-215 #3 planted bug: the work owed after a finish survives the session's end.
        static readonly bool s_mutateEscapeKeepPending = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ESCAPE_KEEP_PENDING") == "1";
        static Vector3 s_pendingFinisherAt;
        static EscapeDone s_send;
        static float s_sendAt;
        const float SendDelay = 1f;

        public static int EscapeFinishesSent, EscapeFinishesReceived, EscapeTeamBrought, EscapeMashRevives, EscapeFallbacks;
        public static int LastMashTeammate, LastFinisher;
        public static string LastMashHint;
        internal static int TestMashPresses;   // test hook: presses added each frame for every downed Ori of this PC in an escape

        static float EscapeNow { get { return Time.realtimeSinceStartup; } }

        // ---- where the escapes are ---------------------------------------------------------------------------------
        static bool SceneRect(string scene, out Rect rect)
        {
            if (s_sceneRects.TryGetValue(scene, out rect)) return true;
            if (Core.Scenes.Manager == null) return false;
            foreach (RuntimeSceneMetaData meta in Core.Scenes.Manager.AllScenes)
                if (meta != null && meta.Scene == scene) { rect = meta.SceneBounds; s_sceneRects[scene] = rect; return true; }
            return false;
        }

        static bool InEscapeScenes(EscapeDef e, Vector3 position)
        {
            foreach (string scene in e.Scenes)
            {
                Rect r;
                if (SceneRect(scene, out r) && r.Contains(new Vector2(position.x, position.y))) return true;
            }
            return false;
        }

        static RisingWater ActiveWater
        {
            get
            {
                if (!RisingWater.Available || s_waterInstance == null) return null;
                RisingWater w = s_waterInstance.GetValue(null) as RisingWater;
                return w != null && w.gameObject.activeInHierarchy ? w : null;
            }
        }

        // An escape is running on this PC: Ginso while its rising water is switched on, Mount Horu once its heart was
        // restored (WarmthReturned). A finished escape is over for FinishWindow seconds.
        static bool EscapeRunning(EscapeDef e)
        {
            if (EscapeRules.RecentlyFinished(s_escapeDone, e.Id, EscapeNow)) return false;
            if (e.Id == EscapeRules.Ginso) return ActiveWater != null;
            if (e.Id == EscapeRules.Horu) return Sein.World.Events.WarmthReturned;
            return false;
        }

        // The running escape this position is in, or null.
        internal static EscapeDef EscapeAt(Vector3 position)
        {
            foreach (EscapeDef e in EscapeRules.All) if (EscapeRunning(e) && InEscapeScenes(e, position)) return e;
            return null;
        }

        // Test diagnostics: per escape, running / inside its scenes at this position.
        internal static string EscapeDebug(Vector3 position)
        {
            RisingWater w = RisingWater.Available && s_waterInstance != null ? s_waterInstance.GetValue(null) as RisingWater : null;
            string text = "water=" + (ActiveWater != null) + (w == null ? " (none)" : " (active " + w.gameObject.activeInHierarchy + ", enabled " + w.enabled + ")");
            foreach (EscapeDef e in EscapeRules.All) text += " " + e.Name + ":running=" + EscapeRunning(e) + ",inside=" + InEscapeScenes(e, position);
            return text;
        }

        static Vector3 SeatPosition(int seat, SeinCharacter s)
        {
            Vector3 at;
            if (Services.Net.Enabled && !Players.IsLocal(seat) && Host.RemotePos(seat, out at)) return at;
            return s.Position;
        }

        // The escape a downed Ori of this PC is in: where its skull is, or (it fell or drowned just past the edge of the
        // escape's scenes, e.g. under the bottom room) the running escape it was in while alive.
        static EscapeDef EscapeOfDowned(Downed d)
        {
            EscapeDef e = EscapeAt(d.Marker);
            if (e != null) return e;
            EscapeDef last = d.Player >= 1 && d.Player <= ReviveLogic.MaxSeats ? EscapeRules.ById(s_lastEscape[d.Player]) : null;
            return last != null && EscapeRunning(last) ? last : null;
        }

        // For T-041's hold-Y soul-link revive (Revive.GiveUpSuppressed, wired on the first tick): true while a downed Ori of this PC
        // is in a running escape, where mashing Y revives at a teammate instead.
        public static bool InEscape
        {
            get
            {
                foreach (int seat in Players.LocalSeats)
                {
                    Downed d = seat >= 1 && seat <= ReviveLogic.MaxSeats ? s_down[seat] : null;
                    if (d != null && !d.Remote && EscapeOfDowned(d) != null) return true;
                }
                return false;
            }
        }

        // ---- every frame (EscapeRunner) ------------------------------------------------------------
        static bool s_giveUpWired;
        static void EscapeTick()
        {
            if (!s_giveUpWired) { s_giveUpWired = true; GiveUpSuppressed = delegate { return InEscape; }; }
            if (Services.Net.Enabled) RegisterEscapeNet();
            // Not gated on Online: the finisher's own game may be loading the scenes at the exit (no Ori for a moment).
            if (s_sendAt > 0f && EscapeNow >= s_sendAt && Services.Net.Enabled && Services.Net.Connected)
            {
                s_sendAt = 0f;
                SeinCharacter finisher = SeinAt(s_send.Seat) ?? Primary;
                Vector3 at = finisher != null ? finisher.Position : new Vector3(s_send.X, s_send.Y, 0f);
                EscapeDone done = s_send; done.FX = at.x; done.FY = at.y;
                Services.Net.Send(EscapeRules.MsgEscapeDone, true, w => EscapeRules.Write(w, done));
                EscapeFinishesSent++;
            }
            if (s_pending.Active) TickPendingFinish();
            for (int seat = 1; seat <= ReviveLogic.MaxSeats; seat++)
            {
                Downed d = s_down[seat];
                if (d == null)
                {
                    SeinCharacter alive = Players.IsLocal(seat) ? SeinAt(seat) : null;
                    if (alive != null && Alive(alive))
                    {
                        // Kept for a few seconds after leaving: falling under the bottom room into the water is still the escape.
                        EscapeDef inside = EscapeAt(alive.Position);
                        if (inside != null) { s_lastEscape[seat] = inside.Id; s_lastEscapeAt[seat] = EscapeNow; }
                        else if (EscapeNow - s_lastEscapeAt[seat] > LastEscapeSeconds) s_lastEscape[seat] = 0;
                    }
                    else if (alive == null) s_lastEscape[seat] = 0;
                }
                EscapeDef e = d != null && !d.Remote && d.Sein != null && Players.IsLocal(seat) && (Online || Local) ? EscapeOfDowned(d) : null;
                if (e == null) { s_mash[seat] = null; HideMash(seat); continue; }
                if (s_mash[seat] == null) s_mash[seat] = new MashState();
                GameController gc = GameController.Instance;
                bool blocked = gc == null || gc.GameplaySuspended || gc.IsLoadingGame || SessionRules.LocalPaused;
                int presses = blocked ? 0 : MashPresses(d.Sein) + TestMashPresses;
                bool full = EscapeRules.Mash(s_mash[seat], presses, Time.deltaTime);
                ShowMash(d, s_mash[seat].Progress);
                if (!full) continue;
                int mate = PickEscapeTeammate(d, e);
                if (mate == 0) continue;   // nobody to come back to (yet): the ring stays full
                s_mash[seat] = null; HideMash(seat);
                RespawnInEscape(d, mate, e);
            }
        }

        // Each Ori reads its own controls: P1 the game's input (pad Y = the game's Bash button, or Q on the keyboard),
        // every other local Ori its own puppet input.
        static int MashPresses(SeinCharacter s)
        {
            Puppet p = Players.OfSein(s);
            if (p != null && p.IsLocal && s != Host.P1) return p.Input.Bash.OnPressed ? 1 : 0;
            return (Core.Input.Bash.OnPressed ? 1 : 0) + (Input.GetKeyDown(KeyCode.Q) ? 1 : 0);
        }

        // Alive, not downed, in the same running escape. The planted bug picks the nearest living teammate anywhere.
        static int PickEscapeTeammate(Downed d, EscapeDef e)
        {
            if (s_mutateMashNearest)
            {
                SeinCharacter nearest = NearestLiving(d.Marker, d.Sein);
                return nearest == null ? 0 : PlayerOf(nearest);
            }
            List<int> eligible = new List<int>();
            foreach (int seat in PresentSeats)
            {
                SeinCharacter s = SeinAt(seat);
                if (seat == d.Player || !Alive(s)) continue;
                if (InEscapeScenes(e, SeatPosition(seat, s))) eligible.Add(seat);
            }
            return EscapeRules.PickTeammate(eligible, s_escapeRandom.Next());
        }

        // A safe grounded spot next to the teammate: its last solid ground if that is within 3 units and above the water,
        // otherwise where it stands now.
        static Vector3 SpotBeside(int seat, SeinCharacter mate)
        {
            Vector3 now = SeatPosition(seat, mate);
            RisingWater water = ActiveWater;
            float waterY = water != null ? water.transform.position.y : float.NegativeInfinity;
            if (s_haveSafe[seat] && (s_safe[seat] - now).magnitude < 3f && s_safe[seat].y > waterY + 1.5f) return s_safe[seat];
            return now;   // the teammate's own footing (a side offset could be over a ledge)
        }

        static void RespawnInEscape(Downed d, int mateSeat, EscapeDef e)
        {
            SeinCharacter mate = SeinAt(mateSeat);
            if (mate == null) return;
            Vector3 at = SpotBeside(mateSeat, mate);
            s_down[d.Player] = null;
            RestoreDowned(d, false);
            Host.Teleport(d.Sein, at);
            d.Sein.Mortality.Health.SetAmount(d.Sein.Mortality.Health.MaxHealth);
            d.Sein.Mortality.DamageReciever.MakeInvincible(1.5f);
            if (Host.IsPuppet(d.Sein)) Host.ReturnCompanion(d.Sein, "came back beside a teammate in the escape");
            PartnerFx.PlayRevive(d.Sein, d.Sein.Position);
            if (Online) SendRevived(d.Player, d.Sein.Position);
            EscapeMashRevives++; LastMashTeammate = mateSeat;
            Services.Log.Info("escape " + e.Name + ": P" + d.Player + " mashed back beside P" + mateSeat + " at " + at);
        }

        static void ShowMash(Downed d, float progress)
        {
            int seat = d.Player;
            bool primary = d.Sein == Host.P1;
            bool pad = !primary || GameSettings.Instance != null && GameSettings.Instance.CurrentControlScheme == ControlScheme.Controller;
            string text = L10n.SessionFormat("escape.mash", pad ? MashPadY : MashKeyQ);
            LastMashHint = text;
            GameplayCamera cam = UI.Cameras.Current;
            if (cam == null || cam.Camera == null) { HideMash(seat); return; }
            // Online the camera follows a teammate (spectating): the primary Ori's ring and hint sit low on the screen.
            // Other local Oris get theirs at their own skull.
            Vector3 at = Services.Net.Enabled && primary
                ? cam.Camera.ViewportToWorldPoint(new Vector3(0.5f, 0.3f, -cam.Camera.transform.position.z))
                : d.Marker + new Vector3(0f, 0.6f, 0f);
            if (s_mashRing[seat] == null) s_mashRing[seat] = new GlowRing("OriCoop_EscapeMashRing_P" + seat);
            if (s_mashRing[seat].Ok) s_mashRing[seat].Place(at, 0.1f, progress, 1f);
            if (s_mashHint[seat] == null) s_mashHint[seat] = new GameText("OriCoop_EscapeMashHint_P" + seat);
            if (s_mashHint[seat].Ok) { s_mashHint[seat].SetText(text); s_mashHint[seat].Place(at + new Vector3(0f, -2.2f, 0f), GameText.PromptHeight, 0.9f, 1f);   // T-255: native size }
        }

        static void HideMash(int seat)
        {
            if (s_mashRing[seat] != null) s_mashRing[seat].Hide();
            if (s_mashHint[seat] != null) s_mashHint[seat].Hide();
        }

        // ---- the end: one finisher brings the team ----------------------------------------------------------------
        static string TriggerPath(Trigger t)
        {
            string path;
            int id = t.GetInstanceID();
            if (s_triggerPaths.TryGetValue(id, out path)) return path;
            path = t.transform.name;
            for (Transform p = t.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
            s_triggerPaths[id] = path;
            return path;
        }

        static Vector3 TriggerCentre(Trigger t)
        {
            PlayerCollisionTrigger pc = t as PlayerCollisionTrigger;
            if (pc != null && s_triggerBounds != null)
            {
                Rect r = (Rect)s_triggerBounds.GetValue(pc);
                if (r.width > 0f && r.height > 0f) return new Vector3(r.center.x, r.center.y, 0f);
            }
            return new Vector3(t.transform.position.x, t.transform.position.y, 0f);
        }

        // An end trigger fired on this PC (Trigger.DoTrigger prefix; the game's triggers only react to this PC's own Ori).
        static void OnEndTrigger(Trigger t)
        {
            EscapeDef e = EscapeRules.ByFinishTrigger(TriggerPath(t));
            if (e == null) return;
            s_exitFiredHere[e.Id] = true;
            SeinCharacter finisher = Characters.Current as SeinCharacter ?? Primary;
            if (finisher == null || IsDowned(finisher)) return;
            if (!EscapeRules.NoteFinish(s_escapeDone, e.Id, EscapeNow)) return;   // our teammates arriving after a finish
            int seat = PlayerOf(finisher);
            if (seat < 1) seat = Host.LocalSeat;
            Vector3 exit = TriggerCentre(t);
            LastFinisher = seat;
            Services.Log.Info("escape " + e.Name + ": P" + seat + " reached the end at " + exit + ": the team is through");
            if (s_mutateEscapeNoTeam) return;
            if (Online)
            {
                // Sent once the game's end has placed the finisher (its SetCharacterPosition runs right after the
                // trigger), so the others know where to end up.
                RegisterEscapeNet();
                s_send = new EscapeDone { Escape = (byte)e.Id, Seat = (byte)seat, X = exit.x, Y = exit.y };
                s_sendAt = EscapeNow + SendDelay;
            }
            BringTeam(e, seat, exit, true, Vector3.zero);
        }

        static void RegisterEscapeNet()
        {
            if (s_escapeNetRegistered) return;
            s_escapeNetRegistered = true;
            Services.Net.On(EscapeRules.MsgEscapeDone, OnNetEscapeDone);
            NetPlay.Forward(EscapeRules.MsgEscapeDone);   // guest -> guest over the P2P host (T-115)
        }

        static void OnNetEscapeDone(BinaryReader r)
        {
            EscapeDone done;
            if (!Services.Net.Enabled || !EscapeRules.TryRead(r, out done) || Players.IsLocal(done.Seat)) return;
            EscapeDef e = EscapeRules.ById(done.Escape);
            if (!EscapeRules.NoteFinish(s_escapeDone, e.Id, EscapeNow)) return;   // we finished at the same moment
            s_exitFiredHere[e.Id] = false;
            EscapeFinishesReceived++; LastFinisher = done.Seat;
            Services.Log.Info("escape " + e.Name + ": P" + done.Seat + " reached the end: bringing this PC's players");
            NetHud.Toast(L10n.SessionFormat("escape.through", Roles.Tag(done.Seat)));
            BringTeam(e, done.Seat, new Vector3(done.X, done.Y, 0f), false, new Vector3(done.FX, done.FY, 0f));
        }

        // This PC's own Ori for a seat. Host.P1 / SeinAt are empty while no teammate puppet exists (a finisher loading
        // the exit): fall back to the downed record or the game's own Ori.
        static SeinCharacter Primary { get { return Host.P1 ?? Characters.Sein; } }
        static SeinCharacter LocalSein(int seat)
        {
            SeinCharacter s = SeinAt(seat);
            if (s == null && s_down[seat] != null && !s_down[seat].Remote) s = s_down[seat].Sein;
            if (s == null && seat == Host.LocalSeat) s = Characters.Sein;
            return s;
        }

        // Every player of this PC that is not the finisher: revive if downed (full health), then into the end trigger
        // (this PC's primary Ori, so the game's own end runs here) or beside the primary Ori (other local Oris).
        static void BringTeam(EscapeDef e, int finisher, Vector3 exit, bool finisherHere, Vector3 finisherAt)
        {
            bool primaryMoved = false;
            foreach (int seat in Players.LocalSeats)
            {
                if (seat == finisher || seat < 1 || seat > ReviveLogic.MaxSeats) continue;
                SeinCharacter s = LocalSein(seat);
                if (s == null) continue;
                Downed d = s_down[seat];
                if (d != null && !d.Remote)
                {
                    s_down[seat] = null;
                    RestoreDowned(d, false);
                }
                s.Mortality.Health.SetAmount(s.Mortality.Health.MaxHealth);
                s.Mortality.DamageReciever.MakeInvincible(3f);
                s_mash[seat] = null; HideMash(seat);
                if (s == Primary && !finisherHere)
                {
                    Host.Teleport(s, exit);
                    primaryMoved = true;
                }
                if (Host.IsPuppet(s)) Host.ReturnCompanion(s, "the team is through the escape");
                if (Services.Net.Enabled && d != null) SendRevived(seat, s.Position);
                EscapeTeamBrought++;
                Services.Log.Info("escape " + e.Name + ": P" + seat + (d != null ? " (was downed)" : "") + " brought to the end, health "
                    + s.Mortality.Health.Amount.ToString("F1") + (primaryMoved && s == Primary ? ", into the end trigger at " + exit : ""));
            }
            if (primaryMoved) LoadScenesHere(Primary);
            // The extra local Oris follow the primary one once the game's end has placed it (or right away when the
            // primary Ori is the finisher).
            s_pendingExit = exit; s_pendingFinisherAt = finisherAt;
            s_pending.Begin(e.Id, finisher, primaryMoved ? EscapeNow + 3f : 0f, EscapeNow + (finisherHere ? 1.2f : 2f));
        }

        // T-215 #3: the session ended (from Revive.ResetLinkState). What this PC still owed after a teammate's finish is
        // dropped: a player who stays in its own game must not be pulled to where the finisher was.
        static void EscapeSessionEnded()
        {
            if (s_mutateEscapeKeepPending) return;
            if (s_pending.Active) Services.Log.Info("escape: the session ended; nothing more is done for the finish by P" + s_pending.Finisher);
            s_pending.Clear();
            s_sendAt = 0f;
        }

        // Test hooks (NetReviveTest R7): a teammate's finish that still owes this PC's Ori the fallback placement beside
        // `finisherAt`, due after `seconds`; and the session's end as the escape code sees it.
        internal static void TestOweFallback(int finisher, Vector3 finisherAt, float seconds)
        {
            s_pendingFinisherAt = finisherAt;
            s_pending.Begin(EscapeRules.Ginso, finisher, EscapeNow + seconds, 0f);
        }
        internal static void TestEscapeSessionEnded() { EscapeSessionEnded(); }
        internal static bool EscapeWorkOwed { get { return s_pending.Active; } }

        static void TickPendingFinish()
        {
            float now = EscapeNow;
            if (s_pending.TakeFallback(now))
            {
                // The game's end did not bring this PC's Ori to the finisher (its end trigger was already used, not
                // loaded yet, or ran while the scenes were still loading): go beside the finisher.
                Vector3 at = s_pendingFinisherAt;   // where the finisher's PC said it stands after the game's end
                bool known = true;
                SeinCharacter own = Primary;
                if (own != null && !IsDowned(own) && (own.Position - at).magnitude > 6f)
                {
                    Host.Teleport(own, at + Vector3.right);
                    LoadScenesHere(own);
                    EscapeFallbacks++;
                    Services.Log.Info("escape: not at the finisher after the end (trigger ran here: " + s_exitFiredHere[s_pending.Escape] + "); placed beside P" + s_pending.Finisher);
                }
            }
            if (s_pending.TakeExtras(now))
            {
                SeinCharacter lead = Primary;
                foreach (int seat in Players.LocalSeats)
                {
                    SeinCharacter s = seat >= 1 && seat <= ReviveLogic.MaxSeats ? SeinAt(seat) : null;
                    if (s == null || s == lead || seat == s_pending.Finisher || lead == null || IsDowned(lead)) continue;
                    Host.Teleport(s, lead.Position + new Vector3(-1f, 0f, 0f));
                }
            }
        }

        // The primary Ori moved far: load the scenes there first, as SaveSync does for a guest joining a host far away.
        static void LoadScenesHere(SeinCharacter own)
        {
            try
            {
                if (own == null || GameController.Instance == null || InstantLoadScenesController.Instance == null) return;
                if (UI.Cameras.Current != null) UI.Cameras.Current.MoveCameraToTargetInstantly();
                Core.Scenes.Manager.SetTargetPositions(own.Position);
                if (!Core.Scenes.Manager.IsInsideASceneBoundary(own.Position) || InstantLoadScenesController.Instance.IsLoading) return;
                GameController.Instance.IsLoadingGame = true;
                InstantLoadScenesController.Instance.LoadScenesAtPosition(delegate { GameController.Instance.IsLoadingGame = false; }, true, false);
                InstantLoadScenesController.Instance.FreezeIfLoadingScenes();
            }
            catch (Exception ex) { Services.Log.Warn("escape: loading scenes at the end failed: " + ex.Message); }
        }

        // ---- patches --------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(Trigger), "DoTrigger")]
        static class EscapeEndTrigger
        {
            static void Prefix(Trigger __instance)
            {
                try { if (__instance != null && __instance.Active && Enabled) OnEndTrigger(__instance); }
                catch (Exception e) { Services.Log.Error("escape end trigger: " + e); }
            }
        }

        // Ticks every frame from its own persistent object (mashing is read per frame). GameController.Update is too small
        // to patch reliably (inlined), so its FixedUpdate only creates the runner once.
        class EscapeRunner : MonoBehaviour
        {
            static readonly Action s_tick = EscapeTick;
            void Update() { if (Enabled) SubsystemGuard.Run("Revive.EscapeTick", s_tick); }
        }
        static GameObject s_escapeRunner;

        [HarmonyPatch(typeof(GameController), "FixedUpdate")]
        static class EscapeRunnerStart
        {
            static void Postfix()
            {
                if (s_escapeRunner != null) return;
                s_escapeRunner = new GameObject("OriCoop_EscapeRunner");
                UnityEngine.Object.DontDestroyOnLoad(s_escapeRunner);
                s_escapeRunner.hideFlags = HideFlags.HideAndDontSave;
                s_escapeRunner.AddComponent<EscapeRunner>();
            }
        }
    }
}
