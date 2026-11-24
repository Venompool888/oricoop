using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace OriCoop.Net
{
    public sealed class FakeSteamApi : ISteamApi, IDisposable
    {
        private const int MaxPayload = 1200;
        private const int HeaderSize = 13;
        private const byte Magic = 0x53;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        private sealed class Lobby
        {
            public ulong Owner;
            public int Limit;
            public readonly List<KeyValuePair<ulong, long>> Members = new List<KeyValuePair<ulong, long>>();
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            // The owner opened the invite dialog for this lobby: only then can a friend "accept an invite" to it.
            public bool Invited;
        }

        private sealed class LobbyCache
        {
            public long NextRead;
            public Lobby Snapshot;
            public List<ulong> Visible = new List<ulong>();
        }

        private sealed class Operation
        {
            public long Due;
            public Func<bool> Run;
        }

        private sealed class Packet
        {
            public ulong Sender;
            public int Channel;
            public byte[] Data;
        }

        private sealed class WaitingSession
        {
            public bool Notified;
            public readonly List<Packet> Packets = new List<Packet>();
        }

        private readonly string dir;
        private readonly Socket socket;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<ulong, LobbyCache> lobbies = new Dictionary<ulong, LobbyCache>();
        private readonly Queue<Operation> operations = new Queue<Operation>();
        private readonly HashSet<ulong> accepted = new HashSet<ulong>();
        private readonly Dictionary<ulong, WaitingSession> waiting = new Dictionary<ulong, WaitingSession>();
        private readonly List<Packet> ready = new List<Packet>();
        private readonly byte[] receiveBuffer = new byte[65535];
        private ulong lobbyCounter;
        private bool dropCreate, dropEnter, noSession, autoInvite, invited;
        private readonly bool noOverlay;
        private readonly bool inviteThrows;   // T-218: the invite window throws when it is opened
        // T-253 (fault no-rerequest): CloseP2PSession on a user whose session was never accepted is final: that user's
        // later packets are dropped and no new P2PSessionRequested comes. Steam's documentation says a new request
        // comes for the next packet; the sixth playtest could not show that it does, so the transport must not need it.
        private readonly bool noRerequest;
        private readonly HashSet<ulong> refusedForGood = new HashSet<ulong>();
        private long lateMembers, nextInviteScan;
        // T-216: the first CreateLobby / JoinLobby result comes this many ms late (fault late-create:<s> / late-enter:<s>).
        private long lateCreate, lateEnter;
        private readonly List<Operation> late = new List<Operation>();

        public FakeSteamApi(string dir, ulong id, string name, string faults)
        {
            this.dir = dir;
            LocalId = id;
            PersonaName = string.IsNullOrEmpty(name) ? DefaultName(id) : name;
            LoggedOn = true;
            foreach (string fault in (faults ?? "").Split(','))
            {
                if (fault == "drop-create") dropCreate = true;
                else if (fault == "drop-enter") dropEnter = true;
                else if (fault == "no-session") noSession = true;
                else if (fault == "no-overlay") noOverlay = true;
                else if (fault == "invite-throws") inviteThrows = true;
                else if (fault == "no-rerequest") noRerequest = true;
                else if (fault.StartsWith("late-members:", StringComparison.Ordinal))
                    lateMembers = (long)(double.Parse(fault.Substring("late-members:".Length), Invariant) * 1000);
                else if (fault.StartsWith("late-create:", StringComparison.Ordinal))
                    lateCreate = (long)(double.Parse(fault.Substring("late-create:".Length), Invariant) * 1000);
                else if (fault.StartsWith("late-enter:", StringComparison.Ordinal))
                    lateEnter = (long)(double.Parse(fault.Substring("late-enter:".Length), Invariant) * 1000);
            }
            Directory.CreateDirectory(dir);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                socket.Blocking = false;
                WriteAtomic(PeerPath(id), new[] { ((IPEndPoint)socket.LocalEndPoint).Port.ToString(Invariant) });
            }
            catch { socket.Close(); throw; }
        }

        internal static FakeSteamApi FromEnvironment(string dir)
        {
            ulong id = ulong.Parse(Environment.GetEnvironmentVariable("ORICOOP_FAKE_STEAM_ID"), NumberStyles.None, Invariant);
            if (id == 0) throw new ArgumentOutOfRangeException("id");
            return new FakeSteamApi(dir, id, Environment.GetEnvironmentVariable("ORICOOP_FAKE_STEAM_NAME"),
                Environment.GetEnvironmentVariable("ORICOOP_FAKE_STEAM_FAULT"))
            { autoInvite = Environment.GetEnvironmentVariable("ORICOOP_FAKE_STEAM_AUTOINVITE") == "1" };
        }

        public bool LoggedOn { get; set; }
        public ulong LocalId { get; private set; }
        public string PersonaName { get; private set; }
        public bool OverlayEnabled { get { return !noOverlay; } }
        public int InviteDialogsOpened { get; private set; }
        private static string DefaultName(ulong id) { return "fake-" + id.ToString(Invariant); }
        public string FriendName(ulong id) { return id == LocalId ? PersonaName : DefaultName(id); }

        public event Action<ulong, ulong> JoinRequested;
        public event Action<bool, string, ulong, bool> LobbyCreated;
        public event Action<ulong, bool, string, bool> LobbyEntered;
        public event Action<ulong, ulong, string> LobbyMemberChanged;
        public event Action<ulong> P2PSessionRequested;
        public event Action<ulong, string> P2PSessionFailed { add { } remove { } }
        public event Action<Exception> WrapperError { add { } remove { } }

        private string PeerPath(ulong id) { return Path.Combine(dir, "peer-" + id.ToString(Invariant) + ".txt"); }
        private string LobbyPath(ulong id) { return Path.Combine(dir, "lobby-" + id.ToString(Invariant) + ".txt"); }
        private static long NowMs { get { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; } }

        private static void WriteAtomic(string path, string[] lines)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllLines(temp, lines);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { File.Delete(temp); }
        }

        private Lobby ReadLobby(ulong id)
        {
            try
            {
                using (var file = new FileStream(LobbyPath(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file))
                {
                    var lobby = new Lobby { Owner = ulong.Parse(reader.ReadLine(), Invariant), Limit = int.Parse(reader.ReadLine(), Invariant) };
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split(new[] { ' ' }, 3);
                        if (parts[0] == "member") lobby.Members.Add(new KeyValuePair<ulong, long>(ulong.Parse(parts[1], Invariant), long.Parse(parts[2], Invariant)));
                        else if (parts[0] == "data") lobby.Data[parts[1]] = parts[2];
                        else if (parts[0] == "invited") lobby.Invited = true;
                    }
                    return lobby;
                }
            }
            catch (FileNotFoundException) { return null; }
        }

        private void WriteLobby(ulong id, Lobby lobby)
        {
            if (lobby.Members.Count == 0) { File.Delete(LobbyPath(id)); return; }
            var lines = new List<string> { lobby.Owner.ToString(Invariant), lobby.Limit.ToString(Invariant) };
            foreach (var member in lobby.Members) lines.Add("member " + member.Key.ToString(Invariant) + " " + member.Value.ToString(Invariant));
            foreach (var entry in lobby.Data) lines.Add("data " + entry.Key + " " + entry.Value);
            if (lobby.Invited) lines.Add("invited");
            WriteAtomic(LobbyPath(id), lines.ToArray());
        }

        private LobbyCache GetLobby(ulong id, bool force = false)
        {
            LobbyCache cache;
            if (!lobbies.TryGetValue(id, out cache)) lobbies.Add(id, cache = new LobbyCache());
            if (!force && clock.ElapsedMilliseconds < cache.NextRead) return cache;
            cache.NextRead = clock.ElapsedMilliseconds + 100;
            Lobby lobby;
            try { lobby = ReadLobby(id); }
            catch (IOException) { return cache; }
            var before = cache.Visible;
            var visible = new List<ulong>();
            if (lobby != null)
            {
                long now = NowMs;
                foreach (var member in lobby.Members)
                    if (member.Key == LocalId || member.Value <= now) visible.Add(member.Key);
            }
            cache.Snapshot = lobby;
            cache.Visible = visible;
            var changed = LobbyMemberChanged;
            if (changed != null)
            {
                foreach (ulong user in before) if (!visible.Contains(user)) changed(id, user, "Left");
                foreach (ulong user in visible) if (!before.Contains(user)) changed(id, user, "Entered");
            }
            return cache;
        }

        private bool EditLobby(ulong id, Func<Lobby, bool> edit)
        {
            // Serialize read/modify/replace across test processes so joins and data writes do not overwrite one another.
            bool wrote;
            using (var mutex = new Mutex(false, "OriCoop-FakeLobby-" + id.ToString(Invariant)))
            {
                mutex.WaitOne();
                try
                {
                    Lobby lobby = ReadLobby(id);
                    wrote = edit(lobby);
                    if (wrote) WriteLobby(id, lobby);
                }
                catch (IOException) { return false; }
                finally { mutex.ReleaseMutex(); }
            }
            if (wrote) GetLobby(id, true);
            return true;
        }

        private void Schedule(Func<bool> action)
        {
            operations.Enqueue(new Operation { Due = clock.ElapsedTicks + (Stopwatch.Frequency + 19) / 20, Run = action });
        }

        // Runs `action` from a Pump() at least `ms` from now, independent of the in-order operation queue.
        private void Later(long ms, Action action)
        {
            late.Add(new Operation { Due = clock.ElapsedTicks + ms * Stopwatch.Frequency / 1000, Run = delegate { action(); return true; } });
        }

        public void CreateLobby(int maxMembers)
        {
            ulong id = (LocalId << 20) + ++lobbyCounter;
            bool drop = dropCreate;
            dropCreate = false;
            long delay = lateCreate;
            lateCreate = 0;
            Schedule(delegate
            {
                var lobby = new Lobby { Owner = LocalId, Limit = maxMembers };
                lobby.Members.Add(new KeyValuePair<ulong, long>(LocalId, NowMs));
                try { WriteLobby(id, lobby); }
                catch (IOException) { return false; }
                GetLobby(id, true);
                Action result = delegate
                {
                    if (LobbyCreated != null) LobbyCreated(true, "OK", id, false);
                    if (LobbyEntered != null) LobbyEntered(id, true, "Success", false);
                };
                if (drop) return true;
                if (delay > 0) Later(delay, result); else result();
                return true;
            });
        }

        public void JoinLobby(ulong id)
        {
            bool drop = dropEnter;
            dropEnter = false;
            long delay = lateEnter;
            lateEnter = 0;
            Schedule(delegate
            {
                string result = "Success";
                if (!EditLobby(id, lobby =>
                {
                    if (lobby == null) { result = "DoesntExist"; return false; }
                    if (lobby.Members.Exists(member => member.Key == LocalId)) return false;
                    if (lobby.Members.Count >= lobby.Limit) { result = "Full"; return false; }
                    // A joining peer sees itself immediately; other readers wait for this visible-from timestamp.
                    lobby.Members.Add(new KeyValuePair<ulong, long>(LocalId, NowMs + lateMembers));
                    return true;
                })) return false;
                Action raise = delegate { if (LobbyEntered != null) LobbyEntered(id, result == "Success", result, false); };
                if (drop) return true;
                if (delay > 0) Later(delay, raise); else raise();
                return true;
            });
        }

        public void LeaveLobby(ulong id)
        {
            EditLobby(id, lobby =>
            {
                if (lobby == null) return false;
                if (lobby.Owner == LocalId) { lobby.Members.Clear(); return true; }
                return lobby.Members.RemoveAll(member => member.Key == LocalId) != 0;
            });
        }

        public bool SetLobbyData(ulong id, string key, string value)
        {
            bool found = false;
            return EditLobby(id, lobby =>
            {
                if (lobby == null) return false;
                lobby.Data[key] = value;
                return found = true;
            }) && found;
        }

        public string GetLobbyData(ulong id, string key)
        {
            Lobby lobby = GetLobby(id).Snapshot;
            string value;
            return lobby != null && lobby.Data.TryGetValue(key, out value) ? value : "";
        }

        public ulong GetLobbyOwner(ulong id)
        {
            Lobby lobby = GetLobby(id).Snapshot;
            return lobby == null ? 0 : lobby.Owner;
        }

        public int GetNumLobbyMembers(ulong id) { return GetLobby(id).Visible.Count; }
        public ulong GetLobbyMemberByIndex(ulong id, int index)
        {
            List<ulong> members = GetLobby(id).Visible;
            return index >= 0 && index < members.Count ? members[index] : 0;
        }

        public void OpenInviteDialog(ulong lobby)
        {
            InviteDialogsOpened++;
            Console.WriteLine("Fake Steam: invite dialog for lobby " + lobby);
            if (inviteThrows) throw new InvalidOperationException("fake Steam: the invite window cannot open");
            // The automatic invite of another copy (ORICOOP_FAKE_STEAM_AUTOINVITE) only goes to a lobby its owner
            // invited to: a lobby the owner never learnt about (a lost CreateLobby result) invites nobody.
            EditLobby(lobby, delegate(Lobby value)
            {
                if (value == null || value.Owner != LocalId || value.Invited) return false;
                value.Invited = true;
                return true;
            });
        }

        private void ScanInvites()
        {
            if (!autoInvite || invited || lobbyCounter != 0 || JoinRequested == null || clock.ElapsedMilliseconds < nextInviteScan) return;
            nextInviteScan = clock.ElapsedMilliseconds + 100;
            string[] files;
            try { files = Directory.GetFiles(dir, "lobby-*.txt"); }
            catch (IOException) { return; }
            foreach (string file in files)
            {
                ulong id;
                if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file).Substring("lobby-".Length), NumberStyles.None, Invariant, out id)) continue;
                Lobby lobby = GetLobby(id).Snapshot;
                if (lobby == null || lobby.Owner == LocalId || !lobby.Invited) continue;
                invited = true;
                JoinRequested(id, lobby.Owner);
                return;
            }
        }

        public bool AllowP2PPacketRelay(bool allow) { return true; }

        public bool AcceptP2PSession(ulong user)
        {
            refusedForGood.Remove(user);
            accepted.Add(user);
            WaitingSession session;
            if (waiting.TryGetValue(user, out session))
            {
                ready.AddRange(session.Packets);
                waiting.Remove(user);
            }
            return true;
        }

        public bool CloseP2PSession(ulong user)
        {
            if (noRerequest && !accepted.Contains(user) && waiting.ContainsKey(user)) refusedForGood.Add(user);
            accepted.Remove(user);
            waiting.Remove(user);
            ready.RemoveAll(packet => packet.Sender == user);
            return true;
        }

        public bool SendP2PPacket(ulong user, byte[] data, int offset, int length, int channel)
        {
            if (length > MaxPayload) return false;
            try
            {
                int port = int.Parse(File.ReadAllText(PeerPath(user)), Invariant);
                var packet = new byte[HeaderSize + length];
                packet[0] = Magic;
                for (int i = 0; i < 8; i++) packet[1 + i] = (byte)(LocalId >> (i * 8));
                for (int i = 0; i < 4; i++) packet[9 + i] = (byte)(channel >> (i * 8));
                Buffer.BlockCopy(data, offset, packet, HeaderSize, length);
                socket.SendTo(packet, new IPEndPoint(IPAddress.Loopback, port));
                return AcceptP2PSession(user);
            }
            catch (IOException) { return false; }
            catch (SocketException) { return false; }
        }

        private void ReceivePackets()
        {
            EndPoint from = new IPEndPoint(IPAddress.Loopback, 0);
            while (socket.Poll(0, SelectMode.SelectRead))
            {
                int size;
                try { size = socket.ReceiveFrom(receiveBuffer, ref from); }
                catch (SocketException) { return; }
                if (size < HeaderSize || size > HeaderSize + MaxPayload || receiveBuffer[0] != Magic) continue;
                ulong sender = 0;
                int channel = 0;
                for (int i = 0; i < 8; i++) sender |= (ulong)receiveBuffer[1 + i] << (i * 8);
                for (int i = 0; i < 4; i++) channel |= receiveBuffer[9 + i] << (i * 8);
                List<Packet> queue = ready;
                if (!accepted.Contains(sender))
                {
                    if (refusedForGood.Contains(sender)) continue;
                    WaitingSession session;
                    if (!waiting.TryGetValue(sender, out session)) waiting.Add(sender, session = new WaitingSession());
                    queue = session.Packets;
                    if (queue.Count >= 64) continue;
                }
                byte[] data = new byte[size - HeaderSize];
                Buffer.BlockCopy(receiveBuffer, HeaderSize, data, 0, data.Length);
                queue.Add(new Packet { Sender = sender, Channel = channel, Data = data });
            }
        }

        public bool IsP2PPacketAvailable(out uint size, int channel)
        {
            ReceivePackets();
            Packet packet = ready.Find(p => p.Channel == channel);
            size = packet == null ? 0 : (uint)packet.Data.Length;
            return packet != null;
        }

        public bool ReadP2PPacket(byte[] buffer, out uint size, out ulong sender, int channel)
        {
            ReceivePackets();
            size = 0;
            sender = 0;
            int index = ready.FindIndex(p => p.Channel == channel);
            if (index < 0) return false;
            Packet packet = ready[index];
            ready.RemoveAt(index);
            size = (uint)Math.Min(packet.Data.Length, buffer.Length);
            sender = packet.Sender;
            Buffer.BlockCopy(packet.Data, 0, buffer, 0, (int)size);
            return true;
        }

        public void Pump()
        {
            for (int i = operations.Count; i > 0 && clock.ElapsedTicks >= operations.Peek().Due; i--)
            {
                if (!operations.Peek().Run()) break;
                operations.Dequeue();
            }
            for (int i = late.Count - 1; i >= 0; i--)
            {
                if (clock.ElapsedTicks < late[i].Due) continue;
                Operation due = late[i];
                late.RemoveAt(i);
                due.Run();
            }
            foreach (ulong id in new List<ulong>(lobbies.Keys)) GetLobby(id);
            ScanInvites();
            ReceivePackets();
            if (noSession || P2PSessionRequested == null) return;
            foreach (ulong user in new List<ulong>(waiting.Keys))
            {
                WaitingSession session;
                if (P2PSessionRequested != null && waiting.TryGetValue(user, out session) && !session.Notified)
                {
                    session.Notified = true;
                    P2PSessionRequested(user);
                }
            }
        }

        public void Dispose()
        {
            socket.Close();
            File.Delete(PeerPath(LocalId));
        }
    }
}
