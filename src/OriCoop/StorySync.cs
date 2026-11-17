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
        sealed class Story : StoryEntry
        {
            public byte Kind;
            public MoonGuid Scene, Id;
            public string Path = "", State = "";
            public Vector3 Position;
            public uint ControlFrame;
            public Vector3 ControlPosition;
            public float FreeSince = -1f, FreeLast = -1f;
            public ActionSequence Sequence;
            public CutsceneController Controller;
        }
        static readonly StoryLedger<Story> Ledger = new StoryLedger<Story>();
        static readonly Dictionary<string, float> Seen = Ledger.Seen;
        static readonly Dictionary<string, float> Performed = new Dictionary<string, float>();
        static readonly Dictionary<string, bool> Classification = new Dictionary<string, bool>();
        sealed class Candidate { public Story Story; public int Before; public float At; }
        struct StartCall { public bool Root, LocalOnly; public int Before; public Vector3 Position; }
        static readonly Dictionary<string, Candidate> Pending = new Dictionary<string, Candidate>();
        static float s_statsAt;
        public static int StartsSeen, StartsSent, StartsMirrored;
        static readonly Dictionary<ulong, Story> Local = Ledger.Local;
        static readonly Dictionary<ulong, Story> Remote = Ledger.Remote;
        static readonly Dictionary<ulong, Story> Accepted = Ledger.Accepted;
        static readonly Dictionary<Renderer, bool> Hidden = new Dictionary<Renderer, bool>();
        static readonly HashSet<Component> Mirrored = new HashSet<Component>();
        static readonly HashSet<CutsceneController> Starting = new HashSet<CutsceneController>();
        static readonly HashSet<ActionSequence> OwnedSequences = new HashSet<ActionSequence>();
        static readonly HashSet<ActionSequence> LocalOnly = new HashSet<ActionSequence>();
        static readonly HashSet<CutsceneController> LocalOnlyControllers = new HashSet<CutsceneController>();
        static int s_localOnlyDepth, s_localOnlyRunDepth;
        static int LiveRemotes() { return Ledger.LiveRemotes; }
        public static bool StoryActive { get { return Local.Count + Remote.Count + Pending.Count > 0; } }
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
        public static int TakeoverMaskPublic() { return TakeoverMask(); }
        static readonly HashSet<CutsceneController> OwnedControllers = new HashSet<CutsceneController>();
        static readonly HashSet<LockPlayerInputAction> OwnedInputActions = new HashSet<LockPlayerInputAction>();
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
        static bool ContainsStory(Component component, HashSet<Component> visited)
        {
            if (component == null || !visited.Add(component)) return false;
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
            ActionSequence sequence = component as ActionSequence;
            if (sequence != null && sequence.Actions == null)
                foreach (ActionMethod action in sequence.GetComponentsInChildren<ActionMethod>(true))
                    if (ContainsStory(action, visited)) return true;
            return false;
        }
        static void Consider(Story story, StartCall call)
        {
            if (s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0 || story.Sequence != null && LocalOnly.Contains(story.Sequence)
                || story.Controller != null && LocalOnlyControllers.Contains(story.Controller) || CutsceneGuard.PedestalWindow) return;
            if (!Online || s_mirroring || PuppetCall || LiveRemotes() != 0 || StoryHost.Current.WorldApplying
                || (story.Sequence != null && s_sourceActionDepth > 0)
                || GameController.Instance == null || GameController.Instance.IsLoadingGame) return;
            story.Key = KeyOf(story); story.Position = call.Position;
            bool classified = story.Sequence != null ? IsStorySequence(story.Sequence) : StoryController(story.Controller);
            story.Classified = classified;
            if (classified || (TakeoverMask() & ~call.Before) != 0) { Begin(story); return; }
            if (!Pending.ContainsKey(story.Key)) Pending[story.Key] = new Candidate { Story = story, Before = call.Before, At = Time.realtimeSinceStartup };
        }
        static void ObserveCandidates(float now)
        {
            List<string> complete = new List<string>(); int mask = TakeoverMask();
            foreach (KeyValuePair<string, Candidate> pair in Pending)
            {
                Candidate candidate = pair.Value;
                if (now - candidate.At > 0.5f) complete.Add(pair.Key);
                else if ((mask & ~candidate.Before) != 0) { Begin(candidate.Story); complete.Add(pair.Key); }
            }
            foreach (string key in complete) Pending.Remove(key);
        }
        public static void Init()
        {
            StoryHost.Current = new GameStoryHost();
            CutsceneGuard.Init();
            Services.Net.On(StartMessage, ReceiveStart);
            Services.Net.On(EndMessage, ReceiveEnd);
            Services.Net.On(SkipMessage, ReceiveSkip);
            Services.Net.PlayerLeft += RemoveSeat;
            s_statsAt = Time.realtimeSinceStartup + 30f;
        }
        public static void Reset()
        {
            CleanupNative();
            RestoreVisible(); Seen.Clear(); Performed.Clear(); Local.Clear(); Remote.Clear(); Accepted.Clear(); Mirrored.Clear(); Starting.Clear();
            Pending.Clear();
            StoryTexts.Clear();
            LocalOnly.Clear(); LocalOnlyControllers.Clear();
            s_mirroring = false;
            // Tokens remain monotonic so a delayed end can never release a new story in this process.
        }
        static void CaptureNative()
        {
            if (s_native != null || Own == null || GameController.Instance == null) return;
            NativeState n = new NativeState(); n.Ori = Own; n.Game = GameController.Instance;
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
            bool keepLock = n != null && n.Game != null && n.Lock && n.Game.LockInput;
            bool keepActionLock = n != null && n.Game != null && n.ActionLock && n.Game.LockInputByAction;
            try
            {
                foreach (ActionSequence sequence in OwnedSequences) TryStop(sequence);
                // Stop only actions actually entered by our replay. Never issue an unmatched global
                // Resume: PauseGameAction owns its exclusion set and releases its own reference.
                foreach (PauseGameAction pause in OwnedPauses)
                    if (pause != null && pause.IsPerforming) TryStop(pause);
                foreach (LockPlayerInputAction action in OwnedInputActions) TryStop(action);
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
                if (n.Ori.CutsceneBlocked != null && !n.Blocked) n.Ori.CutsceneBlocked.Exit();
                if (n.Ori.CutsceneMovement != null && !n.CutsceneMoving) n.Ori.CutsceneMovement.Exit();
                n.Ori.PlatformBehaviour.Gravity.Active = n.Gravity;
                n.Ori.PlatformBehaviour.PlatformMovement.UseCenterRayForGroundAngle = n.CenterRay;
                // Only remove locks introduced since replay began. Preexisting native locks belong
                // to their original script, not this run. Menu/watch gates never write these flags.
                n.Game.LockInput = keepLock;
                n.Game.LockInputByAction = keepActionLock;
                if (!n.Movement) n.Ori.Controller.LockMovementInput = false;
                if (!n.Ignore) n.Ori.Controller.IgnoreControllerInput = false;
                if (!n.Animation) n.Ori.Controller.IsPlayingAnimation = false;
                if (n.Active && !n.Ori.Active) n.Ori.Active = true;
                n.Ori.Speed = Vector3.zero;
                if (!CutsceneGuard.Off)
                {
                    // Put back what the mirrored story changed (it may have been stopped half-way).
                    if (Game.UI.SeinUI != null && Game.UI.SeinUI.ShowUI != n.ShowUI)
                    { Game.UI.SeinUI.ShowUI = n.ShowUI; CutsceneGuard.Log("story restored ShowUI=" + n.ShowUI); }
                    if (Letterbox.ShowLetterboxes != n.Letterboxes)
                    { Letterbox.ShowLetterboxes = n.Letterboxes; CutsceneGuard.Log("story restored letterbox=" + n.Letterboxes); }
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
                OwnedSequences.Clear(); OwnedControllers.Clear(); OwnedInputActions.Clear(); OwnedPauses.Clear();
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
            if (s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0 || story.Sequence != null && LocalOnly.Contains(story.Sequence)
                || story.Controller != null && LocalOnlyControllers.Contains(story.Controller) || CutsceneGuard.PedestalWindow) return;
            if (!Online || s_mirroring || PuppetCall || Own == null || StoryHost.Current.WorldApplying
                || GameController.Instance == null || GameController.Instance.IsLoadingGame) return;
            // Mirrored timelines continue invoking nested actions after Perform has returned. Retain
            // the receive ownership until End rather than clearing the echo guard after one stack.
            if (LiveRemotes() != 0) return;   // a mirror that is only finishing (EndAt set) does not block
            story.Key = KeyOf(story);
            if (!Ledger.PrepareLocal(story, StoryHost.Current.LocalSeat, Services.Net.IsHost, Time.realtimeSinceStartup)) return;
            LastStoryActivity = Time.realtimeSinceStartup;
            story.SawLock = NativeLocked(); Ledger.TrackLocal(story, Services.Net.IsHost);
            Services.Net.Send(StartMessage, true, delegate(BinaryWriter w) {
                StoryWire.WriteStart(w, new StoryStart { Token = story.Token, Kind = story.Kind,
                    Scene = story.Scene.ToByteArray(), Id = story.Id.ToByteArray(), Path = story.Path, State = story.State,
                    X = story.Position.x, Y = story.Position.y, Z = story.Position.z, Classified = story.Classified });
            });
            StartsSent++;
            Services.Log.Info("net: story start " + story.Token + " " + story.Key);
        }
        static void ReceiveStart(BinaryReader r)
        {
            StoryStart data = StoryWire.ReadStart(r);
            Story story = new Story(); story.Sender = Services.Net.Sender; story.Token = data.Token; story.Kind = data.Kind;
            story.Scene = GameStoryHost.ReadGuid(data.Scene); story.Id = GameStoryHost.ReadGuid(data.Id);
            story.Path = data.Path; story.State = data.State; story.Position = new Vector3(data.X, data.Y, data.Z);
            story.Classified = data.Classified;
            if (!Online || Remote.ContainsKey(TokenKey(story.Sender, story.Token)) || LocalOnlyStory(story)) return;
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
            SeinCharacter own = Own;
            if (own != null && Vector3.Distance(own.Position, story.Position) > 12f)
            {
                StoryHost.Current.Teleport(own, story.Position + Vector3.right);
                CutsceneGuard.SnapSpirit("story teleport");
                StoryHost.Current.PositionChanged();
            }
            if (!story.Resolved) Resolve(story);
        }
        static bool LocalOnlyStory(Story story)
        {
            if (CutsceneGuard.PedestalWindow || s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) return true;
            if (story.Kind == 0) return LocalOnly.Contains(story.Sequence ?? FindSequence(story));
            foreach (CutsceneController controller in LocalOnlyControllers)
                if (controller != null && SceneOf(controller) == story.Scene && PathOf(controller.transform) == story.Path) return true;
            return false;
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
        static bool Resolve(Story story)
        {
            if (LocalOnlyStory(story)) { story.Resolved = true; story.Watch = false; return true; }
            if (Own == null || GameController.Instance == null || GameController.Instance.IsLoadingGame) return false;
            bool previous = s_mirroring; s_mirroring = true;
            try
            {
                float performed;
                bool alreadyPerformed = Performed.TryGetValue(story.Key, out performed) && Time.realtimeSinceStartup - performed < 3f;
                if (story.Kind == 0)
                {
                    story.Sequence = FindSequence(story);
                    if (story.Sequence == null) return false;
                    if (LocalOnly.Contains(story.Sequence))
                    {
                        // Already played here by our own pedestal replay (CutsceneGuard): track only.
                        story.Resolved = true; story.Sequence = null;
                        Services.Log.Info("net: story " + story.Token + " already playing locally (pedestal) " + story.Key);
                        return true;
                    }
                    if (!story.Sequence.gameObject.activeInHierarchy)
                    {
                        // ActionSequence advances in FixedUpdate: an inactive copy would stall at its first wait
                        // (no subtitles, orb never returned). The host's copy was active when it ran.
                        for (Transform t = story.Sequence.transform; t != null && t.GetComponent<SceneRoot>() == null; t = t.parent)
                            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                        Services.Log.Info("net: story " + story.Token + " activated inactive sequence " + story.Sequence.name);
                    }
                    CaptureNative(); OwnedSequences.Add(story.Sequence);
                    Mirrored.Add(story.Sequence); if (!alreadyPerformed) story.Sequence.Perform(null);
                }
                else
                {
                    foreach (CutsceneController controller in Resources.FindObjectsOfTypeAll<CutsceneController>())
                    {
                        if (controller == null || SceneOf(controller) != story.Scene || PathOf(controller.transform) != story.Path) continue;
                        foreach (CutsceneState state in controller.GetComponentsInChildren<CutsceneState>(true))
                        {
                            if (PathOf(state.transform) != story.State) continue;
                            story.Controller = controller; CaptureNative(); OwnedControllers.Add(controller);
                            Mirrored.Add(controller);
                            if (!alreadyPerformed) controller.ChangeState(state);
                            break;
                        }
                        if (story.Controller != null) break;
                    }
                    if (story.Controller == null) return false;
                }
                story.Resolved = true;
                StartsMirrored++;
                Services.Log.Info("net: story mirrored " + story.Token + " " + story.Key);
                StoryHost.Current.Trace("story", "mirror.start", "token", story.Token, "key", story.Key);
                if (story.Skipped) SkipRemote(story);
                return true;
            }
            catch (Exception e)
            {
                CleanupNative();
                story.Watch = true; story.Resolved = true;
                Services.Log.Warn("net: story mirror failed; watching " + story.Key + ": " + e);
                return true;
            }
            finally { s_mirroring = previous; }
        }
        static void SendEnd(ulong token)
        {
            Story story;
            if (Local.TryGetValue(token, out story) && !LocalOnlyStory(story))
                Services.Net.Send(EndMessage, true, delegate(BinaryWriter w) { StoryWire.WriteToken(w, (uint)token); });
            Ledger.EndLocal(token);
            LastStoryActivity = Time.realtimeSinceStartup;
            Services.Log.Info("net: story end sent " + (uint)token);
        }
        static ulong TokenKey(int sender, uint token) { return StoryLedger<Story>.TokenKey(sender, token); }
        static void ReceiveEnd(BinaryReader r)
        {
            ulong token = TokenKey(Services.Net.Sender, StoryWire.ReadToken(r)); Story story;
            if (!(Services.Net.IsHost ? Accepted : Remote).TryGetValue(token, out story) || LocalOnlyStory(story)) return;
            StoryHost.Current.Rebroadcast(r);
            Accepted.Remove(token);
            EndRemote(token);
        }
        static void RemoveSeat(int seat)
        {
            List<ulong> tokens = Ledger.RemoteSeatTokens(seat);
            foreach (ulong token in tokens) EndRemote(token, true);
            tokens = Ledger.AcceptedSeatTokens(seat);
            foreach (ulong token in tokens) Accepted.Remove(token);
        }
        static void EndRemote(ulong token, bool force = false)
        {
            Story story;
            if (!Remote.TryGetValue(token, out story)) return;
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
            if (Remote.Count == 0) CleanupNative();
            if (!Watching) RestoreVisible();
        }
        static void ReceiveSkip(BinaryReader r)
        {
            ulong token = TokenKey(Services.Net.Sender, StoryWire.ReadToken(r)); Story story;
            if (!(Services.Net.IsHost ? Accepted : Remote).TryGetValue(token, out story) || LocalOnlyStory(story)) return;
            StoryHost.Current.Rebroadcast(r);
            if (!Remote.ContainsKey(token)) return;
            Ledger.Skip(story); if (story.Resolved && !story.Watch) SkipRemote(story);
        }
        static void SkipRemote(Story story)
        {
            if (LocalOnlyStory(story) || story.SkipApplied || SkipCutsceneController.Instance == null || !SkipCutsceneController.Instance.SkippingAvailable) return;
            // Only the matching mirrored run is eligible. A fallback watch never skips unrelated story.
            if (!StoryLedger<Story>.CanSkip(story, story.Sequence != null || story.Controller != null)) return;
            bool previous = s_mirroring; s_mirroring = true;
            try { SkipCutsceneController.Instance.SkipCutscene(); story.SkipApplied = true; }
            finally { s_mirroring = previous; }
        }
        static readonly Action s_cutsceneTick = CutsceneGuard.Tick;

        public static void Tick()
        {
            SubsystemGuard.Run("CutsceneGuard.Tick", s_cutsceneTick);
            if (Time.realtimeSinceStartup >= s_statsAt)
            {
                s_statsAt = Time.realtimeSinceStartup + 30f;
                if (Services.Net.Enabled) Services.Log.Info("net: story classification startsSeen=" + StartsSeen + " sent=" + StartsSent
                    + " mirrored=" + StartsMirrored + " cachedIds=" + Classification.Count + " watching=" + Watching);
            }
            if (!Online) { if (Local.Count + Remote.Count + Pending.Count > 0) Reset(); return; }
            float now = Time.realtimeSinceStartup; bool locked = NativeLocked();
            List<ulong> expiredAccepted = new List<ulong>();
            foreach (KeyValuePair<ulong, Story> pair in Accepted) if (now - pair.Value.Began >= 60f) expiredAccepted.Add(pair.Key);
            foreach (ulong token in expiredAccepted) Accepted.Remove(token);
            ObserveCandidates(now);
            if (Remote.Count > 0)
            {
                List<ulong> released = new List<ulong>();
                foreach (KeyValuePair<ulong, Story> pair in Remote)
                {
                    Story story = pair.Value;
                    if (story.FreeSince >= 0 && now - story.FreeSince >= 2f && now - story.FreeLast <= 0.25f)
                    { released.Add(pair.Key); story.FreeSince = -1f; }
                }
                foreach (ulong token in released) EndRemote(token);
            }
            List<ulong> ended = new List<ulong>();
            foreach (Story story in Local.Values)
            {
                if (locked) story.SawLock = true;
                bool running = story.Sequence != null && story.Sequence.IsRunning;
                if (StoryLedger<Story>.LocalEnded(story, locked, running, now)) ended.Add(TokenKey(story.Sender, story.Token));
            }
            foreach (ulong token in ended) SendEnd(token);
            ended.Clear();
            List<ulong> finished = new List<ulong>();
            foreach (Story story in Remote.Values)
            {
                bool running = !(now - story.Began >= 60f) && story.EndAt >= 0
                    && story.Sequence != null && story.Sequence.IsRunning;
                if (StoryLedger<Story>.RemoteFinished(story, running, now))
                { finished.Add(TokenKey(story.Sender, story.Token)); continue; }
                if (story.Resolved)
                {
                    if (story.Skipped && !story.Watch) SkipRemote(story);
                    continue;
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
                    if (!Resolve(story) && Core.Scenes.Manager != null)
                        Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(story.Position.x - 16, story.Position.y - 16, 32, 32), true, false, false);
                }
            }
            foreach (ulong token in ended) EndRemote(token);
            foreach (ulong token in finished) EndRemote(token, true);
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
        [HarmonyPatch(typeof(ActionSequence), "Perform")]
        static class SequenceStart
        {
            static bool Prefix(ActionSequence __instance, out StartCall __state)
            {
                __state = new StartCall();
                if (s_localOnlyDepth > 0 || s_localOnlyRunDepth > 0) LocalOnly.Add(__instance);
                bool replay = s_mirroring || s_replayActionDepth > 0 || OwnedSequences.Contains(__instance);
                __state.Root = s_entryDepth == 0 && __instance.enabled && __instance.MoonGuid != null && __instance.MoonGuid != MoonGuid.Empty;
                if (__state.Root) { s_entryDepth++; __state.Before = TakeoverMask(); __state.Position = Own == null ? Vector3.zero : Own.Position; }
                if (replay && s_native != null && Remote.Count > 0 && !PuppetCall) OwnedSequences.Add(__instance);
                return true;
            }
            static void Postfix(ActionSequence __instance, StartCall __state)
            {
                if (Services.Net.Enabled && !s_mirroring && !PuppetCall) StartsSeen++;
                if (OwnedSequences.Contains(__instance) && __instance.MoonGuid != null && __instance.MoonGuid != MoonGuid.Empty)
                    Performed[KeyOf(new Story { Kind = 0, Scene = SceneOf(__instance), Id = __instance.MoonGuid })] = Time.realtimeSinceStartup;
                if (!__state.Root || !__instance.enabled || __instance.MoonGuid == null || __instance.MoonGuid == MoonGuid.Empty || Mirrored.Contains(__instance)) return;
                Consider(new Story { Kind = 0, Scene = SceneOf(__instance), Id = new MoonGuid(__instance.MoonGuid), Sequence = __instance }, __state);
            }
            static Exception Finalizer(StartCall __state, Exception __exception) { if (__state.Root) s_entryDepth--; return __exception; }
        }
        [HarmonyPatch(typeof(ActionSequence), "RunAction")]
        static class ReplayAction
        {
            static void Prefix(ActionSequence __instance, out int __state)
            {
                __state = 0;
                if (LocalOnly.Contains(__instance)) { __state |= 4; s_localOnlyRunDepth++; }
                if (OwnedSequences.Contains(__instance)) { __state |= 1; s_replayActionDepth++; }
                foreach (Story story in Local.Values)
                    if (story.Sequence == __instance) { __state |= 2; s_sourceActionDepth++; break; }
            }
            static Exception Finalizer(int __state, Exception __exception)
            {
                if ((__state & 1) != 0) s_replayActionDepth--;
                if ((__state & 2) != 0) s_sourceActionDepth--;
                if ((__state & 4) != 0) s_localOnlyRunDepth--;
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
                if (__state.Root) { s_entryDepth++; __state.Before = TakeoverMask(); __state.Position = Own == null ? Vector3.zero : Own.Position; }
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
        [HarmonyPatch(typeof(LockPlayerInputAction), "Perform")]
        static class TrackInputAction
        {
            static void Prefix(LockPlayerInputAction __instance)
            { if (s_native != null && (s_mirroring || s_replayActionDepth > 0) && !PuppetCall) OwnedInputActions.Add(__instance); }
        }
        [HarmonyPatch(typeof(PauseGameAction), "Perform")]
        static class TrackPause
        {
            static void Prefix(PauseGameAction __instance)
            { if (s_native != null && (s_mirroring || s_replayActionDepth > 0) && !PuppetCall && !__instance.IsPerforming) OwnedPauses.Add(__instance); }
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
