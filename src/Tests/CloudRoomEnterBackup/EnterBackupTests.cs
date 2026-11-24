using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OriCoop.Rooms;

// RoomSession.Enter: the game ticks the session every frame, also before the enter reply is in. That first tick must not
// start the joiner's periodic backup download (the mode is not known yet): it downloaded the whole save once more, and a
// host then downloaded it a second time; a failed extra download also failed the room entry.
static class EnterBackupTests
{
    sealed class FakeHttp : IRoomHttp
    {
        readonly Queue<KeyValuePair<string, byte[]>> steps = new Queue<KeyValuePair<string, byte[]>>();
        public readonly List<string> Ops = new List<string>();

        public void Reply(string op, params string[] pairs)
        {
            RoomWire.Fields f = new RoomWire.Fields();
            for (int i = 0; i < pairs.Length; i += 2) f.Set(pairs[i], pairs[i + 1]);
            lock (steps) steps.Enqueue(new KeyValuePair<string, byte[]>(op, f.Encode()));
        }

        public byte[] Post(string op, RoomWire.Fields body, int timeoutMs)
        {
            lock (steps)
            {
                Ops.Add(op);
                if (steps.Count == 0) throw new Exception("unexpected operation " + op);
                KeyValuePair<string, byte[]> step = steps.Dequeue();
                if (step.Key != op) throw new Exception("expected " + step.Key + ", got " + op);
                return step.Value;
            }
        }

        public int Count { get { lock (steps) return Ops.Count; } }
    }

    sealed class FakeGame : IRoomGame
    {
        public string LiveCode { get { return ""; } }
        public bool InGameplay { get { return false; } }
        public bool Together { get { return false; } }
        public long PlayedSeconds { get { return 0; } }
        public string ConflictMessage { get { return "conflict"; } }
        public int Hosted = -1;
        public string Joined;
        public int WriteRoomSlot(string room, string save, long version, byte[] data) { return 2; }
        public void UpdateVersion(int slot, string room, string save, long version) { }
        public void Host(int slot, string server) { Hosted = slot; }
        public void Join(string target) { Joined = target; }
        public byte[] Snapshot(out string summary) { summary = ""; return new byte[0]; }
        public void CopySingle(byte[] data) { }
        public void Message(string text) { }
    }

    static int failures;

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Pump(RoomJobs jobs, Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { jobs.Pump(); Thread.Sleep(1); }
        jobs.Pump();
    }

    // Lets every job queued so far finish and runs its completion.
    static void Drain(RoomJobs jobs)
    {
        bool done = false;
        jobs.Add(delegate { return new RoomReply { Fields = new RoomWire.Fields() }; }, delegate(RoomReply r) { done = true; });
        Pump(jobs, delegate { return done; });
        Check(done, "jobs did not drain");
    }

    static void Run(string name, bool host)
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-cloud-enter-" + Guid.NewGuid().ToString("N"));
        RoomJobs jobs = new RoomJobs();
        try
        {
            FakeHttp http = new FakeHttp();
            FakeGame game = new FakeGame();
            RoomSession session = new RoomSession(new RoomClient(http), jobs, new RoomLocalStore(dir), game,
                new RoomAuth { Room = "room", Steam = "player", Token = "token" }, "server");
            string error = null;
            session.Error = delegate(string e) { error = e; };
            if (host)
            {
                http.Reply("enter", "ok", "1", "mode", "host");
                http.Reply("download", "ok", "1", "version", "3", "summary", "s", "save", "\u0001\u0002");
            }
            else http.Reply("enter", "ok", "1", "mode", "join", "live", "code");
            session.Online = true;
            session.Enter("save");
            // The game's first frame after Enter, before the enter reply has been pumped.
            session.Tick(100);
            Pump(jobs, delegate { return host ? game.Hosted >= 0 : game.Joined != null; });
            Drain(jobs);
            session.Tick(100.5);
            Drain(jobs);
            Check(error == null, "session failed: " + error);
            Check(host ? game.Hosted == 2 : game.Joined == "server|code", "did not " + (host ? "host" : "join"));
            int expected = host ? 2 : 1;
            Check(http.Count == expected, "expected " + expected + " request(s), got " + string.Join(", ", http.Ops.ToArray()));
            Console.WriteLine("ok " + name);
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine("FAIL " + name + ": " + e.Message);
        }
        finally
        {
            jobs.Dispose();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    // A joiner still makes its backup download BackupSeconds after it joined.
    static void JoinerBackupStillRuns()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-cloud-enter-" + Guid.NewGuid().ToString("N"));
        RoomJobs jobs = new RoomJobs();
        try
        {
            FakeHttp http = new FakeHttp();
            FakeGame game = new FakeGame();
            RoomSession session = new RoomSession(new RoomClient(http), jobs, new RoomLocalStore(dir), game,
                new RoomAuth { Room = "room", Steam = "player", Token = "token" }, "server");
            string error = null;
            session.Error = delegate(string e) { error = e; };
            http.Reply("enter", "ok", "1", "mode", "join", "live", "code");
            http.Reply("download", "ok", "1", "version", "3", "summary", "s", "save", "\u0001\u0002");
            session.Online = true;
            session.Enter("save");
            Drain(jobs);
            session.Tick(10);
            Drain(jobs);
            Check(http.Count == 1, "backup before BackupSeconds");
            session.Tick(10 + session.BackupSeconds);
            Drain(jobs);
            Check(error == null, "session failed: " + error);
            Check(http.Count == 2 && http.Ops[1] == "download", "no backup download after BackupSeconds");
            Console.WriteLine("ok joiner backup still runs");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine("FAIL joiner backup still runs: " + e.Message);
        }
        finally
        {
            jobs.Dispose();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    static int Main()
    {
        Run("host downloads the save once", true);
        Run("joiner makes no download before the enter reply", false);
        JoinerBackupStillRuns();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
