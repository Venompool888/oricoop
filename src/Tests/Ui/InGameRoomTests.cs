using System;
using System.Collections.Generic;
using OriCoop;
using OriCoop.Ui;

// T-189: the in-game room page (pause menu -> 房间管理). Expected values come from the ticket (docs/tickets/T-189.md):
// the entry only for a C-server session in the game (not LAN, not single player); no save, ready or start in the game;
// 开放邀请码 only for a 我的房间 room whose code is closed, 复制邀请码 when a code is shown, both off when the room is full;
// 踢出 only for the manager and only on another PC's player (the item stays in the manager's bar, off until then, so the
// bar never shifts under a click: review); 离开房间 asks first; rows say 本机 / 远程 / 已断线; the invite line says 未开放 /
// the code with its time left / the server room code / 房间已满.
static class InGameRoomTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal(string expected, string actual, string what)
    {
        if (expected != actual) throw new Exception(what + ": expected '" + expected + "', got '" + actual + "'");
    }

    static string T(string key) { return L10n.T(key, true); }

    static CsRoomModel Model()
    {
        CsRoomModel m = new CsRoomModel(T);
        m.InGame = true;
        return m;
    }

    static void Seat(CsRoomModel m, int seat, string nick, bool local, bool online)
    {
        CsRoomSeat s = m.Seats[seat];
        s.Taken = true; s.Online = online; s.Local = local; s.Nick = nick; s.RttMs = 40; s.Playing = !local;
    }

    static string Bar(CsRoomModel m)
    {
        List<string> items = new List<string>();
        foreach (CsRoomBarItem item in m.Bar()) items.Add(item.Action + (item.Enabled ? "" : "(off)"));
        return string.Join(",", items.ToArray());
    }

    static void BarIs(CsRoomModel m, string expected) { Equal(expected, Bar(m), "bar"); }

    public static void Register(Action<string, Action> run)
    {
        run("in-game room: pause entry only for a linked C-server session in the game", delegate
        {
            Check(InGameRoomRules.ShowPauseEntry(true, "cserver", true, true), "C-server in the game must show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(true, "direct", true, true), "LAN (direct) must not show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(true, "steam", true, true), "a Steam session must not show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(false, "cserver", true, true), "offline (single player) must not show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(true, "cserver", false, true), "a C-server session without its server link must not show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(true, "cserver", true, false), "outside the game (title) must not show 房间管理");
            Check(!InGameRoomRules.ShowPauseEntry(true, "", true, true), "no transport must not show 房间管理");
        });

        run("in-game room: the manager's bar is copy / kick / back / leave, no save, ready or start; 踢出 on only for another PC's player", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true);
            m.HasCode = true;
            m.ResetFocus();
            BarIs(m, "CopyCode,Kick(off),Back,Leave");   // nobody picked yet
            m.ClickRow(2);
            BarIs(m, "CopyCode,Kick,Back,Leave");
            Check(m.Shortcut(CsRoomAction.Kick) == CsRoomAction.Kick && m.KickSeat == 2, "the kick key acts on the focused other player");
            m.ClickRow(1);
            BarIs(m, "CopyCode,Kick(off),Back,Leave");   // never the manager's own player
        });

        run("in-game room: a click on 踢出 kicks the player whose row had the focus (the bar as drawn)", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true);
            m.HasCode = true;
            m.ResetFocus();
            m.Up(); m.Up(); m.Up();   // from the bar up to P2's row, nobody confirmed
            Check(m.FocusRow == 1 && m.PickedSeat == 0, "focus on P2's row, not picked (focus " + m.FocusRow + ", picked " + m.PickedSeat + ")");
            BarIs(m, "CopyCode,Kick,Back,Leave");
            Check(m.Click(1) == CsRoomAction.Kick && m.KickSeat == 2, "clicking the drawn 踢出 must kick P2 (got seat " + m.KickSeat + ")");
            m.ResetFocus();
            Check(m.Click(2) == CsRoomAction.Back, "clicking 返回 must go back, never another item");
            m.ResetFocus();
            Check(m.Click(3) == CsRoomAction.Leave, "clicking 离开房间 must ask to leave, never another item");
        });

        run("in-game room: a guest (not the manager) never has 踢出, even with the host's row picked", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", false, true); Seat(m, 2, "Guest", true, true);
            m.HasCode = true;
            m.ResetFocus();
            BarIs(m, "CopyCode,Back,Leave");
            m.ClickRow(1);   // the host's row
            BarIs(m, "CopyCode,Back,Leave");
            Check(m.Shortcut(CsRoomAction.Kick) == CsRoomAction.None, "the kick key must do nothing on a guest's page");
            Check(!m.CanKick(1), "a guest cannot kick the host");
        });

        run("in-game room: 我的房间 invite code closed -> open; open -> copy; full -> both off", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true);
            m.Persistent = true; m.Confirmed = true; m.CanOpenCode = true;
            m.CodeOpen = false; m.HasCode = false;
            m.ResetFocus();
            BarIs(m, "OpenCode,CopyCode(off),Kick(off),Back,Leave");
            Check(m.Shortcut(CsRoomAction.OpenCode) == CsRoomAction.OpenCode, "a closed code must open with its key");
            m.CodeOpen = true; m.HasCode = true;
            BarIs(m, "OpenCode(off),CopyCode,Kick(off),Back,Leave");
            Check(m.Shortcut(CsRoomAction.CopyCode) == CsRoomAction.CopyCode, "an open code must copy with its key");
            Seat(m, 3, "C", false, true); Seat(m, 4, "D", false, false);   // a held seat counts: the room is full
            Check(m.SeatsFull, "four taken seats (one held for a reconnect) are a full room");
            BarIs(m, "OpenCode(off),CopyCode(off),Kick(off),Back,Leave");
            m.CodeOpen = false; m.HasCode = false;
            BarIs(m, "OpenCode(off),CopyCode(off),Kick(off),Back,Leave");
        });

        run("in-game room: the room service refusing open_code keeps 开放邀请码 off; a pending room cannot open it", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true);
            m.Persistent = true; m.Confirmed = true; m.CanOpenCode = false;
            m.ResetFocus();
            BarIs(m, "OpenCode(off),CopyCode(off),Kick(off),Back,Leave");
            m.CanOpenCode = true; m.Confirmed = false;
            BarIs(m, "OpenCode(off),CopyCode(off),Kick(off),Back,Leave");
        });

        run("in-game room: 离开房间 asks first, 取消 goes back to the normal bar", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true);
            m.HasCode = true;
            m.ResetFocus();
            m.FocusBar = 3;   // CopyCode, Kick, Back, Leave
            Check(m.Press() == CsRoomAction.Leave, "pressing 离开房间 gives Leave (the page then asks)");
            m.AskLeave();
            BarIs(m, "ConfirmLeave,Back");
            Check(m.Press() == CsRoomAction.ConfirmLeave, "the confirm bar opens on 确定离开");
            Check(m.Shortcut(CsRoomAction.Leave) == CsRoomAction.None, "a second 离开房间 press while asking does not leave by itself");
            m.CancelLeave();
            BarIs(m, "CopyCode,Kick(off),Back,Leave");
            m.AskLeave();
            m.ResetFocus();
            Check(!m.LeaveAsked, "reopening the page forgets an unanswered leave question");
        });

        run("in-game room: rows say 本机 / 远程 / 已断线, never ready", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true); Seat(m, 3, "Gone", false, false);
            Equal(T("csroom.local"), m.Row(1).Status, "own row");
            Equal(T("csroom.remote"), m.Row(2).Status, "remote row");
            Equal(T("csroom.offline"), m.Row(3).Status, "offline row");
            Check(m.Row(4).Empty && m.Row(4).Name == T("csroom.waiting_player"), "empty seat: " + m.Row(4).Name);
            m.InGame = false;
            Equal(T("csroom.playing"), m.Row(2).Status, "the title page keeps its own status");
        });

        run("in-game room: the invite line: 未开放 / code with its time left / 0:00 past expiry / server room code / 房间已满", delegate
        {
            const long now = 1795300000;
            Equal("邀请码 未开放", InGameRoomRules.CodeLine(T, true, "", 0, now, false), "closed 我的房间 code");
            Equal("邀请码 482743  4:59", InGameRoomRules.CodeLine(T, true, "482743", now + 299, now, false), "open code, 299 s left");
            Equal("邀请码 482743  0:00", InGameRoomRules.CodeLine(T, true, "482743", now - 5, now, false), "past expiry, not dropped yet");
            Equal("邀请码 482743", InGameRoomRules.CodeLine(T, true, "482743", 0, now, false), "open code, no expiry known");
            Equal("邀请码 298674", InGameRoomRules.CodeLine(T, false, "298674", 0, now, false), "server-list room code");
            Equal("", InGameRoomRules.CodeLine(T, false, "", 0, now, false), "server room without a code");
            Equal("房间已满", InGameRoomRules.CodeLine(T, true, "482743", now + 200, now, true), "full room");
            Equal("房间已满", InGameRoomRules.CodeLine(T, false, "298674", 0, now, true), "full server room");
        });

        run("in-game room: the manager may rename in the game, a guest may not", delegate
        {
            CsRoomModel m = Model();
            Seat(m, 1, "Host", true, true); Seat(m, 2, "Guest", false, true);
            Check(m.CanRename, "the manager renames on the in-game page");
            m.InGame = false;
            Check(!m.CanRename, "the title page keeps T-173's rule: no rename once a game runs");
            CsRoomModel g = Model();
            Seat(g, 1, "Host", false, true); Seat(g, 2, "Guest", true, true);
            Check(!g.CanRename, "a guest never renames");
        });

        run("in-game room: the title page bar is unchanged", delegate
        {
            CsRoomModel m = new CsRoomModel(T);
            Seat(m, 1, "Host", true, true); m.Seats[1].Ready = true; m.Seats[1].Playing = false;
            m.ResetFocus();
            BarIs(m, "Start,Ready,SwitchSave,Kick(off),Leave");
        });
    }
}
