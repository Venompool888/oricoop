using System;
using System.Collections.Generic;
using OriCoop.World;

// T-250 probe (trace only): which shooter starts get a trace line, and the text of its fields. Expected values are
// worked out from the probe's rules (first 4 starts per shooter per scene visit, 300 per process; the game's own
// frames only), not copied from the code's output.
static class ShooterProbeTests
{
    static int cases, failures;
    static void Run(string name, Action body)
    {
        cases++;
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void Eq(object expected, object actual, string what)
    { if (!Equals(expected, actual)) throw new Exception(what + ": expected <" + expected + "> got <" + actual + ">"); }

    static int Main()
    {
        Run("a shooter firing all the time logs only its first starts in one visit", delegate {
            ShooterProbeGate g = new ShooterProbeGate(4, 300);
            List<int> got = new List<int>();
            for (int i = 0; i < 10; i++) got.Add(g.Next("ginsoTreePuzzles", "17"));
            Eq("1,2,3,4,0,0,0,0,0,0", string.Join(",", got.ConvertAll(n => n.ToString()).ToArray()), "ordinals");
            Eq(6, g.Suppressed, "suppressed"); Eq(4, g.Logged, "logged");
        });
        Run("each shooter and each scene has its own count", delegate {
            ShooterProbeGate g = new ShooterProbeGate(2, 300);
            Eq(1, g.Next("ginsoTreePuzzles", "1"), "first shooter");
            Eq(1, g.Next("ginsoTreePuzzles", "2"), "second shooter");
            Eq(1, g.Next("spiritTreeRefined", "1"), "same id, other scene");
            Eq(2, g.Next("ginsoTreePuzzles", "1"), "first shooter again");
            Eq(0, g.Next("ginsoTreePuzzles", "1"), "first shooter past the limit");
        });
        Run("loading the scene again starts its shooters' count again, other scenes keep theirs", delegate {
            ShooterProbeGate g = new ShooterProbeGate(1, 300);
            g.Next("ginsoTreePuzzles", "1"); g.Next("ginsoTreePuzzlesB", "1"); g.Next("moonGrotto", "1");
            Eq(0, g.Next("ginsoTreePuzzles", "1"), "before the reload");
            g.SceneLoaded("ginsoTreePuzzles");
            Eq(1, g.Next("ginsoTreePuzzles", "1"), "after the reload");
            Eq(0, g.Next("ginsoTreePuzzlesB", "1"), "a scene whose name only starts the same is not reset");
            Eq(0, g.Next("moonGrotto", "1"), "another scene is not reset");
        });
        Run("the cap per process holds across scene reloads", delegate {
            ShooterProbeGate g = new ShooterProbeGate(4, 5);
            int logged = 0;
            for (int visit = 0; visit < 3; visit++)
            {
                g.SceneLoaded("ginsoTreePuzzles");
                for (int i = 0; i < 4; i++) if (g.Next("ginsoTreePuzzles", "9") > 0) logged++;
            }
            Eq(5, logged, "logged"); Eq(7, g.Suppressed, "suppressed");
            g.Clear(); Eq(1, g.Next("ginsoTreePuzzles", "9"), "a cleared gate logs again");
        });
        Run("caller frames keep the game's own frames, innermost first", delegate {
            List<string> frames = new List<string> {
                "OriCoop.ShooterProbe.SequencePerformed", ".DMD<ActionSequence::Perform>", "ActionSequence.Perform",
                "HarmonyLib.Something.Call", "OnTimerPerformAction.FixedUpdate", "OnTimerPerformAction.FixedUpdate",
                "System.Reflection.MonoMethod.Invoke", "UnityEngine.Events.InvokableCall.Invoke", "MonoMod.Utils.X.Y" };
            Eq("ActionSequence.Perform<OnTimerPerformAction.FixedUpdate<UnityEngine.Events.InvokableCall.Invoke",
                ShooterProbeText.Frames(frames, 8), "frames");
            Eq("ActionSequence.Perform<+2", ShooterProbeText.Frames(frames, 1), "frames past the limit are counted");
            Eq("-", ShooterProbeText.Frames(new List<string> { "OriCoop.A.B", "" }, 8), "no game frame");
        });
        Run("action types: a run of one type is written once with its count", delegate {
            Eq("WaitAction,SpawnProjectileAction*2,PlaySoundAction",
                ShooterProbeText.Types(new List<string> { "WaitAction", "SpawnProjectileAction", "SpawnProjectileAction", "PlaySoundAction" }, 280), "types");
            Eq("-", ShooterProbeText.Types(new List<string>(), 280), "empty");
            Eq("WaitAct...", ShooterProbeText.Types(new List<string> { "WaitAction", "PlaySoundAction" }, 10), "clipped");
        });
        Run("fields: clipped values, safe names, and a count of what was left out", delegate {
            List<object> kv = new List<object>();
            List<KeyValuePair<string, string>> fields = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>("Speed", "12.5"),
                new KeyValuePair<string, string>("Target Point", new string('x', 100)),
                new KeyValuePair<string, string>("Prefab", "orb#1:GameObject"),
                new KeyValuePair<string, string>("m_time", "0.3") };
            ShooterProbeText.AddFields(kv, "f_", fields, 3);
            Eq(8, kv.Count, "pairs");
            Eq("f_Speed", kv[0], "name 0"); Eq("12.5", kv[1], "value 0");
            Eq("f_Target_Point", kv[2], "a space in a name is replaced");
            Eq(ShooterProbeText.MaxValue, ((string)kv[3]).Length, "a long value is clipped");
            Check(((string)kv[3]).EndsWith("..."), "clipped value ends with ...");
            Eq("f_more", kv[6], "left out"); Eq(1, kv[7], "left out count");
        });
        Run("aim angle: 0 at the player, 180 away from it, -1 without a direction", delegate {
            Check(Math.Abs(ShooterProbeText.AngleDeg(5, 0, 3, 0)) < 0.01f, "straight at");
            Check(Math.Abs(ShooterProbeText.AngleDeg(-5, 0, 3, 0) - 180f) < 0.01f, "straight away");
            Check(Math.Abs(ShooterProbeText.AngleDeg(0, 2, 7, 0) - 90f) < 0.01f, "across");
            Eq(-1f, ShooterProbeText.AngleDeg(0, 0, 3, 0), "no velocity");
            Eq(-1f, ShooterProbeText.AngleDeg(1, 0, 0, 0), "player on the projectile");
        });
        Console.WriteLine("ShooterProbe: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
