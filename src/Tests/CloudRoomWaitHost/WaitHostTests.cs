using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OriCoop.Rooms;

// RoomSession: a player that waits (mode wait, or join with no live code yet) and whose beat then answers host becomes
// the holder. It must stop counting as waiting: the wait deadline (30 s after enter) used to make the new host give
// up its lease ("waiting" error + leave) while it was already hosting, and its beats kept saying waiting=1.
static class WaitHostTests
{
    sealed class FakeHttp : IRoomHttp
    {
        readonly Queue<KeyValuePair<string, byte[]>> steps = new Queue<KeyValuePair<string, byte[]>>();
        public readonly List<string> Ops = new List<string>();
        public readonly List<RoomWire.Fields> Requests = new List<RoomWire.Fields>();

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
                Requests.Add(RoomWire.Fields.Decode(body.Encode()));
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
        public int WriteRoomSlot(string room, string save, long version, byte[] data) { return 1; }
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

    static void Drain(RoomJobs jobs)
    {
        bool done = false;
        jobs.Add(delegate { return new RoomReply { Fields = new RoomWire.Fields() }; }, delegate(RoomReply r) { done = true; });
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done && DateTime.UtcNow < deadline) { jobs.Pump(); Thread.Sleep(1); }
        jobs.Pump();
        Check(done, "jobs did not drain");
    }

    static void Run(string name, string[] enterReply)
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-cloud-waithost-" + Guid.NewGuid().ToString("N"));
        RoomJobs jobs = new RoomJobs();
        try
        {
            FakeHttp http = new FakeHttp();
            FakeGame game = new FakeGame();
            RoomSession session = new RoomSession(new RoomClient(http), jobs, new RoomLocalStore(dir), game,
                new RoomAuth { Room = "room", Steam = "player", Token = "token" }, "server");
            string error = null;
            session.Error = delegate(string e) { error = e; };
            http.Reply("enter", enterReply);
            http.Reply("beat", "ok", "1", "mode", "host");
            http.Reply("download", "ok", "1", "version", "3", "summary", "s", "save", "\u0001\u0002");
            http.Reply("beat", "ok", "1", "mode", "host");
            session.Online = true;
            session.Enter("save");
            Drain(jobs);
            session.Tick(2);          // waiting beat: the answer is host
            Drain(jobs);
            Drain(jobs);              // the host's download
            Check(game.Hosted == 1 && session.Holder, "did not become the host");
            session.Tick(31);         // past the 30 s wait deadline, before the next beat (BeatSeconds after hosting)
            Drain(jobs);
            Check(error == null, "the new host failed with " + error);
            Check(session.Active, "the new host left the room");
            session.Tick(32.5);       // the holder's beat
            Drain(jobs);
            Check(error == null, "the new host failed with " + error);
            Check(session.Active && session.Holder, "no longer the host");
            Check(http.Count == 4 && http.Ops[3] == "beat", "requests: " + string.Join(", ", http.Ops.ToArray()));
            Check(http.Requests[3].GetString("waiting") == "0", "the holder's beat still says waiting");
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

    static int Main()
    {
        Run("waiting player whose beat answers host stays host", new string[] { "ok", "1", "mode", "wait" });
        Run("joiner without a live code whose beat answers host stays host", new string[] { "ok", "1", "mode", "join" });
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
