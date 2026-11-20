using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OriCoop.Sweep
{
    public sealed class SweepScene
    {
        public string Name;
        public bool Dependant, Loadable;
        public bool? HasSaveData;
        public int Pedestals;
        public SweepScene(string name, bool dependant, bool loadable, bool? hasSaveData, int pedestals)
        { Name = name; Dependant = dependant; Loadable = loadable; HasSaveData = hasSaveData; Pedestals = pedestals; }
    }
    public sealed class SweepEntry
    {
        public int Index, Pedestal;
        public string Scene, Kind;
    }
    public static class SweepPlan
    {
        // catAndMouse* = scripted escape chases (teleporting in triggers the scripted death/camera); *Test*/*Placeholder =
        // dev scenes outside the world map, from which recovery cannot teleport back. kuroAttack/kuroMoment/kuroNest/
        // theSacrifice/sacrifice*/swallowsNest* = cinematic or prologue scenes with no playable ground (first full sweep:
        // every one failed placement after a 4-minute timeout). spiritTreeToRunaway* = the scripted run-away transition: entering
        // it starts the transition, so it never stays loaded (T-102).
        public const string DefaultSkip = "(?i)^(credits|worldMap|titleScreen|intro|outro|catAndMouse|kuroAttack|kuroMoment|kuroNest|theSacrifice|sacrifice|swallowsNest|spiritTreeToRunaway|.*Background$|.*Cutscene|.*Test|.*Placeholder$)";
        static int Index(string value, int fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            int n;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out n)) throw new ArgumentException("Invalid sweep index: " + value);
            return n;
        }
        // Zero-based inclusive indices in the unfiltered plan; k/n selects indices congruent to k.
        public static List<SweepEntry> Build(IList<SweepScene> scenes, string from, string to, string shard, string names)
        { return Build(scenes, from, to, shard, names, null); }
        public static List<SweepEntry> Build(IList<SweepScene> scenes, string from, string to, string shard, string names, string skip)
        {
            Regex excluded = new Regex(skip ?? DefaultSkip, RegexOptions.CultureInvariant);
            int first = Index(from, 0), last = Index(to, int.MaxValue), k = 0, n = 1;
            if (!string.IsNullOrEmpty(shard))
            {
                string[] parts = shard.Split('/');
                if (parts.Length != 2) throw new ArgumentException("Shard must be k/n (zero based)");
                k = Index(parts[0], -1); n = Index(parts[1], 0);
                if (n < 1 || k < 0 || k >= n) throw new ArgumentException("Shard requires 0 <= k < n");
            }
            HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(names)) foreach (string name in names.Split(',')) if (name.Trim().Length > 0) selected.Add(name.Trim());
            List<SweepEntry> result = new List<SweepEntry>(); int index = 0;
            foreach (SweepScene scene in scenes)
            {
                if (scene.Dependant || !scene.Loadable || excluded.IsMatch(scene.Name)) continue;
                for (int p = -1; p < scene.Pedestals; p++, index++)
                    if (index >= first && index <= last && index % n == k && (selected.Count == 0 || selected.Contains(scene.Name)))
                        result.Add(new SweepEntry { Index = index, Scene = scene.Name, Kind = p < 0 ? "scene" : "save", Pedestal = p });
            }
            return result;
        }
        // T-102: standing-spot candidates inside a scene's bounds after the centre: three rows from the top down (Ori is
        // dropped onto the floor below each point), middle columns first.
        public static List<float[]> Candidates(float xMin, float yMin, float width, float height)
        {
            List<float[]> result = new List<float[]>();
            foreach (float fy in new[] { 0.75f, 0.5f, 0.25f })
                foreach (float fx in new[] { 0.5f, 0.3f, 0.7f, 0.15f, 0.85f })
                    if (!(fx == 0.5f && fy == 0.5f)) result.Add(new[] { xMin + width * fx, yMin + height * fy });
            return result;
        }
        // A float carried in an int field of a barrier sample, bit for bit.
        public static int FloatBits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }
        public static float BitsFloat(int bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
        public static string Hash(IList<SweepEntry> entries)
        {
            // FNV-1a over explicit invariant, length-delimited UTF-8 fields (not randomized String.GetHashCode).
            StringBuilder text = new StringBuilder();
            foreach (SweepEntry e in entries)
                text.Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.Scene.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(e.Scene)
                    .Append(':').Append(e.Kind).Append(':').Append(e.Pedestal.ToString(CultureInfo.InvariantCulture)).Append('\n');
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text.ToString())) { hash ^= b; hash = unchecked(hash * 1099511628211UL); }
            return hash.ToString("X16", CultureInfo.InvariantCulture);
        }
    }
}
