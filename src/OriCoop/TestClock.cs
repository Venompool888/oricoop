using System.Collections;
using UnityEngine;

namespace OriCoop
{
    // Test windows for things the GAME times (enemy respawn, revive progress, movement, a post sliding in) are measured
    // in game seconds, not real seconds (T-071). Ori caps a frame's game time at Time.maximumDeltaTime (1/60 s, 1/30 s
    // on slow PCs), so with several test games on one PC (about 40 fps each) the game's own clocks run at about 2/3 of
    // real time: E3's 3 s respawn took 4-5+ real seconds and failed its 5 s real-time window. Time.time is the clock the
    // game's timers use (scaled and capped the same way). Every wait also has a real-time cap (4x + 2 s) so a paused or
    // frozen game cannot hang a test.
    public static class TestClock
    {
        public static float Game { get { return Time.time; } }
        public static float Real { get { return Time.realtimeSinceStartup; } }
        public static float RealCap(float gameSeconds) { return gameSeconds * 4f + 2f; }

        // Waits until `seconds` of game time have passed (or the real-time cap).
        public static IEnumerator WaitGame(float seconds)
        {
            float game = Time.time + seconds, real = Time.realtimeSinceStartup + RealCap(seconds);
            while (Time.time < game && Time.realtimeSinceStartup < real) yield return null;
        }

        // A window that started now: open while less than `seconds` of game time (and the real-time cap) have passed.
        public sealed class Window
        {
            readonly float m_game, m_real, m_seconds;
            public Window(float seconds) { m_game = Time.time; m_real = Time.realtimeSinceStartup; m_seconds = seconds; }
            public bool Open { get { return Time.time - m_game < m_seconds && Time.realtimeSinceStartup - m_real < RealCap(m_seconds); } }
            public float GameElapsed { get { return Time.time - m_game; } }
            public float RealElapsed { get { return Time.realtimeSinceStartup - m_real; } }
            // Game time per real second so far; logged with failures so a slow PC is visible in the result.
            public float Rate { get { float r = RealElapsed; return r > 0f ? GameElapsed / r : 1f; } }
        }
    }
}
