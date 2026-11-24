using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OriCoop.Rooms;

// RoomLocalStore.LoadLatest: an unreadable local backup counts as no backup. It threw on the main thread inside
// RoomSession.Download: the session stayed a holder without a slot that kept beating the lease (nobody could host
// the save) and the 我的房间 action never finished.
static class BackupCorruptTests
{
    sealed class FakeHttp : IRoomHttp
    {
        readonly Queue<KeyValuePair<string, byte[]>> steps = new Queue<KeyValuePair<string, byte[]>>();
        public readonly List<RoomWire.Fields> Requests = new List<RoomWire.Fields>();

        public void Reply(string op, RoomWire.Fields f)
        {
            lock (steps) steps.Enqueue(new KeyValuePair<string, byte[]>(op, f.Encode()));
        }

        public byte[] Post(string op, RoomWire.Fields body, int timeoutMs)
        {
            lock (steps)
            {
                Requests.Add(RoomWire.Fields.Decode(body.Encode()));
                if (steps.Count == 0) throw new Exception("unexpected operation " + op);
                KeyValuePair<string, byte[]> step = steps.Dequeue();
                if (step.Key != op) throw new Exception("expected " + step.Key + ", got " + op);
                return step.Value;
            }
        }
    }

    sealed class FakeGame : IRoomGame
    {
        public string LiveCode { get { return ""; } }
        public bool InGameplay { get { return false; } }
        public bool Together { get { return false; } }
        public long PlayedSeconds { get { return 0; } }
        public string ConflictMessage { get { return "conflict"; } }
        public int Hosted = -1;
        public byte[] SlotData;
        public int WriteRoomSlot(string room, string save, long version, byte[] data) { SlotData = data; return 0; }
        public void UpdateVersion(int slot, string room, string save, long version) { }
        public void Host(int slot, string server) { Hosted = slot; }
        public void Join(string target) { }
        public byte[] Snapshot(out string summary) { summary = ""; return new byte[0]; }
        public void CopySingle(byte[] data) { }
        public void Message(string text) { }
    }

    static int failures;

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Run(string name, Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-cloud-backup-" + Guid.NewGuid().ToString("N"));
        try { test(dir); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message); }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { } }
    }

    static string SaveDir(string dir) { return Path.Combine(Path.Combine(Path.Combine(dir, "room"), "saves"), "save"); }

    static void TearCurrentDat(string dir)
    {
        File.WriteAllBytes(Path.Combine(SaveDir(dir), "current.dat"), new byte[] { 7, (byte)'v' });
    }

    static void DropCurrentSav(string dir)
    {
        File.Delete(Path.Combine(SaveDir(dir), "current.sav"));
    }

    static void ExpectNone(RoomLocalStore store)
    {
        long version;
        string summary;
        byte[] data = store.LoadLatest("room", "save", out version, out summary);
        Check(data == null, "unreadable backup returned bytes");
        Check(version == 0 && summary == "", "unreadable backup returned version " + version + " / '" + summary + "'");
    }

    static int Main()
    {
        Run("torn current.dat counts as no backup", delegate(string dir)
        {
            RoomLocalStore store = new RoomLocalStore(dir);
            store.StoreVersion("room", "save", 4, new byte[] { 1 }, "four");
            TearCurrentDat(dir);
            ExpectNone(store);
        });
        Run("missing current.sav counts as no backup", delegate(string dir)
        {
            RoomLocalStore store = new RoomLocalStore(dir);
            store.StoreVersion("room", "save", 4, new byte[] { 1 }, "four");
            DropCurrentSav(dir);
            ExpectNone(store);
        });
        Run("a readable backup still loads", delegate(string dir)
        {
            RoomLocalStore store = new RoomLocalStore(dir);
            store.StoreVersion("room", "save", 4, new byte[] { 1, 2 }, "four");
            long version;
            string summary;
            byte[] data = store.LoadLatest("room", "save", out version, out summary);
            Check(data != null && data.Length == 2 && version == 4 && summary == "four", "backup did not load");
        });
        Run("host with a torn backup downloads the whole save and hosts", delegate(string dir)
        {
            RoomLocalStore store = new RoomLocalStore(dir);
            store.StoreVersion("room", "save", 4, new byte[] { 1 }, "four");
            TearCurrentDat(dir);
            RoomJobs jobs = new RoomJobs();
            try
            {
                FakeHttp http = new FakeHttp();
                FakeGame game = new FakeGame();
                RoomWire.Fields enter = new RoomWire.Fields();
                enter.Set("ok", "1"); enter.Set("mode", "host");
                RoomWire.Fields download = new RoomWire.Fields();
                download.Set("ok", "1"); download.Set("version", 5); download.Set("summary", "five");
                download.Set("save", new byte[] { 9, 9 });
                http.Reply("enter", enter);
                http.Reply("download", download);
                RoomSession session = new RoomSession(new RoomClient(http), jobs, store, game,
                    new RoomAuth { Room = "room", Steam = "player", Token = "token" }, "server");
                string error = null;
                session.Error = delegate(string e) { error = e; };
                session.Online = true;
                session.Enter("save");
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (game.Hosted < 0 && DateTime.UtcNow < deadline) { jobs.Pump(); Thread.Sleep(1); }
                Check(error == null, "session failed: " + error);
                Check(game.Hosted == 0 && game.SlotData != null && game.SlotData.Length == 2, "did not host the downloaded save");
                Check(http.Requests.Count == 2 && http.Requests[1].GetLong("have", -1) == 0, "download did not ask for the whole save");
                Check(session.Version == 5, "version " + session.Version);
            }
            finally { jobs.Dispose(); }
        });
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
