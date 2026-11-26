using System;
using System.Collections.Generic;
using OriCoop.Diagnostics;

// T-252 (#340) audio watch, trace only: when a reading of the game's audio counts as "sound may be lost", and how
// often that is written. Expected values come from the rules (hold times, 15-unit listener distance, 30 s repeat,
// 100 episodes, 40 lines a minute), not from the code's output.
static class AudioWatchTests
{
    static int cases, failures;
    static void Run(string name, Action body)
    {
        cases++;
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Eq(object expected, object actual, string what)
    { if (!Equals(expected, actual)) throw new Exception(what + ": expected <" + expected + "> got <" + actual + ">"); }

    // A healthy reading in a level: one listener on the view, sounds playing and audible.
    static AudioReading Fine()
    {
        AudioReading r = new AudioReading();
        r.InGameplay = true; r.Running = true; r.Listeners = 1; r.Volume = 1f; r.SettingsMin = 0.8f;
        r.ListenerToView = 0.5f; r.Playing = 6; r.NearViewPlaying = 3; r.NearViewQuiet = 0;
        r.DspAdvance = 1.0; r.RealAdvance = 1f;
        return r;
    }

    // Feeds the same reading once a second from t=0 to t=last and returns the reasons at each second.
    static List<string> Feed(AudioWatch w, AudioReading r, int last)
    {
        List<string> got = new List<string>();
        for (int t = 0; t <= last; t++) got.Add(w.Reasons(r, t));
        return got;
    }

    static int Main()
    {
        Run("a normal reading has no reason", delegate {
            Eq("", new AudioWatch().Reasons(Fine(), 0f), "reasons");
            Eq(0, AudioWatch.Seen(Fine()).Count, "seen");
        });
        Run("the listener far from the view counts after one second, and goes at once", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.ListenerToView = 15.5f;
            Eq("", w.Reasons(r, 10f), "first sight");
            Eq(AudioWatch.OffView, w.Reasons(r, 11f), "held one second");
            r.ListenerToView = 14.9f;
            Eq("", w.Reasons(r, 12f), "back within 15 units");
        });
        Run("outside a level the listener's distance and silence do not count", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.InGameplay = false; r.ListenerToView = 40f; r.Playing = 0; r.NearViewPlaying = 0;
            Eq("", Feed(w, r, 10)[10], "title / loading");
        });
        Run("silence counts only after five seconds", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.Playing = 0; r.NearViewPlaying = 0;
            List<string> got = Feed(w, r, 6);
            Eq("", got[4], "4 s of silence");
            Eq(AudioWatch.Silent, got[5], "5 s of silence");
        });
        Run("silence while paused is not a loss", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.Playing = 0; r.NearViewPlaying = 0; r.Running = false;
            Eq("", Feed(w, r, 8)[8], "paused game");
        });
        Run("a sound that plays again restarts the silence wait", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading quiet = Fine(); quiet.Playing = 0; quiet.NearViewPlaying = 0;
            for (int t = 0; t <= 3; t++) w.Reasons(quiet, t);
            w.Reasons(Fine(), 4f);
            for (int t = 5; t <= 8; t++) Eq("", w.Reasons(quiet, t), "silent again for " + (t - 5) + " s");
            Eq("", w.Reasons(quiet, 9f), "silent again for 4 s");
            Eq(AudioWatch.Silent, w.Reasons(quiet, 10f), "silent again for 5 s");
        });
        Run("watching a teammate the listener does not follow", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.Spectating = true; r.ListenerToWatched = 22f; r.ListenerToView = 22f;
            Eq(AudioWatch.FarWatched + "," + AudioWatch.OffView, Feed(w, r, 1)[1], "both, sorted");
        });
        Run("sounds next to the view all too quiet: two needed, held two seconds", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading one = Fine(); one.NearViewPlaying = 1; one.NearViewQuiet = 1;
            Eq("", Feed(w, one, 5)[5], "one quiet sound is not enough");
            AudioWatch w2 = new AudioWatch();
            AudioReading all = Fine(); all.NearViewPlaying = 3; all.NearViewQuiet = 3;
            List<string> got = Feed(w2, all, 2);
            Eq("", got[1], "1 s");
            Eq(AudioWatch.ViewQuiet, got[2], "2 s");
            AudioWatch w3 = new AudioWatch();
            AudioReading some = Fine(); some.NearViewPlaying = 3; some.NearViewQuiet = 2;
            Eq("", Feed(w3, some, 5)[5], "one of three audible");
        });
        Run("no listener while the game starts or loads is not a loss (#442 test: 0.67 s after start)", delegate {
            AudioWatch w = new AudioWatch();
            AudioReading r = Fine(); r.InGameplay = false; r.Listeners = 0; r.Playing = 0; r.NearViewPlaying = 0;
            Eq("", Feed(w, r, 10)[10], "title / loading: no camera yet");
            r.Listeners = 2;
            Eq("", Feed(w, r, 10)[10], "title / loading: two listeners");
            AudioWatch w2 = new AudioWatch();
            AudioReading inLevel = Fine(); inLevel.Listeners = 0;
            List<string> got = Feed(w2, inLevel, 2);
            Eq("", got[1], "in a level, 1 s");
            Eq(AudioWatch.NoListener, got[2], "in a level, 2 s");
        });
        Run("listener count, pause, volumes and the audio clock", delegate {
            AudioReading r = Fine(); r.Listeners = 0;
            Eq(AudioWatch.NoListener, string.Join(",", AudioWatch.Seen(r).ToArray()), "no listener");
            r = Fine(); r.Listeners = 2;
            Eq(AudioWatch.ManyListeners, string.Join(",", AudioWatch.Seen(r).ToArray()), "two listeners");
            r = Fine(); r.Paused = true;
            Eq(AudioWatch.Paused, string.Join(",", AudioWatch.Seen(r).ToArray()), "paused listener in a running level");
            r.Running = false;
            Eq(0, AudioWatch.Seen(r).Count, "paused listener in a paused game");
            r = Fine(); r.Volume = 0.04f;
            Eq(AudioWatch.Muted, string.Join(",", AudioWatch.Seen(r).ToArray()), "volume");
            r = Fine(); r.SettingsMin = 0f;
            Eq(AudioWatch.SettingsZero, string.Join(",", AudioWatch.Seen(r).ToArray()), "a setting at 0");
            r = Fine(); r.SettingsMin = -1f;
            Eq(0, AudioWatch.Seen(r).Count, "settings unknown");
            r = Fine(); r.DspAdvance = 0.05; r.RealAdvance = 1f;
            Eq(AudioWatch.DspStalled, string.Join(",", AudioWatch.Seen(r).ToArray()), "audio clock stood still");
            r = Fine(); r.DspAdvance = 0.0; r.RealAdvance = 0.2f;
            Eq(0, AudioWatch.Seen(r).Count, "too short a gap to judge");
            r = Fine(); r.DspAdvance = -1;
            Eq(0, AudioWatch.Seen(r).Count, "first reading");
        });
        Run("an episode: start, still every 30 s, change, clear", delegate {
            AudioWatch w = new AudioWatch();
            float d;
            Eq(AudioWatch.Step.None, w.Next("", 0f, out d), "fine");
            Eq(AudioWatch.Step.Start, w.Next("silent", 10f, out d), "start");
            Eq(AudioWatch.Step.None, w.Next("silent", 39f, out d), "29 s later");
            Eq(AudioWatch.Step.Still, w.Next("silent", 40f, out d), "30 s later");
            Eq(30f, d, "still: duration");
            Eq(AudioWatch.Step.None, w.Next("silent", 41f, out d), "right after still");
            Eq(AudioWatch.Step.Change, w.Next("listener_off_view,silent", 45f, out d), "change");
            Eq(35f, d, "change: duration");
            Eq(AudioWatch.Step.Clear, w.Next("", 50f, out d), "clear");
            Eq(40f, d, "clear: duration from the start");
            Eq(1, w.Episodes, "episodes");
        });
        Run("after 100 episodes nothing more is written", delegate {
            AudioWatch w = new AudioWatch();
            float d; int starts = 0, clears = 0;
            for (int i = 0; i < 120; i++)
            {
                if (w.Next("silent", i * 2f, out d) == AudioWatch.Step.Start) starts++;
                if (w.Next("", i * 2f + 1f, out d) == AudioWatch.Step.Clear) clears++;
            }
            Eq(100, starts, "starts"); Eq(100, clears, "clears"); Eq(20, w.EpisodesDropped, "dropped");
        });
        Run("budget: 40 lines a minute, refilled over time, dropped lines counted", delegate {
            AudioLogBudget b = new AudioLogBudget(40f);
            int ok = 0;
            for (int i = 0; i < 50; i++) if (b.Take(0f)) ok++;
            Eq(40, ok, "burst"); Eq(10, b.Suppressed, "suppressed");
            Eq(10, b.TakeSuppressed(), "taken"); Eq(0, b.Suppressed, "reset");
            Eq(false, b.Take(1f), "1 s later: 0.67 of a line back");
            Eq(true, b.Take(1.6f), "1.6 s later: one line back");
            Eq(true, b.Take(120f), "two minutes later");
        });
        Run("sound text stays one value", delegate {
            Eq("ori_jump@3.2/0.5/20/Linear/1", AudioText.Sound("ori jump", 3.24f, 0.5f, 20f, "Linear", 1f), "sound");
            Eq("music@40/0.8/500/Logarithmic/0", AudioText.Sound("music", 40f, 0.8f, 500f, "Logarithmic", 0f), "2D sound");
            Eq("a_b_c_d_e", AudioText.Clean("a;b@c/d=e"), "separators");
            Eq("-", AudioText.Clean(null), "no name");
            Eq(48, AudioText.Clean(new string('x', 80)).Length, "long name");
        });
        Console.WriteLine(cases + " cases, " + failures + " failures");
        return failures == 0 ? 0 : 1;
    }
}
