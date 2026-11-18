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
    public static class CutsceneGuard
    {
        const byte PedestalMessage = 73;
        static readonly RecentObjects<MoonGuid> Played = new RecentObjects<MoonGuid>();
        static readonly Dictionary<string, string> Traces = new Dictionary<string, string>();
        static float s_freeSince = -1f;
        static Transform s_traceTarget;
        static bool s_traceTargetKnown;
        static float s_spiritAt = -2f, s_uiAt = -2f, s_letterboxAt = -2f, s_puppetAt = -2f, s_targetAt = -2f, s_fadeAt = -2f, s_actionLockSince = -1f, s_blackSince = -1f;
        public static bool ReplayingPedestal;
        static float s_pedestalUntil = -1f;
        // Since when the gameplay camera has been in straight-line Move motion (-1 = it is chasing). Only the sequence that
        // started the move sends it back to Chase (MoveCameraToPlayerAction / GoToChaseMode); a sequence cut off mid-way
        // (its scene unloaded under it) leaves the camera parked on a fixed point for good (T-058 hotfix).
        static float s_moveSince = -1f, s_chaseAt = -2f;
        public static int CameraMoveRepairs;
        static readonly bool s_mutateNoUnpark = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_CAM_NO_UNPARK") == "1";
        static readonly FieldInfo StraightLineTime = AccessTools.Field(typeof(CameraStraightLineMotion), "m_time");
        // In Move motion with the straight-line move already finished: parked on a fixed point, waiting for a sequence.
        static bool ParkedInMove(GameplayCamera cam)
        {
            if (cam == null || cam.Motion != GameplayCamera.MotionType.Move) return false;
            if (cam.StraightLineMotion == null || StraightLineTime == null) return true;
            return (float)StraightLineTime.GetValue(cam.StraightLineMotion) >= 1f;
        }
        public static bool PedestalWindow { get { return Time.realtimeSinceStartup < s_pedestalUntil; } }
        static float CameraDistance(SeinCharacter own)
        {
            if (UI.Cameras.Current == null || UI.Cameras.Current.Camera == null || own == null) return 0f;
            Vector3 c = UI.Cameras.Current.Camera.transform.position;
            return Vector2.Distance(new Vector2(c.x, c.y), new Vector2(own.Position.x, own.Position.y));
        }
        public static float CameraDistance() { return CameraDistance(Own); }
        // Test-only mutation switch: ORICOOP_P3_OFF=1 restores the pre-fix behaviour so the P3 scenario can prove it fails.
        public static readonly bool Off = Environment.GetEnvironmentVariable("ORICOOP_P3_OFF") == "1";
        public static int PedestalPlaysSent, PedestalPlaysReceived, PedestalPlaysPlayed, Repairs, LocalPedestalActivations;
        static SeinCharacter Own { get { return StoryHost.Current.P1 != null ? StoryHost.Current.P1 : Characters.Sein; } }
        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected; } }
        public static void Log(string msg)
        {
            Services.Log.Info("cutscene " + DateTime.Now.ToString("HH:mm:ss.fff") + " t=" + Time.time.ToString("F2") + " " + msg);
            StoryHost.Current.Trace("story", "guard", "msg", msg);   // merged P1/P2 timeline (docs\coop-trace.md)
        }
        public static CameraPuppetController CameraPuppet()
        {
            return UI.Cameras.Current != null && UI.Cameras.Current.Controller != null
                ? UI.Cameras.Current.Controller.PuppetController : null;
        }
        public static bool IsPlayerCameraTarget(Transform target)
        {
            return Own != null && target == Own.Transform
                || target != null && StoryHost.Current.IsPluginCameraTarget(target);
        }
        public static void Init() { Services.Net.On(PedestalMessage, ReceivePedestal); Services.Net.On(LeverMessage, ReceiveLever); }

        // ---- Levers (playtest 19:07, Spirit Cavern B): the lever's Direction is host-authoritative world state, so a
        // guest's push was silently undone by the host's blob while its camera story ran locally and was "mirrored" on
        // top (performed twice, then stopped half-way: input lock, letterbox and camera left at the platform, or a
        // black fade). Now every push is sent and each end flips its own lever, playing its own camera story natively.
        const byte LeverMessage = 78;
        static bool s_applyingLever;
        public static int LeverPushesSent, LeverPushesPlayed;
        static void ReceiveLever(BinaryReader r)
        {
            byte rawDirection; byte[] bytes = ObjectWire.ReadLever(r, out rawDirection);
            MoonGuid id = GameStoryHost.ReadGuid(bytes); Lever.LeverDirections direction = (Lever.LeverDirections)rawDirection;
            if (Off) return;
            Lever lever = StoryHost.Current.FindAny(id) as Lever;
            if (lever == null) foreach (Lever candidate in Lever.All) if (candidate != null && candidate.MoonGuid == id) { lever = candidate; break; }
            if (lever == null) { Log("lever push skipped: lever not loaded " + id); return; }
            if (ObjectWire.LeverChanged((int)lever.Direction, (int)direction))
            {
                s_applyingLever = true;
                try
                {
                    lever.SetLeverDirection(direction);
                    if (lever.Direction != direction) { lever.Direction = direction; Log("lever forced " + direction + " (condition failed here)"); }
                    LeverPushesPlayed++; Log("lever push played " + id + " " + direction);
                }
                finally { s_applyingLever = false; }
            }
            if (Services.Net.IsHost) Services.Net.Send(LeverMessage, true, delegate(BinaryWriter w) { ObjectWire.WriteLever(w, id.ToByteArray(), (byte)direction); });
        }
        [HarmonyPatch]
        static class LeverScope
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Lever), "OnPushLeverLeft");
                yield return AccessTools.Method(typeof(Lever), "OnPushLeverRight");
                yield return AccessTools.Method(typeof(Lever), "OnPushLeverMiddle");
            }
            static void Prefix(Lever __instance, out int __state)
            {
                __state = -1;
                if (Off || !Online || StoryHost.Current.Inside) return;
                __state = (int)__instance.Direction;
                // The lever's stories run natively on every end; do not also mirror them (see PedestalWindow).
                StorySync.EnterLocalOnly(null); s_pedestalUntil = Mathf.Max(s_pedestalUntil, Time.realtimeSinceStartup + 12f);
            }
            static Exception Finalizer(Lever __instance, int __state, Exception __exception)
            {
                if (__state < 0) return __exception;
                StorySync.ExitLocalOnly();
                if (!s_applyingLever && ObjectWire.LeverChanged((int)__instance.Direction, __state))
                {
                    MoonGuid id = new MoonGuid(__instance.MoonGuid); Lever.LeverDirections direction = __instance.Direction;
                    Services.Net.Send(LeverMessage, true, delegate(BinaryWriter w) { ObjectWire.WriteLever(w, id.ToByteArray(), (byte)direction); });
                    LeverPushesSent++; Log("lever push sent " + id + " " + direction);
                }
                return __exception;
            }
        }
        public static void LocalActivation(GetAbilityPedestal pedestal)
        {
            if (Off || !Online || StoryHost.Current.Inside || ReplayingPedestal || pedestal.CurrentState == GetAbilityPedestal.States.Completed) return;
            MoonGuid id = new MoonGuid(pedestal.MoonGuid);
            Played.Mark(id, Time.realtimeSinceStartup);
            Services.Net.Send(PedestalMessage, true, delegate(BinaryWriter w) { ObjectWire.WritePedestal(w, id.ToByteArray()); });
            PedestalPlaysSent++; Log("pedestal play sent " + id);
        }
        static void ReceivePedestal(BinaryReader r)
        {
            MoonGuid id = GameStoryHost.ReadGuid(ObjectWire.ReadPedestal(r)); PedestalPlaysReceived++; Log("pedestal play received " + id);
            if (Played.Recent(id, Time.realtimeSinceStartup, 30f)) return;
            GetAbilityPedestal pedestal = StoryHost.Current.FindAny(id) as GetAbilityPedestal;
            if (pedestal == null)
                foreach (GetAbilityPedestal candidate in Resources.FindObjectsOfTypeAll<GetAbilityPedestal>())
                    if (candidate != null && candidate.MoonGuid == id) { pedestal = candidate; break; }
            if (pedestal == null) { Log("pedestal play skipped: pedestal missing " + id); return; }
            SeinCharacter own = Own;
            if (own == null) { Log("pedestal play skipped: own player missing"); return; }
            if (GameController.Instance == null || GameController.Instance.IsLoadingGame)
            { Log("pedestal play skipped: loading"); return; }
            Played.Mark(id, Time.realtimeSinceStartup);
            if (Vector3.Distance(own.Position, pedestal.transform.position) > 12f) { StoryHost.Current.Teleport(own, pedestal.transform.position); SnapSpirit("pedestal teleport"); }
            ReplayingPedestal = true;
            try { pedestal.ActivatePedestal(); PedestalPlaysPlayed++; Log("pedestal play played " + id); }
            finally { ReplayingPedestal = false; }
            // Guests only talk to the host: forward so every other guest plays it too (the sender ignores the echo).
            if (Services.Net.IsHost) Services.Net.Send(PedestalMessage, true, delegate(BinaryWriter w) { ObjectWire.WritePedestal(w, id.ToByteArray()); });
        }
        static void Trace(string key, string value)
        {
            string old;
            if (!Traces.TryGetValue(key, out old) || old != value) { Traces[key] = value; Log(key + "=" + value); }
        }
        public static void Tick()
        {
            if (!Online) { s_freeSince = -1f; Traces.Clear(); s_traceTargetKnown = false; return; }
            GameController gc = GameController.Instance; SeinCharacter own = Own;
            if (Game.UI.SeinUI != null) Trace("ShowUI", Game.UI.SeinUI.ShowUI.ToString());
            Trace("letterbox", Letterbox.ShowLetterboxes.ToString());
            Trace("fader", UI.Fader == null ? "null" : UI.Fader.CurrentState.ToString());
            Transform target = UI.Cameras.Current == null ? null : UI.Cameras.Current.Target;
            if (!s_traceTargetKnown || target != s_traceTarget)
            {
                s_traceTarget = target; s_traceTargetKnown = true;
                Log("camera target=" + (target == null ? "null" : target.name));
            }
            CameraPuppetController puppet = CameraPuppet();
            Trace("camera puppet active", (puppet != null && puppet.CinematicPuppet != null && puppet.Tween > 0.01f).ToString());
            Trace("spirit state", Characters.Ori == null ? "null" : Characters.Ori.CurrentState.ToString());
            Trace("local input locked", (gc != null && (gc.LockInput || gc.LockInputByAction)
                || own != null && own.CutsceneBlocked != null && own.CutsceneBlocked.Active).ToString());
            float now = Time.realtimeSinceStartup;
            // Only LockPlayerInputAction's coroutine sets LockInputByAction and it clears it after a few seconds; a
            // coroutine killed with its GameObject (story stopped / deactivated mid-way) never does and the player can't
            // move at all (it also keeps the originator's story alive). Held 20 s: release it.
            // Fully black (FadeStay) while nothing is loading: start the "stuck black" clock.
            bool black = UI.Fader != null && UI.Fader.CurrentState == FaderB.State.FadeStay
                && gc != null && !gc.IsLoadingGame && Services.Session.Ready && !StorySync.StoryActive;
            if (!black) s_blackSince = -1f; else if (s_blackSince < 0) s_blackSince = now;
            if (gc != null && gc.LockInputByAction && !gc.IsLoadingGame) { if (s_actionLockSince < 0) s_actionLockSince = now; }
            else s_actionLockSince = -1f;
            bool parked = ParkedInMove(UI.Cameras.Current);
            if (!parked) s_moveSince = -1f; else if (s_moveSince < 0) s_moveSince = now;
            if (!Off && s_actionLockSince >= 0 && now - s_actionLockSince >= 20f)
            { gc.LockInputByAction = false; s_actionLockSince = -1f; Repairs++; Log("repair stale LockInputByAction (held 20 s)"); }
            if (StoryHost.Current.Inside || own == null || gc == null || gc.IsLoadingGame || gc.GameplaySuspended
                || Game.UI.MainMenuVisible || StorySync.StoryActive
                || PedestalWindow) { s_freeSince = -1f; return; }   // a pedestal/lever cutscene runs natively: hands off
            bool free = (StorySync.TakeoverMaskPublic() & 191) == 0;
            if (!free) s_freeSince = -1f;
            else if (s_freeSince < 0) s_freeSince = now;
            float since = StorySync.LastStoryActivity >= 0 ? now - StorySync.LastStoryActivity : float.PositiveInfinity;
            if (Characters.Ori != null && Characters.Ori.CurrentState == Ori.State.Hovering && now - s_spiritAt >= 2f
                && Vector3.Distance(Characters.Ori.Position, own.Position) > 30f) { s_spiritAt = now; SnapSpirit("far away"); }
            // LockPlayerInputAction's coroutine clears LockInputByAction; a story stopped or deactivated mid-way never
            // does, and the player can't move at all. No story for 15 s: release it.
            // Free for 3 s: repair. Still locked 30-120 s after the last story ended: a half-run story left it so.
            if (s_freeSince >= 0 && now - s_freeSince >= 3f || since >= 30f && since <= 120f)
                Repair(now, false, since <= 120f);
        }
        // A teleport moves the player but not the orb; a hovering orb then drifts in from far away and spirit flame
        // is fired from where it is ("attacks fly over from over there").
        public static void SnapSpirit(string why)
        {
            if (Off || Characters.Ori == null || Own == null || StoryHost.Current.Inside) return;
            if (Vector3.Distance(Characters.Ori.Position, Own.Position) <= 30f) return;
            if (StuckSpirit()) Characters.Ori.BackToPlayerController(); else Characters.Ori.MoveOriToPlayer();
            Repairs++; Log("spirit snapped to player (" + why + ")");
        }
        static bool StuckSpirit()
        {
            return Characters.Ori != null && !Characters.Ori.InsideDoor && !Characters.Ori.InsideMapstone
                && (Characters.Ori.CurrentState == Ori.State.MoveToPosition
                || Characters.Ori.CurrentState == Ori.State.MoveAlongCurve);
        }
        static void Repair(float now, bool immediate, bool camera)
        {
            if (Off) return;
            if (StuckSpirit() && (immediate || now - s_spiritAt >= 2f))
            { Characters.Ori.BackToPlayerController(); s_spiritAt = now; Repairs++; Log("repair spirit"); }
            if (Game.UI.SeinUI != null && !Game.UI.SeinUI.ShowUI && (immediate || now - s_uiAt >= 2f))
            { Game.UI.SeinUI.ShowUI = true; s_uiAt = now; Repairs++; Log("repair ShowUI"); }
            if (Letterbox.ShowLetterboxes && (immediate || now - s_letterboxAt >= 2f))
            { Letterbox.ShowLetterboxes = false; s_letterboxAt = now; Repairs++; Log("repair letterbox"); }
            // Repair runs only once no story is active and the player has been free for 3 s; a camera whose move finished and
            // that stays in Move for 4 s has no sequence left to bring it back (mirrored stomp post whose scene unloaded, net CAM 0/1738).
            if (!s_mutateNoUnpark && s_moveSince >= 0 && now - s_moveSince >= 4f && now - s_chaseAt >= 2f && UI.Cameras.Current != null)
            {
                UI.Cameras.Current.GoToChaseMode(); s_chaseAt = now; s_moveSince = -1f;
                Repairs++; CameraMoveRepairs++; Log("repair camera left in Move motion (nothing playing)");
            }
            if (!camera) return;
            // FadeToBlackAction holds black (FadeStay) until a FadeFromBlack: a story cut in between leaves the screen
            // black. Only a screen that has stayed black for 4 s is stuck: a fade that is still going (deaths, doors,
            // SaveSync's checkpoint reload, which continues from the fade-in callback) must never be touched.
            if (s_blackSince >= 0 && now - s_blackSince >= 4f && now - s_fadeAt >= 2f)
            { UI.Fader.FadeOut(0.5f); s_fadeAt = now; s_blackSince = -1f; Repairs++; Log("repair black fade (black for 4 s)"); }
            CameraPuppetController puppet = CameraPuppet();
            // A held cinematic puppet is only a lock if it keeps the view away from the player.
            if (puppet != null && puppet.CinematicPuppet != null && puppet.Tween > 0.01f && CameraDistance(Own) > 10f && now - s_puppetAt >= 2f)
            { puppet.Tween = 0; puppet.CinematicPuppet = null; s_puppetAt = now; Repairs++; Log("repair camera puppet"); }
            if (UI.Cameras.Current != null && Own != null && !IsPlayerCameraTarget(UI.Cameras.Current.Target) && now - s_targetAt >= 2f)
            {
                ICharacter previous = Characters.Current; Characters.Current = Own;
                try { UI.Cameras.Current.ChangeTargetToCurrentCharacter(); }
                finally { Characters.Current = previous; }
                s_targetAt = now; Repairs++; Log("repair camera target");
            }
        }
        [HarmonyPatch(typeof(GetAbilityPedestal), "ActivatePedestal")]
        static class PedestalScope
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(GetAbilityPedestal __instance, out bool __state)
            {
                __state = !StoryHost.Current.Inside && !Off;
                // Both ends play the pedestal natively; nested stories it starts (timelines, story texts) must not
                // also be mirrored from the partner, or each end performs them twice (double input locks).
                if (__state) s_pedestalUntil = Time.realtimeSinceStartup + 20f;
                if (__state) StorySync.EnterLocalOnly(__instance.ActivatePedestalSequence as ActionSequence);
            }
            static Exception Finalizer(bool __state, Exception __exception)
            { if (__state) StorySync.ExitLocalOnly(); return __exception; }
        }
        [HarmonyPatch(typeof(GetAbilityPedestal), "ActivatePedestal")]
        static class CountPedestal
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(GetAbilityPedestal __instance, out GetAbilityPedestal.States __state) { __state = __instance.CurrentState; }
            static Exception Finalizer(GetAbilityPedestal __instance, GetAbilityPedestal.States __state, Exception __exception)
            {
                if (!StoryHost.Current.Inside && __state != GetAbilityPedestal.States.Completed && __instance.CurrentState == GetAbilityPedestal.States.Completed)
                    LocalPedestalActivations++;
                return __exception;
            }
        }
        [HarmonyPatch(typeof(PlayerAbilities), "SetAbility", new Type[] { typeof(AbilityType), typeof(bool) })]
        static class AbilityTrace
        {
            static void Prefix(PlayerAbilities __instance, AbilityType ability, out bool __state) { __state = __instance.HasAbility(ability); }
            static void Postfix(PlayerAbilities __instance, AbilityType ability, bool value, bool __state)
            {
                if (Online && !StoryHost.Current.Inside && Own != null && __instance == Own.PlayerAbilities && __state != __instance.HasAbility(ability))
                    Log("ability " + ability + "=" + value);
            }
        }
        [HarmonyPatch]
        static class RestoreRepair
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(GameController), "RestoreCheckpoint");
                yield return AccessTools.Method(typeof(GameController), "RestoreCheckpointImmediate");
                yield return AccessTools.Method(typeof(SaveGameController), "OnFinishedLoading");
                yield return AccessTools.Method(typeof(RestoreCheckpointController), "FinishLoading");
            }
            static void Postfix() { if (Online && !StoryHost.Current.Inside) Repair(Time.realtimeSinceStartup, true, false); }
        }
        [HarmonyPatch(typeof(GameController), "CreateCheckpoint")]
        static class SaveRepair
        {
            static void Prefix()
            {
                if (!Off && Online && !StoryHost.Current.Inside && !StorySync.StoryActive && StuckSpirit())
                { Characters.Ori.BackToPlayerController(); Repairs++; Log("repair spirit before checkpoint"); }
            }
        }
    }
}
