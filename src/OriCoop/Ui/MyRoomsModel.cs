using System;
using System.Collections.Generic;

namespace OriCoop.Ui
{
    // T-173: the 我的房间 pages as plain data (no Unity; unit-tested). Designs: docs/design/room-page/codex/myrooms-*.png.
    //  menu: 新建房间 / 加入房间 / 房间列表 (房间列表 only when the player has rooms).
    //  join: 服务器 (ip:port, a dropdown when several servers are saved) / 邀请码 / 加入.
    //  list: one band per room: name, ROOM <number>, members/seats ("4/4 已满" greyed); 进入 / 删除 (pressed twice) / 返回.
    internal enum MyRoomsMenuItem { Create, Join, List }

    internal static class MyRoomsMenu
    {
        public static List<MyRoomsMenuItem> Items(int roomCount)
        {
            List<MyRoomsMenuItem> items = new List<MyRoomsMenuItem> { MyRoomsMenuItem.Create, MyRoomsMenuItem.Join };
            if (roomCount > 0) items.Add(MyRoomsMenuItem.List);
            return items;
        }
    }

    internal enum MyRoomsJoinAction { None, EditCode, Join, Back }

    internal sealed class MyRoomsJoinModel
    {
        public const int RowServer = 0, RowCode = 1, RowJoin = 2;
        public readonly List<string> Servers = new List<string>();
        public int Server;          // selected server index
        public int Focus;           // RowServer / RowCode / RowJoin
        public bool DropdownOpen;
        public int DropdownFocus;
        public string Code = "";

        public const int DropdownRows = 5;   // the dropdown shows five servers and scrolls with its focus

        public string ServerText { get { return Servers.Count == 0 ? "" : Servers[Math.Max(0, Math.Min(Server, Servers.Count - 1))]; } }

        // The first server the open dropdown shows: the window of DropdownRows follows DropdownFocus.
        public int DropdownFirst { get { return Math.Max(0, Math.Min(DropdownFocus - (DropdownRows - 1), Servers.Count - DropdownRows)); } }
        public bool HasDropdown { get { return Servers.Count > 1; } }
        public bool CanJoin { get { return Servers.Count > 0 && Code.Length == 6; } }

        public void Up()
        {
            if (DropdownOpen) { DropdownFocus = (DropdownFocus + Servers.Count - 1) % Servers.Count; return; }
            Focus = Focus <= RowServer ? RowJoin : Focus - 1;
        }

        public void Down()
        {
            if (DropdownOpen) { DropdownFocus = (DropdownFocus + 1) % Servers.Count; return; }
            Focus = Focus >= RowJoin ? RowServer : Focus + 1;
        }

        // Tab: next saved server (closes an open dropdown).
        public void NextServer()
        {
            DropdownOpen = false;
            if (Servers.Count > 0) Server = (Server + 1) % Servers.Count;
        }

        // Confirm (Space / Enter / A / click).
        public MyRoomsJoinAction Press()
        {
            if (DropdownOpen) { Server = DropdownFocus; DropdownOpen = false; return MyRoomsJoinAction.None; }
            switch (Focus)
            {
                case RowServer:
                    if (HasDropdown) { DropdownOpen = true; DropdownFocus = Server; }
                    return MyRoomsJoinAction.None;
                case RowCode: return MyRoomsJoinAction.EditCode;
                default: return CanJoin ? MyRoomsJoinAction.Join : MyRoomsJoinAction.None;
            }
        }

        // Back (Esc / B): closes the dropdown first.
        public MyRoomsJoinAction Back()
        {
            if (DropdownOpen) { DropdownOpen = false; return MyRoomsJoinAction.None; }
            return MyRoomsJoinAction.Back;
        }

        public void ClickServer(int index)
        {
            if (index < 0 || index >= Servers.Count) return;
            Server = index; DropdownOpen = false; Focus = RowServer;
        }

        // Codes are typed as digits or letters (older codes); kept upper case, at most 6.
        public void SetCode(string code)
        {
            string clean = "";
            foreach (char c in (code ?? "").ToUpperInvariant())
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')) clean += c;
            Code = clean.Length > 6 ? clean.Substring(0, 6) : clean;
        }
    }

    internal struct MyRoomsListRow
    {
        public string Id, Name, Number, Count;
        public bool Full, Selected, ConfirmDelete;
    }

