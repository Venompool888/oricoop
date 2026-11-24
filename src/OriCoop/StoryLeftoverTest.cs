using System.Collections;
using Game;
using UnityEngine;

namespace OriCoop
{
    // T-207 / T-212 checks that run in the local suite (run-test.ps1), one scenario each.
    internal static class StoryLeftoverTest
    {
        static float Now { get { return Time.realtimeSinceStartup; } }

        static IEnumerator WaitState(FaderB.State state, float seconds)
        {
            float end = Now + seconds;
            while (Now < end && UI.Fader.CurrentState != state) yield return null;
        }

        // Scenario "afterblack" (T-212 item 1): the session ended while a mirrored story had the screen faded to black.
        // The end of the session is simulated (CutsceneGuard.TestSessionEnded); the fade and the repair are the real ones.
        // Part 1: the screen is black when the session ends: it is faded back in.
        //         ORICOOP_MUTATE_NO_POST_SESSION_FADE=1 must make it fail (the scenario fades it back itself).
        // Part 2 (review finding 9): a black screen that begins after the session ended is the game's own: left alone.
        public static IEnumerator AfterBlack(TestHarness h)
        {
            h.Check(UI.Fader != null, "afterblack: the game has a fader");
            if (UI.Fader == null) yield break;
            int fades = CutsceneGuard.PostSessionFades;
            UI.Fader.Fade(0.2f, 60f, 0.5f, null, null);   // to black, and stay there
            yield return WaitState(FaderB.State.FadeStay, 3f);
            bool wasBlack = UI.Fader.CurrentState == FaderB.State.FadeStay;
            CutsceneGuard.TestSessionEnded();
            float blackAt = Now, end = Now + 10f;
            while (Now < end && UI.Fader.CurrentState == FaderB.State.FadeStay) yield return null;
            bool back = UI.Fader.CurrentState != FaderB.State.FadeStay;
            h.Note("afterblack: black=" + wasBlack + " back=" + back + " after " + (Now - blackAt).ToString("0.0") + " s, repairs=" + (CutsceneGuard.PostSessionFades - fades));
            h.Check(wasBlack, "afterblack: the screen went fully black (setup)");
            h.Check(back && CutsceneGuard.PostSessionFades == fades + 1,
                "afterblack: a screen left black when the session ended is faded back in within 10 s (T-212)");
            if (!back) UI.Fader.FadeOut(0.5f);
            yield return WaitState(FaderB.State.Invisible, 3f);

            fades = CutsceneGuard.PostSessionFades;
            CutsceneGuard.TestSessionEnded();              // the session ends with a clear screen
            UI.Fader.Fade(0.2f, 60f, 0.5f, null, null);   // then the game's own cutscene goes black
            yield return WaitState(FaderB.State.FadeStay, 3f);
            wasBlack = UI.Fader.CurrentState == FaderB.State.FadeStay;
            end = Now + 6.5f;
            while (Now < end && UI.Fader.CurrentState == FaderB.State.FadeStay) yield return null;
            bool stayed = UI.Fader.CurrentState == FaderB.State.FadeStay;
            h.Note("afterblack own: black=" + wasBlack + " stayed=" + stayed + " repairs=" + (CutsceneGuard.PostSessionFades - fades));
            h.Check(wasBlack && stayed && CutsceneGuard.PostSessionFades == fades,
                "afterblack: a black screen that begins after the session ended is left alone for 6.5 s (the game's own cutscene)");
            UI.Fader.FadeOut(0.5f);
            yield return WaitState(FaderB.State.Invisible, 3f);
        }
    }
}
