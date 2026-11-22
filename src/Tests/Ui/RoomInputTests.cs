using System;
using System.Collections.Generic;
using OriCoop;
using OriCoop.Ui;

// T-171 / T-173 review fixes: one press = one action on the room pages (PressGate), the room page's shared state between PCs
// (CsLobbyState), the save slot list after a failed 新建房间 (SlotListRecovery), the server dropdown's scrolling and how the
// 我的房间 pages sit on the native menu (MyRoomsPageFlow). Expected values come from the review findings' scenarios and the
// tickets (docs/tickets/T-171.md, T-173.md).
static class RoomInputTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("expected '" + expected + "', got '" + actual + "'");
    }

    const float Step = 1f / 60f;   // the game's fixed step; frames run faster (144 Hz) in the scenarios below
    const float Frame = 1f / 144f;

    static PressGate OpenGate(float now) { PressGate g = new PressGate(); g.Block(now, 0.3f); return g; }

    public static void Register(Action<string, Action> run)
    {
        run("press gate: one DEL counts once although the key (per frame) and the game button (per fixed step) both see it", delegate {
            // F30 case 1: the key-down frame has no fixed step; the next frame's fixed step sets Delete.Pressed.
            PressGate g = OpenGate(0f);
            float t = 1f;
            int fired = 0;
            if (g.Edge(6, false, t)) fired++;                  // released, seen once
            t += Frame; if (g.Edge(6, true, t)) fired++;            // frame N: GetKey(Delete) true, button not yet
            t += Frame; if (g.Edge(6, true, t)) fired++;            // frame N+1: fixed step, Delete.Pressed true too
            t += Frame; if (g.Edge(6, true, t)) fired++;
            t += Frame; if (g.Edge(6, true, t)) fired++;            // key up this frame, button still held until the next step
            t += Step; if (g.Edge(6, false, t)) fired++;
            Equal(1, fired);
            // ... and the two-press 删除 needs a second, separate press.
            MyRoomsListModel list = new MyRoomsListModel(delegate (string k) { return k; });
            list.Rooms.Add(new MyRoomsListEntry { Id = "a" });
            MyRoomsListAction a = MyRoomsListAction.None;
            for (int i = 0; i < fired; i++) a = list.Delete();
            Equal(MyRoomsListAction.None, a);
            t += 0.3f; Check(g.Edge(6, true, t), "a second press fires");
            Equal(MyRoomsListAction.Delete, list.Delete());
        });

        run("press gate: a stale game button (true over several frames of one fixed step) fires once", delegate {
            PressGate g = OpenGate(0f);
            float t = 1f; g.Edge(2, false, t);
            int fired = 0;
            for (int f = 0; f < 3; f++) { t += Frame; if (g.Edge(2, true, t)) fired++; }   // ActionButtonA.Pressed on 3 frames
            Equal(1, fired);
        });

        run("press gate: Space as a shortcut key and as the game's A button (next fixed step) is one action", delegate {
            PressGate g = OpenGate(0f);
            // -6 = confirm: GetKey(Return) as the key, ActionButtonA.Pressed as the game button.
            float t = 1f; g.Edge(32, false, t); g.Edge(-6, false, false, t);
            t += Frame;
            bool start = g.Edge(32, true, t);     // GetKey(Space) this frame
            bool confirm = g.Edge(-6, false, false, t);  // ActionButtonA not updated yet
            Check(start && !confirm, "the key fires first");
            t += Frame;
            g.Edge(32, true, t);
            Check(!g.Edge(-6, false, true, t), "the A button showing up one fixed step later must not press the focused item too");
            t += 0.5f;
            Check(!g.Edge(-6, false, true, t), "still the same press");
            t += Frame; g.Edge(-6, false, false, t); g.Edge(32, false, t);
            t += Frame; Check(g.Edge(-6, false, true, t), "a new A press fires");
        });

        run("press gate: a different press right after another one is not lost (Down then Enter, two quick Downs, pad)", delegate {
            const int Down = -3, Confirm = -6, Shortcut = 114;
            PressGate g = OpenGate(0f);
            float t = 1f; g.Edge(Down, false, false, t); g.Edge(Confirm, false, false, t); g.Edge(Shortcut, false, t);
            t += Frame; Check(g.Edge(Down, true, false, t), "Down (arrow key)");
            t += Frame; Check(g.Edge(Confirm, true, false, t), "Enter 14 ms later fires too");
            t += Frame; g.Edge(Down, false, false, t); g.Edge(Confirm, false, false, t);
            t += Frame; Check(g.Edge(Down, true, false, t), "a second quick Down fires");
            t += Frame; g.Edge(Down, false, false, t);
            // A shortcut key whose action is not available (the page resolves it to None) does not eat the next key press.
            t += 1f; Check(g.Edge(Shortcut, true, t), "shortcut key edge");
            t += Frame; Check(g.Edge(Down, true, false, t), "the arrow key right after it");
            t += 1f; g.Edge(Shortcut, false, t); g.Edge(Down, false, false, t);
            // Controller: D-pad down, then A within 100 ms (both game buttons).
            t += Step; Check(g.Edge(Down, false, true, t), "pad down");
            t += Step; Check(g.Edge(Confirm, false, true, t), "pad A right after");
            // ... and a game button held over the key press of the same input is still one press.
            t += 1f; g.Edge(Confirm, false, false, t);
            t += Frame; Check(g.Edge(Confirm, true, false, t), "Return");
            t += Step; Check(!g.Edge(Confirm, true, true, t), "its A echo");
        });

        run("press gate: the press that opened the page or ended code editing does not act", delegate {
            PressGate g = new PressGate();
            float t = 5f;
            g.Edge(1, false, t); g.Edge(2, false, t);
            t += Frame; g.Edge(2, true, t);       // Enter pressed (it ends the code editing this frame)
            g.Block(t, 0.2f);
            int fired = 0;
            for (int f = 0; f < 200; f++) { t += Frame; if (g.Edge(2, true, t)) fired++; }   // held long past the block
            Equal(0, fired);
            t += Frame; Check(!g.Edge(2, false, t), "release");
            t += Frame; Check(g.Edge(2, true, t), "the next press fires");
            // Never seen before = treat as held: a key already down when the page first looks never fires.
            PressGate fresh = new PressGate();
            Check(!fresh.Edge(9, true, 100f), "a key down on the first look");
            Check(!fresh.Edge(9, true, 101f), "still the same press");
            fresh.Edge(9, false, 102f);
            Check(fresh.Edge(9, true, 103f), "pressed again");
            // A press that starts inside the block is swallowed too.
            PressGate b = new PressGate();
            b.Edge(3, false, 0f);
            b.Block(0f, 0.3f);
            b.Edge(3, false, 0.05f);
            Check(!b.Edge(3, true, 0.1f), "inside the block");
            Check(!b.Edge(3, true, 0.5f), "held through the end of the block");
            Check(b.Blocked(0.2f) && !b.Blocked(0.31f), "block window");
        });

        run("press gate: Esc ending the code typing does not also close the join page (F31)", delegate {
            // The join page: Esc ends the editing (EndEdit blocks the gate); Cancel.Pressed shows up on the next fixed step.
            PressGate g = OpenGate(0f);
            float t = 1f; g.Edge(1, false, t);
            t += Frame; Check(g.Edge(1, true, t), "Esc fires (it ends the editing)");
            g.Block(t, MyRoomsPageFlow.EditEndIgnore);
            MyRoomsJoinModel m = new MyRoomsJoinModel();
            m.Servers.Add("203.0.113.7:47021"); m.Focus = MyRoomsJoinModel.RowCode; m.SetCode("123456");
            int closes = 0;
            for (int f = 0; f < 60; f++) { t += Frame; if (g.Edge(1, true, t) && m.Back() == MyRoomsJoinAction.Back) closes++; }
            Equal(0, closes);
            Equal("123456", m.Code);
        });

        run("cs lobby state: a short link drop keeps the room's save and name; the end of the session clears them (F05)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.RoomName = "Glimmer Hollow";                    // set before the session starts (我的房间 -> 新建房间)
            Equal(false, s.Session(false));
            Equal("Glimmer Hollow", s.RoomName);
            Equal(false, s.Session(true));
            s.PageOpened(true, "name=Glimmer Hollow;slot=1;area=sunkenGlades");
            s.LocalReady = true;
            // The link drops for a second (CServerLink.Client null) while NetPlay keeps the session: nothing is wiped.
            Equal(false, s.Session(true));
            Equal("name=Glimmer Hollow;slot=1;area=sunkenGlades", s.LocalSummary);
            Equal("Glimmer Hollow", s.RoomName);
            Equal(true, s.LocalReady);
            Equal(true, s.Session(false));                    // NetPlay ended
            Equal("", s.LocalSummary); Equal("", s.RoomName); Equal(false, s.LocalReady);
            Equal(false, s.Session(false));                   // only once
        });

        run("cs lobby state: a seat that leaves the roster is forgotten on every PC; the next player starts clean (F07)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.Session(true);
            s.Roster(2, true);
            Check(s.Apply(2, new CsLobbySeatState { Seat = 2, Ready = true, RttMs = 40 }, true), "B's own state");
            Equal(true, s.Ready(2)); Equal(true, s.Playing(2)); Equal(40, s.Rtt(2));
            s.Roster(2, false);                               // B quit / timed out (not kicked by this PC)
            Equal(false, s.Ready(2)); Equal(false, s.Playing(2)); Equal(-1, s.Rtt(2));
            s.Roster(2, true);                                // C takes seat 2 and has not sent anything yet
            Equal(false, s.Ready(2));
            // A state that arrives before the roster shows the seat is kept (only a taken -> free change forgets).
            CsLobbyState early = new CsLobbyState();
            early.Apply(3, new CsLobbySeatState { Seat = 3, Ready = true, RttMs = 5 }, false);
            early.Roster(3, false);
            Equal(true, early.Ready(3));
        });

        run("cs lobby state: an entry is taken only for a seat its sender owns (F08)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.Session(true);
            for (int seat = 1; seat <= 4; seat++) s.Roster(seat, true);
            // P3 claims P2 before P2 said anything: refused once P2 speaks for itself (the server checks the sender).
            s.Apply(3, new CsLobbySeatState { Seat = 3, Ready = false, RttMs = 9 }, false);
            Check(s.Apply(2, new CsLobbySeatState { Seat = 2, Ready = false, RttMs = 30 }, false), "P2's own state");
            Check(!s.Apply(3, new CsLobbySeatState { Seat = 2, Ready = true, RttMs = 1 }, true), "P3 may not set P2's state");
            Equal(false, s.Ready(2)); Equal(false, s.Playing(2)); Equal(30, s.Rtt(2));
            // A split-screen seat: the first claim wins until the seat leaves.
            Check(s.Apply(1, new CsLobbySeatState { Seat = 4, Ready = true, RttMs = 12 }, false), "P1's split-screen seat 4");
            Check(!s.Apply(3, new CsLobbySeatState { Seat = 4, Ready = false, RttMs = 12 }, false), "P3 may not take over seat 4");
            Equal(true, s.Ready(4));
            s.Roster(4, false);
            s.Roster(4, true);
            Check(s.Apply(3, new CsLobbySeatState { Seat = 4, Ready = true, RttMs = 12 }, false), "after seat 4 left, a new claim");
            Check(!s.Apply(0, new CsLobbySeatState { Seat = 1, Ready = true, RttMs = 1 }, false), "sender 0 is nobody");
        });

        run("cs lobby state: no claim on an empty seat or a lower seat; the next player on it is not ready / playing (F08)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.Session(true);
            s.Roster(1, true); s.Roster(2, false); s.Roster(3, true); s.Roster(4, false);
            // A modified client on P3 sends {seat 2, ready} and playing while seat 2 is empty.
            Check(!s.Apply(3, new CsLobbySeatState { Seat = 2, Ready = true, RttMs = 1 }, true), "empty seat 2 refused");
            s.Roster(2, true);                                // C joins seat 2, has not sent its own state yet
            Equal(false, s.Ready(2)); Equal(false, s.Playing(2));
            // Now taken, but P3's split-screen seats are above P3 (a PC sends as its lowest seat): still refused.
            Check(!s.Apply(3, new CsLobbySeatState { Seat = 2, Ready = true, RttMs = 1 }, true), "lower seat 2 refused");
            Equal(false, s.Ready(2)); Equal(false, s.Playing(2));
            // P1 may not pre-claim the empty seat 4 either ...
            Check(!s.Apply(1, new CsLobbySeatState { Seat = 4, Ready = true, RttMs = 1 }, true), "empty seat 4 refused");
            s.Roster(4, true);
            Equal(false, s.Ready(4));
            // ... but once its split-screen player is on seat 4, P1 speaks for it.
            Check(s.Apply(1, new CsLobbySeatState { Seat = 4, Ready = true, RttMs = 1 }, false), "P1's split-screen seat 4");
            Equal(true, s.Ready(4));
            // C's own state is always taken.
            Check(s.Apply(2, new CsLobbySeatState { Seat = 2, Ready = false, RttMs = 20 }, false), "C's own state");
            Equal(20, s.Rtt(2));
        });

        run("cs lobby state: only the room's creator publishes its save slot, not a late joiner who is the arbiter (F06)", delegate {
            CsLobbyState s = new CsLobbyState();
            s.PageOpened(false, "slot=2;area=valleyOfTheWind");   // C joined on freed seat 1 (arbiter, NetPlay.IsHost) but did not create the room
            Equal("", s.LocalSummary);
            s.PageOpened(true, "slot=1;area=sunkenGlades");
            Equal("slot=1;area=sunkenGlades", s.LocalSummary);
            s.PageOpened(true, null);
            Equal("", s.LocalSummary);
        });

        run("slot list recovery: a failed 新建房间 cancels the difficulty screen and re-activates the slot list later (F18/F29/F34)", delegate {
            SlotListRecovery r = new SlotListRecovery();
            Equal(false, r.Due(10f, true, false));
            Equal(true, r.Fail(10f, true));                   // empty slot: the hidden difficulty screen must go
            Equal(true, r.Pending);
            Equal(false, r.Due(10f, true, false));            // not in the same frame: the caller switches the list off after the pick
            Equal(false, r.Due(10f + SlotListRecovery.Delay - 0.01f, true, false));
            Equal(true, r.Due(10f + SlotListRecovery.Delay, true, false));
            Equal(false, r.Due(20f, true, false));            // once
            Equal(false, r.Fail(30f, false));                 // a used slot: no difficulty screen
            Equal(false, r.Due(31f, false, false));           // the slot screen is gone (left the title): spent, nothing to do
            Equal(false, r.Pending);
            r.Fail(40f, false);
            Equal(false, r.Due(41f, true, true));             // the title orb is open over the slots: leave them alone
            r.Fail(50f, false); r.Cancel();
            Equal(false, r.Due(51f, true, false));            // a new pick started
        });

        run("my rooms join: the server dropdown scrolls with its focus past five servers (F35)", delegate {
            MyRoomsJoinModel m = new MyRoomsJoinModel();
            for (int i = 0; i < 7; i++) m.Servers.Add("192.0.2." + (i + 1) + ":47021");
            m.Focus = MyRoomsJoinModel.RowServer;
            m.Press();
            Equal(true, m.DropdownOpen);
            Equal(0, m.DropdownFirst);
            for (int i = 0; i < 5; i++) m.Down();
            Equal(5, m.DropdownFocus);
            Equal(1, m.DropdownFirst);                       // server 6 is drawn (rows 1..5) and highlighted
            Check(m.DropdownFocus >= m.DropdownFirst && m.DropdownFocus < m.DropdownFirst + MyRoomsJoinModel.DropdownRows, "focus on screen");
            m.Down(); Equal(2, m.DropdownFirst);
            m.ClickServer(m.DropdownFirst + 4);              // the bottom row on screen = server 7
            Equal("192.0.2.7:47021", m.ServerText);
            m.Press(); m.Down();                             // wraps to server 1
            Equal(0, m.DropdownFocus); Equal(0, m.DropdownFirst);
            MyRoomsJoinModel few = new MyRoomsJoinModel();
            few.Servers.Add("a"); few.Servers.Add("b");
            few.DropdownFocus = 1;
            Equal(0, few.DropdownFirst);
        });

        run("my rooms pages: Esc while busy cancels the request; the native menu stays on 我的房间 and comes back locked (F26/F32/F33)", delegate {
            Equal(true, MyRoomsPageFlow.CancelOnBack(true, false));
            Equal(true, MyRoomsPageFlow.CancelOnBack(false, true));    // a join waiting for the room service to be ready
            Equal(false, MyRoomsPageFlow.CancelOnBack(false, false));
            // A 删除 is not cancelled by Esc (it may already have happened on the server): the page just closes.
            Equal(false, MyRoomsPageFlow.CancelOnBack(true, false, true));
            Equal(true, MyRoomsPageFlow.CancelOnBack(true, false, false));
            Equal(true, MyRoomsPageFlow.CancelOnBack(true, true, false));
            Equal(false, MyRoomsPageFlow.NativeFollowsScreen(true));
            Equal(true, MyRoomsPageFlow.NativeFollowsScreen(false));
            Check(MyRoomsPageFlow.NativeUnlockDelay >= Step * 2, "the native menu stays locked for more than a fixed step");
            Check(MyRoomsPageFlow.OpenIgnore > 0f && MyRoomsPageFlow.EditEndIgnore > 0f, "ignore windows");
        });
    }
}
