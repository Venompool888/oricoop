using System;
using System.Collections;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Native story prefabs and a registered timeline on two actual clients, including a guest-originated story.
    internal static class NetSharedSkipTest
    {
        static readonly System.Reflection.FieldInfo StoryBox = AccessTools.Field(typeof(ShowStoryTextAction), "m_messageBox");
        static InteractiveMessageBox InputFor(MessageBox box)
        { return box == null ? null : box.GetComponentInParent<InteractiveMessageBox>(); }

        static void Press(InteractiveMessageBox input)
        {
            var jump = Core.Input.Jump;
            bool old = jump.IsPressed, was = jump.WasPressed, used = jump.Used;
            try { jump.IsPressed = true; jump.WasPressed = false; jump.Used = false; input.UpdateComplete(); }
            finally { jump.IsPressed = old; jump.WasPressed = was; jump.Used = used; }
        }

        static IEnumerator Until(Func<bool> satisfied, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (!satisfied() && Time.realtimeSinceStartup < end) yield return null;
        }

        internal static IEnumerator Run(TestHarness h)
        {
            yield return NetCutsceneTest.Settle();
            bool host = NetPlay.IsHost;
            GameObject go = new GameObject("Shared dialogue fixture");
            var sequence = go.AddComponent<ActionSequence>();
            sequence.MoonGuid = new MoonGuid(0x54323538, 1, 2, 1);
            var text = go.AddComponent<ShowStoryTextAction>();
            var provider = ScriptableObject.CreateInstance<TranslatedMessageProvider>();
            provider.Messages.Add(new TranslatedMessageProvider.MessageItem { English = "First page", Chinese = "第一页" });
            provider.Messages.Add(new TranslatedMessageProvider.MessageItem { English = "Second page", Chinese = "第二页" });
            text.StoryMessage = provider; sequence.Actions.Add(text);
            MessageBox box = null, help = null;
            bool hidden = false;
            try
            {
                yield return NetCutsceneTest.Barrier(h, 891);
                if (host) sequence.Perform(null);
                yield return Until(() => (box = StoryBox.GetValue(text) as MessageBox) != null, 8);
                InteractiveMessageBox input = InputFor(box);
                h.Check(input != null && box.MessageIndex == 0, "T258 native shared dialogue opened on page one");
                if (input == null) yield break;
                box.OnMessageScreenHide += delegate { hidden = true; };
                yield return NetCutsceneTest.Wait(0.6f);
                int shown = StorySync.RequestsShown, sent = StorySync.RequestsSent;
                yield return NetCutsceneTest.Barrier(h, 897);
                if (!host) { Press(input); Press(input); }
                yield return Until(() => (host ? StorySync.RequestsShown > shown : StorySync.RequestsSent > sent), 4);
                h.Check((host ? StorySync.RequestsShown - shown : StorySync.RequestsSent - sent) == 1,
                    "T258 two member presses produced one skip request");
                h.Check(box.MessageIndex == 0 && !hidden, "T258 neither PC advanced before host approval");
                if (host) StorySync.TestDropControlHeartbeats = true;
                yield return NetCutsceneTest.Barrier(h, 898);
                float quietUntil = Time.realtimeSinceStartup + 6f;
                while (Time.realtimeSinceStartup < quietUntil) yield return null;
                if (!host) Press(input);
                h.Check(box.MessageIndex == 0 && !hidden, "T258 member cannot advance after six seconds without host heartbeats");
                yield return NetCutsceneTest.Barrier(h, 899);
                StorySync.TestDropControlHeartbeats = false;
                yield return NetCutsceneTest.Barrier(h, 892);
                if (host) Press(input);
                yield return Until(() => box.MessageIndex == 1, 5);
                h.Check(box.MessageIndex == 1 && !hidden, "T258 host approval advanced both PCs exactly one page");

                // The guest can dismiss a native personal help popup while the shared story is still waiting.
                if (!host)
                {
                    help = UI.MessageController.ShowHelpMessage(provider, null);
                    var helpInput = InputFor(help);
                    bool helpClosed = false;
                    h.Check(helpInput != null, "T258 separate native help popup is interactive");
                    if (helpInput != null)
                    {
                        help.OnMessageScreenHide += delegate { helpClosed = true; };
                        yield return NetCutsceneTest.Wait(0.4f);
                        Press(helpInput); Press(helpInput);
                        h.Check(helpClosed && !hidden && box.MessageIndex == 1, "T258 personal help closes while shared dialogue stays open");
                    }
                    input.CanCancel = true;
                    var cancel = Core.Input.Cancel;
                    bool pressed = cancel.IsPressed, was = cancel.WasPressed, used = cancel.Used;
                    try { cancel.IsPressed = true; cancel.WasPressed = false; cancel.Used = false; input.UpdateWriting(); }
                    finally { cancel.IsPressed = pressed; cancel.WasPressed = was; cancel.Used = used; }
                }
                yield return Until(() => (host ? StorySync.RequestsShown > shown + 1 : StorySync.RequestsSent > sent + 1), 4);
                h.Check((host ? StorySync.RequestsShown - shown : StorySync.RequestsSent - sent) == 2 && !hidden,
                    "T258 guest Cancel requested the final dialogue close without dismissing it");
                yield return NetCutsceneTest.Barrier(h, 893);
                if (host) Press(input);
                yield return Until(() => hidden, 5);
                h.Check(hidden, "T258 host closed the final page for both players");
            }
            finally
            {
                StorySync.TestDropControlHeartbeats = false;
                if (help != null && help.Visibility != null) help.Visibility.HideMessageScreenImmediately();
                if (box != null && !hidden) box.HideMessageScreen();
                UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(provider);
            }
            yield return NetCutsceneTest.Settle();
            yield return Until(() => !StorySync.StoryActive, 8);
            GameObject scene = new GameObject("Shared timeline fixture");
            var start = scene.AddComponent<ActionSequence>(); start.MoonGuid = new MoonGuid(0x54323538, 1, 2, 2);
            var register = scene.AddComponent<RegisterSkipCutsceneAction>();
            var timeline = scene.AddComponent<MasterTimelineSequence>();
            var finalGo = new GameObject("Finish timeline"); finalGo.transform.SetParent(scene.transform, false);
            var finish = finalGo.AddComponent<ActionSequence>(); finish.MoonGuid = new MoonGuid(0x54323538, 1, 2, 3);
            var observable = finalGo.AddComponent<WaitAction>(); observable.Duration = 12;
            finish.Actions.Add(observable); timeline.OnFinish = finish; register.MasterTimelineSequence = timeline;
            var hold = scene.AddComponent<LockPlayerInputAction>(); hold.DurationPlayerCantMove = 20;
            var wait = scene.AddComponent<WaitAction>(); wait.Duration = 20;
            start.Actions.Add(register); start.Actions.Add(hold); start.Actions.Add(wait);
            GameplayCamera gameplayCamera = UI.Cameras.Current;
            Transform originalTarget = gameplayCamera.Target;
            GameObject temporaryTarget = null;
            try
            {
                yield return NetCutsceneTest.Barrier(h, 894);
                if (!host) start.Perform(null);
                yield return Until(() => start.IsRunning && SkipCutsceneController.Instance.SkippingAvailable, 6);
                h.Check(start.IsRunning && SkipCutsceneController.Instance.SkippingAvailable, "T258 both PCs registered the guest-originated skip action");
                if (host && originalTarget != null)
                {
                    temporaryTarget = new GameObject("Same-scene mirrored camera target");
                    temporaryTarget.transform.position = originalTarget.position;
                    gameplayCamera.Target = temporaryTarget.transform;
                }
                yield return NetCutsceneTest.Wait(0.5f);
                int shown = StorySync.RequestsShown, sent = StorySync.RequestsSent;
                yield return NetCutsceneTest.Barrier(h, 896);
                if (!host) { SkipCutsceneController.Instance.SkipCutscene(); SkipCutsceneController.Instance.SkipCutscene(); }
                yield return Until(() => (host ? StorySync.RequestsShown > shown : StorySync.RequestsSent > sent), 4);
                h.Check((host ? StorySync.RequestsShown - shown : StorySync.RequestsSent - sent) == 1 && !finish.IsRunning,
                    "T258 repeated member timeline skips prompt the host once and neither finish action runs");
                yield return NetCutsceneTest.Barrier(h, 895);
                if (host) SkipCutsceneController.Instance.SkipCutscene();
                yield return Until(() => finish.IsRunning, 6);
                h.Check(finish.IsRunning, "T258 host skipped the registered timeline on both PCs");
            }
            finally
            {
                SkipCutsceneController.Instance.Clear(); hold.Stop(); start.Stop(); finish.Stop();
                UI.Fader.FadeIn(0); UnityEngine.Object.Destroy(scene);
            }
            if (host)
            {
                try
                {
                    yield return Until(() => gameplayCamera.Target == originalTarget, 8f);
                    h.Check(temporaryTarget != null && originalTarget != null && gameplayCamera.Target == originalTarget,
                        "T258 ending a mirror in the same scene restores the original live camera target");
                }
                finally
                {
                    if (gameplayCamera.Target == (temporaryTarget == null ? null : temporaryTarget.transform)) gameplayCamera.Target = originalTarget;
                    if (temporaryTarget != null) UnityEngine.Object.Destroy(temporaryTarget);
                }
            }
        }
    }
}
