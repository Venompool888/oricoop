using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    public static class NetCutsceneTest
    {
        const byte BarrierMessage = 74;
        const string SceneName = "sunkenGladesSpiritCavernWalljumpB";
        static readonly MoonGuid FixtureId = new MoonGuid(0x50335445, 0x53545345, 0x5155454e, 0x43453031);
        internal sealed class Sample { public int Step, Plays, Direction; public bool Failed, Found, Ability, SawLetterbox; }
        static readonly Dictionary<int, Sample> Local = new Dictionary<int, Sample>();
        internal static readonly Dictionary<int, Sample> Remote = new Dictionary<int, Sample>();
        static readonly HashSet<int> Acknowledged = new HashSet<int>();
        static bool s_registered;
        static GetAbilityPedestal s_pedestal;
        static Lever s_lever;
        static int s_baseline;
        static float s_lastActivation = -31f;
        static GameObject s_dummy;
        internal static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        internal static float Now { get { return Time.realtimeSinceStartup; } }
        static bool Ability { get { return Own != null && Own.PlayerAbilities.HasAbility(AbilityType.WallJump); } }
        static void Bot() { NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; }
        internal static IEnumerator Wait(float seconds) { float end = Now + seconds; while (Now < end) yield return null; }
        internal static int Failures(TestHarness h)
        { return ((ICollection)AccessTools.Field(typeof(TestHarness), "m_fails").GetValue(h)).Count; }
        static void Receive(BinaryReader r)
        {
            byte kind = r.ReadByte(); int step = r.ReadInt32();
            if (kind == 1) { Acknowledged.Add(step); return; }
            Sample p = new Sample(); p.Step = step; p.Failed = r.ReadBoolean();
            p.Found = r.ReadBoolean(); p.Ability = r.ReadBoolean(); p.Plays = r.ReadInt32();
            p.Direction = r.ReadInt32(); p.SawLetterbox = r.ReadBoolean(); Remote[step] = p;
            NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) { w.Write((byte)1); w.Write(step); });
        }
        static void Send(Sample p)
        {
            NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) {
                w.Write((byte)0); w.Write(p.Step); w.Write(p.Failed); w.Write(p.Found); w.Write(p.Ability); w.Write(p.Plays);
                w.Write(p.Direction); w.Write(p.SawLetterbox);
            });
        }
        internal static void Publish(int step, bool failed)
        {
            Sample p = new Sample { Step = step, Failed = failed, Found = s_pedestal != null,
                Ability = Ability, Plays = CutsceneGuard.LocalPedestalActivations - s_baseline,
                Direction = s_lever == null ? 0 : (int)s_lever.Direction };
            Local[step] = p; Send(p);
        }
        internal static void Publish(Sample p) { Local[p.Step] = p; Send(p); }
        internal static IEnumerator Barrier(TestHarness h, int step)
        {
            if (!Local.ContainsKey(step)) Publish(step, false);
            // Step 1: with ORICOOP_NET_ONLY the two clients can arrive far apart (the guest still loading).
            // Sweep steps (>= 200) load distant scenes; one end can be much slower.
            float end = Now + (step == 1 || step >= 200 ? 60f : 20f), resend = Now + 1f;
            while ((!Remote.ContainsKey(step) || !Acknowledged.Contains(step)) && Now < end)
            {
                if (NetPlay.Connected && Now >= resend) { Send(Local[step]); resend = Now + 1f; }
                yield return null;
            }
            string label = step >= 400 ? "GATE " : "CUT ";
            h.Check(Remote.ContainsKey(step), label + "barrier " + step + " received");
            h.Check(Acknowledged.Contains(step), label + "barrier " + step + " acknowledged");
            if (Remote.ContainsKey(step)) h.Check(!Remote[step].Failed, label + "partner step " + step + " completed");
            if (step >= 400 && (!Remote.ContainsKey(step) || !Acknowledged.Contains(step) || Remote[step].Failed))
                h.Note("GATE barrier " + step + ": " + GateDiag());
            if (NetPlay.Connected) NetPlay.Peer.Flush();
            yield return Wait(0.25f);
        }
        internal static IEnumerator Step(TestHarness h, int step, IEnumerator routine)
        {
            int failures = Failures(h); bool failed = false;
            string label = step >= 400 ? "GATE " : "CUT ";
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(routine);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator top = stack.Peek(); bool moved = false; object value = null;
                    try { moved = top.MoveNext(); if (moved) value = top.Current; }
                    catch (Exception e) { failed = true; h.Check(false, label + "step " + step + " threw " + e); }
                    if (!moved)
                    {
                        stack.Pop(); IDisposable d = top as IDisposable;
                        if (d != null) try { d.Dispose(); } catch (Exception e) { failed = true; h.Check(false, label + "cleanup " + e.Message); }
                    }
                    else { IEnumerator child = value as IEnumerator; if (child == null) yield return value; else stack.Push(child); }
                }
            }
            finally
            {
                try
                {
                    while (stack.Count > 0)
                    {
                        IDisposable d = stack.Pop() as IDisposable;
                        if (d != null) try { d.Dispose(); } catch (Exception e) { failed = true; h.Check(false, label + "dispose " + e.Message); }
                    }
                }
                finally
                {
                    Bot();
                    if (step >= 400 && (failed || Failures(h) > failures)) h.Note("GATE step " + step + ": " + GateDiag());
                    Publish(step, failed || Failures(h) > failures);
                }
            }
            yield return Barrier(h, step);
        }
        static GetAbilityPedestal FindPedestal()
        {
            foreach (GetAbilityPedestal p in Resources.FindObjectsOfTypeAll<GetAbilityPedestal>())
            {
                if (p == null || p.Ability != AbilityType.WallJump) continue;
                for (Transform t = p.transform; t != null; t = t.parent)
                {
                    SceneRoot root = t.GetComponent<SceneRoot>();
                    if (root != null && root.MetaData != null && root.MetaData.SceneName == SceneName) return p;
                }
            }
            return null;
        }
        static IEnumerator Setup(TestHarness h)
        {
            RuntimeSceneMetaData meta = null;
            foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                if (scene.Scene == SceneName) { meta = scene; break; }
            if (meta != null)
            {
                Vector2 center = meta.SceneBounds.center;
                Plugin.Teleport(Own, new Vector3(center.x, center.y, 0));
                Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(meta.SceneBounds, true, false, false);
            }
            float end = Now + 20f;
            while (Now < end)
            {
                s_pedestal = FindPedestal();
                if (s_pedestal != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            yield return Barrier(h, 2);
            h.Check(s_pedestal != null && Remote.ContainsKey(2) && Remote[2].Found, "CUT wall-jump pedestal found");
        }
        static IEnumerator Refind(TestHarness h)
        {
            float start = Now;
            foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
                if (scene.Scene == SceneName)
                {
                    Vector2 center = scene.SceneBounds.center;
                    Plugin.Teleport(Own, new Vector3(center.x, center.y, 0));
                    Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false);
                    break;
                }
            float end = Now + 20f;
            while (Now < end)
            {
                s_pedestal = FindPedestal();
                if (s_pedestal != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            h.Note("CUT pedestal scene had streamed out; found again=" + (s_pedestal != null) + " after " + (Now - start).ToString("F1") + " s");
        }
        static Lever FindLever()
        {
            foreach (ActionLeverSystem system in Resources.FindObjectsOfTypeAll<ActionLeverSystem>())
            {
                if (system == null) continue;
                string path = system.transform.name;
                for (Transform t = system.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                if (!path.EndsWith("levers/lever", StringComparison.Ordinal)) continue;
                for (Transform t = system.transform; t != null; t = t.parent)
                {
                    SceneRoot root = t.GetComponent<SceneRoot>();
                    if (root != null && root.MetaData != null && root.MetaData.SceneName == "sunkenGladesSpiritCavernB") return system.Lever;
                }
            }
            return null;
        }
        static IEnumerator LeverSetup(TestHarness h, int step)
        {
            Plugin.Teleport(Own, new Vector3(-223.8f, -221.3f, 0));
            Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(new Rect(-243.8f, -241.3f, 40f, 40f), true, false, false);
            float end = Now + 20f;
            while (Now < end)
            {
                s_lever = FindLever();
                if (s_lever != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            Sample p = new Sample { Step = step, Found = s_lever != null };
            Local[step] = p; Send(p);
            yield return Barrier(h, step);
            h.Check(s_lever != null && Remote.ContainsKey(step) && Remote[step].Found, "P3 lever found");
            Plugin.Teleport(Own, s_lever.transform.position + Vector3.right * (NetPlay.IsHost ? -1.2f : 1.2f));
            end = Now + 5f;
            while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
            yield return Barrier(h, step + 1);
        }
        static IEnumerator LeverPull(TestHarness h, string label, bool host, bool right, int step)
        {
            int failures = Failures(h);
            yield return Settle();
            yield return Barrier(h, step);
            int mirrored = CutsceneGuard.LeverPushesPlayed, sent = CutsceneGuard.LeverPushesSent;
            h.Note("P3 " + label + ": lever=" + s_lever.Direction + " partner="
                + (Remote.ContainsKey(step) ? Remote[step].Direction.ToString() : "?"));
            if (NetPlay.IsHost == host)
            {
                Plugin.Teleport(Own, s_lever.transform.position);
                yield return Wait(0.3f);
                s_lever.OnEnterLever(); s_lever.OnGrabLever();
                yield return Wait(0.2f);
                if (right) s_lever.OnPushLeverRight(); else s_lever.OnPushLeverLeft();
                yield return Wait(0.4f);
                s_lever.OnReleaseLever(); s_lever.OnExitLever();
            }
            bool sawLetterbox = false, sawCamera = false, freeAgain = false;
            float start = Now, end = Now + 30f, freeSince = -1f;
            while (Now < end)
            {
                sawLetterbox |= Letterbox.ShowLetterboxes;
                sawCamera |= CutsceneGuard.CameraDistance() > 10f;
                NetTestHarness.BotButtons = OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                if ((StorySync.TakeoverMaskPublic() & 191) != 0 || Letterbox.ShowLetterboxes) freeSince = -1f;
                else if (freeSince < 0) freeSince = Now;
                if (Now - start >= 2f && freeSince >= 0 && Now - freeSince >= 3f) { freeAgain = true; break; }
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            Sample p = new Sample { Step = step + 1, Direction = (int)s_lever.Direction, SawLetterbox = sawLetterbox };
            Local[step + 1] = p; Send(p);
            yield return Barrier(h, step + 1);
            h.Check(sawLetterbox, "P3 " + label + ": story ran here");
            h.Check(freeAgain, "P3 " + label + ": free again");
            yield return Wait(2f);
            CheckRestored(h, label);
            h.Check(Remote.ContainsKey(step + 1) && (int)s_lever.Direction == Remote[step + 1].Direction,
                "P3 " + label + ": lever direction same on both");
            // Each end flips its own lever and plays the camera story natively (CutsceneGuard lever sync).
            if (NetPlay.IsHost == host) h.Check(CutsceneGuard.LeverPushesSent > sent, "P3 " + label + ": lever push sent");
            else h.Check(CutsceneGuard.LeverPushesPlayed > mirrored, "P3 " + label + ": partner played the lever");
            h.Note("P3 " + label + ": sawCamera=" + sawCamera);
            if (Failures(h) > failures) h.Note("P3 " + label + " state: " + Diag() + " lever=" + s_lever.Direction);
        }
        static IEnumerator ResetPedestal(TestHarness h, int step)
        {
            while (Now - s_lastActivation < 30.5f) yield return null;
            s_pedestal = FindPedestal();
            s_pedestal.CurrentState = GetAbilityPedestal.States.OutOfRange;
            if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, false);
            float end = Now + 20f;
            while (Ability && Now < end) yield return null;
            yield return Barrier(h, step);
            if (Ability || !Remote.ContainsKey(step) || Remote[step].Ability) h.Note("CUT reset state: partnerAbility=" + (Remote.ContainsKey(step) ? Remote[step].Ability.ToString() : "?") + " " + Diag());
            h.Check(!Ability && Remote.ContainsKey(step) && !Remote[step].Ability, "CUT reset: WallJump false on both");
            Plugin.Teleport(Own, s_pedestal.transform.position + Vector3.right * (NetPlay.IsHost ? -0.5f : 0.5f));
            end = Now + 5f;
            while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
            h.Check(Own.PlatformBehaviour.PlatformMovement.IsOnGround && Vector3.Distance(Own.Position, s_pedestal.transform.position) <= 1f,
                "CUT reset: beside pedestal on ground");
            s_pedestal.CurrentState = GetAbilityPedestal.States.OutOfRange;
            s_baseline = CutsceneGuard.LocalPedestalActivations;
            yield return Barrier(h, step + 1);
        }
        internal static bool SpiritFollows()
        {
            return Characters.Ori != null && Characters.Ori.CurrentState == Ori.State.Hovering
                && Vector3.Distance(Characters.Ori.Position, Own.Position) < 6f;   // it idles ~4.5 away beside a lever
        }
        static bool ScreenBlack() { return UI.Fader != null && UI.Fader.IsFadingInOrStay(); }
        internal static bool HudRestored()
        { return Game.UI.SeinUI.ShowUI && !Letterbox.ShowLetterboxes && P2Hud.GameHudOpacity() > 0.5f && !ScreenBlack(); }
        internal static bool CameraFree()
        {
            CameraPuppetController puppet = CutsceneGuard.CameraPuppet();
            // Locked = the view is held away from the player (an animator may keep a puppet tween that follows the player).
            return UI.Cameras.Current != null && CutsceneGuard.IsPlayerCameraTarget(UI.Cameras.Current.Target)
                && puppet != null && (puppet.Tween < 0.01f || CutsceneGuard.CameraDistance() < 10f);
        }
        internal static int OpenBoxes()
        {
            int n = 0;
            foreach (InteractiveMessageBox box in UnityEngine.Object.FindObjectsOfType<InteractiveMessageBox>()) if (box != null && box.isActiveAndEnabled) n++;
            return n;
        }
        internal static string Diag()
        {
            CameraPuppetController puppet = CutsceneGuard.CameraPuppet();
            Transform target = UI.Cameras.Current == null ? null : UI.Cameras.Current.Target;
            return "mask=" + StorySync.TakeoverMaskPublic() + " storyActive=" + StorySync.StoryActive
                + " target=" + (target == null ? "null" : target.name) + " own=" + (Own == null ? "null" : Own.name)
                + " puppet=" + (puppet == null ? "none" : ((puppet.CinematicPuppet == null ? "null" : puppet.CinematicPuppet.name) + "/" + puppet.Tween.ToString("F2")))
                + " spirit=" + (Characters.Ori == null ? "null" : Characters.Ori.CurrentState + "@" + Vector3.Distance(Characters.Ori.Position, Own.Position).ToString("F1"))
                + " showUI=" + Game.UI.SeinUI.ShowUI + " letterbox=" + Letterbox.ShowLetterboxes + " hud=" + P2Hud.GameHudOpacity().ToString("F2")
                + " camDist=" + CutsceneGuard.CameraDistance().ToString("F1") + " boxes=" + OpenBoxes()
                + " fader=" + (UI.Fader == null ? "null" : UI.Fader.CurrentState.ToString())
                + " saveReady=" + SaveSync.Ready + " inGameplay=" + NetPlay.LocalInGameplay() + " progressReady=" + ProgressSync.Ready
                + " loading=" + GameController.Instance.IsLoadingGame
                + " wallJump=" + Ability + " lockInput=" + GameController.Instance.LockInput + "/" + GameController.Instance.LockInputByAction;
        }
        static void CheckRestored(TestHarness h, string label)
        {
            if (!SpiritFollows() || !HudRestored() || !CameraFree()) h.Note("CUT " + label + " state: " + Diag());
            h.Check(SpiritFollows(), "CUT " + label + ": spirit follows own player");
            h.Check(HudRestored(), "CUT " + label + ": HUD restored");
            h.Check(CameraFree(), "CUT " + label + ": camera not locked");
        }
        static IEnumerator Pedestal(TestHarness h, string label, bool host, int step)
        {
            h.Note("CUT " + label + ": resetting wall-jump pedestal");
            yield return ResetPedestal(h, step);
            s_lastActivation = Now;
            if (NetPlay.IsHost == host) s_pedestal.ActivatePedestal();
            float end = Now + 25f, freeSince = -1f;
            while (Now < end)
            {
                bool played = CutsceneGuard.LocalPedestalActivations == s_baseline + 1 && Ability;
                if (!played || (StorySync.TakeoverMaskPublic() & 191) != 0) freeSince = -1f;
                else if (freeSince < 0) freeSince = Now;
                if (freeSince >= 0 && Now - freeSince >= 2f) break;
                // The ability tutorial box (InteractiveMessageBox) holds LockInput until jump is pressed: press it like a player.
                NetTestHarness.BotButtons = OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                yield return null;
            }
            if (freeSince < 0 || Now - freeSince < 2f) h.Note("CUT " + label + " not free: " + Diag());
            yield return Barrier(h, step + 2);
            h.Check(CutsceneGuard.LocalPedestalActivations == s_baseline + 1 && Remote.ContainsKey(step + 2)
                && Remote[step + 2].Plays == 1, "CUT " + label + ": cutscene played on both");
            h.Check(Ability && Remote.ContainsKey(step + 2) && Remote[step + 2].Ability, "CUT " + label + ": WallJump true on both");
            h.Check(freeSince >= 0 && Now - freeSince >= 2f, "CUT " + label + ": input free for 2s");
            yield return Wait(4f);
            CheckRestored(h, label);
            h.Note("CUT " + label + ": native=" + (CutsceneGuard.LocalPedestalActivations - s_baseline)
                + " sent=" + CutsceneGuard.PedestalPlaysSent + " received=" + CutsceneGuard.PedestalPlaysReceived
                + " played=" + CutsceneGuard.PedestalPlaysPlayed + " repairs=" + CutsceneGuard.Repairs);
        }
        public class BreakCameraAction : ActionMethod
        {
            public override void Perform(IContext context) { BreakCamera(); }
        }
        static void BreakCamera()
        {
            UI.Cameras.Current.Target = s_dummy.transform;
            CameraPuppetController puppet = CutsceneGuard.CameraPuppet();
            puppet.CinematicPuppet = s_dummy.transform; puppet.Tween = 1f;
        }
        static void BreakSpiritHud()
        {
            Characters.Ori.MoveOriToPosition(Own.Position + new Vector3(15, 6, 0), 0.3f);
            Game.UI.SeinUI.ShowUI = false; Letterbox.ShowLetterboxes = true;
        }
        // Close leftover tutorial boxes (press jump) and let the pedestal window / locks run out.
        internal static IEnumerator Settle()
        {
            float end = Now + 25f;
            while (Now < end && (OpenBoxes() > 0 || CutsceneGuard.PedestalWindow || (StorySync.TakeoverMaskPublic() & 191) != 0))
            {
                NetTestHarness.BotButtons = OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
        }
        static IEnumerator Repair(TestHarness h)
        {
            yield return Settle();
            float end = Now + 30f;
            while (StorySync.StoryActive && Now < end) yield return null;
            h.Check(!StorySync.StoryActive, "CUT repair: no previous story running");
            GameObject go = new GameObject("OriCoop P3 repair fixture");
            s_dummy = new GameObject("OriCoop P3 broken camera"); s_dummy.transform.position = Own.Position;
            ActionSequence fixture = go.AddComponent<ActionSequence>(); fixture.MoonGuid = new MoonGuid(FixtureId);
            LockPlayerInputAction input = go.AddComponent<LockPlayerInputAction>(); input.DurationPlayerCantMove = 0.6f;
            WaitAction wait = go.AddComponent<WaitAction>(); wait.Duration = 0.2f;
            WaitAction tail = go.AddComponent<WaitAction>(); tail.Duration = 0.8f;
            fixture.Actions.Add(input); fixture.Actions.Add(wait); fixture.Actions.Add(go.AddComponent<BreakCameraAction>()); fixture.Actions.Add(tail);
            try
            {
                BreakSpiritHud(); int starts = NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored;
                yield return Barrier(h, 41);
                if (NetPlay.IsHost) fixture.Perform(null);
                end = Now + 5f;
                while ((NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored) == starts && Now < end) yield return null;
                h.Check((NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored) > starts, "CUT repair: fixture story started");
                while (fixture.IsRunning && Now < end) yield return null;
                // Release the source's cinematic bit so its ordinary StoryEnd can be sent.
                if (NetPlay.IsHost)
                {
                    CameraPuppetController puppet = CutsceneGuard.CameraPuppet(); puppet.Tween = 0; puppet.CinematicPuppet = null;
                }
                end = Now + 15f;
                while (StorySync.StoryActive && Now < end) yield return null;
                if (StorySync.StoryActive) h.Note("CUT repair story still active: " + Diag());
                h.Check(!StorySync.StoryActive, "CUT repair: fixture story ended");
                yield return Barrier(h, 42);
                // Break everything after the story ended: only the watchdog can fix it now (camera allowed: story < 120 s ago).
                BreakSpiritHud(); BreakCamera(); int repairs = CutsceneGuard.Repairs;
                yield return Wait(6f);
                CheckRestored(h, "repair");
                h.Check(CutsceneGuard.Repairs >= repairs + 3, "CUT repair: watchdog repaired broken state");
            }
            finally { input.Stop(); UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(s_dummy); s_dummy = null; }
        }
        // Relay run (a7612ee): the guest's NetPlay.LocalInGameplay() stayed false after a half-run scene story and
        // the host's WallJump=false never reached it. Snapshots must not depend on it: force it false on the guest
        // (loading flag) while the host clears and restores WallJump.
        static IEnumerator ProgressWhileNotInGameplay(TestHarness h)
        {
            yield return Settle();
            bool before = Ability;
            if (!NetPlay.IsHost) GameController.Instance.IsLoadingGame = true;
            try
            {
                yield return Barrier(h, 6);
                if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, !before);
                float end = Now + 5f;
                while (Ability == before && Now < end) yield return null;
                h.Check(Ability != before, "CUT progress reaches the guest while it is not 'in gameplay' (WallJump " + before + " -> " + Ability + ")");
                yield return Barrier(h, 7);
            }
            finally { if (!NetPlay.IsHost) GameController.Instance.IsLoadingGame = false; }
            if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, before);
            float back = Now + 5f;
            while (Ability != before && Now < back) yield return null;
            yield return Barrier(h, 8);
        }
        static bool s_fadeCallback;
        // a86950a's black-fade repair cancelled any fade begun while the player was free after a story, so fades that
        // continue from their fade-in callback (SaveSync's checkpoint reload, deaths, doors) never continued.
        static IEnumerator FadeGuard(TestHarness h)
        {
            yield return Settle();
            yield return Wait(3.5f);   // free > 3 s, story activity < 120 s ago (Repair step just ran)
            s_fadeCallback = false;
            UI.Fader.Fade(0.25f, 0.1f, 0.25f, delegate { s_fadeCallback = true; }, null);
            float end = Now + 3f;
            while (!s_fadeCallback && Now < end) yield return null;
            h.Check(s_fadeCallback, "CUT fade: a running fade is not cancelled (its callback ran)");
            yield return Wait(1f);
        }
        static IEnumerator Reload(TestHarness h)
        {
            yield return Settle();
            if (NetPlay.IsHost) GameController.Instance.CreateCheckpoint();
            yield return Barrier(h, 51);
            // Free for > 3 s shortly after a story: the state in which a86950a's fade repair cancelled SaveSync's
            // checkpoint-reload fade (its callback never ran, the reload timed out and SaveSync switched itself off).
            yield return Wait(3.5f);
            BreakSpiritHud(); GameController.Instance.RestoreCheckpoint();
            yield return Wait(4f);
            float end = Now + 75f;
            while ((!SaveSync.Ready || Own == null || GameController.Instance.IsLoadingGame) && Now < end) yield return null;
            h.Check(SaveSync.Ready, "CUT reload: save sync ready again (checkpoint reload finished)");
            h.Check(SpiritFollows(), "CUT reload: spirit follows");
            h.Check(HudRestored(), "CUT reload: HUD restored");
            h.Check(Ability, "CUT reload: WallJump still true");
        }
        static PlayerCollisionTrigger s_gateTrigger;
        static Rect s_gateRect;
        static float s_gateFiredAt;
        static string GatePath(Component component)
        {
            string path = component.transform.name;
            for (Transform t = component.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }
        static PlayerCollisionTrigger FindGateTrigger()
        {
            foreach (PlayerCollisionTrigger trigger in Resources.FindObjectsOfTypeAll<PlayerCollisionTrigger>())
            {
                if (trigger == null || !GatePath(trigger).EndsWith("storySetups/storyTextWithTrigger/triggerCollider", StringComparison.Ordinal)) continue;
                for (Transform t = trigger.transform; t != null; t = t.parent)
                {
                    SceneRoot root = t.GetComponent<SceneRoot>();
                    if (root != null && root.MetaData != null && root.MetaData.SceneName == SceneName) return trigger;
                }
            }
            return null;
        }
        static string GateDiag()
        {
            string positions = "";
            foreach (Puppet puppet in Players.Remotes)
                positions += " seat" + puppet.Seat + "=" + (puppet.Sein == null ? "null" : puppet.Sein.Position.ToString());
            return "key=" + ForcedSync.LocalGateKey + " count=" + ForcedSync.LastCount + "/" + ForcedSync.LastTotal
                + " hold=" + ForcedSync.HoldProgress + " fires=" + ForcedSync.Fires + " forces=" + ForcedSync.Forces
                + " blocked=" + ForcedSync.Blocked + " pillars=" + ForcedSync.PillarsShown
                + " top=" + ForcedSync.TopText + " bottom=" + ForcedSync.BottomText
                + " own=" + (Own == null ? "null" : Own.Position.ToString())
                + " trigger=" + (s_gateTrigger == null ? "null" : s_gateTrigger.transform.position.ToString())
                + positions + " starts=" + StorySync.StartsSent + "/" + StorySync.StartsMirrored + " " + Diag();
        }
        static void GateCheck(TestHarness h, bool ok, string label)
        {
            h.Check(ok, "GATE " + label);
            if (!ok) h.Note("GATE " + label + ": " + GateDiag());
        }
        static void GateMove(bool inside)
        {
            float x = inside ? s_gateRect.center.x : s_gateRect.xMin - 10f;
            float z = s_gateTrigger.transform.position.z; RaycastHit hit;
            float y = Physics.Raycast(new Vector3(x, s_gateRect.yMax + 0.5f, z), Vector3.down, out hit, s_gateRect.height + 30f, -1, QueryTriggerInteraction.Ignore)
                ? hit.point.y + 0.5f : s_gateRect.yMin + 0.5f;
            Plugin.Teleport(Own, new Vector3(x, y, z));
        }
        static IEnumerator GateSetup(TestHarness h)
        {
            foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
            {
                if (scene.Scene != SceneName) continue;
                Vector2 center = scene.SceneBounds.center;
                Plugin.Teleport(Own, new Vector3(center.x, center.y, 0));
                Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false); break;
            }
            float end = Now + 20f;
            while (Now < end)
            {
                s_gateTrigger = FindGateTrigger();
                if (s_gateTrigger != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            GateCheck(h, s_gateTrigger != null, "setup trigger found");
            if (s_gateTrigger == null) yield break;
            GateCheck(h, s_gateTrigger.ActionToRun != null
                && GatePath(s_gateTrigger.ActionToRun).EndsWith("storySetups/storyTextWithTrigger/action", StringComparison.Ordinal), "setup story action found");
            s_gateRect = Utility.RectFromBounds(Utility.BoundsFromTransform(s_gateTrigger.transform));
            s_gateTrigger.Active = true; GateMove(false);
            end = Now + 5f;
            while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
            GateCheck(h, Own.PlatformBehaviour.PlatformMovement.IsOnGround && Own.Position.x < s_gateRect.xMin - 5f, "setup outside on ground");
            Publish(new Sample { Step = 401, Found = s_gateTrigger != null });
            yield return Barrier(h, 401);
            yield return Settle();
        }
        static IEnumerator GateHolds(TestHarness h)
        {
            int sent = StorySync.StartsSent, mirrored = StorySync.StartsMirrored, blocked = ForcedSync.Blocked;
            yield return Barrier(h, 411);
            if (!NetPlay.IsHost) GateMove(true);
            bool sawStory = false; float end = Now + 3f;
            while (Now < end)
            {
                sawStory |= Letterbox.ShowLetterboxes || StorySync.StartsSent != sent || StorySync.StartsMirrored != mirrored;
                yield return null;
            }
            GateCheck(h, !sawStory && StorySync.StartsSent == sent && StorySync.StartsMirrored == mirrored, "G1 no story before all here");
            if (!NetPlay.IsHost)
            {
                GateCheck(h, ForcedSync.LocalGateKey != null, "G1 local gate remembered");
                GateCheck(h, ForcedSync.LastCount == 1 && ForcedSync.LastTotal == 2, "G1 count 1/2");
                GateCheck(h, ForcedSync.PillarsShown, "G1 pillars shown");
                h.Note("GATE visuals " + ForcedSync.VisualDebug());
                // For review of the look (ward pillars, hint lines, Y ring): kept next to the result file.
                try { Application.CaptureScreenshot(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(h.ResultPath)), "gate-" + (NetPlay.IsHost ? "host" : "guest") + ".png")); }
                catch (Exception e) { h.Note("gate screenshot failed: " + e.Message); }
                GateCheck(h, !string.IsNullOrEmpty(ForcedSync.TopText) && !string.IsNullOrEmpty(ForcedSync.BottomText)
                    && ForcedSync.BottomText.Contains("1/2"), "G1 two hint lines with 1/2");
                GateCheck(h, ForcedSync.Blocked > blocked, "G1 native trigger blocked");
            }
        }
        static IEnumerator GateStarted(TestHarness h, string label, int starts, int fires, bool hostPulled)
        {
            bool sawLetterbox = false, pulled = false; float end = Now + 8f;
            while (Now < end)
            {
                sawLetterbox |= Letterbox.ShowLetterboxes;
                pulled |= Vector3.Distance(Own.Position, s_gateTrigger.transform.position) < 4f;
                bool started = NetPlay.IsHost ? StorySync.StartsSent >= starts + 1 : StorySync.StartsMirrored >= starts + 1 || sawLetterbox;
                if (started && ForcedSync.Fires >= fires + 1 && (!hostPulled || !NetPlay.IsHost || pulled)) break;
                yield return null;
            }
            GateCheck(h, NetPlay.IsHost ? StorySync.StartsSent == starts + 1 : StorySync.StartsMirrored == starts + 1 || sawLetterbox,
                label + " story started here");
            GateCheck(h, ForcedSync.Fires == fires + 1, label + " fired exactly once");
            if (hostPulled && NetPlay.IsHost) GateCheck(h, pulled, label + " host pulled within 4 units");
            s_gateFiredAt = Now;
        }
        static IEnumerator GateAllHere(TestHarness h)
        {
            int starts = NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored, fires = ForcedSync.Fires;
            yield return Barrier(h, 421);
            if (NetPlay.IsHost) GateMove(true);
            yield return GateStarted(h, "G2", starts, fires, false);
            GateCheck(h, ForcedSync.Fires == 1, "G2 first fire on this end");
            yield return Settle();
        }
        static IEnumerator GateReset(TestHarness h, int step)
        {
            yield return Settle();
            GateMove(false);
            float end = Now + 20f;
            while (StorySync.StoryActive && Now < end) yield return null;
            while (Now - s_gateFiredAt < 15.5f) yield return null;
            s_gateTrigger.Active = true;
            GateCheck(h, !StorySync.StoryActive && !Letterbox.ShowLetterboxes, "reset story settled");
            yield return Barrier(h, step);
        }
        static IEnumerator GateHold(TestHarness h, string label, int step)
        {
            yield return GateReset(h, step);
            int starts = NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored;
            int fires = ForcedSync.Fires, forces = ForcedSync.Forces;
            if (!NetPlay.IsHost) GateMove(true);
            float end = Now + 4f, peak = 0f;
            try
            {
                while (Now < end)
                {
                    if (!NetPlay.IsHost) NetTestHarness.BotButtons = P2Input.BTN_Y;
                    peak = Mathf.Max(peak, ForcedSync.HoldProgress);
                    if (!NetPlay.IsHost && ForcedSync.Forces > forces || NetPlay.IsHost && ForcedSync.Fires > fires) break;
                    yield return null;
                }
            }
            finally { Bot(); }
            if (!NetPlay.IsHost) GateCheck(h, peak >= 1f && ForcedSync.Forces >= forces + 1, label + " hold filled and force sent");
            yield return GateStarted(h, label, starts, fires, true);
            yield return Settle();
        }
        static IEnumerator GateRun(TestHarness h)
        {
            Local.Clear(); Bot(); NetPlay.SetSim(0, 0, 0, 0);
            try
            {
                yield return Step(h, 400, GateSetup(h));
                if (s_gateTrigger == null || !Remote.ContainsKey(401) || !Remote[401].Found) yield break;
                yield return Step(h, 410, GateHolds(h));
                yield return Step(h, 420, GateAllHere(h));
                yield return Step(h, 430, GateHold(h, "G3", 431));
                NetPlay.SetSim(100, 0, 0, 0);
                yield return Step(h, 440, GateHold(h, "G4 lag", 441));
            }
            finally { Bot(); NetPlay.SetSim(0, 0, 0, 0); }
        }
        public static IEnumerator Run(TestHarness h)
        {
            if (!s_registered) { NetPlay.On(BarrierMessage, Receive); s_registered = true; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_GATE") == "1") { yield return GateRun(h); yield break; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_KURO") == "1") { yield return NetKuroTest.Run(h); yield break; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_SCAN") == "1") { yield return NetStoryScan.Run(h); yield break; }
            // Keep Remote/Acknowledged: the partner's first barrier can arrive before this end reaches P3 (one run per
            // process, so nothing stale is in there).
            Local.Clear(); s_pedestal = null; s_lever = null; s_lastActivation = -31f;
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_SWEEP") == "1") { yield return NetStorySweep.Run(h); yield break; }
            bool original = Ability;
            bool leverOnly = Environment.GetEnvironmentVariable("ORICOOP_P3_LEVER") == "1";
            try
            {
                Bot(); NetPlay.SetSim(0, 0, 0, 0); yield return Barrier(h, 1);
                if (!NetPlay.IsHost && Remote.ContainsKey(1)) original = Remote[1].Ability;
                if (!leverOnly)
                {
                    yield return Step(h, 5, ProgressWhileNotInGameplay(h));
                    yield return Step(h, 3, Setup(h));
                    // T-073: on a busy PC the pedestal's scene streamed out on the guest right after step 3; this end then
                    // left P3 silently and the host waited 20 s at every later barrier. Find it again (reloading its
                    // scene), and fail loudly if it stays gone.
                    if (s_pedestal == null && Remote.ContainsKey(2) && Remote[2].Found) yield return Refind(h);
                    if (s_pedestal == null || !Remote.ContainsKey(2) || !Remote[2].Found)
                    {
                        h.Check(false, "CUT wall-jump pedestal still loaded before the pedestal steps (local=" + (s_pedestal != null)
                            + " partner=" + (Remote.ContainsKey(2) && Remote[2].Found) + ")");
                        yield break;
                    }
                    yield return Step(h, 10, Pedestal(h, "host", true, 11));
                    yield return Step(h, 20, Pedestal(h, "guest", false, 21));
                    NetPlay.SetSim(100, 0, 0, 0);   // ~200 ms round trip
                    yield return Step(h, 60, Pedestal(h, "lag", true, 61));
                    NetPlay.SetSim(0, 0, 0, 0);
                }
                // Lever (playtest 19:07) is opt-in: ORICOOP_P3_LEVER=1 (+ ORICOOP_HARNESS_SEC). After the pedestal
                // steps the shared scene state made it flaky; on its own it is deterministic.
                if (!leverOnly) goto LeverDone;
                yield return Step(h, 80, LeverSetup(h, 81));
                yield return Step(h, 84, LeverPull(h, "lever guest right", false, true, 85));
                yield return Step(h, 88, LeverPull(h, "lever host left", true, false, 89));
                NetPlay.SetSim(100, 0, 0, 0);
                yield return Step(h, 92, LeverPull(h, "lag lever guest right", false, true, 93));
                NetPlay.SetSim(0, 0, 0, 0);
                // Playtest 19:07: the host's mirror ran behind the guest's lever story and was stopped inside its
                // FadeToBlack..FadeFromBlack window -> black screen. Make the host's mirror start 1.75 s late.
                yield return Step(h, 96, LeverPull(h, "lever host left (reset)", true, false, 97));
                if (NetPlay.IsHost) StorySync.TestResolveDelay = 1.75f;
                yield return Step(h, 100, LeverPull(h, "late mirror lever guest right", false, true, 101));
                StorySync.TestResolveDelay = 0f;
                if (leverOnly) yield break;
            LeverDone:
                // Checkpoint restore last: it reloads the area (the lever needs the live scene state).
                yield return Step(h, 40, Repair(h));
                yield return Step(h, 45, FadeGuard(h));
                yield return Step(h, 50, Reload(h));
                if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, original);
                float end = Now + 10f;
                while (Ability != original && Now < end) yield return null;
                h.Check(Ability == original, "CUT original WallJump restored");
                yield return Barrier(h, 70);
            }
            finally
            {
                if (NetPlay.IsHost && Own != null) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, original);
                Bot(); NetPlay.SetSim(0, 0, 0, 0); StorySync.TestResolveDelay = 0f;
            }
        }
    }
}
