using System;
using System.Collections;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // T-022 (#3) regression, opt-in ORICOOP_NET_ONLY=P3 ORICOOP_P3_KURO=1: both players stand in spiritTreeRefined and
    // walk into the Spirit Tree story trigger; the story runs the Kuro attack cutscene, which removes Ori
    // (DestroyGameplayElementsAction). Playtest 2026-11-17: the host's Ori never came back because the partner kept
    // spiritTreeRefined loaded. Expect: each end has its Ori back within 5 s of the game being playable again, and
    // sees the partner's puppet. ORICOOP_RESPAWN_OFF=1 (CutsceneRespawn) must make it fail.
    // ORICOOP_P3_KURO=chain (T-172) is the same run with the host holding its 01 for 4 s so the guest's mirror gets
    // ahead, plus the oracles for that race.
    internal static class NetKuroTest
    {
        const string Scene = "spiritTreeRefined";
        const string TriggerPath = "01. reachSpiritTreeActionSequence";
        // The Kuro attack (02, the sequence with DestroyGameplayElementsAction). The game starts it only from 01's last
        // action, "18. Run 02" (RunActionOnce). T-182: the test used to Perform 02 directly while both players stood in
        // the trigger; the gate (T-040) fired 01 as well, and 01's RunActionOnce, which the direct Perform had bypassed,
        // ran 02 a second time and removed the host's Ori again. Both modes now let the game run 01 -> 02 itself.
        const string KuroPath = "02. kuroAttackActionSequence";
        static PlayerCollisionTrigger s_trigger;
        static Rect s_rect;
        static float Now { get { return Time.realtimeSinceStartup; } }

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
        static PlayerCollisionTrigger Find()
        {
            foreach (PlayerCollisionTrigger t in Resources.FindObjectsOfTypeAll<PlayerCollisionTrigger>())
                if (t != null && t.ActionToRun != null && PathOf(t.ActionToRun).EndsWith(TriggerPath, StringComparison.Ordinal) && InScene(t)) return t;
            return null;
        }
        static ActionSequence FindKuro()
        {
            foreach (ActionSequence s in Resources.FindObjectsOfTypeAll<ActionSequence>())
                if (s != null && PathOf(s).EndsWith(KuroPath, StringComparison.Ordinal) && InScene(s)) return s;
            return null;
        }
        static bool OriHere()
        {
            SeinCharacter s = Characters.Sein;
            return s != null && s.gameObject.activeInHierarchy && (P2Context.P2 == null || s != P2Context.P2);
        }
        static bool PartnerVisible()
        {
            foreach (Puppet p in Players.Remotes) if (p.Sein != null && p.Sein.gameObject.activeInHierarchy) return true;
            return false;
        }
        static bool Playable() { return CutsceneRespawn.Playable(); }
        static ActionSequence FindSeq(string name)
        {
            foreach (ActionSequence s in Resources.FindObjectsOfTypeAll<ActionSequence>())
                if (s != null && PathOf(s).EndsWith(name, StringComparison.Ordinal) && InScene(s)) return s;
            return null;
        }
        // T-177: 04's "11. Show World Map Objective Action" opens the world map and waits (12.) until the player presses
        // through it; online it is a live menu (SessionRules.LiveScreen), not a popup. A mirror shows it too.
        static bool ObjectiveShowing()
        {
            GameMapUI map = GameMapUI.Instance;
            return map != null && map.ShowObjective != null && map.ShowObjective.IsPerforming;
        }
        // What a player answers: story texts, skipped native popups (SessionRules.PopupOpen), the world-map objective.
        static bool WaitingForPress() { return NetCutsceneTest.OpenBoxes() > 0 || SessionRules.PopupOpen || ObjectiveShowing(); }
        static void Answer() { NetTestHarness.BotButtons = WaitingForPress() && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0; }
        static readonly string[] ChainPaths = { TriggerPath, KuroPath, "03. worldMapActionSequence", "04. returnCameraToPlayerActionSequence" };
        static string RunningChain()
        {
            string running = "";
            foreach (string name in ChainPaths) { ActionSequence s = FindSeq(name); if (s != null && s.IsRunning) running += name + ";"; }
            return running;
        }
        // T-182: how many times 02 was performed on this PC during the run. Counted at Perform, not by polling IsRunning:
        // in the mutant the direct 02 still sat at index 0 when 01's RunActionOnce performed it again (no visible edge).
        static int s_kuroStarts;
        static bool s_watchKuro;
        static ActionSequence s_kuro;
        // T-183: runs of 03 and 04 that started here (a Perform while the sequence was not running). The game runs each
        // once; a leftover string trigger ran 03 (and its world-map trip) a second time on the host or on the mirror PC.
        // Matched by path, not instance: the tree scene may be reloaded around the world-map trip.
        static int s_worldMapRuns, s_returnCameraRuns;
        [HarmonyPatch(typeof(ActionSequence), "Perform")]
        static class CountKuro
        {
            static bool Prepare() { return TestHarness.Enabled; }
            static void Prefix(ActionSequence __instance, out bool __state) { __state = __instance != null && __instance.IsRunning; }
            static void Postfix(ActionSequence __instance, bool __state)
            {
                if (!s_watchKuro || __instance == null) return;
                if (s_kuro != null && __instance == s_kuro && __instance.IsRunning) s_kuroStarts++;
                if (__state || !__instance.IsRunning) return;
                string name = __instance.name;
                if (name != ChainPaths[2] && name != ChainPaths[3]) return;
                if (!PathOf(__instance).EndsWith(name, StringComparison.Ordinal) || !InScene(__instance)) return;
                if (name == ChainPaths[2]) s_worldMapRuns++; else s_returnCameraRuns++;
            }
        }
        // Both stand in the trigger, the gate fires, the host runs 01 natively and the guest mirrors it; 01 starts 02.
        // T-172 (playtest 3), ORICOOP_P3_KURO=chain: the host holds its own 01 for a few seconds so the guest's mirror
        // reaches "18. Run 02 (RunActionOnce)" first, as in the playtest (there the host was 1.3 s behind).
        static bool Chain { get { return Environment.GetEnvironmentVariable("ORICOOP_P3_KURO") == "chain"; } }
        const float HostDelay = 4f, HostDelayMax = 40f;   // 200 +- 150 ms with 8 % loss needed up to 19.5 s
        const float HostWorldMapDelay = 2f;   // T-183 (KURO=1)
        const int KuroLagMs = 90, KuroJitterMs = 8;   // T-185 (KURO=1)
        static IEnumerator NativeChain(TestHarness h, Action kuroStarted)
        {
            ActionSequence reach = FindSeq(TriggerPath), kuro = FindKuro();
            Check(h, reach != null && kuro != null, "chain: 01 and 02 found");
            if (reach == null || kuro == null) yield break;
            float end = Now + 30f;
            while (!reach.IsRunning && Now < end) yield return null;
            Check(h, reach.IsRunning, "chain: the gate started 01 on this PC");
            if (!reach.IsRunning) yield break;
            h.Note("KURO chain: 01 running (" + (NetPlay.IsHost ? "host, original" : "guest, mirror") + ")");
            if (NetPlay.IsHost && Chain)
            {
                // T-185: at least HostDelay, and until the guest reports that its mirror of 01 has ended (step 513).
                // With network lag (90 ms +- 8 each way) a fixed 4 s did not always let the guest's mirror reach
                // "18. Run 02" first: the T-172 race was not there and its precondition check failed for no bug.
                reach.IsSuspended = true;
                float start = Now, cap = Now + HostDelayMax;
                while (Now < cap && (Now < start + HostDelay || !NetCutsceneTest.Remote.ContainsKey(513))) yield return null;
                reach.IsSuspended = false;
                h.Note("KURO chain: host resumed its 01 after " + (Now - start).ToString("0.0") + " s (guest's mirror of 01 ended: "
                    + NetCutsceneTest.Remote.ContainsKey(513) + ")");
            }
            end = Now + 60f;
            bool started = false;
            while (reach.IsRunning && Now < end) { if (kuro.IsRunning) started = true; yield return null; }
            // 02 may have started in the same frame 01 ended: give it a moment.
            float grace = Now + 1f;
            while (!started && Now < grace) { if (kuro.IsRunning) started = true; yield return null; }
            h.Note("KURO chain: 01 ended, 02 started here=" + started + " held=" + WorldSync.MirrorHeldTotal);
            if (!NetPlay.IsHost && Chain) NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 513, Found = started });
            if (started) kuroStarted();
        }
        static string Diag()
        {
            Vector3 cam = UI.Cameras.Current != null ? UI.Cameras.Current.Camera.transform.position : Vector3.zero;
            return "ori=" + OriHere() + " partner=" + PartnerVisible() + " playable=" + Playable()
                + " story=" + StorySync.StoryActive + " state=" + (GameStateMachine.Instance == null ? "?" : GameStateMachine.Instance.CurrentState.ToString())
                + " cam=" + cam + " removed=" + CutsceneRespawn.Removed + " respawns=" + CutsceneRespawn.Respawns + " how=" + CutsceneRespawn.LastHow
                + " kept=" + EnemySync.KeptScenes + " " + NetCutsceneTest.Diag();
        }
        static void Check(TestHarness h, bool ok, string label)
        {
            h.Check(ok, "KURO " + label);
            if (!ok) h.Note("KURO " + label + ": " + Diag());
        }
        static void MoveInside(float side)
        {
            SeinCharacter own = NetCutsceneTest.Own;
            float x = s_rect.center.x + side * Mathf.Min(1.5f, s_rect.width * 0.25f);
            float z = s_trigger.transform.position.z; RaycastHit hit;
            float y = Physics.Raycast(new Vector3(x, s_rect.yMax + 0.5f, z), Vector3.down, out hit, s_rect.height + 30f, -1, QueryTriggerInteraction.Ignore)
                ? hit.point.y + 0.5f : s_rect.yMin + 0.5f;
            Plugin.Teleport(own, new Vector3(x, y, z));
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
            Check(h, s_trigger != null, "setup trigger found");
            if (s_trigger == null) yield break;
            for (Transform t = s_trigger.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            s_trigger.Active = true;
            s_rect = Utility.RectFromBounds(Utility.BoundsFromTransform(s_trigger.transform));
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 501, Found = true });
            yield return NetCutsceneTest.Barrier(h, 501);
            yield return NetCutsceneTest.Settle();
        }

        static IEnumerator Play(TestHarness h)
        {
            yield return NetCutsceneTest.Barrier(h, 511);
            MoveInside(NetPlay.IsHost ? -1f : 1f);
            yield return NetCutsceneTest.Settle();
            int removedBase = CutsceneRespawn.Removed;
            bool removed = false; float playableAt = -1f, backAt = -1f;
            int heldBase = WorldSync.MirrorHeldTotal, leftoverBase = StorySync.LeftoverCleanups;
            bool hostKuro = false;
            s_kuroStarts = 0; s_kuro = FindKuro();
            s_worldMapRuns = 0; s_returnCameraRuns = 0;
            int consumedBase = StorySync.MirrorTriggersSkipped, tripBase = StorySync.StartsHeldForTrip, tripsBase = StorySync.SceneTripsAsked;
            // T-185, KURO=1: the story runs over a link with 90 +- 8 ms each way (NetPlay's own simulator, outgoing
            // packets on both PCs). With that lag the partner's start of 04 reaches a PC while its own trip back from
            // the world map is still pending (asked for, scenes not enabled yet): the mirror's 04 placed Ori at the
            // tree, the trip then put it at the scene's placeholder (Kuro's drop spot) and nothing placed it again;
            // or the wait ran into the 2 s "unavailable" clock and the stories ended with 04 still running.
            if (!Chain) NetPlay.SetSim(KuroLagMs, KuroJitterMs, 0f, 0f);
            s_watchKuro = true;
            yield return NativeChain(h, delegate { hostKuro = true; });
            if (!Chain && NetPlay.IsHost) Check(h, hostKuro, "the host's own 01 started 02 (the game's chain, no direct Perform)");
            if (Chain)
            {
                // WorldSync captures every 0.25 s: the guest's held change shows up just after its 01 ended.
                float until = Now + 3f;
                while (!NetPlay.IsHost && WorldSync.MirrorHeldTotal <= heldBase && Now < until) yield return null;
                // T-172 oracles. Host: its own 01 still started 02 although the guest's mirror ran 01's RunActionOnce(02)
                // first. Guest: the race really happened here (its mirror held the RunActionOnce change back).
                if (NetPlay.IsHost) Check(h, hostKuro, "chain: the host's own 01 started 02 after the guest's mirror got there first");
                else Check(h, WorldSync.MirrorHeldTotal > heldBase, "chain: the guest's mirror changed story state and held it back (race reproduced)");
            }
            float end = Now + (NetPlay.IsHost ? 240f : 420f);
            // T-183, KURO=1: the host holds its own 03 for 2 s once it starts, so the guest's mirror reaches the world map
            // first (in the bug the host's 03 ended 0.8 s after the guest's mirror; the host then streamed the guest's
            // world-map scene before its own 03 registered "spiritTreeWorldMap").
            ActionSequence worldMap = !Chain && NetPlay.IsHost ? FindSeq(ChainPaths[2]) : null;
            float releaseWorldMap = -1f; bool heldWorldMap = false;
            while (Now < end)
            {
                if (worldMap != null && !heldWorldMap && worldMap.IsRunning)
                { worldMap.IsSuspended = true; heldWorldMap = true; releaseWorldMap = Now + HostWorldMapDelay; h.Note("KURO: host holds its 03 for " + HostWorldMapDelay + " s"); }
                if (releaseWorldMap >= 0f && Now >= releaseWorldMap) { worldMap.IsSuspended = false; releaseWorldMap = -1f; }
                // Story texts, skipped native popups (T-172) and 04's world-map objective (T-177) wait for the player's press.
                Answer();
                if (!OriHere() || CutsceneRespawn.Removed > removedBase) removed = true;
                if (removed && playableAt < 0f && Playable()) playableAt = Now;
                if (removed && playableAt >= 0f && !Playable()) playableAt = -1f;   // still inside the sequence
                if (removed && OriHere() && Playable()) { backAt = Now; break; }
                if (playableAt >= 0f && Now - playableAt > 8f) break;
                if (!NetPlay.IsHost && NetCutsceneTest.Remote.ContainsKey(512) && OriHere() && Playable()) break;
                yield return null;
            }
            if (releaseWorldMap >= 0f) worldMap.IsSuspended = false;
            NetTestHarness.BotButtons = 0;
            if (NetPlay.IsHost) Check(h, removed, "Kuro cutscene removed the host's Ori (DestroyGameplayElementsAction ran)");
            Check(h, OriHere() && (backAt < 0f ? !removed : playableAt < 0f || backAt - playableAt <= 5f), "own Ori present (back within 5 s of the game being playable)");
            Check(h, UI.Cameras.Current != null && UI.Cameras.Current.Target != null, "camera has a target");
            // T-183: the post-story checks run in both modes. KURO=1 found the host's own 03 ending after the guest's
            // mirror and 04 never starting on the host (Ori left in a character animation); chain hits the same thing
            // when the story ends late. Only the race preconditions (T-172 held, T-175 leftovers) stay chain-only.
            {
                // T-175: once every story is over nothing of a cutscene is left. The host mirrored the guest's 03/04
                // while it still had its own spirit-tree letterbox and animation; cleanup used to put those back.
                // T-177: the bot keeps answering like a player while the stories run (04 waits for the world-map
                // objective to be pressed through before it stops the character animation and unlocks input).
                float over = Now + 60f;
                while (StorySync.RealStories > 0 && Now < over) { Answer(); yield return null; }
                NetTestHarness.BotButtons = 0;
                // T-177: when StorySync says every story is over, the spirit-tree sequences really are over here. The
                // guest's stories used to be ended by CutsceneGuard's 20 s LockInputByAction repair while 04 still waited
                // for its world-map objective (03 locks input, 04 unlocks it).
                string stillRunning = RunningChain();
                h.Note("KURO chain stories over: stories=" + StorySync.RealStories + " storyActive=" + StorySync.StoryActive + " running=" + stillRunning + " objective=" + ObjectiveShowing());
                Check(h, StorySync.RealStories == 0 && stillRunning.Length == 0,
                    "chain: stories ended only once the spirit-tree sequences had finished here (none still running)");
                float settle = Now + 1f;
                while (Now < settle) yield return null;
                SeinCharacter own = NetCutsceneTest.Own;
                bool animating = own != null && own.Controller != null && own.Controller.IsPlayingAnimation;
                h.Note("KURO chain end: stories=" + StorySync.RealStories + " storyActive=" + StorySync.StoryActive + " letterbox=" + Letterbox.ShowLetterboxes + " animation=" + animating
                    + " leftoverCleanups=" + (StorySync.LeftoverCleanups - leftoverBase) + " pos=" + (own != null ? own.Position.ToString() : "no-ori")
                    + " 03runs=" + s_worldMapRuns + " 04runs=" + s_returnCameraRuns + " mirrorTriggersConsumed=" + (StorySync.MirrorTriggersSkipped - consumedBase)
                    + " tripWaits=" + (StorySync.StartsHeldForTrip - tripBase) + " trips=" + (StorySync.SceneTripsAsked - tripsBase));
                // T-185: the host runs its own 03 to the world map and back (KURO=1: it is the original), so the scene-trip
                // hook must have seen those trips; without the hook the wait never happens.
                if (!Chain && NetPlay.IsHost) Check(h, StorySync.SceneTripsAsked > tripsBase,
                    "chain: the host's world-map detour was seen as scene trips (" + (StorySync.SceneTripsAsked - tripsBase) + ")");
                // The case really happened: the host mirrored the guest's 03/04 with its own Kuro leftovers still on.
                if (NetPlay.IsHost && Chain) Check(h, StorySync.LeftoverCleanups > leftoverBase,
                    "chain: the host's mirror cleanup met leftover cutscene state (T-175 case reproduced)");
                Check(h, StorySync.RealStories == 0 && !Letterbox.ShowLetterboxes && !animating,
                    "chain: no letterbox or character animation left once the stories ended");
                // T-183 / T-185: the chain ends with Ori back at the spirit tree (04 sets the character position there),
                // not left at the world-map spot (-207, 4) after a second 03 or at the Kuro drop spot (-152, -130).
                float fromTree = own != null ? Vector2.Distance(own.Position, s_rect.center) : 999f;
                Check(h, own != null && fromTree < 30f, "chain: own Ori back at the spirit tree once the stories ended (" + fromTree.ToString("0.0") + " from the trigger)");
                // T-183: 03 (world map) and 04 ran once each on this PC (host and mirror alike).
                Check(h, s_worldMapRuns == 1 && s_returnCameraRuns == 1,
                    "chain: 03 and 04 each ran once here (03 " + s_worldMapRuns + ", 04 " + s_returnCameraRuns + ")");
                // T-177 movement probe: the own Ori can really move. A player first presses through whatever is still
                // open (text, popup, world-map objective), then walks: the bot pushes right for 1 s.
                float answer = Now + 10f; bool wasOpen = false;
                while (WaitingForPress() && Now < answer) { wasOpen = true; Answer(); yield return null; }
                NetTestHarness.BotButtons = 0;
                Vector3 from = own != null ? own.Position : Vector3.zero;
                NetTestHarness.BotLX = 1f;
                float push = Now + 1f;
                while (Now < push) yield return null;
                NetTestHarness.BotLX = 0f;
                float moved = own != null ? Mathf.Abs(own.Position.x - from.x) : 0f;
                h.Note("KURO chain move probe: moved=" + moved.ToString("0.00") + " answeredFirst=" + wasOpen + " blocked="
                    + (own != null && own.Controller != null ? own.Controller.IsPlayingAnimation + "/" + own.Controller.LockMovementInput : "no-ori")
                    + " held=" + SessionRules.LocalLocked);
                Check(h, own != null && moved > 0.5f, "chain: own Ori walks once the stories ended (bot pushed right for 1 s)");
            }
            // T-177: the puppet comes back once this PC is out of every lock (Plugin.SpawnPuppet refuses while
            // GameController.InputLocked). A player presses through what is still open, e.g. the host's mirrored copy
            // of 04's world-map objective; the bot used to stop pressing here and the map stayed open.
            float seen = Now + 10f;
            while (!PartnerVisible() && Now < seen) { Answer(); yield return null; }
            NetTestHarness.BotButtons = 0;
            Check(h, PartnerVisible(), "partner puppet visible again");
            // T-182: the story ran the Kuro attack once. The host is the original: its 02 starts only from its own 01.
            s_watchKuro = false;
            h.Note("KURO 02 starts here=" + s_kuroStarts + " (" + (NetPlay.IsHost ? "host" : "guest") + ")");
            if (NetPlay.IsHost) Check(h, s_kuroStarts == 1, "the Kuro attack (02) ran exactly once on the host (got " + s_kuroStarts + ")");
            h.Note("KURO end: " + Diag());
            NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 512, Found = backAt >= 0f });
            yield return NetCutsceneTest.Barrier(h, 512);
        }

        public static IEnumerator Run(TestHarness h)
        {
            NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; NetPlay.SetSim(0, 0, 0, 0);
            try
            {
                yield return NetCutsceneTest.Step(h, 500, Setup(h));
                if (s_trigger == null || !NetCutsceneTest.Remote.ContainsKey(501) || !NetCutsceneTest.Remote[501].Found) yield break;
                yield return NetCutsceneTest.Step(h, 510, Play(h));
            }
            finally { s_watchKuro = false; NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; NetPlay.SetSim(0, 0, 0, 0); }
        }
    }
}
