using System;
using System.Collections.Generic;
using OriCoop.Rooms;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // T-173: 我的房间 -> 加入房间 / 房间列表 pages (designs: docs/design/room-page/codex/myrooms-2-join.png, myrooms-3-list.png),
    // drawn with NativeGui over the title like the room page; the native title orb is hidden while one is up. Ticked and drawn
    // from OnlineRoomScreen.Tick / Draw (no Plugin hook of their own). The 我的房间 menu itself stays a native orb page
    // (NativeRooms.cs); this file only adds its bottom key bar and header.
    internal static class MyRoomsPages
    {
        public enum Page { None, Join, List }
        static Page s_page = Page.None;
        static float s_ignoreUntil;
        static readonly MyRoomsJoinModel s_join = new MyRoomsJoinModel();
        static readonly MyRoomsListModel s_list = new MyRoomsListModel(delegate (string k) { return L10n.T(k); });
        static Action s_back;
        static bool s_editingCode;
        static bool s_requestRunning;   // a join / enter started here has not called back yet
        static bool s_deleteRunning;    // a 删除 started here has not called back yet (Esc does not cancel it)
        static int s_showCount;         // bumped by every Show: a reply for an earlier page leaves the page alone
        static string s_note = "";
        static float s_noteUntil;
        static readonly List<Rect> s_keyRects = new List<Rect>();
        static readonly List<Rect> s_rowRects = new List<Rect>();
        static Rect s_serverRect, s_codeRect, s_joinRect;
        internal static string LastAction = "";
        // One press = one action, whether it comes as a key (every frame) or a game button (every fixed step): Ui/PressGate.cs.
        static readonly PressGate s_gate = new PressGate();
        const int GateBack = 1, GateConfirm = 2, GateUp = 3, GateDown = 4, GateTab = 5, GateDelete = 6;

        public static bool IsOpen { get { return s_page != Page.None; } }
        public static Page Current { get { return s_page; } }
        internal static MyRoomsJoinModel JoinModel { get { return s_join; } }
        internal static MyRoomsListModel ListModel { get { return s_list; } }

        // back: what Esc on the page does (reopen the 我的房间 menu).
        public static void Show(Page page, Action back)
        {
            s_page = page; s_back = back; s_editingCode = false; s_note = "";
            s_showCount++;
            s_ignoreUntil = Time.unscaledTime + MyRoomsPageFlow.OpenIgnore;
            s_gate.Block(Time.unscaledTime, MyRoomsPageFlow.OpenIgnore);   // the press that opened the page is still down
            if (page == Page.Join)
            {
                s_join.Servers.Clear();
                s_join.Servers.AddRange(OriCoop.Authority.CServerConfig.Servers);
                s_join.Server = Math.Max(0, s_join.Servers.IndexOf(OriCoop.Authority.CServerConfig.Address ?? ""));
                s_join.Focus = s_join.Servers.Count > 0 ? MyRoomsJoinModel.RowCode : MyRoomsJoinModel.RowServer;
                s_join.DropdownOpen = false;
            }
            else ReloadList();
            NativeModeMenu.RoomPanelVisible(true);
            Plugin.Log.LogInfo("my rooms: " + page + " page");
        }

        public static void Close()
        {
            if (s_page == Page.None) return;
            s_page = Page.None;
            s_editingCode = false;
            NativeGui.BeginPage("myrooms"); NativeGui.EndPage();
            // The native 我的房间 menu comes back locked for a moment: the Esc / B that closed the page is still down and must not
            // also back out of 我的房间.
            NativeModeMenu.RoomPanelShowLocked(MyRoomsPageFlow.NativeUnlockDelay);
        }

        static void ReloadList()
        {
            s_list.Rooms.Clear();
            try { s_list.Rooms.AddRange(MyRooms.ListRooms()); }
            catch (Exception e) { Plugin.Log.LogWarning("my rooms: list: " + e.Message); }
            s_list.Clamp();
        }

        static void Note(string text) { s_note = text ?? ""; s_noteUntil = Time.unscaledTime + 4f; }

        static void Done(string error)
        {
            s_requestRunning = false;
            if (error == null) { Close(); return; }   // the room flow takes over (host page / join page / room page)
            Note(error);
        }

        // ---- input ---------------------------------------------------------------------------------------

        public static void Tick()
        {
            if (s_page == Page.None) return;
            float now = Time.unscaledTime;
            // Every input goes through the gate every frame (also while it is blocked or the code is being typed), so a press
            // is seen from its start and fires once (Ui/PressGate.cs).
            bool back = s_gate.Edge(GateBack, Input.GetKey(KeyCode.Escape), Core.Input.Cancel.Pressed, now);
            bool confirm = s_gate.Edge(GateConfirm, Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter),
                Core.Input.ActionButtonA.Pressed, now);
            bool up = s_gate.Edge(GateUp, Input.GetKey(KeyCode.UpArrow), Core.Input.MenuUp.Pressed, now);
            bool down = s_gate.Edge(GateDown, Input.GetKey(KeyCode.DownArrow), Core.Input.MenuDown.Pressed, now);
            bool tab = s_gate.Edge(GateTab, Input.GetKey(KeyCode.Tab), now);
            bool delete = s_gate.Edge(GateDelete, Input.GetKey(KeyCode.Delete), Core.Input.Delete.Pressed, now);
            if (now < s_ignoreUntil) return;
            if (SafeBusy() || s_requestRunning) { if (back) Back(); return; }   // Back cancels the running request first
            if (s_page == Page.Join) TickJoin(back, confirm, up, down, tab, now);
            else TickList(back, confirm, up, down, delete);
        }

        static bool SafeBusy() { try { return MyRooms.Busy; } catch (Exception) { return false; } }

        static void TickJoin(bool back, bool confirm, bool up, bool down, bool tab, float now)
        {
            if (s_editingCode)
            {
                // Typing the code: digits and letters, Backspace, Enter / Esc to finish.
                foreach (char c in Input.inputString)
                {
                    if (c == '\b') { if (s_join.Code.Length > 0) s_join.SetCode(s_join.Code.Substring(0, s_join.Code.Length - 1)); }
                    else if (c == '\n' || c == '\r') { EndEdit(now); return; }
                    else s_join.SetCode(s_join.Code + c);
                }
                if (back) EndEdit(now);
                return;
            }
            if (tab) s_join.NextServer();
            else if (up) s_join.Up();
            else if (down) s_join.Down();
            else if (back) { if (s_join.Back() == MyRoomsJoinAction.Back) Back(); }
            else if (confirm) Act(s_join.Press());
            else if (s_join.Focus == MyRoomsJoinModel.RowCode && !s_join.DropdownOpen && HasCodeChar(Input.inputString))
            {
                // Typing on the focused code row starts editing with that character.
                s_editingCode = true; s_join.SetCode(""); foreach (char c in Input.inputString) if (c != '\b') s_join.SetCode(s_join.Code + c);
            }
        }

        static bool HasCodeChar(string typed)
        {
            foreach (char c in typed) if (char.IsLetterOrDigit(c)) return true;
            return false;
        }

        // Enter / Esc ended the typing: that press (and anything else down now) must not also act on the page.
        static void EndEdit(float now)
        {
            s_editingCode = false;
            s_ignoreUntil = now + MyRoomsPageFlow.EditEndIgnore;
            s_gate.Block(now, MyRoomsPageFlow.EditEndIgnore);
        }

        static void Act(MyRoomsJoinAction a)
        {
            if (a == MyRoomsJoinAction.EditCode) { s_editingCode = true; s_join.SetCode(""); }
            else if (a == MyRoomsJoinAction.Join)
            {
                LastAction = "join " + s_join.ServerText;
                s_requestRunning = true;
                try { MyRooms.JoinByCode(s_join.ServerText, s_join.Code, Done); }
                catch (Exception e) { s_requestRunning = false; Note(e.Message); }
            }
            else if (a == MyRoomsJoinAction.Back) Back();
        }

        static void TickList(bool back, bool confirm, bool up, bool down, bool delete)
        {
            MyRoomsListAction a = MyRoomsListAction.None;
            if (up) s_list.Up();
            else if (down) s_list.Down();
            else if (back) a = s_list.Back();
            else if (confirm) a = s_list.Enter();
            else if (delete) a = s_list.Delete();   // one DEL press = one of the two presses 删除 needs
            ListAct(a);
        }

        static void ListAct(MyRoomsListAction a)
        {
            MyRoomsListEntry room = s_list.Current;
            if (a == MyRoomsListAction.Back) { Back(); return; }
            if (room == null) return;
            if (a == MyRoomsListAction.Enter)
            {
                LastAction = "enter " + room.Id;
                s_requestRunning = true;
                try { MyRooms.EnterRoom(room.Id, Done); } catch (Exception e) { s_requestRunning = false; Note(e.Message); }
            }
            else if (a == MyRoomsListAction.Delete)
            {
                LastAction = "delete " + room.Id;
                int shown = s_showCount;
                s_deleteRunning = true;
                try
                {
                    MyRooms.DeleteRoom(room.Id, delegate (string error)
                    {
                        s_deleteRunning = false;
                        // The page was closed (Esc) while the delete ran: the room service finished it anyway and updated its
                        // own list; nothing to show here, and Back must not run a second time.
                        if (s_page != Page.List) return;
                        if (shown != s_showCount) { ReloadList(); return; }   // the list was reopened meanwhile: just refresh it
                        if (error != null) { Note(error); return; }
                        ReloadList();
                        if (s_list.Rooms.Count == 0) Back();   // no rooms left: back to the menu (which then hides 房间列表)
                    });
                }
                catch (Exception e) { s_deleteRunning = false; Note(e.Message); }
            }
        }

        static void Back()
        {
            // A create / join / enter still running is abandoned first: its reply must not take the player into the room
            // they just backed out of (MyRooms.Cancel: no done callback, no auto-enter). A running 删除 is not: it may
            // already have happened on the server, so it finishes and updates the room list on its own.
            if (MyRoomsPageFlow.CancelOnBack(SafeBusy(), s_requestRunning, s_deleteRunning))
            {
                s_requestRunning = false;
                try { MyRooms.Cancel(); } catch (Exception e) { Plugin.Log.LogWarning("my rooms: cancel: " + e.Message); }
                LastAction = "cancel";
            }
            Action back = s_back;
            Close();
            if (back != null) back();
        }

        // ---- drawing -------------------------------------------------------------------------------------

        public static void Draw()
        {
            if (s_page == Page.None) return;
            NativeGui.BeginPage("myrooms");
            try
            {
                GUI.depth = -250;
                if (s_page == Page.Join) DrawJoin(); else DrawList();
                string status = SafeBusy() ? L10n.T("rooms.connecting") : Time.unscaledTime < s_noteUntil ? s_note : SafeMessage();
                NativeGui.Label("myrooms.status", new Rect(0, Screen.height * 0.79f, Screen.width, Screen.height * 0.05f), status,
                    Screen.height * 0.03f, TextAnchor.MiddleCenter, RoomUiDraw.Gold);
                Mouse();
            }
            finally { NativeGui.EndPage(); }
        }

        static string SafeMessage() { try { return MyRooms.Message; } catch (Exception) { return ""; } }

        static void DrawJoin()
        {
            float w = Screen.width, h = Screen.height;
            RoomUiDraw.Title("myrooms.join.title", L10n.T("myrooms.join"));
            float labelX = w * 0.2f, fieldX = w * 0.34f, fieldW = w * 0.42f, rowH = h * 0.075f, font = h * 0.034f;
            // 服务器
            float y = h * 0.3f;
            NativeGui.Label("myrooms.join.server.l", new Rect(labelX, y, fieldX - labelX - w * 0.02f, rowH), L10n.T("myrooms.server"), font * 1.1f, TextAnchor.MiddleRight, RoomUiDraw.Text);
            s_serverRect = new Rect(fieldX, y, fieldW, rowH);
            RoomUiDraw.Frame("myrooms.join.server", s_serverRect, s_join.Focus == MyRoomsJoinModel.RowServer && !s_join.DropdownOpen);
            string server = s_join.Servers.Count == 0 ? L10n.T("myrooms.no_server") : s_join.ServerText;
            NativeGui.Label("myrooms.join.server.v", new Rect(fieldX + fieldW * 0.05f, y, fieldW * 0.82f, rowH), server, font, TextAnchor.MiddleLeft,
                s_join.Servers.Count == 0 ? RoomUiDraw.Dim : RoomUiDraw.Text);
            if (s_join.HasDropdown) RoomUiDraw.Arrow("myrooms.join.arrow", new Rect(fieldX + fieldW * 0.9f, y + rowH * 0.25f, rowH * 0.5f, rowH * 0.5f), RoomUiDraw.Text);
            // the dropdown (服务器 1 · ip:port ...)
            s_rowRects.Clear();
            float codeY = h * 0.48f;
            if (s_join.DropdownOpen)
            {
                // Five rows at a time; the window scrolls with the dropdown focus (MyRoomsJoinModel.DropdownFirst).
                int first = s_join.DropdownFirst;
                for (int slot = 0; slot < MyRoomsJoinModel.DropdownRows && first + slot < s_join.Servers.Count; slot++)
                {
                    int i = first + slot;
                    Rect r = new Rect(fieldX + fieldW * 0.03f, y + rowH * (1.15f + slot * 0.85f), fieldW * 0.94f, rowH * 0.75f);
                    s_rowRects.Add(r);
                    RoomUiDraw.Band("myrooms.join.dd." + slot, r, i == s_join.DropdownFocus);
                    NativeGui.Label("myrooms.join.dd." + slot + ".t", new Rect(r.x + r.width * 0.06f, r.y, r.width * 0.9f, r.height),
                        L10n.F("myrooms.server_n", i + 1, s_join.Servers[i]), font * 0.9f, TextAnchor.MiddleLeft, RoomUiDraw.Text);
                }
                codeY = y + rowH * (1.3f + Math.Min(MyRoomsJoinModel.DropdownRows, s_join.Servers.Count) * 0.85f);
            }
            // 邀请码
            NativeGui.Label("myrooms.join.code.l", new Rect(labelX, codeY, fieldX - labelX - w * 0.02f, rowH), L10n.T("myrooms.code"), font * 1.1f, TextAnchor.MiddleRight, RoomUiDraw.Text);
            s_codeRect = new Rect(fieldX, codeY, fieldW, rowH);
            RoomUiDraw.Frame("myrooms.join.code", s_codeRect, s_join.Focus == MyRoomsJoinModel.RowCode && !s_join.DropdownOpen);
            string code = s_join.Code + (s_editingCode && (int)(Time.unscaledTime * 2f) % 2 == 0 ? "_" : "");
            NativeGui.Label("myrooms.join.code.v", new Rect(fieldX + fieldW * 0.05f, codeY, fieldW * 0.9f, rowH), code, font * 1.05f, TextAnchor.MiddleLeft, RoomUiDraw.Text);
            // 加入
            s_joinRect = new Rect(w * 0.4f, codeY + rowH * 1.5f, w * 0.2f, rowH);
            bool joinFocused = s_join.Focus == MyRoomsJoinModel.RowJoin && !s_join.DropdownOpen;
            if (joinFocused) RoomUiDraw.Band("myrooms.join.go.sel", s_joinRect, true);
            NativeGui.Label("myrooms.join.go", s_joinRect, L10n.T("myrooms.join_go"), font * 1.4f, TextAnchor.MiddleCenter,
                s_join.CanJoin ? (joinFocused ? RoomUiDraw.Cyan : RoomUiDraw.Text) : RoomUiDraw.Dim);
            s_keyRects.Clear();
            s_keyRects.AddRange(RoomUiDraw.KeyBar("myrooms.keys", new[] {
                Key(KeyCode.Space, KeyCode.JoystickButton0, "myrooms.key.confirm"), Key(KeyCode.Tab, KeyCode.None, "myrooms.key.switch", s_join.Servers.Count > 1),
                Key(KeyCode.Escape, KeyCode.JoystickButton1, "myrooms.key.back") }));
        }

        static void DrawList()
        {
            float w = Screen.width, h = Screen.height;
            RoomUiDraw.Title("myrooms.list.title", L10n.T("myrooms.list"));
            s_rowRects.Clear();
            float left = w * 0.22f, width = w * 0.56f, rowH = h * 0.075f, gap = h * 0.093f, top = h * 0.35f, font = h * 0.036f;
            int first = Math.Max(0, Math.Min(s_list.Focus - 4, s_list.Rooms.Count - 5));   // five rows visible, follow the focus
            for (int slot = 0; slot < 5; slot++)
            {
                int i = first + slot;
                if (i >= s_list.Rooms.Count) break;
                MyRoomsListRow row = s_list.Row(i);
                Rect r = new Rect(left, top + slot * gap, width, rowH);
                s_rowRects.Add(r);
                RoomUiDraw.Band("myrooms.list.row." + slot, r, row.Selected);
                Color c = row.Full && !row.Selected ? RoomUiDraw.Dim : RoomUiDraw.Text;
                NativeGui.Label("myrooms.list.row." + slot + ".n", new Rect(r.x + width * 0.06f, r.y, width * 0.42f, rowH), row.Name, font, TextAnchor.MiddleLeft, c);
                NativeGui.Label("myrooms.list.row." + slot + ".id", new Rect(r.x + width * 0.5f, r.y, width * 0.26f, rowH), row.Number, font * 0.85f, TextAnchor.MiddleLeft, c);
                NativeGui.Label("myrooms.list.row." + slot + ".c", new Rect(r.x + width * 0.76f, r.y, width * 0.2f, rowH),
                    row.ConfirmDelete ? L10n.T("myrooms.delete_again") : row.Count, font * 0.85f, TextAnchor.MiddleCenter, row.ConfirmDelete ? RoomUiDraw.Gold : c);
            }
            s_keyRects.Clear();
            s_keyRects.AddRange(RoomUiDraw.KeyBar("myrooms.keys", new[] {
                Key(KeyCode.Space, KeyCode.JoystickButton0, "myrooms.key.enter"), Key(KeyCode.Delete, KeyCode.JoystickButton3, "myrooms.key.delete"),
                Key(KeyCode.Escape, KeyCode.JoystickButton1, "myrooms.key.back") }));
        }

        // A bar item for the keys Tick reads (confirm Space / A, back Esc / B, delete Del / Y = the game's Delete, Tab):
        // the game's own button icon, or a keycap with the key's name if the game has none.
        static RoomUiDraw.Key Key(KeyCode key, KeyCode pad, string label) { return Key(key, pad, label, true); }
        static RoomUiDraw.Key Key(KeyCode key, KeyCode pad, string label, bool enabled)
        {
            return new RoomUiDraw.Key { Name = RoomUiDraw.KeyName(key), Icon = RoomUiDraw.KeyIcon(key, pad), Label = L10n.T(label), Enabled = enabled };
        }

        static void Mouse()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 0 || Time.unscaledTime < s_ignoreUntil || SafeBusy() || s_requestRunning) return;
            Vector2 p = e.mousePosition;
            if (s_page == Page.Join)
            {
                int firstServer = s_join.DropdownFirst;   // the rows on screen start there
                for (int i = 0; i < s_rowRects.Count; i++) if (s_rowRects[i].Contains(p)) { e.Use(); s_join.ClickServer(firstServer + i); return; }
                if (s_serverRect.Contains(p)) { e.Use(); s_join.Focus = MyRoomsJoinModel.RowServer; Act(s_join.Press()); return; }
                if (s_codeRect.Contains(p)) { e.Use(); s_join.Focus = MyRoomsJoinModel.RowCode; Act(s_join.Press()); return; }
                if (s_joinRect.Contains(p)) { e.Use(); s_join.Focus = MyRoomsJoinModel.RowJoin; Act(s_join.Press()); return; }
                if (s_keyRects.Count == 3)
                {
                    if (s_keyRects[0].Contains(p)) { e.Use(); Act(s_join.Press()); }
                    else if (s_keyRects[1].Contains(p)) { e.Use(); s_join.NextServer(); }
                    else if (s_keyRects[2].Contains(p)) { e.Use(); if (s_join.Back() == MyRoomsJoinAction.Back) Back(); }
                }
                return;
            }
            int first = Math.Max(0, Math.Min(s_list.Focus - 4, s_list.Rooms.Count - 5));
            for (int i = 0; i < s_rowRects.Count; i++)
                if (s_rowRects[i].Contains(p))
                {
                    e.Use();
                    bool again = s_list.Focus == first + i;
                    s_list.Click(first + i);
                    if (again) ListAct(s_list.Enter());   // a second click on the selected row goes in
                    return;
                }
            if (s_keyRects.Count == 3)
            {
                if (s_keyRects[0].Contains(p)) { e.Use(); ListAct(s_list.Enter()); }
                else if (s_keyRects[1].Contains(p)) { e.Use(); ListAct(s_list.Delete()); }
                else if (s_keyRects[2].Contains(p)) { e.Use(); ListAct(s_list.Back()); }
            }
        }

        // ---- test hooks (ORICOOP_TEST=1 only): the page's own action paths, without typing ----------------------

        static bool TestOn { get { return Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1"; } }

        // Picks a saved server and fills the code, as the dropdown and code typing would. false: not on the join page or
        // the server is not in the page's list.
        internal static bool TestSetJoin(string server, string code)
        {
            if (!TestOn || s_page != Page.Join || s_editingCode) return false;
            int index = s_join.Servers.IndexOf(server ?? "");
            if (index < 0) return false;
            s_join.ClickServer(index);
            s_join.SetCode(code);
            s_join.Focus = MyRoomsJoinModel.RowJoin;
            return true;
        }

        // 加入 pressed (the confirm key on the focused 加入 row). true once the join request started.
        internal static bool TestPressJoin()
        {
            if (!TestOn || s_page != Page.Join || s_editingCode || Time.unscaledTime < s_ignoreUntil || SafeBusy() || s_requestRunning) return false;
            s_join.Focus = MyRoomsJoinModel.RowJoin;
            MyRoomsJoinAction a = s_join.Press();
            if (a != MyRoomsJoinAction.Join) return false;
            Act(a);
            return true;
        }

        internal static string TestState
        {
            get { return "page=" + s_page + ", action=" + LastAction + ", running=" + s_requestRunning + ", note=" + s_note + ", message=" + SafeMessage(); }
        }

        // Esc on the page (returns to the 我的房间 menu).
        internal static bool TestBack()
        {
            if (!TestOn || s_page == Page.None || Time.unscaledTime < s_ignoreUntil) return false;
            Back();
            return true;
        }

        // ---- the 我的房间 menu's own key bar (the menu is the native orb page) ---------------------------------

        public static void DrawMenuKeys()
        {
            NativeGui.BeginPage("myrooms.menu");
            try { RoomUiDraw.KeyBar("myrooms.menu.keys", new[] { Key(KeyCode.Space, KeyCode.JoystickButton0, "myrooms.key.select"), Key(KeyCode.Escape, KeyCode.JoystickButton1, "myrooms.key.back") }); }
            finally { NativeGui.EndPage(); }
        }
    }
}
