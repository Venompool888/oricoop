using System;
using System.Collections;
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
    // 50 Start, 51 End, 52 Skip. Object identity never depends on Unity instance IDs.
    public static class StorySync
    {
        const byte StartMessage = 50, EndMessage = 51, SkipMessage = 52;
        static readonly IntroRule Intro = new IntroRule();
        public static bool IntroActive { get { return Intro.Active; } }
        // Test-only mutation: the intro hold also takes the prologue's own character on a guest (the state before T-205).
        static readonly bool s_mutatePrologueHold = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_PROLOGUE_INTRO_HOLD") == "1";
        // T-205: the hold is for the players' own Oris in the game. The weak Ori of the prologue's last scene is the
        // story's character: on a guest that plays the host's prologue along it was taken for "this player's Ori",
        // pinned where it appeared and hidden (its input locked too), while the host walked its own.
        public static bool IntroHeld(int seat) { return Intro.HoldsSeat(seat) && !IntroExempt(seat); }
        // The hold never applies to this seat right now (GameIntroHost.SetIntroHeld asks too: the rule calls it directly).
        internal static bool IntroExempt(int seat) { return !s_mutatePrologueHold && seat == Players.LocalSeat && SharedPrologue.OwnsInput; }
        internal static void TickIntro() { Intro.Tick(StoryHost.Current); }
        internal static void ResetIntro() { Intro.Reset(StoryHost.Current); }
        internal static void IntroSeatJoined(int seat) { Intro.SeatJoined(seat); }
        internal static void IntroSeatLeft(int seat) { Intro.SeatLeft(seat); }
        internal static void ApplyIntro()
        {
            // Called after the usual parking/visual updates, so nothing re-shows an intro-held Ori.
            for (int seat = 1; seat <= 4; seat++) StoryHost.Current.SetIntroHeld(seat, IntroHeld(seat));
        }
        sealed class Story : StoryEntry
        {
            public byte Kind;
            public MoonGuid Scene, Id;
            public string Path = "", State = "";
            public Vector3 Position;
            public uint ControlFrame;
            public Vector3 ControlPosition;
            public float FreeSince = -1f, FreeLast = -1f;
            public Revive.Downed[] DownAtStart;
            public StoryRecovery Recovery;
            public ActionSequence Sequence;
            public CutsceneController Controller;
            public int LastTakeover = -1;   // T-196: index of the sequence's last takeover action (LastTakeoverIndex)
        }
        // T-196: the last action of a sequence that takes the player over (lock, letterbox, story text, ...; -1: none).
        // While a running sequence has not reached it, its story is not over (StoryLedger.LocalEnded).
        static int LastTakeoverIndex(ActionSequence sequence)
        {
            if (sequence == null || sequence.Actions == null) return -1;
            for (int i = sequence.Actions.Count - 1; i >= 0; i--)
                if (sequence.Actions[i] != null && IsTakeoverAction(sequence.Actions[i])) return i;
            return -1;
        }
        // Test-only mutation: a story ends at the first frame without a lock again (before T-196).
        static readonly bool MutateEndsOnGap = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_STORY_ENDS_ON_GAP") == "1";
        static readonly StoryLedger<Story> Ledger = new StoryLedger<Story>();
        static readonly Dictionary<string, float> Seen = Ledger.Seen;
        static readonly Dictionary<string, float> Performed = new Dictionary<string, float>();
        static readonly Dictionary<string, bool> Classification = new Dictionary<string, bool>();
        struct StartCall { public bool Root, LocalOnly; public Vector3 Position; }
        static float s_statsAt;
        public static int StartsSeen, StartsSent, StartsMirrored;
        static readonly Dictionary<ulong, Story> Local = Ledger.Local;
        static readonly Dictionary<ulong, Story> Remote = Ledger.Remote;
        static readonly Dictionary<ulong, Story> Accepted = Ledger.Accepted;
        static readonly Dictionary<Renderer, bool> Hidden = new Dictionary<Renderer, bool>();
        static readonly HashSet<Component> Mirrored = new HashSet<Component>();
        static readonly HashSet<CutsceneController> Starting = new HashSet<CutsceneController>();
        static readonly HashSet<ActionSequence> OwnedSequences = new HashSet<ActionSequence>();
        static readonly Dictionary<GameObject, HashSet<ActionSequence>> MirrorActivations = new Dictionary<GameObject, HashSet<ActionSequence>>();
        static readonly HashSet<ActionSequence> LocalOnly = new HashSet<ActionSequence>();
        static readonly HashSet<CutsceneController> LocalOnlyControllers = new HashSet<CutsceneController>();
        static readonly HashSet<ActionSequence> LocalWellSequences = new HashSet<ActionSequence>();
        static readonly SpiritWellActivations WellActivations = new SpiritWellActivations();
        static int s_localOnlyDepth, s_localOnlyRunDepth, s_localWellDepth;
        internal static bool LocalWellSave { get { return s_localWellDepth > 0; } }
        internal static void WriteWellActivations(BinaryWriter w) { WellActivations.Write(w); }
        internal static void MergeWellActivations(BinaryReader r) { WellActivations.Merge(r); }
        static string WellKey(MoonGuid scene, MoonGuid sequence) { return scene + ":" + sequence; }
        static int LiveRemotes() { return Ledger.LiveRemotes; }
        // T-175: a story of this PC is running, so it owns the takeover flags, not a mirror: a story it started
        // (Local) or a local-only native replay (pedestal / lever, CutsceneGuard).
        static bool OwnStoryRunning()
        {
            if (Local.Count > 0 || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0 || CutsceneGuard.PedestalWindow) return true;
            foreach (ActionSequence sequence in LocalOnly) if (sequence != null && sequence.IsRunning) return true;
            foreach (CutsceneController controller in LocalOnlyControllers) if (controller != null && controller.CurrentState != null) return true;
            return false;
        }
        // T-177: a story sequence or controller this PC runs (its own, a mirror, a local-only replay) is still going.
        // While it is, LockInputByAction may be that story's own "Lock player input" (CutsceneGuard, StoryLockRepair).
        public static bool StorySequenceRunning()
        {
            foreach (Story story in Local.Values)
                if (story.Sequence != null && story.Sequence.IsRunning || story.Controller != null && story.Controller.CurrentState != null) return true;
            foreach (ActionSequence sequence in OwnedSequences) if (sequence != null && sequence.IsRunning) return true;
            foreach (CutsceneController controller in OwnedControllers) if (controller != null && controller.CurrentState != null) return true;
            foreach (ActionSequence sequence in LocalOnly) if (sequence != null && sequence.IsRunning) return true;
            return false;
        }
        // Test only (NetKuroTest chain): mirror cleanups that found takeover state left over from an earlier cutscene.
        public static int LeftoverCleanups;
        public static bool StoryActive { get { return RealStories != 0; } }
        public static int RealStories { get { return Local.Count + Remote.Count; } }
        // T-183: a mirror's start of a sequence consumes the string trigger that would start it here (Story/MirrorStarts.cs).
        static readonly MirrorStarts MirrorStartsLog = new MirrorStarts();
        // Test-only mutation: the string trigger runs its sequence again on the mirror PC (before T-183).
        static readonly bool MutateMirrorTriggerRuns = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MIRROR_TRIGGER_RUNS") == "1";
        public static int MirrorTriggersSkipped { get { return MirrorStartsLog.Skipped; } }
        public static int OvertakenRetired;
        // T-183: a mirrored sequence still on its way to registering the string that starts `started` was overtaken by
        // the original (Story/MirrorStarts.Overtaken). Stop it; of its remaining actions only ActivateAction runs (world
        // objects end as in the original), not its waits, fades, registrations or scene loads.
        static void RetireOvertaken(ActionSequence started, bool handOff)
        {
            if (MutateMirrorTriggerRuns || started == null) return;
            List<string> strings = new List<string>();
            foreach (TriggerByString t in Resources.FindObjectsOfTypeAll<TriggerByString>())
                if (t != null && t.ActionToRun == started && t.Data != null && !string.IsNullOrEmpty(t.Data.String)) strings.Add(t.Data.String);
            if (strings.Count == 0) return;
            foreach (ActionSequence p in new List<ActionSequence>(OwnedSequences))
            {
                if (p == null || p == started || !p.IsRunning || p.Actions == null) continue;
                List<string> ahead = new List<string>();
                for (int i = Math.Max(0, p.Index); i < p.Actions.Count; i++)
                {
                    RegisterStringTriggerAction r = p.Actions[i] as RegisterStringTriggerAction;
                    if (r != null) ahead.Add(r.String);
                }
                if (!MirrorStarts.Overtaken(ahead, strings)) continue;
                int at = p.Index;
                if (handOff) InputLocks.HandOffManual(p, started);
                for (int i = at + 1; i < p.Actions.Count; i++)
                {
                    ActivateAction a = p.Actions[i] as ActivateAction;
                    if (a != null && a.Target != null) a.Perform(null);
                }
                p.Stop();
                OvertakenRetired++;
                Services.Log.Info("net: mirrored " + p.name + " overtaken at action " + at + " by " + started.name + "; retired");
                StoryHost.Current.Trace("story", "mirror.overtaken", "sequence", p.name, "index", at, "by", started.name);
            }
        }
        // T-185: no mirrored start while this PC's own scene trip is pending (Story/SceneTripRule.cs).
        // m_useAfterSceneLoad: set by GoToSceneController.GoToScene, cleared when the scenes are enabled and the
        // character has been put at the placeholder (CompleteGoingToAScene).
        static readonly FieldInfo s_tripFlag = typeof(GoToSceneController).GetField("m_useAfterSceneLoad", BindingFlags.Instance | BindingFlags.NonPublic);
        // Test-only mutation: mirror starts ignore the trip (before T-185).
        static readonly bool MutateStartInTrip = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_START_IN_SCENE_TRIP") == "1";
        public static int StartsHeldForTrip;
        static float s_tripAsked = -1f;   // when the game last asked for a scene trip (GoToScene)
        static bool s_resolveWaitsForTrip;
        public static bool SceneTripFlag
        {
            get { GoToSceneController go = GoToSceneController.Instance; return s_tripFlag != null && go != null && (bool)s_tripFlag.GetValue(go); }
        }
        // Every trip that sets the flag goes through this private overload; the wait is bounded from this moment.
        [HarmonyPatch(typeof(GoToSceneController), "GoToScene", new Type[] { typeof(MoonGuid), typeof(Vector3), typeof(string), typeof(Action), typeof(bool), typeof(bool) })]
        static class SceneTripAsked { static void Postfix() { s_tripAsked = Time.realtimeSinceStartup; SceneTripsAsked++; } }
        public static int SceneTripsAsked;   // tests: the hook really sees the game's trips
        static bool SceneTripHolds()
        {
            if (MutateStartInTrip || s_tripAsked < 0f) return false;
            return SceneTripRule.Holds(SceneTripFlag, Time.realtimeSinceStartup - s_tripAsked);
        }
        public static float LastStoryActivity = -1f;
        // Test only (NetCutsceneTest): start mirrors this late, like a slow resolve / scene load on a real link.
        public static float TestResolveDelay;
        public static void EnterLocalOnly(ActionSequence sequence)
        {
            s_localOnlyDepth++;
            if (sequence != null) LocalOnly.Add(sequence);
            LastStoryActivity = Time.realtimeSinceStartup;
        }
        public static void ExitLocalOnly() { s_localOnlyDepth--; }
        // T-172: is this save object inside a story this PC is only mirroring right now (the mirrored sequence or
        // controller, or a sequence the mirror chained into)? Its state belongs to the PC that runs the story.
        public static bool MirrorOwns(Component c)
        {
            if (c == null || Remote.Count == 0) return false;
            Transform t = c.transform;
            foreach (ActionSequence sequence in OwnedSequences) if (Below(t, sequence)) return true;
            foreach (CutsceneController controller in OwnedControllers) if (Below(t, controller)) return true;
            foreach (Component mirrored in Mirrored) if (Below(t, mirrored)) return true;
            return false;
        }
        static bool Below(Transform t, Component root)
        { return root != null && (t == root.transform || t.IsChildOf(root.transform)); }
        public static int TakeoverMaskPublic() { return TakeoverMask(); }
        static readonly HashSet<CutsceneController> OwnedControllers = new HashSet<CutsceneController>();
        static readonly SequenceInputLocks<ActionSequence, LockPlayerInputAction> InputLocks = new SequenceInputLocks<ActionSequence, LockPlayerInputAction>();
        static ActionSequence s_inputSequence;
        internal static void ForgetInputLocks() { InputLocks.Clear(); }
        static readonly Predicate<LockPlayerInputAction> ActiveTimer = action => action != null && action.gameObject.activeInHierarchy;
        internal static bool TimedInputRunning() { return InputLocks.TimerRunning(ActiveTimer); }
        static readonly HashSet<PauseGameAction> OwnedPauses = new HashSet<PauseGameAction>();
        static readonly Dictionary<ShowStoryTextAction, bool> StoryTexts = new Dictionary<ShowStoryTextAction, bool>();
        static readonly FieldInfo StoryMessageBox = AccessTools.Field(typeof(ShowStoryTextAction), "m_messageBox");
        sealed class NativeState
        {
            public SeinCharacter Ori;
            public GameController Game;
            public bool Lock, ActionLock, Blocked, Movement, Ignore, Animation, Active, CutsceneMoving, Gravity, CenterRay;
            public CameraPuppetController Camera;
            public Transform Cinematic;
            public float Tween, Horizontal, Vertical, Zoom;
            public bool ShowUI, Letterboxes;
            public bool OwnStory;   // T-175: a story of this PC was running when the mirror began (it owns the flags above)
            public Ori Spirit;
            public Ori.State SpiritState;
            public GameplayCamera GameplayCamera;
            public Transform Target;
        }
        static NativeState s_native;
        static bool s_mirroring;
        static int s_entryDepth;
        static int s_replayActionDepth;
        static int s_sourceActionDepth;
        static SeinCharacter s_hiddenOri;
        public static bool Watching
        {
            get
            {
                if (CutsceneGuard.PedestalWindow || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) return false;
                return Ledger.Watching;
            }
        }
        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected && Services.Session.Ready; } }
        static SeinCharacter Own { get { return StoryHost.Current.P1 != null ? StoryHost.Current.P1 : Characters.Sein; } }
        static bool PuppetCall { get { return StoryHost.Current.Inside || StoryHost.Current.IsPuppet(Characters.Sein); } }
        static bool NativeLocked()
        {
            return TakeoverMask() != 0;
        }
        static int TakeoverMask()
        {
            SeinCharacter own = Own; GameController gc = GameController.Instance; int mask = 0;
            if (gc != null) { if (gc.LockInput) mask |= 1; if (gc.LockInputByAction) mask |= 2; }
            if (own != null)
            {
                if (own.CutsceneBlocked != null && own.CutsceneBlocked.Active) mask |= 4;
                if (own.Controller != null && own.Controller.LockMovementInput) mask |= 8;
                if (own.Controller != null && own.Controller.IgnoreControllerInput) mask |= 16;
                if (own.CutsceneMovement != null && own.CutsceneMovement.Active) mask |= 32;
            }
            if (UI.Cameras.Current != null)
            {
                CameraPuppetController camera = CutsceneGuard.CameraPuppet();
                if (camera != null && camera.CinematicPuppet != null && camera.Tween > 0.01f) mask |= 64;
            }
            foreach (ShowStoryTextAction text in StoryTexts.Keys) if (text != null && text.IsPerforming) mask |= 128;
            return mask;
        }
        public static bool IsStorySequence(ActionSequence sequence)
        {
            if (sequence == null) return false;
            string key = SceneOf(sequence) + ":" + sequence.MoonGuid; bool story;
            bool baked = sequence.MoonGuid != null && sequence.MoonGuid != MoonGuid.Empty;
            if (baked && Classification.TryGetValue(key, out story)) return story;
            story = ContainsStory(sequence, new HashSet<Component>());
            if (baked) Classification[key] = story;
            return story;
        }
        public static bool IsStoryAction(Component c) { return ContainsStory(c, new HashSet<Component>()); }
        // T-040: a story that takes the player over (input lock, letterbox, story text, pause, cutscene state).
        // Camera-only moves (chase / escape camera setups) are not: those areas must never wait for everyone.
        static bool s_takeoverOnly;
        public static bool IsTakeoverAction(Component c)
        {
            s_takeoverOnly = true;
            try { return ContainsStory(c, new HashSet<Component>()); }
            finally { s_takeoverOnly = false; }
        }
        public static bool IsStoryCutscene(CutsceneController c) { return StoryController(c); }
        public static string ComponentKey(Component c) { return SceneOf(c) + ":" + PathOf(c.transform); }
        static bool BlocksSein(CutsceneState state)
        {
            return state is NormalBackwardsCutsceneState || state is MovingCutsceneState || state is PlayAnimationCutsceneState;
        }
        static bool StoryController(CutsceneController controller)
        {
            if (controller == null) return false;
            foreach (CutsceneState state in controller.GetComponentsInChildren<CutsceneState>(true))
                if (BlocksSein(state)) return true;
            return false;
        }
        static bool Cinematic(BaseAnimator animator)
        {
            if (animator == null) return false;
            if (animator is GameplayToCinematicAnimator || animator is MasterTimelineSequence) return true;
            return animator.GetComponentsInChildren<GameplayToCinematicAnimator>(true).Length > 0;
        }
        // Objective hint presentations may use the same input locks, camera and text as a cutscene.
        // Other narrative sequences named objectiveSetupAction (e.g. Naru's story) have a different owner.
        static bool LocalPresentation(ActionSequence sequence)
        {
            if (sequence.GetComponentInParent<MenuScreenManager>() != null || sequence.GetComponentInParent<MenuScreen>() != null
                || sequence.GetComponentInParent<CleverMenuItem>() != null) return true;
            for (Transform t = sequence.transform; t != null; t = t.parent)
                if (t.name == "*objectiveSetup") return true;
            if (sequence.Actions != null)
                foreach (ActionMethod action in sequence.Actions)
                    if (action is ReturnToTitleScreenAction && SceneOf(sequence) == MoonGuid.Empty) return true;
            return false;
        }
        static bool ContainsStory(Component component, HashSet<Component> visited)
        {
            if (component == null || !visited.Add(component)) return false;
            ActionSequence sequence = component as ActionSequence;
            if (sequence != null && LocalPresentation(sequence)) return false;
            if (component is StopActionAction || component is StopTimedActionSequenceAction) return false;
            if (component is LockPlayerInputAction || component is ShowStoryTextAction
                || component is RegisterSkipCutsceneAction || component is WatchCutsceneAction) return true;
            LockMovementAction movement = component as LockMovementAction;
            if (movement != null) return movement.ShouldLock;
            LockPlayerInputManualAction manual = component as LockPlayerInputManualAction;
            if (manual != null) return manual.ShouldLock;
            LetterboxAction letterbox = component as LetterboxAction;
            if (letterbox != null) return letterbox.ShowLetterboxes;
            ChangeCutsceneStateAction change = component as ChangeCutsceneStateAction;
            if (change != null) return BlocksSein(change.State) || StoryController(change.Cutscene);
            PauseGameAction pause = component as PauseGameAction;
            if (pause != null && (pause.SuspendPlayer || pause.ShowLetterbox)) return true;
            if (s_takeoverOnly) { }   // camera-only moves do not take the player over (T-040)
            else
            {
            SetCameraTargetAction target = component as SetCameraTargetAction;
            if (target != null && target.Target != null && target.Target.GetComponentInParent<SeinCharacter>() == null) return true;
            if (component is MoveCameraAction || component is MoveCameraHereAction) return true;
            BaseAnimatorAction animation = component as BaseAnimatorAction;
            if (animation != null)
            {
                if (animation.Animators != null) foreach (BaseAnimator animator in animation.Animators) if (Cinematic(animator)) return true;
                if (animation.Target != null)
                    foreach (BaseAnimator animator in animation.Target.GetComponentsInChildren<BaseAnimator>(true)) if (Cinematic(animator)) return true;
            }
            }
            // Follow action references, not arbitrary scene objects. This includes ActionSequence.Actions,
            // PerformActionAction.SequenceToRun and conditional/wrapper actions, with a cycle guard.
            foreach (FieldInfo field in component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (typeof(ActionMethod).IsAssignableFrom(field.FieldType))
                { if (ContainsStory(field.GetValue(component) as ActionMethod, visited)) return true; }
                else if (field.FieldType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                {
                    IEnumerable actions = field.GetValue(component) as IEnumerable;
                    if (actions != null) foreach (object action in actions)
                        if (ContainsStory(action as ActionMethod, visited)) return true;
                }
            }
            if (sequence != null && sequence.Actions == null)
                foreach (ActionMethod action in sequence.GetComponentsInChildren<ActionMethod>(true))
                    if (ContainsStory(action, visited)) return true;
            return false;
        }
        static void Consider(Story story, StartCall call)
        {
            // T-207 (2): a story this PC starts natively inside its lever / pedestal window (see PlayingLocally).
            if (CutsceneGuard.PedestalWindow) WindowStarts.Mark(KeyOf(story), Time.realtimeSinceStartup);
            if (LocalOnlyStory(story)) return;
            if (!Online || s_mirroring || PuppetCall || LiveRemotes() != 0 || StoryHost.Current.WorldApplying
                || (story.Sequence != null && s_sourceActionDepth > 0)
                || GameController.Instance == null || GameController.Instance.IsLoadingGame) return;
            story.Key = KeyOf(story); story.Position = call.Position;
            bool classified = story.Sequence != null ? IsStorySequence(story.Sequence) : StoryController(story.Controller);
            // A lock elsewhere in the world says nothing about this sequence. Only this action graph
            // or cutscene controller can establish a story start.
            if (classified) Begin(story);
        }
        public static void Init()
        {
            StoryHost.Current = new GameStoryHost();
            GameStoryHost.InitIntroNetwork();
            CutsceneGuard.Init();
            ForcedSync.Init();
            CutsceneAnimSync.Init();
            Services.Net.On(StartMessage, ReceiveStart);
            Services.Net.On(EndMessage, ReceiveEnd);
            Services.Net.On(SkipMessage, ReceiveSkip);
            Services.Net.SessionStarted += WellActivations.Clear;
            Services.Net.SessionEnded += WellActivations.Clear;
            Services.Net.PlayerLeft += RemoveSeat;
            s_statsAt = Time.realtimeSinceStartup + 30f;
        }
        public static void Reset()
        {
            CleanupNative();
            RestoreVisible(); Seen.Clear(); Performed.Clear(); Local.Clear(); Remote.Clear(); Accepted.Clear(); Mirrored.Clear(); Starting.Clear();
            StoryTexts.Clear(); ForgetInputLocks();
            MirrorStartsLog.Clear(); WindowStarts.Clear();
            LocalOnly.Clear(); LocalOnlyControllers.Clear(); LocalWellSequences.Clear();
            s_mirroring = false;
            // Tokens remain monotonic so a delayed end can never release a new story in this process.
        }
        static void CaptureNative()
        {
            if (s_native != null || Own == null || GameController.Instance == null) return;
            NativeState n = new NativeState(); n.Ori = Own; n.Game = GameController.Instance;
            n.OwnStory = OwnStoryRunning();
            n.Lock = n.Game.LockInput; n.ActionLock = n.Game.LockInputByAction; n.Active = n.Ori.Active;
            n.Blocked = n.Ori.CutsceneBlocked != null && n.Ori.CutsceneBlocked.Active;
            n.Movement = n.Ori.Controller.LockMovementInput; n.Ignore = n.Ori.Controller.IgnoreControllerInput;
            n.Animation = n.Ori.Controller.IsPlayingAnimation;
            n.CutsceneMoving = n.Ori.CutsceneMovement != null && n.Ori.CutsceneMovement.Active;
            n.Gravity = n.Ori.PlatformBehaviour.Gravity.Active;
            n.CenterRay = n.Ori.PlatformBehaviour.PlatformMovement.UseCenterRayForGroundAngle;
            n.ShowUI = Game.UI.SeinUI != null && Game.UI.SeinUI.ShowUI;
            n.Letterboxes = Letterbox.ShowLetterboxes;
            n.Spirit = Characters.Ori;
            if (n.Spirit != null) n.SpiritState = n.Spirit.CurrentState;
            n.GameplayCamera = UI.Cameras.Current;
            if (n.GameplayCamera != null) n.Target = n.GameplayCamera.Target;
            n.Camera = CutsceneGuard.CameraPuppet();
            if (n.Camera != null)
            {
                n.Cinematic = n.Camera.CinematicPuppet; n.Tween = n.Camera.Tween;
                n.Horizontal = n.Camera.WideScreenHorizontalPanStrength; n.Vertical = n.Camera.WideScreenVerticalPanStrength;
                n.Zoom = n.Camera.WideScreenZoomStrength;
            }
            s_native = n;
        }
        static void CleanupNative()
        {
            NativeState n = s_native; s_native = null;
            bool previous = s_mirroring; s_mirroring = true;
            bool ownNow = OwnStoryRunning();
            try
            {
                foreach (ActionSequence sequence in OwnedSequences) TryStop(sequence);
                // Stop only actions actually entered by our replay. Never issue an unmatched global
                // Resume: PauseGameAction owns its exclusion set and releases its own reference.
                foreach (PauseGameAction pause in OwnedPauses)
                    if (pause != null && pause.IsPerforming) TryStop(pause);
                List<ShowStoryTextAction> texts = new List<ShowStoryTextAction>();
                foreach (KeyValuePair<ShowStoryTextAction, bool> pair in StoryTexts) if (pair.Value) texts.Add(pair.Key);
                foreach (ShowStoryTextAction text in texts)
                {
                    if (text == null) { StoryTexts.Remove(text); continue; }
                    MessageBox box = StoryMessageBox.GetValue(text) as MessageBox;
                    try { if (box != null) box.HideMessageScreen(); }
                    catch (Exception e) { Services.Log.Warn("net: mirrored story text exit: " + e.Message); }
                    finally { StoryTexts.Remove(text); }
                }
                foreach (CutsceneController controller in OwnedControllers)
                {
                    if (controller == null) continue;
                    CutsceneState state = controller.CurrentState; controller.CurrentState = null;
                    try { if (state != null && n != null && n.Ori != null && n.Ori == Own) state.OnExit(); }
                    catch (Exception e) { Services.Log.Warn("net: mirrored state exit: " + e.Message); }
                    finally { if (controller != null) controller.CurrentState = null; }
                }
                if (n == null || n.Ori == null || n.Ori != Own || n.Game != GameController.Instance) return;
                // T-175: a flag captured when no story of ours was running is a leftover of the cutscene the mirror took
                // over, not the original: put back gameplay. A story of ours that started since owns the flags now.
                bool own = n.OwnStory;
                bool leftover = !own && !ownNow && (n.Letterboxes || n.Animation || n.Lock || n.ActionLock || n.Movement
                    || n.Ignore || n.Blocked || n.CutsceneMoving || !n.ShowUI);
                if (leftover) LeftoverCleanups++;
                if (n.OwnStory != ownNow || leftover)
                    CutsceneGuard.Log("story cleanup baseline ownAtStart=" + n.OwnStory + " ownNow=" + ownNow
                        + " letterbox=" + n.Letterboxes + " animation=" + n.Animation + " lock=" + n.Lock + "/" + n.ActionLock);
                if (n.Ori.CutsceneBlocked != null && MirrorBaseline.Restore(n.Blocked, own, ownNow) == false) n.Ori.CutsceneBlocked.Exit();
                if (n.Ori.CutsceneMovement != null && MirrorBaseline.Restore(n.CutsceneMoving, own, ownNow) == false) n.Ori.CutsceneMovement.Exit();
                n.Ori.PlatformBehaviour.Gravity.Active = n.Gravity;
                n.Ori.PlatformBehaviour.PlatformMovement.UseCenterRayForGroundAngle = n.CenterRay;
                // Only remove locks introduced since replay began. Preexisting native locks belong
                // to their original script, not this run. Menu/watch gates never write these flags.
                if (MirrorBaseline.Restore(n.Lock, own, ownNow) == false) n.Game.LockInput = false;
                if (MirrorBaseline.Restore(n.Movement, own, ownNow) == false) n.Ori.Controller.LockMovementInput = false;
                if (MirrorBaseline.Restore(n.Ignore, own, ownNow) == false) n.Ori.Controller.IgnoreControllerInput = false;
                if (MirrorBaseline.Restore(n.Animation, own, ownNow) == false) n.Ori.Controller.IsPlayingAnimation = false;
                if (n.Active && !n.Ori.Active) n.Ori.Active = true;
                n.Ori.Speed = Vector3.zero;
                if (!CutsceneGuard.Off)
                {
                    // Put back what the mirrored story changed (it may have been stopped half-way).
                    bool? hidden = MirrorBaseline.Restore(!n.ShowUI, own, ownNow);
                    if (Game.UI.SeinUI != null && hidden.HasValue && Game.UI.SeinUI.ShowUI != !hidden.Value)
                    { Game.UI.SeinUI.ShowUI = !hidden.Value; CutsceneGuard.Log("story restored ShowUI=" + !hidden.Value); }
                    bool? letterbox = MirrorBaseline.Restore(n.Letterboxes, own, ownNow);
                    if (letterbox.HasValue && Letterbox.ShowLetterboxes != letterbox.Value)
                    { Letterbox.ShowLetterboxes = letterbox.Value; CutsceneGuard.Log("story restored letterbox=" + letterbox.Value); }
                    if (n.GameplayCamera != null && n.GameplayCamera.Target != n.Target)
                    { n.GameplayCamera.Target = n.Target; CutsceneGuard.Log("story restored camera target"); }
                    if (n.Spirit != null && n.SpiritState == Ori.State.Hovering && Characters.Ori != null && Characters.Ori.CurrentState != Ori.State.Hovering)
                    { Characters.Ori.BackToPlayerController(); CutsceneGuard.Log("story restored spirit Hovering"); }
                }
                if (n.Camera != null)
                {
                    if (n.Camera.CinematicPuppet != n.Cinematic || n.Camera.Tween != n.Tween)
                        CutsceneGuard.Log("story restored camera puppet");
                    n.Camera.CinematicPuppet = n.Cinematic; n.Camera.Tween = n.Tween;
                    n.Camera.WideScreenHorizontalPanStrength = n.Horizontal;
                    n.Camera.WideScreenVerticalPanStrength = n.Vertical; n.Camera.WideScreenZoomStrength = n.Zoom;
                }
            }
            catch (Exception e) { Services.Log.Warn("net: mirrored story cleanup: " + e); }
            finally
            {
                RestoreMirrorActivation(null);
                OwnedSequences.Clear(); OwnedControllers.Clear(); OwnedPauses.Clear();
                s_mirroring = previous; StoryHost.Current.PositionChanged();
            }
        }
        static void TryStop(PerformingAction action)
        {
            if (action == null) return;
            try { action.Stop(); }
            catch (Exception e) { Services.Log.Warn("net: stopping mirrored " + action.GetType().Name + ": " + e.Message); }
        }
        static MoonGuid SceneOf(Component component)
        {
            for (Transform t = component.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null && root.MetaData != null) return root.MetaData.SceneMoonGuid;
            }
            return MoonGuid.Empty;
        }
        static string PathOf(Transform transform)
        {
            // Include names and sibling ordinals: some scenes contain identically named controllers.
            List<string> parts = new List<string>();
            while (transform != null)
            {
                int ordinal = 0;
                if (transform.parent != null)
                    for (int i = 0; i < transform.parent.childCount; i++)
                    { Transform sibling = transform.parent.GetChild(i); if (sibling == transform) break; if (sibling.name == transform.name) ordinal++; }
                parts.Add(transform.name.Length + ":" + transform.name + "[" + ordinal + "]");
                transform = transform.parent;
            }
            parts.Reverse(); return string.Join("/", parts.ToArray());
        }
        static string KeyOf(Story story)
        { return story.Kind + ":" + story.Scene + ":" + story.Id + ":" + story.Path + ":" + story.State; }
        static void Begin(Story story)
        {
            if (LocalOnlyStory(story)) return;
            if (!Online || s_mirroring || PuppetCall || Own == null || StoryHost.Current.WorldApplying
                || GameController.Instance == null || GameController.Instance.IsLoadingGame) return;
            // Mirrored timelines continue invoking nested actions after Perform has returned. Retain
            // the receive ownership until End rather than clearing the echo guard after one stack.
            if (LiveRemotes() != 0) return;   // a mirror that is only finishing (EndAt set) does not block
            story.Key = KeyOf(story);
            if (!Ledger.PrepareLocal(story, StoryHost.Current.LocalSeat, Services.Net.IsHost, Time.realtimeSinceStartup)) return;
            if (story.Classified) story.DownAtStart = Revive.LocalStoryDowns();
            LastStoryActivity = Time.realtimeSinceStartup;
            story.SawLock = NativeLocked(); Ledger.TrackLocal(story, Services.Net.IsHost);
            story.LastTakeover = LastTakeoverIndex(story.Sequence);
            Services.Net.Send(StartMessage, true, delegate(BinaryWriter w) {
                StoryWire.WriteStart(w, new StoryStart { Token = story.Token, Kind = story.Kind,
                    Scene = story.Scene.ToByteArray(), Id = story.Id.ToByteArray(), Path = story.Path, State = story.State,
                    X = story.Position.x, Y = story.Position.y, Z = story.Position.z,
                    Classified = story.Classified });
            });
            StartsSent++; story.StartSent = true;
            Services.Log.Info("net: story start " + story.Token + " " + story.Key);
        }
        static void ReceiveStart(BinaryReader r)
        {
            StoryStart data = StoryWire.ReadStart(r);
            Story story = new Story(); story.Sender = Services.Net.Sender; story.Token = data.Token; story.Kind = data.Kind;
            story.Scene = GameStoryHost.ReadGuid(data.Scene); story.Id = GameStoryHost.ReadGuid(data.Id);
            story.Path = data.Path; story.State = data.State; story.Position = new Vector3(data.X, data.Y, data.Z);
            story.Classified = data.Classified;
            if (!Online) return;
            // Earlier net12 builds announced the first well visit as a story. The pedestal and teleporter
            // already synchronize through their native save objects, including when their scene is unloaded.
            if (data.WellActivation) { WellActivations.TryActivate(WellKey(story.Scene, story.Id)); return; }
            if (!story.Classified || Remote.ContainsKey(TokenKey(story.Sender, story.Token)) || PlayingLocally(story)) return;
            if (CutsceneGuard.PedestalWindow) MirroredInWindow++;
            LastStoryActivity = Time.realtimeSinceStartup;
            story.Key = KeyOf(story);
            if (!Ledger.CanReceive(story, Services.Net.IsHost, Time.realtimeSinceStartup)) return;
            if (!Services.Net.IsHost)
            {
                List<ulong> superseded = new List<ulong>();
                foreach (Story local in Local.Values) if (local.Key == story.Key) superseded.Add(TokenKey(local.Sender, local.Token));
                foreach (ulong token in superseded) SendEnd(token);
            }
            Ledger.TrackRemote(story, Services.Net.IsHost, Time.realtimeSinceStartup);
            StoryHost.Current.Rebroadcast(r);
            story.DownAtStart = Revive.LocalStoryDowns();
            // TestResolveDelay (tests): the mirror cannot start before the delay, as during a scene load.
            if (!story.Resolved && TestResolveDelay <= 0f) Resolve(story);
        }
        static bool LocalOnlyStory(Story story)
        {
            if (CutsceneGuard.PedestalWindow || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) return true;
            return LocalOnlyScoped(story);
        }
        // The story's own sequence / controller was started by this PC's lever or pedestal replay.
        static bool LocalOnlyScoped(Story story)
        {
            if (story.Kind == 0) return LocalOnly.Contains(story.Sequence ?? FindSequence(story));
            if (story.Controller != null) return LocalOnlyControllers.Contains(story.Controller);
            foreach (CutsceneController controller in LocalOnlyControllers)
                if (controller != null && SceneOf(controller) == story.Scene && PathOf(controller.transform) == story.Path) return true;
            return false;
        }
        // T-207 (2): a PARTNER's story reaches this PC. A lever push opens a 12 s window and an ability pedestal a 20 s
        // one on every PC that plays it ("the cutscene runs natively here, do not also mirror it"). The window used to
        // drop every story start that arrived inside it, also a partner's unrelated one (a map stone, a story trigger
        // elsewhere): it was never played here and the two PCs' story state could differ from then on.
        // A partner never announces the stories of its own replay (its window keeps them unsent), so a start that
        // arrives is not one of those. It is left out only when this PC plays that very story itself: it was started
        // by this PC's replay (LocalOnlyScoped, or inside the replay's call), or natively here inside the window
        // within the last WindowDedupe seconds (both PCs' windows did not close at the same moment).
        // Test-only mutation ORICOOP_MUTATE_WINDOW_DROPS_STARTS: the window drops them all again.
        static readonly bool MutateWindowDropsStarts = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_WINDOW_DROPS_STARTS") == "1";
        static readonly RecentObjects<string> WindowStarts = new RecentObjects<string>();
        const float WindowDedupe = 5f;
        public static int MirroredInWindow;
        static bool PlayingLocally(Story story)
        {
            bool scoped = MutateWindowDropsStarts && CutsceneGuard.PedestalWindow
                || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0 || LocalOnlyScoped(story);
            bool startedHere = !MutateWindowDropsStarts && WindowStarts.Recent(KeyOf(story), Time.realtimeSinceStartup, WindowDedupe);
            return !StoryLedger<Story>.MirrorInsideWindow(scoped, startedHere);
        }
        static ActionSequence FindSequence(Story story)
        {
            if (story.Id == MoonGuid.Empty) return null;
            // Use the native scene save registry first, including inactive scene objects.
            foreach (SceneManagerScene scene in Core.Scenes.Manager.ActiveScenes)
                if (scene.SceneRoot != null && scene.MetaData.SceneMoonGuid == story.Scene && scene.SceneRoot.SaveSceneManager != null)
                {
                    ActionSequence sequence = scene.SceneRoot.SaveSceneManager.IdToSaveSerialize(story.Id) as ActionSequence;
                    if (sequence != null) return sequence;
                }
            // Master/persistent sequences and the harmless S4 fixture do not belong to a SceneRoot.
            foreach (ActionSequence sequence in Resources.FindObjectsOfTypeAll<ActionSequence>())
                if (sequence != null && sequence.MoonGuid == story.Id && SceneOf(sequence) == story.Scene
                    && (sequence.gameObject.activeInHierarchy || SceneOf(sequence) != MoonGuid.Empty)) return sequence;
            return null;
        }
        static void ActivateMirrorSequence(ActionSequence sequence)
        {
            ActivateMirrorPath(sequence, sequence.transform);
            if (sequence.Actions != null)
                foreach (ActionMethod action in sequence.Actions)
                    if (action != null) ActivateMirrorPath(sequence, action.transform);
        }
        static void ActivateMirrorPath(ActionSequence sequence, Transform t)
        {
            for (; t != null && t.GetComponent<SceneRoot>() == null; t = t.parent)
            {
                GameObject go = t.gameObject;
                HashSet<ActionSequence> users;
                if (!MirrorActivations.TryGetValue(go, out users))
                {
                    if (go.activeSelf) continue;
                    users = new HashSet<ActionSequence>(); MirrorActivations.Add(go, users);
                }
                users.Add(sequence);
                if (!go.activeSelf) go.SetActive(true);
            }
        }
        static void RestoreMirrorActivation(ActionSequence sequence)
        {
            List<GameObject> restore = null;
            foreach (KeyValuePair<GameObject, HashSet<ActionSequence>> pair in MirrorActivations)
            {
                if (!ReferenceEquals(sequence, null) && (!pair.Value.Remove(sequence) || pair.Value.Count != 0)) continue;
                if (restore == null) restore = new List<GameObject>();
                restore.Add(pair.Key);
            }
            if (restore == null) return;
            foreach (GameObject go in restore)
            {
                MirrorActivations.Remove(go);
                if (go != null) go.SetActive(false);
            }
        }
        static void GatherForStory(Story story)
        {
            SeinCharacter own = Own;
            // A downed Ori watches the mirror at the skull and joins the survivors once it ends.
            if (own != null && !Revive.IsDowned(own) && Vector3.Distance(own.Position, story.Position) > 12f)
            {
                StoryHost.Current.Teleport(own, story.Position + Vector3.right);
                CutsceneGuard.SnapSpirit("story teleport");
                StoryHost.Current.PositionChanged();
            }
        }
        static bool Resolve(Story story)
        {
            if (PlayingLocally(story)) { story.Resolved = true; story.Watch = false; return true; }
            if (Own == null || GameController.Instance == null || GameController.Instance.IsLoadingGame) return false;
            if (SceneTripHolds())
            {
                s_resolveWaitsForTrip = true;
                if (!story.TripWait)
                {
                    story.TripWait = true; StartsHeldForTrip++;
                    Services.Log.Info("net: story " + story.Token + " waits for this PC's scene trip " + story.Key);
                    StoryHost.Current.Trace("story", "mirror.trip.wait", "token", story.Token, "key", story.Key);
                }
                return false;
            }
            bool previous = s_mirroring; s_mirroring = true;
            try
            {
                float performed;
                bool alreadyPerformed = Performed.TryGetValue(story.Key, out performed) && Time.realtimeSinceStartup - performed < 3f;
                if (story.Kind == 0)
                {
                    story.Sequence = FindSequence(story);
                    if (story.Sequence == null) return false;
                    // An older peer may have classified its pause or objective presentation as a cutscene.
                    if (!IsStorySequence(story.Sequence)) { story.Resolved = true; story.Sequence = null; return true; }
                    if (LocalOnly.Contains(story.Sequence))
                    {
                        // Already played here by our own pedestal replay (CutsceneGuard): track only.
                        story.Resolved = true; story.Sequence = null;
                        Services.Log.Info("net: story " + story.Token + " already playing locally (pedestal) " + story.Key);
                        return true;
                    }
                    // Use the same player-takeover criterion as an all-player story gate. A camera-only
                    // sequence can still mirror, but leaves the partner's Ori where it was.
                    if (IsTakeoverAction(story.Sequence)) GatherForStory(story);
                    CaptureNative(); OwnedSequences.Add(story.Sequence);
                    Mirrored.Add(story.Sequence);
                    bool running = story.Sequence.IsRunning;
                    if (!alreadyPerformed || running) ActivateMirrorSequence(story.Sequence);
                    MirrorStartsLog.MirrorPerformed(story.Sequence.GetInstanceID(), Time.realtimeSinceStartup);   // T-183
                    RetireOvertaken(story.Sequence, running || !alreadyPerformed && story.Sequence.enabled);
                    ForcedSync.StoryStarted();
                    if (!alreadyPerformed) story.Sequence.Perform(null);
                }
                else
                {
                    foreach (CutsceneController controller in Resources.FindObjectsOfTypeAll<CutsceneController>())
                    {
                        if (controller == null || SceneOf(controller) != story.Scene || PathOf(controller.transform) != story.Path) continue;
                        foreach (CutsceneState state in controller.GetComponentsInChildren<CutsceneState>(true))
                        {
                            if (PathOf(state.transform) != story.State) continue;
                            if (BlocksSein(state)) GatherForStory(story);
                            story.Controller = controller; CaptureNative(); OwnedControllers.Add(controller);
                            Mirrored.Add(controller);
                            ForcedSync.StoryStarted();
                            if (!alreadyPerformed) controller.ChangeState(state);
                            break;
                        }
                        if (story.Controller != null) break;
                    }
                    if (story.Controller == null) return false;
                }
                story.Resolved = true; story.Started = true;
                StartsMirrored++;
                if (story.EndedEarly)
                {
                    // Over for its sender already: it runs as a finishing mirror (until its script stops, 10 s at most).
                    story.EndAt = Time.realtimeSinceStartup; LateStarts++;
                    StoryHost.Current.Trace("story", "mirror.late-start", "token", story.Token);
                }
                Services.Log.Info("net: story mirrored " + story.Token + " " + story.Key);
                StoryHost.Current.Trace("story", "mirror.start", "token", story.Token, "key", story.Key);
                if (story.Skipped) SkipRemote(story);
                return true;
            }
            catch (Exception e)
            {
                CleanupNative();
                story.Watch = true; story.Resolved = true; story.Started = true;   // half-started: not retried (review 4)
                Services.Log.Warn("net: story mirror failed; watching " + story.Key + ": " + e);
                return true;
            }
            finally { s_mirroring = previous; }
        }
        // Test-only mutation (before T-212 item 4): a "local only" window swallows the end of a story already announced,
        // on the sender and on a receiver.
        static readonly bool MutateEndLostInWindow = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_END_LOST_IN_WINDOW") == "1";
        static void SendEnd(ulong token, bool completed = false)
        {
            Story story;
            if (Local.TryGetValue(token, out story)
                && (MutateEndLostInWindow ? !LocalOnlyStory(story) : StoryLedger<Story>.EndOwed(story, LocalOnlyStory(story))))
            {
                StoryRecovery recovery = completed && story.Classified ? Revive.StorySurvivors() : null;
                Services.Net.Send(EndMessage, true, delegate(BinaryWriter w) {
                    StoryWire.WriteToken(w, (uint)token);
                    if (recovery != null) recovery.Write(w);
                });
                Revive.FinishStory(story.DownAtStart, recovery);
            }
            Ledger.EndLocal(token);
            LastStoryActivity = Time.realtimeSinceStartup;
            Services.Log.Info("net: story end sent " + (uint)token);
        }
        static ulong TokenKey(int sender, uint token) { return StoryLedger<Story>.TokenKey(sender, token); }
        static void ReceiveEnd(BinaryReader r)
        {
            ulong token = TokenKey(Services.Net.Sender, StoryWire.ReadToken(r)); Story story;
            // T-212 (4): a story this PC tracks ends when its sender says so, whatever window is open here now (the
            // "already playing locally" entries of a pedestal replay used to sit in Remote for 60 s for this reason).
            if (!(Services.Net.IsHost ? Accepted : Remote).TryGetValue(token, out story) || MutateEndLostInWindow && LocalOnlyStory(story)) return;
            story.Recovery = StoryRecovery.Read(r);
            StoryHost.Current.Rebroadcast(r);
            Accepted.Remove(token);
            EndRemote(token, false, true);
        }
        static void RemoveSeat(int seat)
        {
            List<ulong> tokens = Ledger.RemoteSeatTokens(seat);
            foreach (ulong token in tokens) EndRemote(token, true);
            tokens = Ledger.AcceptedSeatTokens(seat);
            foreach (ulong token in tokens) Accepted.Remove(token);
        }
        // T-212 (3): a mirrored story ends while other mirrors still run (three players: two partners each started a
        // story, one of them dropped). The full cleanup only happens when the last mirror ends, so the ended story's
        // script kept running here: its story text and input lock stayed until the other story was over. Its own
        // script is stopped now: the sequence and everything below it (sub-sequences, input locks, pauses, texts).
        // The flags it set (letterbox, camera) are put back by the full cleanup as before.
        // Test-only mutation ORICOOP_MUTATE_KEEP_DROPPED_MIRROR: it keeps running again.
        static readonly bool MutateKeepDroppedMirror = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_KEEP_DROPPED_MIRROR") == "1";
        public static int StoppedOwn;
        static void StopOwn(Story story)
        {
            bool previous = s_mirroring; s_mirroring = true;
            try
            {
                if (story.Sequence != null)
                {
                    foreach (ActionSequence sequence in new List<ActionSequence>(OwnedSequences))
                        if (sequence != null && Below(sequence.transform, story.Sequence)) { TryStop(sequence); OwnedSequences.Remove(sequence); }
                    foreach (PauseGameAction pause in new List<PauseGameAction>(OwnedPauses))
                        if (pause != null && Below(pause.transform, story.Sequence)) { if (pause.IsPerforming) TryStop(pause); OwnedPauses.Remove(pause); }
                    foreach (ShowStoryTextAction text in new List<ShowStoryTextAction>(StoryTexts.Keys))
                    {
                        if (text == null || !StoryTexts[text] || !Below(text.transform, story.Sequence)) continue;
                        MessageBox box = StoryMessageBox.GetValue(text) as MessageBox;
                        try { if (box != null) box.HideMessageScreen(); }
                        catch (Exception e) { Services.Log.Warn("net: mirrored story text exit: " + e.Message); }
                        finally { StoryTexts.Remove(text); }
                    }
                }
                if (story.Controller != null && OwnedControllers.Remove(story.Controller))
                {
                    CutsceneState state = story.Controller.CurrentState; story.Controller.CurrentState = null;
                    try { if (state != null) state.OnExit(); }
                    catch (Exception e) { Services.Log.Warn("net: mirrored state exit: " + e.Message); }
                    finally { story.Controller.CurrentState = null; }
                }
                StoppedOwn++;
                StoryHost.Current.Trace("story", "mirror.stopped-own", "token", story.Token, "remaining", Remote.Count);
            }
            catch (Exception e) { Services.Log.Warn("net: stopping one mirrored story: " + e); }
            finally { s_mirroring = previous; }
        }
        // Test only (NetStoryLeftoverTest, T-212 item 3): a mirrored story from a seat that is not in this 2-player
        // session, and that seat leaving.
        internal static void TestInjectRemote(int seat, uint token, ActionSequence sequence)
        {
            if (!TestHarness.Enabled || sequence == null) return;
            Story story = new Story { Sender = seat, Token = token, Kind = 0, Scene = SceneOf(sequence), Id = new MoonGuid(sequence.MoonGuid),
                Position = Own == null ? Vector3.zero : Own.Position, Classified = true };
            story.Key = KeyOf(story);
            Ledger.TrackRemote(story, false, Time.realtimeSinceStartup);
            Resolve(story);
        }
        internal static void TestSeatLeft(int seat) { if (TestHarness.Enabled) RemoveSeat(seat); }
        // Test-only mutation (before T-212 item 5): an end that arrives before the mirror started drops the story.
        static readonly bool MutateNoLateStart = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_LATE_START") == "1";
        public static int LateStarts;
        static void EndRemote(ulong token, bool force = false, bool completed = false)
        {
            Story story;
            if (!Remote.TryGetValue(token, out story)) return;
            // T-212 (5): its mirror has not started here yet (scene trip, scene still loading): keep it for a late start.
            if (!force && !MutateNoLateStart && story.Kind == 0 && !Revive.IsDowned(Own)
                && StoryLedger<Story>.KeepForLateStart(story, Time.realtimeSinceStartup))
            {
                if (!story.EndedEarly)
                {
                    // Review finding 4: given up on after 2 s ("watching"): try again until the 8 s are over.
                    if (story.Watch) { story.Watch = false; story.Resolved = false; story.NextResolve = Time.realtimeSinceStartup; }
                    story.EndedEarly = true;
                    Services.Log.Info("net: story end " + story.Token + " arrived before its mirror started; kept for a late start");
                    StoryHost.Current.Trace("story", "mirror.ended-early", "token", story.Token);
                }
                return;
            }
            if (!force && !CutsceneGuard.Off && story.Sequence != null && story.Sequence.IsRunning && !story.Watch)
            {
                // The partner's copy ended, ours is still running (it started later). Stopping it half-way left the
                // screen black (FadeToBlack without FadeFromBlack) or the letterbox down (playtest 19:07, lever).
                // Let it finish on its own; Tick force-ends it when it stops, or after 10 s.
                if (StoryLedger<Story>.DeferEnd(story, Time.realtimeSinceStartup))
                {
                    Services.Log.Info("net: story end " + story.Token + " deferred: mirror still running");
                    StoryHost.Current.Trace("story", "mirror.finishing", "token", story.Token);
                }
                return;
            }
            if (story.Sequence != null) Mirrored.Remove(story.Sequence);
            if (story.Controller != null) Mirrored.Remove(story.Controller);
            Remote.Remove(token);
            LastStoryActivity = Time.realtimeSinceStartup;
            Services.Log.Info("net: story end " + story.Token + " remaining=" + Remote.Count);
            if (StoryLedger<Story>.Cleanup(Remote.Count) == StoryCleanup.All) CleanupNative();
            else if (!MutateKeepDroppedMirror) StopOwn(story);
            if (!Watching) RestoreVisible();
            if (completed) Revive.FinishStory(story.DownAtStart, story.Recovery);
        }
        static void ReceiveSkip(BinaryReader r)
        {
            ulong token = TokenKey(Services.Net.Sender, StoryWire.ReadToken(r)); Story story;
            if (!(Services.Net.IsHost ? Accepted : Remote).TryGetValue(token, out story) || PlayingLocally(story)) return;
            StoryHost.Current.Rebroadcast(r);
            if (!Remote.ContainsKey(token)) return;
            Ledger.Skip(story); if (story.Resolved && !story.Watch) SkipRemote(story);
        }
        static void SkipRemote(Story story)
        {
            if (PlayingLocally(story) || story.SkipApplied || SkipCutsceneController.Instance == null || !SkipCutsceneController.Instance.SkippingAvailable) return;
            // Only the matching mirrored run is eligible. A fallback watch never skips unrelated story.
            if (!StoryLedger<Story>.CanSkip(story, story.Sequence != null || story.Controller != null)) return;
            bool previous = s_mirroring; s_mirroring = true;
            try { SkipCutsceneController.Instance.SkipCutscene(); story.SkipApplied = true; }
            finally { s_mirroring = previous; }
        }
        static readonly Action s_cutsceneTick = CutsceneGuard.Tick;
        static readonly Action s_gateTick = ForcedSync.Tick;
        static readonly Action s_respawnTick = CutsceneRespawn.Tick;

        public static void Tick()
        {
            TickIntro();
            SubsystemGuard.Run("CutsceneGuard.Tick", s_cutsceneTick);
            SubsystemGuard.Run("ForcedSync.Tick", s_gateTick);
            SubsystemGuard.Run("CutsceneAnimSync.Tick", CutsceneAnimSync.Tick);
            SubsystemGuard.Run("CutsceneRespawn.Tick", s_respawnTick);
            if (Time.realtimeSinceStartup >= s_statsAt)
            {
                s_statsAt = Time.realtimeSinceStartup + 30f;
                if (Services.Net.Enabled) Services.Log.Info("net: story classification startsSeen=" + StartsSeen + " sent=" + StartsSent
                    + " mirrored=" + StartsMirrored + " cachedIds=" + Classification.Count + " watching=" + Watching);
            }
            if (!Online) { if (StoryActive) Reset(); return; }
            float now = Time.realtimeSinceStartup; bool locked = NativeLocked();
            List<ulong> expiredAccepted = new List<ulong>();
            foreach (KeyValuePair<ulong, Story> pair in Accepted) if (now - pair.Value.Began >= 60f) expiredAccepted.Add(pair.Key);
            foreach (ulong token in expiredAccepted) Accepted.Remove(token);
            if (Remote.Count > 0)
            {
                List<ulong> released = new List<ulong>();
                foreach (KeyValuePair<ulong, Story> pair in Remote)
                {
                    Story story = pair.Value;
                    if (story.DownAtStart == null && story.FreeSince >= 0 && now - story.FreeSince >= 2f && now - story.FreeLast <= 0.25f)
                    { released.Add(pair.Key); story.FreeSince = -1f; }
                }
                foreach (ulong token in released) EndRemote(token);
            }
            List<ulong> ended = new List<ulong>();
            foreach (Story story in Local.Values)
            {
                if (locked) story.SawLock = true;
                bool running = story.Sequence != null && story.Sequence.IsRunning;
                bool ahead = !MutateEndsOnGap && StoryLedger<Story>.TakeoverAhead(running, running ? story.Sequence.Index : 0, story.LastTakeover);
                // The normal unlock can precede the last native action. An end carrying a rescue must
                // wait for that tail, including when the downed seat is local to the story starter.
                if (StoryLedger<Story>.LocalEnded(story, locked, running, ahead || Revive.AnyDowned, now)) ended.Add(TokenKey(story.Sender, story.Token));
            }
            foreach (ulong token in ended) SendEnd(token, now - Local[token].Began < 60f);
            ended.Clear();
            List<ulong> finished = new List<ulong>();
            foreach (Story story in Remote.Values)
            {
                bool running = !(now - story.Began >= 60f) && story.EndAt >= 0
                    && story.Sequence != null && story.Sequence.IsRunning;
                if (StoryLedger<Story>.RemoteFinished(story, running, now))
                { finished.Add(TokenKey(story.Sender, story.Token)); continue; }
                // T-212 (5): ended by its sender before it started here, and settled without a mirror of its own
                // (already playing locally, or the mirror failed): nothing left to wait for.
                if (story.EndedEarly && story.Resolved && story.EndAt < 0)
                { finished.Add(TokenKey(story.Sender, story.Token)); continue; }
                if (story.Resolved)
                {
                    if (story.Skipped && !story.Watch) SkipRemote(story);
                    continue;
                }
                // T-185: while it waits for this PC's scene trip the 2 s "unavailable" clock does not run (a trip that
                // reloads the scene takes about 3 s; "watching" hid Ori at the placeholder). The clock starts again
                // once, when the wait ends.
                if (story.TripWait)
                {
                    if (SceneTripHolds()) continue;
                    story.TripWait = false; story.Began = now; story.NextResolve = now;
                }
                StoryResolution resolution = StoryLedger<Story>.Resolution(story, now, CutsceneGuard.Off);
                if (resolution == StoryResolution.Drop)
                {
                    // Not a real story on the sender (e.g. a plant's projectile sequence that started while a lock
                    // began): never hide and freeze our own player for it (playtest 14:28 "我这边怎么站着不动了").
                    Services.Log.Info("net: unclassified story unavailable; dropped " + story.Key);
                    StoryHost.Current.Trace("story", "unavailable.dropped", "token", story.Token, "key", story.Key);
                    ended.Add(TokenKey(story.Sender, story.Token)); continue;
                }
                if (resolution == StoryResolution.Watch)
                {
                    story.Resolved = true; story.Watch = true;
                    Services.Log.Warn("net: story unavailable after 2s; watching " + story.Key);
                    StoryHost.Current.Trace("story", "unavailable", "token", story.Token, "key", story.Key); continue;
                }
                if (StoryLedger<Story>.ResolveDue(story, now, TestResolveDelay))
                {
                    StoryLedger<Story>.ScheduleResolve(story, now);
                    s_resolveWaitsForTrip = false;
                    // Waiting for the trip: no extra scene loads, the trip is loading this PC's scenes itself (T-185).
                    if (!Resolve(story) && !s_resolveWaitsForTrip && Core.Scenes.Manager != null && StoryLedger<Story>.SceneLoadDue(story, now))
                    {
                        StoryHost.Current.Trace("story", "scene.load", "key", story.Key, "n", story.SceneLoads);
                        Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(story.Position.x - 16, story.Position.y - 16, 32, 32), true, false, false);
                    }
                }
            }
            foreach (ulong token in ended) EndRemote(token, true);   // dropped: never kept for a late start
            foreach (ulong token in finished)
            {
                Story story = Remote[token];
                bool completed = story.Recovery != null && now - story.Began < 60f
                    && (story.Sequence == null || !story.Sequence.IsRunning);
                EndRemote(token, true, completed);
            }
            if (Watching) HideOwn(); else RestoreVisible();
            if (Seen.Count > 512)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, float> pair in Seen) if (now - pair.Value > 3f) expired.Add(pair.Key);
                foreach (string key in expired) Seen.Remove(key);
            }
            if (Performed.Count > 512)
            {
                List<string> expired = new List<string>();
                foreach (KeyValuePair<string, float> pair in Performed) if (now - pair.Value > 3f) expired.Add(pair.Key);
                foreach (string key in expired) Performed.Remove(key);
            }
        }
        static void HideOwn()
        {
            SeinCharacter own = Own; if (own == null) return;
            if (s_hiddenOri != own) { RestoreVisible(); s_hiddenOri = own; }
            foreach (Renderer renderer in own.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                if (!Hidden.ContainsKey(renderer)) Hidden.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
            own.Speed = Vector3.zero;
        }
        static void RestoreVisible()
        {
            foreach (KeyValuePair<Renderer, bool> pair in Hidden) if (pair.Key != null) pair.Key.enabled = pair.Value;
            Hidden.Clear(); s_hiddenOri = null;
        }
        [HarmonyPatch(typeof(SavePedestal), "SaveOnPedestal")]
        static class WellSave
        {
            static void Prefix(SavePedestal __instance, out bool __state)
            {
                __state = Online && !PuppetCall;
                if (!__state) return;
                ActionSequence opening = __instance.OnOpenedAction as ActionSequence;
                if (opening != null && opening.MoonGuid != null && opening.MoonGuid != MoonGuid.Empty)
                    WellActivations.TryActivate(WellKey(SceneOf(opening), opening.MoonGuid));   // net12 snapshot for older guests
                EnterLocalOnly(opening);
                s_localWellDepth++;
            }
            static void Finalizer(bool __state)
            {
                if (__state) { s_localWellDepth--; ExitLocalOnly(); }
            }
        }
        [HarmonyPatch(typeof(ActionSequence), "Perform")]
        static class SequenceStart
        {
            static bool Prefix(ActionSequence __instance, out StartCall __state)
            {
                __state = new StartCall();
                bool replay = s_mirroring || s_replayActionDepth > 0 || OwnedSequences.Contains(__instance);
                bool canStart = __instance.enabled && __instance.MoonGuid != null && __instance.MoonGuid != MoonGuid.Empty;
                if (!replay && (s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0)) LocalOnly.Add(__instance);
                if (!replay && LocalWellSave) LocalWellSequences.Add(__instance);
                else LocalWellSequences.Remove(__instance);
                __state.Root = s_entryDepth == 0 && canStart;
                if (__state.Root) { s_entryDepth++; __state.Position = Own == null ? Vector3.zero : Own.Position; }
                if (replay && s_native != null && Remote.Count > 0 && !PuppetCall)
                {
                    OwnedSequences.Add(__instance);
                    if (!Mirrored.Contains(__instance)) ActivateMirrorSequence(__instance);
                }
                return true;
            }
            static void Postfix(ActionSequence __instance, StartCall __state)
            {
                if (Services.Net.Enabled && !s_mirroring && !PuppetCall) StartsSeen++;
                if (OwnedSequences.Contains(__instance))
                {
                    if (!__instance.IsRunning) RestoreMirrorActivation(__instance);
                    if (__instance.MoonGuid != null && __instance.MoonGuid != MoonGuid.Empty)
                        Performed[KeyOf(new Story { Kind = 0, Scene = SceneOf(__instance), Id = __instance.MoonGuid })] = Time.realtimeSinceStartup;
                }
                if (!__state.Root || !__instance.enabled || __instance.MoonGuid == null || __instance.MoonGuid == MoonGuid.Empty || Mirrored.Contains(__instance)) return;
                Consider(new Story { Kind = 0, Scene = SceneOf(__instance), Id = new MoonGuid(__instance.MoonGuid), Sequence = __instance }, __state);
            }
            static Exception Finalizer(StartCall __state, Exception __exception)
            {
                if (__state.Root) s_entryDepth--;
                return __exception;
            }
        }
        [HarmonyPatch(typeof(ActionSequence), "RunAction")]
        static class ReplayAction
        {
            struct ActionCall { public int Flags; public ActionSequence Previous; }
            static void Prefix(ActionSequence __instance, out ActionCall __state)
            {
                __state = new ActionCall { Previous = s_inputSequence };
                s_inputSequence = __instance;
                if (LocalOnly.Contains(__instance)) { __state.Flags |= 4; s_localOnlyRunDepth++; }
                if (LocalWellSequences.Contains(__instance)) { __state.Flags |= 8; s_localWellDepth++; }
                if (OwnedSequences.Contains(__instance)) { __state.Flags |= 1; s_replayActionDepth++; }
                foreach (Story story in Local.Values)
                    if (story.Sequence == __instance) { __state.Flags |= 2; s_sourceActionDepth++; break; }
            }
            static Exception Finalizer(ActionCall __state, Exception __exception)
            {
                s_inputSequence = __state.Previous;
                if ((__state.Flags & 1) != 0) s_replayActionDepth--;
                if ((__state.Flags & 2) != 0) s_sourceActionDepth--;
                if ((__state.Flags & 4) != 0) s_localOnlyRunDepth--;
                if ((__state.Flags & 8) != 0) s_localWellDepth--;
                return __exception;
            }
        }
        [HarmonyPatch(typeof(CutsceneController), "Start")]
        static class AutomaticState
        {
            static void Prefix(CutsceneController __instance) { Starting.Add(__instance); s_entryDepth++; }
            static Exception Finalizer(CutsceneController __instance, Exception __exception)
            { Starting.Remove(__instance); s_entryDepth--; return __exception; }
        }
        [HarmonyPatch(typeof(CutsceneController), "ChangeState")]
        static class StateStart
        {
            static bool Prefix(CutsceneController __instance, CutsceneState state, out StartCall __state)
            {
                __state = new StartCall();
                if (s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) LocalOnlyControllers.Add(__instance);
                __state.LocalOnly = LocalOnlyControllers.Contains(__instance);
                if (__state.LocalOnly) s_localOnlyRunDepth++;
                bool replay = s_mirroring || s_replayActionDepth > 0 || OwnedControllers.Contains(__instance);
                __state.Root = s_entryDepth == 0;
                if (__state.Root) { s_entryDepth++; __state.Position = Own == null ? Vector3.zero : Own.Position; }
                if (replay && s_native != null && Remote.Count > 0 && !PuppetCall) OwnedControllers.Add(__instance);
                return true;
            }
            static void Postfix(CutsceneController __instance, CutsceneState state, StartCall __state)
            {
                if (state != null && OwnedControllers.Contains(__instance)) Performed[KeyOf(new Story { Kind = 1,
                    Scene = SceneOf(__instance), Id = MoonGuid.Empty, Path = PathOf(__instance.transform), State = PathOf(state.transform) })] = Time.realtimeSinceStartup;
                if (!__state.Root || state == null || Starting.Contains(__instance) || Mirrored.Contains(__instance)) return;
                Consider(new Story { Kind = 1, Scene = SceneOf(__instance), Id = MoonGuid.Empty,
                    Path = PathOf(__instance.transform), State = PathOf(state.transform), Controller = __instance }, __state);
            }
            static Exception Finalizer(StartCall __state, Exception __exception)
            {
                if (__state.Root) s_entryDepth--;
                if (__state.LocalOnly) s_localOnlyRunDepth--;
                return __exception;
            }
        }
        [HarmonyPatch(typeof(ShowStoryTextAction), "Perform")]
        static class TrackStoryText
        {
            static void Postfix(ShowStoryTextAction __instance)
            {
                if (!Online || PuppetCall || !__instance.IsPerforming) return;
                StoryTexts[__instance] = s_native != null && (s_mirroring || s_replayActionDepth > 0);
            }
        }
        [HarmonyPatch(typeof(ShowStoryTextAction), "OnMessageScreenHide")]
        static class StoryTextEnded
        {
            static void Prefix(ShowStoryTextAction __instance) { StoryTexts.Remove(__instance); }
        }
        // Inspect accepted, newest owner frames. Puppet movement and interpolated/missing frames
        // cannot prove that the partner has regained control; the neutral flag (4) comes from its owner.
        [HarmonyPatch(typeof(NetPlay), "Receive")]
        static class PartnerControl
        {
            static void Postfix(NetPlay.Frame f)
            {
                if (!StoryHost.Current.NewestRemoteFrame(Services.Net.Sender, f.Tick)) return;
                float now = Time.realtimeSinceStartup;
                bool moving = f.Vel.sqrMagnitude > 0.04f || Math.Abs((int)f.LX) > 12 || Math.Abs((int)f.LY) > 12;
                foreach (Story story in Remote.Values)
                {
                    if (story.Sender != Services.Net.Sender || f.Tick <= story.ControlFrame) continue;
                    float distance = Vector3.Distance(f.Pos, story.ControlPosition);
                    bool free = (f.Flags & 4) == 0 && moving && distance < 4f;
                    if (!free) story.FreeSince = -1f;
                    else if (story.FreeSince < 0 || now - story.FreeLast > 0.25f) story.FreeSince = now;
                    story.FreeLast = free ? now : -1f; story.ControlPosition = f.Pos; story.ControlFrame = f.Tick;
                }
            }
        }
        static void ApplyInputLock(bool? locked)
        {
            if (locked.HasValue && GameController.Instance != null) GameController.Instance.LockInputByAction = locked.Value;
        }
        static readonly Action<LockPlayerInputAction> StopInputTimer = delegate(LockPlayerInputAction action)
        { if (action != null) action.StopCoroutine("PerformActionCoroutine"); };

        static void EndSequenceInput(ActionSequence sequence, bool finished)
        {
            if (!InputLocks.Any) return;
            ActionSequence next = null;
            bool sceneTrip = false;
            if (finished && sequence.Actions != null && sequence.Actions.Count > 0)
            {
                // A sequence can intentionally hand a manual lock to its next script, or across a scene trip.
                ActionMethod last = sequence.Actions[sequence.Actions.Count - 1];
                RunActionOnce run = last as RunActionOnce;
                if (run != null) next = run.Action as ActionSequence;
                if (next == sequence || next != null && !next.IsRunning) next = null;
                sceneTrip = last is LoadSceneAction;
            }
            ApplyInputLock(InputLocks.End(sequence, next, sceneTrip, StopInputTimer));
        }
        static void FinishSequence(ActionSequence sequence, bool finished)
        {
            EndSequenceInput(sequence, finished);
            RestoreMirrorActivation(sequence);
        }
        [HarmonyPatch(typeof(ActionSequence), "UpdateActions")]
        static class SequenceFinished
        {
            static void Prefix(ActionSequence __instance, out bool __state) { __state = __instance.IsRunning; }
            static void Postfix(ActionSequence __instance, bool __state)
            { if (__state && !__instance.IsRunning) FinishSequence(__instance, true); }
        }
        [HarmonyPatch(typeof(ActionSequence), "Stop")]
        static class SequenceStopped
        {
            static void Postfix(ActionSequence __instance) { FinishSequence(__instance, false); }
        }
        [HarmonyPatch(typeof(LockPlayerInputManualAction), "Perform")]
        static class TrackManualInput
        {
            static void Prefix(LockPlayerInputManualAction __instance)
            {
                if (Online && !PuppetCall && __instance.ShouldLock && s_inputSequence != null)
                    InputLocks.LockManual(s_inputSequence, GameController.Instance.LockInputByAction);
            }
            static void Postfix(LockPlayerInputManualAction __instance)
            {
                if (Online && !PuppetCall && !__instance.ShouldLock) ApplyInputLock(InputLocks.UnlockManual(s_inputSequence));
            }
        }
        [HarmonyPatch(typeof(LockPlayerInputAction), "PerformActionCoroutine")]
        static class TrackTimedInput
        {
            static void Postfix(LockPlayerInputAction __instance, ref IEnumerator __result)
            {
                if (Online && !PuppetCall) __result = Observe(__instance, s_inputSequence, __result);
            }
            static IEnumerator Observe(LockPlayerInputAction action, ActionSequence sequence, IEnumerator routine)
            {
                InputLocks.LockTimed(sequence, action, GameController.Instance.LockInputByAction);
                while (routine.MoveNext()) yield return routine.Current;
                ApplyInputLock(InputLocks.EndTimed(action));
            }
        }
        [HarmonyPatch(typeof(LockPlayerInputAction), "Stop")]
        static class TimedInputStopped
        {
            static void Postfix(LockPlayerInputAction __instance)
            { if (Online && !PuppetCall) ApplyInputLock(InputLocks.EndTimed(__instance)); }
        }
        [HarmonyPatch(typeof(GameController), "ResetInputLocks")]
        static class NativeInputReset { static void Postfix() { ForgetInputLocks(); } }
        [HarmonyPatch(typeof(PauseGameAction), "Perform")]
        static class TrackPause
        {
            static void Prefix(PauseGameAction __instance)
            { if (s_native != null && (s_mirroring || s_replayActionDepth > 0) && !PuppetCall && !__instance.IsPerforming) OwnedPauses.Add(__instance); }
        }
        // T-183: TriggerByString.DoTrigger() deregisters its string and runs its action. On a PC that already started the
        // target sequence as a mirror, the firing is consumed instead (Story/MirrorStarts.cs): no second 03 / 04.
        [HarmonyPatch(typeof(TriggerByString), "DoTrigger", new Type[0])]
        static class MirrorConsumesStringTrigger
        {
            static bool Prefix(TriggerByString __instance)
            {
                if (!Services.Net.Enabled || __instance == null || __instance.Data == null || MutateMirrorTriggerRuns) return true;
                ActionSequence target = __instance.ActionToRun as ActionSequence;
                if (target == null || !MirrorStartsLog.SkipTrigger(target.GetInstanceID(), Time.realtimeSinceStartup)) return true;
                TriggerByString.Deregister(__instance.Data.String);
                Services.Log.Info("net: string trigger " + __instance.Data.String + " consumed by the mirror's start of " + target.name);
                StoryHost.Current.Trace("story", "mirror.trigger.consumed", "string", __instance.Data.String, "sequence", target.name);
                return false;
            }
        }
        // T-183: ActionSequence.Serialize (no ActionSequenceSerializer) reads nothing (ActionMethod.Serialize is empty) and
        // only calls Stop(). SceneRoot.LateStart loads a scene's saved data whenever the checkpoint already has that scene,
        // which WorldSync's partner blobs make true for a scene this PC has never visited (StoreCheckpoint). So a sequence
        // started right after the scene loaded (a mirror's Resolve, or this PC's own chain) was stopped one frame later
        // (runs K/L: "actionSequence index=3 stopped from ActionSequence.Serialize < SaveSceneManager.Load <
        // SceneRoot.LateStart"; run I-3: the host's own world-map sequence ended at index 0 and 04 never started).
        // Online, such a read leaves a running sequence alone. A real checkpoint restore still stops it
        // (ActionSequence.OnRestoreCheckpoint, a separate path). Test-only mutation ORICOOP_MUTATE_SERIALIZE_STOPS.
        static readonly bool MutateSerializeStops = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_SERIALIZE_STOPS") == "1";
        public static int SerializeStopsKept;
        [HarmonyPatch(typeof(ActionSequence), "Serialize")]
        static class KeepRunningOnSceneLoad
        {
            static bool Prefix(ActionSequence __instance, Archive ar)
            {
                if (MutateSerializeStops || !Services.Net.Enabled || __instance == null || ar == null || !ar.Reading || !__instance.IsRunning) return true;
                if (__instance.GetComponent<ActionSequenceSerializer>() != null) return true;
                SerializeStopsKept++;
                StoryHost.Current.Trace("story", "sequence.kept", "id", __instance.name, "index", __instance.Index);
                return false;
            }
        }
        // T-183: who stopped a running story sequence (ActionSequence.Stop, also called by Serialize when reading and
        // by a checkpoint restore). Run I-3: the host's own world-map sequence ended at index 0 one frame after it started.
        [HarmonyPatch(typeof(ActionSequence), "Stop")]
        static class TraceStop
        {
            static void Prefix(ActionSequence __instance)
            {
                if (__instance == null || !__instance.IsRunning || !Services.Net.Enabled) return;
                try
                {
                    System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(2, false);
                    string from = "";
                    for (int i = 0; i < st.FrameCount && i < 5; i++)
                    {
                        System.Reflection.MethodBase m = st.GetFrame(i).GetMethod();
                        if (m != null) from += (from.Length > 0 ? " < " : "") + (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
                    }
                    StoryHost.Current.Trace("story", "sequence.stopped", "id", __instance.name, "index", __instance.Index, "from", from);
                }
                catch { }
            }
        }
        [HarmonyPatch(typeof(CutsceneTrigger), "OnTriggerEnter")]
        static class PuppetTrigger
        {
            static bool Prefix(Collider collider)
            {
                return !Services.Net.Enabled || (collider != null && !PuppetCall && !StoryHost.Current.IsPuppetComponent(collider));
            }
        }
        [HarmonyPatch(typeof(SkipCutsceneController), "SkipCutscene")]
        static class Skip
        {
            static void Prefix()
            {
                if (!Online || s_mirroring || PuppetCall || CutsceneGuard.PedestalWindow
                    || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) return;
                foreach (Story story in Local.Values)
                {
                    if (LocalOnlyStory(story)) continue;
                    uint token = story.Token;
                    Services.Net.Send(SkipMessage, true, delegate(BinaryWriter w) { StoryWire.WriteToken(w, token); });
                }
            }
        }
    }
}
