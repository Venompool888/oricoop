using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OriCoop;
using OriCoop.Ui;

static class UiTests
{
    static int s_failed, s_cases;
    static string s_language = "en";

    static void Run(string name, Action test)
    {
        s_cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { s_failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    static HashSet<int> Slots(string text)
    {
        HashSet<int> slots = new HashSet<int>();
        foreach (Match m in Regex.Matches(text, @"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}"))
            slots.Add(int.Parse(m.Groups[1].Value));
        return slots;
    }

    static int Main()
    {
        L10n.LanguageCode = delegate { return s_language; };

        Run("all keys resolve in both languages", delegate {
            int count = 0;
            foreach (string key in L10n.Keys)
            {
                count++;
                Check(L10n.Has(key), key + " missing");
                foreach (bool zh in new[] { false, true })
                {
                    string value = L10n.T(key, zh);
                    Check(!string.IsNullOrEmpty(value) && value != key, key + " missing " + (zh ? "zh" : "en"));
                }
            }
            Check(count > 0, "empty table");
        });
        Run("every translation has matching format arguments", delegate {
            object[] args = new object[32];
            for (int i = 0; i < args.Length; i++) args[i] = "arg" + i;
            foreach (string key in L10n.Keys)
            {
                string en = L10n.T(key, false), zh = L10n.T(key, true);
                Check(Slots(en).SetEquals(Slots(zh)), key + " has different placeholders");
                try { string.Format(en, args); string.Format(zh, args); }
                catch (FormatException e) { throw new Exception(key + ": " + e.Message); }
            }
        });
        Run("english lookup", delegate { s_language = "en"; Equal("Back", L10n.T("common.back")); });
        Run("chinese lookup", delegate { s_language = "zh"; Equal("返回", L10n.T("common.back")); });
        Run("unknown language defaults to english", delegate { s_language = "unknown"; Equal("Back", L10n.T("common.back")); });
        Run("null language defaults to english", delegate { s_language = null; Equal("Back", L10n.T("common.back")); });
        Run("unknown key falls back to key", delegate { Equal("missing.test", L10n.T("missing.test", false)); });
        Run("null key falls back to empty", delegate { Equal("", L10n.T(null, false)); Check(!L10n.Has(null), "null key exists"); });
        Run("missing key is counted once", delegate {
            int before = L10n.MissingCount;
            L10n.T("missing.once", false); L10n.T("missing.once", true);
            Equal(before + 1, L10n.MissingCount);
            Check(L10n.MissingList.Contains("missing.once"), "missing list omits key");
        });
        Run("missing key warning is emitted once", delegate {
            List<string> warnings = new List<string>(); L10n.Warning = warnings.Add;
            L10n.T("missing.warning", false); L10n.T("missing.warning", true);
            L10n.Warning = null;
            Equal(1, warnings.Count); Equal("l10n: missing key 'missing.warning'", warnings[0]);
        });
        Run("language hook retries until installed", delegate {
            int calls = 0; L10n.EnsureHook = delegate { calls++; };
            L10n.T("common.back", false); L10n.T("common.back", false);
            L10n.EnsureHook = null; Equal(2, calls);
        });
        Run("english format arguments", delegate {
            s_language = "en"; Equal("My rooms - Ada", L10n.F("rooms.title", "Ada"));
        });
        Run("chinese format arguments", delegate {
            s_language = "zh"; Equal("我的房间 — Ada", L10n.F("rooms.title", "Ada"));
        });
        Run("invalid format returns original", delegate {
            s_language = "en"; Equal(L10n.T("rooms.title"), L10n.F("rooms.title"));
        });
        Run("set binds live translated text", delegate {
            Func<string> text = null;
            Check(L10n.Set(delegate(Func<string> value) { text = value; }, "common.back"), "set failed");
            s_language = "en"; Equal("Back", text());
            s_language = "zh"; Equal("返回", text());
        });
        Run("set binds live value provider", delegate {
            string value = "first"; Func<string> text = null;
            Check(L10n.Set(delegate(Func<string> f) { text = f; }, delegate { return value; }), "set failed");
            value = "second"; Equal("second", text());
        });
        Run("set rejects missing target", delegate { Check(!L10n.Set((Action<Func<string>>)null, "common.back"), "null target accepted"); });

        Run("strip supported rich text", delegate {
            Equal("one two", NativeGuiLogic.PlainText("<b>one</b> <color=#fff>two</color>"));
        });
        Run("retain unsupported markup", delegate { Equal("<other>x</other>", NativeGuiLogic.PlainText("<other>x</other>")); });
        Run("wrap at measured line changes", delegate {
            float[] y = { 0, 0, 1, 1, 2 };
            Equal("ab\ncd\ne", NativeGuiLogic.Wrap("abcde", delegate(int i) { return y[i]; }));
        });
        Run("wrap does not duplicate existing newline", delegate {
            float[] y = { 0, 0, 1, 1 };
            Equal("a\nbc", NativeGuiLogic.Wrap("a\nbc", delegate(int i) { return y[i]; }));
        });
        Run("alignment left center right", delegate {
            Equal(10f, NativeGuiLogic.Align(10f, 100f, 40f, 0));
            Equal(40f, NativeGuiLogic.Align(10f, 100f, 40f, 1));
            Equal(70f, NativeGuiLogic.Align(10f, 100f, 40f, 2));
        });
        Run("text scale fits available width", delegate { Equal(0.5f, NativeGuiLogic.FitScale(2f, 100f, 50f, 1f)); });
        Run("text scale stays when it fits", delegate { Equal(0.5f, NativeGuiLogic.FitScale(0.5f, 100f, 50f, 1f)); });
        Run("clip accepts one pixel tolerance", delegate { Check(NativeGuiLogic.Inside(-1f, 101f, -1f, 101f, 0f, 100f, 0f, 100f), "within tolerance clipped"); });
        Run("clip hides outside viewport", delegate { Check(!NativeGuiLogic.Inside(-1.1f, 100f, 0f, 100f, 0f, 100f, 0f, 100f), "outside viewport visible"); });
        Run("focus and hover highlight navigation", delegate {
            Check(NativeGuiLogic.Highlight(true, false), "focus not highlighted");
            Check(NativeGuiLogic.Highlight(false, true), "hover not highlighted");
            Check(!NativeGuiLogic.Highlight(false, false), "idle highlighted");
        });
        Run("page changes preserve notification order", delegate {
            NativeGuiState state = new NativeGuiState(); List<string> calls = new List<string>();
            state.PageChanged = delegate(string page) { Equal(page, state.Page); calls.Add(page); };
            state.BeginPage("first"); state.EndPage(); state.BeginPage("second"); state.EndPage();
            Equal("first,,second,", string.Join(",", calls.ToArray())); Equal("", state.Page);
        });
        Run("frame captures pages and label calls", delegate {
            NativeGuiState state = new NativeGuiState();
            Check(state.StartFrame(5), "first frame ignored"); state.AddPage("a"); state.AddLabel("x"); state.AddLabel("x");
            Check(!state.StartFrame(5), "same frame reset"); state.Capture(6);
            Check(state.PagesLastFrame.Contains("a") && state.LastLabelIds.Contains("x"), "previous frame missing");
            Equal(2, state.LabelsLastFrame); Equal(1, state.LastLabelIds.Count);
        });
        Run("stale frame clears last frame", delegate {
            NativeGuiState state = new NativeGuiState(); state.StartFrame(3); state.AddPage("a"); state.AddLabel("x");
            state.Capture(4); state.Capture(5);
            Equal(0, state.PagesLastFrame.Count); Equal(0, state.LastLabelIds.Count); Equal(0, state.LabelsLastFrame);
        });
        Run("new frame resets current data", delegate {
            NativeGuiState state = new NativeGuiState(); state.StartFrame(3); state.AddPage("old"); state.AddLabel("old");
            state.StartFrame(4); state.AddPage("new"); state.Capture(5);
            Check(!state.PagesLastFrame.Contains("old") && state.PagesLastFrame.Contains("new"), "page reset failed");
            Equal(0, state.LabelsLastFrame);
        });
        Run("warnings deduplicate exact failures", delegate {
            NativeGuiState state = new NativeGuiState();
            Check(state.ShouldWarn("draw", "a", new InvalidOperationException("bad")), "first warning lost");
            Check(!state.ShouldWarn("draw", "a", new InvalidOperationException("bad")), "duplicate warning");
            Check(state.ShouldWarn("draw", "b", new InvalidOperationException("bad")), "different id lost");
            Check(state.ShouldWarn("draw", "a", new ArgumentException("bad")), "different type lost");
            Check(state.ShouldWarn("draw", "a", new InvalidOperationException("other")), "different message lost");
        });

        // T-032: build info shown on the title screen and in F8.
        Run("build info: a clean commit line gives the commit", delegate {
            Equal("abc1234", BuildInfo.ParseCommit("commit=abc1234\n"));
            Equal("0123456789abcdef0123456789abcdef01234567", BuildInfo.ParseCommit("commit=0123456789abcdef0123456789abcdef01234567"));
        });
        Run("build info: a working-tree build keeps +local", delegate {
            Equal("abc1234+local", BuildInfo.ParseCommit("commit=abc1234+local\r\n"));
        });
        Run("build info: missing or malformed resource is unknown", delegate {
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit(null));
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit(""));
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit("sha=abc1234"));
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit("commit=abc123"));      // 6 chars: below git's short form
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit("commit=ABC1234"));     // git prints lower-case hex
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit("commit=xyz1234"));
            Equal(BuildInfo.Unknown, BuildInfo.ParseCommit("commit=" + new string('a', 41)));
        });
        Run("build info: label names version and commit in both languages", delegate {
            Equal("0.2.0 (abc1234)", BuildInfo.Short("0.2.0", "abc1234"));
            Equal("0.2.0 (unknown)", BuildInfo.Short("0.2.0", null));
            foreach (bool zh in new[] { false, true })
            {
                string text = string.Format(L10n.T("build.version", zh), BuildInfo.Short("0.2.0", "abc1234"));
                Check(text.Contains("0.2.0") && text.Contains("abc1234"), "build.version " + (zh ? "zh" : "en") + ": " + text);
            }
            Equal("打开日志文件夹", L10n.T("coop.open_logs", true));
        });

        return s_failed == 0 ? 0 : 1;
    }
}
