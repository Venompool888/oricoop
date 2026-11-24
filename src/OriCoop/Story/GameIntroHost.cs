using System;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using UnityEngine;

namespace OriCoop.Story
{
    internal partial class GameStoryHost
    {
        const byte IntroMessage = 54;
        static IntroState remoteIntro;
        static bool hostSession;
        static float publishAt;
        static IntroState lastPublished;
        sealed class HeldOri
        {
            public SeinCharacter Ori;
            public Vector3 Position;
            public readonly Dictionary<Renderer, bool> Renderers = new Dictionary<Renderer, bool>();
        }
        static readonly Dictionary<int, HeldOri> heldOris = new Dictionary<int, HeldOri>();
        static SeinCharacter LocalOri { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        public bool IntroAuthority { get { return !Services.Net.Enabled || Services.Net.IsHost; } }
        public virtual bool IntroPlaying
        {
            get { return GameStateMachine.Instance != null && (GameStateMachine.Instance.CurrentState == GameStateMachine.State.Logos
                || GameStateMachine.Instance.CurrentState == GameStateMachine.State.Prologue); }
        }
        public bool IntroAtTitle
        {
            get { return GameStateMachine.Instance != null && (GameStateMachine.Instance.CurrentState == GameStateMachine.State.StartScreen
                || GameStateMachine.Instance.CurrentState == GameStateMachine.State.TitleScreen); }
        }
        public virtual bool IntroP1Free
        {
            get
            {
                SeinCharacter p1 = LocalOri; GameController gc = GameController.Instance;
                return p1 != null && p1.Active && (Characters.Current == null || object.ReferenceEquals(Characters.Current, p1))
                    && GameStateMachine.Instance != null && GameStateMachine.Instance.CurrentState == GameStateMachine.State.Game
                    && gc != null && !gc.IsLoadingGame && !gc.GameplaySuspended && !gc.GameplaySuspendedForUI && !gc.InputLocked
                    && p1.Controller != null && p1.Controller.CanMove && !p1.Controller.LockMovementInput && !p1.Controller.IgnoreControllerInput
                    && (p1.CutsceneBlocked == null || !p1.CutsceneBlocked.Active)
                    && (p1.CutsceneMovement == null || !p1.CutsceneMovement.Active)
                    && (Core.Scenes.Manager == null || !Core.Scenes.Manager.IsLoadingScenes);
            }
        }
        public StoryPoint IntroP1Position { get { return Point(LocalOri == null ? Vector3.zero : LocalOri.Position); } }
        public int[] IntroSeats
        {
            get
            {
                List<int> seats = new List<int>();
                if (LocalOri != null) seats.Add(Players.LocalSeat);
                foreach (Puppet puppet in Players.Remotes) if (puppet.Sein != null) seats.Add(puppet.Seat);
                return seats.ToArray();
            }
        }
        public int[] IntroSessionSeats
        {
            get
            {
                // T-139: the seat table (host: authoritative; guest: the host's published copy). C-server sessions have none.
                if (!Services.Net.Enabled || !NetPlay.MultiSeatSupported) return null;
                List<int> seats = new List<int>();
                for (int seat = 1; seat <= 4; seat++) if (NetPlay.SeatOwner(seat) != 0) seats.Add(seat);
                return seats.Count == 0 ? null : seats.ToArray();
            }
        }
        public bool CanPlaceIntroSeat(int seat)
        {
            SeinCharacter ori = OriAt(seat); GameController gc = GameController.Instance;
            return ori != null && ori.gameObject.activeInHierarchy && gc != null && !gc.IsLoadingGame
                && (Core.Scenes.Manager == null || !Core.Scenes.Manager.IsLoadingScenes)
                && (!Services.Net.Enabled || Services.Session.Ready);
        }
        public IntroState RemoteIntro { get { return remoteIntro; } }
        public bool IntroHostSession { get { return hostSession; } }
        static StoryPoint Point(Vector3 p) { return new StoryPoint(p.x, p.y, p.z); }
        static Vector3 Vector(StoryPoint p) { return new Vector3(p.X, p.Y, p.Z); }
        static SeinCharacter OriAt(int seat)
        {
            if (seat == Players.LocalSeat) return LocalOri;
            foreach (Puppet puppet in Players.Remotes) if (puppet.Seat == seat) return puppet.Sein;
            return null;
        }
        public bool TryIntroGround(StoryPoint candidate, out StoryPoint spot)
        {
            spot = candidate; SeinCharacter p1 = LocalOri;
            if (p1 == null || p1.Controller == null) return false;
            Vector3 origin = Vector(candidate); float highest = float.NegativeInfinity; bool found = false;
            foreach (RaycastHit hit in Physics.RaycastAll(origin + Vector3.up * 3f, Vector3.down, 7f, p1.Controller.RayTestLayerMask))
            {
                if (hit.collider == null || hit.collider.isTrigger || hit.normal.y < 0.6f
                    || hit.collider.GetComponentInParent<Entity>() != null || hit.collider.GetComponentInParent<SeinCharacter>() != null
                    || hit.point.y <= highest) continue;
                Vector3 position = hit.point + (p1.Position - p1.PlatformBehaviour.PlatformMovement.FeetPosition) + Vector3.up * 0.1f;
                bool blocked = false;
                foreach (Collider c in Physics.OverlapSphere(position + Vector3.up * 0.5f, 0.35f, p1.Controller.RayTestLayerMask))
                    if (!c.isTrigger && c != hit.collider && c.GetComponentInParent<SeinCharacter>() == null) { blocked = true; break; }
                if (blocked) continue;
                highest = hit.point.y; spot = Point(position); found = true;
            }
            return found;
        }
        public void SetIntroHeld(int seat, bool held)
        {
            // T-205: not the story's own character while this PC plays the host's prologue along (StorySync.IntroHeld).
            if (held && StorySync.IntroExempt(seat)) held = false;
            HeldOri previous;
            heldOris.TryGetValue(seat, out previous);
            SeinCharacter ori = OriAt(seat);
            if (previous != null && (!held || ori != previous.Ori))
            {
                foreach (KeyValuePair<Renderer, bool> r in previous.Renderers) if (r.Key != null) r.Key.enabled = r.Value;
                heldOris.Remove(seat); previous = null;
            }
            if (!held || ori == null) return;
            if (previous == null)
            {
                previous = new HeldOri { Ori = ori, Position = ori.Position }; heldOris[seat] = previous;
            }
            ori.Position = previous.Position; ori.Speed = Vector3.zero;
            HideIntroRenderers(ori.gameObject, previous);
            Puppet puppet = Players.OfSein(ori);
            if (puppet != null)
            {
                P2Input.Fill(puppet.Input, 0, 0, 0, 0, 0, false, false, false);
                foreach (GameObject owned in puppet.Owned) if (owned != null) HideIntroRenderers(owned, previous);
                if (puppet.Companion != null) HideIntroRenderers(puppet.Companion.gameObject, previous);
            }
            else if (Characters.Ori != null) HideIntroRenderers(Characters.Ori.gameObject, previous);
        }
        static void HideIntroRenderers(GameObject go, HeldOri held)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!held.Renderers.ContainsKey(r)) held.Renderers[r] = r.enabled;
                r.enabled = false;
            }
        }
        public void PlaceIntroSeat(int seat, StoryPoint position)
        {
            SeinCharacter ori = OriAt(seat); if (ori == null) return;
            // Plugin.Teleport adds half a unit; the layout already contains the Ori's exact grounded position.
            Plugin.Teleport(ori, Vector(position) - Vector3.up * 0.5f);
            SessionRules.PositionChanged();
            Services.Log.Info("story: intro wake P" + seat + " at " + Vector(position));
        }
        internal static void InitIntroNetwork()
        {
            SharedPrologue.Init();   // T-203
            Services.Net.On(IntroMessage, delegate(BinaryReader r) {
                if (Services.Net.IsHost || Services.Net.Sender != Services.Net.HostSeat) return;
                remoteIntro = IntroWire.Read(r);
            });
            // T-105: only the host's rule decides; guests take its snapshot (with the late seats) as it is.
            Services.Net.PlayerJoined += delegate(int seat) { publishAt = 0; if (Services.Net.IsHost) StorySync.IntroSeatJoined(seat); };
            Services.Net.PlayerLeft += delegate(int seat) { if (Services.Net.IsHost) StorySync.IntroSeatLeft(seat); };
            Services.Net.SessionStarted += delegate {
                remoteIntro = null; publishAt = 0; lastPublished = null; hostSession = true;
                if (!Services.Net.IsHost) StorySync.ResetIntro();
            };
            Services.Net.SessionEnded += delegate {
                remoteIntro = null; publishAt = 0; lastPublished = null; hostSession = false; StorySync.ResetIntro();
            };
        }
        [HarmonyPatch(typeof(PlayerInput), "FixedUpdate")]
        static class IntroPrimaryInput
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix()
            {
                // T-203: while this guest plays the host's prologue along, SharedPrologue replaces the gameplay input
                // with the host's and leaves the menu buttons to this player (its pause menu: options, leave).
                if (SharedPrologue.OwnsInput) return;
                if (!StorySync.IntroHeld(Players.LocalSeat)) return;
                Core.Input.Horizontal = Core.Input.Vertical = 0;
                Core.Input.HorizontalAnalogLeft = Core.Input.VerticalAnalogLeft = 0;
                Core.Input.HorizontalAnalogRight = Core.Input.VerticalAnalogRight = 0;
                Core.Input.HorizontalDigiPad = Core.Input.VerticalDigiPad = 0;
                foreach (Core.Input.InputButtonProcessor button in Core.Input.Buttons) button.Update(false);
            }
        }
        [HarmonyPatch(typeof(P2Input), "Fill")]
        static class IntroPuppetInput
        {
            static void Prefix(InputState s, ref int b, ref float lx, ref float ly, ref float rx, ref float ry, ref bool lt, ref bool rt)
            {
                foreach (Puppet p in Players.Remotes)
                    if (p.Input == s && StorySync.IntroHeld(p.Seat))
                    { b = 0; lx = ly = rx = ry = 0; lt = rt = false; return; }
            }
        }
        [HarmonyPatch(typeof(SeinController), "get_InputLocked")]
        static class IntroControllerLock
        {
            static void Postfix(SeinController __instance, ref bool __result)
            { if (StorySync.IntroHeld(Players.SeatOf(__instance.Sein))) __result = true; }
        }
        [HarmonyPatch(typeof(SeinController), "FixedUpdate")]
        static class IntroFreeze
        {
            static void Postfix(SeinController __instance)
            {
                int seat = Players.SeatOf(__instance.Sein);
                if (StorySync.IntroHeld(seat)) StoryHost.Current.SetIntroHeld(seat, true);
            }
        }
        [HarmonyPatch(typeof(NetPlay), "Correct")]
        static class IntroIgnoreRemoteCorrection
        {
            static bool Prefix(SeinCharacter p2) { return !StorySync.IntroHeld(Players.SeatOf(p2)); }
        }
        public void PublishIntro(IntroState state)
        {
            if (!Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost) return;
            if (lastPublished != state || Time.realtimeSinceStartup >= publishAt)
            {
                publishAt = Time.realtimeSinceStartup + 0.25f; lastPublished = state;
                Services.Net.Send(IntroMessage, true, delegate(BinaryWriter w) { IntroWire.Write(w, state); });
            }
        }
    }
}
