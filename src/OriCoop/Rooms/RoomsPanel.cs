using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using OriCoop.Net;
using UnityEngine;

namespace OriCoop.Rooms
{
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
        string screen = "list", message = "", roomName = "", password = "", code = "";
        Action<string> passwordDone;
        string passwordBack;
        bool visible, busy, online, wasNetEnabled;
        int revision, focus, buttonIndex, activate = -1;
        byte[] copyBytes;
        int overwrite = -1;
        Vector2 scroll;
        Action<string> editAssign;
        string editText, editAlphabet;
        int editCursor, editMax;
        readonly List<Action> buttons = new List<Action>();
        readonly Dictionary<string, Action> testDrawing = new Dictionary<string, Action>();
        readonly Dictionary<string, Action> testDrawn = new Dictionary<string, Action>();
        string testScreen;

        // Only completed repaint passes expose actions; Update/job callbacks can precede the next OnGUI.
        internal bool TestClick(string expectedScreen, string id)
        {
            if (Environment.GetEnvironmentVariable("ORICOOP_TEST") != "1") return false;
            Action action;
            if (!visible || busy || screen != expectedScreen || testScreen != screen
                || !testDrawn.TryGetValue(id, out action)) return false;
            action();
            return true;
        }

        internal string TestDrawState
        {
            get
            {
                return "screen=" + screen + ", drawn=" + (testScreen ?? "none") + ", visible=" + visible
                    + ", busy=" + busy + ", buttons=[" + string.Join(", ", new List<string>(testDrawn.Keys).ToArray())
                    + "], message=" + message;
            }
        }

        public static void Open()
        {
            if (Instance == null)
            {
                GameObject go = new GameObject("OriCoop_Rooms");
                DontDestroyOnLoad(go);
                Instance = go.AddComponent<RoomsPanel>();
                Instance.jobs = new RoomJobs();
                Instance.Game = new RoomGame();
                Instance.Game.Starting = delegate
                {
                    Instance.busy = false;
                    Instance.visible = false;
                };
                Instance.Game.PickCopySlot = delegate(byte[] bytes)
                {
                    Instance.copyBytes = bytes;
                    Instance.Show("slots");
                };
                NetPlay.SessionEnded += delegate
                {
                    if (Instance.Session != null) Instance.Session.Leave();
                };
            }
            RoomsPanel p = Instance;
            if (p.Session != null && p.Session.Active) return;
            p.visible = true;
            NativeModeMenu.RoomPanelVisible(true);
            p.steam = TestValue("ORICOOP_TEST_STEAMID", SteamTransport.LocalSteamId.ToString());
            p.nick = TestValue("ORICOOP_TEST_NICK", SteamTransport.LocalPersonaName);
            p.Show("list");
            if (p.steam == "0")
            {
                p.message = L10n.T("rooms.steam_required");
                return;
            }
            p.online = false;
            p.store = new RoomLocalStore(TestValue("ORICOOP_ROOMS_DIR",
                Path.GetFullPath(Path.Combine(Paths.ConfigPath, "../OriCoop/rooms"))));
            p.rooms = p.store.ListRooms();
            p.refreshed.Clear();
            p.relay = TestValue("ORICOOP_NET_RELAY", RelayList.Current);
            p.serviceName = L10n.T("rooms.service");
            foreach (RelayList.Entry entry in RelayList.Entries)
                if (entry.Address == p.relay && !string.IsNullOrEmpty(entry.Name)) p.serviceName = entry.Name;
            p.server = Plugin.Instance.Config.Bind("Rooms", "Server", "", "Room service URL; empty uses relay host")
                .Value.Trim();
            p.server = TestValue("ORICOOP_ROOMS_SERVER", p.server);
            if (p.server.Length == 0)
            {
                Uri address;
                if (!Uri.TryCreate("http://" + p.relay, UriKind.Absolute, out address))
                {
                    p.message = ErrorText("network");
                    return;
                }
                p.server = "http://" + address.Host + ":47021/";
            }
            p.client = new RoomClient(p.server);
            p.Request(p.client.Hello, delegate(RoomReply reply)
            {
                p.online = reply.Ok;
                if (!p.online) p.message = Offline;
                else p.RefreshNext(0);
            });
        }

