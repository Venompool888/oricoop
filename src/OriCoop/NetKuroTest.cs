using System;
using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-022 (#3) regression, opt-in ORICOOP_NET_ONLY=P3 ORICOOP_P3_KURO=1: both players stand in spiritTreeRefined and
    // walk into the Spirit Tree story trigger; the story runs the Kuro attack cutscene, which removes Ori
    // (DestroyGameplayElementsAction). Playtest 2026-11-17: the host's Ori never came back because the partner kept
    // spiritTreeRefined loaded. Expect: each end has its Ori back within 5 s of the game being playable again, and
    // sees the partner's puppet. ORICOOP_RESPAWN_OFF=1 (CutsceneRespawn) must make it fail.
    internal static class NetKuroTest
    {
        const string Scene = "spiritTreeRefined";
        const string TriggerPath = "01. reachSpiritTreeActionSequence";
        // The Spirit Tree story chains 01 -> 02 through world-state conditions; the test runs the Kuro attack (02, the
        // sequence with DestroyGameplayElementsAction) directly on the host while the guest stands beside it, which is
        // exactly where the gate (T-040) puts both players.
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
        static void Press() { NetTestHarness.BotButtons = ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0; }
        // Host: what the trigger chain does after the Spirit Tree talk: start the Kuro attack (02).
        static IEnumerator RunChain(TestHarness h)
        {
            // 02 starts the Kuro cutscene scenes; the game itself continues to 03 / 04 (string triggers), so only 02.
            foreach (string name in new string[] { KuroPath })
            {
                ActionSequence s = FindSeq(name);
                Check(h, s != null, "sequence found: " + name);
                if (s == null) yield break;
                for (Transform t = s.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                s.Perform(null);
                float end = Now + 150f;
                while (s.IsPerforming && Now < end) { Press(); yield return null; }
                NetTestHarness.BotButtons = 0;
                h.Note("KURO " + name + " done=" + !s.IsPerforming + ": " + Diag());
            }
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
            if (NetPlay.IsHost) yield return RunChain(h);
            float end = Now + (NetPlay.IsHost ? 240f : 420f);
            while (Now < end)
            {
                NetTestHarness.BotButtons = NetCutsceneTest.OpenBoxes() > 0 && ((int)(Now * 4f)) % 2 == 0 ? P2Input.BTN_A : 0;
                if (!OriHere() || CutsceneRespawn.Removed > removedBase) removed = true;
                if (removed && playableAt < 0f && Playable()) playableAt = Now;
                if (removed && playableAt >= 0f && !Playable()) playableAt = -1f;   // still inside the sequence
                if (removed && OriHere() && Playable()) { backAt = Now; break; }
                if (playableAt >= 0f && Now - playableAt > 8f) break;
                if (!NetPlay.IsHost && NetCutsceneTest.Remote.ContainsKey(512) && OriHere() && Playable()) break;
                yield return null;
            }
            NetTestHarness.BotButtons = 0;
            if (NetPlay.IsHost) Check(h, removed, "Kuro cutscene removed the host's Ori (DestroyGameplayElementsAction ran)");
            Check(h, OriHere() && (backAt < 0f ? !removed : playableAt < 0f || backAt - playableAt <= 5f), "own Ori present (back within 5 s of the game being playable)");
            Check(h, UI.Cameras.Current != null && UI.Cameras.Current.Target != null, "camera has a target");
            float seen = Now + 10f;
            while (!PartnerVisible() && Now < seen) yield return null;
            Check(h, PartnerVisible(), "partner puppet visible again");
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
            finally { NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; }
        }
    }
}
