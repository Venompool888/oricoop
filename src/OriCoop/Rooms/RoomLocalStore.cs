using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop.Rooms
{
    public sealed class RoomBackupMember
    {
        public int Seat;
        public string Steam;
        public string Nick;
    }

    public sealed class RoomBackupSave
    {
        public string Id;
        public string Name;
        public string Summary;
        public long Version;
    }

    public sealed class RoomBackupInfo
    {
        public string Server;
        public string Id;
        public string Name;
        public string Role;
        public string Steam;
        public long Number = -1;
        public long Played;
        public int Seats = 2;
        public bool Solo;
        public List<RoomBackupMember> Members = new List<RoomBackupMember>();
        public List<RoomBackupSave> Saves = new List<RoomBackupSave>();

        public static RoomBackupInfo FromInfoReply(RoomReply reply)
        {
            return Read(reply.Fields);
        }

        internal static RoomBackupInfo Read(RoomWire.Fields f)
        {
            RoomBackupInfo info = new RoomBackupInfo
            {
                Server = f.GetString("server"),
                Id = f.GetString("id"),
                Name = f.GetString("name"),
                Role = f.GetString("role"),
                Steam = f.GetString("steam"),
                Number = f.GetLong("no", -1),
                Played = f.GetLong("played", 0),
                Seats = (int)f.GetLong("seats", 2),
                Solo = f.GetString("solo") == "1"
            };
            if (info.Id.Length == 0)
            {
                info.Id = f.GetString("room");
            }
            for (int i = 0; i < Math.Min(f.GetLong("m.count", 0), 16); i++)
            {
                string p = "m." + i + ".";
                info.Members.Add(new RoomBackupMember
                {
                    Seat = (int)f.GetLong(p + "seat", 0),
                    Steam = f.GetString(p + "steam"),
                    Nick = f.GetString(p + "nick")
                });
            }
            for (int i = 0; i < Math.Min(f.GetLong("save.count", 0), 256); i++)
            {
                string p = "save." + i + ".";
                info.Saves.Add(new RoomBackupSave
                {
                    Id = f.GetString(p + "id"),
                    Name = f.GetString(p + "name"),
                    Version = f.GetLong(p + "version", 0),
                    Summary = f.GetString(p + "summary")
                });
            }
            return info;
        }

        internal void WriteMembers(RoomWire.Fields f)
        {
            f.Set("m.count", Members.Count);
            for (int i = 0; i < Members.Count; i++)
            {
                string p = "m." + i + ".";
                f.Set(p + "seat", Members[i].Seat);
                f.Set(p + "steam", Members[i].Steam);
                f.Set(p + "nick", Members[i].Nick);
            }
        }

        internal RoomWire.Fields Encode()
        {
            RoomWire.Fields f = new RoomWire.Fields();
            f.Set("server", Server);
            f.Set("id", Id);
            f.Set("name", Name);
            f.Set("role", Role);
            f.Set("steam", Steam);
            f.Set("no", Number);
            f.Set("played", Played);
            f.Set("seats", Seats);
            f.Set("solo", Solo ? "1" : "0");
            WriteMembers(f);
            f.Set("save.count", Saves.Count);
            for (int i = 0; i < Saves.Count; i++)
            {
                string p = "save." + i + ".";
                RoomBackupSave save = Saves[i];
                f.Set(p + "id", save.Id);
                f.Set(p + "name", save.Name);
                f.Set(p + "version", save.Version);
                f.Set(p + "summary", save.Summary);
            }
            return f;
        }
    }

    public sealed class RoomLocalStore
    {
        readonly string root;

        public RoomLocalStore(string rootFolder)
        {
            root = rootFolder;
            Directory.CreateDirectory(root);
        }

        // Room and save ids come from the rooms server and become folder names: only plain ids (the server makes
        // "r"/"s" + hex) are accepted, so a hostile server cannot write or delete outside the store ("..\..", "C:\").
        static string Id(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) throw new ArgumentException("bad room/save id");
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    throw new ArgumentException("bad room/save id");
            return id;
        }

        public void SaveRoom(RoomBackupInfo info)
        {
            RoomWire.AtomicWrite(Path.Combine(Path.Combine(root, Id(info.Id)), "room.dat"), info.Encode().Encode());
        }

        public List<RoomBackupInfo> ListRooms()
        {
            List<RoomBackupInfo> list = new List<RoomBackupInfo>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                string path = Path.Combine(directory, "room.dat");
                if (File.Exists(path))
                {
                    list.Add(RoomBackupInfo.Read(RoomWire.Fields.Decode(File.ReadAllBytes(path))));
                }
            }
            return list;
        }

        // T-095: the room played last, for the 上次游玩 row (file "last-room.txt": room id, server).
        public void SaveLastRoom(string roomId, string server)
        {
            RoomWire.AtomicWrite(Path.Combine(root, "last-room.txt"), Encoding.UTF8.GetBytes(Id(roomId) + "\n" + (server ?? "")));
        }

        public bool LoadLastRoom(out string roomId, out string server)
        {
            roomId = server = "";
            string path = Path.Combine(root, "last-room.txt");
            if (!File.Exists(path)) return false;
            string[] parts = File.ReadAllText(path, Encoding.UTF8).Split('\n');
            if (parts.Length < 1 || parts[0].Length == 0) return false;
            roomId = parts[0].Trim(); server = parts.Length > 1 ? parts[1].Trim() : "";
            foreach (RoomBackupInfo room in ListRooms()) if (room.Id == roomId) return true;
            return false;
        }

        public string LoadToken(string roomId)
        {
            string path = Path.Combine(Path.Combine(root, Id(roomId)), "token");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void SaveToken(string roomId, string token)
        {
            string path = Path.Combine(Path.Combine(root, Id(roomId)), "token");
            RoomWire.AtomicWrite(path, Encoding.UTF8.GetBytes(token));
        }

        string SavePath(string room, string save)
        {
            return Path.Combine(Path.Combine(Path.Combine(root, Id(room)), "saves"), Id(save));
        }

        public void StoreVersion(string roomId, string saveId, long version, byte[] data, string summary)
        {
            string path = SavePath(roomId, saveId);
            string history = Path.Combine(path, "history");
            RoomWire.AtomicWrite(Path.Combine(history, "v" + version + ".sav"), data);
            RoomWire.AtomicWrite(Path.Combine(path, "current.sav"), data);
            RoomWire.Fields f = new RoomWire.Fields();
            f.Set("version", version);
            f.Set("summary", summary);
            f.Set("at", RoomWire.Now);
            RoomWire.AtomicWrite(Path.Combine(path, "current.dat"), f.Encode());
            List<long> versions = new List<long>();
            foreach (string file in Directory.GetFiles(history, "v*.sav"))
            {
                versions.Add(long.Parse(Path.GetFileNameWithoutExtension(file).Substring(1)));
            }
            versions.Sort();
            while (versions.Count > 10)
            {
                File.Delete(Path.Combine(history, "v" + versions[0] + ".sav"));
                versions.RemoveAt(0);
            }
        }

        public byte[] LoadLatest(string roomId, string saveId, out long version, out string summary)
        {
            string path = SavePath(roomId, saveId);
            version = 0;
            summary = "";
            if (!File.Exists(Path.Combine(path, "current.dat")))
            {
                return null;
            }
            // A backup that cannot be read (torn current.dat, missing current.sav) counts as none: the caller then
            // downloads the whole save. It used to throw on the main thread inside RoomSession.Download, which left a
            // holder without a slot beating the lease (nobody else could host) and the 我的房间 action waiting for good.
            try
            {
                RoomWire.Fields f = RoomWire.Fields.Decode(File.ReadAllBytes(Path.Combine(path, "current.dat")));
                byte[] data = File.ReadAllBytes(Path.Combine(path, "current.sav"));
                version = f.GetLong("version", 0);
                summary = f.GetString("summary");
                return data;
            }
            catch (FormatException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        public void DeleteRoom(string roomId)
        {
            string path = Path.Combine(root, Id(roomId));
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        // T-173 (review F24): the creator pressed 开始 in this pending open room; confirm is retried until the server
        // says the room is confirmed (file "confirm" in the room's folder, so it survives a restart).
        public bool ConfirmWanted(string roomId)
        {
            return File.Exists(Path.Combine(Path.Combine(root, Id(roomId)), "confirm"));
        }

        public void SetConfirmWanted(string roomId, bool wanted)
        {
            string path = Path.Combine(Path.Combine(root, Id(roomId)), "confirm");
            if (wanted) RoomWire.AtomicWrite(path, new byte[] { (byte)'1' });
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    // T-173: the 我的房间 rules that do not need Unity (MyRooms / RoomsPanel use them; RoomTest checks them).
    public static class MyRoomsRules
    {
        // Rooms of the selected room service only: another service's rooms can be neither entered nor deleted from
        // here (review F21).
        public static List<RoomBackupInfo> OnServer(List<RoomBackupInfo> rooms, string server)
        {
            List<RoomBackupInfo> list = new List<RoomBackupInfo>();
            if (rooms == null) return list;
            foreach (RoomBackupInfo room in rooms)
                if (room.Server == server) list.Add(room);
            return list;
        }

        // The server no longer knows this room for us: the local record goes too (review F20).
        public static bool RoomGone(string error)
        {
            return error == "no_room" || error == "bad_token" || error == "not_member";
        }

        // Delete (creator, seat 1) or quit, from fresh info of that room, else the stored member list (review F27).
        public static bool DeleteAsCreator(RoomWire.Fields freshInfo, RoomBackupInfo stored, string steam)
        {
            if (freshInfo != null) return freshInfo.GetString("role") == "1";
            if (stored == null) return false;
            foreach (RoomBackupMember member in stored.Members)
                if (member.Seat == 1 && member.Steam == steam) return true;
            return false;
        }

        // Seconds the invite code stays open, by the server's clock: serverOffset = server now - local now when the
        // reply came (review F28). <= 0 = closed.
        public static long CodeSecondsLeft(long expires, long serverOffset, long localNow)
        {
            return expires <= 0 ? 0 : expires - (localNow + serverOffset);
        }

        // The invite code's expiry on this PC's clock (server expiry minus the server's clock lead). 0 while no code
        // expires: none open, or a pending room's code (no expiry until confirmed), so no countdown is drawn.
        public static long LocalCodeExpiry(long expires, long serverOffset)
        {
            return expires <= 0 ? 0 : expires - serverOffset;
        }

        // A room session error while a 我的房间 action still waits for that session to start hosting or joining: the
        // action reports the error and the session ends, so it neither enters the room after the page showed the
        // error (a later beat that sees the live code) nor stays active as a holder without a slot (a failed
        // download), which would block other rooms with `busy`. false: no action was waiting (the session runs on).
        public static bool FailBeforeStart(RoomActionLatch action, RoomSession session, string message)
        {
            if (!action.Finish(message)) return false;
            if (session != null) session.Cancel();
            return true;
        }

        // 开放邀请码: an open, confirmed room with a free room-service seat.
        public static bool CanOpenCode(RoomWire.Fields info)
        {
            return info != null && info.GetString("open") == "1" && info.GetString("pending") != "1"
                && info.GetLong("m.count", 0) < info.GetLong("seats", 2);
        }

        // Seats shown in the room list: fresh info, else what the local record stored (review F41).
        public static int Seats(RoomWire.Fields info, RoomBackupInfo stored)
        {
            if (info != null) return (int)info.GetLong("seats", 2);
            return stored == null ? 2 : stored.Seats;
        }

        // The screen after opening a room: 我的房间 flows keep the hidden native menu where it is (review F25).
        public static string ScreenAfterOpen(string current, bool myRooms)
        {
            return myRooms ? current : "room";
        }
    }

    // T-173: one running 我的房间 create/join/enter. Its done callback runs once: done(null) when the room session starts
    // hosting or joining, done(error) when anything before that fails; Cancel drops it without a callback.
    public sealed class RoomActionLatch
    {
        Action<string> done;
        int generation;

        public bool Pending { get { return done != null; } }
        public int Generation { get { return generation; } }

        public int Begin(Action<string> callback)
        {
            done = callback;
            return ++generation;
        }

        // Still the running action (not finished, cancelled or replaced since Begin returned `token`).
        public bool Current(int token)
        {
            return done != null && token == generation;
        }

        public bool Finish(string error)
        {
            Action<string> callback = done;
            done = null;
            if (callback == null) return false;
            callback(error);
            return true;
        }

        public bool Cancel()
        {
            bool was = done != null;
            done = null;
            generation++;
            return was;
        }
    }

    // T-173 (review F24): 开始 was pressed on this PC for a pending open room. The confirm goes out again until info
    // says the room is confirmed: on every fresh info of that room (我的房间 menu refresh, room page refresh) and, while
    // the room session runs (in game, where no page refreshes), from an info check every CheckSeconds. The marker is a
    // file in the room's folder, so it survives a restart. A confirm is resent only when fresh info says the room is
    // still pending: confirming a confirmed room would close an invite code reopened since.
    public sealed class RoomConfirmRetry
    {
        readonly RoomJobs jobs;
        readonly RoomLocalStore store;
        readonly Dictionary<string, bool> wanted = new Dictionary<string, bool>();   // cache of the store's markers
        readonly Dictionary<string, bool> sending = new Dictionary<string, bool>();
        double nextCheck;
        public RoomClient Client;
        public double CheckSeconds = 30;
        public Action<string> Confirmed;              // room: the server has the confirm
        public Action<string, string> Failed;         // room, error: retried later
        public Action<RoomAuth, RoomReply> Checked;   // the periodic check's info reply (null: only SawInfo)
        public Action<string> Log;

        public bool Checking { get; private set; }

        public RoomConfirmRetry(RoomJobs jobs, RoomLocalStore store)
        {
            this.jobs = jobs;
            this.store = store;
        }

        public bool Wanted(string room)
        {
            if (store == null || string.IsNullOrEmpty(room)) return false;
            bool value;
            if (wanted.TryGetValue(room, out value)) return value;
            try { value = store.ConfirmWanted(room); }
            catch (Exception) { value = false; }
            wanted[room] = value;
            return value;
        }

        void Set(string room, bool value)
        {
            if (store == null || string.IsNullOrEmpty(room) || Wanted(room) == value) return;
            wanted[room] = value;
            try { store.SetConfirmWanted(room, value); }
            catch (Exception e) { if (Log != null) Log("confirm marker: " + e.Message); }
        }

        public void Forget(string room)
        {
            if (room != null) wanted.Remove(room);
        }

        // Fresh info says the room is open and still pending, and 开始 was pressed here.
        public static bool ResendWanted(RoomWire.Fields info, bool pressed)
        {
            return pressed && info != null && info.GetString("open") == "1" && info.GetString("pending") == "1";
        }

        // 开始 by the creator.
        public void Pressed(RoomAuth auth)
        {
            if (auth == null || string.IsNullOrEmpty(auth.Room)) return;
            Set(auth.Room, true);
            Send(auth);
        }

        // Fresh info of auth.Room: confirmed clears the marker, still pending with the marker resends.
        public void SawInfo(RoomAuth auth, RoomWire.Fields info)
        {
            if (auth == null || info == null || info.GetString("open") != "1") return;
            if (info.GetString("pending") != "1") Set(auth.Room, false);
            else if (Wanted(auth.Room)) Send(auth);
        }

        // While the room session runs: one info check of the current room at a time, every CheckSeconds, as long as
        // its latest info says pending and 开始 was pressed here. The check flag clears on every reply. true: sent.
        public bool Tick(double now, bool sessionActive, RoomAuth auth, RoomWire.Fields info)
        {
            if (!sessionActive || auth == null || Checking || Client == null || now < nextCheck
                || !ResendWanted(info, Wanted(auth.Room))) return false;
            Checking = true;
            nextCheck = now + CheckSeconds;
            RoomClient c = Client;
            RoomAuth current = auth;
            jobs.Add(delegate { return c.Info(current); }, delegate(RoomReply r)
            {
                Checking = false;
                if (!r.Ok) { if (Log != null) Log("confirm check: " + r.Error); return; }
                if (Checked != null) Checked(current, r);
                else SawInfo(current, r.Fields);
            });
            return true;
        }

        void Send(RoomAuth auth)
        {
            string room = auth.Room;
            if (Client == null || sending.ContainsKey(room)) return;
            sending[room] = true;
            RoomClient c = Client;
            jobs.Add(delegate { return c.Confirm(auth); }, delegate(RoomReply r)
            {
                sending.Remove(room);
                if (r.Ok)
                {
                    Set(room, false);
                    if (Confirmed != null) Confirmed(room);
                }
                else if (Failed != null) Failed(room, r.Error);
            });
        }
    }

    // T-200 (playtest 4): how long the 我的房间 page may show "connecting" while the room service's first refresh runs.
    // The refresh is a chain of jobs on one worker thread, each bounded only by its HTTP timeout, so the page itself
    // gives up after LimitSeconds without a break and shows "cannot connect" with a retry. Call Expired every frame;
    // it is true once per wait that lasted the whole limit.
    public sealed class RoomBusyWatch
    {
        readonly double limit;
        double since = -1;

        public RoomBusyWatch(double limitSeconds) { limit = limitSeconds; }

        public double LimitSeconds { get { return limit; } }

        // A new refresh starts (the page was reopened): its wait counts from now, also when the previous one never paused.
        public void Restart() { since = -1; }

        public bool Expired(bool waiting, double now)
        {
            if (!waiting) { since = -1; return false; }
            if (since < 0) { since = now; return false; }
            if (now - since < limit) return false;
            since = -1;
            return true;
        }
    }

    // T-173 (review F23): callers waiting for the room service's first refresh. End always answers every waiter, so a
    // refresh chain cut short (a dropped reply) can never leave them queued for good; Drop forgets them (cancel).
    public sealed class RoomReadyGate
    {
        bool running;
        Action<string> waiting;

        public bool Running { get { return running; } }

        public void Begin() { running = true; }

        public void Wait(Action<string> done, Func<string> state)
        {
            if (running) waiting += done;
            else done(state());
        }

        public void End(Func<string> state)
        {
            running = false;
            Action<string> queued = waiting;
            waiting = null;
            if (queued == null) return;
            foreach (Delegate one in queued.GetInvocationList()) Wait((Action<string>)one, state);
        }

        public void Drop()
        {
            running = false;
            waiting = null;
        }
    }
}
