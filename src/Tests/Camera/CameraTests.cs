using System;
using System.Collections.Generic;
using OriCoop.Rules;

// T-058 (#33): online influence camera. Expected values come from the owner's rule in the issue and the algorithm in
// docs\multiplayer-model.md §3 (weights, falloff, safe box, dead zone, bounded zoom), not from the code's output.
static class CameraTests
{
    static int s_fail, s_pass;
    static void Check(bool ok, string what)
    {
        if (ok) s_pass++; else { s_fail++; Console.WriteLine("FAIL: " + what); }
    }
    static bool Near(float a, float b, float eps) { return Math.Abs(a - b) <= eps; }
    static CamVec V(float x, float y) { return new CamVec(x, y); }
    static readonly CamVec View = V(11.5f, 6.5f);   // half view at normal zoom (about 23 x 13 units)
    const float Dt = 1f / 60f;

    static InfluenceCameraSettings Def() { return new InfluenceCameraSettings(); }

    static int Main()
    {
        Falloff();
        Weights();
        GoalOffsets();
        SafeBox();
        NoJumps();
        OwnerScenario();
        SeveralTeammates();
        Zoom();
        DeadZone();
        Disabled();
        Console.WriteLine("camera tests: " + s_pass + " passed, " + s_fail + " failed");
        return s_fail == 0 ? 0 : 1;
    }

    static void Falloff()
    {
        Check(InfluenceCamera.Falloff(0f, 0.35f) == 1f, "falloff: full at the player");
        Check(InfluenceCamera.Falloff(0.35f, 0.35f) == 1f, "falloff: full at the inner radius");
        Check(InfluenceCamera.Falloff(1f, 0.35f) == 0f, "falloff: zero exactly at the radius");
        Check(InfluenceCamera.Falloff(1.0001f, 0.35f) == 0f && InfluenceCamera.Falloff(50f, 0.35f) == 0f, "falloff: zero beyond the radius");
        Check(Near(InfluenceCamera.Falloff(0.675f, 0.35f), 0.5f, 1e-4f), "falloff: half way through the band is 0.5");
        // Property: monotone non-increasing and Lipschitz (no step anywhere, in particular not at either edge).
        float prev = 1f, maxStep = 0f; const float h = 0.001f;
        for (float r = 0f; r <= 1.2f; r += h)
        {
            float f = InfluenceCamera.Falloff(r, 0.35f);
            if (f > prev + 1e-6f) { Check(false, "falloff: increases at r=" + r); break; }
            maxStep = Math.Max(maxStep, prev - f); prev = f;
        }
        // smoothstep slope max = 1.5 / (1 - inner) = 2.31 per unit r
        Check(maxStep <= 2.4f * h, "falloff: largest step " + maxStep + " per " + h + " (no jump)");
        // Zero slope at both band edges: tiny change in value right inside the edges.
        Check(1f - InfluenceCamera.Falloff(0.36f, 0.35f) < 0.002f, "falloff: flat entering the band");
        Check(InfluenceCamera.Falloff(0.99f, 0.35f) < 0.002f, "falloff: flat leaving the band");
    }

