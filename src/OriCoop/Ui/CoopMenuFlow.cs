using System;
using System.Collections.Generic;

namespace OriCoop.Ui
{
    // The title's 多人游玩 menu as intent first, connection method second (menu redesign, issue 「[菜单重构] 主菜单与联机引导」):
    //   多人游玩  : 本地同屏 / 创建房间 / 加入房间 / 自建服务器
    //   创建房间  : Steam 好友 / 房间码（自建服务器）/ 局域网（IP 地址）  -> save slots -> the host page of that method
    //   加入房间  : Steam 邀请 / 房间码 / 局域网 IP                       -> the join page of that method (no save slot)
    //   自建服务器: the old server-mode page (上次游玩 / 我的房间 / 服务器管理), unchanged.
    // The old transport-first pages (局域网 / Steam -> 开房 / 加入) stay in the code: the fallback "how do you want to play?" orb
    // after a save slot still uses them, and so does the 自建服务器 -> 服务器列表 path.
    // No Unity types: the rows, what a press does, where Back goes and the default focus are unit-tested without the game
    // (src/Tests/Ui). TitleMenu.cs turns the Steps into page changes and NetPlay calls.
    public static class CoopMenuFlow
    {
        // Pages of this flow (TitleMenu maps them to the orb's page numbers).
        public const int PageCoop = 0, PageHostMethod = 1, PageJoinMethod = 2;

        // Rows of the 多人游玩 page.
        public const int RowLocal = 0, RowHost = 1, RowJoin = 2, RowServer = 3;

        // The connection methods, in the order both method pages list them: the one with nothing to set up first.
        public const string Steam = "steam", Code = "cserver", Lan = "direct";
        public static readonly string[] Methods = { Steam, Code, Lan };

        public struct RowKeys
        {
            public string Label, Desc;
            public RowKeys(string label, string desc) { Label = label; Desc = desc; }
        }

        static readonly RowKeys[] s_coop = {
            new RowKeys("title.local", "title.local.desc"),
            new RowKeys("title.coop.host", "title.coop.host.desc"),
            new RowKeys("title.coop.join", "title.coop.join.desc"),
            new RowKeys("server.title", "title.coop.server.desc"),
        };
        static readonly RowKeys[] s_host = {
            new RowKeys("title.method.steam", "title.host.steam.desc"),
            new RowKeys("title.method.code", "title.host.code.desc"),
            new RowKeys("title.method.lan", "title.host.lan.desc"),
        };
        static readonly RowKeys[] s_join = {
            new RowKeys("title.method.steam.join", "title.join.steam.desc"),
            new RowKeys("title.method.code", "title.join.code.desc"),
            new RowKeys("title.method.lan.join", "title.join.lan.desc"),
        };

        // The rows of a page. A room-code row's description takes the server as {0}; with no usable server it is the
        // "add a server first" text instead (see DescKey).
        public static RowKeys[] Rows(int page)
        {
            RowKeys[] src = page == PageHostMethod ? s_host : page == PageJoinMethod ? s_join : s_coop;
            return (RowKeys[])src.Clone();
        }

        // Every key the flow can show (font coverage check, l10n tests).
        public static IEnumerable<string> AllKeys()
        {
            foreach (RowKeys[] page in new[] { s_coop, s_host, s_join })
                foreach (RowKeys r in page) { yield return r.Label; yield return r.Desc; }
            yield return "title.code.noserver.desc";
            yield return "title.code.noserver";
        }

        // The description key of a row, given whether a server is set up for room codes.
        public static string DescKey(int page, int row, bool haveServer)
        {
            RowKeys[] rows = Rows(page);
            if (row < 0 || row >= rows.Length) return "";
            if (page != PageCoop && MethodAt(row) == Code && !haveServer) return "title.code.noserver.desc";
            return rows[row].Desc;
        }

        public static string MethodAt(int row) { return row >= 0 && row < Methods.Length ? Methods[row] : ""; }
        public static int RowOf(string method) { int i = Array.IndexOf(Methods, Normalize(method)); return i < 0 ? 0 : i; }

