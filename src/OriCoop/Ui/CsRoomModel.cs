using System;
using System.Collections.Generic;

namespace OriCoop.Ui
{
    // T-171: the C-server room page (房间管理页) as plain data, no Unity, so the unit tests drive it.
    // The page (CsRoomPage) fills Seats from the C-server roster and the players' lobby states every frame,
    // asks this model what to draw (rows, bottom bar) and feeds it navigation and key presses; the model
    // answers with the action to run. The roster's arbiter seat supplies the room owner label.
    // T-189: CopyCode, Back and ConfirmLeave belong to the in-game page (pause menu -> 房间管理) only.
    internal enum CsRoomAction { None, Start, Ready, SwitchSave, Kick, Leave, OpenCode, CopyCode, Back, ConfirmLeave }

    internal sealed class CsRoomSeat
    {
        public bool Taken;      // the seat has a player (online, or held for a reconnect)
        public bool Online;
        public bool Local;      // a player on this PC (split-screen: several)
        public bool Ready;
        public bool Playing;    // that PC already left the page and is in the game (a late join goes straight in)
        public string Nick = "";
        public int RttMs = -1;  // the player's own round trip to the server; -1 = not known yet
    }

    internal struct CsRoomRow
    {
        public int Seat;
        public bool Empty, Selected;
        public bool Picked;     // the bar has the focus and 踢出 would act on this player
        public string Label, Name, Role, Ping, Status;
        public bool Ready;
    }

    internal struct CsRoomBarItem
    {
        public CsRoomAction Action;
        public string Label;
        public bool Enabled, Selected;
    }

    // The room page's shortcut keys (keyboard and controller, from the config): key code -> actions, in bind order. One key
    // may carry several actions; CsRoomModel.ShortcutFirst picks the one the bar allows. Key codes are plain ints (Unity's
    // KeyCode values; 0 = KeyCode.None = not bound).
    internal sealed class CsRoomKeyMap
    {
        readonly List<KeyValuePair<int, CsRoomAction>> m_binds = new List<KeyValuePair<int, CsRoomAction>>();

        public void Clear() { m_binds.Clear(); }

        public void Bind(CsRoomAction action, int key)
        {
            if (key != 0) m_binds.Add(new KeyValuePair<int, CsRoomAction>(key, action));
        }

        // Every bound key once, in bind order.
        public List<int> Keys()
        {
            List<int> keys = new List<int>();
            foreach (KeyValuePair<int, CsRoomAction> b in m_binds) if (!keys.Contains(b.Key)) keys.Add(b.Key);
            return keys;
        }

        public List<CsRoomAction> ActionsFor(int key)
        {
            List<CsRoomAction> actions = new List<CsRoomAction>();
            foreach (KeyValuePair<int, CsRoomAction> b in m_binds) if (b.Key == key && !actions.Contains(b.Value)) actions.Add(b.Value);
            return actions;
        }
    }

    internal sealed class CsRoomModel
    {
        public const int SeatCount = 4;
        public const int BarRow = SeatCount;   // focus row index of the bottom bar (rows 0..3 = P1..P4)

        public readonly CsRoomSeat[] Seats = new CsRoomSeat[SeatCount + 1];   // [1..4]
        int m_focusRow;
        int m_pickedSeat;
        string m_pickedNick = "";
        // 0..3 player rows, BarRow = the bottom bar. Moving the focus does not pick anyone: Up / Down pass through the rows
        // on the way to the bar (Down from P4, Up from P1), so a pick that followed the focus would always end on P4 or P1.
        // A player is picked by confirming (A / Enter) on that row or by clicking it; the pick stays while the focus moves on
        // (to the bar), so the bar's 踢出 can be pressed with the cursor or clicked (T-171: every bar item can be).
        public int FocusRow
        {
            get { return m_focusRow; }
            set { m_focusRow = value; }
        }
        // The player the bar's 踢出 acts on: the last player row confirmed or clicked (0 = none yet).
        public int PickedSeat { get { return m_pickedSeat; } }
        // Whom 踢出 acts on right now: the focused player row while a row has the focus (the 踢出 key on a row), else the pick.
        public int KickSeat { get { return FocusRow >= 0 && FocusRow < BarRow ? FocusRow + 1 : m_pickedSeat; } }

