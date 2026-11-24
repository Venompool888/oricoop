using System;
using System.Collections.Generic;
using System.IO;
using OriCoop;
using OriCoop.Ui;

// C-server room page rows, actions, and lobby messages.
static class CsRoomTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    static CsRoomModel Model() { return new CsRoomModel(delegate (string key) { return L10n.T(key, true); }); }

    static void Seat(CsRoomModel m, int seat, string nick, bool local, bool ready, int rtt)
    {
        CsRoomSeat s = m.Seats[seat];
        s.Taken = true; s.Online = true; s.Local = local; s.Ready = ready; s.Nick = nick; s.RttMs = rtt;
    }

    static List<CsRoomAction> Actions(CsRoomModel m)
    {
        List<CsRoomAction> list = new List<CsRoomAction>();
        foreach (CsRoomBarItem item in m.Bar()) list.Add(item.Action);
        return list;
    }

    static string Join(List<CsRoomAction> actions)
    {
        List<string> names = new List<string>();
        foreach (CsRoomAction a in actions) names.Add(a.ToString());
        return string.Join(",", names.ToArray());
    }

    public static void Register(Action<string, Action> run)
    {
        run("cs room: two local players both marked (你)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "Ori-3847", true, true, 23);
            Seat(m, 2, "Ori-3847·2", true, true, 23);
            Seat(m, 3, "Ori-7712", false, false, 42);
            Equal("Ori-3847 (你)", m.Row(1).Name);
            Equal("Ori-3847·2 (你)", m.Row(2).Name);
            Equal("Ori-7712", m.Row(3).Name);
            Equal("P3", m.Row(3).Label);
        });

        run("cs room: the owner badge follows the arbiter across departure and re-entry", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "creator", false, true, 30);
            Seat(m, 2, "me", true, true, 23);
            m.OwnerSeat = 1;
            Equal("房主", m.Row(1).Role);
            Equal("成员", m.Row(2).Role);
            Equal("", m.Row(3).Role);
            m.InGame = true;
            m.Seats[1].Online = false;
            m.OwnerSeat = 2;
            Equal("成员", m.Row(1).Role);
            Equal("房主", m.Row(2).Role);
            Seat(m, 4, "creator", false, true, 30);
            Equal("成员", m.Row(4).Role);
            Equal("房主", m.Row(2).Role);
        });

        run("cs room: ping is each player's own rtt; unknown or offline shows - (the game font has no em dash)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "a", true, false, 23);
            Seat(m, 2, "b", true, false, 23);
            Seat(m, 3, "c", false, false, -1);
            Seat(m, 4, "d", false, false, 99);
            m.Seats[4].Online = false;
            Equal("23 ms", m.Row(1).Ping);
            Equal("23 ms", m.Row(2).Ping);
            Equal("-", m.Row(3).Ping);
            Equal("-", m.Row(4).Ping);
            Equal("0 ms", Ping(0));
        });

        run("cs room: empty seat waits for a player; ready / not ready / disconnected status", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "a", true, true, 5);
            Seat(m, 2, "b", false, false, 5);
            Seat(m, 3, "c", false, true, 5);
            m.Seats[3].Online = false;
            Equal(true, m.Row(4).Empty);
            Equal("等待玩家加入…", m.Row(4).Name);
            Equal("等待中", m.Row(4).Status);
            Equal("-", m.Row(4).Ping);
            Equal("已准备", m.Row(1).Status);
            Equal("未准备", m.Row(2).Status);
            Equal("已断线", m.Row(3).Status);
        });

        run("cs room: manager = lowest online seat, passes on when it goes offline", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "creator", false, false, 5);
            Seat(m, 2, "me", true, false, 5);
            Equal(1, m.ManagerSeat);
            Equal(false, m.IsManager);
            m.Seats[1].Online = false;
            Equal(2, m.ManagerSeat);
            Equal(true, m.IsManager);
            m.Seats[1].Taken = false; m.Seats[2].Taken = false;
            Equal(0, m.ManagerSeat);
        });

        run("cs room: the manager's bar has start / ready / change save / kick / leave; others only ready / leave", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            Seat(m, 2, "other", false, false, 5);
            Equal("Start,Ready,SwitchSave,Kick,Leave", Join(Actions(m)));
            m.Seats[1].Local = false; m.Seats[2].Local = true;
            Equal("Ready,Leave", Join(Actions(m)));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Start));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Kick));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.SwitchSave));
            Equal(CsRoomAction.Leave, m.Shortcut(CsRoomAction.Leave));
        });

        run("cs room: start only when every online player is ready (offline held seats do not block)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Seat(m, 2, "b", false, false, 5);
            Equal(false, m.CanStart);
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Start));
            m.Seats[2].Ready = true;
            Equal(true, m.CanStart);
            Equal(CsRoomAction.Start, m.Shortcut(CsRoomAction.Start));
            Seat(m, 3, "c", false, false, 5);
            Equal(false, m.CanStart);
            m.Seats[3].Online = false;
            Equal(true, m.CanStart);
            m.Seats[1].Ready = false;
            Equal(false, m.CanStart);
        });

        run("cs room: alone in the room the manager starts after readying", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            Equal(false, m.CanStart);
            m.Seats[1].Ready = true;
            Equal(true, m.CanStart);
        });

        run("cs room: ready label flips to 取消准备 once every local seat is ready", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Seat(m, 2, "me2", true, false, 5);
            Check(m.Bar().Exists(i => i.Action == CsRoomAction.Ready && i.Label == "准备"), "partly ready should offer 准备");
            m.Seats[2].Ready = true;
            Check(m.Bar().Exists(i => i.Action == CsRoomAction.Ready && i.Label == "取消准备"), "all local ready should offer 取消准备");
        });

        run("cs room: kick acts on the picked row, only another PC's player; moving to the bar keeps the pick", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            Seat(m, 2, "me2", true, false, 5);
            Seat(m, 3, "other", false, false, 5);
            m.FocusRow = 2;   // P3
            Equal(CsRoomAction.Kick, m.Shortcut(CsRoomAction.Kick));
            m.FocusRow = 1;   // P2, own split-screen player
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Kick));
            m.FocusRow = 3;   // P4, empty
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Kick));
            m.Press();                         // confirm on P4 (empty): picks it
            m.FocusRow = CsRoomModel.BarRow;   // the bar, P4 (empty) still picked
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Kick));
            // T-171: every bar item can be pressed with the cursor or the mouse: with P3 picked, the bar's 踢出 kicks P3.
            m.FocusRow = 2; m.Press(); m.FocusRow = CsRoomModel.BarRow;
            Equal(3, m.PickedSeat);
            Equal(CsRoomAction.Kick, m.Shortcut(CsRoomAction.Kick));
            Check(m.Row(3).Picked && !m.Row(3).Selected, "the picked row should stay marked while the bar has the focus");
            Equal(false, m.CanKick(0)); Equal(false, m.CanKick(5));
        });

        run("cs room: click a player row, then click 踢出 on the bar: kicks that player (also with the arrow keys + Enter)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Seat(m, 2, "other", false, false, 5);
            m.ResetFocus();                                   // the page opens on the bar, nobody picked
            int kick = Actions(m).IndexOf(CsRoomAction.Kick);
            Check(kick >= 0, "manager bar has 踢出");
            Check(!m.Bar()[kick].Enabled, "踢出 disabled before a player is picked");
            Equal(CsRoomAction.None, m.Click(kick));
            m.ClickRow(2);
            Check(m.Bar()[kick].Enabled, "踢出 enabled once P2 is picked");
            Equal(CsRoomAction.Kick, m.Click(kick));          // Click moves the focus to the bar first
            Equal(2, m.PickedSeat);
            m.ResetFocus();
            m.Up(); m.Up(); m.Up();                           // bar -> P4 -> P3 -> P2: moving alone picks nobody
            Equal(0, m.PickedSeat);
            Equal(CsRoomAction.None, m.Press());              // Enter on P2: no action, it picks P2
            Equal(2, m.PickedSeat);
            m.Down(); m.Down(); m.Down();                     // P3, P4, bar: passing the rows keeps the pick
            Equal(CsRoomModel.BarRow, m.FocusRow);
            Equal(2, m.PickedSeat);
            while (m.Bar().FindIndex(i => i.Selected) != kick) m.Right();
            Equal(CsRoomAction.Kick, m.Press());              // Enter on 踢出 kicks P2
            Equal(2, m.KickSeat);
        });

        run("cs room: arrow keys only, full room: Enter on P2, Down past P3 / P4 to the bar, Right to 踢出, Enter kicks P2 (F02)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Seat(m, 2, "b", false, true, 5);
            Seat(m, 3, "c", false, true, 5);
            Seat(m, 4, "d", false, true, 5);
            m.ResetFocus();
            int kick = Actions(m).IndexOf(CsRoomAction.Kick);
            m.Down(); m.Down();                               // bar -> P1 -> P2
            Equal(1, m.FocusRow);
            m.Press();                                        // pick P2
            m.Down(); m.Down(); m.Down();                     // P3, P4, bar
            Equal(CsRoomModel.BarRow, m.FocusRow);
            Check(m.Row(2).Picked && !m.Row(3).Picked && !m.Row(4).Picked, "P2 stays picked, not the rows passed on the way");
            while (m.Bar().FindIndex(i => i.Selected) != kick) m.Right();
            Equal(CsRoomAction.Kick, m.Press());
            Equal(2, m.KickSeat);                             // not P4, the last row the focus passed
            // The 踢出 key with the focus on a row acts on that row.
            m.Up(); Equal(3, m.FocusRow);
            Equal(CsRoomAction.Kick, m.Shortcut(CsRoomAction.Kick));
            Equal(4, m.KickSeat);
        });

        run("cs room: the pick is dropped when its player leaves; a newcomer on that seat is not picked (F02 follow-up)", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Seat(m, 3, "c", false, true, 5);
            m.ResetFocus();
            m.ClickRow(3);
            m.FocusRow = CsRoomModel.BarRow;
            m.SeatsUpdated();
            Equal(3, m.PickedSeat);
            Equal(CsRoomAction.Kick, m.Shortcut(CsRoomAction.Kick));
            m.Seats[3].Taken = false; m.Seats[3].Nick = "";   // C leaves
            m.SeatsUpdated();
            Seat(m, 3, "e", false, true, 5);                  // E takes seat 3
            m.SeatsUpdated();
            Equal(0, m.PickedSeat);
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Kick));
            Check(!m.Row(3).Picked, "the newcomer is not drawn as picked");
            // Leave and take within one frame (never seen free): the name changed, so the pick is dropped too.
            m.ClickRow(3); m.FocusRow = CsRoomModel.BarRow;
            Seat(m, 3, "f", false, true, 5);
            m.SeatsUpdated();
            Equal(0, m.PickedSeat);
        });

        run("cs room keys: one button for 换存档 and 开放邀请码 (Y) goes to the one the bar has enabled", delegate {
            const int Y = 333, C = 99, O = 111, Space = 32;
            CsRoomKeyMap keys = new CsRoomKeyMap();
            keys.Bind(CsRoomAction.Start, Space);
            keys.Bind(CsRoomAction.SwitchSave, C); keys.Bind(CsRoomAction.SwitchSave, Y);
            keys.Bind(CsRoomAction.OpenCode, O); keys.Bind(CsRoomAction.OpenCode, Y);
            keys.Bind(CsRoomAction.Kick, 0);                  // not bound
            Equal("32,99,333,111", string.Join(",", keys.Keys().ConvertAll(k => k.ToString()).ToArray()));
            Equal("SwitchSave,OpenCode", Join(keys.ActionsFor(Y)));
            Equal(0, keys.ActionsFor(0).Count);
            // A confirmed 我的房间 room: 进入 / 开放邀请码 / 离开房间. Y opens the code.
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            m.Persistent = true; m.Confirmed = true; m.CanOpenCode = true;
            Equal(CsRoomAction.OpenCode, m.ShortcutFirst(keys.ActionsFor(Y)));
            Equal(CsRoomAction.OpenCode, m.ShortcutFirst(keys.ActionsFor(O)));
            Equal(CsRoomAction.None, m.ShortcutFirst(keys.ActionsFor(C)));
            // A T-171 room, this PC the manager: Y changes the save.
            m.Persistent = false; m.Confirmed = false;
            Equal(CsRoomAction.SwitchSave, m.ShortcutFirst(keys.ActionsFor(Y)));
            // Not the manager: Y does nothing.
            Seat(m, 1, "creator", false, false, 5); Seat(m, 2, "me", true, false, 5);
            Equal(CsRoomAction.None, m.ShortcutFirst(keys.ActionsFor(Y)));
        });

        run("cs room: up/down wraps rows and bar; left/right and press on the bar; rows press nothing", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, true, 5);
            Equal(0, m.FocusRow);
            Equal(CsRoomAction.None, m.Press());
            m.Up(); Equal(CsRoomModel.BarRow, m.FocusRow);
            m.Down(); Equal(0, m.FocusRow);
            for (int i = 0; i < 4; i++) m.Down();
            Equal(CsRoomModel.BarRow, m.FocusRow);
            Equal(CsRoomAction.Start, m.Press());          // bar[0] = start, alone and ready
            m.Right(); Equal(CsRoomAction.Ready, m.Press());
            m.Left(); m.Left(); Equal(CsRoomAction.Leave, m.Press());   // wraps to the last item
            Check(m.Bar()[m.Bar().Count - 1].Selected, "focused bar item not marked selected");
            m.Up(); Check(!m.Bar().Exists(i => i.Selected), "bar item still selected with focus on a row");
            Check(m.Row(4).Selected, "row P4 should be selected after moving up from the bar");
        });

        run("cs room: clicking a bar item focuses and presses it; a disabled one does nothing", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            Equal(CsRoomAction.None, m.Click(0));          // start, not ready yet
            Equal(CsRoomModel.BarRow, m.FocusRow);
            Equal(CsRoomAction.Ready, m.Click(1));
            Equal(CsRoomAction.None, m.Click(99));
            m.ClickRow(3); Equal(2, m.FocusRow);
        });

        run("cs room: a game already running on another PC: everyone gets 加入游戏 (enabled), no change save", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "creator", false, true, 5);
            Seat(m, 2, "me", true, false, 5);
            m.Seats[1].Playing = true;
            Equal("游戏中", m.Row(1).Status);
            Equal("Start,Ready,Leave", Join(Actions(m)));
            Check(m.Bar()[0].Label == "加入游戏" && m.Bar()[0].Enabled, "join item should read 加入游戏 and be enabled");
            Equal(CsRoomAction.Start, m.Shortcut(CsRoomAction.Start));
            m.Seats[1].Online = false;   // the player in the game dropped: back to the normal rules (I am now the manager)
            Equal("Start,Ready,SwitchSave,Kick,Leave", Join(Actions(m)));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Start));
        });

        run("my room (persistent), confirmed: everyone gets 进入 (no ready) / 开放邀请码 / 离开房间", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "creator", false, false, 5);
            Seat(m, 2, "me", true, false, 5);
            m.Persistent = true; m.Confirmed = true; m.CanOpenCode = true;
            Equal("Start,OpenCode,Leave", Join(Actions(m)));
            Check(m.Bar()[0].Label == "进入" && m.Bar()[0].Enabled, "进入 should be enabled without readying");
            Equal(CsRoomAction.Start, m.Shortcut(CsRoomAction.Start));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Ready));
            Equal(CsRoomAction.OpenCode, m.Shortcut(CsRoomAction.OpenCode));
            m.CodeOpen = true;
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.OpenCode));   // already open
            m.CodeOpen = false;
            m.CanOpenCode = false;                                         // the room service says members >= seats
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.OpenCode));   // full room
        });

        run("my room: 开放邀请码 follows the room service (members < seats), not the C-server seats", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "a", true, false, 5); Seat(m, 2, "b", false, false, 5);
            m.Persistent = true; m.Confirmed = true;
            m.CanOpenCode = false;   // a 2-seat room with both members: the server would answer forbidden
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.OpenCode));
            Seat(m, 3, "c", false, false, 5); Seat(m, 4, "d", false, false, 5);
            m.Seats[3].Online = false; m.Seats[4].Online = false;
            m.CanOpenCode = true;    // all four C-server seats taken, but the room service still has a free seat
            Equal(CsRoomAction.OpenCode, m.Shortcut(CsRoomAction.OpenCode));
        });

        run("my room (persistent), pending: the T-171 rules without 换存档", delegate {
            CsRoomModel m = Model();
            Seat(m, 1, "me", true, false, 5);
            Seat(m, 2, "b", false, false, 5);
            m.Persistent = true;
            Equal("Start,Ready,Kick,Leave", Join(Actions(m)));
            Equal(CsRoomAction.None, m.Shortcut(CsRoomAction.Start));
        });

        run("cs lobby wire: state round-trips seats, ready, rtt and summary", delegate {
            List<CsLobbySeatState> sent = new List<CsLobbySeatState> {
                new CsLobbySeatState { Seat = 1, Ready = true, RttMs = 23 },
                new CsLobbySeatState { Seat = 2, Ready = false, RttMs = -1 } };
            MemoryStream stream = new MemoryStream();
            CsLobbyWire.WriteState(new BinaryWriter(stream), sent, "slot=1;area=sunkenGlades", true);
            Equal(1 + 2 * 4 + 1 + 24 + 1, (int)stream.Length);   // pinned size: count, 2 x (seat, ready, u16 rtt), summary length + bytes, flags
            stream.Position = 0;
            List<CsLobbySeatState> got = new List<CsLobbySeatState>();
            string summary; bool playing;
            Check(CsLobbyWire.TryReadState(new BinaryReader(stream), got, out summary, out playing), "decode failed");
            Equal(true, playing);
            Equal(2, got.Count);
            Equal((byte)1, got[0].Seat); Equal(true, got[0].Ready); Equal(23, got[0].RttMs);
            Equal((byte)2, got[1].Seat); Equal(false, got[1].Ready); Equal(-1, got[1].RttMs);
            Equal("slot=1;area=sunkenGlades", summary);
        });

        run("cs lobby wire: truncated, too many seats or seat 0 are refused", delegate {
            List<CsLobbySeatState> got = new List<CsLobbySeatState>();
            string summary; bool playing;
            Equal(false, CsLobbyWire.TryReadState(new BinaryReader(new MemoryStream(new byte[] { 1, 1, 1 })), got, out summary, out playing));
            Equal(false, CsLobbyWire.TryReadState(new BinaryReader(new MemoryStream(new byte[] { 5 })), got, out summary, out playing));
            Equal(false, CsLobbyWire.TryReadState(new BinaryReader(new MemoryStream(new byte[] { 1, 0, 1, 0, 0, 0 })), got, out summary, out playing));
            Equal(0, got.Count);
            bool threw = false;
            try { CsLobbyWire.WriteState(new BinaryWriter(new MemoryStream()), new List<CsLobbySeatState>(), new string('x', 256), false); }
            catch (ArgumentException) { threw = true; }
            Check(threw, "a 256-byte summary should be refused");
        });

        run("room names: default is two words from the two lists; names are cut to English (ASCII) only", delegate {
            Random random = new Random(7);
            for (int i = 0; i < 200; i++)
            {
                string n = RoomNames.Random(random);
                string[] w = n.Split(' ');
                Check(w.Length == 2 && Array.IndexOf(RoomNames.First, w[0]) >= 0 && Array.IndexOf(RoomNames.Last, w[1]) >= 0 && w[0] != w[1], "bad default name " + n);
                Equal(n, RoomNames.Clean(n));
            }
            Equal("Ori Room", RoomNames.Clean("Ori 房间 Room"));
            Equal("abc", RoomNames.Clean("  *a#b<c>;= "));
            Equal("", RoomNames.Clean(null));
            Equal(RoomNames.MaxLength, RoomNames.Clean(new string('x', 40)).Length);
            CsRoomSummary s = CsRoomSummary.Parse(new CsRoomSummary { Name = "Glimmer Hollow", Slot = 0, Area = "sunkenGlades" }.Encode());
            Equal("Glimmer Hollow", s.Name);
            Equal("Glow", CsRoomSummary.Parse("name=Glow房").Name);
        });

        run("cs room summary: encode / parse round trip, junk ignored, clock text", delegate {
            CsRoomSummary s = new CsRoomSummary { Slot = 0, Area = "sunkenGlades", Completion = 6, Seconds = 4214, Difficulty = 1 };
            string text = s.Encode();
            Equal("slot=1;area=sunkenGlades;pct=6;secs=4214;diff=1", text);
            CsRoomSummary back = CsRoomSummary.Parse(text);
            Equal(0, back.Slot); Equal("sunkenGlades", back.Area); Equal(6, back.Completion); Equal(4214, back.Seconds); Equal(1, back.Difficulty);
            CsRoomSummary junk = CsRoomSummary.Parse("slot=0;pct=-3;secs=x;what=1;;=;area=");
            Equal(-1, junk.Slot); Equal(-1, junk.Completion); Equal(-1, junk.Seconds); Equal(false, junk.HasSave);
            Equal(false, CsRoomSummary.Parse(null).HasSave);
            Equal("01:10:14", CsRoomSummary.Clock(4214));
            Equal("00:00:00", CsRoomSummary.Clock(0));
            Equal("", CsRoomSummary.Clock(-1));
            Equal("slot=2;area=ab", new CsRoomSummary { Slot = 1, Area = "a;b=" }.Encode());
        });
    }

    static string Ping(int rtt)
    {
        CsRoomModel m = Model();
        Seat(m, 1, "x", true, false, rtt);
        return m.Row(1).Ping;
    }
}
