using System;
using System.Collections.Generic;
using OriCoop.World;

// T-250 (#338, choice B): the host's level timers set the pace. A timer here is the Archive's one float of phase,
// simulated both ways the game could keep it (counting up to the period and back to 0, or down to 0 and back to the
// period); it "fires" each time it wraps. The guest gets the host's float through WorldSync's resend schedule with
// one-way latency and takes it by TimerPhase.Judge.
static class TimerPhaseTests
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    sealed class Timer
    {
        public float Phase, Period;
        public bool Down;
        public int Fires;
        public readonly List<float> FiredAt = new List<float>();
        public void Step(float dt, float now)
        {
            if (Down) { Phase -= dt; if (Phase <= 0f) { Phase += Period; Fires++; FiredAt.Add(now); } }
            else { Phase += dt; if (Phase >= Period) { Phase -= Period; Fires++; FiredAt.Add(now); } }
        }
    }

    struct InFlight { public float Arrive, Value; }

    // Runs host and guest for seconds; returns the guest. resend: false = before T-250 (phase crosses only once).
    // fixedGap > 0: resend every fixedGap seconds instead of TimerPhaseSchedule.
    static Timer Run(float period, bool down, float offset, float latency, float seconds, bool resend, float fixedGap,
        out Timer host, out int taken)
    {
        const float dt = 1f / 60f;
        host = new Timer { Period = period, Down = down, Phase = down ? period : 0f };
        Timer guest = new Timer { Period = period, Down = down };
        guest.Phase = host.Phase; for (float t = 0f; t < offset; t += dt) guest.Step(dt, -1f);
        guest.Fires = 0; guest.FiredAt.Clear();
        TimerPhaseSchedule schedule = new TimerPhaseSchedule(250u);
        int gapFrames = (int)Math.Round(fixedGap * 60f); taken = 0;
        List<InFlight> wire = new List<InFlight>();
        int frames = (int)(seconds * 60f);
        for (int frame = 0; frame < frames; frame++)
        {
            float now = frame * dt;
            host.Step(dt, now); guest.Step(dt, now);
            bool due = gapFrames > 0 ? frame % gapFrames == 0 : schedule.Due(now);
            if (resend && due) wire.Add(new InFlight { Arrive = now + latency, Value = host.Phase });
            for (int i = wire.Count - 1; i >= 0; i--)
            {
                if (wire[i].Arrive > now) continue;
                if (TimerPhase.Judge(guest.Phase, wire[i].Value) == TimerPhase.Verdict.Take) { guest.Phase = wire[i].Value; taken++; }
                wire.RemoveAt(i);
            }
        }
        return guest;
    }

    // A repeating TimedTrigger as the #443 probe showed it: counts up to Duration, fires, then stays Paused while its
    // projectile lives (ProjectileSpawner.WaitForProjectileToBeDestroyed), the projectile's life a little different per PC.
    sealed class Shooter
    {
        public float Phase, Duration, Alive, PausedFor;
        public readonly List<float> FiredAt = new List<float>();
        public bool Paused { get { return PausedFor > 0f; } }
        public void Step(float dt, float now)
        {
            if (PausedFor > 0f) { PausedFor -= dt; return; }
            Phase += dt;
            if (Phase >= Duration) { Phase = 0f; FiredAt.Add(now); PausedFor = Alive; }
        }
    }

    // The host sends only a running trigger; the guest judges only while its own runs (WorldSync's rule).
    static void RunShooter(float duration, float hostAlive, float guestAlive, float offset, float latency, float seconds,
        out Shooter host, out Shooter guest, out int taken, bool resend = true)
    {
        const float dt = 1f / 60f;
        host = new Shooter { Duration = duration, Alive = hostAlive };
        guest = new Shooter { Duration = duration, Alive = guestAlive };
        for (float t = 0f; t < offset; t += dt) host.Step(dt, -1f);   // the guest loaded the scene offset s later
        host.FiredAt.Clear();
        TimerPhaseSchedule schedule = new TimerPhaseSchedule(250u);
        List<InFlight> wire = new List<InFlight>(); taken = 0;
        int frames = (int)(seconds * 60f);
        for (int frame = 0; frame < frames; frame++)
        {
            float now = frame * dt;
            host.Step(dt, now); guest.Step(dt, now);
            if (resend && schedule.Due(now) && TimerPhase.Keeps(true, host.Paused)) wire.Add(new InFlight { Arrive = now + latency, Value = host.Phase });
            for (int i = wire.Count - 1; i >= 0; i--)
            {
                if (wire[i].Arrive > now) continue;
                if (TimerPhase.Keeps(true, guest.Paused)
                    && TimerPhase.Judge(guest.Phase, wire[i].Value, duration) == TimerPhase.Verdict.Take) { guest.Phase = wire[i].Value; taken++; }
                wire.RemoveAt(i);
            }
        }
    }

    // How far apart the two timers fire (seconds, the shorter way round the period).
    static float Apart(Timer host, Timer guest)
    {
        float d = Math.Abs(host.Phase - guest.Phase) % host.Period;
        return Math.Min(d, host.Period - d);
    }

    // Fewest seconds between two guest fires after the first takeover: a double shot shows up as a gap far below the period.
    static float ShortestGap(Timer t, float after)
    {
        float best = float.MaxValue;
        for (int i = 1; i < t.FiredAt.Count; i++)
            if (t.FiredAt[i - 1] >= after) best = Math.Min(best, t.FiredAt[i] - t.FiredAt[i - 1]);
        return best;
    }

    public static void Register(Action<string, Action> run)
    {
        run("T-250: the only timer type is OnTimerPerformAction (#436: TimedTrigger is a one-shot delay)", delegate {
            Check(TimerPhase.IsTimer("OnTimerPerformAction") && TimerPhase.IsTimer("Game.OnTimerPerformAction"), "timers");
            Check(!TimerPhase.IsTimer("TimedTrigger") && !TimerPhase.IsTimer("Game.TimedTrigger"), "TimedTrigger is not resent");
            Check(!TimerPhase.IsTimer("SinMovement") && !TimerPhase.IsTimer("LaserPulsator") && !TimerPhase.IsTimer(null)
                && !TimerPhase.IsTimer("NotOnTimerPerformActionX"), "not timers");
        });
        run("#443: a repeating TimedTrigger is a level timer while it runs; a one-shot or paused one is not", delegate {
            Check(TimerPhase.IsTimedTrigger("TimedTrigger") && TimerPhase.IsTimedTrigger("Game.TimedTrigger"), "TimedTrigger type");
            Check(!TimerPhase.IsTimedTrigger("OnTimerPerformAction") && !TimerPhase.IsTimedTrigger(null)
                && !TimerPhase.IsTimedTrigger("TimedTriggerX"), "not TimedTrigger");
            Check(TimerPhase.MayBeTimer("OnTimerPerformAction") && TimerPhase.MayBeTimer("TimedTrigger")
                && !TimerPhase.MayBeTimer("ProjectileSpawner") && !TimerPhase.MayBeTimer("TimedDoor"), "may be a timer");
            Check(TimerPhase.Keeps(true, false), "repeating, running: kept");
            Check(!TimerPhase.Keeps(true, true), "repeating, paused: left");
            Check(!TimerPhase.Keeps(false, false) && !TimerPhase.Keeps(false, true), "one-shot: left (#436)");
        });
        run("#443: with the period known, a float near the period counts as near the restart too", delegate {
            Check(TimerPhase.Judge(2.9f, 1.0f, 3f) == TimerPhase.Verdict.NearRestart, "guest about to fire (counting up)");
            Check(TimerPhase.Judge(1.0f, 2.95f, 3f) == TimerPhase.Verdict.NearRestart, "host just reset (counting down)");
            Check(TimerPhase.Judge(2.9f, 1.0f, 0f) == TimerPhase.Verdict.Take, "period unknown: as before");
            Check(TimerPhase.Judge(1.0f, 2.0f, 3f) == TimerPhase.Verdict.Take, "1 s apart, mid period");
            Check(TimerPhase.Judge(1.0f, 1.1f, 3f) == TimerPhase.Verdict.InStep, "in step");
            Check(TimerPhase.Judge(1.0f, 2.0f, float.NaN) == TimerPhase.Verdict.Take, "NaN period ignored");
        });
        foreach (float offset in new float[] { 0.8f, 1.5f, 2.2f })
        foreach (float jitter in new float[] { 0f, 0.15f })
        {
            float off = offset, jit = jitter;
            run("#443: a Ginso shooter (Duration 3, paused while its projectile lives) " + off + " s behind, projectile life +"
                + jit + " s on the guest, fires with the host's", delegate {
                Shooter host, guest; int taken;
                RunShooter(3f, 1.4f, 1.4f + jit, off, 0.08f, 60f, out host, out guest, out taken);
                float worst = 0f;
                foreach (float t in host.FiredAt)
                {
                    if (t < 30f || t > 55f) continue;
                    float nearest = float.MaxValue;
                    foreach (float g in guest.FiredAt) nearest = Math.Min(nearest, Math.Abs(g - t));
                    worst = Math.Max(worst, nearest);
                }
                Check(worst <= TimerPhase.Tolerance + jit + 0.1f, "guest fires up to " + worst + " s from the host's after 30 s, taken " + taken);
                Check(taken >= 1, "never taken");
                float gap = float.MaxValue;
                for (int i = 1; i < guest.FiredAt.Count; i++) if (guest.FiredAt[i - 1] >= 5f) gap = Math.Min(gap, guest.FiredAt[i] - guest.FiredAt[i - 1]);
                Check(gap >= 3f + 1.4f - TimerPhase.Tolerance - 0.1f, "guest fired twice within " + gap + " s");
            });
        }
        run("#443: without the resend the same shooter stays out of step", delegate {
            Shooter host, guest; int taken;
            RunShooter(3f, 1.4f, 1.4f, 1.5f, 0.08f, 60f, out host, out guest, out taken, false);
            float t = host.FiredAt[host.FiredAt.Count - 1], nearest = float.MaxValue;
            foreach (float g in guest.FiredAt) nearest = Math.Min(nearest, Math.Abs(g - t));
            Check(nearest > 1f, "in step without a resend: " + nearest);
        });
        run("#436: a guest keeps a timer it has not seen yet (unloaded scene), so the digests still agree", delegate {
            Check(TimerPhase.GuestRoute(false, false, false) == TimerPhase.Route.Ordinary, "unloaded, first sighting: ordinary path");
            Check(TimerPhase.GuestRoute(false, false, true) == TimerPhase.Route.Skip, "unloaded, already known: resend skipped");
            Check(TimerPhase.GuestRoute(true, false, false) == TimerPhase.Route.Judge, "live: judged");
            Check(TimerPhase.GuestRoute(true, false, true) == TimerPhase.Route.Judge, "live and known: judged");
            Check(TimerPhase.GuestRoute(true, true, false) == TimerPhase.Route.Ordinary, "disabled here, unknown: ordinary path");
            Check(TimerPhase.GuestRoute(true, true, true) == TimerPhase.Route.Skip, "disabled here, known: skipped");
        });
        run("T-250: Judge leaves an in-step timer and one near its restart, takes one out of step", delegate {
            Check(TimerPhase.Judge(1.0f, 1.2f) == TimerPhase.Verdict.InStep, "0.2 s apart");
            Check(TimerPhase.Judge(1.0f, 2.0f) == TimerPhase.Verdict.Take, "1 s apart");
            Check(TimerPhase.Judge(0.05f, 1.95f) == TimerPhase.Verdict.NearRestart, "guest just fired, host about to");
            Check(TimerPhase.Judge(1.95f, 0.1f) == TimerPhase.Verdict.NearRestart, "host just fired");
            Check(TimerPhase.Judge(float.NaN, 1f) == TimerPhase.Verdict.Unknown, "NaN");
            float p;
            Check(!TimerPhase.TryRead(new byte[3], out p) && !TimerPhase.TryRead(null, out p), "short layout read");
            Check(TimerPhase.TryRead(BitConverter.GetBytes(1.5f), out p) && p == 1.5f, "4-byte layout");
        });
        run("T-250: the resend gap varies between ResendMin and ResendMax", delegate {
            TimerPhaseSchedule s = new TimerPhaseSchedule(250u);
            Check(s.Due(0f), "first call not due");
            float last = 0f, min = float.MaxValue, max = 0f; int dues = 0;
            for (float t = 0.01f; t < 120f; t += 0.01f)
                if (s.Due(t)) { float gap = t - last; last = t; dues++; min = Math.Min(min, gap); max = Math.Max(max, gap); }
            Check(min >= TimerPhase.ResendMin - 0.02f && max <= TimerPhase.ResendMax + 0.02f, "gaps " + min + ".." + max);
            Check(max - min > 0.3f, "gap does not vary: " + min + ".." + max);
            Check(dues > 80 && dues < 180, dues + " resends in 2 min");
        });
        foreach (bool down in new bool[] { false, true })
        foreach (float period in new float[] { 1.5f, 2f, 3f, 4.5f })
        foreach (float offsetShare in new float[] { 0.25f, 0.5f, 0.75f })
        {
            bool d = down; float per = period, off = period * offsetShare;
            run("T-250: a timer " + off.ToString("0.00") + " s behind the host (period " + per + " s, "
                + (d ? "counting down" : "counting up") + ") is in step within " + TimerPhase.Tolerance + " s and stays", delegate {
                Timer host; int taken;
                Timer guest = Run(per, d, off, 0.08f, 30f, true, 0f, out host, out taken);
                Check(Apart(host, guest) <= TimerPhase.Tolerance + 0.1f, "still " + Apart(host, guest) + " s apart after 30 s, taken " + taken);
                Check(taken >= 1 && taken <= 4, "taken " + taken + " times (one or two should do)");
                float gap = ShortestGap(guest, 10f);
                Check(gap >= per - TimerPhase.Tolerance - 0.1f, "guest fired twice within " + gap + " s after it was in step");
                Check(Math.Abs(guest.Fires - host.Fires) <= 1, "fires host " + host.Fires + " guest " + guest.Fires);
            });
        }
        run("T-250: without the resend (before T-250) the guest stays out of step", delegate {
            Timer host; int taken;
            Timer guest = Run(2f, false, 1f, 0.08f, 30f, false, 0f, out host, out taken);
            Check(Apart(host, guest) > 0.9f, "in step without a resend: " + Apart(host, guest));
        });
        run("T-250: a fixed 1 s gap on a 2 s timer half a period out never takes it; the varied gap does", delegate {
            Timer host; int taken;
            Timer guest = Run(2f, false, 1f, 0f, 30f, true, 1f, out host, out taken);
            Check(taken == 0 && Apart(host, guest) > 0.9f, "fixed gap took it: taken " + taken + ", apart " + Apart(host, guest));
            guest = Run(2f, false, 1f, 0f, 30f, true, 0f, out host, out taken);
            Check(Apart(host, guest) <= TimerPhase.Tolerance + 0.05f, "varied gap apart " + Apart(host, guest));
        });
    }
}
