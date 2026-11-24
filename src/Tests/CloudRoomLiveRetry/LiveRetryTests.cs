using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OriCoop.Rooms;

// RoomSession: the holder tells the room service its live code once the game has one. When the service refused it
// (not_host after the lease expired or the service restarted, any quick error), the next frame sent it again: a
// request and an error page about once per round trip for as long as the refusal lasted. A refused live code is now
// retried at the beat pace.
static class LiveRetryTests
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
                // The service keeps refusing the live code (not_host) however often it is asked.
                if (op == "live" && (steps.Count == 0 || steps.Peek().Key != "live"))
                {
                    RoomWire.Fields refused = new RoomWire.Fields();
                    refused.Set("ok", "0"); refused.Set("error", "not_host");
                    return refused.Encode();
                }
                if (steps.Count == 0) throw new Exception("unexpected operation " + op);
                KeyValuePair<string, byte[]> step = steps.Dequeue();
                if (step.Key != op) throw new Exception("expected " + step.Key + ", got " + op);
                return step.Value;
            }
        }

        public int Count(string op)
        {
            lock (steps) { int n = 0; foreach (string o in Ops) if (o == op) n++; return n; }
        }
    }

    sealed class FakeGame : IRoomGame
    {
        public string LiveCode { get; set; }
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

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-cloud-live-" + Guid.NewGuid().ToString("N"));
        RoomJobs jobs = new RoomJobs();
        try
        {
            FakeHttp http = new FakeHttp();
            FakeGame game = new FakeGame();
            RoomSession session = new RoomSession(new RoomClient(http), jobs, new RoomLocalStore(dir), game,
                new RoomAuth { Room = "room", Steam = "player", Token = "token" }, "server");
            int errors = 0;
            session.Error = delegate(string e) { errors++; };
            http.Reply("enter", "ok", "1", "mode", "host");
            http.Reply("download", "ok", "1", "version", "3", "summary", "s", "save", "\u0001");
            session.Online = true;
            session.Enter("save");
            Drain(jobs);
            Drain(jobs);
            Check(game.Hosted == 1, "did not host");
            game.LiveCode = "123456";
            // Two seconds of frames at 60 fps, the jobs answered between frames as on a fast link.
            double t = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                t += 1.0 / 60;
                session.Tick(t);
                Drain(jobs);
            }
            Check(http.Count("live") == 1, "live sent " + http.Count("live") + " times in 2 s");
            Check(errors == 1, "error shown " + errors + " times in 2 s");
            Console.WriteLine("ok refused live code is not resent every frame");

            http.Reply("live", "ok", "1");
            http.Reply("beat", "ok", "1");
            session.Tick(t + session.BeatSeconds);
            Drain(jobs);
            Check(http.Count("live") == 2, "live not retried after BeatSeconds");
            session.Tick(t + session.BeatSeconds + 1);
            Drain(jobs);
            Check(http.Count("live") == 2, "accepted live code sent again");
            Console.WriteLine("ok refused live code is retried at the beat pace");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine("FAIL live retry: " + e.Message);
        }
        finally
        {
            jobs.Dispose();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception) { }
        }
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
