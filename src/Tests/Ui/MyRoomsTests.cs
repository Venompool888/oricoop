using System;
using System.Collections.Generic;
using OriCoop;
using OriCoop.Ui;

// T-173: the 我的房间 pages. Expected values come from the ticket (docs/tickets/T-173.md) and the owner's sketch:
// no 房间列表 without rooms; a server dropdown only with several saved servers, Tab cycles servers; 加入 needs a 6-char code;
// rows show name / ROOM number / members+seats with 已满 when full; 删除 needs two presses on the same row.
static class MyRoomsTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    public static void Register(Action<string, Action> run)
    {
        run("my rooms menu: 房间列表 only when the player has rooms", delegate {
            Equal(2, MyRoomsMenu.Items(0).Count);
            Check(!MyRoomsMenu.Items(0).Contains(MyRoomsMenuItem.List), "list shown without rooms");
            List<MyRoomsMenuItem> some = MyRoomsMenu.Items(1);
            Equal(3, some.Count); Equal(MyRoomsMenuItem.Create, some[0]); Equal(MyRoomsMenuItem.Join, some[1]); Equal(MyRoomsMenuItem.List, some[2]);
        });

        run("my rooms join: one server = no dropdown; several = dropdown picks, Tab cycles, Esc closes it first", delegate {
            MyRoomsJoinModel m = new MyRoomsJoinModel();
            m.Servers.Add("203.0.113.7:47021");
            Equal(false, m.HasDropdown);
            Equal(MyRoomsJoinAction.None, m.Press());
            Equal(false, m.DropdownOpen);
            m.Servers.Add("198.51.100.4:47021"); m.Servers.Add("192.0.2.10:47021");
            Equal(MyRoomsJoinAction.None, m.Press());
            Equal(true, m.DropdownOpen); Equal(0, m.DropdownFocus);
            m.Down(); m.Down();
            Equal(MyRoomsJoinAction.None, m.Press());
            Equal(false, m.DropdownOpen); Equal("192.0.2.10:47021", m.ServerText);
            m.NextServer(); Equal("203.0.113.7:47021", m.ServerText);
            m.Press(); Equal(true, m.DropdownOpen);
            Equal(MyRoomsJoinAction.None, m.Back()); Equal(false, m.DropdownOpen);
            Equal(MyRoomsJoinAction.Back, m.Back());
        });

        run("my rooms join: rows wrap; code row edits; 加入 only with a server and a 6-char code", delegate {
            MyRoomsJoinModel m = new MyRoomsJoinModel();
            m.Up(); Equal(MyRoomsJoinModel.RowJoin, m.Focus);
            Equal(MyRoomsJoinAction.None, m.Press());                 // no server, no code
            m.Servers.Add("a:1");
            m.SetCode("k7q-2m9x");
            Equal("K7Q2M9", m.Code);
            Equal(MyRoomsJoinAction.Join, m.Press());
            m.SetCode("12345"); Equal(MyRoomsJoinAction.None, m.Press());
            m.Down(); Equal(MyRoomsJoinModel.RowServer, m.Focus);
            m.Down(); Equal(MyRoomsJoinAction.EditCode, m.Press());
            m.ClickServer(5); Equal(MyRoomsJoinModel.RowCode, m.Focus);   // out of range: ignored
            m.ClickServer(0); Equal(MyRoomsJoinModel.RowServer, m.Focus);
        });

        run("my rooms list: name, ROOM number, members/seats, 已满 when full", delegate {
            MyRoomsListModel m = new MyRoomsListModel(delegate (string k) { return L10n.T(k, true); });
            m.Rooms.Add(new MyRoomsListEntry { Id = "r1", Name = "Glimmer Hollow", Number = 1334, Members = 2, Seats = 4 });
            m.Rooms.Add(new MyRoomsListEntry { Id = "r2", Name = "Silver Thicket", Number = 4471, Members = 4, Seats = 4 });
            m.Rooms.Add(new MyRoomsListEntry { Id = "r3", Name = "", Number = -1, Members = 1, Seats = 2 });
            MyRoomsListRow a = m.Row(0), b = m.Row(1), c = m.Row(2);
            Equal("Glimmer Hollow", a.Name); Equal("ROOM 1334", a.Number); Equal("2/4", a.Count); Equal(false, a.Full); Equal(true, a.Selected);
            Equal("4/4 已满", b.Count); Equal(true, b.Full);
            Equal("r3", c.Name); Equal("ROOM -", c.Number); Equal("1/2", c.Count);
        });

        run("my rooms list: navigation wraps; a full room can still be entered by its member", delegate {
            MyRoomsListModel m = new MyRoomsListModel(delegate (string k) { return k; });
            Equal(MyRoomsListAction.None, m.Enter());
            m.Rooms.Add(new MyRoomsListEntry { Id = "r1" }); m.Rooms.Add(new MyRoomsListEntry { Id = "r2", Members = 4, Seats = 4 });
            m.Up(); Equal(1, m.Focus);
            Equal(MyRoomsListAction.Enter, m.Enter());
            Equal("r2", m.Current.Id);
            m.Down(); Equal(0, m.Focus);
        });

        run("my rooms list: 删除 needs two presses on the same row; moving or Esc cancels", delegate {
            MyRoomsListModel m = new MyRoomsListModel(delegate (string k) { return k; });
            m.Rooms.Add(new MyRoomsListEntry { Id = "r1" }); m.Rooms.Add(new MyRoomsListEntry { Id = "r2" });
            Equal(MyRoomsListAction.None, m.Delete());
            Equal(true, m.Row(0).ConfirmDelete);
            m.Down();
            Equal(false, m.Row(0).ConfirmDelete);
            Equal(MyRoomsListAction.None, m.Delete());
            Equal(MyRoomsListAction.None, m.Back());   // cancels the pending delete, does not leave
            Equal(false, m.Row(1).ConfirmDelete);
            Equal(MyRoomsListAction.None, m.Delete());
            Equal(MyRoomsListAction.Delete, m.Delete());
            Equal(MyRoomsListAction.Back, m.Back());
            m.Rooms.RemoveAt(1); m.Clamp(); Equal(0, m.Focus);
        });
    }
}
