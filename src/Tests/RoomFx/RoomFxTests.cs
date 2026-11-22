using System;
using OriCoop.Ui;

// T-184 unit tests for the room pages' animated effects (Ui/RoomFxLogic). Expected values come from the ticket's spec:
// smoke drifts off the banner's left / right ends and top / bottom edges, sparks stay on the frames' straight edges,
// everything fades in and out (no popping), the selected button breathes with a ~2.2 s period and a shimmer sweeps
// across it then rests, and every particle is a pure function of (id, index, time).
static class RoomFxTests
{
    static int s_failed, s_passed;

    static void Ok(string name, bool ok, string detail)
    {
        if (ok) s_passed++;
        else { s_failed++; Console.WriteLine("FAIL " + name + (detail != null ? ": " + detail : "")); }
    }
    static void Near(string name, double expected, double actual, double eps)
    {
        Ok(name, Math.Abs(expected - actual) <= eps, "expected " + expected + " got " + actual);
    }

    static int Main()
    {
        HashRange();
        PhaseBoundaries();
        FadeShape();
        SmokeSides();
        SmokeFadesInAndOut();
        SparksOnEdges();
        SparksGlideWithoutJumps();
        SelectedBrighter();
        PulseAndShimmer();
        Deterministic();
        Console.WriteLine("roomfx tests: " + s_passed + " passed, " + s_failed + " failed");
        return s_failed == 0 ? 0 : 1;
    }

    static void HashRange()
    {
        float min = 1f, max = 0f; int same = 0; float prev = -1f;
        for (int a = -50; a < 50; a++)
            for (int b = 0; b < 50; b++)
            {
                float h = RoomFxLogic.Hash(a, b);
                if (h < min) min = h;
                if (h > max) max = h;
                if (h == prev) same++;
                prev = h;
            }
        Ok("hash >= 0", min >= 0f, "min " + min);
        Ok("hash < 1", max < 1f, "max " + max);
        Ok("hash spreads over [0,1)", min < 0.02f && max > 0.98f, min + ".." + max);
        Ok("hash neighbours differ", same < 5, same + " repeats");
        Ok("seed differs per id", RoomFxLogic.Seed("csroom.title.banner") != RoomFxLogic.Seed("csroom.name.banner"), null);
        Ok("seed of null is stable", RoomFxLogic.Seed(null) == RoomFxLogic.Seed(null), null);
    }

    static void PhaseBoundaries()
    {
        Near("phase t=0", 0, RoomFxLogic.Phase(0f, 4f, 0f), 1e-6);
        Near("phase half life", 0.5, RoomFxLogic.Phase(2f, 4f, 0f), 1e-6);
        Near("phase just before the end", 0.99975, RoomFxLogic.Phase(3.999f, 4f, 0f), 1e-4);
        Near("phase wraps at one life", 0, RoomFxLogic.Phase(4f, 4f, 0f), 1e-6);
        Near("phase offset", 0.25, RoomFxLogic.Phase(0f, 4f, 0.25f), 1e-6);
        Near("phase offset wraps", 0.25, RoomFxLogic.Phase(3f, 4f, 0.5f), 1e-6);
        Near("phase negative time stays in [0,1)", 0.75, RoomFxLogic.Phase(-1f, 4f, 0f), 1e-6);
        Near("phase no life", 0, RoomFxLogic.Phase(3f, 0f, 0.3f), 1e-6);
        Near("phase negative life", 0, RoomFxLogic.Phase(3f, -2f, 0.3f), 1e-6);
    }

    static void FadeShape()
    {
        Near("fade at birth", 0, RoomFxLogic.Fade(0f), 1e-6);
        Near("fade mid life", 1, RoomFxLogic.Fade(0.5f), 1e-6);
        Near("fade at death", 0, RoomFxLogic.Fade(1f), 1e-6);
        Near("fade before birth", 0, RoomFxLogic.Fade(-0.2f), 1e-6);
        Near("fade after death", 0, RoomFxLogic.Fade(1.3f), 1e-6);
        Ok("fade symmetric", Math.Abs(RoomFxLogic.Fade(0.2f) - RoomFxLogic.Fade(0.8f)) < 1e-5, null);
    }