        void Pick(int seat)
        {
            m_pickedSeat = seat;
            m_pickedNick = seat >= 1 && seat <= SeatCount ? Seats[seat].Nick ?? "" : "";
        }

        // After the seats were filled (every frame): a picked player who left (the seat is free, or someone else is on it
        // now) is no longer picked, so 踢出 never turns on for a newcomer nobody picked.
        public void SeatsUpdated()
        {
            if (m_pickedSeat < 1 || m_pickedSeat > SeatCount) return;
            CsRoomSeat s = Seats[m_pickedSeat];
            if (!s.Taken || (s.Nick ?? "") != m_pickedNick) m_pickedSeat = 0;
        }
        // T-173: a 我的房间 room (persistent). Pending = not confirmed yet (the T-171 rules, without 换存档: the room's save was
        // fixed when it was made); confirmed = everyone just goes in (进入, no ready), any member can reopen the invite code.
        public bool Persistent, Confirmed, CodeOpen;
        // T-173: the room service allows open_code (a confirmed room with fewer members than seats; MyRooms.CanOpenCode).
        // The C-server seats say nothing about it: a member may be offline, and a room may have fewer than 4 seats.
        public bool CanOpenCode;
        // T-189: the in-game page (pause menu -> 房间管理): no save, no ready / start; the invite code (open, copy), kick, back to
        // the pause menu, and leaving the room (asked first, then back to the main menu). HasCode = a code to copy is shown
        // (a 我的房间 room's open invite code, or a server-list room's live room code). LeaveAsked = 离开房间 was pressed and the
        // bar asks to confirm.
        public bool InGame, HasCode, LeaveAsked;
        public int FocusBar;     // index into Bar() while FocusRow == BarRow
        public int OwnerSeat;
        Func<string, string> m_text;

        public CsRoomModel(Func<string, string> text)
        {
            m_text = text;
            for (int s = 1; s <= SeatCount; s++) Seats[s] = new CsRoomSeat();
        }

        public int ManagerSeat
        {
            get
            {
                for (int s = 1; s <= SeatCount; s++) if (Seats[s].Taken && Seats[s].Online) return s;
                return 0;
            }
        }

        public bool IsManager { get { int m = ManagerSeat; return m != 0 && Seats[m].Local; } }

        public bool LocalReady
        {
            get
            {
                bool any = false;
                for (int s = 1; s <= SeatCount; s++)
                    if (Seats[s].Taken && Seats[s].Local) { any = true; if (!Seats[s].Ready) return false; }
                return any;
            }
        }

        // Every online player is ready (offline seats held for a reconnect do not block the start).
        public bool AllReady
        {
            get
            {
                bool any = false;
                for (int s = 1; s <= SeatCount; s++)
                {
                    CsRoomSeat seat = Seats[s];
                    if (!seat.Taken || !seat.Online) continue;
                    any = true;
                    if (!seat.Ready) return false;
                }
                return any;
            }
        }

        public bool CanStart { get { return IsManager && AllReady; } }

        // T-173 (room-v2-a): the room's name can be changed ([E] 改名) by the room's manager only, and not once a game is
        // already running (the name is the room's, set before it plays).
        // T-189: in the game (the in-game page) the manager may rename too (owner-approved design).
        public bool CanRename { get { return IsManager && (InGame || !GameRunning); } }

        // T-189: every seat has a player (online or held): nobody else can come in, so the invite code cannot be opened or
        // copied.
        public bool SeatsFull
        {
            get
            {
                for (int s = 1; s <= SeatCount; s++) if (!Seats[s].Taken) return false;
                return true;
            }
        }

        // Someone on another PC is already in the game: this PC can only join it (加入游戏), whoever it is.
        public bool GameRunning
        {
            get
            {
                for (int s = 1; s <= SeatCount; s++)
                    if (Seats[s].Taken && Seats[s].Online && !Seats[s].Local && Seats[s].Playing) return true;
                return false;
            }
        }

