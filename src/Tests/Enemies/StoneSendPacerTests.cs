using System;
using OriCoop.Authority;

// #424: an owned push stone at rest sends a heartbeat, not 20 states a second.
static class StoneSendPacerTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    // Steps a stone at 60 fps from t0 for seconds; pose(t) gives x and vx. Returns how many states were sent.
    static int Count(StoneSendPacer pacer, float t0, float seconds, uint epoch, bool active, Func<float, float[]> pose)
    {
        int sent = 0;
        for (int f = 0; f < (int)(seconds * 60f); f++)
        {
            float t = t0 + f / 60f; float[] p = pose(t);
            if (pacer.Take(t, epoch, active, p[0], 2f, 0f, p[1], 0f, 0f)) sent++;
        }
        return sent;
    }

    static float[] Still(float t) { return new[] { 5f, 0f }; }

    // The pacing before #424: one state every 0.05 s for as long as this PC owns the stone.
    static int OldCount(float t0, float seconds)
    {
        int sent = 0; float at = -10f;
        for (int f = 0; f < (int)(seconds * 60f); f++) { float t = t0 + f / 60f; if (t - at >= StoneSendPacer.Interval) { at = t; sent++; } }
        return sent;
    }

    internal static void Register(Action<string, Action> run)
    {
        run("#424 a stone lying still sends a heartbeat twice a second", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Check(pacer.Take(0f, 3, false, 5f, 2f, 0f, 0f, 0f, 0f), "the first state goes out at once");
            int burst = Count(pacer, 1f / 60f, 0.5f, 3, false, Still);
            int idle = Count(pacer, 0.5f + 1f / 60f, 4f, 3, false, Still);
            Check(idle >= 7 && idle <= 9, "4 s at rest: about 8 heartbeats, got " + idle);
            int old = OldCount(0f, 4.5f);
            Check((burst + idle) * 3 < old, "far fewer than the " + old + " states the old pacing sent, got " + (burst + idle));
        });
        run("#424 a moving stone still sends 20 states a second", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            int sent = Count(pacer, 0f, 1f, 3, false, delegate(float t) { return new[] { 5f + t, 1f }; });
            Check(sent == OldCount(0f, 1f), "1 s of sliding: as many states as before, got " + sent + " against " + OldCount(0f, 1f));
        });
        run("#424 a push sends at full rate even before the stone moves", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 2f, 3, false, Still);
            int sent = Count(pacer, 2f, 1f, 3, true, Still);
            Check(sent >= OldCount(2f, 1f) - 1, "an owner that pushes keeps its partner's view as fresh as before, got " + sent);
        });
        run("#424 the first frame of a push or of its stop goes out without waiting for the heartbeat", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 2f, 3, false, Still);
            Check(pacer.Take(2.1f, 3, true, 5f, 2f, 0f, 0f, 0f, 0f), "the push starts: sent at once");
            Count(pacer, 2.11f, 0.3f, 3, true, Still);
            Check(pacer.Take(2.5f, 3, false, 5f, 2f, 0f, 0f, 0f, 0f), "the push stops: sent at once");
        });
        run("#424 a new owner's first state goes out at once", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 2f, 3, false, Still);
            Check(pacer.Take(2.1f, 4, false, 5f, 2f, 0f, 0f, 0f, 0f), "a lease epoch the partner has no state of yet");
        });
        run("#424 the resting pose is repeated for a moment after the stone stops", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 1f, 3, false, delegate(float t) { return new[] { 5f + t, 1f }; });
            int settle = Count(pacer, 1f, 0.45f, 3, false, delegate(float t) { return new[] { 6f, 0f }; });
            Check(settle >= 6, "states are not reliable: the stop is sent several times, got " + settle);
        });
        run("#424 a slow drift goes out once it adds up", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 2f, 3, false, Still);
            Check(!pacer.Take(2.02f, 3, false, 5.005f, 2f, 0f, 0.01f, 0f, 0f), "half a hundredth: waits for the heartbeat");
            Check(pacer.Take(2.1f, 3, false, 5.02f, 2f, 0f, 0.01f, 0f, 0f), "two hundredths from the last sent pose: sent");
        });
        run("#424 a turn across 360 degrees is a small turn", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            pacer.Take(0f, 3, false, 5f, 2f, 359.95f, 0f, 0f, 0f);
            Count(pacer, 0.02f, 2f, 3, false, Still);
            Check(!pacer.Take(2.03f, 3, false, 5f, 2f, 0.05f, 0f, 0f, 0f), "0.1 degree across the wrap is not a move");
        });
        run("#424 never more than 20 states a second", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Check(pacer.Take(0f, 3, true, 5f, 2f, 0f, 1f, 0f, 0f), "first");
            Check(!pacer.Take(0.03f, 4, true, 9f, 2f, 0f, 1f, 0f, 0f), "even a new epoch waits out the 0.05 s interval");
            Check(pacer.Take(0.05f, 4, true, 9f, 2f, 0f, 1f, 0f, 0f), "then goes out");
        });
        run("#424 a reset sends the next state at once", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 2f, 3, false, Still);
            pacer.Reset();
            Check(pacer.Take(2.02f, 3, false, 5f, 2f, 0f, 0f, 0f, 0f), "after an ownership reset");
        });
        run("#424 a resting stone's solver jitter is not movement", delegate {
            // A body lying on the ground reports a small residual velocity every step and wobbles by a few thousandths.
            StoneSendPacer pacer = new StoneSendPacer();
            Count(pacer, 0f, 1f, 3, false, Still);
            pacer.ResetStats();
            int f = 0;
            int sent = Count(pacer, 1f, 2f, 3, false, delegate(float t) { f++; return new[] { 5f + ((f & 1) == 0 ? 0.003f : -0.003f), (f & 1) == 0 ? 0.06f : -0.08f }; });
            Check(sent >= 3 && sent <= 5, "2 s of jitter at rest: heartbeats only, got " + sent + " (" + pacer.Why() + ")");
            Check(pacer.Heartbeats == sent && pacer.Moving == 0 && pacer.Moved == 0, "every state counted as a heartbeat (" + pacer.Why() + ")");
        });
        run("#424 the reasons for the states sent are counted", delegate {
            StoneSendPacer pacer = new StoneSendPacer();
            Check(pacer.Take(0f, 3, false, 5f, 2f, 0f, 0f, 0f, 0f) && pacer.First == 1, "the first state");
            Check(pacer.Take(0.1f, 4, false, 5f, 2f, 0f, 0f, 0f, 0f) && pacer.NewEpoch == 1, "a new epoch");
            Check(pacer.Take(0.2f, 4, true, 5f, 2f, 0f, 0f, 0f, 0f) && pacer.Active == 1, "a push");
            Check(pacer.Take(0.3f, 4, false, 5f, 2f, 0f, 0f, 0f, 0f) && pacer.Active == 2, "the push stops");
            Check(pacer.Take(0.4f, 4, false, 5.5f, 2f, 0f, 2f, 0f, 0f) && pacer.Moving == 1, "sliding");
            Check(pacer.Take(0.5f, 4, false, 5.6f, 2f, 0f, 0f, 0f, 0f) && pacer.Moved == 1, "moved, no speed reported");
            Check(pacer.Take(0.6f, 4, false, 5.6f, 2f, 0f, 0f, 0f, 0f) && pacer.Settling == 1, "settling");
            Check(pacer.Why().Contains("max speed 2.000"), "the largest speed is kept: " + pacer.Why());
            pacer.ResetStats();
            Check(pacer.First + pacer.NewEpoch + pacer.Active + pacer.Moving + pacer.Moved + pacer.Settling + pacer.Heartbeats == 0 && pacer.MaxSpeed == 0f, "stats reset");
        });
    }
}
