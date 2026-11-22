using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        // Title -> 多人游玩 -> 自建服务器; stops on the server mode page (Run checks and presses its 我的房间 row next).
        internal static IEnumerator RoomsTestMenu(MonoBehaviour host)
        {
            s_tHost = host;
            yield return host.StartCoroutine(PressCoop());
            yield return host.StartCoroutine(Row(Array.IndexOf(TitleTransports, "cserver"), 0.5f));
        }
    }
}

namespace OriCoop.Rooms
{
    // T-173 我的房间 end to end through the real title menu, the native pages and the room page; no synthetic
    // create/join/enter/upload/leave calls to the room service (the test only reads info with the panel's own auth).
    //  both:  title -> 多人游玩 -> 自建服务器 -> 我的房间: a fresh room folder shows exactly 新建房间 / 加入房间 / 返回 (no
    //         房间列表); Back returns to 自建服务器; reopen.
    //  host:  新建房间 -> save slots -> co-op slot 1 -> the open room is created and entered (room page, holding the save
    //         from the picked slot): pending, 6-character invite code open, English two-word name, 4 seats, ROOM >= 1001.
    //         Waits for the guest on the C-server and on its room page; the page is pending (准备 / 开始 / 踢出 / 离开, no
    //         换存档); ready + 开始 through the page; the confirm closes the code (pending 0, no code).
    //  guest: 加入房间 page -> the test C-server + the shared code -> 加入 -> the room page of the same room (pending, 准备);
    //         ready; the host's 开始 takes it into the game; after the confirm it reopens the invite code (开放邀请码: 6
    //         characters, about 300 s left).
    //  then the gameplay half (both): room slot loaded, live code, PerformSave upload + version, guest backup, host adopt,
    //         return-to-title snapshot upload, lease release. Last, the guest returns to the title, opens 我的房间 again
    //         (now with 房间列表) and the list shows exactly this room: its name, ROOM <no>, 2/4.
    public sealed class RoomsHookTest : MonoBehaviour
    {
        internal static bool Enabled
        {
            get
            {
                string role = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_TEST");
                return TestHarness.Enabled && (role == "host" || role == "join");
            }
        }

        [HarmonyPatch(typeof(TestHarness), "Awake")]
        static class Boot
        {
            static bool Prefix(TestHarness __instance)
            {
                if (!Enabled) return true;
                __instance.enabled = false;
                __instance.gameObject.AddComponent<RoomsHookTest>();
                return false;
            }
        }

        static string joinedTarget;
        static volatile string postedLive;

        [HarmonyPatch(typeof(RoomClient), "Live")]
        static class ObserveLive
        {
            static void Postfix(string liveCode, RoomReply __result)
            {
                if (Enabled && __result.Ok) postedLive = liveCode;
            }
        }

        [HarmonyPatch(typeof(RoomGame), "Join")]
        static class ObserveJoin
        {
            static void Prefix(string target) { if (Enabled) joinedTarget = target; }
        }

        int checks;
        bool done;
        float began;
        string role, share, room, save;
        RoomsPanel panel;
        RoomReply observed;
        RoomAuth auth;
        RoomClient client;
        RoomJobs reads;
        long upload;
        bool autoContinue;   // the Update loop may press the room page's continue (ready, and start / go in)
        const int PickedSlot = 1;   // co-op slot 2: run-rooms.ps1 puts the host's fixture save there (not the first slot, review F19)

        static object Get(object target, string name)
        {
            return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        }

        bool Check(bool ok, string text)
        {
            Plugin.Log.LogInfo("ROOMS " + (ok ? "PASS " : "FAIL ") + (++checks) + " " + text);
            if (!ok) Finish(false);
            return ok;
        }

        void Finish(bool ok)
        {
            if (done) return;
            done = true;
            Plugin.Log.LogInfo("ROOMS DONE " + (ok ? "ok" : "fail"));
        }

        void Start()
        {
            began = Time.realtimeSinceStartup;
            role = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_TEST");
            share = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_SHARE");
            reads = new RoomJobs();
            StartCoroutine(Guard(Run()));
        }

