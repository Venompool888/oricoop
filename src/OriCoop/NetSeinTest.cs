using System;
using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-196 (#279) regression, opt-in ORICOOP_NET_ONLY=P3 ORICOOP_P3_SEIN=1: picking up Sein in sunkenGladesOriRoom
    // (playtest 4). The pickup is a key-press trigger (PlayerCollisionStayTrigger + ButtonPressedCondition) behind the
    // T-040 gate; its script (activateSequence, 52 actions, about 22 s) locks input, plays two character animations and
    // a story text. The test re-arms the already-played pickup on both PCs and plays it like the playtest:
    //   1. the guest presses the pickup key alone: no story, the guest reads the wait hint (1/2);
    //   2. the host arrives later and only stands in the area: the story starts (no key held, no second press);
    //   3. the guest dismisses the story text a few seconds before the host (as the players did);
    //   4. when the script has ended on a PC, its Ori is free within 1.5 s and walks.
    // Before the fix: step 2 never started the story (the gate's fire re-checked the key 0.6 s later, then the area was
    // dead for 15 s), and after the story the host stood in the idleListen character animation for 55 s.
    internal static class NetSeinTest
    {
        const string Scene = "sunkenGladesOriRoom";
        const string TriggerPath = "findingOri/seinInterestZone/trigger";
        static PlayerCollisionStayTrigger s_trigger;
        static ActionSequence s_sequence;
        static Rect s_rect;
        static float Now { get { return Time.realtimeSinceStartup; } }
        static string Who { get { return NetPlay.IsHost ? "host" : "guest"; } }

        static string PathOf(Component c)
        {
            string path = c.name;
            for (Transform t = c.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }
        static bool InScene(Component c)
        {
            for (Transform t = c.transform; t != null; t = t.parent)
            {
                SceneRoot root = t.GetComponent<SceneRoot>();
                if (root != null) return root.MetaData != null && root.MetaData.SceneName == Scene;
            }
            return false;
        }
        static PlayerCollisionStayTrigger Find()
        {
            foreach (PlayerCollisionStayTrigger t in Resources.FindObjectsOfTypeAll<PlayerCollisionStayTrigger>())
                if (t != null && PathOf(t).EndsWith(TriggerPath, StringComparison.Ordinal) && InScene(t)) return t;
            return null;
        }
        static string Diag()
        {
            SeinCharacter own = NetCutsceneTest.Own;
            return "running=" + (s_sequence != null && s_sequence.IsRunning) + " index=" + (s_sequence != null ? s_sequence.Index : -1)
                + " stories=" + StorySync.RealStories + " mask=" + StorySync.TakeoverMaskPublic()
                + " animation=" + (own != null && own.Controller != null && own.Controller.IsPlayingAnimation)
                + " letterbox=" + Letterbox.ShowLetterboxes + " pos=" + (own != null ? own.Position.ToString() : "no-ori")
                + " gate=" + ForcedSync.LastCount + "/" + ForcedSync.LastTotal + " fires=" + ForcedSync.Fires + " adopted=" + ForcedSync.Adopted
                + " fizzles=" + ForcedSync.Fizzles + " kept=" + WorldSync.SequenceProgressKept + " boxes=" + NetCutsceneTest.OpenBoxes();
        }
        static void Check(TestHarness h, bool ok, string label)
        {
            h.Check(ok, "SEIN " + label);
            if (!ok) h.Note("SEIN " + label + ": " + Diag());
        }
        static bool FreeNow(SeinCharacter own)
        {
            return (StorySync.TakeoverMaskPublic() & 191) == 0 && own != null && own.Controller != null
                && !own.Controller.IsPlayingAnimation && !Letterbox.ShowLetterboxes && NetCutsceneTest.OpenBoxes() == 0;
        }
        // Stand on the ground at x (dx from the middle of the pickup area).
        static void Place(float dx)
        {
            float x = s_rect.center.x + dx, z = s_trigger.transform.position.z; RaycastHit hit;
            float y = Physics.Raycast(new Vector3(x, s_rect.yMax + 1.5f, z), Vector3.down, out hit, s_rect.height + 30f, -1, QueryTriggerInteraction.Ignore)
                ? hit.point.y + 0.5f : s_rect.yMin + 0.5f;
            Plugin.Teleport(NetCutsceneTest.Own, new Vector3(x, y, z));
        }
        // The bot button for the trigger's own "press to pick up" condition (0: not one the bot can press).
        static int PickupButton()
        {
            ButtonPressedCondition c = s_trigger.Condition as ButtonPressedCondition;
            if (c == null) return 0;
            switch (c.Button)
            {
                case Core.Input.Button.ButtonA: return P2Input.BTN_A;
                case Core.Input.Button.ButtonX: return P2Input.BTN_X;
                case Core.Input.Button.ButtonY: return P2Input.BTN_Y;
                case Core.Input.Button.ButtonB: return P2Input.BTN_B;
                default: return 0;
            }
        }

        static void Arm()
        {
            for (Transform t = s_trigger.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            if (s_sequence.IsRunning) s_sequence.Stop();
            s_trigger.Active = true;
        }

        static IEnumerator Setup(TestHarness h)
        {
            foreach (RuntimeSceneMetaData scene in Core.Scenes.Manager.AllScenes)
            {
                if (scene.Scene != Scene) continue;
                Vector2 center = scene.SceneBounds.center;
                Plugin.Teleport(NetCutsceneTest.Own, new Vector3(center.x, center.y, 0));
                Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false); break;
            }
            float end = Now + 25f;
            while (Now < end)
            {
                s_trigger = Find();
                if (s_trigger != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            Check(h, s_trigger != null, "setup: pickup trigger found");
            if (s_trigger == null) yield break;
            s_sequence = s_trigger.ActionToRun as ActionSequence;
            s_rect = Utility.RectFromBounds(Utility.BoundsFromTransform(s_trigger.transform));
            ButtonPressedCondition button = s_trigger.Condition as ButtonPressedCondition;
            h.Note("SEIN setup (" + Who + "): rect=" + s_rect + " sequence=" + (s_sequence == null ? "none" : s_sequence.name + " actions=" + (s_sequence.Actions == null ? -1 : s_sequence.Actions.Count))
                + " condition=" + (s_trigger.Condition == null ? "none" : s_trigger.Condition.GetType().Name) + (button != null ? " " + button.Button + "/" + button.State : "")
                + " once=" + s_trigger.TriggerOnce + " whileRunning=" + s_trigger.DontTriggerWhileRunning + " delay=" + s_trigger.Delay);
            Check(h, s_sequence != null && s_sequence.Actions != null && s_sequence.Actions.Count == 52, "setup: the pickup script is the 52-action activateSequence");
            Check(h, PickupButton() != 0, "setup: the pickup needs a key the bot can press (ButtonPressedCondition)");
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 701, Found = s_sequence != null && PickupButton() != 0 });
            yield return NetCutsceneTest.Barrier(h, 701);
            yield return NetCutsceneTest.Settle();
        }

        static IEnumerator Play(TestHarness h)
        {
            SeinCharacter own = NetCutsceneTest.Own;
            yield return NetCutsceneTest.Barrier(h, 711);
            // The guest stands at Sein, the host 8 units to the right (outside the area).
            Place(NetPlay.IsHost ? s_rect.width * 0.5f + 8f : -0.6f);
            yield return NetCutsceneTest.Wait(1.5f);
            yield return NetCutsceneTest.Settle();
            // The save has the pickup behind it (TriggerOnce: not active, script at its end): arm it again on this PC.
            // Only now: the scene's own load, a moment after it streamed in, puts the saved state back (first run: the
            // host's trigger was inactive again 4 s after an arming done in Setup).
            Arm();
            yield return NetCutsceneTest.Barrier(h, 712);
            yield return NetCutsceneTest.Wait(1.5f);
            Check(h, s_trigger.Active && s_trigger.gameObject.activeInHierarchy && !s_sequence.IsRunning, "setup: the pickup is armed on this PC (trigger active, script not running)");
            Arm();
            yield return NetCutsceneTest.Barrier(h, 714);
            int fires = ForcedSync.Fires, guard = CutsceneGuard.Repairs - CutsceneGuard.SpiritRepairs;

            // 1. The guest presses the pickup key alone.
            bool hint = false, early = false;
            float until = Now + 3f, release = Now + 0.3f;
            while (Now < until)
            {
                if (!NetPlay.IsHost) NetTestHarness.BotButtons = Now < release ? PickupButton() : 0;
                if (ForcedSync.LastCount == 1 && ForcedSync.LastTotal == 2 && ForcedSync.BottomText.Length > 0 && ForcedSync.TopText.Length > 0 && ForcedSync.PillarsShown) hint = true;
                if (s_sequence.IsRunning || StorySync.RealStories > 0) early = true;
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            if (!NetPlay.IsHost) Check(h, hint, "1 the guest pressed the pickup key alone: it reads the wait hint (1/2, hold to force) and sees the ward");
            Check(h, !early && ForcedSync.Fires == fires, "1 the pickup waits for the partner: no story while only the guest is at Sein");
            yield return NetCutsceneTest.Barrier(h, 713);

            // 2. The host arrives and only stands in the area: nobody holds or presses the key now.
            h.Note("SEIN before the host arrives (" + Who + "): trigger active=" + s_trigger.Active + " " + Diag());
            if (NetPlay.IsHost) Place(0.6f);
            float arrived = Now; until = Now + 5f;
            while (!s_sequence.IsRunning && Now < until) yield return null;
            float startedAfter = Now - arrived;
            Check(h, s_sequence.IsRunning, "2 the pickup story started within 5 s of the partner standing in the area (no key held, no second press)");
            h.Note("SEIN story start (" + Who + "): after " + startedAfter.ToString("0.00") + " s, " + Diag());
            if (!s_sequence.IsRunning) { NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 719, Failed = true }); yield return NetCutsceneTest.Barrier(h, 719); yield break; }

            // 3. The script runs. The guest dismisses the story text 2 s after it opens, the host 5 s after.
            int starts = 1; bool wasRunning = true, orphan = false; float began = Now, textAt = -1f, orphanAt = -1f;
            int maxIndex = 0, lastIndex = -1; string steps = "";   // the waits the script stopped at: index@seconds
            float scriptEndedAt = -1f, freeAt = -1f;
            until = Now + 60f;
            while ((s_sequence.IsRunning || StorySync.RealStories > 0) && Now < until)
            {
                bool running = s_sequence.IsRunning;
                if (running && !wasRunning) { starts++; h.Note("SEIN the script started again here (" + Who + ") at index " + s_sequence.Index + " after " + (Now - began).ToString("0.0") + " s"); }
                // This PC's own copy ended (the guest's does so before the host's story is over: it closed the text first).
                if (!running && wasRunning && scriptEndedAt < 0f) scriptEndedAt = Now;
                if (scriptEndedAt >= 0f && freeAt < 0f && FreeNow(own)) freeAt = Now;
                wasRunning = running;
                if (running && s_sequence.Index != lastIndex)
                { lastIndex = s_sequence.Index; steps += " " + lastIndex + "@" + (Now - began).ToString("0.0"); }
                if (running && s_sequence.Index > maxIndex) maxIndex = s_sequence.Index;
                // The script runs here although no story is announced / mirrored any more.
                if (running && Now - began > 1f && StorySync.RealStories == 0 && !orphan) { orphan = true; orphanAt = Now - began; }
                if (NetCutsceneTest.OpenBoxes() > 0 && textAt < 0f) textAt = Now;
                bool answer = textAt >= 0f && Now - textAt >= (NetPlay.IsHost ? 5f : 2f) && NetCutsceneTest.OpenBoxes() > 0;
                NetTestHarness.BotButtons = answer && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            float lasted = Now - began;
            h.Note("SEIN steps (" + Who + "):" + steps);
            h.Note("SEIN story end (" + Who + "): lasted " + lasted.ToString("0.0") + " s, starts=" + starts + " maxIndex=" + maxIndex
                + " orphanAt=" + orphanAt.ToString("0.0") + " guardRepairs=" + (CutsceneGuard.Repairs - CutsceneGuard.SpiritRepairs - guard) + " " + Diag());
            Check(h, !s_sequence.IsRunning && StorySync.RealStories == 0, "3 the pickup script and its story ended within 60 s");
            // "43. Wait 0.8 seconds" (index 42) is the last wait; 44..52 run in one frame. The final index is not checked:
            // a mirror's cleanup stops its copy when the host's end arrives, which puts the index back to 0 (run 4).
            Check(h, maxIndex >= 42, "3 the script ran through to its last wait here (43. Wait 0.8 seconds, index 42; reached " + maxIndex + ")");
            Check(h, starts == 1, "3 the pickup script started exactly once here (got " + starts + ")");
            Check(h, !orphan, "3 a story was announced on this PC for as long as the script ran here" + (orphan ? " (none from " + orphanAt.ToString("0.0") + " s)" : ""));
            Check(h, CutsceneGuard.Repairs - CutsceneGuard.SpiritRepairs == guard, "3 no guard repair was needed during the story (input lock, letterbox, camera)");

            // 4. Free within 1.5 s of this PC's own script ending, and the Ori walks.
            if (scriptEndedAt < 0f) scriptEndedAt = Now;   // ended in the frame the loop left
            while (freeAt < 0f && Now < scriptEndedAt + 1.5f)
            {
                if (FreeNow(own)) freeAt = Now; else yield return null;
            }
            Check(h, freeAt >= 0f && freeAt - scriptEndedAt <= 1.5f, "4 input is free within 1.5 s of the script ending on this PC (no lock, no character animation, no letterbox, no text; "
                + (freeAt < 0f ? "never" : "after " + (freeAt - scriptEndedAt).ToString("0.00") + " s") + ")");
            Check(h, FreeNow(own), "4 input is still free once the story is over on both PCs");
            Vector3 from = own != null ? own.Position : Vector3.zero;
            NetTestHarness.BotLX = 1f;
            yield return NetCutsceneTest.Wait(1f);
            NetTestHarness.BotLX = 0f;
            float moved = own != null ? Mathf.Abs(own.Position.x - from.x) : 0f;
            h.Note("SEIN move probe (" + Who + "): moved=" + moved.ToString("0.00") + " " + Diag());
            Check(h, own != null && moved > 0.5f, "4 own Ori walks once the story ended (bot pushed right for 1 s, moved " + moved.ToString("0.00") + ")");
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 719, Found = true });
            yield return NetCutsceneTest.Barrier(h, 719);
        }

        public static IEnumerator Run(TestHarness h)
        {
            NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; NetPlay.SetSim(0, 0, 0, 0);
            try
            {
                yield return NetCutsceneTest.Step(h, 700, Setup(h));
                if (s_trigger == null || s_sequence == null || !NetCutsceneTest.Remote.ContainsKey(701) || !NetCutsceneTest.Remote[701].Found) yield break;
                yield return NetCutsceneTest.Step(h, 710, Play(h));
            }
            finally { NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; }
        }
    }
}
