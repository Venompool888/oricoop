using System;
using System.Collections.Generic;
using OriCoop.Sweep;

class SweepTests
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    static SweepRecord Record(int index, string side, string verdict)
    { return new SweepRecord { Index = index, Side = side, Kind = "scene", Scene = "a", Verdict = verdict, Start = "2026-11-18T01:00:00Z", End = verdict == "RUNNING" ? "" : "2026-11-18T01:01:00Z" }; }
    static int Main()
    {
        try {
            List<SweepScene> scenes = new List<SweepScene> {
                new SweepScene("b", false, true, null, 2), new SweepScene("dep", true, true, true, 3),
                new SweepScene("off", false, false, false, 1), new SweepScene("a", false, true, false, 1) };
            List<SweepEntry> plan = SweepPlan.Build(scenes, null, null, null, null);
            Check(plan.Count == 5 && plan[0].Scene == "b" && plan[0].Kind == "scene" && plan[1].Pedestal == 0 && plan[2].Pedestal == 1 && plan[3].Scene == "a", "stable scene and pedestal order; no save-data exclusion");
            Check(SweepPlan.Build(scenes, "1", "3", null, null).Count == 3, "inclusive from/to");
            List<SweepEntry> filtered = SweepPlan.Build(scenes, "1", "4", "1/2", " a , b ");
            Check(filtered.Count == 2 && filtered[0].Index == 1 && filtered[1].Index == 3, "shard uses original indices");
            filtered = SweepPlan.Build(scenes, null, null, null, "a");
            Check(filtered.Count == 2 && filtered[0].Index == 3, "name filter preserves original indices");
            Check(SweepPlan.Build(scenes, "4", "1", null, null).Count == 0, "empty range");
            List<SweepScene> gameplay = new List<SweepScene> {
                new SweepScene("creditsScreen", false, true, null, 0), new SweepScene("worldMapScene", false, true, null, 0),
                new SweepScene("titleScreenSwallowsNest", false, true, null, 0), new SweepScene("introScene", false, true, null, 0),
                new SweepScene("outroScene", false, true, null, 0), new SweepScene("forestBackground", false, true, null, 0),
                new SweepScene("ginsoCutscene", false, true, null, 0), new SweepScene("FORESTBACKGROUND", false, true, null, 0),
                new SweepScene("forest", false, true, null, 0) };
            Check(SweepPlan.Build(gameplay, null, null, null, null).Count == 1, "default skip regex excludes non-gameplay case-insensitively");
            Check(SweepPlan.Build(gameplay, null, null, null, null, "^forest$").Count == 8, "custom skip replaces default");
            Check(SweepPlan.Build(gameplay, null, null, null, null, "(?!)").Count == 9, "explicit nonmatching skip includes all");
            Check(SweepPlan.Build(gameplay, null, null, null, "creditsScreen").Count == 0, "name selection cannot bypass default skip");
            List<SweepScene> cinematic = new List<SweepScene> {
                new SweepScene("kuroAttackLandCliff", false, true, null, 0), new SweepScene("kuroNestFeeding", false, true, null, 0),
                new SweepScene("kuroMomentTree", false, true, null, 0), new SweepScene("theSacrificeBurningNest", false, true, null, 0),
                new SweepScene("sacrificeInsideNest", false, true, null, 0), new SweepScene("swallowsNestTheGift", false, true, null, 0),
                new SweepScene("mountHoruHubTop", false, true, null, 0) };
            Check(SweepPlan.Build(cinematic, null, null, null, null).Count == 1, "default skip excludes cinematic/prologue scenes only");
            bool invalidSkip = false; try { SweepPlan.Build(gameplay, null, null, null, null, "["); } catch (ArgumentException) { invalidSkip = true; }
            Check(invalidSkip, "invalid skip regex rejected");
            bool bad = false; try { SweepPlan.Build(scenes, null, null, "2/2", null); } catch (ArgumentException) { bad = true; }
            Check(bad, "invalid shard rejected");
            Check(SweepPlan.Hash(plan) == SweepPlan.Hash(SweepPlan.Build(scenes, null, null, null, null)), "hash deterministic");
            Check(SweepPlan.Hash(plan) != SweepPlan.Hash(filtered), "hash includes selection");
            Check(SweepReport.Classify(true, true, false, 1, new string[0]) == "PASS", "pass");
            Check(SweepReport.Classify(true, true, false, 1, new[] { "camera" }) == "FAIL", "failed check");
            Check(SweepReport.Classify(false, true, false, 1, new[] { "load" }) == "FAIL", "could not load is failure");
            Check(SweepReport.Classify(true, false, false, 1, new string[0]) == "FAIL", "place not found is failure");
            Check(SweepReport.Classify(false, false, true, 0, new[] { "exception-storm" }) == "FAIL", "exception storm overrides inconclusive");
            Check(SweepReport.Classify(true, true, true, 0, new string[0]) == "INCONCLUSIVE", "timeout before checks");
            Check(SweepReport.Classify(true, true, true, 1, new string[0]) == "FAIL", "timeout after checks");
            List<SweepRecord> records = new List<SweepRecord> { Record(0, "host", "PASS"), Record(0, "guest", "FAIL"), Record(1, "host", "PASS"), Record(2, "host", "RUNNING") };
            records[1].Fails = "camera";
            List<SweepRecord> merged = SweepReport.Merge(plan, records);
            Check(merged[0].Verdict == "FAIL" && merged[0].Fails.Contains("camera"), "merge disagreement");
            Check(merged[1].Verdict == "INCONCLUSIVE" && merged[1].Fails.Contains("missing-guest"), "missing side");
            Check(SweepReport.ResumeIndex(plan, records) == 1, "resume requires both final sides");
            records.Add(Record(1, "guest", "PASS"));
            Check(SweepReport.ResumeIndex(plan, records) == 2, "running is not final");
            records.Add(Record(2, "host", "FAIL")); records.Add(Record(2, "guest", "FAIL"));
            Check(SweepReport.ResumeIndex(plan, records) == 3, "crash final advances resume");
            List<SweepRecord> interrupted = new List<SweepRecord> { Record(0, "host", "PASS"), Record(0, "guest", "PASS"), Record(1, "host", "RUNNING") };
            List<SweepRecord> crashes = SweepReport.Interrupted(interrupted, "2026-11-18T02:00:00Z");
            Check(crashes.Count == 2 && crashes[0].Verdict == "FAIL" && crashes[0].Fails == "crash" && crashes[0].End == "2026-11-18T02:00:00Z", "crash finalizes active entry on both sides");
            interrupted.AddRange(crashes);
            Check(SweepReport.ResumeIndex(plan, interrupted) == 2, "resume after synthesized crash records");
            Check(SweepReport.Interrupted(interrupted, "later").Count == 0, "crash finalization idempotent");
            Check(SweepReport.Merge(plan, new[] { Record(0, "host", "FAIL") })[0].Verdict == "INCONCLUSIVE", "missing side overrides fail");
            List<SweepRecord> aborted = new List<SweepRecord> { Record(0, "host", "PASS"), Record(0, "guest", "PASS"),
                Record(1, "host", "FAIL"), Record(1, "guest", "FAIL"), Record(1, "host", "ABORT") };
            aborted[4].Kind = "abort"; aborted[4].Note = "SWEEP ABORT recoveries=3";
            List<SweepRecord> abortMerge = SweepReport.Merge(plan, aborted);
            Check(abortMerge[1].Verdict == "FAIL", "abort marker preserves triggering failure");
            Check(abortMerge[2].Verdict == "INCONCLUSIVE" && abortMerge[2].Fails.Contains("missing-host"), "unvisited entry after abort stays inconclusive");
            Check(SweepReport.Interrupted(aborted, "later").Count == 0, "abort marker is not an interrupted entry");
            Check(SweepReport.ResumeIndex(plan, aborted) == plan[2].Index, "abort restarts the game at the next entry");
            SweepRecord quoted = Record(4, "host", "FAIL"); quoted.Note = "comma, quote\" and\nnewline";
            Check(SweepRecord.ParseCsv(quoted.ToCsv()).Note == quoted.Note.Replace('\n', ' '), "CSV escaping round trip");
            Check(SweepReport.Markdown(merged).Contains("| 0 |") && SweepReport.Csv(merged).Contains("INCONCLUSIVE"), "report formats");
            return 0;
        } catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
    }
}
