using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using OriCoop.Rooms;

static class RoomTests
{
    sealed class FakeHttp : IRoomHttp
    {
        sealed class Step
        {
            public string Op;
            public Func<RoomWire.Fields, byte[]> Reply;
        }

        readonly Queue<Step> steps = new Queue<Step>();
        public readonly List<string> Ops = new List<string>();
        public readonly List<RoomWire.Fields> Requests = new List<RoomWire.Fields>();
        public readonly List<int> Timeouts = new List<int>();

        public void Expect(string op, Func<RoomWire.Fields, byte[]> reply)
        {
            lock (steps) steps.Enqueue(new Step { Op = op, Reply = reply });
        }

        public void Reply(string op, params object[] fields)
        {
            byte[] response = Fields(fields).Encode();
            Expect(op, delegate(RoomWire.Fields request) { return response; });
        }

        public byte[] Post(string op, RoomWire.Fields fields, int timeoutMs)
        {
            byte[] body = fields.Encode();
            Step step;
            lock (steps)
            {
                Ops.Add(op);
                Requests.Add(RoomWire.Fields.Decode(body));
                Timeouts.Add(timeoutMs);
                if (steps.Count == 0) throw new Exception("unexpected operation " + op);
                step = steps.Dequeue();
            }
            if (step.Op != op) throw new Exception("expected " + step.Op + ", got " + op);
            return step.Reply(RoomWire.Fields.Decode(body));
        }

        public void Done()
        {
            lock (steps) Check(steps.Count == 0, "expected requests remain");
        }
    }

    sealed class FakeClock
    {
        public double Now;
        public void Advance(RoomSession session, double seconds)
        {
            Now += seconds;
            session.Tick(Now);
        }
    }

    sealed class LoopbackServer : IDisposable
    {
        readonly TcpListener listener;
        readonly Thread worker;
        readonly byte[] response;
        readonly int status;
        Exception error;
        public string Headers;
        public byte[] Body;

        public LoopbackServer(int statusCode, byte[] responseBytes)
        {
            status = statusCode;
            response = responseBytes;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            worker = new Thread(Serve);
            worker.IsBackground = true;
            worker.Start();
        }

        public string Url
        {
            get
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                return new UriBuilder("http", IPAddress.Loopback.ToString(), port, "base/").Uri.ToString();
            }
        }