        public int FocusSeat { get { return FocusRow < BarRow ? FocusRow + 1 : 0; } }

        // The page opens with the focus on the bar and no player picked.
        public void ResetFocus() { m_focusRow = BarRow; FocusBar = 0; m_pickedSeat = 0; m_pickedNick = ""; LeaveAsked = false; }

        // T-189 (in-game page): 离开房间 asks first; the bar becomes 确定离开 / 取消 with the focus on 确定离开.
        public void AskLeave() { LeaveAsked = true; m_focusRow = BarRow; FocusBar = 0; }
        public void CancelLeave() { LeaveAsked = false; m_focusRow = BarRow; FocusBar = 0; }

        // The given seat's player can be kicked by this PC: a taken seat of another PC.
        public bool CanKick(int seat)
        {
            return IsManager && seat >= 1 && seat <= SeatCount && Seats[seat].Taken && !Seats[seat].Local;
        }

        public CsRoomRow Row(int seat)
        {
            CsRoomSeat s = Seats[seat];
            CsRoomRow row = new CsRoomRow { Seat = seat, Label = "P" + seat, Selected = FocusRow == seat - 1, Ready = s.Ready,
                Picked = FocusRow == BarRow && PickedSeat == seat && CanKick(seat) };
            if (!s.Taken)
            {
                row.Empty = true;
                row.Name = m_text("csroom.waiting_player");
                row.Role = "";
                row.Ping = "-";
                row.Status = m_text("csroom.waiting");
                return row;
            }
            row.Name = s.Nick + (s.Local ? " " + m_text("csroom.you") : "");
            row.Role = m_text(L10n.RoleKey(true, seat == OwnerSeat));
            row.Ping = s.RttMs < 0 || !s.Online ? "-" : s.RttMs + " ms";
            if (InGame) row.Status = m_text(!s.Online ? "csroom.offline" : s.Local ? "csroom.local" : "csroom.remote");   // T-189: no ready in the game
            else row.Status = !s.Online ? m_text("csroom.offline") : s.Playing ? m_text("csroom.playing") : m_text(s.Ready ? "csroom.ready" : "csroom.not_ready");
            return row;
        }

        public List<CsRoomBarItem> Bar()
        {
            List<CsRoomBarItem> bar = new List<CsRoomBarItem>();
            bool manager = IsManager;
            if (InGame)
            {
                // T-189: [开放邀请码] (我的房间 rooms: a closed code opens for 5 minutes) / [复制邀请码] / [踢出] (the manager; on
                // with another PC's player picked or focused) /
                // [返回] (the pause menu) / 离开房间. A full room cannot take anyone: open and copy are off.
                if (LeaveAsked)
                {
                    Add(bar, CsRoomAction.ConfirmLeave, "csroom.act.leave_yes", true);
                    Add(bar, CsRoomAction.Back, "csroom.act.cancel", true);
                    MarkFocus(bar);
                    return bar;
                }
                bool full = SeatsFull;
                if (Persistent) Add(bar, CsRoomAction.OpenCode, "csroom.act.open_code", Confirmed && !CodeOpen && CanOpenCode && !full);
                Add(bar, CsRoomAction.CopyCode, "csroom.act.copy_code", HasCode && !full);
                // 踢出 stays in the manager's bar (off until another PC's player is picked or focused): a bar that grew and
                // shrank with the pick made a click or a confirm run the item next to the one drawn (T-189 review).
                if (manager) Add(bar, CsRoomAction.Kick, "csroom.act.kick", CanKick(KickSeat));
                Add(bar, CsRoomAction.Back, "csroom.act.back", true);
                Add(bar, CsRoomAction.Leave, "csroom.act.leave", true);
                MarkFocus(bar);
                return bar;
            }
            if (Persistent && Confirmed)
            {
                Add(bar, CsRoomAction.Start, "csroom.act.enter", true);
                Add(bar, CsRoomAction.OpenCode, "csroom.act.open_code", !CodeOpen && CanOpenCode);
                Add(bar, CsRoomAction.Leave, "csroom.act.leave", true);
                MarkFocus(bar);
                return bar;
            }
            if (GameRunning) Add(bar, CsRoomAction.Start, "csroom.act.join_game", true);
            else if (manager) Add(bar, CsRoomAction.Start, "csroom.act.start", CanStart);
            Add(bar, CsRoomAction.Ready, LocalReady ? "csroom.act.unready" : "csroom.act.ready", true);
            if (manager && !GameRunning && !Persistent) Add(bar, CsRoomAction.SwitchSave, "csroom.act.switch", true);
            if (manager) Add(bar, CsRoomAction.Kick, "csroom.act.kick", CanKick(KickSeat));
            Add(bar, CsRoomAction.Leave, "csroom.act.leave", true);
            MarkFocus(bar);
            return bar;
        }

