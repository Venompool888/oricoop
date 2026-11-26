using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop
{
    // T-203: an online new game plays its prologue on every PC at the same time (rules: Story\PrologueCore.cs).
    //   host   the game's own new-game action starts the prologue; the host tells everyone (message 96) and, while it
    //          plays, sends its gameplay input and the position of the character it controls. Leaving the Prologue
    //          state (skip, or played to the end) ends it for everyone.
    //   guest  a guest that waits on the title for the host's save (GuestJoin) runs the same new-game action: the
    //          same prologue plays here. The playable parts take the host's input, never this PC's; skipping is
    //          requested from the host. The guest's own pause menu still opens (online menus do not pause), so the player can leave.
    //          When the host's prologue ends, this copy is put away (back to the title, the session stays) and the
    //          join goes on exactly as before: the host's save loads from the title.
    // T-205: the copy follows the host's story, so it can start at any time ("sitting down on the sofa half-way").
    //   host   counts its story steps (physics steps not frozen for a scene load) and logs what depended on the
    //          player: a trigger its character walked into, a "press a button" wait it passed, a state change of the
    //          interactive cutscene, and each scene load (PrologueStory). Clock every 0.25 s, the log on every new
    //          entry, to a new seat, and again every 3 s.
    //   guest  its copy never passes such a point by itself: it does what the host's log says, at the host's step
    //          (PrologueFollower). A copy behind the host runs faster until it is level (ProloguePace): 60x under
    //          the "catching up" screen for a late joiner, 2x for a few frames after a slow scene load; a copy ahead
    //          runs slower. The game's own sounds mute themselves while the time scale is high and continue at the
    //          right place afterwards (SoundPlayer); sounds that would already be over are stopped here.
    //   A host that does not send its story (a T-203 build) is followed as in T-203: only within 5 s of its start,
    //   by its input alone.
    // Nothing is saved here: a guest never writes a slot, and the local new game is thrown away at the end.
    public static class SharedPrologue
    {
        enum Local { None, Playing, Returning }

        static readonly PrologueHost s_host = new PrologueHost();
        static readonly PrologueGuest s_guest = new PrologueGuest();
        static readonly PrologueInputQueue s_inputs = new PrologueInputQueue();
        static readonly PrologueStory s_story = new PrologueStory();
        static readonly PrologueFollower s_follow = new PrologueFollower();
        static readonly ProloguePace s_pace = new ProloguePace();
        static readonly PrologueHostClock s_hostClock = new PrologueHostClock();
        // Test-only mutations: the guest never plays along / the guest's own input and skip are not replaced / the
        // guest's copy never changes its speed (a late copy plays from the start) / the guest's own character and
        // buttons pass the story's player points (they are not held for the host's log).
        static readonly bool s_mutateOff = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_SHARED_PROLOGUE") == "1";
        static readonly bool s_mutateControl = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_GUEST_CONTROL") == "1";
        static readonly bool s_mutateNoCatchUp = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_NO_CATCHUP") == "1";
        static readonly bool s_mutateOwnGates = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_OWN_GATES") == "1";
        // ... / a trigger of the host's log counts as done although the game did not take it / the pause key opens
        // the pause menu under the catch-up cover.
        static readonly bool s_mutateReplayBlind = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_REPLAY_BLIND") == "1";
        static readonly bool s_mutateMenuUnderCover = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_MENU_UNDER_COVER") == "1";

        const float ClockEvery = 0.25f, LogEvery = 3f;
        // A host event that this copy cannot do for this long (its object never appears) is left out.
        const float DueLimit = 5f;

        // host
        static PrologueState s_announced;
        static float s_publishAt, s_inputAt, s_clockAt, s_logAt;
        static bool s_publishedAny, s_sentAnyInput;
        static PrologueInput s_sentInput;
        static uint s_sequence;
        static int s_logSent;
        static readonly HashSet<int> s_buttonsLogged = new HashSet<int>();
        // guest
        static bool s_haveHost, s_hostLeft, s_ownRestart, s_localSawPrologue, s_localEndRequested;
        static PrologueState s_remote;
        static int s_remoteSeat;
        static float s_remoteAt, s_startedAt, s_returnAt, s_titleAt = -1f, s_snapAt;
        static Local s_local;
        static uint s_followGeneration, s_buttonPass, s_coverFrom;
        static bool s_ownsTime, s_replaying, s_replayFired, s_wasFast, s_lost;
        static Trigger s_replayTarget;
        static float s_dueSince = -1f, s_scanAt, s_coverSince = -1f;
        static readonly HashSet<int> s_buttonsPassed = new HashSet<int>();
        static readonly Dictionary<uint, Trigger> s_triggers = new Dictionary<uint, Trigger>();

        // The local copy of the host's prologue is on screen (no transfer screen, the host's input drives it).
        public static bool Watching { get { return s_local == Local.Playing; } }
        // ... or is being put away: the join (GuestJoin) loads nothing until the title is back.
        public static bool Busy { get { return s_local != Local.None; } }
        // The host is in its prologue right now: the waiting screen says so, the join's "no save" timer does not run.
        public static bool HostPlaying { get { return s_guest.HostPlaying; } }
        // The RestartGame running now is this guest putting its copy away, not the player leaving (TitleReturn).
        public static bool OwnRestart { get { return s_ownRestart; } }
        // This PC's input is replaced while its copy plays (GameIntroHost's intro hold stands back).
        public static bool OwnsInput { get { return s_local == Local.Playing; } }
        public static bool HostAnnouncing
        { get { return Services.Net.Enabled && Services.Net.IsHost && s_local == Local.None && s_host.State.Phase == ProloguePhase.Playing; } }
        // T-205: the copy is far behind the host and fast-forwards: the "catching up" screen covers it (SessionScreens).
        public static bool CatchingUp { get { return s_local == Local.Playing && s_pace.Covered; } }
        // 0..1 of the way from where the cover began to the host; -1 unknown.
        public static float CatchUpProgress
        {
            get
            {
                if (!CatchingUp || !s_hostClock.Have) return -1f;
                float all = (float)s_hostClock.Now(Now) - s_coverFrom;
                return all <= 0f ? 1f : Mathf.Clamp01(((float)s_follow.Step - s_coverFrom) / all);
            }
        }

        // For tests and logs.
        public static int LocalStarts, LocalStops, SkipsRefused, Snaps, InputsSent, InputsApplied, HostStarts;
        public static int EventsApplied, EventsSkipped, CatchUps, GatesHeld, MenusRefused;
        public static int LastLag;
        public static string LastStop = "";
        public static float LastStartDelay = -1f;       // seconds after the host's start at which this PC started its copy
        public static float LastReturnSeconds = -1f;    // how long putting the copy away took
        public static float LastCatchUpSeconds = -1f;   // how long the last covered catch-up took
        public static uint LastCatchUpSteps;            // ... and how many story steps it made up
        public static uint HostStep { get { return s_story.Step; } }      // this PC's own prologue (the host's side)
        public static int EventsLogged { get { return s_story.Count; } }
        public static uint LocalStep { get { return s_follow.Step; } }    // this PC's copy on the host's clock
        public static int EventsKnown { get { return s_follow.Count; } }
        public static bool FollowsStory { get { return Following; } }
        public static string RateNow { get { return s_pace.Rate.ToString(); } }
        // Test only: this PC's "device" (test games have no real input): horizontal axis, the jump button, and a button
        // the host's input never replaces (the map's legend button: nothing reacts to it in the prologue).
        internal static bool TestInput; internal static float TestHorizontal; internal static bool TestJump, TestOwnButton;
        internal static int TestSteps;   // physics steps seen by the input patch (probe)

        static float Now { get { return Services.Clock.RealTime; } }
        static bool InPrologue { get { return GameStateMachine.Instance != null && GameStateMachine.Instance.CurrentState == GameStateMachine.State.Prologue; } }
        static bool AtTitle
        {
            get
            {
                GameStateMachine g = GameStateMachine.Instance;
                return g != null && (g.CurrentState == GameStateMachine.State.StartScreen || g.CurrentState == GameStateMachine.State.TitleScreen);
            }
        }
        // The story stands still in this physics step: the game is frozen for a scene load, or suspended as a whole
        // (a pause menu offline, the resume screen). Its sequences and timelines skip such steps, so the clock does too.
        static bool StoryFrozen
        {
            get { GameController gc = GameController.Instance; return GameController.FreezeFixedUpdate || gc != null && gc.IsSuspended; }
        }
        // This PC's own prologue plays (not a copy of somebody else's): its story is counted and logged, online or not.
        static bool OwnStory { get { return s_local == Local.None && s_host.State.Phase == ProloguePhase.Playing; } }
        // The copy follows the host's story (a T-203 host sends none: then only its input is followed).
        static bool Following
        { get { return s_local == Local.Playing && s_follow.Known && s_followGeneration == s_remote.Generation; } }
        // ... and its own character / buttons are held at the story's player points.
        static bool HoldsGates { get { return Following && !s_mutateOwnGates; } }

        internal static void Init()
        {
            Services.Net.On(PrologueWire.Message, Receive);
            // Session events also fire on a reconnect and whenever the last other player leaves (a 2-player host
            // quitting ends the guest's "session" while online play stays on): they only make the host tell its state
            // again. What a guest heard from its host lives as long as the online run (TrackRun).
            Services.Net.SessionStarted += Republish;
            Services.Net.SessionEnded += Republish;
            Services.Net.PlayerJoined += delegate(int seat) { s_publishAt = 0f; s_logAt = 0f; };
            // The host's seat left while this PC's own link is up (a C-server room: the server says so). A lost link
            // (LAN: the host quitting looks the same) is HostHere = false instead, with its grace.
            Services.Net.PlayerLeft += delegate(int seat) { if (s_haveHost && seat == s_remoteSeat && Services.Net.Connected) s_hostLeft = true; };
        }

        static void Republish() { s_publishedAny = s_sentAnyInput = false; s_publishAt = s_inputAt = s_logAt = s_clockAt = 0f; }

        // One online-play run (NetPlay.Begin .. End): everything heard from its host belongs to it and to nothing later.
        // The host's own prologue (s_host, s_story) is the game's, not the run's; a copy still on screen (s_local) is
        // put away by GuestTick once its run is gone.
        static float s_run = -1f;
        static void TrackRun()
        {
            float run = Services.Net.Enabled ? NetPlay.StartedAt : -1f;
            if (run == s_run) return;
            s_run = run;
            requestedGeneration = shownGeneration = 0;
            s_guest.Reset();
            s_haveHost = s_hostLeft = false; s_remote = new PrologueState(); s_remoteSeat = 0;
            s_follow.Reset(); s_hostClock.Reset(); s_followGeneration = 0;
        }

        // The host's story messages belong to one of its prologues.
        static void FollowGeneration(uint generation)
        {
            if (generation == s_followGeneration) return;
            s_followGeneration = generation;
            s_follow.Reset(); s_hostClock.Reset();
        }

        static void Receive(BinaryReader r)
        {
            if (Services.Net.IsHost || Services.Net.Sender != Services.Net.HostSeat) return;
            byte kind = PrologueWire.ReadKind(r);
            if (kind == PrologueWire.KindState)
            {
                PrologueState state = PrologueWire.ReadState(r);
                TrackRun();   // a state that arrives before this run's first Tick belongs to this run
                if (!s_haveHost || state.Phase != s_remote.Phase || state.Generation != s_remote.Generation)
                    Services.Log.Info("prologue: host P" + Services.Net.Sender + " " + state.Phase + " (generation " + state.Generation
                        + ", " + state.Elapsed.ToString("F1") + " s in)");
                s_remote = state; s_haveHost = true; s_remoteAt = Now; s_remoteSeat = Services.Net.Sender;
            }
            else if (kind == PrologueWire.KindInput)
            {
                PrologueInput input = PrologueWire.ReadInput(r);
                if (s_local == Local.Playing) s_inputs.Push(input, Now);
            }
            else if (kind == PrologueWire.KindClock)
            {
                uint generation, step; int events;
                PrologueWire.ReadClock(r, out generation, out step, out events);
                TrackRun(); FollowGeneration(generation);
                s_follow.HostKnown();
                s_hostClock.Report(step, events, Now);
            }
            else if (kind == PrologueWire.KindLog)
            {
                uint generation; int first;
                PrologueEvent[] entries = PrologueWire.ReadLog(r, out generation, out first);
                TrackRun(); FollowGeneration(generation);
                s_follow.Receive(first, entries);
            }
        }

        // ---------------------------------------------------------------- per frame (SaveSync.Tick, before GuestJoin)

        public static void Tick()
        {
            float now = Now;
            TrackRun();
            TrackOwn(now);
            GuestTick(now);
        }

        // This PC's own prologue, online or not (a guest's copy of the host's is not its own): its true start time and
        // its story are known whenever a session wants them. Only an online host tells anyone.
        static void TrackOwn(float now)
        {
            s_host.Tick(InPrologue && s_local == Local.None, AtTitle, Difficulty(), now);
            PrologueState state = s_host.State;
            bool changed = state.Phase != s_announced.Phase || state.Generation != s_announced.Generation;
            if (changed)
            {
                s_announced = state;
                Services.Log.Info("prologue: " + state.Phase + " (generation " + state.Generation + ")"
                    + (state.Phase == ProloguePhase.Ended ? ", story step " + s_story.Step + ", " + s_story.Count + " player points logged" : ""));
                CoopTrace.Event("story", "prologue.host", "phase", state.Phase, "generation", state.Generation);
                if (state.Phase == ProloguePhase.Playing)
                {
                    HostStarts++; s_sentAnyInput = false;
                    s_story.Begin(); s_buttonsLogged.Clear(); s_logSent = 0; s_logAt = s_clockAt = 0f;
                }
            }
            // The story first: a clock never reports more points than the log messages sent before it (a guest that
            // has fewer points than the clock says stands still until they are there).
            if (Services.Net.Enabled && Services.Net.IsHost) { PublishStory(now); Publish(changed); }
        }

        static byte Difficulty()
        {
            DifficultyController d = DifficultyController.Instance;
            int value = d == null ? (int)DifficultyMode.Normal : (int)d.Difficulty;
            return (byte)Mathf.Clamp(value, 0, 3);
        }

        static void Publish(bool force)
        {
            if (!Services.Net.Connected) return;
            PrologueState state = s_host.State;
            // A host that loads an existing save never has a prologue: nothing is sent at all.
            if (state.Phase == ProloguePhase.None && !s_publishedAny) return;
            // On every change and to a seat that just joined; while it plays also once a second (the elapsed time).
            if (!force && (Now < s_publishAt || state.Phase != ProloguePhase.Playing && s_publishAt > 0f)) return;
            s_publishAt = Now + 1f; s_publishedAny = true;
            // The clock goes first: a guest that hears "playing, 40 s in" already knows this host can be joined late.
            if (state.Phase == ProloguePhase.Playing) SendClock();
            Services.Net.Send(PrologueWire.Message, true, delegate(BinaryWriter w) { PrologueWire.WriteState(w, state); });
        }

        // T-205: the story clock every 0.25 s; the log on every new entry, to a seat that just joined, and whole
        // again every 3 s (a guest takes only what extends its copy of the log).
        static void PublishStory(float now)
        {
            if (!Services.Net.Connected || s_host.State.Phase != ProloguePhase.Playing || s_local != Local.None) return;
            if (now >= s_logAt) { s_logAt = now + LogEvery; SendLog(0, s_story.Count); s_logSent = s_story.Count; SendClock(); }
            else if (s_logSent < s_story.Count) { SendLog(s_logSent, s_story.Count - s_logSent); s_logSent = s_story.Count; SendClock(); }
            else if (now >= s_clockAt) SendClock();
        }

        static void SendClock()
        {
            uint generation = s_host.State.Generation, step = s_story.Step; int events = s_story.Count;
            s_clockAt = Now + ClockEvery;
            Services.Net.Send(PrologueWire.Message, true, delegate(BinaryWriter w) { PrologueWire.WriteClock(w, generation, step, events); });
        }

        static void SendLog(int first, int count)
        {
            uint generation = s_host.State.Generation;
            for (int at = first; at < first + count; at += PrologueWire.MaxLogBatch)
            {
                int from = at, n = Math.Min(PrologueWire.MaxLogBatch, first + count - at);
                Services.Net.Send(PrologueWire.Message, true, delegate(BinaryWriter w) { PrologueWire.WriteLog(w, generation, s_story, from, n); });
            }
        }

        static void GuestTick(float now)
        {
            bool guestSession = Services.Net.Enabled && !Services.Net.IsHost;
            PrologueGuestInput i = new PrologueGuestInput();
            i.HaveHost = s_haveHost && guestSession; i.Host = s_remote;
            if (i.HaveHost && i.Host.Phase == ProloguePhase.Playing) i.Host.Elapsed += Mathf.Max(0f, now - s_remoteAt);
            i.HostHere = guestSession && Services.Net.Connected && (!s_haveHost || NetPlay.RemoteInSession(s_remoteSeat));
            i.HostLeft = s_hostLeft || s_haveHost && (!guestSession || Services.Net.HostSeat != s_remoteSeat);
            i.Waiting = GuestJoin.Waiting && !GuestJoin.NeedSlotChoice;
            i.CanStart = CanStartLocal();
            i.LocalEnded = LocalEnded(now);
            i.Returned = s_local == Local.None;
            i.HostStory = s_follow.Known && s_followGeneration == s_remote.Generation;
            i.Now = now;
            switch (s_guest.Tick(i))
            {
                case PrologueAction.Start: StartLocal(i.Host); break;
                case PrologueAction.Stop:
                    PutAway(!guestSession ? "the session ended" : !i.Waiting ? "the join ended"
                        : s_lost ? "this copy could not follow the host's story" : i.LocalEnded ? "this copy reached its end first" : "the host's prologue ended");
                    break;
                case PrologueAction.HostLeft:
                    PutAway("the host left");
                    GuestJoin.HostLeft();
                    break;
            }
            // Whatever the rules above saw: a copy never outlives the session it was started for.
            if (s_local == Local.Playing && !guestSession) PutAway("the session ended");
            if (s_local == Local.Playing && InPrologue) s_localSawPrologue = true;
            if (s_local == Local.Playing) Pace(now); else ReleaseTime();
            if (s_local == Local.Returning) FinishReturn(now);
        }

        static bool CanStartLocal()
        {
            if (s_mutateOff || s_local != Local.None) return false;
            SaveSlotsUI ui = SaveSlotsUI.Instance; GameController gc = GameController.Instance;
            return ui != null && ui.EmptySaveSlotPressedAction != null && gc != null && !gc.IsLoadingGame && AtTitle
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots && DifficultyController.Instance != null
                && !SaveSync.HavePendingCheckpoint && !GuestJoin.Loading;
        }

        // The local copy is over by itself: it left the Prologue state (played to its end before the host's did, or the
        // game tried to: NewGameAction / SkipPrologue are held back below), or it never got there.
        static bool LocalEnded(float now)
        {
            if (s_local != Local.Playing) return false;
            if (s_localEndRequested || s_lost) return true;
            if (s_localSawPrologue) return !InPrologue;
            return now - s_startedAt > PrologueHost.StartGrace;
        }

        static void StartLocal(PrologueState host)
        {
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            s_inputs.Clear(); s_local = Local.Playing; s_localSawPrologue = s_localEndRequested = false; s_startedAt = Now; s_titleAt = -1f;
            s_follow.Restart(); s_pace.Reset(); s_buttonPass = 0; s_buttonsPassed.Clear(); s_triggers.Clear(); s_dueSince = -1f; s_wasFast = false; s_lost = false;
            LocalStarts++; LastStartDelay = host.Elapsed;
            Services.Log.Info("prologue: playing along with the host (generation " + host.Generation + ", difficulty " + (DifficultyMode)host.Difficulty
                + ", " + host.Elapsed.ToString("F2") + " s after its start" + (s_follow.Known ? ", its story is at step " + s_hostClock.Now(Now) + " with "
                + s_follow.Count + " player points" : ", it sends no story position") + ")");
            CoopTrace.Event("story", "prologue.guest.start", "generation", host.Generation, "delay", host.Elapsed);
            try
            {
                DifficultyController.Instance.SetDifficulty((DifficultyMode)host.Difficulty);
                // The game's own new-game action, as SaveSlotsUI.SetDifficulty runs it; no slot is created or written
                // (a guest's saves are all suppressed, SaveSync).
                ui.EmptySaveSlotPressedAction.Perform(null);
                ui.Active = false;
                // A copy that starts far behind is covered from its first frame, not after the title has flashed by.
                Pace(Now);
            }
            catch (Exception e)
            {
                Services.Log.Warn("prologue: starting the local copy failed: " + e);
                PutAway("the local copy could not start");
            }
        }

        // End the local copy: back to the title through the game's own return path. The session and the join stay.
        static void PutAway(string why)
        {
            if (s_local != Local.Playing) return;
            LocalStops++; LastStop = why;
            Services.Log.Info("prologue: local copy ends (" + why + "), " + (Now - s_startedAt).ToString("F1") + " s after it started; back to the title");
            CoopTrace.Event("story", "prologue.guest.stop", "why", why);
            s_local = Local.Returning; s_returnAt = Now; s_titleAt = -1f; s_inputs.Clear();
            ReleaseTime();
            try
            {
                if (UI.Menu != null && UI.MainMenuVisible) UI.Menu.HideMenuScreen(true);   // this player's own pause menu
                s_ownRestart = true;
                GameController.Instance.RestartGame();
            }
            catch (Exception e) { Services.Log.Warn("prologue: returning to the title failed: " + e); }
            finally { s_ownRestart = false; }
        }

        static void FinishReturn(float now)
        {
            GameController gc = GameController.Instance;
            TitleScreenManager.Screen screen = TitleScreenManager.CurrentScreen;
            bool title = AtTitle && gc != null && !gc.IsLoadingGame
                && (screen == TitleScreenManager.Screen.PressStart || screen == TitleScreenManager.Screen.MainMenu);
            if (title && s_titleAt < 0f) s_titleAt = now;
            bool late = now - s_returnAt > 30f;
            // Half a second on the title before the join may load from it (the title's own start-up action settles).
            if (!(title && now - s_titleAt >= 0.5f) && !late) return;
            s_local = Local.None; LastReturnSeconds = now - s_returnAt;
            if (late && !title) Services.Log.Warn("prologue: the title did not come back within 30 s (screen " + screen + ")");
            else Services.Log.Info("prologue: back on the title after " + LastReturnSeconds.ToString("F1") + " s");
            if (!GuestJoin.Waiting) return;
            // The join goes on: the load-from-title action expects the save-slot screen (as NativeModeMenu.StartGuestNoSlot sets it).
            if (TitleScreenManager.CurrentScreen != TitleScreenManager.Screen.SaveSlots) TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
            if (SaveSlotsUI.Instance != null) SaveSlotsUI.Instance.Active = false;
        }

        // Any other return to the title while the copy plays (the player's own pause menu -> quit, a disconnect dialog):
        // the copy goes with it. PutAway above has already moved on to Returning before its own RestartGame.
        [HarmonyPatch(typeof(GameController), "RestartGame")]
        static class RestartTakesCopy
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                if (s_local != Local.Playing) return;
                LocalStops++; LastStop = "returned to the title";
                Services.Log.Info("prologue: local copy ends (the game returns to the title), " + (Now - s_startedAt).ToString("F1") + " s after it started");
                s_local = Local.Returning; s_returnAt = Now; s_titleAt = -1f; s_inputs.Clear();
                ReleaseTime();
            }
        }

        // ---------------------------------------------------------------- guest: the copy's speed (T-205)

        // Once per frame while the copy plays: how far behind the host it is, and how fast it therefore runs.
        static void Pace(float now)
        {
            // No clock from the host for a second (a link drop, inside its 10 s grace): the copy plays on at its
            // normal speed; what it was doing (a covered catch-up) goes on when the clock is back.
            if (!Following || s_mutateNoCatchUp || !s_hostClock.Fresh(now)) { ReleaseTime(); return; }
            int lag = s_follow.Lag(s_hostClock.Now(now));
            LastLag = lag;
            bool covered = s_pace.Covered;
            // The host's clock counts more points than have arrived here (a piece of the log is late or was refused):
            // the step count alone would let this copy run past them and look level.
            PrologueRate rate = s_pace.Decide(lag, s_hostClock.Events > s_follow.Count);
            if (s_pace.Covered && !covered)
            {
                CatchUps++; s_coverSince = now; s_coverFrom = s_follow.Step;
                Services.Log.Info("prologue: " + (lag / 60f).ToString("F1") + " s behind the host: catching up (fast-forward under the cover screen)");
                CoopTrace.Event("story", "prologue.guest.catchup", "behind", lag / 60f);
            }
            else if (!s_pace.Covered && covered)
            {
                LastCatchUpSeconds = now - s_coverSince; LastCatchUpSteps = s_follow.Step - s_coverFrom;
                Services.Log.Info("prologue: level with the host after " + LastCatchUpSeconds.ToString("F1") + " s (" + (LastCatchUpSteps / 60f).ToString("F1")
                    + " s of story made up, " + s_follow.Applied + " of " + s_follow.Count + " player points done, " + EventsSkipped + " left out)");
                CoopTrace.Event("story", "prologue.guest.level", "seconds", LastCatchUpSeconds, "story", LastCatchUpSteps / 60f);
            }
            bool fast = rate == PrologueRate.Fast;
            if (s_wasFast && !fast) StopFinishedSounds();
            s_wasFast = fast;
            if (rate == PrologueRate.Normal) { ReleaseTime(); return; }
            bool loading = Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes;
            s_ownsTime = true;
            // The frame-time cap applies before the scale: with 1/60 s a scale of n is at most n steps per frame.
            Time.maximumDeltaTime = 1f / 60f;
            Time.timeScale = ProloguePace.Scale(rate, loading);
        }

        // The game's own pacing takes over again (SyncFramesTest, PhysicsLimitTest).
        static void ReleaseTime()
        {
            if (!s_ownsTime) return;
            s_ownsTime = false;
            if (s_wasFast) { StopFinishedSounds(); s_wasFast = false; }
            Time.timeScale = 1f;
            Time.maximumDeltaTime = PhysicsLimitTest.IsSynced ? 1f / 60f : 1f / 30f;
        }

        // While this module sets the time scale the game's own frame pacing (which rewrites it every frame) stands back.
        [HarmonyPatch(typeof(SyncFramesTest), "Update")]
        static class OwnTime
        {
            static bool Prefix() { return !s_ownsTime; }
        }

        // A sound that started during the fast-forward and whose whole length has passed in story time: the game
        // would let it play on from the little it really played (SoundPlayer.Update sets the time only when it is
        // inside the clip). Looping sounds and those still inside their clip are the game's to continue.
        static readonly FieldInfo s_soundScaled = AccessTools.Field(typeof(SoundPlayer), "m_timeScaleChanged");
        static readonly FieldInfo s_soundTime = AccessTools.Field(typeof(SoundPlayer), "m_timeToSet");
        static void StopFinishedSounds()
        {
            if (s_soundScaled == null || s_soundTime == null) return;
            try
            {
                int stopped = 0;
                foreach (SoundPlayer sound in new List<SoundPlayer>(SoundPlayer.All))
                {
                    if (sound == null || sound.Loop || sound.Clip == null || !(bool)s_soundScaled.GetValue(sound)) continue;
                    if ((float)s_soundTime.GetValue(sound) < sound.Clip.length - 0.05f) continue;
                    sound.DestroySound(); stopped++;
                }
                if (stopped > 0) Services.Log.Info("prologue: " + stopped + " sound(s) that ended during the fast-forward stopped");
            }
            catch (Exception e) { Services.Log.Warn("prologue: stopping finished sounds failed: " + e.Message); }
        }

        // ---------------------------------------------------------------- the story: what depends on the player (T-205)

        static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }
        static uint TriggerId(Trigger t)
        {
            Vector3 p = t.transform.position;
            return PrologueHash.Of(PathOf(t.transform) + "@" + Mathf.RoundToInt(p.x * 10f) + "," + Mathf.RoundToInt(p.y * 10f));
        }
        // The state's own class is part of the id: two states may sit on one object.
        static uint CutsceneId(CutsceneController c, CutsceneState state)
        { return PrologueHash.Of(PathOf(c.transform) + ">" + PathOf(state.transform) + ":" + state.GetType().Name); }
        // The triggers a character fires by walking into them.
        static bool IsPlayerTrigger(Trigger t) { return t is PlayerCollisionTrigger || t is DirectionalPlayerCollisionTrigger; }

        // Host: one more point of its story that the player decided.
        static void HostEvent(byte kind, uint id, string what)
        {
            if (!OwnStory) return;
            ICharacter character;
            byte actor = Actor(out character);
            Vector3 p = character != null ? character.Position : Vector3.zero;
            int index = s_story.Add(kind, id, actor, p.x, p.y);
            if (index < 0) { Services.Log.Warn("prologue: the story log is full; " + what + " is not logged"); return; }
            CoopTrace.Event("story", "prologue.host.point", "index", index, "step", s_story.Step, "kind", kind, "what", what);
        }

        // Guest, once per physics step of its copy: the clock, and the host's next point when its step has come.
        static void GuestStep()
        {
            s_follow.Tick(StoryFrozen);
            PrologueEvent e;
            // Nothing is done in a step in which the game is frozen for a scene load (it ends by itself): no
            // character is moved, no trigger fired, and the time limit below does not run. A suspension of the story
            // itself (a text that waits) is not held back here: the host's point may be what ends it.
            if (!s_follow.Due(GameController.FreezeFixedUpdate, out e)) { s_dueSince = -1f; s_buttonPass = 0; return; }
            float now = Now;
            // Not counted while a scene loads: the trigger's scene may simply not be here yet.
            if (s_dueSince < 0f || Core.Scenes.Manager != null && Core.Scenes.Manager.IsLoadingScenes) s_dueSince = now;
            bool done = false;
            // First the character goes where the host's stood: a copy that fast-forwards does not walk, and the
            // trigger may sit in the next scene, which only loads once the character is near it.
            SnapActor(e);
            switch (e.Kind)
            {
                case PrologueEventKind.Trigger:
                    Trigger trigger = FindTrigger(e.Id, now);
                    // Not taken (see Replay): tried again in the next step, like a trigger that is not here yet.
                    if (trigger != null) done = Replay(trigger);
                    break;
                case PrologueEventKind.Cutscene:
                    CutsceneController controller; CutsceneState state;
                    // Every state of that cutscene works on the grown Ori: not before it is there (on the host it was).
                    if (Characters.Sein != null && FindCutscene(e.Id, now, out controller, out state))
                    {
                        s_replaying = true;
                        // Not ready yet (its character appears a frame later than on the host): tried again next step.
                        try { controller.ChangeState(state); done = true; }
                        catch (Exception) { }
                        finally { s_replaying = false; }
                    }
                    break;
                case PrologueEventKind.Button:
                    s_buttonPass = e.Id;   // the game's own wait asks every step; ButtonGate answers yes and calls Done
                    break;
                case PrologueEventKind.Load:
                    break;                 // this copy's own story loads the scene (StoryLoad below)
            }
            if (done) { s_follow.Done(); s_dueSince = -1f; EventsApplied++; }
            else if (now - s_dueSince > DueLimit && !s_lost)
            {
                // The story of this copy cannot go on without that point, and nothing after it would fit: the copy
                // is put away (GuestTick) and this PC waits for the host's save, with the waiting screen, as a late
                // guest did before T-205. Never a picture that is not the host's.
                Services.Log.Warn("prologue: the host's point " + s_follow.Applied + " (kind " + e.Kind + ", step " + e.Step + ", object id " + e.Id.ToString("X8")
                    + ") could not be done in this copy for " + DueLimit + " s (scene " + SceneName() + ", character " + Actor(out s_unused)
                    + "): this copy cannot follow the host's story and is put away");
                CoopTrace.Event("story", "prologue.guest.lost", "point", s_follow.Applied, "kind", e.Kind, "step", e.Step);
                s_lost = true; s_buttonPass = 0; EventsSkipped++;
            }
        }

        // Fires a player trigger of this copy because the host's log says so. True only when the game really took it
        // (Trigger.ProcessTrigger began): DoTrigger(false) leaves out the trigger's condition (the host's held), not
        // its other rules. A trigger that is switched off or used up (Active), whose action still runs
        // (DontTriggerWhileRunning) or whose object is inactive does nothing; such a point is not done.
        internal static bool Replay(Trigger trigger)
        {
            // The game would mark a once-only trigger as used and then fail to start it on an inactive object.
            if (!s_mutateReplayBlind && !trigger.gameObject.activeInHierarchy) return false;
            s_replayTarget = trigger; s_replayFired = false; s_replaying = true;
            try { trigger.DoTrigger(false); }
            // It began and then failed inside its own action: the game took it, asking again would not change that.
            catch (Exception x) { Services.Log.Warn("prologue: trigger " + PathOf(trigger.transform) + " failed: " + x.Message); }
            finally { s_replaying = false; s_replayTarget = null; }
            return s_replayFired || s_mutateReplayBlind;
        }

        static ICharacter s_unused;
        static string SceneName() { return Core.Scenes.Manager != null && Core.Scenes.Manager.CurrentScene != null ? Core.Scenes.Manager.CurrentScene.Scene : "?"; }

        // The host's character stood here when it happened: this copy's character stands there too (it does not walk
        // while the copy fast-forwards).
        static void SnapActor(PrologueEvent e)
        {
            ICharacter character;
            if (e.Actor == PrologueActor.None || Actor(out character) != e.Actor) return;
            Vector3 p = character.Position;
            character.Position = new Vector3(e.X, e.Y, p.z);
        }

        static Trigger FindTrigger(uint id, float now)
        {
            Trigger found;
            if (s_triggers.TryGetValue(id, out found) && found != null) return found;
            if (now < s_scanAt) return null;
            s_scanAt = now + 0.2f;
            s_triggers.Clear();
            foreach (Trigger t in UnityEngine.Object.FindObjectsOfType<Trigger>())
                if (IsPlayerTrigger(t)) s_triggers[TriggerId(t)] = t;
            return s_triggers.TryGetValue(id, out found) ? found : null;
        }

        static bool FindCutscene(uint id, float now, out CutsceneController controller, out CutsceneState state)
        {
            controller = null; state = null;
            if (now < s_scanAt) return false;
            foreach (CutsceneController c in UnityEngine.Object.FindObjectsOfType<CutsceneController>())
                foreach (CutsceneState s in c.GetComponentsInChildren<CutsceneState>(true))
                    if (CutsceneId(c, s) == id) { controller = c; state = s; return true; }
            s_scanAt = now + 0.2f;
            return false;
        }

        // A character walks into a trigger. Host: logged when it really fires. Guest: its own character never fires
        // one; the host's log does (GuestStep).
        [HarmonyPatch(typeof(Trigger), "DoTrigger", new[] { typeof(bool) })]
        static class GuestTriggerHeld
        {
            static bool Prefix(Trigger __instance)
            {
                if (!HoldsGates || s_replaying || !IsPlayerTrigger(__instance)) return true;
                GatesHeld++;
                return false;
            }
        }

        [HarmonyPatch(typeof(Trigger), "ProcessTrigger")]
        static class HostTriggerFired
        {
            static void Prefix(Trigger __instance)
            {
                if (s_replaying && ReferenceEquals(__instance, s_replayTarget)) s_replayFired = true;   // guest: Replay's answer
                if (OwnStory && IsPlayerTrigger(__instance)) HostEvent(PrologueEventKind.Trigger, TriggerId(__instance), PathOf(__instance.transform));
            }
        }

        // "Press a button to go on". Host: logged the first time the wait is answered. Guest: this PC's own buttons
        // (any of them, Start and the menu buttons included) never answer it; the host's log does.
        [HarmonyPatch(typeof(ButtonPressedCondition), "Validate")]
        static class ButtonGate
        {
            static bool Prefix(ButtonPressedCondition __instance, ref bool __result)
            {
                if (!HoldsGates) return true;
                int instance = __instance.GetInstanceID();
                if (s_buttonsPassed.Contains(instance)) { __result = true; return false; }   // asked more than once in a step
                if (s_buttonPass != 0 && PrologueHash.Of(PathOf(__instance.transform)) == s_buttonPass)
                {
                    s_buttonsPassed.Add(instance); s_buttonPass = 0;
                    s_follow.Done(); s_dueSince = -1f; EventsApplied++;
                    __result = true;
                    return false;
                }
                GatesHeld++;
                __result = false;
                return false;
            }
            static void Postfix(ButtonPressedCondition __instance, bool __result)
            {
                if (!__result || !OwnStory || !s_buttonsLogged.Add(__instance.GetInstanceID())) return;
                string path = PathOf(__instance.transform);
                HostEvent(PrologueEventKind.Button, PrologueHash.Of(path), path);
            }
        }

        // The interactive cutscene (the weak Ori before the Spirit Tree): every state change is the host's.
        [HarmonyPatch(typeof(CutsceneController), "ChangeState")]
        static class CutsceneStateChange
        {
            static bool Prefix(CutsceneController __instance, CutsceneState state)
            {
                if (state == null) return true;
                if (HoldsGates) { if (!s_replaying) GatesHeld++; return s_replaying; }
                if (Following) return true;
                if (OwnStory) HostEvent(PrologueEventKind.Cutscene, CutsceneId(__instance, state), state.name);
                return true;
            }
        }

        // The story loads its next scene: every copy does this by itself, and it lines the clocks up again.
        [HarmonyPatch(typeof(LoadSceneAction), "Perform")]
        static class StoryLoad
        {
            static void Prefix(LoadSceneAction __instance)
            {
                if (__instance.SceneMetaData == null) return;
                string scene = __instance.SceneMetaData.name;
                if (Following) s_follow.LocalLoad(PrologueHash.Of(scene));
                else if (OwnStory) HostEvent(PrologueEventKind.Load, PrologueHash.Of(scene), scene);
            }
        }

        // ---------------------------------------------------------------- host: the new game starts

        [HarmonyPatch(typeof(SaveSlotsUI), "SetDifficulty")]
        static class HostNewGame
        {
            // After the game's own SetDifficulty really ran (the mod's menus skip it until 开始游戏): the first frame of the prologue.
            static void Postfix(DifficultyMode difficulty, bool __runOriginal)
            {
                if (!__runOriginal || s_local != Local.None) return;
                s_host.Started(Now, (byte)Mathf.Clamp((int)difficulty, 0, 3));
                TrackOwn(Now);
            }
        }

        // ---------------------------------------------------------------- input

        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class PrologueInputPatch
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix()
            {
                TestSteps++;
                if (TestInput) ApplyTestInput();
                if (s_local == Local.Playing)
                {
                    if (Following) GuestStep();
                    if (!s_mutateControl) ApplyHostInput();
                }
                else
                {
                    if (OwnStory) s_story.Tick(StoryFrozen);
                    if (HostAnnouncing && Services.Net.Connected) SendHostInput();
                }
            }
        }

        static void ApplyTestInput()
        {
            Core.Input.Horizontal = TestHorizontal; Core.Input.HorizontalAnalogLeft = TestHorizontal;
            Core.Input.HorizontalDigiPad = TestHorizontal > 0.4f ? 1 : TestHorizontal < -0.4f ? -1 : 0;
            Core.Input.Jump.IsPressed = TestJump;
            if (TestOwnButton) Core.Input.Legend.IsPressed = true;
        }

        static byte Actor(out ICharacter character)
        {
            character = null;
            ICharacter current = Characters.Current;
            if (current == null) return PrologueActor.None;
            if (Characters.Naru != null && object.ReferenceEquals(current, Characters.Naru)) { character = Characters.Naru; return PrologueActor.Naru; }
            if (Characters.BabySein != null && object.ReferenceEquals(current, Characters.BabySein)) { character = Characters.BabySein; return PrologueActor.BabyOri; }
            if (Characters.Sein != null && object.ReferenceEquals(current, Characters.Sein)) { character = Characters.Sein; return PrologueActor.Ori; }
            return PrologueActor.None;
        }

        static void SendHostInput()
        {
            PrologueInput input = new PrologueInput { Generation = s_host.State.Generation };
            input.Horizontal = PrologueButtons.Quantise(Core.Input.Horizontal);
            input.Vertical = PrologueButtons.Quantise(Core.Input.Vertical);
            for (int bit = 0; bit < PrologueButtons.Mirrored.Length; bit++)
                if (Core.Input.Buttons[PrologueButtons.Mirrored[bit]].IsPressed) input.Buttons |= (ushort)(1 << bit);
            ICharacter character;
            input.Actor = Actor(out character);
            if (character != null) { Vector3 p = character.Position; input.X = p.x; input.Y = p.y; }
            // Every change of the input at once (a press and its release are two messages), else every 0.2 s for the position.
            bool changed = !s_sentAnyInput || input.Horizontal != s_sentInput.Horizontal || input.Vertical != s_sentInput.Vertical
                || input.Buttons != s_sentInput.Buttons || input.Generation != s_sentInput.Generation;
            if (!changed && Now < s_inputAt) return;
            input.Sequence = ++s_sequence; s_sentInput = input; s_sentAnyInput = true; s_inputAt = Now + 0.2f; InputsSent++;
            Services.Net.Send(PrologueWire.Message, true, delegate(BinaryWriter w) { PrologueWire.WriteInput(w, input); });
        }

        static void ApplyHostInput()
        {
            float now = Now;
            ICharacter character;
            byte actor = Actor(out character);
            // T-209 (3): the host's input drives the character the host controls, never another one that happens to
            // be here (this copy is in another part: behind after a slow load, or still catching up).
            PrologueInput input = s_inputs.Next(now).For(actor);
            // ... nor a character of the same kind on the other side of a scene load: the host is already in the next
            // scene (or still in the one before), its input and its position belong there. Also while the copy
            // fast-forwards: the host's input of now belongs to a later moment of the story.
            bool elsewhere = Following && (s_pace.Rate == PrologueRate.Fast || s_follow.AcrossLoad);
            // This player's own pause menu is open: the gameplay input is neutral (the host's jump must not press a menu
            // item); the menu buttons below stay this player's own.
            bool neutral = UI.MainMenuVisible || elsewhere;
            float h = neutral ? 0f : PrologueButtons.Axis(input.Horizontal), v = neutral ? 0f : PrologueButtons.Axis(input.Vertical);
            Core.Input.Horizontal = h; Core.Input.Vertical = v;
            Core.Input.HorizontalAnalogLeft = h; Core.Input.VerticalAnalogLeft = v;
            Core.Input.HorizontalAnalogRight = Core.Input.VerticalAnalogRight = 0f;
            Core.Input.HorizontalDigiPad = h > 0.4f ? 1 : h < -0.4f ? -1 : 0;
            Core.Input.VerticalDigiPad = v > 0.6f ? 1 : v < -0.6f ? -1 : 0;
            // PlayerInput just moved every button's IsPressed into WasPressed and read this PC's device: only the new
            // value is replaced, so the host's presses still give OnPressed edges here.
            for (int bit = 0; bit < PrologueButtons.Mirrored.Length; bit++)
                Core.Input.Buttons[PrologueButtons.Mirrored[bit]].IsPressed = !neutral && (input.Buttons & (1 << bit)) != 0;
            InputsApplied++;
            PrologueInput newest;
            // T-209 (4): TryNewest refuses a stale report (a lost link): nothing is pulled back to an old position.
            if (elsewhere || now < s_snapAt || character == null || !s_inputs.TryNewest(now, out newest) || newest.Actor != actor) return;
            Vector3 p = character.Position;
            if (!PrologueDrift.ShouldSnap(newest.X - p.x, newest.Y - p.y, s_inputs.HostResting(now), s_inputs.CaughtUp)) return;
            character.Position = new Vector3(newest.X, newest.Y, p.z);
            Snaps++; s_snapAt = now + 0.5f;
            CoopTrace.Event("story", "prologue.guest.snap", "actor", newest.Actor, "from", p.x.ToString("F2") + "," + p.y.ToString("F2"),
                "to", newest.X.ToString("F2") + "," + newest.Y.ToString("F2"));
        }

        // The guest can request the current prologue. Only the host's native skip ends it.
        static uint requestedGeneration, shownGeneration;
        internal static int RequestsShown;
        internal static void ReceiveSkipRequest(uint generation)
        {
            if (!OwnStory || !InPrologue || generation == 0 || generation != s_host.State.Generation || shownGeneration == generation) return;
            shownGeneration = generation; RequestsShown++;
            NetHud.Toast(L10n.T("story.request.cutscene"));
        }

        static bool GuestHolds { get { return s_local == Local.Playing && !s_mutateControl; } }

        [HarmonyPatch(typeof(SkipCutsceneController), "get_SkippingAvailable")]
        static class GuestSkipAvailable
        {
            static void Postfix(ref bool __result)
            {
                if (GuestHolds) __result = !CatchingUp && InPrologue && s_haveHost && !s_hostLeft && Services.Net.Connected;
            }
        }

        [HarmonyPatch(typeof(SkipCutsceneController), "SkipCutscene")]
        static class GuestSkipRefused
        {
            static bool Prefix()
            {
                if (!GuestHolds) return true;
                SkipsRefused++;
                uint generation = s_remote.Generation;
                if (!CatchingUp && InPrologue && s_haveHost && !s_hostLeft && Services.Net.Connected &&
                    Services.Net.HostSeat == s_remoteSeat && s_remote.Phase == ProloguePhase.Playing && generation != requestedGeneration)
                {
                    requestedGeneration = generation;
                    StorySync.SendRequestToHost(delegate(BinaryWriter w) { w.Write((byte)2); w.Write(generation); });
                    NetHud.Toast(L10n.T("story.request.sent"));
                }
                return false;
            }
        }

        // The catch-up cover is drawn over everything: a pause menu opened under it could not be seen, and its items
        // would still take this player's buttons. Until the copy is level no key of this PC does anything; the menu
        // opens as soon as the cover is gone. (One that is already open can still be closed.)
        [HarmonyPatch(typeof(MenuScreenManager), "DoMenuKeyPress")]
        static class NoMenuUnderCover
        {
            static bool Prefix(MenuScreenManager __instance)
            {
                if (!CatchingUp || s_mutateMenuUnderCover || __instance.MainMenuVisible) return true;
                MenusRefused++;
                return false;
            }
        }

        // The prologue's own last step (or any other caller) would turn this copy into a new game of its own. It is
        // held back and the copy is put away instead (LocalEnded): this PC then waits for the host like everyone else.
        [HarmonyPatch(typeof(SkipCutsceneController), "SkipPrologue")]
        static class GuestPrologueEndHeld
        {
            static bool Prefix()
            {
                if (s_local != Local.Playing) return true;
                s_localEndRequested = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(NewGameAction), "Perform")]
        static class GuestNewGameHeld
        {
            static bool Prefix()
            {
                // Not the copy's own start (the title's new-game action runs NewGameAction before the Prologue state).
                if (s_local != Local.Playing || !InPrologue) return true;
                s_localEndRequested = true;
                return false;
            }
        }
    }
}