    // Puffs i%3 != 2 come off the ends: even i the left end, odd i the right end, moving outwards over their life;
    // the others come off the top / bottom edges in the middle part.
    static void SmokeSides()
    {
        int seed = RoomFxLogic.Seed("csroom.title.banner");
        for (int i = 0; i < RoomFxLogic.SmokeCount; i++)
        {
            float uMin = 9f, uMax = -9f, vMin = 9f, vMax = -9f;
            for (float t = 0f; t < 20f; t += 0.05f)
            {
                FxParticle f = RoomFxLogic.Smoke(seed, i, t);
                if (f.Alpha <= 0.05f) continue;
                uMin = Math.Min(uMin, f.U); uMax = Math.Max(uMax, f.U); vMin = Math.Min(vMin, f.V); vMax = Math.Max(vMax, f.V);
                Ok("smoke alpha <= 1 #" + i, f.Alpha <= 1f, "alpha " + f.Alpha);
                Ok("smoke size in range #" + i, f.Size > 0.3f && f.Size < 1.5f, "size " + f.Size);
            }
            if (i % 3 == 2) Ok("edge puff stays in the middle span #" + i, uMin > 0.1f && uMax < 0.9f, uMin + ".." + uMax);
            else if (i % 2 == 0) Ok("left puff on the left end, drifting out #" + i, uMax < 0.2f && uMin < 0.1f, uMin + ".." + uMax);
            else Ok("right puff on the right end, drifting out #" + i, uMin > 0.8f && uMax > 0.9f, uMin + ".." + uMax);
            Ok("puff stays near the banner #" + i, uMin > -0.3f && uMax < 1.3f && vMin > -0.7f && vMax < 1.7f, "u " + uMin + ".." + uMax + " v " + vMin + ".." + vMax);
        }
    }

    // No popping: across its lives a puff's alpha goes through ~0 (birth / death), is clearly visible part of the
    // time, and never jumps between two 60 fps frames.
    static void SmokeFadesInAndOut()
    {
        int seed = 1234;
        for (int i = 0; i < RoomFxLogic.SmokeCount; i++)
        {
            float lo = 1f, hi = 0f, prev = -1f, jump = 0f;
            for (float t = 0f; t < 16f; t += 1f / 60f)
            {
                float a = RoomFxLogic.Smoke(seed, i, t).Alpha;
                lo = Math.Min(lo, a); hi = Math.Max(hi, a);
                if (prev >= 0f) jump = Math.Max(jump, Math.Abs(a - prev));
                prev = a;
            }
            Ok("smoke reaches ~0 #" + i, lo < 0.01f, "min " + lo);
            Ok("smoke becomes visible #" + i, hi > 0.8f, "max " + hi);
            Ok("smoke never pops (60 fps step) #" + i, jump < 0.05f, "largest step " + jump);
        }
    }

    static void SparksOnEdges()
    {
        Ok("selected has more sparks", RoomFxLogic.SparkCount(true) > RoomFxLogic.SparkCount(false), null);
        Ok("unselected sparks few", RoomFxLogic.SparkCount(false) >= 4 && RoomFxLogic.SparkCount(false) <= 8, RoomFxLogic.SparkCount(false).ToString());
        int seed = RoomFxLogic.Seed("csroom.row.2");
        for (int i = 0; i < RoomFxLogic.SparkCount(true); i++)
            for (float t = 0f; t < 12f; t += 0.07f)
            {
                FxParticle f = RoomFxLogic.Spark(seed, i, t, true);
                Ok("spark alpha in [0,1] #" + i, f.Alpha >= 0f && f.Alpha <= 1f, "alpha " + f.Alpha);
                if (i % 4 == 3) Ok("mote rises from the top edge #" + i, f.V <= 0.1201f && f.V > -0.8f && f.U >= 0.09f && f.U <= 0.91f, "u " + f.U + " v " + f.V);
                else
                {
                    Ok("edge spark on the straight part #" + i, f.U >= 0.08f && f.U <= 0.92f, "u " + f.U);
                    Ok("edge spark on the top or bottom edge #" + i, Math.Abs(f.V - (i % 2 == 0 ? 0.1f : 0.9f)) < 1e-6, "v " + f.V);
                }
            }
    }

