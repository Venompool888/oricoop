using System;
using System.Collections.Generic;
using System.IO;
using OriCoop;
using OriCoop.Ui;

// Menu redesign (issue 「[菜单重构] 主菜单与联机引导」): the title's 多人游玩 flow, intent first (创建房间 / 加入房间), then the
// connection method (Steam / room code / LAN). Ui/CoopMenuFlow.cs holds the rows, what a press does, where Back goes and
// where the cursor starts; TitleMenu.cs only turns its Steps into page changes.
static class CoopMenuFlowTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(what + ": expected '" + expected + "', got '" + actual + "'");
    }

    static readonly Func<string, bool> Valid = delegate (string a) { return a != null && a.Contains(":") && !a.Contains(" "); };
    const CoopMenuFlow.ServerPick Have = CoopMenuFlow.ServerPick.Selected, None = CoopMenuFlow.ServerPick.None;

    static string Labels(int page)
    {
        var l = new List<string>();
        foreach (CoopMenuFlow.RowKeys r in CoopMenuFlow.Rows(page)) l.Add(L10n.T(r.Label, true));
        return string.Join(" / ", l.ToArray());
    }

    // The glyph table baked into the mod (src/assets/zh-glyphs/zh_glyphs.txt), found from the test's folder; null if absent.
    static HashSet<int> BakedGlyphs()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
        {
            string dir = start;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                foreach (string rel in new[] { Path.Combine("assets", Path.Combine("zh-glyphs", "zh_glyphs.txt")), Path.Combine("src", Path.Combine("assets", Path.Combine("zh-glyphs", "zh_glyphs.txt"))) })
                {
                    string f = Path.Combine(dir, rel);
                    if (!File.Exists(f)) continue;
                    var ids = new HashSet<int>();
                    foreach (string line in File.ReadAllLines(f, System.Text.Encoding.UTF8))
                    {
                        if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                        int id; if (int.TryParse(line.Split(' ')[0], out id)) ids.Add(id);
                    }
                    return ids;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
        }
        return null;
    }

    public static void Register(Action<string, Action> run)
    {
        run("co-op menu: 多人游玩 lists 本地同屏 / 创建房间 / 加入房间 / 自建服务器, intent first", delegate {
            Equal("本地同屏 / 创建房间 / 加入房间 / 自建服务器", Labels(CoopMenuFlow.PageCoop), "多人游玩 rows");
            Equal(CoopMenuFlow.Act.Local, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowLocal, None).Act, "本地同屏");
            Equal(CoopMenuFlow.Act.OpenHostMethods, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowHost, None).Act, "创建房间");
            Equal(CoopMenuFlow.Act.OpenJoinMethods, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowJoin, None).Act, "加入房间");
            Equal(CoopMenuFlow.Act.OpenServerMode, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowServer, None).Act, "自建服务器");
            Equal(CoopMenuFlow.Act.None, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, 4, None).Act, "a row past the end");
            Equal(CoopMenuFlow.Act.None, CoopMenuFlow.Press(CoopMenuFlow.PageCoop, -1, None).Act, "no row");
        });

        run("co-op menu: both method pages list Steam, room code, LAN in the same order", delegate {
            Equal("Steam 好友 / 房间码（自建服务器） / 局域网（IP 地址）", Labels(CoopMenuFlow.PageHostMethod), "创建房间 rows");
            Equal("Steam 邀请 / 房间码（自建服务器） / 局域网（输入 IP）", Labels(CoopMenuFlow.PageJoinMethod), "加入房间 rows");
            Equal(3, CoopMenuFlow.Methods.Length, "methods");
            for (int i = 0; i < CoopMenuFlow.Methods.Length; i++)
            {
                Equal(i, CoopMenuFlow.RowOf(CoopMenuFlow.Methods[i]), "RowOf " + CoopMenuFlow.Methods[i]);
                Equal(CoopMenuFlow.Methods[i], CoopMenuFlow.MethodAt(i), "MethodAt " + i);
            }
            Equal("steam", CoopMenuFlow.MethodAt(0), "Steam first: nothing to set up");
            Equal("", CoopMenuFlow.MethodAt(3), "no fourth method");
        });

        run("co-op menu: every page fits the 4-row orb", delegate {
            foreach (int page in new[] { CoopMenuFlow.PageCoop, CoopMenuFlow.PageHostMethod, CoopMenuFlow.PageJoinMethod })
                Check(CoopMenuFlow.Rows(page).Length <= 4, "page " + page + " has " + CoopMenuFlow.Rows(page).Length + " rows (NativeModeMenu.MaxItems is 4)");
        });

        run("co-op menu: 创建房间 hosts with the pressed method (to the save slots)", delegate {
            foreach (string m in CoopMenuFlow.Methods)
            {
                CoopMenuFlow.Step s = CoopMenuFlow.Press(CoopMenuFlow.PageHostMethod, CoopMenuFlow.RowOf(m), Have);
                Equal(CoopMenuFlow.Act.Host, s.Act, "host " + m);
                Equal(m, s.Method, "host method");
            }
        });

        run("co-op menu: 加入房间 opens the Steam wait page or the code / IP entry page, never a save slot", delegate {
            CoopMenuFlow.Step steam = CoopMenuFlow.Press(CoopMenuFlow.PageJoinMethod, CoopMenuFlow.RowOf("steam"), Have);
            Equal(CoopMenuFlow.Act.JoinSteam, steam.Act, "Steam invite");
            CoopMenuFlow.Step code = CoopMenuFlow.Press(CoopMenuFlow.PageJoinMethod, CoopMenuFlow.RowOf("cserver"), Have);
            Equal(CoopMenuFlow.Act.JoinEntry, code.Act, "room code"); Equal("cserver", code.Method, "room code method");
            CoopMenuFlow.Step lan = CoopMenuFlow.Press(CoopMenuFlow.PageJoinMethod, CoopMenuFlow.RowOf("direct"), None);
            Equal(CoopMenuFlow.Act.JoinEntry, lan.Act, "LAN needs no server"); Equal("direct", lan.Method, "LAN method");
        });

        run("co-op menu: a room code with no server sends the player to add one (host and join)", delegate {
            foreach (int page in new[] { CoopMenuFlow.PageHostMethod, CoopMenuFlow.PageJoinMethod })
            {
                Equal(CoopMenuFlow.Act.NeedServer, CoopMenuFlow.Press(page, CoopMenuFlow.RowOf("cserver"), None).Act, "page " + page);
                Equal("title.code.noserver.desc", CoopMenuFlow.DescKey(page, CoopMenuFlow.RowOf("cserver"), false), "the row says so before the press");
                Check(CoopMenuFlow.DescKey(page, CoopMenuFlow.RowOf("cserver"), true) != "title.code.noserver.desc", "with a server the row describes the room code");
                Check(CoopMenuFlow.DescKey(page, CoopMenuFlow.RowOf("steam"), false) != "title.code.noserver.desc", "Steam never needs a server");
                Check(CoopMenuFlow.DescKey(page, CoopMenuFlow.RowOf("direct"), false) != "title.code.noserver.desc", "LAN never needs a server");
            }
            Equal("title.coop.server.desc", CoopMenuFlow.DescKey(CoopMenuFlow.PageCoop, CoopMenuFlow.RowServer, false), "自建服务器 row on 多人游玩");
            Equal("", CoopMenuFlow.DescKey(CoopMenuFlow.PageHostMethod, 7, true), "no such row");
        });

        run("co-op menu: Back walks the same way back, cursor on the row that was pressed", delegate {
            CoopMenuFlow.Step b = CoopMenuFlow.Back(CoopMenuFlow.PageHostMethod);
            Equal(CoopMenuFlow.Act.ToPage, b.Act, "创建房间 back"); Equal(CoopMenuFlow.PageCoop, b.Page, "to 多人游玩"); Equal(CoopMenuFlow.RowHost, b.Focus, "on 创建房间");
            b = CoopMenuFlow.Back(CoopMenuFlow.PageJoinMethod);
            Equal(CoopMenuFlow.PageCoop, b.Page, "to 多人游玩"); Equal(CoopMenuFlow.RowJoin, b.Focus, "on 加入房间");
            Equal(CoopMenuFlow.Act.CloseOrb, CoopMenuFlow.Back(CoopMenuFlow.PageCoop).Act, "多人游玩 back: the title items");
            foreach (string m in CoopMenuFlow.Methods)
            {
                CoopMenuFlow.Step j = CoopMenuFlow.BackToMethods(m);
                Equal(CoopMenuFlow.PageJoinMethod, j.Page, "join page back (" + m + ")");
                Equal(CoopMenuFlow.RowOf(m), j.Focus, "cursor on " + m);
            }
        });

        run("co-op menu: the cursor starts on the method used last, Steam on a first run", delegate {
            Equal(0, CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageHostMethod, "", ""), "first run host");
            Equal(0, CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageJoinMethod, null, null), "first run join");
            Equal(CoopMenuFlow.RowOf("direct"), CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageHostMethod, "direct", "cserver"), "host remembers LAN");
            Equal(CoopMenuFlow.RowOf("cserver"), CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageJoinMethod, "direct", "cserver"), "join remembers the room code on its own");
            Equal(CoopMenuFlow.RowOf("direct"), CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageJoinMethod, "", " Direct "), "case and spaces from a hand-edited config");
            Equal(0, CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageHostMethod, "relay", ""), "a removed method (the relay, T-159) starts on Steam");
            Equal(0, CoopMenuFlow.DefaultFocus(CoopMenuFlow.PageCoop, "direct", "direct"), "多人游玩 starts on its first row");
        });

        run("co-op menu: the room-code server is the selected one, else the first saved one, else none", delegate {
            string a;
            Equal(CoopMenuFlow.ServerPick.Selected, CoopMenuFlow.ResolveServer(new List<string> { "a:1", "b:2" }, "b:2", Valid, out a), "selected"); Equal("b:2", a, "address");
            Equal(CoopMenuFlow.ServerPick.Selected, CoopMenuFlow.ResolveServer(new List<string>(), " env:47022 ", Valid, out a), "a test run's server (not saved)"); Equal("env:47022", a, "trimmed");
            Equal(CoopMenuFlow.ServerPick.First, CoopMenuFlow.ResolveServer(new List<string> { "bad entry", "a:1", "b:2" }, "", Valid, out a), "none selected"); Equal("a:1", a, "first valid");
            Equal(CoopMenuFlow.ServerPick.First, CoopMenuFlow.ResolveServer(new List<string> { "a:1" }, "not valid", Valid, out a), "selected is invalid"); Equal("a:1", a, "first");
            Equal(CoopMenuFlow.ServerPick.None, CoopMenuFlow.ResolveServer(new List<string>(), "", Valid, out a), "nothing saved"); Equal("", a, "no address");
            Equal(CoopMenuFlow.ServerPick.None, CoopMenuFlow.ResolveServer(null, null, Valid, out a), "null list");
            Equal(CoopMenuFlow.ServerPick.None, CoopMenuFlow.ResolveServer(new List<string> { "", "  ", "x y" }, "", Valid, out a), "only invalid entries");
        });

        run("co-op menu: room code goals take fewer presses than before the redesign", delegate {
            // From 多人游玩 to the room-code dial: 加入房间 -> 房间码 (was 自建服务器 -> 服务器管理 -> 服务器列表 -> a server -> 加入房间).
            CoopMenuFlow.Step s = CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowJoin, Have);
            Equal(CoopMenuFlow.Act.OpenJoinMethods, s.Act, "press 1");
            s = CoopMenuFlow.Press(CoopMenuFlow.PageJoinMethod, CoopMenuFlow.RowOf("cserver"), Have);
            Equal(CoopMenuFlow.Act.JoinEntry, s.Act, "press 2 is the code dial");
            // Hosting on the own server: 创建房间 -> 房间码 -> save slots (was 5 presses before the slots).
            s = CoopMenuFlow.Press(CoopMenuFlow.PageCoop, CoopMenuFlow.RowHost, Have);
            s = CoopMenuFlow.Press(CoopMenuFlow.PageHostMethod, CoopMenuFlow.RowOf("cserver"), Have);
            Equal(CoopMenuFlow.Act.Host, s.Act, "press 2 hosts");
        });

        run("co-op menu: every text exists in both languages, the server shows in the room-code rows", delegate {
            foreach (string key in CoopMenuFlow.AllKeys())
            {
                Check(L10n.Has(key), key + " missing");
                foreach (bool zh in new[] { true, false }) Check(L10n.T(key, zh).Length > 0 && L10n.T(key, zh) != key, key + " empty " + (zh ? "zh" : "en"));
            }
            foreach (int page in new[] { CoopMenuFlow.PageHostMethod, CoopMenuFlow.PageJoinMethod })
                foreach (bool zh in new[] { true, false })
                {
                    string text = string.Format(L10n.T(CoopMenuFlow.DescKey(page, CoopMenuFlow.RowOf("cserver"), true), zh), "192.0.2.5:47022");
                    Check(text.Contains("192.0.2.5:47022"), "page " + page + " room-code row names the server: " + text);
                }
            Check(L10n.T("title.local", true) == "本地同屏", "local play keeps its name (T-154)");
            // The room-code dial joins a running game (C-server room code); a 我的房间 invite code is another code that needs
            // the room password, so the join row says where that one goes.
            Check(L10n.T("title.join.code.desc", true).Contains("我的房间") && L10n.T("title.join.code.desc", false).Contains("My rooms"), "join room-code row points 我的房间 invite codes to 我的房间");
        });

        run("co-op menu: every Chinese character of the flow is in the baked glyph atlas", delegate {
            HashSet<int> baked = BakedGlyphs();
            if (baked == null) { Console.WriteLine("  (zh_glyphs.txt not found from here; skipped)"); return; }
            string missing = "";
            foreach (string key in CoopMenuFlow.AllKeys())
                foreach (char c in L10n.T(key, true))
                    if (c > '~' && !baked.Contains(c) && missing.IndexOf(c) < 0) missing += c;
            Check(missing.Length == 0, "not in zh_glyphs.txt (the row would fall back to English): " + missing);
        });
    }
}