        void Serve()
        {
            try
            {
                using (TcpClient peer = listener.AcceptTcpClient())
                using (NetworkStream stream = peer.GetStream())
                using (MemoryStream head = new MemoryStream())
                {
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;
                    int matched = 0;
                    while (matched < 4)
                    {
                        int current = stream.ReadByte();
                        if (current < 0) throw new IOException("request header ended early");
                        head.WriteByte((byte)current);
                        int expected = matched == 0 || matched == 2 ? 13 : 10;
                        matched = current == expected ? matched + 1 : (current == 13 ? 1 : 0);
                    }
                    Headers = Encoding.ASCII.GetString(head.ToArray());
                    int length = 0;
                    string[] lines = Headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = int.Parse(line.Substring("Content-Length:".Length).Trim());
                    }
                    if (Headers.IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        byte[] go = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        stream.Write(go, 0, go.Length);
                    }
                    Body = new byte[length];
                    int offset = 0;
                    while (offset < Body.Length)
                    {
                        int n = stream.Read(Body, offset, Body.Length - offset);
                        if (n <= 0) throw new IOException("request body ended early");
                        offset += n;
                    }
                    string statusText = status == 200 ? "OK" : "Service Unavailable";
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + statusText
                        + "\r\nContent-Type: application/octet-stream\r\nContent-Length: " + response.Length
                        + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(response, 0, response.Length);
                }
            }
            catch (Exception ex) { error = ex; }
        }

        public void Complete()
        {
            Check(worker.Join(5000), "server did not finish");
            if (error != null) throw new Exception("server: " + error.Message);
        }

        public void Dispose()
        {
            listener.Stop();
            worker.Join(5000);
        }
    }

    sealed class FakeGame : IRoomGame
    {
        public string LiveCode { get; set; }
        public bool InGameplay { get; set; }
        public bool Together { get; set; }
        public long PlayedSeconds { get; set; }
        public string ConflictMessage { get { return "conflict"; } }
        public int Slot = 2;
        public int Hosted = -1;
        public string Joined;
        public string Shown;
        public byte[] SlotData;
        public byte[] Copied;
        public long SlotVersion;
        public byte[] SnapshotData = new byte[] { 9, 8 };
        public int WriteRoomSlot(string room, string save, long version, byte[] data)
        {
            SlotData = data;
            SlotVersion = version;
            return Slot;
        }
        public void UpdateVersion(int slot, string room, string save, long version) { SlotVersion = version; }
        public void Host(int slot, string server) { Hosted = slot; }
        public void Join(string target) { Joined = target; }
        public byte[] Snapshot(out string summary) { summary = "snapshot"; return SnapshotData; }
        public void CopySingle(byte[] data) { Copied = data; }
        public void Message(string text) { Shown = text; }
    }

    sealed class Fixture : IDisposable
    {
        public readonly string PathName = Path.Combine(Path.GetTempPath(), "oricoop-rooms-" + Guid.NewGuid().ToString("N"));
        public readonly FakeHttp Http = new FakeHttp();
        public readonly FakeClock Clock = new FakeClock();
        public readonly FakeGame Game = new FakeGame();
        public readonly RoomJobs Jobs = new RoomJobs();
        public readonly RoomAuth Auth = new RoomAuth { Room = "room", Steam = "player", Token = "token" };
        public readonly RoomLocalStore Store;
        public readonly RoomClient Client;
        public readonly RoomSession Session;
        public string Error;

        public Fixture()
        {
            Store = new RoomLocalStore(PathName);
            Client = new RoomClient(Http);
            Session = new RoomSession(Client, Jobs, Store, Game, Auth, "server");
            Session.Error = delegate(string error) { Error = error; };
        }

        public void PumpUntil(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < deadline)
            {
                Jobs.Pump();
                Thread.Sleep(1);
            }
            Jobs.Pump();
            Check(done(), "timed out waiting for room job");
        }

        public void Drain()
        {
            bool done = false;
            Jobs.Add(delegate { return new RoomReply { Fields = new RoomWire.Fields() }; },
                delegate(RoomReply reply) { done = true; });
            PumpUntil(delegate { return done; });
        }

        public void Host()
        {
            Http.Reply("enter", "ok", "1", "mode", "host");
            Http.Reply("download", "ok", "1", "version", "3", "save", new byte[] { 1, 2 }, "summary", "initial");
            Session.Online = true;
            Session.Enter("save");
            PumpUntil(delegate { return Game.Hosted >= 0; });
        }

        public void Dispose()
        {
            Jobs.Dispose();
            if (Directory.Exists(PathName)) Directory.Delete(PathName, true);
        }
    }

    static RoomWire.Fields Fields(params object[] pairs)
    {
        RoomWire.Fields f = new RoomWire.Fields();
        for (int i = 0; i < pairs.Length; i += 2)
        {
            if (pairs[i + 1] is byte[]) f.Set((string)pairs[i], (byte[])pairs[i + 1]);
            else f.Set((string)pairs[i], (string)pairs[i + 1]);
        }
        return f;
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!object.Equals(expected, actual)) throw new Exception("expected " + expected + ", got " + actual);
    }

    static void Bytes(byte[] expected, byte[] actual)
    {
        Check(RoomWire.Equal(expected, actual), "bytes differ");
    }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("expected " + typeof(T).Name);
    }

    static void WireFixture()
    {
        RoomWire.Fields f = Fields("a", "hi", "b", new byte[] { 0, 255 });
        Bytes(new byte[] { 1, 97, 2, 0, 0, 0, 104, 105, 1, 98, 2, 0, 0, 0, 0, 255 }, f.Encode());
        RoomWire.Fields decoded = RoomWire.Fields.Decode(f.Encode());
        Equal("hi", decoded.GetString("a"));
        Bytes(new byte[] { 0, 255 }, decoded.GetBytes("b"));
    }

    static void WireUtf8AndNumbers()
    {
        RoomWire.Fields f = new RoomWire.Fields();
        f.Set("name", "中文");
        f.Set("version", -123L);
        RoomWire.Fields decoded = RoomWire.Fields.Decode(f.Encode());
        Equal("中文", decoded.GetString("name"));
        Equal(-123L, decoded.GetLong("version", 8));
        Equal(8L, decoded.GetLong("missing", 8));
        Check(!decoded.Has("missing"), "missing field present");
    }

    static void WireOverwrite()
    {
        RoomWire.Fields f = Fields("a", "old", "b", "middle");
        f.Set("a", "new");
        RoomWire.Fields decoded = RoomWire.Fields.Decode(f.Encode());
        Equal("new", decoded.GetString("a"));
        Equal("middle", decoded.GetString("b"));
        Equal((byte)'a', f.Encode()[1]);
    }

    static void WireBadNames()
    {
        Throws<FormatException>(delegate { Fields("", "x").Encode(); });
        Throws<FormatException>(delegate { Fields(new string('a', 256), "x").Encode(); });
        Throws<FormatException>(delegate { RoomWire.Fields.Decode(new byte[] { 0 }); });
        Throws<FormatException>(delegate { RoomWire.Fields.Decode(new byte[] { 2, 97 }); });
    }

    static void WireTruncatedLength()
    {
        Throws<FormatException>(delegate { RoomWire.Fields.Decode(new byte[] { 1, 97, 1, 0, 0 }); });
    }

    static void WireTruncatedValue()
    {
        Throws<FormatException>(delegate { RoomWire.Fields.Decode(new byte[] { 1, 97, 2, 0, 0, 0, 42 }); });
    }

    static void WireProof()
    {
        byte[] key = Encoding.ASCII.GetBytes("key");
        byte[] input = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
        Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", RoomWire.Hex(RoomWire.Proof(key, input)));
        Check(!RoomWire.Equal(new byte[] { 1 }, new byte[] { 1, 0 }), "lengths must differ");
    }

    static void StoreRoom()
    {
        using (Fixture x = new Fixture())
        {
            RoomBackupInfo info = new RoomBackupInfo { Id = "room", Name = "name", Server = "server", Role = "host", Steam = "player", Seats = 4, Solo = true };
            info.Members.Add(new RoomBackupMember { Seat = 2, Steam = "other", Nick = "nick" });
            info.Saves.Add(new RoomBackupSave { Id = "save", Name = "slot", Version = 8, Summary = "summary" });
            x.Store.SaveRoom(info);
            info.Name = "renamed";
            x.Store.SaveRoom(info);
            List<RoomBackupInfo> list = x.Store.ListRooms();
            Equal(1, list.Count);
            Equal("renamed", list[0].Name);
            Equal(4, list[0].Seats);
            Check(list[0].Solo, "solo lost");
            Equal("nick", list[0].Members[0].Nick);
            Equal(8L, list[0].Saves[0].Version);
            Equal("summary", list[0].Saves[0].Summary);
        }
    }

    static void StoreToken()
    {
        using (Fixture x = new Fixture())
        {
            Equal<string>(null, x.Store.LoadToken("room"));
            x.Store.SaveToken("room", "first");
            x.Store.SaveToken("room", "second");
            Equal("second", x.Store.LoadToken("room"));
        }
    }

    static void StoreVersions()
    {
        using (Fixture x = new Fixture())
        {
            long version;
            string summary;
            Check(x.Store.LoadLatest("room", "save", out version, out summary) == null, "unexpected save");
            Equal(0L, version);
            x.Store.StoreVersion("room", "save", 1, new byte[] { 1 }, "first");
            x.Store.StoreVersion("room", "save", 2, new byte[] { 2, 3 }, "second");
            Bytes(new byte[] { 2, 3 }, x.Store.LoadLatest("room", "save", out version, out summary));
            Equal(2L, version);
            Equal("second", summary);
        }
    }

    static void StoreHistoryRetention()
    {
        using (Fixture x = new Fixture())
        {
            for (int i = 1; i <= 12; i++) x.Store.StoreVersion("room", "save", i, new byte[] { (byte)i }, "");
            string history = Path.Combine(Path.Combine(Path.Combine(Path.Combine(x.PathName, "room"), "saves"), "save"), "history");
            Equal(10, Directory.GetFiles(history, "v*.sav").Length);
            Check(!File.Exists(Path.Combine(history, "v2.sav")), "old history retained");
            Check(File.Exists(Path.Combine(history, "v3.sav")), "new history deleted");
        }
    }

    static void StoreDeleteAndId()
    {
        using (Fixture x = new Fixture())
        {
            Throws<ArgumentException>(delegate { x.Store.SaveToken("../outside", "x"); });
            Throws<ArgumentException>(delegate { x.Store.DeleteRoom(".."); });
            x.Store.SaveToken("room", "token");
            x.Store.DeleteRoom("room");
            Check(x.Store.LoadToken("room") == null, "token survived delete");
        }
    }

    static void ClientHello()
    {
        FakeHttp http = new FakeHttp();
        http.Reply("hello", "ok", "1", "message", "welcome");
        RoomClient client = new RoomClient(http);
        RoomReply reply = client.Hello();
        Check(reply.Ok, "hello failed");
        Equal("welcome", reply.Message);
        Equal("1", http.Requests[0].GetString("proto"));
        Equal("OriCoop", http.Requests[0].GetString("client"));
        // T-200: hello only asks whether the service is there (the 我的房间 page waits for it): its own short timeout,
        // never longer than the general one; every other call keeps the general timeout.
        Equal(5000, http.Timeouts[0]);
        http.Reply("info", "ok", "1");
        client.Info(new RoomAuth { Room = "room", Steam = "player", Token = "token" });
        Equal(15000, http.Timeouts[1]);
        client.TimeoutMs = 2000;
        http.Reply("hello", "ok", "1");
        client.Hello();
        Equal(2000, http.Timeouts[2]);
        http.Done();
    }

    // T-200 (playtest 4): requests abandoned by leaving the 我的房间 page stay queued behind the one that is running. A job
    // nobody waits for when its turn comes is not run (its reply is "cancelled"); the jobs after it run as usual.
    static void JobsSkipUnwanted()
    {
        using (RoomJobs jobs = new RoomJobs())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            int abandonedRan = 0;
            bool waiting = true;
            List<string> replies = new List<string>();
            Action<RoomReply> record = delegate(RoomReply reply) { replies.Add(reply.Ok ? reply.Message : "error " + reply.Error); };
            jobs.Add(delegate
            {
                started.Set();
                release.WaitOne(5000, false);   // still waiting for the server's answer
                return new RoomReply { Ok = true, Message = "first", Fields = new RoomWire.Fields() };
            }, record);
            jobs.Add(delegate
            {
                abandonedRan++;
                return new RoomReply { Ok = true, Message = "abandoned", Fields = new RoomWire.Fields() };
            }, record, delegate { return waiting; });
            jobs.Add(delegate { return new RoomReply { Ok = true, Message = "next", Fields = new RoomWire.Fields() }; },
                record, delegate { return true; });
            Check(started.WaitOne(5000, false), "the first job did not start");
            waiting = false;                    // the page was left while the first request was on its way
            release.Set();
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (replies.Count < 3 && DateTime.UtcNow < until) { jobs.Pump(); Thread.Sleep(1); }
            Equal(3, replies.Count);
            Equal("first", replies[0]);
            Equal("error cancelled", replies[1]);
            Equal("next", replies[2]);
            Equal(0, abandonedRan);
        }
    }

    // T-200: the 我的房间 page stops waiting for the room service's first refresh after the limit, counted without a break.
    static void BusyWatchLimit()
    {
        RoomBusyWatch watch = new RoomBusyWatch(8);
        Equal(8.0, watch.LimitSeconds);
        Check(!watch.Expired(false, 0), "expired while nothing waits");
        Check(!watch.Expired(true, 10), "expired when the wait started");
        Check(!watch.Expired(true, 17.9), "expired before the limit");
        Check(watch.Expired(true, 18), "not expired at the limit");
        Check(!watch.Expired(true, 18.1), "expired twice for one wait");
        Check(!watch.Expired(false, 19), "expired after the wait ended");
        Check(!watch.Expired(true, 30), "a new wait expired at once");
        Check(!watch.Expired(true, 37), "expired before the new wait's limit");
        Check(!watch.Expired(false, 37.5), "expired on the break");
        Check(!watch.Expired(true, 37.6), "a wait after a break expired at once");
        Check(!watch.Expired(true, 45.5), "a break did not restart the count");
        Check(watch.Expired(true, 45.6), "not expired after the restarted count");
        // The page reopened in the same frame it was left (the wait never paused): the new refresh gets its own limit.
        Check(!watch.Expired(true, 50), "expired when the next wait started");
        Check(!watch.Expired(true, 57), "expired before the limit");
        watch.Restart();
        Check(!watch.Expired(true, 57.5), "a restarted wait expired at once");
        Check(!watch.Expired(true, 65.4), "a restart did not restart the count");
        Check(watch.Expired(true, 65.5), "not expired after the restart's limit");
    }

    static void ClientCreate()
    {
        FakeHttp http = new FakeHttp();
        http.Expect("create", delegate(RoomWire.Fields f)
        {
            Equal("player", f.GetString("steam"));
            Equal("nick", f.GetString("nick"));
            Equal("name", f.GetString("name"));
            Equal("slot", f.GetString("savename"));
            Bytes(new byte[] { 3, 4 }, f.GetBytes("save"));
            Equal(16, f.GetBytes("salt").Length);
            Bytes(RoomWire.Verifier("pass", f.GetBytes("salt")), f.GetBytes("verifier"));
            return Fields("ok", "1").Encode();
        });
        RoomReply r = new RoomClient(http).Create("player", "nick", "name", "pass", new byte[] { 3, 4 }, "summary", "slot");
        Check(r.Ok, r.Message);
        Equal(120000, http.Timeouts[0]);
        http.Done();
    }

    static void ClientShortPassword()
    {
        FakeHttp http = new FakeHttp();
        RoomReply r = new RoomClient(http).Create("player", "nick", "name", "abc", null, "", "");
        Check(!r.Ok, "short password accepted");
        Equal("bad_request", r.Error);
        Equal(0, http.Ops.Count);
    }

    static void ClientJoinChallenge()
    {
        FakeHttp http = new FakeHttp();
        byte[] salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        byte[] nonce = new byte[] { 9, 10, 11 };
        http.Reply("challenge", "ok", "1", "salt", salt, "nonce", nonce);
        http.Expect("join", delegate(RoomWire.Fields f)
        {
            Equal("code", f.GetString("code"));
            Equal("player", f.GetString("steam"));
            Bytes(nonce, f.GetBytes("nonce"));
            Bytes(RoomWire.Proof(RoomWire.Verifier("pass", salt), nonce), f.GetBytes("proof"));
            return Fields("ok", "1", "token", "new-token").Encode();
        });
        RoomReply r = new RoomClient(http).Join("code", "pass", "player", "nick");
        Check(r.Ok, r.Message);
        Equal("new-token", r.Fields.GetString("token"));
        Equal("code", http.Requests[0].GetString("code"));
        http.Done();
    }

    static void ClientChallengeError()
    {
        FakeHttp http = new FakeHttp();
        http.Reply("challenge", "ok", "0", "error", "missing", "message", "not found");
        RoomReply r = new RoomClient(http).Join("code", "pass", "player", "nick");
        Check(!r.Ok, "error reported as success");
        Equal("missing", r.Error);
        Equal("not found", r.Message);
        Equal(1, http.Ops.Count);
        http.Done();
    }

    static void ClientNetworkError()
    {
        FakeHttp http = new FakeHttp();
        http.Expect("hello", delegate(RoomWire.Fields f) { throw new IOException("disconnected"); });
        RoomReply r = new RoomClient(http).Hello();
        Check(!r.Ok, "network error reported as success");
        Equal("network", r.Error);
        Equal("disconnected", r.Message);
        Check(r.Fields != null, "missing error fields");
    }

    static void ClientMalformedReply()
    {
        FakeHttp http = new FakeHttp();
        http.Expect("hello", delegate(RoomWire.Fields f) { return new byte[] { 2, 97 }; });
        RoomReply r = new RoomClient(http).Hello();
        Equal("network", r.Error);
        Check(!r.Ok, "malformed response reported as success");
    }

    static void ClientUpload()
    {
        FakeHttp http = new FakeHttp();
        http.Reply("upload", "ok", "1", "version", "5");
        RoomAuth auth = new RoomAuth { Room = "room", Steam = "player", Token = "token" };
        RoomReply r = new RoomClient(http).Upload(auth, "save", 4, new byte[] { 5 }, "summary", 12, true);
        Check(r.Ok, "upload failed");
        RoomWire.Fields f = http.Requests[0];
        Equal("room", f.GetString("room"));
        Equal("player", f.GetString("steam"));
        Equal("token", f.GetString("token"));
        Equal("save", f.GetString("save"));
        Equal(4L, f.GetLong("base", -1));
        Equal(12L, f.GetLong("secs", -1));
        Equal("1", f.GetString("together"));
        Bytes(new byte[] { 5 }, f.GetBytes("data"));
        Equal(120000, http.Timeouts[0]);
        http.Done();
    }

    static void ClientRealHttp()
    {
        using (LoopbackServer server = new LoopbackServer(200, Fields("ok", "1", "message", "welcome").Encode()))
        {
            RoomClient client = new RoomClient(server.Url);
            RoomReply reply = client.Hello();
            server.Complete();
            Check(reply.Ok, reply.Message);
            Equal("welcome", reply.Message);
            Check(server.Headers.StartsWith("POST /base/v1/hello HTTP/1.1\r\n"), "wrong method or path");
            Check(server.Headers.IndexOf("Content-Type: application/octet-stream", StringComparison.OrdinalIgnoreCase) >= 0,
                "wrong content type");
            RoomWire.Fields request = RoomWire.Fields.Decode(server.Body);
            Equal("1", request.GetString("proto"));
            Equal("OriCoop", request.GetString("client"));
        }
    }

    static void ClientRealHttpError()
    {
        using (LoopbackServer server = new LoopbackServer(503, new byte[0]))
        {
            RoomReply reply = new RoomClient(server.Url).Hello();
            server.Complete();
            Check(!reply.Ok, "HTTP error reported as success");
            Equal("network", reply.Error);
            Check(reply.Message.Length > 0, "HTTP error message missing");
        }
    }

    static void ClientRealHttpOversized()
    {
        using (LoopbackServer server = new LoopbackServer(200, new byte[RoomWire.MaximumBodyBytes + 1]))
        {
            RoomReply reply = new RoomClient(server.Url).Hello();
            server.Complete();
            Equal("network", reply.Error);
            Equal("rooms reply too large", reply.Message);
        }
    }

    static void JobsSerialAndPump()
    {
        using (RoomJobs jobs = new RoomJobs())
        using (ManualResetEvent worked = new ManualResetEvent(false))
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            int worker = 0;
            List<int> callbacks = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                int number = i;
                jobs.Add(delegate
                {
                    int thread = Thread.CurrentThread.ManagedThreadId;
                    if (worker == 0) worker = thread;
                    if (worker != thread || thread == caller) throw new Exception("worker changed");
                    if (number == 2) worked.Set();
                    return new RoomReply { Ok = true, Message = number.ToString(), Fields = new RoomWire.Fields() };
                }, delegate(RoomReply reply)
                {
                    Check(reply.Ok, "worker failure");
                    Equal(caller, Thread.CurrentThread.ManagedThreadId);
                    callbacks.Add(int.Parse(reply.Message));
                });
            }
            Check(worked.WaitOne(5000, false), "worker did not finish");
            Equal(0, callbacks.Count);
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (callbacks.Count < 3 && DateTime.UtcNow < until) { jobs.Pump(); Thread.Sleep(1); }
            Equal(3, callbacks.Count);
            Equal(0, callbacks[0]);
            Equal(1, callbacks[1]);
            Equal(2, callbacks[2]);
        }
    }

    static void JobsException()
    {
        using (Fixture x = new Fixture())
        {
            RoomReply result = null;
            x.Jobs.Add(delegate { throw new InvalidOperationException("failed"); },
                delegate(RoomReply reply) { result = reply; });
            x.PumpUntil(delegate { return result != null; });
            Check(!result.Ok, "exception reported as success");
            Equal("network", result.Error);
            Check(result.Fields != null, "missing error fields");
        }
    }

    static void SessionOfflineAndBusy()
    {
        using (Fixture x = new Fixture())
        {
            x.Session.Enter("save");
            Equal("network", x.Error);
            Check(!x.Session.Active, "offline session active");
            x.Http.Reply("enter", "ok", "1", "mode", "wait");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.Session.Enter("other");
            Equal("busy", x.Error);
            Equal("save", x.Session.Save);
            x.Drain();
        }
    }

    static void SessionHello()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("hello", "ok", "1");
            bool done = false;
            x.Session.Hello(delegate(bool online) { Check(online, "offline"); done = true; });
            x.PumpUntil(delegate { return done; });
            Check(x.Session.Online, "online not set");
        }
    }

    static void SessionEnterError()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("enter", "ok", "0", "error", "denied");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.PumpUntil(delegate { return x.Error != null; });
            Equal("denied", x.Error);
            Check(!x.Session.Active, "session still active");
        }
    }

    static void SessionHost()
    {
        using (Fixture x = new Fixture())
        {
            x.Host();
            Check(x.Session.Active && x.Session.Holder, "not hosting");
            Equal(3L, x.Session.Version);
            Equal(2, x.Game.Hosted);
            Bytes(new byte[] { 1, 2 }, x.Game.SlotData);
            long version;
            string summary;
            Bytes(new byte[] { 1, 2 }, x.Store.LoadLatest("room", "save", out version, out summary));
            Equal(3L, version);
            x.Http.Done();
        }
    }

    static void SessionJoin()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("enter", "ok", "1", "mode", "join", "live", "code");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.PumpUntil(delegate { return x.Game.Joined != null; });
            Equal("server|code", x.Game.Joined);
            Check(x.Session.Active && !x.Session.Holder, "join state incorrect");
            x.Http.Done();
        }
    }

    static void SessionWaitingJoin()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("enter", "ok", "1", "mode", "wait");
            x.Http.Reply("beat", "ok", "1", "mode", "join", "live", "code");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.Drain();
            x.Clock.Advance(x.Session, 1.9);
            Equal(1, x.Http.Ops.Count);
            x.Clock.Advance(x.Session, 0.1);
            x.PumpUntil(delegate { return x.Game.Joined != null; });
            Equal("server|code", x.Game.Joined);
            Equal("1", x.Http.Requests[1].GetString("waiting"));
            x.Http.Done();
        }
    }

    static void SessionWaitingExpires()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("enter", "ok", "1", "mode", "wait");
            x.Http.Reply("leave", "ok", "1");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.Drain();
            x.Clock.Advance(x.Session, 30);
            x.PumpUntil(delegate { return !x.Session.Active; });
            Equal("waiting", x.Error);
            x.Http.Done();
        }
    }

    static void SessionLeave()
    {
        using (Fixture x = new Fixture())
        {
            x.Host();
            x.Http.Reply("leave", "ok", "1");
            x.Session.Leave();
            x.PumpUntil(delegate { return !x.Session.Active; });
            Check(!x.Session.Holder, "holder remains set");
            Equal("save", x.Http.Requests[2].GetString("save"));
            x.Http.Done();
        }
    }

    static void SessionLiveAndBeat()
    {
        using (Fixture x = new Fixture())
        {
            x.Host();
            x.Game.LiveCode = "code";
            x.Http.Reply("live", "ok", "1");
            x.Http.Reply("beat", "ok", "1");
            x.Clock.Advance(x.Session, 29);
            x.Drain();
            Equal("code", x.Http.Requests[2].GetString("live"));
            x.Clock.Advance(x.Session, 1);
            x.Drain();
            Equal("0", x.Http.Requests[3].GetString("waiting"));
            x.Http.Done();
        }
    }

    static void SessionUpload()
    {
        using (Fixture x = new Fixture())
        {
            x.Game.PlayedSeconds = 10;
            x.Host();
            x.Game.PlayedSeconds = 25;
            x.Game.Together = true;
            x.Http.Reply("upload", "ok", "1", "version", "4");
            x.Session.Saved(new byte[] { 7, 8 }, "updated");
            x.PumpUntil(delegate { return x.Session.Version == 4; });
            Equal(4L, x.Game.SlotVersion);
            Equal(15L, x.Http.Requests[2].GetLong("secs", -1));
            Equal("1", x.Http.Requests[2].GetString("together"));
            long version;
            string summary;
            Bytes(new byte[] { 7, 8 }, x.Store.LoadLatest("room", "save", out version, out summary));
            Equal("updated", summary);
            x.Http.Done();
        }
    }

    static void SessionQueuedUploadsBeforeLeave()
    {
        using (Fixture x = new Fixture())
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            x.Host();
            x.Http.Expect("upload", delegate(RoomWire.Fields request)
            {
                entered.Set();
                if (!release.WaitOne(5000, false)) throw new Exception("upload not released");
                return Fields("ok", "1", "version", "4").Encode();
            });
            x.Http.Reply("upload", "ok", "1", "version", "5");
            x.Http.Reply("leave", "ok", "1");
            x.Session.Saved(new byte[] { 7 }, "first");
            try
            {
                Check(entered.WaitOne(5000, false), "upload did not start");
                x.Session.Saved(new byte[] { 8 }, "superseded");
                x.Session.Saved(new byte[] { 9 }, "latest");
                x.Session.Leave();
                Equal(3, x.Http.Ops.Count);
                Check(x.Session.Active, "left before upload finished");
            }
            finally { release.Set(); }
            x.PumpUntil(delegate { return !x.Session.Active; });
            Equal("upload", x.Http.Ops[2]);
            Equal("upload", x.Http.Ops[3]);
            Equal("leave", x.Http.Ops[4]);
            Equal(3L, x.Http.Requests[2].GetLong("base", -1));
            Equal(4L, x.Http.Requests[3].GetLong("base", -1));
            Bytes(new byte[] { 7 }, x.Http.Requests[2].GetBytes("data"));
            Bytes(new byte[] { 9 }, x.Http.Requests[3].GetBytes("data"));
            Equal(5L, x.Game.SlotVersion);
            long version;
            string summary;
            Bytes(new byte[] { 9 }, x.Store.LoadLatest("room", "save", out version, out summary));
            Equal("latest", summary);
            x.Http.Done();
        }
    }

    static void SessionConflict()
    {
        using (Fixture x = new Fixture())
        {
            x.Host();
            x.Http.Reply("upload", "ok", "0", "error", "version_conflict");
            x.Session.Saved(new byte[] { 7 }, "updated");
            x.PumpUntil(delegate { return x.Session.UploadStopped; });
            Equal("conflict", x.Game.Shown);
            x.Session.Saved(new byte[] { 8 }, "ignored");
            Equal(3, x.Http.Ops.Count);
            x.Http.Done();
        }
    }

    static void SessionRebind()
    {
        using (Fixture x = new Fixture())
        {
            // A room entry is still a foreground operation until Host/Join actually starts.
            x.Game.InGameplay = true;
            byte[] salt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            x.Http.Reply("enter", "ok", "0", "error", "bad_token");
            x.Http.Reply("challenge", "ok", "1", "salt", salt, "nonce", new byte[] { 9 });
            x.Http.Reply("rebind", "ok", "1", "token", "replacement");
            x.Http.Reply("enter", "ok", "1", "mode", "join", "live", "code");
            x.Session.Password = delegate(Action<string> done) { done("pass"); };
            x.Session.Online = true;
            x.Session.Enter("save");
            x.PumpUntil(delegate { return x.Game.Joined != null; });
            Equal("replacement", x.Auth.Token);
            Equal("replacement", x.Store.LoadToken("room"));
            Equal("replacement", x.Http.Requests[3].GetString("token"));
            x.Http.Done();
        }
    }

    static void SharedPasswordPrompt(bool cancel)
    {
        using (Fixture x = new Fixture())
        {
            int prompts = 0, completed = 0;
            Action<string> submit = null;
            x.Session.Password = delegate(Action<string> action) { prompts++; submit = action; };
            for (int i = 0; i < 2; i++)
            {
                x.Http.Reply("info", "ok", "0", "error", "bad_token");
                x.Session.Call(delegate { return x.Client.Info(x.Auth); }, delegate(RoomReply r)
                {
                    Check(cancel ? r.Error == "cancelled" : r.Ok, "unexpected rebind result");
                    completed++;
                });
            }
            x.Drain();
            Equal(1, prompts);
            if (!cancel)
            {
                x.Http.Reply("challenge", "ok", "1", "salt", new byte[8], "nonce", new byte[] { 9 });
                x.Http.Reply("rebind", "ok", "1", "token", "replacement");
                x.Http.Reply("info", "ok", "1");
                x.Http.Reply("info", "ok", "1");
            }
            submit(cancel ? null : "4321");
            x.PumpUntil(delegate { return completed == 2; });
            Equal(1, prompts);
            x.Http.Done();
        }
    }

    static void SessionWaitingDeadline()
    {
        using (Fixture x = new Fixture())
        {
            x.Http.Reply("enter", "ok", "1", "mode", "wait");
            x.Http.Reply("beat", "ok", "1", "mode", "wait");
            x.Http.Reply("beat", "ok", "1", "mode", "wait");
            x.Http.Reply("leave", "ok", "1");
            x.Session.Online = true;
            x.Session.Enter("save");
            x.Drain();
            x.Clock.Advance(x.Session, 2);
            x.Drain();
            x.Clock.Advance(x.Session, 2);
            x.Drain();
            x.Clock.Advance(x.Session, 26);
            x.PumpUntil(delegate { return !x.Session.Active; });
            Equal("waiting", x.Error);
            x.Http.Done();
        }
    }

    static void SessionCancelPending()
    {
        using (Fixture x = new Fixture())
        using (ManualResetEvent entered = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            x.Http.Expect("enter", delegate(RoomWire.Fields f)
            {
                entered.Set();
                if (!release.WaitOne(5000, false)) throw new Exception("enter not released");
                return Fields("ok", "1", "mode", "host").Encode();
            });
            x.Http.Reply("leave", "ok", "1");
            x.Session.Online = true;
            x.Session.Enter("save");
            try
            {
                Check(entered.WaitOne(5000, false), "enter did not start");
                x.Session.Cancel();
            }
            finally { release.Set(); }
            x.PumpUntil(delegate { return !x.Session.Active; });
            Equal(-1, x.Game.Hosted);
            Check(!x.Session.Holder, "cancelled session is holder");
            x.Http.Done();
        }
    }

    static void SessionCopyOffline()
    {
        using (Fixture x = new Fixture())
        {
            x.Session.Copy("save");
            Equal("no_backup", x.Error);
            x.Store.StoreVersion("room", "save", 1, new byte[] { 5 }, "backup");
            bool copied = false;
            x.Session.Copied = delegate { copied = true; };
            x.Session.Copy("save");
            Check(copied, "copy callback missing");
            Bytes(new byte[] { 5 }, x.Game.Copied);
        }
    }

    static void SessionExpiredPasswordReply()
    {
        using (Fixture x = new Fixture())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            bool wanted = true, prompted = false, completed = false;
            x.Session.Password = delegate { prompted = true; };
            x.Http.Expect("info", delegate
            {
                started.Set(); release.WaitOne(5000, false);
                return Fields("ok", "0", "error", "bad_token").Encode();
            });
            x.Session.Call(delegate { return x.Client.Info(x.Auth); }, delegate { completed = true; }, delegate { return wanted; });
            try { Check(started.WaitOne(5000, false), "info did not start"); wanted = false; }
            finally { release.Set(); }
            x.Drain();
            Check(!prompted && !completed, "abandoned info opened a password page or completed");
        }
    }

    static void FailedHostDownloadReleasesBeforeRetry()
    {
        using (Fixture x = new Fixture())
        {
            x.Session.Online = true;
            x.Http.Reply("enter", "ok", "1", "mode", "host");
            x.Http.Reply("download", "ok", "0", "error", "network");
            x.Http.Reply("leave", "ok", "1");
            x.Session.Enter("save");
            x.PumpUntil(delegate { return x.Error != null; });
            Check(x.Error == "network" && !x.Session.Active && !x.Session.Holder && x.Game.Hosted == -1,
                "failed initial download still owns the room when retry becomes available");
            x.Http.Done();
            x.Host();
            Check(x.Game.Hosted == 2, "retry did not acquire the room and host");
            x.Http.Done();
        }
    }

    static void ExpiredRebindDoesNotReplaceCurrentToken()
    {
        using (Fixture x = new Fixture())
        using (ManualResetEvent started = new ManualResetEvent(false))
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            bool wanted = true, completed = false;
            x.Http.Reply("info", "ok", "0", "error", "bad_token");
            x.Http.Reply("challenge", "ok", "1", "salt", new byte[8], "nonce", new byte[] { 1 });
            x.Http.Expect("rebind", delegate
            {
                started.Set(); release.WaitOne(5000, false);
                return Fields("ok", "1", "token", "stale").Encode();
            });
            x.Session.Password = delegate(Action<string> enter) { enter("4321"); };
            x.Session.Call(delegate { return x.Client.Info(x.Auth); }, delegate { completed = true; }, delegate { return wanted; });
            try
            {
                x.PumpUntil(delegate { return started.WaitOne(0, false); });
                wanted = false; x.Store.SaveToken("room", "newer");
            }
            finally { release.Set(); }
            x.Drain();
            Check(!completed && x.Auth.Token == "token" && x.Store.LoadToken("room") == "newer",
                "late password rebind overwrote the newer room token");
        }
    }

    static void BackgroundBadTokenDoesNotOpenPasswordPage()
    {
        using (Fixture x = new Fixture())
        {
            x.Host(); x.Game.InGameplay = true;
            bool prompted = false;
            x.Session.Password = delegate { prompted = true; };
            x.Http.Reply("upload", "ok", "0", "error", "bad_token");
            x.Session.Saved(new byte[] { 4 }, "checkpoint");
            x.Drain();
            Check(!prompted && x.Error == "bad_token" && x.Session.Active,
                "in-game backup should report its authentication failure and keep playing");
        }
    }

    static int failures;
    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }

    static int Main()
    {
        Run("wire fixture round trip", WireFixture);
        Run("wire UTF-8 and numbers", WireUtf8AndNumbers);
        Run("wire overwrite", WireOverwrite);
        Run("wire invalid names", WireBadNames);
        Run("wire truncated length", WireTruncatedLength);
        Run("wire truncated value", WireTruncatedValue);
        Run("wire HMAC proof", WireProof);
        Run("store room metadata", StoreRoom);
        Run("store token", StoreToken);
        Run("store latest version", StoreVersions);
        Run("store history retention", StoreHistoryRetention);
        Run("store deletion and ids", StoreDeleteAndId);
        Run("client hello", ClientHello);
        Run("client create", ClientCreate);
        Run("client rejects short password", ClientShortPassword);
        Run("client join challenge", ClientJoinChallenge);
        Run("client challenge error", ClientChallengeError);
        Run("client network error", ClientNetworkError);
        Run("client malformed reply", ClientMalformedReply);
        Run("client authenticated upload", ClientUpload);
        Run("client real HTTP request", ClientRealHttp);
        Run("client real HTTP error", ClientRealHttpError);
        Run("client real HTTP oversized reply", ClientRealHttpOversized);
        Run("jobs serial worker and pump", JobsSerialAndPump);
        Run("jobs work exception", JobsException);
        Run("jobs skip abandoned requests", JobsSkipUnwanted);
        Run("busy watch limit", BusyWatchLimit);
        Run("session offline and busy", SessionOfflineAndBusy);
        Run("session hello", SessionHello);
        Run("session enter error", SessionEnterError);
        Run("session host", SessionHost);
        Run("session join", SessionJoin);
        Run("session waiting joins", SessionWaitingJoin);
        Run("session waiting expires", SessionWaitingExpires);
        Run("session waiting keeps deadline", SessionWaitingDeadline);
        Run("session cancel pending enter", SessionCancelPending);
        Run("session leave", SessionLeave);
        Run("session live and heartbeat", SessionLiveAndBeat);
        Run("session upload", SessionUpload);
        Run("session queues uploads before leave", SessionQueuedUploadsBeforeLeave);
        Run("session upload conflict", SessionConflict);
        Run("session rebind", SessionRebind);
        Run("concurrent requests share one password and rebind", delegate { SharedPasswordPrompt(false); });
        Run("cancelling the password answers every waiting request", delegate { SharedPasswordPrompt(true); });
        Run("abandoned info does not ask for a password", SessionExpiredPasswordReply);
        Run("abandoned rebind preserves the newer token", ExpiredRebindDoesNotReplaceCurrentToken);
        Run("in-game upload reports authentication failure", BackgroundBadTokenDoesNotOpenPasswordPage);
        Run("failed initial download releases its lease before retry", FailedHostDownloadReleasesBeforeRetry);
        Run("session offline copy", SessionCopyOffline);
        return failures == 0 ? 0 : 1;
    }
}
