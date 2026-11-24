using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Rules;

// T-169 escape rules (docs\tickets\T-169.md). Expected values come from the ticket / the owner's #15 wording:
// one finisher = the team is through; mash Y to fill a ring that drains when not mashed and takes at least 0.5 s;
// revive next to a random teammate who is alive, not downed and in the same escape.
static class EscapeTests
{
    static int cases, failed;
    static void Run(string name, Action body)
    {
        cases++;
        try { body(); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }
    static void Near(float expected, float actual, string what)
    {
        if (Math.Abs(expected - actual) > 1e-4f) throw new Exception(what + ": expected " + expected + ", got " + actual);
    }

    // Mash at a steady rate (presses per second) for `seconds` at 60 fps; returns the time the ring filled (or -1).
    static float MashFor(MashState m, float pressesPerSecond, float seconds)
    {
        const float dt = 1f / 60f;
        float owed = 0f;
        for (int frame = 1; frame * dt <= seconds + 1e-6f; frame++)
        {
            owed += pressesPerSecond * dt;
            int presses = (int)owed; owed -= presses;
            if (EscapeRules.Mash(m, presses, dt)) return frame * dt;
        }
        return -1f;
    }

    static int Main()
    {
        // ---- whitelist ----------------------------------------------------------------------------------------------
        Run("whitelist: each Ginso escape scene maps to Ginso", delegate {
            foreach (string s in new[] { "ginsoTreeWaterRisingBtm", "ginsoTreeWaterRisingMid", "ginsoTreeWaterRisingEnd" })
                Check(EscapeRules.ByScene(s) != null && EscapeRules.ByScene(s).Id == EscapeRules.Ginso, s);
        });
        Run("whitelist: Mount Horu escape is the catAndMouse rooms", delegate {
            foreach (string s in new[] { "catAndMouseResurrectionRoom", "catAndMouseLeft", "catAndMouseMid", "catAndMouseRight" })
                Check(EscapeRules.ByScene(s) != null && EscapeRules.ByScene(s).Id == EscapeRules.Horu, s);
        });
        Run("whitelist: neighbours and junk are not escapes", delegate {
            // the heart room before the Ginso escape, the background scene, the tree's other rooms, Forlorn, case, empty, null
            foreach (string s in new[] { "ginsoTreeResurrection", "ginsoTreeWaterRisingBackground", "ginsoTreeSaveRoom", "catAndMouseBackground",
                "forlornRuinsResurrection", "GINSOTREEWATERRISINGMID", "", null })
                Check(EscapeRules.ByScene(s) == null, "'" + s + "' counted as an escape");
        });
        Run("whitelist: the end triggers found by the probe, exact path only", delegate {
            Check(EscapeRules.ByFinishTrigger("ginsoTreeWaterRisingEnd/*exit/trigger").Id == EscapeRules.Ginso, "Ginso end");
            Check(EscapeRules.ByFinishTrigger("catAndMouseRight/*kuroSetup/bottom/exit/collider").Id == EscapeRules.Horu, "Horu bottom end");
            Check(EscapeRules.ByFinishTrigger("catAndMouseRight/*kuroSetup/top/exit/collider").Id == EscapeRules.Horu, "Horu top end");
            // the Kuro rooms' own exits inside the escape and the preload trigger are not the end
            Check(EscapeRules.ByFinishTrigger("catAndMouseLeft/*kuroA/exit/collider") == null, "a middle Kuro room");
            Check(EscapeRules.ByFinishTrigger("ginsoTreeWaterRisingEnd/preload") == null, "Ginso preload");
            Check(EscapeRules.ByFinishTrigger("ginsoTreeWaterRisingEnd/*exit/action") == null, "the action, not the trigger");
            Check(EscapeRules.ByFinishTrigger(null) == null && EscapeRules.ByFinishTrigger("") == null, "empty");
        });
        Run("whitelist: ids are unique and every escape has scenes and an end", delegate {
            HashSet<int> ids = new HashSet<int>();
            foreach (EscapeDef e in EscapeRules.All)
            {
                Check(e.Id > 0 && ids.Add(e.Id), "id " + e.Id);
                Check(e.Scenes.Length > 0 && e.FinishTriggers.Length > 0, e.Name);
                foreach (string t in e.FinishTriggers) Check(Array.IndexOf(e.Scenes, t.Split('/')[0]) >= 0, e.Name + " end outside its scenes: " + t);
                Check(EscapeRules.ById(e.Id) == e, "ById " + e.Id);
            }
            Check(EscapeRules.ById(0) == null && EscapeRules.ById(99) == null, "unknown id");
        });

        // ---- mash ring (rule 7) ---------------------------------------------------------------------------------------
        Run("mash: very fast mashing still takes at least 0.5 s", delegate {
            MashState m = new MashState();
            float t = MashFor(m, 60f, 3f);
            Check(t >= EscapeRules.MashMinSeconds - 1e-4f, "filled after " + t + " s");
            Check(t > 0f && t <= 0.55f, "very fast mashing should fill at about 0.5 s, took " + t);
        });
        Run("mash: one huge burst of presses in one frame cannot fill it", delegate {
            MashState m = new MashState();
            Check(!EscapeRules.Mash(m, 100, 1f / 60f), "filled in one frame");
            Check(m.Progress <= 1f / 60f / EscapeRules.MashMinSeconds + 1e-4f, "gained " + m.Progress);
        });
        Run("mash: steady mashing (10 per second) fills within 1.5 s", delegate {
            MashState m = new MashState();
            float t = MashFor(m, 10f, 5f);
            Check(t > 0.9f && t < 1.5f, "took " + t);
        });
        Run("mash: too slow (3 per second) never fills: the ring drains between presses", delegate {
            MashState m = new MashState();
            Check(MashFor(m, 3f, 20f) < 0f, "filled at 3 presses/s");
            Check(m.Progress < 0.5f, "progress " + m.Progress);
        });
        Run("mash: stopping drains it back to empty (tug of war)", delegate {
            MashState m = new MashState();
            MashFor(m, 10f, 0.6f);
            float before = m.Progress;
            Check(before > 0.3f, "built up only " + before);
            MashFor(m, 0f, 0.5f);
            Check(m.Progress < before - 0.15f, "did not drain: " + before + " -> " + m.Progress);
            MashFor(m, 0f, 5f);
            Near(0f, m.Progress, "empty after a long pause");
        });
        Run("mash: boundaries: never below 0 or above 1, zero dt changes nothing, null is safe", delegate {
            MashState m = new MashState();
            EscapeRules.Mash(m, 0, 1f); Near(0f, m.Progress, "below 0");
            MashFor(m, 60f, 3f); Near(1f, m.Progress, "above 1");
            MashState z = new MashState { Progress = 0.4f };
            EscapeRules.Mash(z, 5, 0f); Near(0.4f, z.Progress, "dt 0");
            EscapeRules.Mash(z, 0, -1f); Near(0.4f, z.Progress, "negative dt");
            Check(!EscapeRules.Mash(null, 3, 0.1f), "null");
        });

        // ---- random teammate ------------------------------------------------------------------------------------------
        Run("teammate: nobody eligible = 0", delegate {
            Check(EscapeRules.PickTeammate(new List<int>(), 5) == 0 && EscapeRules.PickTeammate(null, 5) == 0, "empty");
        });
        Run("teammate: only eligible seats are picked, any roll (negative too)", delegate {
            int[] eligible = { 2, 4 };
            for (int roll = -20; roll <= 20; roll++)
            {
                int p = EscapeRules.PickTeammate(eligible, roll);
                Check(p == 2 || p == 4, "roll " + roll + " -> " + p);
            }
            Check(EscapeRules.PickTeammate(eligible, int.MinValue) != 0 && EscapeRules.PickTeammate(eligible, int.MaxValue) != 0, "extreme rolls");
        });
        Run("teammate: every eligible seat can be picked, evenly (symmetric over P1-P4)", delegate {
            foreach (int[] eligible in new[] { new[] { 1, 2, 3 }, new[] { 2, 3, 4 }, new[] { 1, 3, 4 }, new[] { 1, 2, 4 }, new[] { 3 } })
            {
                Dictionary<int, int> count = new Dictionary<int, int>();
                Random random = new Random(169);
                for (int i = 0; i < 3000; i++) { int p = EscapeRules.PickTeammate(eligible, random.Next()); count[p] = count.ContainsKey(p) ? count[p] + 1 : 1; }
                Check(count.Count == eligible.Length, "picked " + count.Count + " of " + eligible.Length);
                foreach (int seat in eligible) Check(count[seat] > 3000 / eligible.Length * 0.85f, "P" + seat + " picked only " + count[seat]);
            }
        });

        // ---- finish de-duplication ------------------------------------------------------------------------------------
        Run("finish: first end counts, repeats within 30 s do not, later ones do", delegate {
            float[] done = new float[8];
            Check(EscapeRules.NoteFinish(done, EscapeRules.Ginso, 100f), "first");
            Check(!EscapeRules.NoteFinish(done, EscapeRules.Ginso, 100f), "same instant");
            Check(!EscapeRules.NoteFinish(done, EscapeRules.Ginso, 129.9f), "29.9 s later");
            Check(EscapeRules.RecentlyFinished(done, EscapeRules.Ginso, 129.9f), "still recent");
            Check(!EscapeRules.RecentlyFinished(done, EscapeRules.Ginso, 130.1f), "no longer recent");
            Check(EscapeRules.NoteFinish(done, EscapeRules.Ginso, 130.1f), "30.1 s later (a new run after a wipe)");
            Check(EscapeRules.NoteFinish(done, EscapeRules.Horu, 130.1f), "another escape is independent");
        });
        Run("finish: time 0 and bad ids", delegate {
            float[] done = new float[8];
            Check(EscapeRules.NoteFinish(done, EscapeRules.Ginso, 0f), "at t=0");
            Check(!EscapeRules.NoteFinish(done, EscapeRules.Ginso, 1f), "repeat after t=0 start");
            Check(!EscapeRules.NoteFinish(done, 0, 5f) && !EscapeRules.NoteFinish(done, 8, 5f) && !EscapeRules.NoteFinish(null, 1, 5f), "bad id");
        });
        Run("finish: a clock that went back (new session) does not block a finish", delegate {
            float[] done = new float[8];
            EscapeRules.NoteFinish(done, EscapeRules.Ginso, 500f);
            Check(EscapeRules.NoteFinish(done, EscapeRules.Ginso, 10f), "clock reset");
        });

        // ---- wire ------------------------------------------------------------------------------------------------------
        Run("wire: EscapeDone round trip, pinned size", delegate {
            MemoryStream s = new MemoryStream();
            EscapeRules.Write(new BinaryWriter(s), new EscapeDone { Escape = EscapeRules.Ginso, Seat = 3, X = 527.6f, Y = 954.25f, FX = 527.4f, FY = 988.6f });
            Check(s.Length == EscapeRules.WireSize && EscapeRules.WireSize == 18, "size " + s.Length);
            s.Position = 0; EscapeDone d;
            Check(EscapeRules.TryRead(new BinaryReader(s), out d), "read");
            Check(d.Escape == EscapeRules.Ginso && d.Seat == 3, "ids");
            Near(527.6f, d.X, "x"); Near(954.25f, d.Y, "y"); Near(527.4f, d.FX, "finisher x"); Near(988.6f, d.FY, "finisher y");
        });
        Run("wire: malformed or foreign EscapeDone is dropped", delegate {
            foreach (EscapeDone bad in new[] {
                new EscapeDone { Escape = 0, Seat = 1 }, new EscapeDone { Escape = 9, Seat = 1 },
                new EscapeDone { Escape = 1, Seat = 0 }, new EscapeDone { Escape = 1, Seat = 5 },
                new EscapeDone { Escape = 1, Seat = 2, X = float.NaN }, new EscapeDone { Escape = 1, Seat = 2, Y = float.PositiveInfinity }, new EscapeDone { Escape = 1, Seat = 2, FX = float.NaN }, new EscapeDone { Escape = 1, Seat = 2, FY = float.NegativeInfinity } })
            {
                MemoryStream s = new MemoryStream(); EscapeRules.Write(new BinaryWriter(s), bad); s.Position = 0; EscapeDone d;
                Check(!EscapeRules.TryRead(new BinaryReader(s), out d), "accepted escape " + bad.Escape + " seat " + bad.Seat + " x " + bad.X + " y " + bad.Y);
            }
            EscapeDone t;
            Check(!EscapeRules.TryRead(new BinaryReader(new MemoryStream(new byte[] { 1, 2, 0, 0, 0, 0, 0, 0, 0, 0 })), out t), "truncated");
        });

        // ---- T-215 #3: the "bring the team" work owed after a finish ---------------------------------------------------
        Run("T-215 #3 pending finish: the fallback and the extras each come due once, at their time", delegate {
            PendingFinish p = new PendingFinish();
            Check(!p.Active, "active before any finish");
            p.Begin(EscapeRules.Ginso, 2, 103f, 102f);
            Check(p.Active && p.Escape == EscapeRules.Ginso && p.Finisher == 2, "not active after a finish");
            Check(!p.TakeExtras(101.9f) && !p.TakeFallback(102.9f), "due early");
            Check(p.TakeExtras(102f) && !p.TakeExtras(102.1f), "extras: not due at their time, or due twice");
            Check(p.Active, "settled while the fallback is still owed");
            Check(p.TakeFallback(103f) && !p.TakeFallback(104f), "fallback: not due at its time, or due twice");
            Check(!p.Active && p.Escape == EscapeRules.Ginso, "still active after both, or the escape id was lost");
        });
        Run("T-215 #3 pending finish: nothing owed when the times are 0", delegate {
            PendingFinish p = new PendingFinish();
            p.Begin(EscapeRules.Horu, 1, 0f, 101.2f);   // the finisher's own PC: no fallback
            Check(!p.TakeFallback(1000f), "a fallback that was never owed");
            Check(p.TakeExtras(101.2f) && !p.Active, "extras");
        });
        Run("T-215 #3 pending finish: the session's end drops what is still owed", delegate {
            PendingFinish p = new PendingFinish();
            p.Begin(EscapeRules.Ginso, 2, 103f, 102f);
            p.Clear();   // the player ended the session and stays in its own game
            Check(!p.Active, "still active after the session ended");
            Check(!p.TakeFallback(103f) && !p.TakeFallback(1000f), "pulled to the finisher after the session ended");
            Check(!p.TakeExtras(102f) && !p.TakeExtras(1000f), "extras moved after the session ended");
        });

        Console.WriteLine("ESCAPE " + (cases - failed) + "/" + cases + (failed == 0 ? " PASS" : " FAIL"));
        return failed == 0 ? 0 : 1;
    }
}