        internal static string TestValue(string name, string fallback)
        {
            if (Environment.GetEnvironmentVariable("ORICOOP_TEST") != "1") return fallback;
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        static string Offline { get { return L10n.T("rooms.offline"); } }

        void Show(string next)
        {
            screen = next;
            testScreen = null;
            testDrawn.Clear();
            editAssign = null;
            focus = 0;
            scroll = Vector2.zero;
            message = "";
        }

        void RefreshNext(int index)
        {
            if (index >= rooms.Count) return;
            RoomBackupInfo room = rooms[index];
            if (room.Server != server)
            {
                RefreshNext(index + 1);
                return;
            }
            SetRoom(room.Id);
            RoomAuth current = auth;
            RequestAuth(delegate
            {
                return client.Info(current);
            }, delegate(RoomReply r)
            {
                if (r.Ok) SaveInfo(r);
                else message = ErrorText(r.Error);
                RefreshNext(index + 1);
            });
        }

        void SetRoom(string room)
        {
            auth = new RoomAuth
            {
                Room = room, Steam = steam, Token = store.LoadToken(room)
            };
            Session = new RoomSession(client, jobs, store, Game, auth, relay);
            Session.Online = online;
            double poll;
            if (double.TryParse(TestValue("ORICOOP_ROOMS_POLL", ""),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out poll)
                && poll > 0) Session.BackupSeconds = poll;
            Session.Password = AskPassword;
            Session.Copied = delegate
            {
                busy = false;
            };
            Session.Error = delegate(string error)
            {
                busy = false;
                visible = true;
                NativeModeMenu.RoomPanelVisible(true);
                message = ErrorText(error);
            };
        }

        void AskPassword(Action<string> done)
        {
            busy = false;
            password = "";
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

        void OpenRoom(string room)
        {
            SetRoom(room);
            Show("room");
            info = null;
            RoomAuth current = auth;
            RequestAuth(delegate
            {
                return client.Info(current);
            }, delegate(RoomReply r)
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
                        Request(delegate
                        {
                            return client.Rebind(existingRoom, pw, steam);
                        },
                            delegate(RoomReply r)
                            {
                                if (!r.Ok)
                                {
                                    message = ErrorText(r.Error);
                                    return;
                                }
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

        void Back()
        {
            revision++;
            busy = false;
            if (Session != null) Session.Cancel();
            if (screen == "list")
            {
                visible = false;
                NativeModeMenu.RoomPanelVisible(false);
                return;
            }
            if (screen == "password")
            {
                Action<string> cancel = passwordDone;
                passwordDone = null;
                cancel(null);
                Show(passwordBack);
                return;
            }
            Show("list");
            rooms = store == null ? null : store.ListRooms();
        }

        void Update()
        {
            jobs.Pump();
            if (Session != null) Session.Tick(Time.realtimeSinceStartup);
            if (wasNetEnabled && !NetPlay.Enabled && Session != null) Session.Leave();
            wasNetEnabled = NetPlay.Enabled;
            if (!visible) return;
            if (editAssign != null)
            {
                EditTick();
                return;
            }
            if (Core.Input.Cancel.OnPressed || Input.GetKeyDown(KeyCode.Escape))
            {
                Back();
                return;
            }
            if (busy) return;
            if (Core.Input.MenuUp.OnPressed) focus = Math.Max(0, focus - 1);
            if (Core.Input.MenuDown.OnPressed) focus = Math.Min(buttons.Count - 1, focus + 1);
            if (Core.Input.ActionButtonA.OnPressed || Input.GetKeyDown(KeyCode.Return)) activate = focus;
        }

        bool Button(string text, Action action, string id = null)
        {
            int index = buttonIndex++;
            buttons.Add(action);
            if (id != null && Environment.GetEnvironmentVariable("ORICOOP_TEST") == "1") testDrawing[id] = action;
            GUI.SetNextControlName("rooms-button-" + index);
            bool pressed = GUILayout.Button((index == focus ? "> " : "") + text, GUILayout.Height(36));
            if ((pressed || activate == index) && !busy)
            {
                activate = -1;
                action();
                return true;
            }
            return false;
        }

        void OnGUI()
        {
            if (!visible) return;
            GUI.depth = -200;
            GUILayout.BeginArea(new Rect(Screen.width / 2 - 340, 35, 680, Screen.height - 70), GUI.skin.box);
            GUILayout.Label(
                L10n.F("rooms.title", serviceName));
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(message);
            if (busy) GUILayout.Label(
                L10n.T("rooms.connecting"));
            bool enabled = GUI.enabled;
            GUI.enabled = !busy;
            buttons.Clear();
            testDrawing.Clear();
            string drawingScreen = screen;
            buttonIndex = 0;
            scroll = GUILayout.BeginScrollView(scroll);
            if (steam != "0" && store != null)
            {
                if (screen == "list") DrawList();
                else if (screen == "room") DrawRoom();
                else if (screen == "create") DrawCreate();
                else if (screen == "join") DrawJoin();
                else if (screen == "password") DrawPassword();
                else if (screen == "slots") DrawSlots();
            }
            GUILayout.EndScrollView();
            GUI.enabled = enabled;
            // Back remains usable while a job is outstanding.
            if (GUILayout.Button(
                L10n.T("common.back"), GUILayout.Height(36))) Back();
            GUILayout.EndArea();
            if (Event.current.type == EventType.Repaint)
            {
                testScreen = null;
                testDrawn.Clear();
                if (screen == drawingScreen && visible && !busy)
                {
                    foreach (KeyValuePair<string, Action> button in testDrawing)
                        testDrawn[button.Key] = button.Value;
                    testScreen = drawingScreen;
                }
            }
            activate = -1;
        }

        void DrawList()
        {
            if (!online) GUILayout.Label(Offline);
            foreach (RoomBackupInfo room in rooms.ToArray())
            {
                GUILayout.Label(
                    L10n.F("rooms.save_count", room.Name, room.Saves.Count));
                foreach (RoomBackupMember member in room.Members)
                    GUILayout.Label(
                        L10n.F("rooms.member", member.Seat, member.Nick));
                RoomWire.Fields detail;
                if (refreshed.TryGetValue(room.Id, out detail))
                {
                    GUILayout.Label(
                        L10n.F("rooms.last_played", TimeText(detail.GetLong("played", 0))));
                    for (int i = 0; i < detail.GetLong("save.count", 0); i++)
                    {
                        string holder = detail.GetString("save." + i + ".holder");
                        if (holder.Length == 0) continue;
                        foreach (RoomBackupMember member in room.Members)
                            if (member.Steam == holder) GUILayout.Label(
                                L10n.F("rooms.playing", member.Nick));
                    }
                }
                string id = room.Id;
                if (room.Server == server && online)
                    Button(L10n.T("rooms.open"), delegate
                    {
                        OpenRoom(id);
                    });
                else
                {
                    foreach (RoomBackupSave save in room.Saves)
                    {
                        string sid = save.Id;
                        Button(L10n.F("rooms.copy_named", save.Name), delegate
                        {
                            SetRoom(id);
                            Session.Online = false;
                            busy = true;
                            Session.Copy(sid);
                        });
                    }
                }
            }
            if (online)
            {
                Button(L10n.T("rooms.create"), delegate
                {
                    roomName = L10n.F("rooms.default_name", nick);
                    password = "";
                    Show("create");
                }, "create-room");
                Button(L10n.T("rooms.join_code"), delegate
                {
                    password = "";
                    code = "";
                    Show("join");
                }, "join-code");
            }
        }

        void DrawRoom()
        {
            if (info == null) return;
            GUILayout.Label(
                info.GetString("name"));
            string join = info.GetString("code");
            if (join.Length > 0)
                GUILayout.Label(
                    L10n.F("rooms.share", join));
            for (int i = 0; i < info.GetLong("save.count", 0); i++)
            {
                string prefix = "save." + i + ".";
                string save = info.GetString(prefix + "id");
                GUILayout.Label(
                    L10n.F("rooms.save_version", info.GetString(prefix + "name"), info.GetString(prefix + "version")));
                GUILayout.Label(
                FormatSummary(info.GetString(prefix + "summary")));
                for (int seat = 1; seat <= 2; seat++)
                    GUILayout.Label(
                        L10n.F("rooms.seat_seconds", seat, info.GetLong(prefix + "secs.seat." + seat, 0)));
                GUILayout.Label(
                    L10n.F("rooms.saved", info.GetLong(prefix + "secs.both", 0), MemberName(info.GetString(prefix + "by")), TimeText(info.GetLong(prefix + "at", 0))));
                string holder = info.GetString(prefix + "holder");
                if (holder.Length > 0) GUILayout.Label(
                    L10n.F("rooms.playing", MemberName(holder)));
                Button(L10n.T("rooms.continue"), delegate
                {
                    busy = true;
                    Session.Enter(save);
                }, "continue-" + save);
                Button(L10n.T("rooms.copy_single"), delegate
                {
                    busy = true;
                    Session.Copy(save);
                });
            }
        }

        string MemberName(string id)
        {
            for (int i = 0; i < info.GetLong("m.count", 0); i++)
                if (info.GetString("m." + i + ".steam") == id) return info.GetString("m." + i + ".nick");
            return "";
        }

        static string TimeText(long seconds)
        {
            return seconds == 0 ? "" : new DateTime(1970, 1, 1).AddSeconds(seconds).ToLocalTime().ToString("g");
        }

        static string FormatSummary(string summary)
        {
            return summary.Replace("lv=", L10n.T("rooms.level")).Replace("pct=", L10n.T("rooms.completion"))
                .Replace("area=", L10n.T("rooms.area")).Replace("secs=", L10n.T("rooms.seconds")).Replace(";", "  ");
        }

        void Field(string value, int max, bool masked, bool codeField, Action<string> assign)
        {
            string text = masked ? GUILayout.PasswordField(value, '*', max) : GUILayout.TextField(value, max);
            assign(text);
            Button(L10n.T("rooms.edit"), delegate
            {
                editText = text;
                editCursor = Math.Min(text.Length, max - 1);
                editMax = max;
                editAlphabet = codeField ? RoomWire.CodeAlphabet
                    : " abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.!@#$%";
                editAssign = assign;
            });
            if (editAssign != null)
                GUILayout.Label(
                    L10n.F("rooms.edit_hint", editCursor + 1));
        }

        void EditTick()
        {
            if (Core.Input.ActionButtonA.OnPressed || Core.Input.Cancel.OnPressed)
            {
                editAssign = null;
                return;
            }
            if (Core.Input.MenuLeft.OnPressed) editCursor = Math.Max(0, editCursor - 1);
            if (Core.Input.MenuRight.OnPressed) editCursor = Math.Min(editMax - 1,
                Math.Min(editText.Length, editCursor + 1));
            int delta = Core.Input.MenuUp.OnPressed ? 1 : Core.Input.MenuDown.OnPressed ? -1 : 0;
            if (delta != 0)
            {
                int index = editCursor < editText.Length ? editAlphabet.IndexOf(editText[editCursor]) : -1;
                char ch = editAlphabet[(index + delta + editAlphabet.Length) % editAlphabet.Length];
                if (editCursor < editText.Length)
                    editText = editText.Substring(0, editCursor) + ch + editText.Substring(editCursor + 1);
                else if (editText.Length < editMax) editText += ch;
            }
            if (Core.Input.Copy.OnPressed && editText.Length > 0)
            {
                int remove = Math.Min(editCursor, editText.Length - 1);
                editText = editText.Remove(remove, 1);
                editCursor = Math.Min(editCursor, editText.Length);
            }
            if (Core.Input.Delete.OnPressed)
            {
                string pasted = GUIUtility.systemCopyBuffer ?? "";
                editText = pasted.Length > editMax ? pasted.Substring(0, editMax) : pasted;
                editCursor = Math.Min(editText.Length, editMax - 1);
            }
            editAssign(editText);
        }

        void DrawCreate()
        {
            GUILayout.Label(
                L10n.T("rooms.name"));
            Field(roomName, 32, false, false, delegate(string text)
            {
                roomName = text;
            });
            GUILayout.Label(
                L10n.T("rooms.password_min"));
            Field(password, 128, true, false, delegate(string text)
            {
                password = text;
            });
            GUILayout.Label(
                L10n.T("rooms.choose_save"));
            for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
            {
                int slot = i;
                if (!File.Exists(RoomGame.PathFor(slot))) continue;
                Button(L10n.F("rooms.create_slot", slot + 1, FormatSummary(RoomGame.Summary(slot))), delegate
                {
                    if (password.Length < 4 || roomName.Trim().Length == 0)
                    {
                        message = ErrorText("bad_request");
                        return;
                    }
                    byte[] bytes = File.ReadAllBytes(RoomGame.PathFor(slot));
                    string summary = RoomGame.Summary(slot);
                    string enteredName = roomName, enteredPassword = password;
                    Request(delegate
                    {
                        return client.Create(steam, nick, enteredName, enteredPassword, bytes, summary, L10n.F("rooms.slot_name", slot + 1));
                    }, delegate(RoomReply r)
                    {
                        if (r.Ok)
                            store.StoreVersion(r.Fields.GetString("room"), r.Fields.GetString("saveid"),
                                r.Fields.GetLong("version", 1), bytes, summary);
                        LoginResult(r);
                    });
                }, "create-slot-" + slot);
            }
        }

        void DrawJoin()
        {
            GUILayout.Label(
                L10n.T("rooms.code"));
            Field(code, 6, false, true, delegate(string text)
            {
                code = text.ToUpperInvariant();
            });
            GUILayout.Label(
                L10n.T("rooms.password"));
            Field(password, 128, true, false, delegate(string text)
            {
                password = text;
            });
            Button(L10n.T("rooms.join"), delegate
            {
                if (code.Length != 6 || password.Length < 4)
                {
                    message = ErrorText("bad_request");
                    return;
                }
                string enteredCode = code, enteredPassword = password;
                Request(delegate
                {
                    return client.Join(enteredCode, enteredPassword, steam, nick);
                }, LoginResult);
            }, "join-room");
        }

        void DrawPassword()
        {
            GUILayout.Label(
                L10n.T("rooms.password_again"));
            Field(password, 128, true, false, delegate(string text)
            {
                password = text;
            });
            Button(L10n.T("rooms.confirm"), delegate
            {
                if (password.Length < 4)
                {
                    message = ErrorText("bad_request");
                    return;
                }
                Action<string> done = passwordDone;
                Show(passwordBack);
                busy = true;
                done(password);
            });
        }

        void DrawSlots()
        {
            GUILayout.Label(
                L10n.T("rooms.choose_slot"));
            for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
            {
                int slot = i;
                Button(L10n.F("rooms.slot_summary", slot + 1, FormatSummary(RoomGame.Summary(slot))), delegate
                {
                    overwrite = slot;
                });
            }
            if (overwrite >= 0)
                Button(L10n.F("rooms.overwrite", overwrite + 1), delegate
                {
                    RoomGame.WriteSingle(overwrite, copyBytes);
                    overwrite = -1;
                    Show("list");
                    message = L10n.T("rooms.copied");
                });
        }

        void OnApplicationQuit()
        {
            if (Session == null || !Session.Active) return;
            // The worker owns all HTTP calls, including the bounded quit flush.
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
                case "version_conflict": return RoomSession.ConflictMessage;
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
