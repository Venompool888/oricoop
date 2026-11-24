using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Rules
{
    // T-169 (#249, T-041 rules 6 and 7): escape sequences. Unity-free so the unit tests can pin it.
    // Which scenes form each escape and which trigger is its end come from the in-game probe (EscapeProbe.cs,
    // docs\tickets\T-169.md "游戏里逃亡是怎么搭的"): the game wires escapes in scene assets, not in C#.
    public sealed class EscapeDef
    {
        public int Id;
        public string Name;
        public string[] Scenes;           // the escape's own scenes: a position inside one of them is "in this escape"
        public string[] FinishTriggers;   // transform paths (scene root first) of the trigger(s) that end it
    }

    public struct EscapeDone
    {
        public byte Escape;   // EscapeDef.Id
        public byte Seat;     // the player who reached the end
        public float X, Y;    // the end trigger's centre: teammates are put there so the game's own end runs for them
        public float FX, FY;  // where the finisher stands once the game's end has placed it: anyone not there by then goes there
    }

    public sealed class MashState
    {
        public float Progress;   // the ring, 0..1
        public float Pending;    // presses not yet shown (the ring never fills faster than MashMinSeconds)
    }

    // The "bring the team" work a PC still owes after a finish: its own Ori goes beside a remote finisher if the game's
    // end did not take it there (fallback), and its extra local Oris join the primary one. T-215 #3: all of it is
    // dropped when the session ends, or a player who stays in its own game would still be pulled to the finisher.
    public sealed class PendingFinish
    {
        public int Escape, Finisher;
        float m_fallbackAt, m_extrasAt;   // real time; 0 = not owed

        public bool Active { get { return m_fallbackAt > 0f || m_extrasAt > 0f; } }

        public void Begin(int escape, int finisher, float fallbackAt, float extrasAt)
        {
            Escape = escape; Finisher = finisher; m_fallbackAt = fallbackAt; m_extrasAt = extrasAt;
        }

        // true once, when the fallback placement is due.
        public bool TakeFallback(float now)
        {
            if (m_fallbackAt <= 0f || now < m_fallbackAt) return false;
            m_fallbackAt = 0f;
            return true;
        }

        // true once, when the extra local Oris should join the primary one.
        public bool TakeExtras(float now)
        {
            if (m_extrasAt <= 0f || now < m_extrasAt) return false;
            m_extrasAt = 0f;
            return true;
        }

        public void Clear() { Escape = 0; Finisher = 0; m_fallbackAt = 0f; m_extrasAt = 0f; }
    }

    public static class EscapeRules
    {
        public const byte MsgEscapeDone = 92;   // docs/net-msg-ids.md
        public const int Ginso = 1, Horu = 2;

        public static readonly EscapeDef[] All =
        {
            new EscapeDef { Id = Ginso, Name = "Ginso Tree",
                Scenes = new[] { "ginsoTreeWaterRisingBtm", "ginsoTreeWaterRisingMid", "ginsoTreeWaterRisingEnd" },
                FinishTriggers = new[] { "ginsoTreeWaterRisingEnd/*exit/trigger" } },
            // The game calls the Mount Horu escape "catAndMouse": the heart room sets WarmthReturned, then Kuro chases
            // through Left, Mid and Right; the last Kuro room's exit (bottom or top route) ends it.
            new EscapeDef { Id = Horu, Name = "Mount Horu",
                Scenes = new[] { "catAndMouseResurrectionRoom", "catAndMouseLeft", "catAndMouseMid", "catAndMouseRight" },
                FinishTriggers = new[] { "catAndMouseRight/*kuroSetup/bottom/exit/collider", "catAndMouseRight/*kuroSetup/top/exit/collider" } },
        };

        public static EscapeDef ById(int id)
        {
            foreach (EscapeDef e in All) if (e.Id == id) return e;
            return null;
        }

        public static EscapeDef ByScene(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return null;
            foreach (EscapeDef e in All) if (Array.IndexOf(e.Scenes, scene) >= 0) return e;
            return null;
        }

        public static EscapeDef ByFinishTrigger(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (EscapeDef e in All) if (Array.IndexOf(e.FinishTriggers, path) >= 0) return e;
            return null;
        }

        // ---- rule 7: mash Y to fill the ring (owner, #15 2026-11-18) -------------------------------------------------
        // Every press adds MashStep; without presses the ring drains at MashDecay per second (a tug of war); however fast
        // the player mashes, the ring gains at most 1 / MashMinSeconds per second, so it takes at least 0.5 s.
        public const float MashStep = 0.1f, MashDecay = 0.5f, MashMinSeconds = 0.5f, MashMaxPending = 0.3f;

        // true once the ring is full.
        public static bool Mash(MashState m, int presses, float dt)
        {
            if (m == null || dt <= 0f) return m != null && m.Progress >= 1f;
            if (presses > 0) m.Pending = Math.Min(MashMaxPending, m.Pending + presses * MashStep);
            float gain = Math.Min(m.Pending, dt / MashMinSeconds);
            m.Pending -= gain;
            if (gain > 0f) m.Progress += gain;
            else m.Progress -= MashDecay * dt;
            if (m.Progress < 0f) m.Progress = 0f;
            if (m.Progress > 1f) m.Progress = 1f;
            return m.Progress >= 1f;
        }

        // A random teammate from the eligible ones (alive, not downed, in the same escape). roll: any int (System.Random).
        // 0 = nobody eligible.
        public static int PickTeammate(IList<int> eligible, int roll)
        {
            if (eligible == null || eligible.Count == 0) return 0;
            int n = eligible.Count;
            return eligible[((roll % n) + n) % n];
        }

        // ---- rule 6: one finisher = the team is through -----------------------------------------------------------------
        public const float FinishWindow = 30f;   // a second end of the same escape within this many seconds is the same one

        // true = a new finish of this escape (and it is recorded); false = a repeat (another PC's message, or this PC's
        // own teammates walking into the end trigger after being brought there).
        public static bool NoteFinish(float[] lastDone, int escape, float now)
        {
            if (lastDone == null || escape <= 0 || escape >= lastDone.Length) return false;
            if (lastDone[escape] > 0f && now - lastDone[escape] < FinishWindow && now >= lastDone[escape]) return false;
            lastDone[escape] = now <= 0f ? 0.0001f : now;
            return true;
        }

        public static bool RecentlyFinished(float[] lastDone, int escape, float now)
        {
            return lastDone != null && escape > 0 && escape < lastDone.Length && lastDone[escape] > 0f
                && now >= lastDone[escape] && now - lastDone[escape] < FinishWindow;
        }

        // ---- wire: msg 92 EscapeDone (finisher's PC -> all, forwarded by the P2P host) ----------------------------------
        public static void Write(BinaryWriter w, EscapeDone d)
        {
            w.Write(d.Escape); w.Write(d.Seat); w.Write(d.X); w.Write(d.Y); w.Write(d.FX); w.Write(d.FY);
        }

        public const int WireSize = 18;

        // false = malformed or not ours (unknown escape, seat outside 1..4, not finite): dropped.
        public static bool TryRead(BinaryReader r, out EscapeDone d)
        {
            d = new EscapeDone();
            try
            {
                d.Escape = r.ReadByte(); d.Seat = r.ReadByte(); d.X = r.ReadSingle(); d.Y = r.ReadSingle(); d.FX = r.ReadSingle(); d.FY = r.ReadSingle();
            }
            catch (EndOfStreamException) { return false; }
            return ById(d.Escape) != null && d.Seat >= 1 && d.Seat <= ReviveLogic.MaxSeats
                && Finite(d.X) && Finite(d.Y) && Finite(d.FX) && Finite(d.FY);
        }

        static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f);
        }
    }
}