    // T-184 review (release v0.2.4): no popping for edge sparks either. While a spark is visible it glides along its
    // edge: between two 60 fps frames it moves at most a few thousandths of the width (0.35 per 2.2-4.4 s life). A
    // spark that wraps from one pointed end to the other while lit jumps ~0.84 of the width in one frame.
    static void SparksGlideWithoutJumps()
    {
        foreach (string id in new string[] { "csroom.row.1", "csroom.row.2", "csroom.row.3", "csroom.bar.0.sel", "myrooms.list.0" })
        {
            int seed = RoomFxLogic.Seed(id);
            for (int i = 0; i < RoomFxLogic.SparkCount(true); i++)
            {
                if (i % 4 == 3) continue;   // motes rise; their U barely moves
                float jump = 0f, at = 0f;
                FxParticle prev = RoomFxLogic.Spark(seed, i, 0f, true);
                for (float t = 1f / 60f; t < 20f; t += 1f / 60f)
                {
                    FxParticle f = RoomFxLogic.Spark(seed, i, t, true);
                    if (prev.Alpha > 0.02f && f.Alpha > 0.02f && Math.Abs(f.U - prev.U) > jump) { jump = Math.Abs(f.U - prev.U); at = t; }
                    prev = f;
                }
                Ok("edge spark glides, no jump while visible " + id + " #" + i, jump < 0.01f, "largest visible step " + jump + " at t=" + at);
            }
        }
    }

    static void SelectedBrighter()
    {
        int seed = 99;
        for (int i = 0; i < RoomFxLogic.SparkCount(false); i++)
            for (float t = 0.3f; t < 8f; t += 0.5f)
            {
                FxParticle on = RoomFxLogic.Spark(seed, i, t, true), off = RoomFxLogic.Spark(seed, i, t, false);
                Ok("selected spark not dimmer #" + i, on.Alpha >= off.Alpha, on.Alpha + " < " + off.Alpha);
                if (on.Alpha > 0.1f) Ok("unselected spark clearly dimmer #" + i, off.Alpha < on.Alpha * 0.7f, off.Alpha + " vs " + on.Alpha);
            }
    }

    static void PulseAndShimmer()
    {
        for (float t = 0f; t < 10f; t += 0.13f)
        {
            float p = RoomFxLogic.Pulse(t);
            Ok("pulse in [0,1]", p >= 0f && p <= 1f, "t " + t + " p " + p);
            Near("pulse period 2.2 s", p, RoomFxLogic.Pulse(t + 2.2f), 1e-3);
        }
        Near("pulse rest value", 0.5, RoomFxLogic.Pulse(0f), 1e-6);
        Near("pulse peak a quarter period in", 1, RoomFxLogic.Pulse(0.55f), 1e-4);
        float s;
        float u = RoomFxLogic.ShimmerU(0f, out s);
        Near("shimmer starts off the left", -0.2, u, 1e-6);
        Near("shimmer starts dark", 0, s, 1e-6);
        u = RoomFxLogic.ShimmerU(RoomFxLogic.ShimmerSweep / 2f, out s);
        Near("shimmer mid sweep at the centre", 0.5, u, 1e-5);
        Near("shimmer brightest mid sweep", 1, s, 1e-5);
        RoomFxLogic.ShimmerU(RoomFxLogic.ShimmerSweep + 0.1f, out s);
        Near("shimmer rests after the sweep", 0, s, 1e-6);
        RoomFxLogic.ShimmerU(RoomFxLogic.ShimmerPeriod - 0.01f, out s);
        Near("shimmer rests until the next period", 0, s, 1e-6);
        u = RoomFxLogic.ShimmerU(RoomFxLogic.ShimmerPeriod * 3f + RoomFxLogic.ShimmerSweep / 2f, out s);
        Near("shimmer repeats", 0.5, u, 1e-3);
        RoomFxLogic.ShimmerU(-0.5f, out s);
        Ok("shimmer negative time ok", s >= 0f && s <= 1f, "s " + s);
        Ok("rest longer than half the period", RoomFxLogic.ShimmerPeriod - RoomFxLogic.ShimmerSweep > RoomFxLogic.ShimmerPeriod / 2f, null);
    }

    static void Deterministic()
    {
        FxParticle a = RoomFxLogic.Smoke(7, 3, 12.345f), b = RoomFxLogic.Smoke(7, 3, 12.345f);
        Ok("smoke deterministic", a.U == b.U && a.V == b.V && a.Size == b.Size && a.Alpha == b.Alpha && a.Angle == b.Angle, null);
        FxParticle c = RoomFxLogic.Smoke(8, 3, 12.345f);
        Ok("smoke differs per seed", a.U != c.U || a.V != c.V, null);
        FxParticle d = RoomFxLogic.Spark(7, 2, 5f, true), e = RoomFxLogic.Spark(7, 2, 5f, true);
        Ok("spark deterministic", d.U == e.U && d.V == e.V && d.Alpha == e.Alpha, null);
    }
}
