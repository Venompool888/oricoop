using System.Collections;
using UnityEngine;

namespace OriCoop
{
    // T-207 / T-212 live checks of the story mirror, part of the 2-player CUT phase (called from NetCutsceneTest.Run).
    // Each PC builds the same small fixture story (an ActionSequence with a fixed MoonGuid: lock input, then a tail);
    // the host starts it natively and the guest mirrors it, as for any story.
    internal static class NetStoryLeftoverTest
    {
        // One fixture id per step: the ledger takes a second start of the same story within 3 s for a repeat.
        static MoonGuid Id(int n) { return new MoonGuid(0x54323132, 0x4c454654, 0x53544f52, 0x59303030 + n); }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static string Who { get { return NetPlay.IsHost ? "host" : "guest"; } }
        static int Starts { get { return NetPlay.IsHost ? StorySync.StartsSent : StorySync.StartsMirrored; } }

        sealed class Fixture
        {
            public GameObject Go; public ActionSequence Sequence; public LockPlayerInputAction Input;
            public void Destroy() { if (Input != null) Input.Stop(); if (Go != null) Object.Destroy(Go); }
        }
        static Fixture Build(MoonGuid id, float lockSeconds, float tailSeconds)
        {
            Fixture f = new Fixture();
            f.Go = new GameObject("OriCoop T-212 fixture story");
            f.Sequence = f.Go.AddComponent<ActionSequence>(); f.Sequence.MoonGuid = new MoonGuid(id);
            f.Input = f.Go.AddComponent<LockPlayerInputAction>(); f.Input.DurationPlayerCantMove = lockSeconds;
            WaitAction wait = f.Go.AddComponent<WaitAction>(); wait.Duration = lockSeconds + tailSeconds;
            f.Sequence.Actions.Add(f.Input); f.Sequence.Actions.Add(wait);
            return f;
        }
        static void Check(TestHarness h, bool ok, string label)
        {
            h.Check(ok, "LEFT " + label);
            if (!ok) h.Note("LEFT " + label + " (" + Who + "): stories=" + StorySync.RealStories + " active=" + StorySync.StoryActive
                + " sent=" + StorySync.StartsSent + " mirrored=" + StorySync.StartsMirrored + " window=" + CutsceneGuard.PedestalWindow
                + " mask=" + StorySync.TakeoverMaskPublic());
        }
        static IEnumerator Quiet(TestHarness h, string label)
        {
            yield return NetCutsceneTest.Settle();
            float end = Now + 20f;
            while (StorySync.StoryActive && Now < end) yield return null;
            Check(h, !StorySync.StoryActive, label + ": no story running before the step");
        }
        static IEnumerator WaitStarted(int startsBefore, float seconds)
        {
            float end = Now + seconds;
            while (Starts == startsBefore && Now < end) yield return null;
        }
        static IEnumerator WaitOver(Fixture f, float seconds)
        {
            float end = Now + seconds;
            while ((f.Sequence.IsRunning || StorySync.RealStories > 0) && Now < end) yield return null;
        }

        // T-212 (4): the host's story is announced; then the host enters a "local only" window (as when the partner
        // pulls a lever). Its end must still reach the guest. ORICOOP_MUTATE_END_LOST_IN_WINDOW must fail here.
        static IEnumerator EndInsideWindow(TestHarness h)
        {
            yield return Quiet(h, "4");
            Fixture f = Build(Id(4), 1.0f, 0.8f);
            try
            {
                int starts = Starts;
                yield return NetCutsceneTest.Barrier(h, 811);
                if (NetPlay.IsHost) f.Sequence.Perform(null);
                yield return WaitStarted(starts, 5f);
                Check(h, Starts == starts + 1, "4 the fixture story started (host announced it, guest mirrored it)");
                if (NetPlay.IsHost) CutsceneGuard.TestLocalOnlyWindow(12f);
                float began = Now;
                yield return WaitOver(f, 8f);
                h.Note("LEFT 4 (" + Who + "): over after " + (Now - began).ToString("0.0") + " s, stories=" + StorySync.RealStories);
                Check(h, !f.Sequence.IsRunning && StorySync.RealStories == 0,
                    "4 the story is over on this PC within 8 s although its sender entered a local-only window before the end");
            }
            finally { CutsceneGuard.TestLocalOnlyWindow(0f); f.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 812);
        }

