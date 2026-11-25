using System;

namespace OriCoop.World
{
    // T-250 (#338), the maintainer's choice B: the host's level timers set the pace. Two kinds of level timer keep one
    // float of phase in their Archive (4 bytes, WorldSync's AmbientPhase layout):
    // - OnTimerPerformAction (e.g. moonGrottoLaserPuzzle).
    // - TimedTrigger with Repeat on: the Ginso Tree puzzle shooter (#443 probe: projectileSpawnerPinkPlant, Duration 3,
    //   Repeat, started from TimedTrigger.FixedUpdate) and the Moon Grotto / Forlorn shooters are built this way. A
    //   one-shot TimedTrigger (Repeat off) stays out (#436): it is a delay, and writing the host's time into it fired its
    //   action on the guest at the wrong moment. A repeating one while Paused (its ProjectileSpawner waits for the last
    //   projectile to be destroyed) stays out too: its float is not moving, so it says nothing about the pace.
    // Before this ticket that phase crossed only in
    // the first full world snapshot; a scene loaded later started its timers when it loaded on each PC, so the same
    // shooter fired at different moments on each PC. Now the host sends its timers' phase again every second or so
    // (existing world message 12, no protocol change) and a guest takes it when its own phase is off by more than
    // Tolerance. Unity-free (unit tests: src\Tests\World\TimerPhaseTests.cs); the game side is in WorldSync.
    public static class TimerPhase
    {
        // About the time a shooter's bullet needs to look out of step; also covers one-way latency (the host's value is
        // RTT/2 old when it is applied, and the Archive does not say whether the float counts up or down).
        public const float Tolerance = 0.25f;
        public const float ResendMin = 0.7f, ResendMax = 1.3f;

        public static bool IsTimer(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return Ends(typeName, "OnTimerPerformAction");
        }

        // A TimedTrigger save type: a level timer only while Repeat is on and it is not Paused (Keeps).
        public static bool IsTimedTrigger(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return Ends(typeName, "TimedTrigger");
        }

        // Whether a blob of this type may be a level timer's phase (the guest's rule looks at it).
        public static bool MayBeTimer(string typeName) { return IsTimer(typeName) || IsTimedTrigger(typeName); }

        // Whether one TimedTrigger is kept in step right now: Repeat on and not Paused.
        public static bool Keeps(bool repeat, bool paused) { return repeat && !paused; }

        // Guest, a timer blob that came outside a snapshot. Ordinary: the world blob path as for any object (the timer's
        // first sighting, e.g. in a scene this PC has not loaded: #436, skipping it left the guest's digest without the
        // object and forced a full resync). Skip: already known and not live here, nothing to keep in step. Judge: live
        // here, TakeTimerPhase decides.
        public static Route GuestRoute(bool live, bool bad, bool known)
        {
            if (live && !bad) return Route.Judge;
            return known ? Route.Skip : Route.Ordinary;
        }

        public enum Route { Ordinary, Skip, Judge }

        static bool Ends(string full, string name)
        {
            return full == name || full.EndsWith("." + name, StringComparison.Ordinal);
        }

        // Read the phase from a timer's Archive bytes. False: not the 4-byte layout this rule knows.
        public static bool TryRead(byte[] bytes, out float phase)
        {
            phase = 0f;
            if (bytes == null || bytes.Length != 4) return false;
            phase = BitConverter.ToSingle(bytes, 0);
            return !float.IsNaN(phase) && !float.IsInfinity(phase);
        }

        // Whether a guest takes the host's phase for one timer.
        // - Off by no more than Tolerance: already in step, leave it (writing it every second would only jitter).
        // - Either side within Tolerance of 0: that side just restarted its period (it fired, or is about to). The other
        //   side may be about to restart too, so the two can be in step even though the numbers are far apart;
        //   writing the host's value there could fire the shot a second time. The next resend, at another point of the
        //   period (ResendMin..ResendMax varies), decides.
        // - period > 0 (a TimedTrigger's Duration, known on this PC): within Tolerance of the period counts as near the
        //   restart too, whichever way the float counts.
        public static Verdict Judge(float local, float remote) { return Judge(local, remote, 0f); }

        public static Verdict Judge(float local, float remote, float period)
        {
            if (float.IsNaN(local) || float.IsNaN(remote) || float.IsInfinity(local) || float.IsInfinity(remote))
                return Verdict.Unknown;
            if (Math.Abs(remote - local) <= Tolerance) return Verdict.InStep;
            if (Math.Abs(local) < Tolerance || Math.Abs(remote) < Tolerance) return Verdict.NearRestart;
            if (period > 0f && !float.IsNaN(period) && !float.IsInfinity(period)
                && (Math.Abs(period - local) < Tolerance || Math.Abs(period - remote) < Tolerance)) return Verdict.NearRestart;
            return Verdict.Take;
        }

        public enum Verdict { Take, InStep, NearRestart, Unknown }
    }

    // When the host sends its timers' phase again. The gap varies between ResendMin and ResendMax so that resends do
    // not fall on the same few points of a timer whose period is a multiple of the gap: there NearRestart could skip
    // a timer that is out of step on every resend.
    public sealed class TimerPhaseSchedule
    {
        float m_next;
        uint m_seed;
        bool m_started;

        public TimerPhaseSchedule(uint seed) { m_seed = seed == 0 ? 1u : seed; }

        // True when a resend is due at now (seconds, any clock that only moves forward). The first call is due.
        public bool Due(float now)
        {
            if (m_started && now < m_next) return false;
            m_started = true;
            m_next = now + NextGap();
            return true;
        }

        public void Reset() { m_started = false; }

        float NextGap()
        {
            m_seed = m_seed * 1664525u + 1013904223u;   // LCG: same sequence on every run, no UnityEngine.Random
            float unit = (m_seed >> 8) / 16777216f;     // [0, 1)
            return TimerPhase.ResendMin + unit * (TimerPhase.ResendMax - TimerPhase.ResendMin);
        }
    }
}
