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
        string steam, nick, server, relay, serviceName;
        string screen = "list", message = "", roomName = "";
        Action<string> passwordDone;
        string passwordBack;
        bool visible, busy, online, wasNetEnabled;
        int revision;
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

        static void EnsureInstance()
        {
            if (Instance != null) return;
            GameObject go = new GameObject("OriCoop_Rooms");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<RoomsPanel>();
            Instance.jobs = new RoomJobs();
            Instance.Game = new RoomGame();
            Instance.Game.Starting = delegate { Instance.busy = false; Instance.visible = false; };
            Instance.Game.PickCopySlot = delegate(byte[] bytes)
            {
                Instance.copyBytes = bytes;
                Instance.overwrite = -1;
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
            p.visible = true;
            p.online = false;
            p.steam = TestValue("ORICOOP_TEST_STEAMID", SteamTransport.LocalSteamId.ToString());
            p.nick = TestValue("ORICOOP_TEST_NICK", SteamTransport.LocalPersonaName);
            p.serviceName = L10n.T("rooms.service");
            p.Show("list");
            p.autoOpen = autoRoom;
            if (p.steam == "0") { p.message = L10n.T("rooms.steam_required"); return; }
            p.store = new RoomLocalStore(RoomsDir);
            p.rooms = p.store.ListRooms();
            p.refreshed.Clear();
            p.relay = TestValue("ORICOOP_NET_RELAY", RelayList.Current);
            foreach (RelayList.Entry entry in RelayList.Entries)
                if (entry.Address == p.relay && !string.IsNullOrEmpty(entry.Name)) p.serviceName = entry.Name;
            p.server = Plugin.Instance.Config.Bind("Rooms", "Server", "", "Room service URL; empty uses relay host").Value.Trim();
            p.server = TestValue("ORICOOP_ROOMS_SERVER", p.server);
            if (p.server.Length == 0)
            {
                Uri address;
                if (!Uri.TryCreate("http://" + p.relay, UriKind.Absolute, out address))
                { p.message = ErrorText("network"); return; }
                p.server = "http://" + address.Host + ":47021/";
            }
            p.client = new RoomClient(p.server);
            p.Request(p.client.Hello, delegate(RoomReply reply)
            {
                p.online = reply.Ok;
                if (!p.online) p.message = L10n.T("rooms.offline");
                else if (p.autoOpen != null) { string room = p.autoOpen; p.autoOpen = null; p.OpenRoom(room); }
                else p.RefreshNext(0);
            });
        }

        internal static string TestValue(string name, string fallback)
        {
            if (Environment.GetEnvironmentVariable("ORICOOP_TEST") != "1") return fallback;
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        void Show(string next) { screen = next; message = ""; }

        void RefreshNext(int index)
        {
            if (index >= rooms.Count) return;
            RoomBackupInfo room = rooms[index];
            if (room.Server != server) { RefreshNext(index + 1); return; }
            SetRoom(room.Id);
            RoomAuth current = auth;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                if (r.Ok) SaveInfo(r);
                else message = ErrorText(r.Error);
                RefreshNext(index + 1);
            });
        }

        void SetRoom(string room)
        {
            auth = new RoomAuth { Room = room, Steam = steam, Token = store.LoadToken(room) };
            Session = new RoomSession(client, jobs, store, Game, auth, relay);
            Session.Online = online;
            double poll;
            if (double.TryParse(TestValue("ORICOOP_ROOMS_POLL", ""),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out poll)
                && poll > 0) Session.BackupSeconds = poll;
            Session.Password = AskPassword;
            Session.Copied = delegate { busy = false; };
            Session.Error = delegate(string error)
            {
                busy = false;
                visible = true;
                message = ErrorText(error);
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

        void Request(Func<RoomReply> work, Action<RoomReply> done)
        {
            busy = true;
            int request = revision;
            jobs.Add(work, delegate(RoomReply reply)
            {
                if (request != revision) return;
                busy = false;
                done(reply);
            });
        }

        void RequestAuth(Func<RoomReply> work, Action<RoomReply> done)
        {
            busy = true;
            int request = revision;
            Session.Call(work, delegate(RoomReply reply)
            {
                if (request != revision) return;
                busy = false;
                done(reply);
            });
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
            refreshed[auth.Room] = info;
        }

        internal void OpenRoom(string room)
        {
            SetRoom(room);
            Show("room");
            info = null;
            RoomAuth current = auth;
            RequestAuth(delegate { return client.Info(current); }, delegate(RoomReply r)
            {
                if (r.Ok) SaveInfo(r);
                else message = ErrorText(r.Error);
            });
        }

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

        static readonly bool s_oldNameMutation = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_ROOM_NAME_HASH") == "1";

        internal void BeginCreate()
        {
            int count = 0;
            foreach (RoomBackupInfo room in rooms) if (room.Steam == steam) count++;
            int number = count + 1;
            while (true)
            {
                roomName = L10n.F("rooms.auto_name", nick, number);
                // Test-only mutation switch (T-136): the old "#N" name, which the game draws as yellow markup.
                if (s_oldNameMutation) roomName = L10n.F("rooms.default_name", nick) + " #" + number;
                bool collision = false;
                foreach (RoomBackupInfo room in rooms) if (room.Name == roomName) { collision = true; break; }
                if (!collision) break;
                number++;
            }
            Show("create");
        }

        internal void BeginJoin() { Show("join"); }

        internal void CreateWithSlot(int slot, string password)
        {
            if (!NumericPassword(password)) { message = ErrorText("bad_request"); return; }
            byte[] bytes = File.ReadAllBytes(RoomGame.PathFor(slot));
            string summary = RoomGame.Summary(slot);
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
            if (Session != null) Session.Tick(Time.realtimeSinceStartup);
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