        // T-207 (2): the guest is inside its "local only" window (as after replaying the host's lever pull) when the
        // host starts an unrelated story. The guest must mirror it. ORICOOP_MUTATE_WINDOW_DROPS_STARTS must fail here.
        static IEnumerator StartInsideWindow(TestHarness h)
        {
            yield return Quiet(h, "2");
            Fixture f = Build(Id(2), 1.0f, 0.6f);
            try
            {
                int starts = Starts, inWindow = StorySync.MirroredInWindow;
                if (!NetPlay.IsHost) CutsceneGuard.TestLocalOnlyWindow(12f);
                yield return NetCutsceneTest.Barrier(h, 841);
                if (NetPlay.IsHost) f.Sequence.Perform(null);
                yield return WaitStarted(starts, 5f);
                Check(h, Starts == starts + 1, NetPlay.IsHost ? "2 the host's unrelated fixture story was announced"
                    : "2 a partner's unrelated story that starts while this PC is inside a lever / pedestal window is mirrored here");
                if (!NetPlay.IsHost) Check(h, CutsceneGuard.PedestalWindow && StorySync.MirroredInWindow == inWindow + 1, "2 the start arrived inside the window (the case really happened)");
                yield return WaitOver(f, 8f);
                Check(h, !f.Sequence.IsRunning && StorySync.RealStories == 0, "2 the story mirrored inside the window ended normally");
            }
            finally { CutsceneGuard.TestLocalOnlyWindow(0f); f.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 842);
        }

        // T-212 (5): the guest cannot start its mirror for 2 s (as in a scene trip); the host's story is short and its
        // end arrives first. The guest must still play it. ORICOOP_MUTATE_NO_LATE_START must fail here.
        static IEnumerator EndBeforeMirror(TestHarness h)
        {
            yield return Quiet(h, "5");
            Fixture f = Build(Id(5), 0.4f, 0.4f);
            try
            {
                int starts = Starts, late = StorySync.LateStarts;
                if (!NetPlay.IsHost) StorySync.TestResolveDelay = 2f;
                yield return NetCutsceneTest.Barrier(h, 821);
                if (NetPlay.IsHost) f.Sequence.Perform(null);
                yield return WaitStarted(starts, 7f);
                StorySync.TestResolveDelay = 0f;
                Check(h, Starts == starts + 1, NetPlay.IsHost ? "5 the short fixture story was announced"
                    : "5 a short story whose end arrived before the mirror could start is still played here");
                if (!NetPlay.IsHost) Check(h, StorySync.LateStarts == late + 1, "5 the mirror started after the sender's end (the case really happened, " + (StorySync.LateStarts - late) + " late starts)");
                yield return WaitOver(f, 8f);
                Check(h, !f.Sequence.IsRunning && StorySync.RealStories == 0, "5 the late mirror ran out and the story is over here");
            }
            finally { StorySync.TestResolveDelay = 0f; f.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 822);
        }

        // T-212 (3): three players, two partners each run a story that this PC mirrors, one of them drops. The 2-player
        // suite has no third seat: on the guest a second mirrored story is injected for "seat 3" (StorySync.TestInject-
        // Remote, a second fixture) next to the host's real one, then seat 3 leaves. Its script must stop at once and
        // the host's story must go on. ORICOOP_MUTATE_KEEP_DROPPED_MIRROR must fail here.
        static IEnumerator DroppedTeammate(TestHarness h)
        {
            yield return Quiet(h, "3");
            Fixture a = Build(Id(3), 4f, 0.5f), b = Build(Id(33), 6f, 0.5f);
            try
            {
                int starts = Starts, stopped = StorySync.StoppedOwn;
                yield return NetCutsceneTest.Barrier(h, 831);
                if (NetPlay.IsHost) a.Sequence.Perform(null);
                yield return WaitStarted(starts, 5f);
                Check(h, Starts == starts + 1, "3 the host's fixture story started");
                if (!NetPlay.IsHost)
                {
                    StorySync.TestInjectRemote(3, 9001, b.Sequence);
                    yield return NetCutsceneTest.Wait(0.5f);
                    Check(h, a.Sequence.IsRunning && b.Sequence.IsRunning && StorySync.RealStories == 2, "3 two mirrored stories run here: the host's and seat 3's (setup)");
                    StorySync.TestSeatLeft(3);
                    yield return NetCutsceneTest.Wait(0.3f);
                    Check(h, !b.Sequence.IsRunning && StorySync.StoppedOwn == stopped + 1,
                        "3 the dropped teammate's story script stopped at once although another story is still mirrored");
                    Check(h, a.Sequence.IsRunning && StorySync.RealStories == 1 && GameController.Instance.LockInputByAction,
                        "3 the host's mirrored story goes on (running, its input lock kept)");
                }
                yield return WaitOver(a, 10f);
                Check(h, !a.Sequence.IsRunning && !b.Sequence.IsRunning && StorySync.RealStories == 0, "3 the remaining story ended normally");
            }
            finally { a.Destroy(); b.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 832);
        }