        // Catch exceptions from nested iterators as well as the outer test, always producing a final result.
        IEnumerator Guard(IEnumerator run)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(run);
            while (stack.Count > 0 && !done)
            {
                object next = null;
                bool moved = false;
                try { moved = stack.Peek().MoveNext(); if (moved) next = stack.Peek().Current; }
                catch (Exception e) { Check(false, e.ToString()); }
                if (!moved) stack.Pop();
                else if (next is IEnumerator) stack.Push((IEnumerator)next);
                else yield return next;
            }
        }

        void Update()
        {
            Application.runInBackground = true;
            if (reads != null) reads.Pump();
            if (done) return;
            if (Time.realtimeSinceStartup - began > 480) Check(false, "overall timeout");   // run-rooms.ps1 waits 500 s
            if (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart)
                TestHarness.PulseMenu = true;
            // T-059 / T-171: the room page comes up before the game starts; once Run allows it, press its continue
            // (CsRoomPage.TestContinue: ready, then the creator starts once everyone is ready).
            if (autoContinue && OnlineRoomScreen.Visible && OnlineRoomScreen.Click("room.continue") && !roomScreenSeen) { roomScreenSeen = true; Plugin.Log.LogInfo("ROOMS room screen continued"); }
            if (UI.MainMenuVisible && GameController.Instance != null && GameController.Instance.IsLoadingGame)
            {
                UI.Menu.HideResumeScreen();
                UI.Menu.HideMenuScreen(true);
            }
        }

        bool roomScreenSeen;
        void OnDestroy() { if (reads != null) reads.Dispose(); }