        // A remembered method ("" or anything unknown = Steam, the default for a first run).
        public static string Normalize(string method)
        {
            string m = (method ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Methods, m) >= 0 ? m : Steam;
        }

        public enum Act
        {
            None,
            Local,            // 本地同屏: to the save slots, local co-op
            OpenHostMethods,  // to the 创建房间 page
            OpenJoinMethods,  // to the 加入房间 page
            OpenServerMode,   // to the old 自建服务器 page
            Host,             // host with Method: to the save slots, then the host page
            JoinSteam,        // the Steam join page (waits for an invite)
            JoinEntry,        // the room-code or IP entry page of Method
            NeedServer,       // room code without a usable server: to 服务器管理 with a note
            CloseOrb,         // Back on 多人游玩: the title items again
            ToPage,           // Back inside the flow: Page with the cursor on Focus
        }

        public struct Step
        {
            public Act Act;
            public string Method;
            public int Page, Focus;
            public override string ToString() { return Act + (Method != null && Method.Length > 0 ? ":" + Method : "") + (Act == Act.ToPage ? " " + Page + "@" + Focus : ""); }
        }

        static Step S(Act a) { Step s = new Step(); s.Act = a; s.Method = ""; return s; }
        static Step S(Act a, string method) { Step s = S(a); s.Method = method; return s; }
        static Step To(int page, int focus) { Step s = S(Act.ToPage); s.Page = page; s.Focus = focus; return s; }

        // A press on a row. server = what ResolveServer said for the room-code rows.
        public static Step Press(int page, int row, ServerPick server)
        {
            if (page == PageCoop)
            {
                if (row == RowLocal) return S(Act.Local);
                if (row == RowHost) return S(Act.OpenHostMethods);
                if (row == RowJoin) return S(Act.OpenJoinMethods);
                if (row == RowServer) return S(Act.OpenServerMode);
                return S(Act.None);
            }
            string method = MethodAt(row);
            if (method.Length == 0) return S(Act.None);
            if (method == Code && server == ServerPick.None) return S(Act.NeedServer, Code);
            if (page == PageHostMethod) return S(Act.Host, method);
            if (page == PageJoinMethod) return method == Steam ? S(Act.JoinSteam, Steam) : S(Act.JoinEntry, method);
            return S(Act.None);
        }

        // Back on a page of the flow: the page it came from, the cursor on the row that opened it.
        public static Step Back(int page)
        {
            if (page == PageHostMethod) return To(PageCoop, RowHost);
            if (page == PageJoinMethod) return To(PageCoop, RowJoin);
            return S(Act.CloseOrb);
        }

        // Back from a page the join methods opened (the entry page, the Steam join page, a failed join): the join methods,
        // the cursor on that method.
        public static Step BackToMethods(string method) { return To(PageJoinMethod, RowOf(method)); }

        // Where the cursor starts on a method page: the method used last time (Steam on a first run).
        public static int DefaultFocus(int page, string lastHost, string lastJoin)
        {
            if (page == PageHostMethod) return RowOf(lastHost);
            if (page == PageJoinMethod) return RowOf(lastJoin);
            return 0;
        }

        // ---- the server for a room code ------------------------------------------------------------------------

        public enum ServerPick { None, Selected, First }

        // Which C-server a room-code press uses: the selected one when it is a valid address (as the old 创建房间 / 加入房间
        // page checked it; a test run may select one with ORICOOP_NET_CSERVER that is not in the saved list), else the first
        // valid saved one (the press then selects it), else none (the player is sent to add one).
        public static ServerPick ResolveServer(IList<string> servers, string selected, Func<string, bool> valid, out string address)
        {
            address = "";
            string sel = (selected ?? "").Trim();
            if (sel.Length > 0 && valid(sel)) { address = sel; return ServerPick.Selected; }
            if (servers != null)
                foreach (string s in servers)
                {
                    string t = (s ?? "").Trim();
                    if (t.Length > 0 && valid(t)) { address = t; return ServerPick.First; }
                }
            return ServerPick.None;
        }
    }
}