        // Review finding 4: the guest cannot start its mirror for 3.2 s, so after 2 s it gives up on the story's scene
        // ("watching"); the host's story ends at about 2.6 s. The guest must still play it once it can (at 3.2 s).
        static IEnumerator EndAfterWatch(TestHarness h)
        {
            yield return Quiet(h, "5b");
            Fixture f = Build(Id(55), 2.6f, 0.3f);
            try
            {
                int starts = Starts, late = StorySync.LateStarts;
                if (!NetPlay.IsHost) StorySync.TestResolveDelay = 3.2f;
                yield return NetCutsceneTest.Barrier(h, 851);
                if (NetPlay.IsHost) f.Sequence.Perform(null);
                yield return WaitStarted(starts, 9f);
                StorySync.TestResolveDelay = 0f;
                Check(h, Starts == starts + 1, NetPlay.IsHost ? "5b the 2.6 s fixture story was announced"
                    : "5b a story whose end arrived after this PC had given up on its scene (2 s) is still played once the scene is ready");
                if (!NetPlay.IsHost) Check(h, StorySync.LateStarts == late + 1, "5b the mirror started after the sender's end (" + (StorySync.LateStarts - late) + " late starts)");
                yield return WaitOver(f, 8f);
                Check(h, !f.Sequence.IsRunning && StorySync.RealStories == 0, "5b the late mirror ran out and the story is over here");
            }
            finally { StorySync.TestResolveDelay = 0f; f.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 852);
        }

        // Review finding 6: the host story's own input lock (0.5 s) has run out while its script still runs; then "seat
        // 3's" story takes the lock and seat 3 drops. No other mirrored lock is still running, so the lock is released
        // at once (it used to stay until the host's story was over).
        static IEnumerator DroppedAfterLockRanOut(TestHarness h)
        {
            yield return Quiet(h, "3b");
            Fixture a = Build(Id(36), 0.5f, 5f), b = Build(Id(37), 6f, 0.5f);
            try
            {
                int starts = Starts;
                yield return NetCutsceneTest.Barrier(h, 861);
                if (NetPlay.IsHost) a.Sequence.Perform(null);
                yield return WaitStarted(starts, 5f);
                Check(h, Starts == starts + 1, "3b the host's fixture story started");
                if (!NetPlay.IsHost)
                {
                    yield return NetCutsceneTest.Wait(1.2f);
                    bool freeBefore = !GameController.Instance.LockInputByAction;
                    StorySync.TestInjectRemote(3, 9002, b.Sequence);
                    yield return NetCutsceneTest.Wait(0.4f);
                    Check(h, freeBefore && GameController.Instance.LockInputByAction && a.Sequence.IsRunning && b.Sequence.IsRunning,
                        "3b setup: the host story's lock has run out, seat 3's story holds the input lock, both scripts run");
                    StorySync.TestSeatLeft(3);
                    yield return NetCutsceneTest.Wait(0.3f);
                    Check(h, !b.Sequence.IsRunning && !GameController.Instance.LockInputByAction,
                        "3b the dropped teammate's input lock is released at once when no other mirrored lock is still running");
                    Check(h, a.Sequence.IsRunning, "3b the host's mirrored script goes on");
                }
                yield return WaitOver(a, 12f);
                Check(h, !a.Sequence.IsRunning && !b.Sequence.IsRunning && StorySync.RealStories == 0, "3b the remaining story ended normally");
            }
            finally { a.Destroy(); b.Destroy(); }
            yield return NetCutsceneTest.Barrier(h, 862);
        }

        public static IEnumerator Run(TestHarness h)
        {
            NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0;
            try
            {
                yield return NetCutsceneTest.Barrier(h, 801);
                yield return EndInsideWindow(h);
                yield return EndBeforeMirror(h);
                yield return DroppedTeammate(h);
                yield return StartInsideWindow(h);
                yield return EndAfterWatch(h);
                yield return DroppedAfterLockRanOut(h);
                yield return NetCutsceneTest.Barrier(h, 899);
            }
            finally { CutsceneGuard.TestLocalOnlyWindow(0f); StorySync.TestResolveDelay = 0f; NetTestHarness.BotLX = 0; NetTestHarness.BotButtons = 0; }
        }
    }
}