        IEnumerator Wait(Func<bool> ready, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!done && !ready() && Time.realtimeSinceStartup < end) yield return null;
        }

        IEnumerator ReadInfo()
        {
            observed = null;
            reads.Add(delegate { return client.Info(auth); }, delegate(RoomReply r) { observed = r; });
            yield return Wait(() => observed != null, 18);
            if (!Check(observed != null && observed.Ok, "info readable")) yield break;
        }

        // The two clients replace share.dat atomically, but a read that lands inside the other client's replace can find
        // no file for a moment (host: FileNotFoundException at step 14, T-082). Retry briefly instead of failing the run.
        RoomWire.Fields Shared()
        {
            for (int attempt = 0; ; attempt++)
            {
                try { return SharedOnce(); }
                catch (IOException) { if (attempt >= 40) throw; System.Threading.Thread.Sleep(25); }
            }
        }

        RoomWire.Fields SharedOnce()
        {
            if (!File.Exists(share)) return new RoomWire.Fields();
            using (var file = new FileStream(share, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var bytes = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) > 0) bytes.Write(buffer, 0, count);
                return RoomWire.Fields.Decode(bytes.ToArray());
            }
        }

        void Publish(string key, string value)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    RoomWire.Fields fields = Shared();
                    fields.Set(key, value);
                    RoomWire.AtomicWrite(share, fields.Encode());
                    return;
                }
                catch (IOException) { if (attempt >= 40) throw; System.Threading.Thread.Sleep(25); }
            }
        }

        static string RowText(string key) { return L10n.T(key).Split('{')[0].Trim(); }

        // A native row shows the key's text (Chinese or English, possibly upper case).
        static bool RowIs(string row, string key)
        {
            return row.IndexOf(L10n.T(key, true), StringComparison.OrdinalIgnoreCase) >= 0
                || row.IndexOf(L10n.T(key, false), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        IEnumerator Press(string page, string key, string label = null)
        {
            bool pressed = false;
            yield return Wait(() => pressed = NativeModeMenu.LastPage == page
                && NativeModeMenu.TestRoomPress(label ?? RowText(key)), 15);
            if (done) yield break;
            string detail = "native page '" + page + "' row '" + L10n.T(key) + "'";
            if (!pressed) detail = "timeout waiting for " + detail + "; " + NativeModeMenu.TestRoomState()
                + " (" + string.Join(" | ", NativeModeMenu.TestRoomRows()) + ")";
            Check(pressed, detail);
        }

        bool Idle { get { return !(bool)Get(panel, "busy"); } }
        bool RoomReady { get { return Idle && Get(panel, "info") != null && panel.Session != null; } }
        static bool SlotsReady
        {
            get
            {
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                return ui != null && ui.IsVisible && ui.Active && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.SaveSlots;
            }
        }

        static string BarText(List<OriCoop.Ui.CsRoomBarItem> bar)
        {
            string text = "";
            foreach (OriCoop.Ui.CsRoomBarItem item in bar) text += (text.Length > 0 ? "/" : "") + item.Action + (item.Enabled ? "" : "(off)");
            return text;
        }

        static bool BarHas(List<OriCoop.Ui.CsRoomBarItem> bar, OriCoop.Ui.CsRoomAction action)
        {
            return bar.Exists(x => x.Action == action);
        }

        // Title -> 多人游玩 -> 自建服务器 -> 我的房间 through the real menu; waits for the room service's answer.
        IEnumerator OpenMyRooms(string when)
        {
            yield return Wait(() => NativeModeMenu.TitleItemsReady
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 100);
            if (!Check(NativeModeMenu.TitleItemsReady
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, "title ready (" + when + ")")) yield break;
            yield return NativeModeMenu.RoomsTestMenu(this);
            if (!Check(NativeModeMenu.LastPage == "servermode" && NativeModeMenu.LastLabels.Exists(x => x.Contains("我的房间")
                || x.IndexOf("My rooms", StringComparison.OrdinalIgnoreCase) >= 0), "自建服务器 page has the 我的房间 row (" + when + ")"))
                yield break;
            // Dispatch the actual 我的房间 row, not an independent panel initialization.
            typeof(NativeModeMenu).GetMethod("TitlePressed", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { NativeModeMenu.TestServerModeRoomsRow });   // the 自建服务器 page's 我的房间 row (T-045, T-095)
            panel = RoomsPanel.Instance;
            if (!Check(panel != null && NativeModeMenu.LastPage == "roomsmenu", "我的房间 opens the controller and its native page (" + when + ")")) yield break;
            yield return Wait(() => Idle && Array.Exists(NativeModeMenu.TestRoomRows(), x => RowIs(x, "myrooms.create")), 25);
            Check(Idle && (bool)Get(panel, "online"), "room service hello (" + when + ")");
        }

        IEnumerator Run()
        {
            // T-159: rooms play through the C-server (ORICOOP_NET_CSERVER, read by CServerConfig.Address).
            string cserver = Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER") ?? "";
            Uri url;
            string server = Environment.GetEnvironmentVariable("ORICOOP_ROOMS_SERVER");
            // T-155: both services come from the runner: the remote test slot (src\test-servers), or a loopback pair.
            // Never a remote machine's default ports (rooms 47021, C-server 47022): a test must not write its rooms
            // into a real server.
            string[] cs = cserver.Split(':'); int csPort = 0;
            bool given = Uri.TryCreate(server, UriKind.Absolute, out url) && url.Scheme == "http" && cs.Length == 2 && cs[0].Length > 0
                && int.TryParse(cs[1], out csPort);
            bool loopback = given && url.Host == "127.0.0.1" && cs[0] == "127.0.0.1";
            if (!Check(given && (loopback || (url.Port != 47021 && csPort != 47022)),
                "test services from the runner (loopback, or a test slot: not the default ports of a real server)")) yield break;
            // Setup: the player saved this C-server (服务器管理), as anyone who reached 自建服务器 with it selected has. The
            // client's config is a private copy of the test copy's (test-launch.ps1), so this never reaches another run.
            yield return Wait(() => Plugin.Instance != null, 30);
            if (!OriCoop.Authority.CServerConfig.Servers.Contains(cserver)) OriCoop.Authority.CServerConfig.AddServer(cserver);
            if (!Check(OriCoop.Authority.CServerConfig.Servers.Contains(cserver) && OriCoop.Authority.CServerConfig.Address == cserver,
                "test C-server " + cserver + " saved and selected")) yield break;

            yield return OpenMyRooms("first visit");
            if (done) yield break;
            // T-173 (owner): no 房间列表 while the player has no rooms.
            string[] menuRows = NativeModeMenu.TestRoomRows();
            if (!Check(NativeModeMenu.LastPage == "roomsmenu" && menuRows.Length == 3 && RowIs(menuRows[0], "myrooms.create")
                && RowIs(menuRows[1], "myrooms.join") && RowIs(menuRows[2], "common.back")
                && !Array.Exists(menuRows, x => RowIs(x, "myrooms.list")),
                "fresh 我的房间 menu is exactly 新建房间 / 加入房间 / 返回, no 房间列表 (" + string.Join(" | ", menuRows) + ")")) yield break;
            bool backed = false;
            yield return Wait(() => backed = NativeModeMenu.TestRoomBack(), 5);
            if (!Check(backed && NativeModeMenu.LastPage == "servermode", "我的房间 Back returns to the 自建服务器 page")) yield break;
            typeof(NativeModeMenu).GetMethod("TitlePressed", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { NativeModeMenu.TestServerModeRoomsRow });
            yield return Wait(() => Idle && Array.Exists(NativeModeMenu.TestRoomRows(), x => RowIs(x, "myrooms.create")), 25);
            if (!Check(NativeModeMenu.LastPage == "roomsmenu" && panel == RoomsPanel.Instance && Idle,
                "我的房间 reopens (" + NativeModeMenu.TestRoomState() + ")")) yield break;
            if (role == "host") yield return CreateRoom();
            else yield return JoinRoom(cserver);
            if (done) yield break;
            if (role == "host") yield return Host();
            else
            {
                yield return Join();
                if (!done) yield return RoomList();
            }
            if (!done) Finish(true);
        }

        // Host: 新建房间 -> save slots -> slot 1 -> the open room's page; the guest joins; ready + 开始 confirms the room.
        IEnumerator CreateRoom()
        {
            yield return Press("roomsmenu", "myrooms.create");
            if (done) yield break;
            yield return Wait(() => SlotsReady, 15);
            if (!Check(SlotsReady && NativeModeMenu.MyRoomCreating, "新建房间 opens the save slot screen (screen="
                + TitleScreenManager.CurrentScreen + ")")) yield break;
            yield return Wait(() => false, 0.8f);   // the slot list settles (TitleJoinTest does the same)
            if (!Check(SaveSlotsManager.SlotExists(PickedSlot), "co-op slot " + (PickedSlot + 1) + " has the fixture save ("
                + Rooms.RoomGame.PathFor(PickedSlot) + ")")) yield break;
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            ui.SetCurrentItem(PickedSlot);
            ui.UsedSaveSlotSelected();
            if (!Check(NativeModeMenu.LastSlotPick == "myroom-create-" + PickedSlot, "slot pick creates the room ("
                + NativeModeMenu.LastSlotPick + ")")) yield break;
            yield return Wait(() => panel.Session != null && panel.Session.Holder && panel.Game.Slot >= 0 && CsRoomPage.IsOpen
                && !string.IsNullOrEmpty(NetPlay.RoomCode) && RoomReady, 45);
            if (!Check(panel.Session != null && panel.Session.Holder && CsRoomPage.IsOpen && !string.IsNullOrEmpty(NetPlay.RoomCode) && RoomReady,
                "new room created and entered: room page open, holding the room's save (page=" + NativeModeMenu.LastPage
                + ", room page=" + CsRoomPage.IsOpen + ", code=" + NetPlay.RoomCode + ", " + NativeModeMenu.TestRoomState() + ")")) yield break;
            if (!Check(panel.Game.Slot == PickedSlot && SaveSlotsManager.CurrentSlotIndex == PickedSlot,
                "the room's save is hosted from the picked co-op slot (F19): slot " + panel.Game.Slot)) yield break;
            auth = panel.Auth;
            client = (RoomClient)Get(panel, "client");
            room = auth.Room;
            yield return ReadInfo();
            if (done) yield break;
            RoomWire.Fields f = observed.Fields;
            string code = f.GetString("code"), name = f.GetString("name");
            long no = f.GetLong("no", 0);
            save = f.GetString("save.0.id");
            if (!Check(f.GetString("open") == "1" && f.GetString("pending") == "1" && code.Length == 6,
                "new room is open and pending with a 6-character invite code (" + code + ")")) yield break;
            string[] words = name.Split(' ');
            bool ascii = name.Length > 0;
            foreach (char c in name) if (c < ' ' || c > '~') ascii = false;
            if (!Check(ascii && words.Length == 2 && Array.IndexOf(OriCoop.Ui.RoomNames.First, words[0]) >= 0
                && Array.IndexOf(OriCoop.Ui.RoomNames.Last, words[1]) >= 0, "room name is two English room-name words (" + name + ")")) yield break;
            // T-136: # * $ @ ^ [ ] < > are MessageBox markup; a room name must not contain any of them.
            if (!Check(!OriCoop.Ui.MarkupText.HasMarkup(name), "room name has no MessageBox markup (" + name + ")")) yield break;
            if (!Check(f.GetLong("seats", 0) == 4 && no >= 1001 && save.Length > 0,
                "4 seats, ROOM " + no + " (>= 1001), one save")) yield break;
            Publish("room", room);
            Publish("name", name);
            Publish("no", no.ToString());
            Publish("code", code);
            // The C-server room's code reaches the room service (live) while the creator is still on the room page.
            yield return Wait(() => postedLive == NetPlay.RoomCode, 20);
            if (!Check(postedLive == NetPlay.RoomCode, "live code posted from the room page (" + postedLive + ")")) yield break;
            Publish("live", NetPlay.RoomCode);
            OriCoop.Authority.CServerClient cs = null;
            yield return Wait(delegate
            {
                cs = OriCoop.Authority.CServerLink.Client;
                return cs != null && cs.SeatOnlineNow(2) && Shared().GetString("guestpage") == "1";
            }, 150);
            if (!Check(cs != null && cs.SeatOnlineNow(2) && Shared().GetString("guestpage") == "1",
                "guest online on the C-server (seat 2) and on its room page")) yield break;
            OriCoop.Ui.CsRoomModel model = CsRoomPage.Model;
            List<OriCoop.Ui.CsRoomBarItem> bar = model.Bar();
            if (!Check(CsRoomPage.IsOpen && model.Persistent && !model.Confirmed && model.IsManager
                && BarHas(bar, OriCoop.Ui.CsRoomAction.Start) && BarHas(bar, OriCoop.Ui.CsRoomAction.Ready)
                && BarHas(bar, OriCoop.Ui.CsRoomAction.Kick) && BarHas(bar, OriCoop.Ui.CsRoomAction.Leave)
                && !BarHas(bar, OriCoop.Ui.CsRoomAction.SwitchSave),
                "creator's room page: pending my room, bar 开始/准备/踢出/离开, no 换存档 (" + BarText(bar) + ", persistent="
                + model.Persistent + ", confirmed=" + model.Confirmed + ", in room=" + MyRooms.InRoom + ")")) yield break;
            autoContinue = true;   // ready, then 开始 once the guest is ready (CsRoomPage.TestContinue)
            yield return Wait(() => roomScreenSeen && !CsRoomPage.IsOpen, 60);
            if (!Check(roomScreenSeen && !CsRoomPage.IsOpen,
                "ready + 开始 through the room page (" + CsRoomPage.LastAction + ")")) yield break;
            float end = Time.realtimeSinceStartup + 15;
            do
            {
                yield return ReadInfo();
                if (done) yield break;
                if (observed.Fields.GetString("pending") == "0" && observed.Fields.GetString("code").Length == 0) break;
                yield return Wait(() => false, 0.5f);
            } while (Time.realtimeSinceStartup < end);
            if (!Check(observed.Fields.GetString("pending") == "0" && observed.Fields.GetString("code").Length == 0
                && observed.Fields.GetLong("code_expires", -1) == 0,
                "开始 confirmed the room: pending 0, invite code closed (code='" + observed.Fields.GetString("code") + "')")) yield break;
            Publish("confirmed", "1");
        }

        // Guest: 加入房间 page -> the test C-server and the shared code -> 加入 -> the room page; ready; reopen the code.
        IEnumerator JoinRoom(string cserver)
        {
            yield return Wait(() => Shared().GetString("code").Length > 0, 150);
            string code = Shared().GetString("code");
            if (!Check(code.Length == 6, "host published the invite code (" + code + ")")) yield break;
            yield return Press("roomsmenu", "myrooms.join");
            if (done) yield break;
            if (!Check(MyRoomsPages.IsOpen && MyRoomsPages.Current == MyRoomsPages.Page.Join, "加入房间 opens the join page ("
                + MyRoomsPages.TestState + ")")) yield break;
            if (!Check(MyRoomsPages.JoinModel.Servers.Contains(cserver), "join page lists the test C-server ("
                + string.Join(", ", MyRoomsPages.JoinModel.Servers.ToArray()) + ")")) yield break;
            // The page preselects the server chosen under 服务器管理 (the test C-server), before anything is picked by hand.
            if (!Check(MyRoomsPages.JoinModel.ServerText == cserver, "join page preselects the selected server ("
                + MyRoomsPages.JoinModel.ServerText + ")")) yield break;
            bool set = false;
            yield return Wait(() => set = MyRoomsPages.TestSetJoin(cserver, code), 5);
            if (!Check(set && MyRoomsPages.JoinModel.ServerText == cserver && MyRoomsPages.JoinModel.Code == code && MyRoomsPages.JoinModel.CanJoin,
                "join page: server " + MyRoomsPages.JoinModel.ServerText + ", code " + MyRoomsPages.JoinModel.Code)) yield break;
            bool pressed = false;
            yield return Wait(() => pressed = MyRoomsPages.TestPressJoin(), 10);
            if (!Check(pressed, "加入 pressed (" + MyRoomsPages.TestState + ")")) yield break;
            yield return Wait(() => RoomReady && !MyRoomsPages.IsOpen, 60);
            if (!Check(RoomReady && !MyRoomsPages.IsOpen, "joined by invite code: authenticated room (" + MyRoomsPages.TestState + ")")) yield break;
            auth = panel.Auth;
            client = (RoomClient)Get(panel, "client");
            room = auth.Room;
            save = panel.Info.GetString("save.0.id");
            if (!Check(room == Shared().GetString("room") && panel.Info.GetString("name") == Shared().GetString("name"),
                "joined the shared room (" + room + ", " + panel.Info.GetString("name") + ")")) yield break;
            yield return Wait(() => CsRoomPage.IsOpen, 60);
            if (!Check(CsRoomPage.IsOpen, "guest's room page open (page=" + NativeModeMenu.LastPage + ", net=" + NetPlay.CurrentPhase + ")")) yield break;
            yield return Wait(() => false, 0.5f);   // the page fills its model when it ticks / draws
            OriCoop.Ui.CsRoomModel model = CsRoomPage.Model;
            List<OriCoop.Ui.CsRoomBarItem> bar = model.Bar();
            if (!Check(model.Persistent && !model.Confirmed && !model.IsManager && BarHas(bar, OriCoop.Ui.CsRoomAction.Ready),
                "guest's room page: pending my room with 准备 (" + BarText(bar) + ", persistent=" + model.Persistent + ", confirmed="
                + model.Confirmed + ", in room=" + MyRooms.InRoom + ", pending=" + MyRooms.Pending + ")")) yield break;
            Publish("guestpage", "1");
            autoContinue = true;   // ready; the creator's 开始 takes this PC into the game (LobbyStart)
            yield return Wait(() => Shared().GetString("confirmed") == "1", 120);
            if (!Check(Shared().GetString("confirmed") == "1" && !CsRoomPage.IsOpen, "host's 开始 took the guest in (page closed), room confirmed"))
                yield break;
            // 开放邀请码 as the room page offers it: on a confirmed room (fresh info) with a free seat.
            yield return Wait(delegate { MyRooms.RefreshRoom(); return !MyRooms.Pending && MyRooms.CanOpenCode; }, 20);
            if (!Check(!MyRooms.Pending && MyRooms.CanOpenCode, "guest sees the room confirmed, 开放邀请码 allowed")) yield break;
            MyRooms.OpenCode();
            long left = 0;
            yield return Wait(delegate { left = MyRooms.CodeExpires - RoomWire.Now; return MyRooms.Code.Length == 6 && left > 0; }, 10);
            Check(MyRooms.Code.Length == 6 && left >= 280 && left <= 305,
                "member reopened the invite code: " + MyRooms.Code + ", " + left + " s left");
        }

        // Guest, after the session: 我的房间 now has 房间列表, and the list shows exactly this room.
        IEnumerator RoomList()
        {
            yield return Wait(() => Shared().GetString("released") == "1", 150);
            if (!Check(Shared().GetString("released") == "1", "host returned to the title, lease released")) yield break;
            if (NetPlay.LocalInGameplay()) GameController.Instance.RestartGame();
            yield return Wait(() => (panel.Session == null || !panel.Session.Active)
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 60);
            if (!Check((panel.Session == null || !panel.Session.Active)
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, "guest back on the title, room session ended")) yield break;
            yield return OpenMyRooms("after playing");
            if (done) yield break;
            string[] menuRows = NativeModeMenu.TestRoomRows();
            if (!Check(menuRows.Length == 4 && RowIs(menuRows[0], "myrooms.create") && RowIs(menuRows[1], "myrooms.join")
                && RowIs(menuRows[2], "myrooms.list") && RowIs(menuRows[3], "common.back"),
                "我的房间 menu now has 房间列表 (" + string.Join(" | ", menuRows) + ")")) yield break;
            yield return Press("roomsmenu", "myrooms.list");
            if (done) yield break;
            if (!Check(MyRoomsPages.IsOpen && MyRoomsPages.Current == MyRoomsPages.Page.List, "房间列表 page open (" + MyRoomsPages.TestState + ")")) yield break;
            OriCoop.Ui.MyRoomsListModel list = MyRoomsPages.ListModel;
            string rows = "";
            for (int i = 0; i < list.Rooms.Count; i++)
            {
                OriCoop.Ui.MyRoomsListRow r = list.Row(i);
                rows += (i > 0 ? " | " : "") + r.Id + " " + r.Name + " / " + r.Number + " / " + r.Count;
            }
            OriCoop.Ui.MyRoomsListRow row = list.Rooms.Count == 1 ? list.Row(0) : new OriCoop.Ui.MyRoomsListRow();
            if (!Check(list.Rooms.Count == 1 && row.Id == room && row.Name == Shared().GetString("name")
                && row.Number == "ROOM " + Shared().GetString("no") && row.Count == "2/4" && !row.Full,
                "房间列表 shows exactly this room: name, ROOM number, 2/4 (" + rows + ")")) yield break;
            bool backed = false;
            yield return Wait(() => backed = MyRoomsPages.TestBack(), 5);
            yield return Wait(() => NativeModeMenu.LastPage == "roomsmenu" && !MyRoomsPages.IsOpen, 5);
            Check(backed && !MyRoomsPages.IsOpen && NativeModeMenu.LastPage == "roomsmenu", "房间列表 Back returns to 我的房间");
        }

        // The gameplay half (the room was created, entered and started through the room page by CreateRoom).
        IEnumerator Host()
        {
            yield return Wait(() => NetPlay.LocalInGameplay(), 90);
            if (!Check(NetPlay.LocalInGameplay() && SaveSlotsManager.CurrentSlotIndex == panel.Game.Slot,
                "room slot loaded and gameplay running")) yield break;
            if (!Check(roomScreenSeen, "room screen shown before the game starts")) yield break;
            if (!Check(RoomsPanel.LastPlayedRoom() == room, "Enter records the room as last played (上次游玩)")) yield break;
            // live() is queued independently of game loading; allow the job to finish before asserting.
            float liveEnd = Time.realtimeSinceStartup + 20;
            do
            {
                yield return ReadInfo();
                if (done) yield break;
                if (postedLive == NetPlay.RoomCode) break;
                yield return new WaitForSeconds(0.25f);
            } while (Time.realtimeSinceStartup < liveEnd);
            if (!Check(observed.Fields.GetString("save.0.holder") == auth.Steam
                && postedLive == NetPlay.RoomCode,
                "live code published, holder is me")) yield break;
            Publish("live", NetPlay.RoomCode);
            yield return Wait(() => NetPlay.PartnerHere && Shared().GetString("playing") == "1", 100);
            if (!Check(NetPlay.PartnerHere && Shared().GetString("playing") == "1",
                "partner connected and in gameplay")) yield break;
            yield return ReadInfo();
            if (done) yield break;
            long before = observed.Fields.GetLong("save.0.version", 0);
            yield return Wait(() => GameController.Instance.SaveGameController.CanPerformSave(), 15);
            if (!Check(GameController.Instance.SaveGameController.CanPerformSave(), "game ready to save")) yield break;
            GameController.Instance.SaveGameController.PerformSave();
            yield return Wait(() => panel.Session.Version > before, 25);
            yield return ReadInfo();
            if (done) yield break;
            upload = observed.Fields.GetLong("save.0.version", 0);
            if (!Check(upload == before + 1 && panel.Session.Version == upload,
                "PerformSave upload version +1: " + upload)) yield break;
            Publish("upload", upload.ToString());
            yield return Wait(() => Shared().GetString("backup") == upload.ToString(), 90);
            if (!Check(Shared().GetString("backup") == upload.ToString(), "guest acknowledged upload backup"))
                yield break;
            // T-130: the holder adopts the partner's progress (the choice at a co-op save divergence). SaveSync
            // restarts the game to load it; that restart is not a return to the title, so the room session must stay
            // and the next save must still upload to the room.
            SaveSync.HostAdopt(GameController.Instance.SaveGameController.SaveToBytes());
            if (!Check(SaveSync.HostAdopting, "host adopt started")) yield break;
            bool restarted = false;
            yield return Wait(delegate { restarted |= SaveSync.RestartingForLoad; return restarted && !SaveSync.HostAdopting; }, 90);
            yield return Wait(() => NetPlay.LocalInGameplay(), 60);
            if (!Check(restarted && !SaveSync.HostAdopting && NetPlay.LocalInGameplay(),
                "adopted progress loaded through the restart")) yield break;
            if (!Check(panel.Session.Active && panel.Session.Holder && SaveSlotsManager.CurrentSlotIndex == panel.Game.Slot,
                "room session kept after adopting partner progress (T-130)")) yield break;
            long adopted = panel.Session.Version;
            yield return Wait(() => GameController.Instance.SaveGameController.CanPerformSave(), 30);
            if (!Check(GameController.Instance.SaveGameController.CanPerformSave(), "game ready to save after adopting")) yield break;
            GameController.Instance.SaveGameController.PerformSave();
            yield return Wait(() => panel.Session.Version > adopted, 25);
            yield return ReadInfo();
            if (done) yield break;
            if (!Check(panel.Session.Version > adopted && observed.Fields.GetLong("save.0.version", 0) == panel.Session.Version
                && observed.Fields.GetString("save.0.holder") == auth.Steam,
                "save after adopting uploads to the room, lease still held: v" + panel.Session.Version)) yield break;
            upload = panel.Session.Version;
            GameController.Instance.RestartGame();
            yield return Wait(() => !panel.Session.Active
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu, 45);
            yield return ReadInfo();
            if (done) yield break;
            Check(!panel.Session.Active
                && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu
                && observed.Fields.GetLong("save.0.version", 0) == upload + 1
                && observed.Fields.GetString("save.0.holder").Length == 0,
                "return title snapshot version +1 and lease free");
            if (!done) Publish("released", "1");   // the guest's 房间列表 check waits for this
        }

        IEnumerator Join()
        {
            yield return Wait(() => NetPlay.PartnerHere && NetPlay.LocalInGameplay() && Shared().GetString("live").Length > 0, 100);
            string target = Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER") + "|"
                + Shared().GetString("live");
            if (!Check(joinedTarget == target && panel.Session.Active && !panel.Session.Holder && !NetPlay.IsHost
                && NetPlay.PartnerHere && NetPlay.LocalInGameplay() && NetPlay.RoomCode == Shared().GetString("live"),
                "enter join with host live code, connected in gameplay")) yield break;
            Publish("playing", "1");
            var store = (RoomLocalStore)Get(panel, "store");
            long backup = 0;
            yield return Wait(delegate
            {
                upload = Shared().GetLong("upload", 0);
                string summary;
                byte[] data = store.LoadLatest(room, save, out backup, out summary);
                return upload > 0 && data != null && data.Length > 0 && backup == upload;
            }, 90);
            if (!Check(upload > 0 && backup == upload, "local backup has host upload version " + backup)) yield break;
            Publish("backup", backup.ToString());
        }
    }
}
