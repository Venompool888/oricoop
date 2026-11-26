using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Horu's falling rocks start another sequence beside the root. Its first action locks input
    // and its fourth waits 3.5 seconds. Dropping the owner during that wait must release the lock.
    internal static class NetExternalStoryTest
    {
        static readonly MoonGuid Rocks = new MoonGuid(271722278, 1258791440, 1216339647, 2088785004);
        static readonly MoonGuid Door = new MoonGuid(2031988133, 1253051438, -1048575855, -1243562809);
        static readonly FieldInfo OneShot = AccessTools.Field(typeof(RunActionOnce), "m_hasRun");
        static float Now { get { return Time.realtimeSinceStartup; } }

        static ActionSequence FindRocks()
        {
            foreach (ActionSequence seq in Resources.FindObjectsOfTypeAll<ActionSequence>())
                if (seq != null && seq.MoonGuid == Rocks && seq.gameObject.scene.IsValid()) return seq;
            return null;
        }

        public static IEnumerator Run(TestHarness h)
        {
            SeinCharacter own = NetCutsceneTest.Own;
            bool immortal = own.Mortality.DamageReciever.IsImmortal;
            Vector3 origin = own.Position;
            GameObject survivor = new GameObject("OriCoop surviving mirror");
            ActionSequence keep = survivor.AddComponent<ActionSequence>();
            keep.MoonGuid = new MoonGuid(0x54323232, 0x484f5255, 0x53544f52, 3);
            WaitAction wait = survivor.AddComponent<WaitAction>(); wait.Duration = 24f;
            LockPlayerInputAction later = survivor.AddComponent<LockPlayerInputAction>(); later.DurationPlayerCantMove = 0.1f;
            keep.Actions.Add(wait); keep.Actions.Add(later);
            RunActionOnce call = null; ActionSequence external = null;
            bool used = false, injected = false;
            try
            {
                own.Mortality.DamageReciever.IsImmortal = true;
                RuntimeSceneMetaData scene = Core.Scenes.Manager.GetSceneInformation("mountHoruProjectileCorridor");
                Vector2 center = scene.SceneBounds.center;
                Plugin.Teleport(own, new Vector3(center.x, center.y, 0f));
                Core.Scenes.Manager.AdditivelyLoadScenesInsideRect(scene.SceneBounds, true, false, false);
                ActionSequence root = null;
                float end = Now + 16f;
                while (Now < end && (root = FindRocks()) == null) yield return NetCutsceneTest.Wait(0.1f);
                if (root != null && root.Actions.Count > 1) call = root.Actions[0] as RunActionOnce;
                if (call != null) external = call.Action as ActionSequence;
                bool fixture = external != null && external.MoonGuid == Door && !external.transform.IsChildOf(root.transform)
                    && external.Actions.Count >= 4 && external.Actions[0] is LockPlayerInputManualAction;
                h.Check(fixture, "EXTERNAL shipped Horu one-shot references the separate input-lock sequence");
                NetCutsceneTest.Publish(new NetCutsceneTest.Sample { Step = 881, Found = fixture });
                yield return NetCutsceneTest.Barrier(h, 881);
                if (!fixture || !NetCutsceneTest.Remote.ContainsKey(881) || !NetCutsceneTest.Remote[881].Found) yield break;
                int starts = StorySync.StartsMirrored;
                if (NetPlay.IsHost) keep.Perform(null);
                else
                {
                    end = Now + 6f;
                    while (StorySync.StartsMirrored == starts && Now < end) yield return null;
                    h.Check(keep.IsRunning && !external.IsRunning && !GameController.Instance.LockInputByAction,
                        "EXTERNAL host mirror is waiting and input starts free");
                    used = (bool)OneShot.GetValue(call); OneShot.SetValue(call, false);
                    injected = true;
                    StorySync.TestInjectRemote(3, 922203, root);
                    end = Now + 2f;
                    while (!external.IsRunning && Now < end) yield return null;
                    h.Check(root.IsRunning && external.IsRunning && keep.IsRunning && StorySync.RealStories == 2
                        && GameController.Instance.LockInputByAction, "EXTERNAL third seat started Horu's off-tree sequence and acquired the real input lock");
                    // The rocks script's 1 s and 2 s waits expire before the door's 3.5 s wait.
                    end = Now + 8f;
                    while (root.IsRunning && Now < end) yield return null;
                    h.Check(!root.IsRunning && external.IsRunning, "EXTERNAL native parent ended with its separate door sequence still running");
                    StorySync.TestRemoteEnd(3, 922203);
                    yield return null;
                    h.Check(external.IsRunning && StorySync.RealStories == 2 && GameController.Instance.LockInputByAction,
                        "EXTERNAL normal story end lets the active child finish on this PC");
                    StorySync.TestSeatLeft(3);
                    h.Check(!external.IsRunning, "EXTERNAL dropped player's off-tree sequence stopped immediately");
                    h.Check(!GameController.Instance.LockInputByAction, "EXTERNAL dropped player's native input lock cleared immediately");
                    h.Check(keep.IsRunning && StorySync.RealStories == 1, "EXTERNAL other player's mirror remains active after the drop");
                }
                yield return NetCutsceneTest.Barrier(h, 882);
            }
            finally
            {
                if (injected)
                {
                    StorySync.TestSeatLeft(3);
                    if (external != null && external.IsRunning) external.Stop();
                    if (call != null) OneShot.SetValue(call, used);
                }
                keep.Stop(); Object.Destroy(survivor);
                if (own != null) { Plugin.Teleport(own, origin); own.Mortality.DamageReciever.IsImmortal = immortal; }
            }
        }
    }
}
