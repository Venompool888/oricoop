using System;
using System.IO;
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
    //   guest  a guest that already waits on the title for the host's save (GuestJoin) runs the same new-game action
    //          at once: the same prologue plays here. The playable parts take the host's input, never this PC's;
    //          skipping is refused. The guest's own pause menu still opens (online menus do not pause), so the player
    //          can leave. When the host's prologue ends, this copy is put away (back to the title, the session
    //          stays) and the join goes on exactly as before: the host's save loads from the title.
    //   late   a guest that arrives later keeps the waiting screen (SessionScreens says why) until the host is done.
    // Nothing is saved here: a guest never writes a slot, and the local new game is thrown away at the end.
    public static class SharedPrologue
    {
        enum Local { None, Playing, Returning }

        static readonly PrologueHost s_host = new PrologueHost();
        static readonly PrologueGuest s_guest = new PrologueGuest();
        static readonly PrologueInputQueue s_inputs = new PrologueInputQueue();
        // Test-only mutations: the guest never plays along / the guest's own input and skip are not replaced.
        static readonly bool s_mutateOff = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_SHARED_PROLOGUE") == "1";
        static readonly bool s_mutateControl = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_GUEST_CONTROL") == "1";

        // host
        static PrologueState s_announced;
        static float s_publishAt, s_inputAt;
        static bool s_publishedAny, s_sentAnyInput;
        static PrologueInput s_sentInput;
        static uint s_sequence;
        // guest
        static bool s_haveHost, s_hostLeft, s_ownRestart, s_localSawPrologue, s_localEndRequested;
        static PrologueState s_remote;
        static int s_remoteSeat;
        static float s_remoteAt, s_startedAt, s_returnAt, s_titleAt = -1f, s_snapAt;
        static Local s_local;

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

        // For tests and logs.
        public static int LocalStarts, LocalStops, SkipsRefused, Snaps, InputsSent, InputsApplied, HostStarts;
        public static string LastStop = "";
        public static float LastStartDelay = -1f;      // seconds after the host's start at which this PC started its copy
        public static float LastReturnSeconds = -1f;   // how long putting the copy away took
        // Test only: this PC's "device" (test games have no real input): horizontal axis and the jump button.
        internal static bool TestInput; internal static float TestHorizontal; internal static bool TestJump;

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

        internal static void Init()
        {
            Services.Net.On(PrologueWire.Message, Receive);
            // Session events also fire on a reconnect and whenever the last other player leaves (a 2-player host
            // quitting ends the guest's "session" while online play stays on): they only make the host tell its state
            // again. What a guest heard from its host lives as long as the online run (TrackRun).
            Services.Net.SessionStarted += Republish;
            Services.Net.SessionEnded += Republish;
            Services.Net.PlayerJoined += delegate(int seat) { s_publishAt = 0f; };
            // The host's seat left while this PC's own link is up (a C-server room: the server says so). A lost link
            // (LAN: the host quitting looks the same) is HostHere = false instead, with its grace.
            Services.Net.PlayerLeft += delegate(int seat) { if (s_haveHost && seat == s_remoteSeat && Services.Net.Connected) s_hostLeft = true; };
        }

        static void Republish() { s_publishedAny = s_sentAnyInput = false; s_publishAt = s_inputAt = 0f; }

        // One online-play run (NetPlay.Begin .. End): everything heard from its host belongs to it and to nothing later.
        // The host's own prologue (s_host) is the game's, not the run's; a copy still on screen (s_local) is put away
        // by GuestTick once its run is gone.
        static float s_run = -1f;
        static void TrackRun()
        {
            float run = Services.Net.Enabled ? NetPlay.StartedAt : -1f;
            if (run == s_run) return;
            s_run = run;
            s_guest.Reset();
            s_haveHost = s_hostLeft = false; s_remote = new PrologueState(); s_remoteSeat = 0;
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
        }

        // ---------------------------------------------------------------- per frame (SaveSync.Tick, before GuestJoin)

        public static void Tick()
        {
            float now = Now;
            TrackRun();
            TrackOwn(now);
            GuestTick(now);
        }

        // This PC's own prologue, online or not (a guest's copy of the host's is not its own): its true start time is
        // known whenever a session wants it. Only an online host tells anyone.
        static void TrackOwn(float now)
        {
            s_host.Tick(InPrologue && s_local == Local.None, AtTitle, Difficulty(), now);
            PrologueState state = s_host.State;
            bool changed = state.Phase != s_announced.Phase || state.Generation != s_announced.Generation;
            if (changed)
            {
                s_announced = state;
                Services.Log.Info("prologue: " + state.Phase + " (generation " + state.Generation + ")");
                CoopTrace.Event("story", "prologue.host", "phase", state.Phase, "generation", state.Generation);
                if (state.Phase == ProloguePhase.Playing) { HostStarts++; s_sentAnyInput = false; }
            }
            if (Services.Net.Enabled && Services.Net.IsHost) Publish(changed);
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
            Services.Net.Send(PrologueWire.Message, true, delegate(BinaryWriter w) { PrologueWire.WriteState(w, state); });
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
            i.Now = now;
            switch (s_guest.Tick(i))
            {
                case PrologueAction.Start: StartLocal(i.Host); break;
                case PrologueAction.Stop:
                    PutAway(!guestSession ? "the session ended" : !i.Waiting ? "the join ended"
                        : i.LocalEnded ? "this copy reached its end first" : "the host's prologue ended");
                    break;
                case PrologueAction.HostLeft:
                    PutAway("the host left");
                    GuestJoin.HostLeft();
                    break;
            }
            // Whatever the rules above saw: a copy never outlives the session it was started for.
            if (s_local == Local.Playing && !guestSession) PutAway("the session ended");
            if (s_local == Local.Playing && InPrologue) s_localSawPrologue = true;
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
            if (s_localEndRequested) return true;
            if (s_localSawPrologue) return !InPrologue;
            return now - s_startedAt > PrologueHost.StartGrace;
        }

        static void StartLocal(PrologueState host)
        {
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            s_inputs.Clear(); s_local = Local.Playing; s_localSawPrologue = s_localEndRequested = false; s_startedAt = Now; s_titleAt = -1f;
            LocalStarts++; LastStartDelay = host.Elapsed;
            Services.Log.Info("prologue: playing along with the host (generation " + host.Generation + ", difficulty " + (DifficultyMode)host.Difficulty
                + ", " + host.Elapsed.ToString("F2") + " s after its start)");
            CoopTrace.Event("story", "prologue.guest.start", "generation", host.Generation, "delay", host.Elapsed);
            try
            {
                DifficultyController.Instance.SetDifficulty((DifficultyMode)host.Difficulty);
                // The game's own new-game action, as SaveSlotsUI.SetDifficulty runs it; no slot is created or written
                // (a guest's saves are all suppressed, SaveSync).
                ui.EmptySaveSlotPressedAction.Perform(null);
                ui.Active = false;
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
                if (TestInput) ApplyTestInput();
                if (s_local == Local.Playing) { if (!s_mutateControl) ApplyHostInput(); }
                else if (HostAnnouncing && Services.Net.Connected) SendHostInput();
            }
        }

        static void ApplyTestInput()
        {
            Core.Input.Horizontal = TestHorizontal; Core.Input.HorizontalAnalogLeft = TestHorizontal;
            Core.Input.HorizontalDigiPad = TestHorizontal > 0.4f ? 1 : TestHorizontal < -0.4f ? -1 : 0;
            Core.Input.Jump.IsPressed = TestJump;
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
            PrologueInput input = s_inputs.Next(now);
            // This player's own pause menu is open: the gameplay input is neutral (the host's jump must not press a menu
            // item); the menu buttons below stay this player's own.
            bool menu = UI.MainMenuVisible;
            float h = menu ? 0f : PrologueButtons.Axis(input.Horizontal), v = menu ? 0f : PrologueButtons.Axis(input.Vertical);
            Core.Input.Horizontal = h; Core.Input.Vertical = v;
            Core.Input.HorizontalAnalogLeft = h; Core.Input.VerticalAnalogLeft = v;
            Core.Input.HorizontalAnalogRight = Core.Input.VerticalAnalogRight = 0f;
            Core.Input.HorizontalDigiPad = h > 0.4f ? 1 : h < -0.4f ? -1 : 0;
            Core.Input.VerticalDigiPad = v > 0.6f ? 1 : v < -0.6f ? -1 : 0;
            // PlayerInput just moved every button's IsPressed into WasPressed and read this PC's device: only the new
            // value is replaced, so the host's presses still give OnPressed edges here.
            for (int bit = 0; bit < PrologueButtons.Mirrored.Length; bit++)
                Core.Input.Buttons[PrologueButtons.Mirrored[bit]].IsPressed = !menu && (input.Buttons & (1 << bit)) != 0;
            InputsApplied++;
            PrologueInput newest; ICharacter character;
            if (now < s_snapAt || !s_inputs.TryNewest(out newest) || newest.Actor == PrologueActor.None || Actor(out character) != newest.Actor) return;
            Vector3 p = character.Position;
            if (!PrologueDrift.ShouldSnap(newest.X - p.x, newest.Y - p.y, s_inputs.HostResting(now), s_inputs.CaughtUp)) return;
            character.Position = new Vector3(newest.X, newest.Y, p.z);
            Snaps++; s_snapAt = now + 0.5f;
            CoopTrace.Event("story", "prologue.guest.snap", "actor", newest.Actor, "from", p.x.ToString("F2") + "," + p.y.ToString("F2"),
                "to", newest.X.ToString("F2") + "," + newest.Y.ToString("F2"));
        }

        // ---------------------------------------------------------------- guests cannot skip; their copy never becomes a game

        static bool GuestHolds { get { return s_local == Local.Playing && !s_mutateControl; } }

        [HarmonyPatch(typeof(SkipCutsceneController), "get_SkippingAvailable")]
        static class GuestSkipUnavailable
        {
            static void Postfix(ref bool __result) { if (GuestHolds) __result = false; }
        }

        [HarmonyPatch(typeof(SkipCutsceneController), "SkipCutscene")]
        static class GuestSkipRefused
        {
            static bool Prefix()
            {
                if (!GuestHolds) return true;
                SkipsRefused++;
                Services.Log.Info("prologue: skip refused on this PC (only P1 skips the prologue)");
                if (UI.MainMenuVisible) NetHud.Toast(L10n.T("prologue.skip_p1_only"));
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