    static void Weights()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(100f, 50f);
        Check(Near(c.Weight(me, me), 0.8f, 1e-6f), "weight: overlapping teammate has the full teammate weight 0.8");
        Check(c.Weight(me, V(100f + 20f, 50f)) == 0f, "weight: zero at the horizontal radius (20)");
        Check(c.Weight(me, V(100f, 50f + 12f)) == 0f, "weight: zero at the vertical radius (20*0.6 = 12)");
        Check(c.Weight(me, V(100f, 50f + 11f)) > 0f, "weight: still some inside the vertical radius");
        Check(c.Weight(me, V(100f - 25f, 50f)) == 0f, "weight: zero beyond the radius (left side too)");
        Check(Near(c.Weight(me, V(107f, 50f)), 0.8f, 1e-6f), "weight: full inside the inner radius (7 = 0.35*20)");
        var s = Def(); s.TeammateWeight = 3f;
        Check(new InfluenceCamera(s).Weight(me, me) <= 1f, "weight: a teammate never outweighs the local player (clamped to 1)");
        // Symmetry: same weight on both sides.
        Check(c.Weight(me, V(110f, 50f)) == c.Weight(me, V(90f, 50f)), "weight: left/right symmetric");
    }

    static void GoalOffsets()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(10f, 5f);
        Check(c.GoalOffset(me, null, View).Length == 0f, "goal: alone = follow the player exactly");
        Check(c.GoalOffset(me, new CamVec[0], View).Length == 0f, "goal: no teammates = zero offset");
        Check(c.GoalOffset(me, new[] { me }, View).Length == 0f, "goal: overlapping teammate keeps both centred");
        // Teammate 6 to the right (inside the inner radius): weighted mean = 0.8*6 / 1.8 = 2.667
        CamVec g = c.GoalOffset(me, new[] { V(16f, 5f) }, View);
        Check(Near(g.X, 0.8f * 6f / 1.8f, 1e-4f) && Near(g.Y, 0f, 1e-6f), "goal: teammate 6 right -> offset 2.667 right, got " + g);
        Check(c.GoalOffset(me, new[] { V(40f, 5f) }, View).Length == 0f, "goal: far teammate (30 away) has no influence");
        // Mirror symmetry and order independence.
        CamVec a = c.GoalOffset(me, new[] { V(14f, 7f), V(6f, 3f) }, View);
        Check(a.Length < 1e-5f, "goal: mirrored teammates cancel, got " + a);
        CamVec p = c.GoalOffset(me, new[] { V(14f, 7f), V(3f, 9f), V(19f, 2f) }, View);
        CamVec q = c.GoalOffset(me, new[] { V(19f, 2f), V(14f, 7f), V(3f, 9f) }, View);
        Check(Near(p.X, q.X, 1e-5f) && Near(p.Y, q.Y, 1e-5f), "goal: teammate order does not matter");
        // Translation invariance: the same layout elsewhere in the world gives the same offset.
        CamVec t = c.GoalOffset(V(1010f, -995f), new[] { V(1016f, -995f) }, View);
        Check(Near(t.X, g.X, 1e-3f) && Near(t.Y, g.Y, 1e-3f), "goal: same layout elsewhere gives the same offset");
    }

    static void SafeBox()
    {
        var c = new InfluenceCamera(Def());
        CamVec safe = c.SafeHalf(View);
        Check(Near(safe.X, 11.5f * 0.55f, 1e-4f) && Near(safe.Y, 6.5f * 0.55f, 1e-4f), "safe: box = 55% of the half view");
        var rnd = new Random(58);
        float worst = 0f;
        for (int i = 0; i < 2000; i++)
        {
            CamVec me = V((float)rnd.NextDouble() * 400f - 200f, (float)rnd.NextDouble() * 400f - 200f);
            int n = 1 + rnd.Next(3);
            var mates = new CamVec[n];
            for (int k = 0; k < n; k++) mates[k] = me + V((float)rnd.NextDouble() * 50f - 25f, (float)rnd.NextDouble() * 30f - 15f);
            CamVec g = c.GoalOffset(me, mates, View);
            worst = Math.Max(worst, Math.Max(Math.Abs(g.X) / safe.X, Math.Abs(g.Y) / safe.Y));
        }
        Check(worst <= 1.0001f, "safe: goal offset never leaves the safe box (worst " + worst + ")");
        // Strong pull: 3 teammates all at the edge of full influence on one side are clamped.
        var s = Def(); s.TeammateWeight = 1f; s.InnerFraction = 0.9f; s.SafeFraction = 0.3f;
        var strong = new InfluenceCamera(s);
        CamVec sg = strong.GoalOffset(V(0, 0), new[] { V(17f, 0), V(17f, 0), V(17f, 0) }, View);
        Check(Near(sg.X, 11.5f * 0.3f, 1e-4f), "safe: strong pull is clamped to the box edge, got " + sg);
        // The smoothed offset obeys the box too while a teammate runs around.
        strong.Reset();
        float worstRun = 0f;
        for (int f = 0; f < 600; f++)
        {
            float x = 25f * (float)Math.Sin(f * 0.05);
            strong.Step(V(0, 0), new[] { V(x, 3f), V(x, 3f), V(x, 3f) }, View, Dt);
            worstRun = Math.Max(worstRun, Math.Abs(strong.Offset.X) / (11.5f * 0.3f));
        }
        Check(worstRun <= 1.0001f, "safe: smoothed offset stays in the box while teammates run (worst " + worstRun + ")");
    }

    // Teammate walks in from far away to the player and back out at running speed: the camera offset must never jump.
    static void NoJumps()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(0f, 0f);
        float x = 35f, speed = 12f, maxDelta = 0f, maxAccelAtEdge = 0f, maxAccel = 0f, prevZoom = 1f, maxZoomDelta = 0f;
        CamVec prevStep = new CamVec();
        CamVec prev = c.Offset;
        for (int f = 0; f < 60 * 12; f++)
        {
            x += (f < 60 * 6 ? -speed : speed) * Dt;
            c.Step(me, new[] { V(x, 0f) }, View, Dt);
            float d = (c.Offset - prev).Length;
            maxDelta = Math.Max(maxDelta, d);
            CamVec step = c.Offset - prev;
            float acc = (step - prevStep).Length;
            if (f != 60 * 6) maxAccel = Math.Max(maxAccel, acc);   // the walker turns round at f = 360 (its own step)
            if (Math.Abs(Math.Abs(x) - 20f) < 1f) maxAccelAtEdge = Math.Max(maxAccelAtEdge, acc);
            prevStep = step;
            maxZoomDelta = Math.Max(maxZoomDelta, Math.Abs(c.Zoom - prevZoom));
            prev = c.Offset; prevZoom = c.Zoom;
        }
        // Speed cap 30 u/s -> at most 0.5 per frame; a smooth pull at a 12 u/s walk should stay far below that.
        Check(maxDelta <= 30f * Dt + 1e-4f, "no jump: per-frame offset change " + maxDelta + " within the speed cap");
        Check(maxDelta < 0.15f, "no jump: per-frame offset change " + maxDelta + " < 0.15 while a teammate walks through");
        // A jump is a step change in camera velocity; the spring keeps the per-frame velocity change tiny everywhere,
        // in particular where the teammate crosses the influence radius.
        Check(maxAccelAtEdge < 0.005f, "no jump: crossing the radius changes the camera speed by " + maxAccelAtEdge + " per frame");
        Check(maxAccel < 0.01f, "no jump: camera speed changes by at most " + maxAccel + " per frame");
        Check(maxZoomDelta < 0.01f, "no jump: zoom changes at most " + maxZoomDelta + " per frame");
        // Teleport across the radius in one frame (door): still eased, never a step bigger than the cap.
        c.Reset();
        for (int f = 0; f < 120; f++) c.Step(me, new[] { V(50f, 0f) }, View, Dt);
        prev = c.Offset; maxDelta = 0f;
        for (int f = 0; f < 120; f++) { c.Step(me, new[] { V(5f, 0f) }, View, Dt); maxDelta = Math.Max(maxDelta, (c.Offset - prev).Length); prev = c.Offset; }
        Check(maxDelta <= 30f * Dt + 1e-4f, "no jump: teammate appearing next to the player eases in (" + maxDelta + " per frame)");
    }

    // The owner's example in #33: the player stands still at the left of the screen, a teammate walks in from the right,
    // overlaps (both centred), keeps walking left (view extends left though the player did not move), then leaves.
    static void OwnerScenario()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(0f, 0f);
        float x = 30f;
        float atRight10 = float.NaN, atOverlap = float.NaN, atLeft6 = float.NaN;
        for (int f = 0; f < 60 * 8; f++)
        {
            float target = -6f;
            x = Math.Max(target, x - 8f * Dt);
            c.Step(me, new[] { V(x, 0f) }, View, Dt);
            if (float.IsNaN(atRight10) && x <= 10f) atRight10 = c.Offset.X;
            if (float.IsNaN(atOverlap) && x <= 0f) { for (int k = 0; k < 90; k++) c.Step(me, new[] { V(0f, 0f) }, View, Dt); atOverlap = c.Offset.X; }
        }
        atLeft6 = c.Offset.X;
        Check(atRight10 > 0.5f, "owner: teammate approaching from the right pulls the view right (" + atRight10 + ")");
        Check(Math.Abs(atOverlap) < 0.05f, "owner: overlapping -> both centred (offset " + atOverlap + ")");
        Check(Near(atLeft6, -0.8f * 6f / 1.8f, Def().DeadZone + 0.01f), "owner: teammate walks on to the left -> view extends left (" + atLeft6 + ", expected -2.67 within the dead zone)");
        // Teammate leaves far to the left: the pull fades to zero, smoothly.
        float maxStep = 0f; float prev = c.Offset.X;
        for (int f = 0; f < 60 * 8; f++)
        {
            x -= 8f * Dt;
            c.Step(me, new[] { V(x, 0f) }, View, Dt);
            maxStep = Math.Max(maxStep, Math.Abs(c.Offset.X - prev)); prev = c.Offset.X;
        }
        Check(Math.Abs(c.Offset.X) < 0.01f && Math.Abs(c.Zoom - 1f) < 0.01f, "owner: teammate gone (" + x + ") -> camera back on the player (" + c.Offset + ", zoom " + c.Zoom + ")");
        Check(maxStep < 0.1f, "owner: returning is smooth (max step " + maxStep + ")");
    }

    static void SeveralTeammates()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(0f, 0f);
        CamVec one = c.GoalOffset(me, new[] { V(6f, 0f) }, View);
        CamVec two = c.GoalOffset(me, new[] { V(6f, 0f), V(6f, 0f) }, View);
        // 0.8*6/1.8 = 2.667 ; 1.6*6/2.6 = 3.692
        Check(Near(two.X, 1.6f * 6f / 2.6f, 1e-4f) && two.X > one.X, "several: two teammates pull more than one (" + one + " -> " + two + ")");
        CamVec mix = c.GoalOffset(me, new[] { V(6f, 0f), V(0f, 4f), V(-60f, 0f) }, View);
        Check(Near(mix.X, 0.8f * 6f / 2.6f, 1e-4f) && Near(mix.Y, 0.8f * 4f / 2.6f, 1e-4f), "several: each near teammate counts, the far one does not (" + mix + ")");
    }

    static void Zoom()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(0f, 0f);
        Check(c.GoalZoom(me, null, V(0, 0), View) == 1f, "zoom: none when alone");
        Check(c.GoalZoom(me, new[] { V(2f, 0f) }, c.GoalOffset(me, new[] { V(2f, 0f) }, View), View) == 1f, "zoom: none when the teammate is already well in view");
        CamVec[] m = { V(0f, 9f) };
        float z = c.GoalZoom(me, m, c.GoalOffset(me, m, View), View);
        Check(z > 1f && z <= 1.25f, "zoom: teammate above the view opens the view a little (" + z + ")");
        var s = Def(); s.InnerFraction = 0.9f;
        var wide = new InfluenceCamera(s);
        CamVec[] far = { V(0f, 10.5f) };
        Check(wide.GoalZoom(me, far, wide.GoalOffset(me, far, View), View) == 1.25f, "zoom: capped at MaxZoom 1.25");
        var off = Def(); off.MaxZoom = 1f;
        Check(new InfluenceCamera(off).GoalZoom(me, far, V(0, 0), View) == 1f, "zoom: MaxZoom 1 disables zooming");
        Check(c.GoalZoom(me, new[] { V(25f, 0f) }, V(0, 0), View) == 1f, "zoom: no zoom for a teammate outside the radius");
        // Continuity of the goal zoom across the radius (blended by falloff).
        float prev = 1f, maxStep = 0f;
        for (float x = 0f; x < 25f; x += 0.01f)
        {
            CamVec[] mm = { V(x, 5f) };
            float gz = c.GoalZoom(me, mm, c.GoalOffset(me, mm, View), View);
            maxStep = Math.Max(maxStep, Math.Abs(gz - prev)); prev = gz;
        }
        Check(maxStep < 0.01f, "zoom: goal zoom is continuous in teammate position (max step " + maxStep + ")");
    }

    static void DeadZone()
    {
        var c = new InfluenceCamera(Def());
        CamVec me = V(0f, 0f);
        for (int f = 0; f < 120; f++) c.Step(me, new[] { V(6f, 0f) }, View, Dt);
        CamVec goal = c.Goal;
        var rnd = new Random(7);
        bool moved = false;
        for (int f = 0; f < 300; f++)
        {
            c.Step(me, new[] { V(6f + (float)(rnd.NextDouble() * 0.4 - 0.2), (float)(rnd.NextDouble() * 0.4 - 0.2)) }, View, Dt);
            if ((c.Goal - goal).Length > 1e-6f) moved = true;
        }
        Check(!moved, "dead zone: +-0.2 network jitter does not move the goal");
        c.Step(me, new[] { V(10f, 0f) }, View, Dt);
        Check((c.Goal - goal).Length > 0.3f, "dead zone: a real move (4 units) does move the goal");
    }

    static void Disabled()
    {
        var s = Def(); s.Enabled = false;
        var c = new InfluenceCamera(s);
        for (int f = 0; f < 120; f++) c.Step(V(0, 0), new[] { V(5f, 0f) }, View, Dt);
        Check(c.Offset.Length == 0f && c.Zoom == 1f, "disabled: camera follows the player only (" + c.Offset + ", zoom " + c.Zoom + ")");
    }
}