    internal sealed class MyRoomsListEntry
    {
        public string Id = "", Name = "";
        public long Number = -1;     // the server's room number (ROOM 1334); -1 = not known yet
        public int Members, Seats = 4;
        public bool Creator;         // this player is seat 1: 删除 deletes the room, otherwise it leaves it
    }

    internal enum MyRoomsListAction { None, Enter, Delete, Back }

    internal sealed class MyRoomsListModel
    {
        public readonly List<MyRoomsListEntry> Rooms = new List<MyRoomsListEntry>();
        public int Focus;
        int m_confirm = -1;          // the row whose 删除 was pressed once
        Func<string, string> m_text;

        public MyRoomsListModel(Func<string, string> text) { m_text = text; }

        public MyRoomsListRow Row(int i)
        {
            MyRoomsListEntry r = Rooms[i];
            bool full = r.Seats > 0 && r.Members >= r.Seats;
            return new MyRoomsListRow
            {
                Id = r.Id, Name = r.Name.Length > 0 ? r.Name : r.Id,
                Number = r.Number >= 0 ? "ROOM " + r.Number : "ROOM -",
                Count = r.Members + "/" + r.Seats + (full ? " " + m_text("myrooms.full") : ""),
                Full = full, Selected = i == Focus, ConfirmDelete = i == m_confirm,
            };
        }

        public void Up() { if (Rooms.Count > 0) { Focus = (Focus + Rooms.Count - 1) % Rooms.Count; m_confirm = -1; } }
        public void Down() { if (Rooms.Count > 0) { Focus = (Focus + 1) % Rooms.Count; m_confirm = -1; } }

        // A member may always come back into its own room, full or not (full only matters to new players).
        public MyRoomsListAction Enter() { m_confirm = -1; return Rooms.Count > 0 ? MyRoomsListAction.Enter : MyRoomsListAction.None; }

        // 删除 needs two presses on the same row.
        public MyRoomsListAction Delete()
        {
            if (Rooms.Count == 0) return MyRoomsListAction.None;
            if (m_confirm != Focus) { m_confirm = Focus; return MyRoomsListAction.None; }
            m_confirm = -1;
            return MyRoomsListAction.Delete;
        }

        public MyRoomsListAction Back()
        {
            if (m_confirm >= 0) { m_confirm = -1; return MyRoomsListAction.None; }
            return MyRoomsListAction.Back;
        }

        public void Click(int i) { if (i >= 0 && i < Rooms.Count) { if (Focus != i) m_confirm = -1; Focus = i; } }

        public MyRoomsListEntry Current { get { return Rooms.Count == 0 ? null : Rooms[Math.Max(0, Math.Min(Focus, Rooms.Count - 1))]; } }

        public void Clamp() { Focus = Rooms.Count == 0 ? 0 : Math.Max(0, Math.Min(Focus, Rooms.Count - 1)); if (m_confirm >= Rooms.Count) m_confirm = -1; }
    }

    // T-173 review: how the 我的房间 join / list pages sit on the hidden native 我的房间 menu (MyRoomsPages, NativeRooms).
    internal static class MyRoomsPageFlow
    {
        // After a page closes, the native menu comes back locked this long: the Esc / B that closed the page is still down
        // and would otherwise also back out of 我的房间 (NativeModeMenu's s_unlockAt).
        public const float NativeUnlockDelay = 0.35f;
        // After a page opens or code editing ends, the page ignores input this long (PressGate.Block).
        public const float OpenIgnore = 0.3f, EditEndIgnore = 0.2f;

        // Back (Esc / B) while a join / enter started on the page is still running (or the room service is busy with it)
        // abandons it first (MyRooms.Cancel), so its reply cannot take the player into the room they backed out of.
        public static bool CancelOnBack(bool busy, bool requestRunning) { return CancelOnBack(busy, requestRunning, false); }
        // A 删除 is never cancelled (the room service may already have deleted the room; cancelling would drop the reply and
        // leave the room in the local list until the next refresh): the page closes and the delete finishes on its own.
        public static bool CancelOnBack(bool busy, bool requestRunning, bool deleteRunning)
        {
            return requestRunning || (busy && !deleteRunning);
        }

        // While a page is up the hidden native menu stays on 我的房间: it does not follow RoomsPanel.Screen to the legacy
        // room pages (room / create / join / password / slots).
        public static bool NativeFollowsScreen(bool pageOpen) { return !pageOpen; }
    }
}