        void MarkFocus(List<CsRoomBarItem> bar)
        {
            if (FocusRow != BarRow) return;
            int focus = Math.Max(0, Math.Min(FocusBar, bar.Count - 1));
            CsRoomBarItem item = bar[focus]; item.Selected = true; bar[focus] = item;
        }

        void Add(List<CsRoomBarItem> bar, CsRoomAction action, string key, bool enabled)
        {
            bar.Add(new CsRoomBarItem { Action = action, Label = m_text(key), Enabled = enabled });
        }

        // ---- input --------------------------------------------------------------------------------------

        public void Up() { FocusRow = FocusRow <= 0 ? BarRow : FocusRow - 1; }
        public void Down() { FocusRow = FocusRow >= BarRow ? 0 : FocusRow + 1; }

        public void Left()
        {
            if (FocusRow != BarRow) return;
            int count = Bar().Count;
            FocusBar = (Math.Min(FocusBar, count - 1) + count - 1) % count;
        }

        public void Right()
        {
            if (FocusRow != BarRow) return;
            int count = Bar().Count;
            FocusBar = (Math.Min(FocusBar, count - 1) + 1) % count;
        }

        // Confirm (A / Enter / click): on the bar the focused item; on a player row no action: it picks that player for the
        // bar's 踢出. Returns the action to run, or None when it is not allowed right now.
        public CsRoomAction Press()
        {
            if (FocusRow != BarRow) { if (FocusRow >= 0 && FocusRow < BarRow) Pick(FocusRow + 1); return CsRoomAction.None; }
            List<CsRoomBarItem> bar = Bar();
            CsRoomBarItem item = bar[Math.Max(0, Math.Min(FocusBar, bar.Count - 1))];
            return item.Enabled ? item.Action : CsRoomAction.None;
        }

        // A shortcut key: the same rules as pressing that bar item (hidden or disabled items do nothing).
        public CsRoomAction Shortcut(CsRoomAction action)
        {
            foreach (CsRoomBarItem item in Bar())
                if (item.Action == action) return item.Enabled ? action : CsRoomAction.None;
            return CsRoomAction.None;
        }

        // A key bound to several actions (换存档 and 开放邀请码 share Y by default; they are never on the same bar): the
        // press goes to the first of them the bar has enabled right now.
        public CsRoomAction ShortcutFirst(IList<CsRoomAction> actions)
        {
            foreach (CsRoomAction action in actions)
            {
                CsRoomAction a = Shortcut(action);
                if (a != CsRoomAction.None) return a;
            }
            return CsRoomAction.None;
        }

        // Clicking a bar item with the mouse focuses it and presses it.
        public CsRoomAction Click(int barIndex)
        {
            if (barIndex < 0 || barIndex >= Bar().Count) return CsRoomAction.None;
            // The bar as drawn: with the keyboard focus on another PC's player row (not confirmed), 踢出 was drawn on and acts on
            // that player; moving the focus to the bar must not leave 踢出 without its target (T-189 review).
            if (FocusRow >= 0 && FocusRow < BarRow && CanKick(FocusRow + 1)) Pick(FocusRow + 1);
            FocusRow = BarRow; FocusBar = barIndex;
            return Press();
        }

        public void ClickRow(int seat)
        {
            if (seat >= 1 && seat <= SeatCount) { FocusRow = seat - 1; Pick(seat); }
        }
    }
}
