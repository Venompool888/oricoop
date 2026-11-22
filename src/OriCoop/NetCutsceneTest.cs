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
            string label = step >= 600 ? "LOOK " : step >= 400 ? "GATE " : "CUT ";
            h.Check(Remote.ContainsKey(step), label + "barrier " + step + " received");
            h.Check(Acknowledged.Contains(step), label + "barrier " + step + " acknowledged");
            if (Remote.ContainsKey(step)) h.Check(!Remote[step].Failed, label + "partner step " + step + " completed");
            if (step >= 400 && step < 600 && (!Remote.ContainsKey(step) || !Acknowledged.Contains(step) || Remote[step].Failed))
                h.Note("GATE barrier " + step + ": " + GateDiag());
            if (NetPlay.Connected) NetPlay.Peer.Flush();
            yield return Wait(0.25f);
        }
        internal static IEnumerator Step(TestHarness h, int step, IEnumerator routine)
        {
            int failures = Failures(h); bool failed = false;
            string label = step >= 600 ? "LOOK " : step >= 400 ? "GATE " : "CUT ";
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
                    if (step >= 400 && step < 600 && (failed || Failures(h) > failures)) h.Note("GATE step " + step + ": " + GateDiag());
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
        // ---- T-194 (playtest 4, Spirit Cavern B): the real grab. LeverPull above calls the Lever's own events and never
        // goes through the player's SeinLever, so it could not see a player left attached to the lever. Here the bot
        // holds Grab and pushes the stick like a player; after the lever's story everybody must be able to walk away
        // from it (ticket: "all players move normally after the lever and its cutscene"). A player the lever still
        // holds gets at most 0.8 + 0.4 from it (SeinLever pulls Ori back to 0.8 beside the lever every frame).
        const float LeverWalkGoal = 5f, LeverWalkFree = 4f;
        static readonly System.Reflection.FieldInfo F_leverPulls = AccessTools.Field(typeof(SeinLever), "m_moveSeinPosition");
        static bool HeldByLever()
        {
            SeinLever own = Own == null ? null : Own.Abilities.Lever;
            return own != null && (own.IsUsingLever || (bool)F_leverPulls.GetValue(own));
        }
        static string LeverState()
        {
            SeinLever own = Own == null ? null : Own.Abilities.Lever;
            return "lever=" + s_lever.Direction + " grabbed=" + s_lever.IsGrabbed + " inRange=" + s_lever.InRange
                + " using=" + (own != null && own.IsUsingLever) + " pulls=" + (own != null && (bool)F_leverPulls.GetValue(own))
                + " dx=" + (Own.Position.x - s_lever.transform.position.x).ToString("F2") + " kept=" + CutsceneGuard.LeverHoldsKept;
        }
        static IEnumerator BesideLever()
        {
            Bot(); P2Input.BotRT = false;
            Plugin.Teleport(Own, s_lever.transform.position + Vector3.right * (NetPlay.IsHost ? -1.2f : 1.2f));
            float end = Now + 5f;
            while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
            yield return Wait(0.3f);   // SeinLever.EnterLever needs a fixed step inside the radius
        }
        static IEnumerator LeverRealPull(TestHarness h, string label, bool host, int step)
        {
            int failures = Failures(h); bool puller = NetPlay.IsHost == host;
            string who = puller ? "the player who pulled" : "the partner";
            yield return Settle();
            yield return BesideLever();
            int before = (int)s_lever.Direction;   // before the barrier: the partner may push as soon as it is through
            yield return Barrier(h, step);
            if (puller)
            {
                P2Input.BotRT = true;   // hold Grab: SeinLever.GrabLever
                float limit = Now + 4f;
                while (Now < limit && !Own.Abilities.Lever.IsUsingLever) yield return null;
                h.Check(Own.Abilities.Lever.IsUsingLever, "T-194 " + label + ": the Grab button grabs the lever (" + LeverState() + ")");
                NetTestHarness.BotLX = before == (int)Lever.LeverDirections.Right ? -1f : 1f;
                limit = Now + 4f;
                while (Now < limit && (int)s_lever.Direction == before) yield return null;
                h.Check((int)s_lever.Direction != before, "T-194 " + label + ": the stick pushes the lever (" + LeverState() + ")");
                // A player lets go during the lever's cutscene (input is locked there): nothing is pressed after it.
                yield return Wait(0.3f);
                P2Input.BotRT = false; NetTestHarness.BotLX = 0f;
            }
            bool sawLetterbox = false, freeAgain = false;
            float start = Now, end = Now + 30f, freeSince = -1f;
            while (Now < end)
            {
                sawLetterbox |= Letterbox.ShowLetterboxes;
                NetTestHarness.BotButtons = OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                if ((StorySync.TakeoverMaskPublic() & 191) != 0 || Letterbox.ShowLetterboxes) freeSince = -1f;
                else if (freeSince < 0) freeSince = Now;
                if (Now - start >= 2f && freeSince >= 0 && Now - freeSince >= 1.5f) { freeAgain = true; break; }
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            Sample p = new Sample { Step = step + 1, Direction = (int)s_lever.Direction, SawLetterbox = sawLetterbox };
            Local[step + 1] = p; Send(p);
            yield return Barrier(h, step + 1);
            h.Check(sawLetterbox, "T-194 " + label + ": the lever's story ran here");
            h.Check(freeAgain, "T-194 " + label + ": free again after the story");
            h.Check(Remote.ContainsKey(step + 1) && (int)s_lever.Direction == Remote[step + 1].Direction && (int)s_lever.Direction != before,
                "T-194 " + label + ": lever moved, same direction on both");
            string after = LeverState();
            // The check itself: walk away from the lever (host to the left, guest to the right; flat ground both ways).
            float leverX = s_lever.transform.position.x, far = Mathf.Abs(Own.Position.x - leverX);
            NetTestHarness.BotLX = NetPlay.IsHost ? -1f : 1f;
            end = Now + 3f;
            while (Now < end && far < LeverWalkGoal) { far = Mathf.Max(far, Mathf.Abs(Own.Position.x - leverX)); yield return null; }
            NetTestHarness.BotLX = 0f;
            h.Check(far >= LeverWalkFree, "T-194 " + label + ": " + who + " walks away from the lever ("
                + far.ToString("F2") + " >= " + LeverWalkFree.ToString("F0") + "; before the walk: " + after + ")");
            h.Check(!HeldByLever(), "T-194 " + label + ": the lever no longer holds " + who + " (" + LeverState() + ")");
            if (Failures(h) > failures) h.Note("T-194 " + label + " state: " + Diag() + " " + LeverState());
        }
        // The partner pushed the lever while this player stood beside it: the lever must still take a grab here.
        static IEnumerator LeverGrabAgain(TestHarness h, string label, bool host, int step)
        {
            yield return Settle();
            yield return BesideLever();
            yield return Barrier(h, step);
            if (NetPlay.IsHost != host) yield break;
            P2Input.BotRT = true;
            float limit = Now + 4f;
            while (Now < limit && !Own.Abilities.Lever.IsUsingLever) yield return null;
            h.Check(Own.Abilities.Lever.IsUsingLever, "T-194 " + label + ": the lever takes a grab after the partner's push (" + LeverState() + ")");
            P2Input.BotRT = false;
            limit = Now + 3f;
            while (Now < limit && HeldByLever()) yield return null;
            h.Check(!HeldByLever(), "T-194 " + label + ": letting go of Grab releases the lever (" + LeverState() + ")");
        }
        static IEnumerator LeverHoldRun(TestHarness h)
        {
            yield return Step(h, 120, LeverSetup(h, 121));
            if (s_lever == null) yield break;
            try
            {
                yield return Step(h, 124, LeverRealPull(h, "guest pulls", false, 125));
                yield return Step(h, 128, LeverRealPull(h, "host pulls", true, 129));   // also puts the lever back
                yield return Step(h, 132, LeverGrabAgain(h, "guest", false, 133));
            }
            finally { Bot(); P2Input.BotRT = false; }
        }
        static IEnumerator ResetPedestal(TestHarness h, int step)
        {
            while (Now - s_lastActivation < 30.5f) yield return null;
            s_pedestal = FindPedestal();
            if (s_pedestal == null) yield return Refind(h);   // its scene can stream out during the 30 s wait (T-073 hotfix)
            if (s_pedestal == null) { h.Check(false, "CUT reset: wall-jump pedestal still loaded"); yield break; }
            s_pedestal.CurrentState = GetAbilityPedestal.States.OutOfRange;
            if (NetPlay.IsHost) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, false);
            float end = Now + 20f;
            while (Ability && Now < end) yield return null;
            yield return Barrier(h, step);
            if (Ability || !Remote.ContainsKey(step) || Remote[step].Ability) h.Note("CUT reset state: partnerAbility=" + (Remote.ContainsKey(step) ? Remote[step].Ability.ToString() : "?") + " " + Diag());
            h.Check(!Ability && Remote.ContainsKey(step) && !Remote[step].Ability, "CUT reset: WallJump false on both");
            // The barrier can take a while on a busy PC; the pedestal's scene may have streamed out meanwhile (step 10
            // "threw NullReferenceException at get_transform", 2026-11-18, also before T-073).
            if (s_pedestal == null) yield return Refind(h);
            if (s_pedestal == null) { h.Check(false, "CUT reset: wall-jump pedestal still loaded after the barrier"); yield break; }
            Plugin.Teleport(Own, s_pedestal.transform.position + Vector3.right * (NetPlay.IsHost ? -0.5f : 0.5f));
            end = Now + 5f;
            while (!Own.PlatformBehaviour.PlatformMovement.IsOnGround && Now < end) yield return null;
            h.Check(s_pedestal != null && Own.PlatformBehaviour.PlatformMovement.IsOnGround && Vector3.Distance(Own.Position, s_pedestal.transform.position) <= 1f,
                "CUT reset: beside pedestal on ground");
            if (s_pedestal == null) yield break;
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
                // T-137: a scene camera framing zone sets the cinematic puppet every frame on purpose (still < 120 s
                // after the story): the watchdog may clear it once, then must leave it alone (it used to every 2 s).
                GameObject framing = new GameObject("OriCoop T-137 framing zone");
                framing.transform.position = Own.Position + new Vector3(25f, 0f, 0f);
                int puppetRepairs = CutsceneGuard.PuppetRepairs, driven = CutsceneGuard.PuppetGate.DrivenDetections;
                try
                {
                    float drive = Now + 7f;
                    while (Now < drive)
                    {
                        CameraPuppetController framed = CutsceneGuard.CameraPuppet();
                        if (framed != null) { framed.CinematicPuppet = framing.transform; framed.Tween = 1f; }
                        yield return null;
                    }
                }
                finally
                {
                    CameraPuppetController framed = CutsceneGuard.CameraPuppet();
                    if (framed != null) { framed.Tween = 0; framed.CinematicPuppet = null; }
                    UnityEngine.Object.Destroy(framing);
                }
                h.Check(CutsceneGuard.PuppetRepairs - puppetRepairs <= 1, "CUT repair: a scene-driven camera puppet is cleared at most once in 7 s (T-137, "
                    + (CutsceneGuard.PuppetRepairs - puppetRepairs) + " repairs, driven seen " + (CutsceneGuard.PuppetGate.DrivenDetections - driven) + ")");
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
        static Vector3 GatePos(bool inside, float dx)
        {
            float x = (inside ? s_gateRect.center.x : s_gateRect.xMin - 10f) + dx;
            float z = s_gateTrigger.transform.position.z; RaycastHit hit;
            float y = Physics.Raycast(new Vector3(x, s_gateRect.yMax + 0.5f, z), Vector3.down, out hit, s_gateRect.height + 30f, -1, QueryTriggerInteraction.Ignore)
                ? hit.point.y + 0.5f : s_gateRect.yMin + 0.5f;
            return new Vector3(x, y, z);
        }
        static void GateMove(bool inside) { Plugin.Teleport(Own, GatePos(inside, 0f)); }
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
        // T-110: an extra local player on one PC. The gate waits for every player, and each PC reports each of its seats.
        static int s_extraSeat;
        static IEnumerator GateAddExtra(TestHarness h, bool mine, string label, int step)
        {
            yield return Barrier(h, step);
            HashSet<int> before = new HashSet<int>();
            foreach (Puppet p in Players.Remotes) before.Add(p.Seat);
            if (mine) GateCheck(h, NetPlay.AddLocalSeat(), label + " extra local player added");
            s_extraSeat = 0; float end = Now + 20f;
            while (s_extraSeat == 0 && Now < end)
            {
                foreach (Puppet p in Players.Remotes) if (!before.Contains(p.Seat) && p.Sein != null) s_extraSeat = p.Seat;
                yield return null;
            }
            GateCheck(h, s_extraSeat != 0, label + " the extra player's Ori appears here");
            yield return Barrier(h, step + 1);
        }
        static IEnumerator GateDropExtra(TestHarness h, bool mine, int step)
        {
            yield return Barrier(h, step);
            int seat = s_extraSeat;
            if (mine && seat != 0) NetPlay.ReleaseLocalSeat(seat);
            float end = Now + 15f;
            while (Players.OriOf(seat) != null && Now < end) yield return null;
            GateCheck(h, seat == 0 || Players.OriOf(seat) == null, "extra player " + seat + " released");
            s_extraSeat = 0;
            yield return Barrier(h, step + 1);
        }
        static float GateDx { get { return Mathf.Min(1f, s_gateRect.width * 0.25f); } }
        // G5: the guest has an extra local player; all three walk in: the gate fires by itself (was stuck at 2/3).
        static IEnumerator GateGuestExtra(TestHarness h)
        {
            yield return GateAddExtra(h, !NetPlay.IsHost, "G5", 451);
            if (s_extraSeat == 0) yield break;
            yield return GateReset(h, 453);
            bool mine = Players.IsLocal(s_extraSeat);
            if (mine) Plugin.Teleport(Players.OriOf(s_extraSeat), GatePos(false, 1.5f));
            int starts = NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored, fires = ForcedSync.Fires, forces = ForcedSync.Forces;
            yield return Barrier(h, 454);
            GateMove(true);
            if (mine) Plugin.Teleport(Players.OriOf(s_extraSeat), GatePos(true, GateDx));
            int total = 0; float end = Now + 8f;
            while (ForcedSync.Fires == fires && Now < end) { total = Mathf.Max(total, ForcedSync.LastTotal); yield return null; }
            h.Note("GATE5 total=" + total + " fires=" + (ForcedSync.Fires - fires) + " forces=" + (ForcedSync.Forces - forces) + " " + GateDiag());
            GateCheck(h, total == 3 || ForcedSync.Fires > fires && total == 0, "G5 the gate counts three players (host, guest, guest's extra)");
            yield return GateStarted(h, "G5", starts, fires, false);
            GateCheck(h, ForcedSync.Forces == forces, "G5 fired without anyone forcing it");
            yield return Settle();
            yield return GateDropExtra(h, !NetPlay.IsHost, 456);
        }
        // G6: the host has an extra local player that stays outside: the gate waits (was firing at 2/2); the host forces
        // and its extra player is pulled into the area.
        static IEnumerator GateHostExtra(TestHarness h)
        {
            yield return GateAddExtra(h, NetPlay.IsHost, "G6", 461);
            if (s_extraSeat == 0) yield break;
            yield return GateReset(h, 463);
            bool mine = Players.IsLocal(s_extraSeat);
            if (mine) Plugin.Teleport(Players.OriOf(s_extraSeat), GatePos(false, -1.5f));
            int starts = NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored, fires = ForcedSync.Fires, forces = ForcedSync.Forces;
            yield return Barrier(h, 464);
            GateMove(true);
            float end = Now + 3f; int total = 0;
            while (Now < end) { total = Mathf.Max(total, ForcedSync.LastTotal); yield return null; }
            h.Note("GATE6 wait total=" + total + " count=" + ForcedSync.LastCount + " fires=" + (ForcedSync.Fires - fires) + " " + GateDiag());
            GateCheck(h, ForcedSync.Fires == fires, "G6 no fire while the host's extra player is outside");
            GateCheck(h, total == 3 && ForcedSync.LastCount == 2, "G6 the gate shows 2/3");
            yield return Barrier(h, 465);
            end = Now + 4f;
            try
            {
                while (Now < end && ForcedSync.Fires == fires)
                {
                    if (NetPlay.IsHost) NetTestHarness.BotButtons = P2Input.BTN_Y;
                    yield return null;
                }
            }
            finally { Bot(); }
            yield return GateStarted(h, "G6", starts, fires, false);
            if (mine)
            {
                SeinCharacter extra = Players.OriOf(s_extraSeat);
                float distance = extra == null ? -1f : Vector3.Distance(extra.Position, s_gateTrigger.transform.position);
                h.Note("GATE6 extra distance=" + distance);
                GateCheck(h, extra != null && distance < 4f, "G6 the host's extra player was pulled into the area");
            }
            yield return Settle();
            yield return GateDropExtra(h, NetPlay.IsHost, 466);
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
                NetPlay.SetSim(0, 0, 0, 0);
                yield return Step(h, 450, GateGuestExtra(h));
                yield return Step(h, 460, GateHostExtra(h));
            }
            finally { Bot(); NetPlay.SetSim(0, 0, 0, 0); }
        }
        static bool LookPlaying(SeinCharacter sein, TextureAnimationWithTransitions anim)
        {
            return sein != null && sein.Controller.IsPlayingAnimation
                && sein.PlatformBehaviour.Visuals.Animation.Animator.CurrentTextureAnimationTransitions == anim;
        }
        static IEnumerator LookCase(TestHarness h)
        {
            int failures = Failures(h);
            yield return Settle();
            Publish(601, false);
            yield return Barrier(h, 601);
            TextureAnimationWithTransitions anim = Characters.Sein.Abilities.LookUp.LookUpAnimation;
            GameObject go = null;
            PerformingAction action = null;
            try
            {
                if (NetPlay.IsHost)
                {
                    go = new GameObject("OriCoop P3 look fixture");
                    Type type = AccessTools.TypeByName("PlayCharacterAnimation");
                    action = (PerformingAction)go.AddComponent(type);
                    AccessTools.Field(type, "Animation").SetValue(action, anim);
                    action.Perform(null);
                }
                SeinCharacter host = NetPlay.IsHost ? Own : Players.First.Sein;
                SeinCharacter guest = NetPlay.IsHost ? Players.First.Sein : Own;
                float end = Now + 2f;
                while (Now < end && (!LookPlaying(host, anim) || !LookPlaying(guest, anim))) yield return null;
                h.Check(LookPlaying(host, anim), "LOOK host's Ori plays it");
                h.Check(LookPlaying(guest, anim), "LOOK guest's Ori plays it");
                Publish(602, Failures(h) > failures);
                yield return Barrier(h, 602);
                if (NetPlay.IsHost) action.Stop();
                end = Now + 2f;
                while (Now < end)
                {
                    bool stopped = Own != null && !Own.Controller.IsPlayingAnimation;
                    foreach (Puppet puppet in Players.Remotes)
                        stopped &= puppet.Sein != null && !puppet.Sein.Controller.IsPlayingAnimation;
                    if (stopped) break;
                    yield return null;
                }
                h.Check(Own != null && !Own.Controller.IsPlayingAnimation, "LOOK own Ori stopped");
                foreach (Puppet puppet in Players.Remotes)
                    h.Check(puppet.Sein != null && !puppet.Sein.Controller.IsPlayingAnimation, "LOOK puppet seat " + puppet.Seat + " stopped");
                Publish(603, Failures(h) > failures);
                yield return Barrier(h, 603);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
        // The animation key CutsceneAnimSync sends (same formula as CutsceneAnimSync.AnimationKey, which is private).
        static string LookKey(TextureAnimationWithTransitions anim)
        {
            return (string)AccessTools.Method(typeof(CutsceneAnimSync), "AnimationKey").Invoke(null, new object[] { anim });
        }
        // Loaded animations with frames, longest name first (both ends load the same character animations).
        static List<TextureAnimationWithTransitions> LookCandidates()
        {
            List<TextureAnimationWithTransitions> list = new List<TextureAnimationWithTransitions>();
            foreach (TextureAnimationWithTransitions a in Resources.FindObjectsOfTypeAll<TextureAnimationWithTransitions>())
                if (a != null && a.Animation != null && a.Animation.FrameGuids.Count > 0) list.Add(a);
            list.Sort(delegate(TextureAnimationWithTransitions x, TextureAnimationWithTransitions y)
            { int c = y.name.Length.CompareTo(x.name.Length); return c != 0 ? c : string.CompareOrdinal(x.name, y.name); });
            return list;
        }
        // Review suspect (2026-11-19): the key is name + "|" + MoonGuid ("A B C D", ~43 chars) + "|" + count, and
        // AnimWire.Write throws above 64 chars, inside the Harmony postfix of the game's PlayCharacterAnimation.Perform.
        // The host plays the loaded animation with the longest name through the real action: the action must not throw
        // and the guest's Ori must play the same animation.
        static IEnumerator LookLongCase(TestHarness h)
        {
            int failures = Failures(h);
            yield return Settle();
            List<TextureAnimationWithTransitions> candidates = LookCandidates();
            int over = 0; string longest = "";
            foreach (TextureAnimationWithTransitions a in candidates)
            {
                string key = LookKey(a);
                if (key.Length > 64) over++;
                if (longest.Length < 300 && (key.Length > 64 || longest == "")) longest += a.name + "(" + key.Length + ") ";
            }
            h.Note("LOOK long: " + candidates.Count + " loaded animations, " + over + " with a key over 64 chars: " + longest);
            TextureAnimationWithTransitions anim = candidates.Count > 0 ? candidates[0] : null;
            // Read before the barrier: the host passes it first and sends at once, so the guest can receive the
            // animation before it leaves the barrier.
            int received = CutsceneAnimSync.Received, errors = CutsceneAnimSync.Errors;
            Publish(new Sample { Step = 611, Found = anim != null, Plays = anim == null ? 0 : anim.name.Length });
            yield return Barrier(h, 611);
            GameObject go = null; PerformingAction action = null; Exception thrown = null;
            try
            {
                if (NetPlay.IsHost && anim != null)
                {
                    go = new GameObject("OriCoop P3 long look fixture");
                    Type type = AccessTools.TypeByName("PlayCharacterAnimation");
                    action = (PerformingAction)go.AddComponent(type);
                    AccessTools.Field(type, "Animation").SetValue(action, anim);
                    try { action.Perform(null); } catch (Exception e) { thrown = e; }
                    h.Check(thrown == null, "LOOK long: the game's PlayCharacterAnimation ran without an exception ("
                        + anim.name + ", key " + LookKey(anim).Length + " chars" + (thrown == null ? "" : ", " + thrown.GetType().Name + ": " + thrown.Message) + ")");
                    h.Check(CutsceneAnimSync.Errors == errors, "LOOK long: the animation was sent without a sync error (key fits the wire)");
                }
                int length = NetPlay.IsHost ? (anim == null ? 0 : anim.name.Length) : (Remote.ContainsKey(611) ? Remote[611].Plays : 0);
                float end = Now + 2f; bool own = false;
                while (Now < end)
                {
                    TextureAnimationWithTransitions current = Own == null ? null : Own.PlatformBehaviour.Visuals.Animation.Animator.CurrentTextureAnimationTransitions;
                    own = Own != null && Own.Controller.IsPlayingAnimation && current != null && current.name.Length == length
                        && (NetPlay.IsHost ? current == anim : CutsceneAnimSync.Received > received);
                    if (own) break;
                    yield return null;
                }
                h.Check(own, NetPlay.IsHost ? "LOOK long: host's Ori plays the long animation" : "LOOK long: guest's Ori plays the host's long animation");
                Publish(612, Failures(h) > failures);
                yield return Barrier(h, 612);
                if (NetPlay.IsHost && action != null) action.Stop();
                end = Now + 2f;
                while (Now < end && Own != null && Own.Controller.IsPlayingAnimation) yield return null;
                if (Own != null && Own.Controller.IsPlayingAnimation) Own.Controller.StopAnimation();
                Publish(613, Failures(h) > failures);
                yield return Barrier(h, 613);
            }
            finally { UnityEngine.Object.Destroy(go); }
        }
        static IEnumerator LookRun(TestHarness h)
        {
            Local.Clear(); Bot();
            yield return Step(h, 600, LookCase(h));
            yield return Step(h, 610, LookLongCase(h));
        }
        public static IEnumerator Run(TestHarness h)
        {
            if (!s_registered) { NetPlay.On(BarrierMessage, Receive); s_registered = true; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_GATE") == "1") { yield return GateRun(h); yield break; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_LOOK") == "1") { yield return LookRun(h); yield break; }
            string kuro = Environment.GetEnvironmentVariable("ORICOOP_P3_KURO");
            if (kuro == "1" || kuro == "chain") { yield return NetKuroTest.Run(h); yield break; }
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_SCAN") == "1") { yield return NetStoryScan.Run(h); yield break; }
            // Keep Remote/Acknowledged: the partner's first barrier can arrive before this end reaches P3 (one run per
            // process, so nothing stale is in there).
            Local.Clear(); s_pedestal = null; s_lever = null; s_lastActivation = -31f;
            if (Environment.GetEnvironmentVariable("ORICOOP_P3_SWEEP") == "1") { yield return NetStorySweep.Run(h); yield break; }
            bool original = Ability;
            // ORICOOP_P3_LEVER=1: only the opt-in lever steps; =hold: only the T-194 real-grab steps (they also close CUT).
            string leverMode = Environment.GetEnvironmentVariable("ORICOOP_P3_LEVER");
            bool leverOnly = leverMode == "1";
            try
            {
                Bot(); NetPlay.SetSim(0, 0, 0, 0); yield return Barrier(h, 1);
                if (leverMode == "hold") { yield return LeverHoldRun(h); yield break; }
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
                // T-194: a real lever grab, after the reload above (fresh scene state, nothing left of the pedestal).
                yield return LeverHoldRun(h);
            }
            finally
            {
                if (NetPlay.IsHost && Own != null) Own.PlayerAbilities.SetAbility(AbilityType.WallJump, original);
                Bot(); P2Input.BotRT = false; NetPlay.SetSim(0, 0, 0, 0); StorySync.TestResolveDelay = 0f;
            }
        }
    }
}
