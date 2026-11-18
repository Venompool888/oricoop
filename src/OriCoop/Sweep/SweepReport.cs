using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OriCoop.Sweep
{
    public sealed class SweepRecord
    {
        public const string Header = "index,side,kind,scene,verdict,fails,t0,t1,note";
        public int Index;
        public string Side = "", Kind = "", Scene = "", Verdict = "RUNNING", Fails = "", Start = "", End = "", Note = "";
        public bool Final { get { return End.Length > 0 && (Verdict == "PASS" || Verdict == "FAIL" || Verdict == "INCONCLUSIVE"); } }
        static string Quote(string s) { return "\"" + (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "\"\"") + "\""; }
        public string ToCsv()
        {
            string[] fields = { Index.ToString(CultureInfo.InvariantCulture), Side, Kind, Scene, Verdict, Fails, Start, End, Note };
            for (int i = 0; i < fields.Length; i++) fields[i] = Quote(fields[i]);
            return string.Join(",", fields);
        }
        public static SweepRecord ParseCsv(string line)
        {
            List<string> fields = new List<string>(); StringBuilder field = new StringBuilder(); bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
                else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Length = 0; }
                else field.Append(c);
            }
            fields.Add(field.ToString());
            if (quoted || fields.Count != 9) throw new FormatException("Invalid sweep CSV row");
            return new SweepRecord { Index = int.Parse(fields[0], CultureInfo.InvariantCulture), Side = fields[1], Kind = fields[2], Scene = fields[3],
                Verdict = fields[4], Fails = fields[5], Start = fields[6], End = fields[7], Note = fields[8] };
        }
    }
    public static class SweepReport
    {
        public static string Classify(bool loaded, bool foundBoth, bool timeout, int checks, ICollection<string> failed)
        {
            if (failed.Contains("exception-storm") || !loaded || !foundBoth) return "FAIL";
            if (timeout && checks == 0) return "INCONCLUSIVE";
            return timeout || failed.Count > 0 ? "FAIL" : "PASS";
        }
        static Dictionary<string, SweepRecord> Latest(IEnumerable<SweepRecord> records)
        {
            Dictionary<string, SweepRecord> result = new Dictionary<string, SweepRecord>();
            foreach (SweepRecord r in records) if (r.Verdict != "ABORT") result[r.Index + ":" + r.Side] = r;
            return result;
        }
        public static int ResumeIndex(IList<SweepEntry> plan, IEnumerable<SweepRecord> records)
        {
            List<SweepRecord> all = new List<SweepRecord>(records);
            Dictionary<string, SweepRecord> latest = Latest(all);
            foreach (SweepEntry e in plan)
            {
                SweepRecord h, g;
                if (!latest.TryGetValue(e.Index + ":host", out h) || !h.Final || !latest.TryGetValue(e.Index + ":guest", out g) || !g.Final) return e.Index;
            }
            return plan.Count == 0 ? 0 : plan[plan.Count - 1].Index + 1;
        }
        // Only touched entries are finalized: never invent crash failures for the unvisited tail.
        public static List<SweepRecord> Interrupted(IEnumerable<SweepRecord> records, string utc)
        {
            Dictionary<string, SweepRecord> latest = Latest(records);
            SortedDictionary<int, SweepRecord> touched = new SortedDictionary<int, SweepRecord>();
            foreach (SweepRecord r in latest.Values) touched[r.Index] = r;
            List<SweepRecord> result = new List<SweepRecord>();
            foreach (KeyValuePair<int, SweepRecord> pair in touched)
                foreach (string side in new[] { "host", "guest" })
                {
                    SweepRecord r; latest.TryGetValue(pair.Key + ":" + side, out r);
                    if (r != null && r.Final) continue;
                    SweepRecord source = r ?? pair.Value;
                    result.Add(new SweepRecord { Index = pair.Key, Side = side, Kind = source.Kind, Scene = source.Scene,
                        Verdict = "FAIL", Fails = "crash", Start = source.Start, End = utc, Note = "process ended or timed out before final record" });
                }
            return result;
        }
        static void Add(List<string> list, string value) { if (!string.IsNullOrEmpty(value) && !list.Contains(value)) list.Add(value); }
        public static List<SweepRecord> Merge(IList<SweepEntry> plan, IEnumerable<SweepRecord> records)
        {
            List<SweepRecord> all = new List<SweepRecord>(records);
            Dictionary<string, SweepRecord> latest = Latest(all); List<SweepRecord> result = new List<SweepRecord>();
            foreach (SweepEntry e in plan)
            {
                SweepRecord h, g; latest.TryGetValue(e.Index + ":host", out h); latest.TryGetValue(e.Index + ":guest", out g);
                bool haveH = h != null && h.Final, haveG = g != null && g.Final;
                List<string> fails = new List<string>();
                if (!haveH) fails.Add("missing-host"); if (!haveG) fails.Add("missing-guest");
                if (h != null) foreach (string f in h.Fails.Split(',')) Add(fails, f);
                if (g != null) foreach (string f in g.Fails.Split(',')) Add(fails, f);
                string verdict = !haveH || !haveG ? "INCONCLUSIVE" : h.Verdict == "FAIL" || g.Verdict == "FAIL" ? "FAIL"
                    : h.Verdict == "PASS" && g.Verdict == "PASS" ? "PASS" : "INCONCLUSIVE";
                string start = h == null ? "" : h.Start, end = h == null ? "" : h.End;
                if (g != null) { if (start == "" || string.CompareOrdinal(g.Start, start) < 0) start = g.Start; if (string.CompareOrdinal(g.End, end) > 0) end = g.End; }
                result.Add(new SweepRecord { Index = e.Index, Side = "merged", Kind = e.Kind, Scene = e.Scene, Verdict = verdict,
                    Fails = string.Join(",", fails.ToArray()), Start = start, End = end,
                    Note = "host: " + (h == null ? "missing" : h.Note) + "; guest: " + (g == null ? "missing" : g.Note) });
            }
            return result;
        }
        public static string Csv(IList<SweepRecord> records)
        {
            StringBuilder b = new StringBuilder(SweepRecord.Header + "\n"); foreach (SweepRecord r in records) b.Append(r.ToCsv()).Append('\n'); return b.ToString();
        }
        static string Cell(string s) { return (s ?? "").Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' '); }
        public static string Markdown(IList<SweepRecord> records)
        {
            StringBuilder b = new StringBuilder("# Scene sweep\n\nIndices are zero-based. UTC timestamps locate evidence in the archived per-PC logs. Failures require triage (no automatic issues).\n\n| Index | Kind | Scene | Verdict | Failed checks | UTC start | UTC end | Evidence / note |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
            foreach (SweepRecord r in records) b.Append("| ").Append(r.Index).Append(" | ").Append(Cell(r.Kind)).Append(" | ").Append(Cell(r.Scene)).Append(" | ").Append(r.Verdict)
                .Append(" | ").Append(Cell(r.Fails)).Append(" | ").Append(r.Start).Append(" | ").Append(r.End).Append(" | ").Append(Cell(r.Note)).Append(" |\n");
            return b.ToString();
        }
    }
}
