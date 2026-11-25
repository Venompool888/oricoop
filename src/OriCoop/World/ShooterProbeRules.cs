using System;
using System.Collections.Generic;
using System.Text;

namespace OriCoop.World
{
    // T-250 (#338) probe, trace only: which starts of a projectile-shooter sequence get a trace line, and how the
    // facts are written. Unity-free (unit tests: src\Tests\ShooterProbe). The game side is ShooterProbe.cs.
    //
    // A shooter fires every few seconds for as long as its scene is loaded, so only the first starts of each shooter
    // in each visit of its scene are logged (a new load of the scene starts the count again), with a cap per process.
    public sealed class ShooterProbeGate
    {
        readonly int m_perVisit, m_total;
        readonly Dictionary<string, int> m_counts = new Dictionary<string, int>();
        int m_logged;
        public int Suppressed;
        public int Logged { get { return m_logged; } }

        public ShooterProbeGate(int perVisit, int total) { m_perVisit = perVisit; m_total = total; }

        static string Key(string scene, string id) { return (scene ?? "") + "|" + (id ?? ""); }

        // The ordinal (1, 2, ...) of this start within the scene visit when it is logged, 0 when it is not.
        public int Next(string scene, string id)
        {
            string key = Key(scene, id);
            int n; m_counts.TryGetValue(key, out n); n++; m_counts[key] = n;
            if (n > m_perVisit || m_logged >= m_total) { Suppressed++; return 0; }
            m_logged++;
            return n;
        }

        // The scene was loaded again: its shooters are counted from the start (the cap per process still holds).
        public void SceneLoaded(string scene)
        {
            string prefix = (scene ?? "") + "|";
            List<string> drop = new List<string>();
            foreach (string key in m_counts.Keys) if (key.StartsWith(prefix, StringComparison.Ordinal)) drop.Add(key);
            foreach (string key in drop) m_counts.Remove(key);
        }

        public void Clear() { m_counts.Clear(); m_logged = 0; Suppressed = 0; }
    }

    public static class ShooterProbeText
    {
        public const int MaxValue = 60;

        public static string Clip(string s, int max)
        {
            if (s == null) return "null";
            if (max < 4 || s.Length <= max) return s;
            return s.Substring(0, max - 3) + "...";
        }

        // Frames of the mod, Harmony / MonoMod (patch wrappers) and the runtime say nothing about what started the
        // sequence; the game's own frames (and UnityEngine's, e.g. an animation event) do.
        public static bool GameFrame(string frame)
        {
            if (string.IsNullOrEmpty(frame) || frame.StartsWith(".", StringComparison.Ordinal)) return false;
            string[] skip = { "OriCoop.", "HarmonyLib.", "MonoMod.", "System.", "Mono." };
            foreach (string s in skip) if (frame.StartsWith(s, StringComparison.Ordinal)) return false;
            return true;
        }

        // "Type.Method" frames, innermost first -> "A.Perform<B.FixedUpdate<..." (the game frames only, repeats once).
        public static string Frames(IList<string> frames, int max)
        {
            if (frames == null) return "-";
            StringBuilder sb = new StringBuilder(); string last = null; int kept = 0, rest = 0;
            foreach (string f in frames)
            {
                if (!GameFrame(f) || f == last) continue;
                last = f;
                if (kept >= max) { rest++; continue; }
                if (kept > 0) sb.Append('<');
                sb.Append(f); kept++;
            }
            if (rest > 0) sb.Append("<+" + rest);
            return kept == 0 ? "-" : sb.ToString();
        }

        // Action type names in order, a run of the same type once with its count: "WaitAction,SpawnProjectileAction*2".
        public static string Types(IList<string> names, int maxLength)
        {
            if (names == null || names.Count == 0) return "-";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count; )
            {
                int j = i + 1;
                while (j < names.Count && names[j] == names[i]) j++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(names[i] ?? "null");
                if (j - i > 1) sb.Append('*').Append(j - i);
                i = j;
            }
            return Clip(sb.ToString(), maxLength);
        }

        // Appends prefix+name / value pairs (each value clipped) to a trace field list; past maxFields one
        // prefix+"more" field gives the count of the fields left out.
        public static void AddFields(List<object> kv, string prefix, IList<KeyValuePair<string, string>> fields, int maxFields)
        {
            if (kv == null || fields == null) return;
            int n = 0;
            foreach (KeyValuePair<string, string> f in fields)
            {
                if (n >= maxFields) { kv.Add(prefix + "more"); kv.Add(fields.Count - maxFields); return; }
                kv.Add(prefix + f.Key.Replace(' ', '_').Replace('=', '_')); kv.Add(Clip(f.Value, MaxValue)); n++;
            }
        }

        // Angle in degrees (0..180) between a projectile's velocity and the direction to a player; -1 when either is zero.
        public static float AngleDeg(float vx, float vy, float dx, float dy)
        {
            double a = Math.Sqrt(vx * vx + vy * vy), b = Math.Sqrt(dx * dx + dy * dy);
            if (a < 1e-6 || b < 1e-6) return -1f;
            double c = (vx * dx + vy * dy) / (a * b);
            if (c > 1) c = 1; if (c < -1) c = -1;
            return (float)(Math.Acos(c) * 180.0 / Math.PI);
        }
    }
}
