using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop.Rooms
{
    // Room jobs and session lifetime are independent of the native menu's lifetime.
    public sealed class RoomsPanel : MonoBehaviour
    {
        public static RoomsPanel Instance;
        public RoomSession Session;
        public RoomGame Game;
        RoomJobs jobs;
        RoomClient client;
        RoomLocalStore store;
        RoomAuth auth;
        List<RoomBackupInfo> rooms;
        RoomWire.Fields info;
        readonly Dictionary<string, RoomWire.Fields> refreshed = new Dictionary<string, RoomWire.Fields>();
        string steam, nick, server, gameServer, serviceName;
        string screen = "list", message = "", roomName = "";
        Action<string> passwordDone;
        string passwordBack;
        bool visible, busy, online, wasNetEnabled, openRooms;
        readonly RoomReadyGate ready = new RoomReadyGate();   // waits for the first refresh (review F23)
        readonly RoomActionLatch action = new RoomActionLatch();   // the running 我的房间 create/join/enter
        long serverOffset;   // room service clock - local clock, from the latest info / open_code (review F28)
        RoomConfirmRetry confirm;   // 开始 pressed, confirm not on the server yet (review F24)
        volatile int revision;      // also read on the job thread (a queued request of an older revision is skipped, T-200)
        // T-200: the first refresh (hello + the rooms' info) may keep the page on "connecting" for this long at most.
        const double RefreshLimitSeconds = 8;
        readonly RoomBusyWatch refreshWatch = new RoomBusyWatch(RefreshLimitSeconds);
        bool offlineRetry;          // the room service did not answer: the 我的房间 page offers "try again"
        byte[] copyBytes;
        int overwrite = -1;

        internal string Screen { get { return screen; } }
        internal string Message { get { return message; } }
        internal bool Busy { get { return busy; } }
        internal bool Online { get { return online; } }
        internal bool Visible { get { return visible; } }
        internal bool Available { get { return steam != "0" && store != null; } }
        internal string Server { get { return server; } }
        internal string ServiceName { get { return serviceName; } }
        internal List<RoomBackupInfo> Rooms { get { return rooms; } }
        internal RoomWire.Fields Info { get { return info; } }
        internal Dictionary<string, RoomWire.Fields> Refreshed { get { return refreshed; } }
        internal int Overwrite { get { return overwrite; } }
        internal string AutoName { get { return roomName; } }
        internal string Steam { get { return steam; } }
        internal string GameServer { get { return gameServer; } }
        internal RoomAuth Auth { get { return auth; } }
        // T-200: the selected server's room service did not answer (hello failed or took too long); trying again may help.
        internal bool OfflineRetry { get { return offlineRetry && !online && !busy; } }
        // T-200: rooms this PC has records of on other room services (the page shows the selected server's rooms only).
        internal int OtherServerRooms
        {
            get
            {
                int count = 0;
                if (rooms != null)
                    foreach (RoomBackupInfo room in rooms)
                        if (room.Server.Length > 0 && room.Server != server) count++;
                return count;
            }
        }

        static void EnsureInstance()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("OriCoop_Rooms");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<RoomsPanel>();
            Instance.jobs = new RoomJobs();
            Instance.Game = new RoomGame();
            // RoomGame.Host / Join: the room session is hosting or joining, so a waiting 我的房间 action has succeeded.
            Instance.Game.Starting = delegate { Instance.busy = false; Instance.visible = false; Instance.action.Finish(null); };
            Instance.Game.PickCopySlot = delegate(byte[] bytes)
            {
                Instance.copyBytes = bytes;
                Instance.overwrite = -1;
                Instance.ReadSinglePlayerSlots();
                Instance.Show("slots");
            };
            NetPlay.SessionEnded += delegate { if (Instance.Session != null) Instance.Session.Leave(); };
        }

        static string RoomsDir { get { return TestValue("ORICOOP_ROOMS_DIR", Path.GetFullPath(Path.Combine(Paths.ConfigPath, "../OriCoop/rooms"))); } }

        // T-095: the room to show on the 上次游玩 row (null = no record yet).
        internal static string LastPlayedRoom()
        {
            try { string id, server; return new RoomLocalStore(RoomsDir).LoadLastRoom(out id, out server) ? id : null; }
            catch (Exception) { return null; }
        }

        string autoOpen;

        public static void Open() { Open(null); }

        // autoRoom: open that room as soon as the room service answers (the 上次游玩 row).
        public static void Open(string autoRoom)
        {
            EnsureInstance();
            RoomsPanel p = Instance;
            if (p.Session != null && p.Session.Active) return;
            p.revision++;
            p.busy = false;
            p.openRooms = false;
            p.visible = true;
            p.online = false;
            p.offlineRetry = false;
            p.serverOffset = 0;
            p.steam = TestValue("ORICOOP_TEST_STEAMID", SteamTransport.LocalSteamId.ToString());
            p.nick = TestValue("ORICOOP_TEST_NICK", SteamTransport.LocalPersonaName);
            p.serviceName = L10n.T("rooms.service");
            p.Show("list");
            p.autoOpen = autoRoom;
            // Waiters queued before this reopen are answered by this refresh, or right away on the early returns.
            if (p.steam == "0") { p.message = L10n.T("rooms.steam_required"); p.FinishRefresh(); return; }
            p.store = new RoomLocalStore(RoomsDir);
            p.rooms = p.store.ListRooms();
            p.refreshed.Clear();
            p.confirm = p.NewConfirmRetry();
            // T-159: rooms play through the selected C-server (was the relay); the room service runs on the same host.
            p.gameServer = (OriCoop.Authority.CServerConfig.Address ?? "").Trim();
            p.server = Plugin.Instance.Config.Bind("Rooms", "Server", "", "Room service URL; empty uses the selected C-server's host, port 47021").Value.Trim();
            p.server = TestValue("ORICOOP_ROOMS_SERVER", p.server);
            if (p.server.Length == 0)
            {
                Uri address;
                if (p.gameServer.Length == 0 || !Uri.TryCreate("http://" + p.gameServer, UriKind.Absolute, out address))
                { p.message = ErrorText("network"); p.FinishRefresh(); return; }
                p.server = "http://" + address.Host + ":47021/";
            }
            // The 上次游玩 room of another room service cannot be opened through this one (review F21).
            if (p.autoOpen != null)
                foreach (RoomBackupInfo known in p.rooms)
                    if (known.Id == p.autoOpen && known.Server.Length > 0 && known.Server != p.server) p.autoOpen = null;
            p.client = new RoomClient(p.server);
            p.confirm.Client = p.client;
            p.ready.Begin();
            p.refreshWatch.Restart();
            p.Request(p.client.Hello, delegate(RoomReply reply)
            {
                p.online = reply.Ok;
                if (!p.online)
                {
                    Plugin.Log.LogInfo("my rooms: the room service did not answer (" + reply.Error + ")");
                    p.message = L10n.T("rooms.offline"); p.offlineRetry = true; p.FinishRefresh();
                }
                else if (p.autoOpen != null)
                {
                    string room = p.autoOpen; p.autoOpen = null;
                    p.FinishRefresh();
                    p.OpenRoom(room);
                }
                else p.RefreshNext(p.rooms, 0);
            });
        }

        internal static string TestValue(string name, string fallback)
        {
            if (Environment.GetEnvironmentVariable("ORICOOP_TEST") != "1") return fallback;
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        void Show(string next) { screen = next; message = ""; }

        internal void UseOpenRooms()
        {
            openRooms = true;
            if (Session != null) Session.Password = null;
        }

        internal void WhenReady(Action<string> done)
        {
            ready.Wait(done, ReadyState);
        }

        string ReadyState()
        {
            return online ? null : ErrorText(steam == "0" ? "steam_required" : "network");
        }

        void FinishRefresh()
        {
            ready.End(ReadyState);
        }

        // 我的房间 (MyRooms.Cancel): the running create/join/enter is abandoned. Its done callback never runs, replies
        // still on the way are dropped (revision), and a room session that has not started hosting or joining yet is
        // left, so nothing enters the room afterwards.
        internal void CancelMyRooms()
        {
            bool pending = action.Cancel();
            ready.Drop();
            revision++;
            busy = false;
            if (pending && Session != null && Session.Active) Session.Cancel();
        }

        void RefreshNext(List<RoomBackupInfo> batch, int index)
        {
            if (index >= batch.Count) { FinishRefresh(); return; }
            RoomBackupInfo room = batch[index];
            if (room.Server != server) { RefreshNext(batch, index + 1); return; }
            SetRoom(room.Id);
            RoomAuth current = auth;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                ApplyRoomInfo(current, r);
                RefreshNext(batch, index + 1);
            });
        }

        void SetRoom(string room)
        {
            auth = new RoomAuth { Room = room, Steam = steam, Token = store.LoadToken(room) };
            Session = new RoomSession(client, jobs, store, Game, auth, gameServer);
            Session.Online = online;
            double poll;
            if (double.TryParse(TestValue("ORICOOP_ROOMS_POLL", ""),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out poll)
                && poll > 0) Session.BackupSeconds = poll;
            if (!openRooms) Session.Password = AskPassword;
            Session.Copied = delegate { busy = false; };
            RoomSession session = Session;
            session.Error = delegate(string error)
            {
                if (session != Session) return;
                busy = false;
                message = ErrorText(error);
                // A 我的房间 action still waiting for the session to start: its page shows the error, the hidden native
                // menu stays where it is (enter / download failures, review F25 / contract), and the session ends, so
                // it can neither enter the room after the error was shown nor stay active as a slotless holder.
                // Only the current session may fail the waiting action: a replaced session's late reply (an upload or
                // live call still in flight) must not report the new room's enter as failed.
                if (MyRoomsRules.FailBeforeStart(action, session, message)) return;
                if (!RoomSession.MutateRoomFailures && session.Active && Game.InGameplay)
                {
                    Game.Message(L10n.F("rooms.sync_failed", message));
                    return;
                }
                visible = true;
                NativeModeMenu.ShowRoomError();
            };
        }

        void AskPassword(Action<string> done)
        {
            busy = false;
            passwordBack = screen;
            passwordDone = done;
            Show("password");
        }

        // T-200: a request whose revision is over when its turn comes on the job thread (the page was left, reopened or
        // cancelled since) is not sent at all: on an unreachable server every abandoned request would otherwise still
        // wait for its whole timeout, one after the other, before the next visit's hello gets its turn.
        void Request(Func<RoomReply> work, Action<RoomReply> done)
        {
            busy = true;
            int request = revision;
            jobs.Add(work, delegate(RoomReply reply)
            {
                if (request != revision) return;
                busy = false;
                done(reply);
            }, delegate { return request == revision; });
        }

        void RequestAuth(Func<RoomReply> work, Action<RoomReply> done)
        {
            if (openRooms) { Request(work, done); return; }
            busy = true;
            int request = revision;
            Session.Call(work, delegate(RoomReply reply)
            {
                if (request != revision) return;
                busy = false;
                done(reply);
            }, delegate { return request == revision; });
        }

        // T-200: the first refresh took the whole limit (the worker is still waiting for an answer, or for an older
        // request's timeout): stop waiting. Without a hello answer the page shows "cannot connect" with a retry; with
        // one, it goes on with the rooms' stored details.
        void RefreshGaveUp()
        {
            Plugin.Log.LogInfo("my rooms: no answer within " + RefreshLimitSeconds + " s (" + (online ? "room details" : "hello") + "), not waiting any longer");
            revision++;
            busy = false;
            if (!online) { message = L10n.T("rooms.offline"); offlineRetry = true; }
            FinishRefresh();
        }

        void ApplyRoomInfo(RoomAuth queried, RoomReply reply)
        {
            if (reply.Ok) { SaveInfo(reply); return; }
            if (!RoomSession.MutateRoomFailures && reply.Error == "no_room")
            {
                ForgetRoom(queried.Room, true);
                if (screen == "room") Show("list");
            }
            message = ErrorText(reply.Error);
        }

        void SaveInfo(RoomReply reply)
        {
            RoomBackupInfo data = RoomBackupInfo.FromInfoReply(reply);
            data.Id = auth.Room;
            data.Server = server;
            data.Steam = steam;
            store.SaveRoom(data);
            rooms = store.ListRooms();
            info = reply.Fields;
            // T-173: an info reply that left before a rename still on its way has the old name: keep the new one.
            if (renaming.Length > 0 && renamingRoom == auth.Room && renamingRevision == revision) info.Set("name", renaming);
            refreshed[auth.Room] = info;
            NoteServerClock(info);
            // Confirmed on the server: the marker goes; still pending after 开始 here: the confirm goes out again (F24).
            if (confirm != null) confirm.SawInfo(auth, info);
        }

        // Room service clock - local clock (review F28): code_expires is the server's time, so the countdown must not
        // depend on this PC's clock.
        void NoteServerClock(RoomWire.Fields f)
        {
            if (f.Has("now")) serverOffset = f.GetLong("now", RoomWire.Now) - RoomWire.Now;
        }

        internal long ServerOffset { get { return serverOffset; } }

        internal bool ConfirmWanted(string room)
        {
            return confirm != null && confirm.Wanted(room);
        }

        RoomConfirmRetry NewConfirmRetry()
        {
            RoomConfirmRetry retry = new RoomConfirmRetry(jobs, store);
            retry.Log = delegate(string text) { Plugin.Log.LogWarning("my rooms: " + text); };
            retry.Failed = delegate(string room, string error)
            {
                Plugin.Log.LogWarning("my rooms: confirm (retried later): " + ErrorText(error));
            };
            retry.Confirmed = MarkConfirmed;
            // The in-game check's info: the current room's info is updated (its SaveInfo resends or clears);
            // another room's only feeds the retry.
            retry.Checked = delegate(RoomAuth current, RoomReply r)
            {
                if (auth != null && auth.Room == current.Room && confirm == retry) SaveInfo(r);
                else retry.SawInfo(current, r.Fields);
            };
            return retry;
        }

        void MarkConfirmed(string room)
        {
            RoomWire.Fields known;
            if (refreshed.TryGetValue(room, out known)) ClearPending(known);
            if (auth != null && auth.Room == room && info != null) ClearPending(info);
        }

        static void ClearPending(RoomWire.Fields f)
        {
            f.Set("pending", 0);
            f.Set("code", "");
            f.Set("code_expires", 0);
        }

        internal void OpenRoom(string room) { OpenRoom(room, false, null); }

        // myRooms: a 我的房间 flow, which draws its own pages over the hidden native 我的房间 menu, so the panel's screen
        // (which moves that hidden menu) stays as it is (review F25).
        void OpenRoom(string room, bool myRooms, Action<string> done)
        {
            SetRoom(room);
            Show(MyRoomsRules.ScreenAfterOpen(screen, myRooms));
            info = null;
            RoomAuth current = auth;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                ApplyRoomInfo(current, r);
                if (done != null) done(r.Ok ? null : message);
            });
        }

        // The running 我的房间 action failed before its room session started: its page gets the reason.
        void MyRoomsFail(string text)
        {
            message = text;
            action.Finish(text);
        }

        // 我的房间 actions (create / join / enter): done(null) once the room session hosts or joins (Game.Starting),
        // done(error) when anything before that fails (Session.Error included); CancelMyRooms drops them silently.
        internal void CreateOpen(int slot, string name, Action<string> done)
        {
            int mine = action.Begin(done);
            Saves.CoopSaveFolder.Enter();
            string path = RoomGame.PathFor(slot);
            if (!File.Exists(path)) { MyRoomsFail(ErrorText("no_save")); return; }
            byte[] bytes = File.ReadAllBytes(path);
            string summary = RoomGame.Summary(bytes);
            string saveName = L10n.F("rooms.slot_name", slot + 1);
            WhenReady(delegate(string error)
            {
                if (!action.Current(mine)) return;
                if (error != null) { MyRoomsFail(error); return; }
                message = "";
                // Open rooms get 4 seats, like the C-server room they play in (review F41).
                Request(delegate { return client.Create(steam, nick, name, bytes, summary, saveName, 4); }, delegate(RoomReply r)
                {
                    if (!action.Current(mine)) return;
                    if (r.Ok)
                    {
                        string room = r.Fields.GetString("room"), save = r.Fields.GetString("saveid");
                        long version = r.Fields.GetLong("version", 1);
                        store.StoreVersion(room, save, version, bytes, summary);
                        // Link the picked co-op slot to the new room, so entering it hosts from this slot instead of
                        // copying the save into another empty slot (review F19).
                        try { Game.UpdateVersion(slot, room, save, version); }
                        catch (Exception e) { Plugin.Log.LogWarning("my rooms: could not link slot " + (slot + 1) + ": " + e.Message); }
                    }
                    OpenLoginResult(r, mine);
                });
            });
        }

        internal void JoinOpen(string code, Action<string> done)
        {
            int mine = action.Begin(done);
            message = "";
            Request(delegate { return client.Join(code, steam, nick); }, delegate(RoomReply r)
            {
                if (action.Current(mine)) OpenLoginResult(r, mine);
            });
        }

        void OpenLoginResult(RoomReply reply, int mine)
        {
            // already_member: an older room service without fresh tokens for members (review F22); try the stored one.
            if (!reply.Ok && reply.Error != "already_member") { MyRoomsFail(ErrorText(reply.Error)); return; }
            string room = reply.Fields.GetString("room");
            if (reply.Ok) store.SaveToken(room, reply.Fields.GetString("token"));
            EnterOpen(room, mine);
        }

        internal void EnterOpenRoom(string room, Action<string> done)
        {
            EnterOpen(room, action.Begin(done));
        }

        void EnterOpen(string room, int mine)
        {
            OpenRoom(room, true, delegate(string error)
            {
                if (!action.Current(mine)) return;
                if (error != null) { MyRoomsFail(error); return; }
                if (info.GetLong("save.count", 0) == 0) { MyRoomsFail(ErrorText("no_save")); return; }
                // done(null) follows from Game.Starting, done(error) from Session.Error (enter, download, slot).
                Enter(info.GetString("save.0.id"));
            });
        }

        // Delete (creator) or quit a 我的房间 room. The role comes from fresh info of that room, or from its stored
        // member list when info fails (never from another room's info, review F27); a room the server no longer
        // knows for us (no_room / bad_token / not_member) only loses its local record (review F20).
        internal void DeleteOpenRoom(string room, Action<string> done) { DeleteOpenRoom(room, false, done); }

        // onlyPending (abandoning before 开始): delete only while fresh info says the room is still pending.
        void DeleteOpenRoom(string room, bool onlyPending, Action<string> done)
        {
            if (Session != null && Session.Active)
            {
                if (auth == null || auth.Room != room) { message = ErrorText("busy"); done(message); return; }
                Session.Cancel();
            }
            RoomBackupInfo stored = null;
            if (rooms != null)
                foreach (RoomBackupInfo known in rooms)
                    if (known.Id == room) stored = known;
            SetRoom(room);
            info = null;
            message = "";
            RoomAuth current = auth;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                if (MyRoomsRules.RoomGone(r.Error)) { ForgetRoom(current.Room); done(null); return; }
                if (r.Ok) SaveInfo(r);
                if (onlyPending && (!r.Ok || r.Fields.GetString("pending") != "1"))
                {
                    done(r.Ok ? null : ErrorText(r.Error));
                    return;
                }
                bool creator = MyRoomsRules.DeleteAsCreator(r.Ok ? r.Fields : null, stored, steam);
                RequestAuth(delegate { return creator ? client.DeleteRoom(current) : client.Quit(current); }, delegate(RoomReply d)
                {
                    if (!d.Ok && !MyRoomsRules.RoomGone(d.Error)) { message = ErrorText(d.Error); done(message); return; }
                    ForgetRoom(current.Room);
                    done(null);
                });
            });
        }

        void ForgetRoom(string room, bool keepBackups = false)
        {
            if (keepBackups) store.RemoveListing(room);
            else store.DeleteRoom(room);
            refreshed.Remove(room);
            if (confirm != null) confirm.Forget(room);
            rooms = store.ListRooms();
            if (auth != null && auth.Room == room)
            {
                info = null;
                if (!keepBackups || Session == null || !Session.Active) { auth = null; Session = null; }
            }
            message = "";
        }

        // 我的房间 abandoned before 开始 by its creator (the room page's 离开房间): the pending room is deleted. Not
        // when this PC already pressed 开始 (the confirm is only waiting for a retry, review F24).
        internal void AbandonPendingRoom()
        {
            if (auth == null || info == null || info.GetString("open") != "1" || info.GetString("pending") != "1"
                || info.GetString("role") != "1" || ConfirmWanted(auth.Room)) return;
            DeleteOpenRoom(auth.Room, true, delegate(string error)
            {
                if (error != null) Plugin.Log.LogWarning("my rooms: abandon: " + error);
            });
        }

        internal void RefreshOpenRoom()
        {
            RoomAuth current = auth;
            if (current == null) return;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                if (auth == null || auth.Room != current.Room) return;
                // SaveInfo also resends a confirm that 开始 here did not get through (review F24).
                ApplyRoomInfo(current, r);
            });
        }

        // 开始 by the creator: the confirm is remembered and resent until the server has it (review F24).
        internal void ConfirmOpenRoom()
        {
            if (auth != null && confirm != null) confirm.Pressed(auth);
        }

        internal void OpenInviteCode()
        {
            RoomAuth current = auth;
            if (current == null) return;
            message = "";
            RequestAuth(delegate { return client.OpenCode(current); }, delegate(RoomReply r)
            {
                if (!r.Ok) { message = ErrorText(r.Error); return; }
                NoteServerClock(r.Fields);
                if (auth == null || auth.Room != current.Room || info == null) return;
                info.Set("code", r.Fields.GetString("code"));
                info.Set("code_expires", info.GetString("pending") == "1" ? 0 : r.Fields.GetLong("expires", 0));
            });
        }

        // T-173 (room-v2-a, [E] 改名 on the room page): rename the current open room on the room service (any member may;
        // the page lets only the C-server room's manager). The page shows the new name at once; fresh info follows.
        // Until the rename's reply is in, an info reply (a refresh queued before the rename) carries the old name: the
        // new one is kept over it (SaveInfo), so the page does not flip back. done(ok) runs with the rename's reply.
        internal void RenameOpenRoom(string name, Action<bool> done)
        {
            RoomAuth current = auth;
            if (current == null || info == null || string.IsNullOrEmpty(name)) { if (done != null) done(false); return; }
            message = "";
            info.Set("name", name);
            renaming = name; renamingRoom = current.Room; renamingRevision = revision;
            RequestAuth(delegate { return client.RenameRoom(current, name); }, delegate(RoomReply r)
            {
                if (renamingRoom == current.Room && renaming == name) renaming = "";   // not a later rename still on its way
                if (!r.Ok) { message = ErrorText(r.Error); Plugin.Log.LogWarning("my rooms: rename refused: " + message); }
                else Plugin.Log.LogInfo("my rooms: room renamed");
                if (done != null) done(r.Ok);
                if (auth != null && auth.Room == current.Room) RefreshOpenRoom();   // the stored name, or back to the old one
            });
        }

        string renaming = "", renamingRoom = "";   // a rename on its way to the room service (RenameOpenRoom)
        int renamingRevision;

        void LoginResult(RoomReply reply)
        {
            if (!reply.Ok)
            {
                if (reply.Error == "already_member")
                {
                    string existingRoom = reply.Fields.GetString("room");
                    AskPassword(delegate(string pw)
                    {
                        if (pw == null) return;
                        Request(delegate { return client.Rebind(existingRoom, pw, steam); }, delegate(RoomReply r)
                        {
                            if (!r.Ok) { message = ErrorText(r.Error); return; }
                            store.SaveToken(existingRoom, r.Fields.GetString("token"));
                            OpenRoom(existingRoom);
                        });
                    });
                }
                else message = ErrorText(reply.Error);
                return;
            }
            string room = reply.Fields.GetString("room");
            store.SaveToken(room, reply.Fields.GetString("token"));
            OpenRoom(room);
        }

        internal readonly string[] SinglePlayerSummaries = new string[SaveGameController.MAX_SAVES];

        void ReadSinglePlayerSlots()
        {
            Array.Clear(SinglePlayerSummaries, 0, SinglePlayerSummaries.Length);
            foreach (int slot in Saves.CoopSaveFolder.SinglePlayerSlotsWithSaves())
                SinglePlayerSummaries[slot] = RoomGame.Summary(File.ReadAllBytes(Saves.CoopSaveFolder.SinglePlayerPath(slot)));
        }

        internal void BeginCreate()
        {
            int count = 0;
            foreach (RoomBackupInfo room in rooms) if (room.Steam == steam) count++;
            int number = count + 1;
            while (true)
            {
                roomName = L10n.F("rooms.auto_name", nick, number);
                bool collision = false;
                foreach (RoomBackupInfo room in rooms) if (room.Name == roomName) { collision = true; break; }
                if (!collision) break;
                number++;
            }
            ReadSinglePlayerSlots();
            Show("create");
        }

        internal void BeginJoin() { Show("join"); }

        internal void CreateWithSlot(int slot, string password)
        {
            if (!NumericPassword(password)) { message = ErrorText("bad_request"); return; }
            byte[] bytes = File.ReadAllBytes(Saves.CoopSaveFolder.SinglePlayerPath(slot));
            string summary = RoomGame.Summary(bytes);
            string enteredName = roomName;
            Request(delegate
            {
                return client.Create(steam, nick, enteredName, password, bytes, summary, L10n.F("rooms.slot_name", slot + 1));
            }, delegate(RoomReply r)
            {
                if (r.Ok) store.StoreVersion(r.Fields.GetString("room"), r.Fields.GetString("saveid"),
                    r.Fields.GetLong("version", 1), bytes, summary);
                LoginResult(r);
            });
        }

        static bool NumericPassword(string password)
        {
            if (password.Length < 4 || password.Length > 8) return false;
            foreach (char c in password) if (c < '0' || c > '9') return false;
            return true;
        }

        internal void JoinWithCode(string code, string password)
        {
            if (code.Length != 6 || !NumericPassword(password)) { message = ErrorText("bad_request"); return; }
            Request(delegate { return client.Join(code.ToUpperInvariant(), password, steam, nick); }, LoginResult);
        }

        internal void ConfirmPassword(string password)
        {
            if (!NumericPassword(password)) { message = ErrorText("bad_request"); return; }
            Action<string> done = passwordDone;
            passwordDone = null;
            Show(passwordBack);
            busy = true;
            done(password);
        }

        internal void CopySave(string id) { busy = true; Session.Copy(id); }
        internal void CopyLocal(string room, string id) { SetRoom(room); Session.Online = false; CopySave(id); }
        internal void Enter(string save)
        {
            try { store.SaveLastRoom(auth.Room, server); } catch (Exception e) { Plugin.Log.LogWarning("rooms: could not record the last room: " + e.Message); }
            busy = true; Session.Enter(save);
        }

        internal void PickSlot(int slot)
        {
            if (overwrite != slot) { overwrite = slot; return; }
            RoomGame.WriteSingle(slot, copyBytes);
            overwrite = -1;
            Show("list");
            message = L10n.T("rooms.copied");
        }

        internal void Back()
        {
            revision++;
            busy = false;
            // The revision drops the replies a running refresh chain waits for: answer its waiters now, or every later
            // 我的房间 action would queue forever (review F23). A running 我的房间 action is abandoned with the page.
            action.Cancel();
            if (ready.Running) FinishRefresh();
            if (Session != null) Session.Cancel();
            if (screen == "list") { visible = false; return; }
            if (screen == "password")
            {
                Action<string> cancel = passwordDone;
                passwordDone = null;
                cancel(null);
                Show(passwordBack);
                return;
            }
            overwrite = -1;
            Show("list");
            rooms = store == null ? null : store.ListRooms();
        }

        void Update()
        {
            jobs.Pump();
            if (refreshWatch.Expired(busy && ready.Running, Time.realtimeSinceStartup)) RefreshGaveUp();
            if (Session != null) Session.Tick(Time.realtimeSinceStartup);
            // In game no room page refreshes: a confirm 开始 did not get through is checked here (review F24).
            if (confirm != null && Session != null) confirm.Tick(Time.realtimeSinceStartup, Session.Active, auth, info);
            if (wasNetEnabled && !NetPlay.Enabled && Session != null) Session.Leave();
            wasNetEnabled = NetPlay.Enabled;
        }

        internal string MemberName(string id)
        {
            for (int i = 0; i < info.GetLong("m.count", 0); i++)
                if (info.GetString("m." + i + ".steam") == id) return info.GetString("m." + i + ".nick");
            return "";
        }

        internal static string TimeText(long seconds)
        {
            return seconds == 0 ? "" : new DateTime(1970, 1, 1).AddSeconds(seconds).ToLocalTime().ToString("g");
        }

        internal static string FormatSummary(string summary, bool zh)
        {
            return summary.Replace("lv=", L10n.T("rooms.level", zh)).Replace("pct=", L10n.T("rooms.completion", zh))
                .Replace("area=", L10n.T("rooms.area", zh)).Replace("secs=", L10n.T("rooms.seconds", zh)).Replace(";", "  ");
        }

        void OnApplicationQuit()
        {
            if (Session == null || !Session.Active) return;
            client.TimeoutMs = client.TransferTimeoutMs = 2000;
            Session.Quit(5000);
            jobs.Dispose();
        }

        public static string ErrorText(string error)
        {
            switch (error)
            {
                case "network": return L10n.T("rooms.err.network");
                case "steam_required": return L10n.T("rooms.steam_required");
                case "no_save": return L10n.T("rooms.err.no_save");
                case "bad_request": return L10n.T("rooms.err.bad_request");
                case "old_client": return L10n.T("rooms.err.old_client");
                case "no_room": return L10n.T("rooms.err.no_room");
                case "bad_password": return L10n.T("rooms.err.bad_password");
                case "locked": return L10n.T("rooms.err.locked");
                case "bad_token": return L10n.T("rooms.err.bad_token");
                case "not_member": return L10n.T("rooms.err.not_member");
                case "already_member": return L10n.T("rooms.err.already_member");
                case "forbidden": return L10n.T("rooms.err.forbidden");
                case "busy": return L10n.T("rooms.err.busy");
                case "not_host":
                case "version_conflict": return RoomGame.ConflictMessage;
                case "limit": return L10n.T("rooms.err.limit");
                case "too_large": return L10n.T("rooms.err.too_large");
                case "solo_off": return L10n.T("rooms.err.solo_off");
                case "no_slot": return L10n.T("rooms.err.no_slot");
                case "no_backup": return L10n.T("rooms.err.no_backup");
                case "waiting": return L10n.T("rooms.err.waiting");
                default: return L10n.T("rooms.err.failed");
            }
        }
    }
}
