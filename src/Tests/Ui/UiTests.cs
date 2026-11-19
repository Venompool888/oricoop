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

    // Render with the wheels from..to (inclusive) bracketed as one span, for readable expectations.
    static string Mark(DigitDial d, int from, int to)
    {
        string plain = d.Render(false, null); int digit = -1; System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (char c in plain)
        {
            bool isDigit = c >= '0' && c <= '9'; if (isDigit) digit++;
            if (isDigit && digit == from) sb.Append('<');
            sb.Append(c);
            if (isDigit && digit == to) sb.Append('>');
        }
        return sb.ToString();
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

        // T-093: the how-to-play chooser must stay readable and proportional from 720p to 4K and on other aspect ratios.
        Run("mode select: text and panel scale with the screen height", delegate {
            int[][] screens = { new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 } };
            foreach (int[] s in screens)
            {
                ModeSelectLayout l = ModeSelectLayout.Compute(s[0], s[1]);
                Check(l.FontPx >= s[1] / 45f, s[0] + "x" + s[1] + ": body font " + l.FontPx + " px is too small (need >= " + (s[1] / 45f) + ")");
                Check(l.TitlePx > l.FontPx && l.SmallPx < l.FontPx, s[0] + "x" + s[1] + ": title / body / small order");
                Check(l.PanelW / s[0] >= 0.3f && l.PanelW <= s[0] && l.PanelH <= s[1], s[0] + "x" + s[1] + ": panel " + l.PanelW + "x" + l.PanelH + " not proportional to the screen");
                Check(l.RowH >= l.FontPx * 2f, s[0] + "x" + s[1] + ": row height " + l.RowH + " cannot hold two lines of " + l.FontPx + " px");
            }
            ModeSelectLayout hd = ModeSelectLayout.Compute(1920, 1080), uhd = ModeSelectLayout.Compute(3840, 2160);
            Check(Math.Abs(uhd.FontPx - 2 * hd.FontPx) <= 2 && Math.Abs(uhd.PanelW - 2 * hd.PanelW) <= 4 && Math.Abs(uhd.PanelH - 2 * hd.PanelH) <= 4,
                "4K is not twice 1080p: font " + hd.FontPx + " -> " + uhd.FontPx + ", panel " + hd.PanelW + " -> " + uhd.PanelW);
        });
        Run("mode select: ultra-wide and 4:3 screens keep the panel inside", delegate {
            foreach (int[] s in new[] { new[] { 2560, 1080 }, new[] { 1440, 1080 }, new[] { 640, 360 } })
            {
                ModeSelectLayout l = ModeSelectLayout.Compute(s[0], s[1]);
                Check(l.PanelW <= s[0] - 20 && l.PanelH <= s[1] && l.FontPx >= 14, s[0] + "x" + s[1] + ": panel " + l.PanelW + "x" + l.PanelH + ", font " + l.FontPx);
            }
        });

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
        Run("controls guide names the default keys in each language", delegate {
            Equal("Move: left stick or A / D      Jump: A button or Space", ControlsGuide.Text(false, "A", "D", "Space"));
            Equal("移动：左摇杆 或 A / D      跳跃：A 键 或 空格键", ControlsGuide.Text(true, "A", "D", "Space"));
            Equal("Left / Right arrows", ControlsGuide.MoveKeys(false, "LeftArrow", "RightArrow"));
            Equal("左右方向键", ControlsGuide.MoveKeys(true, "LeftArrow", "RightArrow"));
            Equal("Q / D", ControlsGuide.MoveKeys(false, "Q", "D"));
            Equal("1", ControlsGuide.KeyName(false, "Alpha1"));
            Equal("Shift", ControlsGuide.KeyName(true, "LeftShift"));
            Equal("?", ControlsGuide.KeyName(false, "None"));
        });
        Run("controls guide waits for steady gameplay, shows once, then stays gone", delegate {
            ControlsGuide g = new ControlsGuide(false);
            Equal(0f, g.Update(false, 0f));
            Equal(0f, g.Update(true, 1f));                       // gameplay starts
            Equal(0f, g.Update(true, 1f + ControlsGuide.Delay - 0.1f));
            Equal(0f, g.Update(true, 1f + ControlsGuide.Delay)); // appears now, fading in
            float shownAt = 1f + ControlsGuide.Delay;
            Equal(1f, g.Update(true, shownAt + ControlsGuide.Fade + 1f));
            Check(g.Showing, "showing in the middle");
            Equal(0f, g.Update(false, shownAt + 3f));             // a menu hides it without restarting it
            Equal(1f, g.Update(true, shownAt + 4f));
            Equal(0f, g.Update(true, shownAt + ControlsGuide.Duration + 2f * ControlsGuide.Fade));
            Check(g.Done, "done after its time");
            Equal(0f, g.Update(true, shownAt + 100f));
        });
        Run("controls guide stays hidden once seen", delegate {
            ControlsGuide g = new ControlsGuide(true);
            Check(g.Done, "seen means done");
            Equal(0f, g.Update(true, 10f));
        });
        Run("controls guide restarts its wait when gameplay drops before it shows", delegate {
            ControlsGuide g = new ControlsGuide(false);
            g.Update(true, 0f); g.Update(false, 1f);
            Equal(0f, g.Update(true, 1.2f));
            Equal(0f, g.Update(true, 1.2f + ControlsGuide.Delay - 0.1f));
            Check(!g.Showing, "not yet");
        });
        Run("zh text keys are short SHA-1 hashes of the English line", delegate {
            Equal("3159fe421b322138", ZhTextKey.Of("Empty"));
            Equal("fec334bd25f727f9", ZhTextKey.Of("I am #Sein#, the light and eyes of the #Spirit Tree#."));
            Check(ZhTextKey.IsKey("0123456789abcdef") && !ZhTextKey.IsKey("Empty") && !ZhTextKey.IsKey("0123456789ABCDEF"), "IsKey");
        });
        Run("published zh_extra.txt holds hashed keys only, no game text", delegate {
            string dir = AppDomain.CurrentDomain.BaseDirectory, file = null;
            for (int i = 0; i < 8 && dir != null && file == null; i++)
            {
                string f = System.IO.Path.Combine(dir, System.IO.Path.Combine("src", System.IO.Path.Combine("assets", System.IO.Path.Combine("zh-text", "zh_extra.txt"))));
                if (System.IO.File.Exists(f)) file = f; else dir = System.IO.Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            if (file == null) { Console.WriteLine("  (zh_extra.txt not in this checkout; skipped)"); return; }   // public OriCoop repo
            int rows = 0; bool sein = false;
            foreach (string line in System.IO.File.ReadAllLines(file, System.Text.Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int tab = line.IndexOf('\t');
                Check(tab > 0 && ZhTextKey.IsKey(line.Substring(0, tab)), "not a hashed row: " + line);
                rows++;
                if (line.StartsWith("fec334bd25f727f9\t")) sein = true;
            }
            Check(rows >= 50 && sein, rows + " rows, Sein line " + sein);
        });

        // T-046: digit dials for IP / port / room code.
        Run("dial: up and down wrap around 9 and 0", delegate {
            DigitDial d = DigitDial.Port();
            d.Down(); Equal(9, d.Digit(0));
            d.Up(); Equal(0, d.Digit(0));
            d.Up(); d.Up(); Equal(2, d.Digit(0));
        });
        Run("dial: left and right stop at the ends", delegate {
            DigitDial d = DigitDial.Room();
            d.Left(); Equal(0, d.Focus);
            for (int i = 0; i < 10; i++) d.Right();
            Equal(5, d.Focus);
        });
        Run("dial: digit keys fill and advance, backspace zeroes and steps back", delegate {
            DigitDial d = DigitDial.Room();
            foreach (int k in new[] { 4, 7, 0, 1, 2, 9 }) d.Type(k);
            Equal("470129", d.Digits); Equal(5, d.Focus);
            d.Type(3); Equal("470123", d.Digits);                  // last wheel: overwrite, stay
            d.Back(); Equal("470120", d.Digits); Equal(4, d.Focus);
            d.Type(-1); d.Type(10); Equal("470120", d.Digits);     // not digits: ignored
        });
        Run("dial: IP loads right-aligned per octet and gives numbers back", delegate {
            DigitDial d = DigitDial.Ip();
            string addr = string.Join(".", new[] { "192", "168", "1", "23" });
            d.Load(addr);
            Equal("192168001023", d.Digits); Equal(addr, d.Numbers(".")); Equal(0, d.Focus);
            Equal(string.Join(".", new[] { "192", "168", "001", "<023>" }), Mark(d, 9, 11));
            Check(DigitDial.ValidIp(d), "valid ip rejected");
            d.Load(string.Join(".", new[] { "256", "1", "1", "1" })); Check(!DigitDial.ValidIp(d), "octet 256 accepted");
            d.Load(""); Check(!DigitDial.ValidIp(d), "all-zero address accepted");
            d.Load(string.Join(".", new[] { "1", "2" })); Equal(string.Join(".", new[] { "001", "002", "000", "000" }), d.Render(false, null));
        });
        Run("dial: port range and room code digits", delegate {
            DigitDial p = DigitDial.Port();
            p.Load("47011"); Equal("47011", p.Numbers("")); Check(DigitDial.ValidPort(p), "47011 rejected");
            p.Load("0"); Check(!DigitDial.ValidPort(p), "port 0 accepted");
            p.Load("65536"); Check(!DigitDial.ValidPort(p), "port 65536 accepted");
            p.Load("99999999"); Equal("99999", p.Digits);           // too many digits: the last five
            DigitDial r = DigitDial.Room();
            r.Load("123 456"); Equal("123456", r.Digits); Equal("123 456", r.Render(false, null));
            r.Load("AB12"); Equal("120000", r.Digits);              // a legacy letter code keeps only its digits
        });
        Run("dial: render marks only the focused wheel", delegate {
            DigitDial r = DigitDial.Room(); r.Load("123456"); r.Right(); r.Right(); r.Right();
            Equal("123 [4]56", r.Render(true, delegate(string c) { return "[" + c + "]"; }));
            Equal("123 456", r.Render(false, delegate(string c) { return "[" + c + "]"; }));
        });

        return s_failed == 0 ? 0 : 1;
    }
}
