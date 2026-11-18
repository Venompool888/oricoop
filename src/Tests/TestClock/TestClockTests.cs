using System;
using System.Collections;
using OriCoop;
using UnityEngine;

static class TestClockTests
{
    static int cases, failed;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Run(string name, Action test)
    {
        cases++; Time.time = 100; Time.realtimeSinceStartup = 200;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static int Main()
    {
        Run("E3 five game second window survives five real seconds at half rate", delegate {
            TestClock.Window w = new TestClock.Window(5);
            Time.time += 2.5f; Time.realtimeSinceStartup += 5;
            Check(w.Open, "window closed before the three game second enemy respawn could run");
            Time.time += 2.5f; Time.realtimeSinceStartup += 5;
            Check(!w.Open, "window did not finish at five game seconds");
        });
        Run("R2 hold supplies all game seconds before releasing input", delegate {
            IEnumerator wait = TestClock.WaitGame(1.2f);
            Check(wait.MoveNext(), "wait must begin");
            Time.time += 0.6f; Time.realtimeSinceStartup += 1.2f;
            Check(wait.MoveNext(), "real-time deadline released Spirit Flame before enough game time elapsed");
            Time.time += 0.61f; Time.realtimeSinceStartup += 1.22f;
            Check(!wait.MoveNext(), "wait must finish once the game has advanced");
        });
        Run("frozen game cannot hang either clock window or coroutine", delegate {
            TestClock.Window w = new TestClock.Window(1);
            IEnumerator wait = TestClock.WaitGame(1); Check(wait.MoveNext(), "wait began");
            Time.realtimeSinceStartup += TestClock.RealCap(1) - 0.01f;
            Check(w.Open && wait.MoveNext(), "real cap must not expire early");
            Time.realtimeSinceStartup += 0.02f;
            Check(!w.Open && !wait.MoveNext(), "paused game exceeded its hard real-time cap");
        });
        Run("diagnostics report actual game rate rather than assuming real time", delegate {
            TestClock.Window w = new TestClock.Window(5);
            Time.time += 1; Time.realtimeSinceStartup += 2;
            Check(w.GameElapsed == 1 && w.RealElapsed == 2 && w.Rate == 0.5f, "clock diagnostics");
        });
        Console.WriteLine("TESTCLOCK " + (cases - failed) + "/" + cases + (failed == 0 ? " PASS" : " FAIL"));
        return failed == 0 ? 0 : 1;
    }
}
