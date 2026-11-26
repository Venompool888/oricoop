using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using OriCoop.Net;

namespace OriCoop { internal static class Players { internal const int MaxSeats = 4; } }

// The game adapter is compiled by build-commit, not loaded in this console suite.
namespace OriCoop.Net
{
    internal static class RealSteamApi
    {
        internal static ISteamApi GetCurrent() { return null; }
    }
}

static class SteamTests
{
    private static readonly byte[] Payload = { 1, 2, 3 };
    private static int cases, failures;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Until(Func<bool> condition, string message, params FakeSteamApi[] peers)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            foreach (FakeSteamApi peer in peers) peer.Pump();
            if (condition()) return;
            Thread.Sleep(5);
        } while (clock.ElapsedMilliseconds < 600);
        throw new Exception(message);
    }

    private static void PumpFor(int milliseconds, FakeSteamApi peer)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do { peer.Pump(); Thread.Sleep(5); } while (clock.ElapsedMilliseconds < milliseconds);
    }

    private static ulong Create(FakeSteamApi host, int max)
    {
        ulong lobby = 0;
        Action<bool, string, ulong, bool> created = (ok, result, id, io) =>
        {
            Check(ok && result == "OK" && !io, "create result");
            lobby = id;
        };
        host.LobbyCreated += created;
        host.CreateLobby(max);
        Until(() => lobby != 0, "no create result", host);
        host.LobbyCreated -= created;
        return lobby;
    }

    private static void Join(FakeSteamApi guest, ulong lobby, string expected)
    {
        bool entered = false;
        Action<ulong, bool, string, bool> handler = (id, ok, result, io) =>
        {
            Check(id == lobby && result == expected && ok == (expected == "Success") && !io, "join result: " + result);
            entered = true;
        };
        guest.LobbyEntered += handler;
        guest.JoinLobby(lobby);
        Check(!entered, "join completed inside JoinLobby");
        Until(() => entered, "no join result", guest);
        guest.LobbyEntered -= handler;
    }

    private static void FriendRoomDirectory(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, "Host", "no-overlay"))
        using (var guest = new FakeSteamApi(dir, 1002, "Guest", ""))
        using (var other = new FakeSteamApi(dir, 1003, "Other", ""))
        {
            ulong lobby = Create(host, 2);
            Check(host.SetLobbyData(lobby, "oricoop", "1") && host.SetLobbyData(lobby, "build", "build-a"), "publish version");
            SteamFriendRow[] matches = SteamDirectory.List(guest, true, "build-a");
            Check(matches.Length == 1 && matches[0].Lobby == lobby && matches[0].Reason == "", "discover a joinable friend room");
            Check(SteamDirectory.List(guest, true, "build-b")[0].Reason == "server.err.version", "version mismatch remains visible");
            Join(guest, lobby, "Success");
            host.SetMemberState(lobby, "loading");
            Until(() => guest.MemberState(lobby, host.LocalId) == "loading", "guest sees the host start without user input", guest);
            matches = SteamDirectory.List(other, true, "build-a");
            Check(matches.Length == 1 && matches[0].Reason == "lobby.error.full", "full friend room deduplicated and disabled");
            Check(SteamDirectory.List(host, false, "build-a").Length == 2, "fallback shows both friends");
            Check(!host.OverlayEnabled && host.Invite(lobby, guest.LocalId), "send targeted invite without overlay");
            Check(host.GetLobbyData(lobby, "invite." + guest.LocalId) == "1" &&
                host.GetLobbyData(lobby, "invite." + other.LocalId) == "", "only the selected friend was invited");
            host.LeaveLobby(lobby);
            Until(() => SteamDirectory.List(other, true, "build-a").Length == 0, "closed lobby disappears", other);
        }
    }

    private static void LobbyFlow(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, "fake-host", ""))
        using (var guest = new FakeSteamApi(dir, 1002, "fake-guest", ""))
        using (var other = new FakeSteamApi(dir, 1003, null, ""))
        {
            var events = new List<string>();
            ulong lobby = 0;
            Stopwatch clock = Stopwatch.StartNew();
            host.LobbyCreated += (ok, result, id, io) =>
            {
                Check(clock.ElapsedMilliseconds >= 50, "create delivered too early");
                Check(ok && result == "OK" && !io, "create result");
                events.Add("created:" + id);
                lobby = id;
            };
            host.LobbyEntered += (id, ok, result, io) =>
            {
                Check(ok && result == "Success" && !io, "host enter result");
                events.Add("entered:" + id);
            };
            host.CreateLobby(2);
            Check(events.Count == 0, "create completed inside CreateLobby");
            Until(() => events.Count >= 2, "missing create/enter", host);
            Check(events.Count == 2 && events[0] == "created:" + lobby && events[1] == "entered:" + lobby, "create/enter order");
            Check(host.SetLobbyData(lobby, "oricoop", "1"), "set lobby data");
            var changes = new List<string>();
            host.LobbyMemberChanged += (id, user, change) =>
            {
                if (id == lobby && user == guest.LocalId) changes.Add(change);
            };
            Join(guest, lobby, "Success");
            Until(() => host.GetNumLobbyMembers(lobby) == 2, "host does not see guest", host);
            Check(guest.GetNumLobbyMembers(lobby) == 2 && guest.GetLobbyData(lobby, "oricoop") == "1", "guest members/data");
            Check(host.GetLobbyOwner(lobby) == host.LocalId && guest.GetLobbyOwner(lobby) == host.LocalId, "lobby owner");
            Check(host.GetLobbyMemberByIndex(lobby, 1) == guest.LocalId && changes.Count == 1 && changes[0] == "Entered", "member entered");
            Join(other, lobby, "Full");
            Join(other, lobby + 99, "DoesntExist");
            guest.LeaveLobby(lobby);
            Check(guest.GetNumLobbyMembers(lobby) == 1, "leave did not refresh own cache");
            Until(() => host.GetNumLobbyMembers(lobby) == 1, "host did not see leave", host);
            Check(changes.Count == 2 && changes[1] == "Left", "member left");
            host.LeaveLobby(lobby);
            Join(other, lobby, "DoesntExist");
        }
    }

    private static void DropCreate(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, "drop-create"))
        {
            int events = 0;
            host.LobbyCreated += (ok, result, id, io) => events++;
            host.LobbyEntered += (id, ok, result, io) => events++;
            host.CreateLobby(2);
            PumpFor(150, host);
            Check(events == 0, "first create result was not dropped");
            Create(host, 2);
            Check(events == 2, "second create did not emit both results");
        }
    }

    private static void DropEnter(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, "drop-enter"))
        {
            ulong lobby = Create(host, 2);
            int events = 0;
            guest.LobbyEntered += (id, ok, result, io) => events++;
            guest.JoinLobby(lobby);
            PumpFor(150, guest);
            Check(events == 0, "first enter result was not dropped");
            Join(guest, lobby, "Success");
            Check(events == 1, "second join did not emit a result");
        }
    }

    private static void LateMembers(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, "late-members:0.3"))
        {
            ulong lobby = Create(host, 2);
            Join(guest, lobby, "Success");
            Check(guest.GetNumLobbyMembers(lobby) == 2 && host.GetNumLobbyMembers(lobby) == 1, "late guest not hidden from host");
            Until(() => host.GetNumLobbyMembers(lobby) == 2, "late guest never became visible", host);
        }
    }

    private static bool Available(FakeSteamApi peer, int channel)
    {
        uint size;
        return peer.IsP2PPacketAvailable(out size, channel);
    }

    private static void ReadPacket(FakeSteamApi peer, ulong from, byte[] expected, int channel)
    {
        byte[] actual = new byte[1200];
        uint size;
        ulong sender;
        Check(peer.ReadP2PPacket(actual, out size, out sender, channel), "no packet to read");
        Check(size == expected.Length && sender == from, "P2P packet metadata");
        for (int i = 0; i < expected.Length; i++) Check(actual[i] == expected[i], "P2P payload");
    }

    private static void P2P(string dir)
    {
        using (var sender = new FakeSteamApi(dir, 1001, null, ""))
        using (var receiver = new FakeSteamApi(dir, 1002, null, ""))
        {
            const int channel = 7;
            int requests = 0;
            receiver.P2PSessionRequested += id => { Check(id == sender.LocalId, "wrong session request sender"); requests++; };
            Check(sender.SendP2PPacket(receiver.LocalId, Payload, 1, 1, channel), "send first");
            Check(sender.SendP2PPacket(receiver.LocalId, Payload, 2, 1, channel), "send second");
            Until(() => requests == 1, "no session request", receiver);
            PumpFor(50, receiver);
            Check(requests == 1 && !Available(receiver, channel), "pending packets visible or repeated request");
            Check(receiver.AcceptP2PSession(sender.LocalId), "accept failed");
            Check(!Available(receiver, 0), "wrong channel visible");
            ReadPacket(receiver, sender.LocalId, new byte[] { 2 }, channel);
            ReadPacket(receiver, sender.LocalId, new byte[] { 3 }, channel);
            Check(sender.SendP2PPacket(receiver.LocalId, Payload, 0, 1, channel), "send accepted packet");
            Until(() => Available(receiver, channel), "accepted packet missing", receiver);
            Check(receiver.CloseP2PSession(sender.LocalId) && !Available(receiver, channel), "close did not discard ready packet");
            Check(sender.SendP2PPacket(receiver.LocalId, Payload, 0, 1, channel), "send after close");
            Until(() => requests == 2, "no new request after close", receiver);
            receiver.CloseP2PSession(sender.LocalId);
            receiver.AcceptP2PSession(sender.LocalId);
            Check(!Available(receiver, channel), "close did not discard pending packet");
            Check(!sender.SendP2PPacket(receiver.LocalId, new byte[1201], 0, 1201, channel), "oversized packet accepted");
            Check(!sender.SendP2PPacket(1003, Payload, 0, 1, channel), "unknown peer accepted");
        }
    }

    private static void NoSession(string dir)
    {
        using (var sender = new FakeSteamApi(dir, 1001, null, ""))
        using (var receiver = new FakeSteamApi(dir, 1002, null, "no-session"))
        {
            receiver.P2PSessionRequested += id => { throw new Exception("session request was not suppressed"); };
            Check(sender.SendP2PPacket(receiver.LocalId, Payload, 0, Payload.Length, 0), "send with no-session");
            PumpFor(50, receiver);
            Check(!Available(receiver, 0), "packet visible without a session");
            Check(receiver.SendP2PPacket(sender.LocalId, Payload, 0, Payload.Length, 0), "send back with no-session");
            Until(() => Available(receiver, 0), "outbound send did not accept session", receiver);
            ReadPacket(receiver, sender.LocalId, Payload, 0);
        }
    }

    private static void TransportHost(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, ""))
        {
            SteamApi.SetForTests(host);
            using (SteamTransport transport = SteamTransport.Host())
            {
                Until(() => { SteamTransport.PumpFake(); return transport.Ready; }, "transport host not ready");
                Join(guest, transport.LobbyId, "Success");
                Until(() => host.GetNumLobbyMembers(transport.LobbyId) == 2, "transport host cannot see guest", host);
                Check(guest.SendP2PPacket(host.LocalId, Payload, 0, Payload.Length, 0), "guest send failed");
                byte[] actual = new byte[1200];
                EndPoint from = null;
                Until(() => { transport.Update(); return transport.Receive(actual, out from) == Payload.Length; }, "host receive failed", host);
                Check(from != null && transport.RemoteSteamId == guest.LocalId && actual[0] == 1 && actual[1] == 2 && actual[2] == 3, "host receive metadata/payload");
                transport.SendTo(Payload, 1, 2, from);
                Until(() => Available(guest, 0), "guest did not receive host reply", guest);
                ReadPacket(guest, host.LocalId, new byte[] { 2, 3 }, 0);
            }
        }
    }

    private static void InviteOverlay(string dir, string faults, bool enabled)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, faults))
        {
            SteamApi.SetForTests(host);
            using (SteamTransport transport = SteamTransport.Host())
            {
                Until(() => { SteamTransport.PumpFake(); return transport.Ready; }, "transport host not ready");
                int before = host.InviteDialogsOpened;
                Check(transport.OpenInviteOverlay() == enabled, "wrong overlay availability");
                Check(host.InviteDialogsOpened == before + 1, "invite dialog was not opened once");
            }
        }
    }

    // T-218: an invite window that throws must not cost the host its lobby.
    private static void InviteThrows(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, "invite-throws"))
        {
            SteamApi.SetForTests(host);
            using (SteamTransport transport = SteamTransport.Host())
            {
                Until(() => { SteamTransport.PumpFake(); return transport.Ready; }, "transport host not ready");
                ulong lobby = transport.LobbyId;
                Check(!transport.OpenInviteOverlay(), "a throwing invite window must report 'not shown'");
                transport.Update();
                Check(transport.Ready && transport.Error == null && transport.LobbyId == lobby && host.GetNumLobbyMembers(lobby) == 1,
                    "the lobby survives a throwing invite window (" + transport.Error + ")");
            }
        }
    }

    // ---- T-216 orderings, with a Steam whose results the test raises by hand (the file fake cannot reorder them).
    private sealed class ManualSteam : ISteamApi
    {
        public readonly List<ulong> Left = new List<ulong>();
        public int Creates;
        public bool LoggedOn { get { return true; } }
        public ulong LocalId { get { return 1002; } }
        public string PersonaName { get { return "me"; } }
        public string FriendName(ulong id) { return "friend"; }
        public void CreateLobby(int maxMembers) { Creates++; }
        public void JoinLobby(ulong lobby) { }
        public void LeaveLobby(ulong lobby) { Left.Add(lobby); }
        public bool SetLobbyData(ulong lobby, string key, string value) { return true; }
        public string GetLobbyData(ulong lobby, string key) { return "1"; }
        public ulong GetLobbyOwner(ulong lobby) { return 1001; }
        public int GetNumLobbyMembers(ulong lobby) { return 2; }
        public ulong GetLobbyMemberByIndex(ulong lobby, int index) { return index == 0 ? 1001UL : 1002UL; }
        public bool OverlayEnabled { get { return true; } }
        public void OpenInviteDialog(ulong lobby) { }
        public bool AllowP2PPacketRelay(bool allow) { return true; }
        public bool AcceptP2PSession(ulong user) { return true; }
        public bool CloseP2PSession(ulong user) { return true; }
        public bool SendP2PPacket(ulong user, byte[] data, int offset, int length, int channel) { return true; }
        public bool IsP2PPacketAvailable(out uint size, int channel) { size = 0; return false; }
        public bool ReadP2PPacket(byte[] buffer, out uint size, out ulong sender, int channel) { size = 0; sender = 0; return false; }
        public event Action<ulong, ulong> JoinRequested { add { } remove { } }
        public event Action<bool, string, ulong, bool> LobbyCreated;
        public event Action<ulong, bool, string, bool> LobbyEntered;
        public event Action<ulong, ulong, string> LobbyMemberChanged { add { } remove { } }
        public event Action<ulong> P2PSessionRequested { add { } remove { } }
        public event Action<ulong, string> P2PSessionFailed { add { } remove { } }
        public event Action<Exception> WrapperError { add { } remove { } }
        public void Pump() { }
        public void Created(bool ok, ulong lobby) { if (LobbyCreated != null) LobbyCreated(ok, ok ? "OK" : "Fail", lobby, false); }
        public void Entered(ulong lobby, bool ok) { if (LobbyEntered != null) LobbyEntered(lobby, ok, ok ? "Success" : "Error", false); }
        public string LeftText { get { return string.Join(",", Left.ConvertAll(id => id.ToString()).ToArray()); } }
    }

    // Lets the operation time out (test timeout 50 ms) and closes it, as NetPlay does before its retry.
    private static void GiveUp(SteamTransport operation, string what)
    {
        Thread.Sleep(80);
        operation.Update();
        Check(operation.Error != null && operation.Error.IndexOf("Timed out waiting for the Steam lobby", StringComparison.Ordinal) >= 0, what + ": did not time out (" + operation.Error + ")");
        operation.Dispose();
    }

    private static ManualSteam Manual()
    {
        var steam = new ManualSteam();
        SteamApi.SetForTests(steam);
        SteamTransport.SetTimeoutForTests(0.05);
        return steam;
    }

    private static void OldCreateResultFirst(string dir)
    {
        ManualSteam steam = Manual();
        GiveUp(SteamTransport.Host(), "old create first");
        using (SteamTransport retry = SteamTransport.Host())
        {
            steam.Created(true, 11);    // the given-up attempt's lobby arrives first: any lobby of ours will do
            Check(retry.Ready && retry.LobbyId == 11 && steam.Left.Count == 0, "old create first: the retry takes the first lobby (" + retry.LobbyId + ", left " + steam.LeftText + ")");
            steam.Created(true, 12);    // the retry's own lobby: nobody waits for it any more
            Check(steam.LeftText == "12" && retry.Ready && retry.LobbyId == 11, "old create first: the spare lobby is left, the live one kept (left " + steam.LeftText + ")");
        }
    }

    private static void OldCreateFailureFirst(string dir)
    {
        ManualSteam steam = Manual();
        GiveUp(SteamTransport.Host(), "old failure first");
        SteamTransport retry = SteamTransport.Host();
        steam.Created(false, 0);        // the given-up attempt's failure lands on the retry
        Check(retry.Error != null && !retry.Ready, "old failure first: the retry counts as failed");
        retry.Dispose();
        steam.Created(true, 12);        // the retry's real lobby comes afterwards: left, not kept as an unknown lobby
        Check(steam.LeftText == "12", "old failure first: the lobby that came after the failure is left (left " + steam.LeftText + ")");
        using (SteamTransport third = SteamTransport.Host())
        {
            Check(third.Error == null, "old failure first: the next attempt was refused: " + third.Error);
            steam.Created(true, 13);
            Check(third.Ready && third.LobbyId == 13 && steam.LeftText == "12", "old failure first: the next attempt works (" + third.LobbyId + ", left " + steam.LeftText + ")");
        }
    }

    private static void TwoGivenUpJoinsOfOneLobby(string dir)
    {
        ManualSteam steam = Manual();
        GiveUp(SteamTransport.Join(21), "two joins, first");
        GiveUp(SteamTransport.Join(21), "two joins, second");
        using (SteamTransport hosting = SteamTransport.Host())
        {
            steam.Created(true, 31);
            Check(hosting.Ready && hosting.LobbyId == 31, "two joins: own lobby is up");
            steam.Entered(21, false);   // the first join's late failure must not use up the only record
            Check(steam.Left.Count == 0, "two joins: a late failure leaves nothing (left " + steam.LeftText + ")");
            steam.Entered(21, true);    // the second join's late success: this PC is in the friend's lobby now
            Check(steam.LeftText == "21" && hosting.Ready && hosting.LobbyId == 31, "two joins: the late success is left (left " + steam.LeftText + ")");
        }
    }

    private static void LateJoinWhileJoiningAnother(string dir)
    {
        ManualSteam steam = Manual();
        GiveUp(SteamTransport.Join(21), "other lobby");
        using (SteamTransport second = SteamTransport.Join(22))
        {
            steam.Entered(21, true);    // arrives while the join of 22 is still waiting
            Check(steam.LeftText == "21" && !second.Ready && second.Error == null, "other lobby: the old lobby is left, the new join still waits (left " + steam.LeftText + ", " + second.Error + ")");
            steam.Entered(22, true);
            Check(second.Ready && second.LobbyId == 22 && second.RemoteSteamId == 1001 && steam.LeftText == "21", "other lobby: the new join is in (" + second.LobbyId + ", left " + steam.LeftText + ")");
        }
    }

    private static void NothingAwaitedAfterSteamRestart(string dir)
    {
        ManualSteam old = Manual();
        GiveUp(SteamTransport.Host(), "restart");
        SteamTransport stuck = SteamTransport.Host();       // one given-up create is on record, this one is waiting
        var fresh = new ManualSteam();
        SteamApi.SetForTests(fresh);                        // Ori restarted Steam: a new interface object
        using (SteamTransport next = SteamTransport.Host())
        {
            Check(next.Error == null && stuck.Error != null, "restart: the new interface starts clean (" + next.Error + ")");
            fresh.Created(true, 41);
            Check(next.Ready && next.LobbyId == 41, "restart: lobby on the new interface");
            fresh.Created(true, 42);                        // not awaited: the old interface's records are gone
            Check(fresh.Left.Count == 0 && old.Left.Count == 0, "restart: no record of the old interface is applied to the new one (left " + fresh.LeftText + ")");
        }
        stuck.Dispose();
    }

    private static void TransportGuest(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, ""))
        {
            ulong lobby = Create(host, 2);
            Check(host.SetLobbyData(lobby, "oricoop", "1"), "set lobby protocol");
            SteamApi.SetForTests(guest);
            using (SteamTransport transport = SteamTransport.Join(lobby))
            {
                Until(() => { SteamTransport.PumpFake(); return transport.Ready; }, "transport guest not ready");
                Check(transport.RemoteSteamId == host.LocalId, "transport guest has wrong host");
            }
        }
    }

    // ---- T-216: an operation that was given up must not block the next one; its late result is dropped.
    private static void PumpAll(int milliseconds, params FakeSteamApi[] peers)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            SteamTransport.PumpFake();
            foreach (FakeSteamApi peer in peers) peer.Pump();
            Thread.Sleep(5);
        }
    }

    private static SteamTransport TimedOut(SteamTransport first, string what)
    {
        Until(() => { SteamTransport.PumpFake(); first.Update(); return first.Error != null; }, what + ": the first attempt never timed out");
        Check(first.Error.IndexOf("Timed out waiting for the Steam lobby", StringComparison.Ordinal) >= 0, what + ": first attempt error '" + first.Error + "'");
        first.Dispose();
        return first;
    }

    private static void RetryAfterLostCreate(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, "drop-create"))
        {
            SteamApi.SetForTests(host);
            SteamTransport.SetTimeoutForTests(0.3);
            TimedOut(SteamTransport.Host(), "lost create");
            using (SteamTransport second = SteamTransport.Host())
            {
                Check(second.Error == null, "lost create: the retry was refused: " + second.Error);
                Until(() => { SteamTransport.PumpFake(); second.Update(); return second.Ready; }, "lost create: the retry never got its lobby");
                Check(second.LobbyId == (1001UL << 20) + 2 && host.GetNumLobbyMembers(second.LobbyId) == 1, "lost create: retry lobby " + second.LobbyId);
            }
        }
    }

    private static void LateCreateIsLeft(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, "late-create:0.7"))
        {
            SteamApi.SetForTests(host);
            SteamTransport.SetTimeoutForTests(0.2);
            ulong firstLobby = (1001UL << 20) + 1, secondLobby = firstLobby + 1;
            TimedOut(SteamTransport.Host(), "late create");
            using (SteamTransport second = SteamTransport.Host())
            {
                Check(second.Error == null, "late create: the retry was refused: " + second.Error);
                Until(() => { SteamTransport.PumpFake(); second.Update(); return second.Ready; }, "late create: the retry never got its lobby");
                Check(second.LobbyId == secondLobby, "late create: the retry took lobby " + second.LobbyId);
                Check(host.GetNumLobbyMembers(firstLobby) == 1, "late create: the first lobby exists before its late result");
                PumpAll(900);
                second.Update();
                Check(host.GetNumLobbyMembers(firstLobby) == 0, "late create: the lobby of the given-up attempt was left (" + host.GetNumLobbyMembers(firstLobby) + " member(s))");
                Check(second.Ready && second.Error == null && host.GetNumLobbyMembers(secondLobby) == 1, "late create: the live lobby is untouched (" + second.Error + ")");
            }
        }
    }

    private static void RetryAfterLostEnter(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, "drop-enter"))
        {
            ulong lobby = Create(host, 2);
            Check(host.SetLobbyData(lobby, "oricoop", "1"), "set lobby protocol");
            SteamApi.SetForTests(guest);
            SteamTransport.SetTimeoutForTests(0.3);
            TimedOut(SteamTransport.Join(lobby), "lost enter");
            using (SteamTransport second = SteamTransport.Join(lobby))
            {
                Check(second.Error == null, "lost enter: the retry was refused: " + second.Error);
                Until(() => { SteamTransport.PumpFake(); second.Update(); return second.Ready; }, "lost enter: the retry never got into the lobby", host);
                Check(second.RemoteSteamId == host.LocalId, "lost enter: retry has the wrong host");
            }
        }
    }

    // The player gave up joining and hosts instead; the late "you are in" of the old join must not leave this PC
    // sitting in the friend's lobby (it would take the friend's only free seat).
    private static void LateEnterAfterSwitchToHost(string dir)
    {
        using (var friend = new FakeSteamApi(dir, 1001, null, ""))
        using (var me = new FakeSteamApi(dir, 1002, null, "late-enter:0.7"))
        {
            ulong lobby = Create(friend, 2);
            Check(friend.SetLobbyData(lobby, "oricoop", "1"), "set lobby protocol");
            SteamApi.SetForTests(me);
            SteamTransport.SetTimeoutForTests(0.2);
            TimedOut(SteamTransport.Join(lobby), "late enter");
            Until(() => friend.GetNumLobbyMembers(lobby) == 2, "late enter: the friend never saw the half-finished join", friend);
            using (SteamTransport hosting = SteamTransport.Host())
            {
                Check(hosting.Error == null, "late enter: hosting was refused: " + hosting.Error);
                Until(() => { SteamTransport.PumpFake(); hosting.Update(); return hosting.Ready; }, "late enter: own lobby never came up");
                PumpAll(900, friend);
                hosting.Update();
                Check(friend.GetNumLobbyMembers(lobby) == 1, "late enter: this PC left the friend's lobby (" + friend.GetNumLobbyMembers(lobby) + " member(s))");
                Check(hosting.Ready && hosting.Error == null && me.GetNumLobbyMembers(hosting.LobbyId) == 1, "late enter: own lobby is untouched (" + hosting.Error + ")");
            }
        }
    }

    // A retry of the SAME join takes the first result that comes; the second one must not throw this PC out again.
    private static void LateEnterOfSameLobbyKeepsIt(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, "late-enter:0.7"))
        {
            ulong lobby = Create(host, 2);
            Check(host.SetLobbyData(lobby, "oricoop", "1"), "set lobby protocol");
            SteamApi.SetForTests(guest);
            SteamTransport.SetTimeoutForTests(0.2);
            TimedOut(SteamTransport.Join(lobby), "same lobby");
            using (SteamTransport second = SteamTransport.Join(lobby))
            {
                Until(() => { SteamTransport.PumpFake(); second.Update(); return second.Ready; }, "same lobby: the retry never got in", host);
                PumpAll(900, host);
                second.Update();
                Check(second.Ready && second.Error == null && host.GetNumLobbyMembers(lobby) == 2, "same lobby: still in the lobby after the late result (" + second.Error + ", " + host.GetNumLobbyMembers(lobby) + " member(s))");
            }
        }
    }

    // ---- T-253: the game handshake (NetPeer connect request / accept) over the Steam transport.
    // The guest side of a link without a SteamTransport (one live transport per process): sends to the host's Steam
    // id on channel 0 and accepts the host's session, as a guest's SteamTransport does.
    private sealed class RawLink : IDatagramTransport
    {
        public static readonly EndPoint Host = new IPEndPoint(IPAddress.Parse("192.0.2.2"), 1);
        private readonly FakeSteamApi steam;
        private readonly ulong host;
        public RawLink(FakeSteamApi steam, ulong host)
        {
            this.steam = steam;
            this.host = host;
            steam.P2PSessionRequested += id => { if (id == host) steam.AcceptP2PSession(id); };
        }
        public int LocalPort { get { return 0; } }
        public void SendTo(byte[] data, int offset, int length, EndPoint to) { steam.SendP2PPacket(host, data, offset, length, 0); }
        public int Receive(byte[] buffer, out EndPoint from)
        {
            from = null;
            uint size;
            ulong sender;
            if (!steam.IsP2PPacketAvailable(out size, 0) || !steam.ReadP2PPacket(buffer, out size, out sender, 0)) return 0;
            from = Host;
            return (int)size;
        }
        public void Dispose() { }
    }

    private static List<string> Capture()
    {
        var lines = new List<string>();
        SteamTransport.Trace += m => lines.Add("steam: " + m);
        NetPeer.Trace = m => lines.Add("peer: " + m);
        return lines;
    }

    private static bool Has(List<string> lines, string text)
    {
        foreach (string line in lines) if (line.IndexOf(text, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static string Text(List<string> lines) { return string.Join(" | ", lines.ToArray()); }

    // A host SteamTransport with its game handshake listening, and a guest that joined its lobby.
    private static SteamTransport HostWithGuest(FakeSteamApi host, FakeSteamApi guest, out NetPeer hostPeer)
    {
        SteamApi.SetForTests(host);
        SteamTransport transport = SteamTransport.Host();
        hostPeer = new NetPeer(transport);
        hostPeer.Listen();
        Until(() => { SteamTransport.PumpFake(); transport.Update(); return transport.Ready; }, "transport host not ready");
        Join(guest, transport.LobbyId, "Success");
        return transport;
    }

    private static bool Handshake(SteamTransport transport, NetPeer hostPeer, FakeSteamApi guest, NetPeer guestPeer, int milliseconds)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            SteamTransport.PumpFake();
            guest.Pump();
            hostPeer.Poll();
            guestPeer.Poll();
            if (hostPeer.State == PeerState.Connected && guestPeer.State == PeerState.Connected) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    // The sixth playtest: the host logged the guest's P2P session request, the guest's hello got no answer. The guest's
    // first packet comes before Steam lists the guest in the host's lobby; a Steam that does not raise a new request
    // after the host refused (closed) the first one never lets the handshake through.
    private static void GuestListedLate(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, "no-rerequest"))
        using (var guest = new FakeSteamApi(dir, 1002, null, "late-members:0.6"))
        {
            List<string> lines = Capture();
            NetPeer hostPeer;
            using (SteamTransport transport = HostWithGuest(host, guest, out hostPeer))
            {
                Check(host.GetNumLobbyMembers(transport.LobbyId) == 1, "late guest: the host already lists the guest");
                var guestPeer = new NetPeer(new RawLink(guest, host.LocalId));
                guestPeer.Connect(RawLink.Host);
                bool done = Handshake(transport, hostPeer, guest, guestPeer, 3000);
                Check(done, "late guest: the handshake never completed (host " + hostPeer.State + ", guest " + guestPeer.State +
                    "; " + transport.HandshakeReport + ") " + Text(lines));
                Check(Has(lines, "held: not (yet) a member of lobby") && Has(lines, "s waiting for the lobby member list"),
                    "late guest: the request was not held until Steam listed the guest: " + Text(lines));
                Check(!Has(lines, " refused: "), "late guest: the guest's request was refused: " + Text(lines));
                Check(transport.HandshakeStep == "packets both ways", "late guest: step " + transport.HandshakeStep);
                Check(Has(lines, "peer: connect request (hello) sent") && Has(lines, "peer: accept (hello answer) received"),
                    "late guest: the hello / hello answer lines are missing: " + Text(lines));
            }
        }
    }

    private static void FourPlayerHub(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var a = new FakeSteamApi(dir, 1002, null, ""))
        using (var b = new FakeSteamApi(dir, 1003, null, ""))
        using (var c = new FakeSteamApi(dir, 1004, null, ""))
        using (var spare = new FakeSteamApi(dir, 1005, null, ""))
        {
            SteamApi.SetForTests(host);
            SteamTransport transport = SteamTransport.Host();
            using (var hub = new PeerHub(transport))
            {
                var connected = new List<NetPeer>();
                hub.Connected += connected.Add;
                hub.Disconnected += (peer, why) => connected.Remove(peer);
                transport.GuestLeft += hub.Drop;
                Until(() => transport.Ready, "four seats: lobby did not open", host);
                var guests = new[] { a, b, c };
                var clients = new NetPeer[3];
                Action pump = delegate
                {
                    host.Pump();
                    for (int i = 0; i < clients.Length; i++)
                    {
                        guests[i].Pump();
                        if (clients[i] != null) clients[i].Poll();
                    }
                    hub.Poll();
                };
                for (int i = 0; i < guests.Length; i++)
                {
                    Join(guests[i], transport.LobbyId, "Success");
                    clients[i] = new NetPeer(new RawLink(guests[i], host.LocalId));
                    clients[i].Connect(RawLink.Host);
                    Until(() => { pump(); return clients[i].State == PeerState.Connected && connected.Count == i + 1; },
                        "four seats: a guest could not complete its own handshake");
                }
                Join(spare, transport.LobbyId, "Full");
                for (int i = 0; i < clients.Length; i++)
                {
                    transport.SetSeatMember(i + 2, connected[i].Remote);
                    Check(transport.SeatName(i + 2) == guests[i].PersonaName, "four seats: wrong name for guest");
                    clients[i].SendReliable(new byte[] { (byte)(i + 2) }, 0, 1);
                    connected[i].SendReliable(new byte[] { (byte)(20 + i) }, 0, 1);
                }
                for (int i = 0; i < clients.Length; i++)
                {
                    byte[] reply = null; bool reliable = false;
                    Until(() => { pump(); return clients[i].TryReceive(out reply, out reliable); }, "four seats: missing private host reply");
                    Check(reliable && reply[0] == 20 + i, "four seats: reply delivered to another guest");
                    Until(() => { pump(); return connected[i].TryReceive(out reply, out reliable); }, "four seats: missing guest message");
                    Check(reliable && reply[0] == i + 2, "four seats: guest message read on another host connection");
                }
                NetPeer first = connected[0], last = connected[2];
                EndPoint oldEndpoint = connected[1].Remote;
                b.LeaveLobby(transport.LobbyId);
                Until(() => { pump(); return connected.Count == 2; }, "four seats: the departed guest kept its route");
                Check(connected[0] == first && connected[1] == last, "four seats: another guest was disconnected");
                clients[1] = null;
                guests[1] = spare;
                Join(spare, transport.LobbyId, "Success");
                clients[1] = new NetPeer(new RawLink(spare, host.LocalId));
                clients[1].Connect(RawLink.Host);
                Until(() => { pump(); return clients[1].State == PeerState.Connected && connected.Count == 3; },
                    "four seats: the replacement could not join");
                transport.SendTo(Payload, 0, Payload.Length, oldEndpoint);
                connected[2].SendReliable(new byte[] { 99 }, 0, 1);
                byte[] final = null; bool finalReliable = false;
                Until(() => { pump(); return clients[1].TryReceive(out final, out finalReliable); }, "four seats: no reply on replacement route");
                Check(finalReliable && final[0] == 99 && first.State == PeerState.Connected && last.State == PeerState.Connected,
                    "four seats: joining the replacement changed another route");
            }
        }
    }

    // Membership may arrive late for the second guest too. Never accept packets from a nonmember.
    private static void StrangerRefused(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, ""))
        using (var stranger = new FakeSteamApi(dir, 1003, null, ""))
        {
            List<string> lines = Capture();
            SteamTransport.SetMemberGraceForTests(0.3);
            NetPeer hostPeer;
            using (SteamTransport transport = HostWithGuest(host, guest, out hostPeer))
            {
                guest.LeaveLobby(transport.LobbyId);
                Until(() => { transport.Update(); return host.GetNumLobbyMembers(transport.LobbyId) == 1; }, "stranger: the guest did not leave", host);
                Check(stranger.SendP2PPacket(host.LocalId, Payload, 0, Payload.Length, 0), "stranger: send failed");
                Until(() => { SteamTransport.PumpFake(); transport.Update(); return Has(lines, "1003 held"); }, "stranger: request not held: " + Text(lines));
                Check(transport.HandshakeStep.StartsWith("session request held", StringComparison.Ordinal), "stranger: step while held: " + transport.HandshakeStep);
                Until(() => { SteamTransport.PumpFake(); transport.Update(); return Has(lines, "1003 refused"); }, "stranger: request never refused: " + Text(lines));
                Check(Has(lines, "after waiting"), "stranger: the refusal does not say how long it waited: " + Text(lines));
                Check(transport.HandshakeStep.StartsWith("session request refused", StringComparison.Ordinal), "stranger: step after refusal: " + transport.HandshakeStep);
                Check(!Available(host, 0), "stranger: a packet of the refused user got through");

                // A second request while the first guest is connected stays subject to the same membership check.
                Join(guest, transport.LobbyId, "Success");
                var guestPeer = new NetPeer(new RawLink(guest, host.LocalId));
                guestPeer.Connect(RawLink.Host);
                Check(Handshake(transport, hostPeer, guest, guestPeer, 3000), "stranger: the guest could not connect: " + transport.HandshakeReport);
                lines.Clear();
                Check(stranger.SendP2PPacket(host.LocalId, Payload, 0, Payload.Length, 0), "stranger: second send failed");
                Until(() => { SteamTransport.PumpFake(); transport.Update(); return Has(lines, "1003 refused"); }, "stranger: second request not refused: " + Text(lines));
                Check(Has(lines, "1003 held") && !Available(host, 0), "stranger: the second membership check failed: " + Text(lines));
            }
        }
    }

    // When the host's game never answers, the guest's trace names the step: hello sent N times, nothing back, and the
    // Steam side: packets sent to the host, nothing received from it.
    private static void GuestTraceNamesStep(string dir)
    {
        using (var host = new FakeSteamApi(dir, 1001, null, ""))
        using (var guest = new FakeSteamApi(dir, 1002, null, ""))
        {
            ulong lobby = Create(host, 2);
            Check(host.SetLobbyData(lobby, "oricoop", "1"), "set lobby protocol");
            List<string> lines = Capture();
            SteamApi.SetForTests(guest);
            using (SteamTransport transport = SteamTransport.Join(lobby))
            {
                Until(() => { SteamTransport.PumpFake(); transport.Update(); return transport.Ready; }, "guest trace: not in the lobby", host);
                Check(transport.HandshakeStep == "in the lobby, nothing sent to the host", "guest trace: step before sending: " + transport.HandshakeStep);
                double now = 0;
                var peer = new NetPeer(transport, () => now);
                peer.Connect(transport.RemoteEndPoint);
                for (int i = 0; i < 60 && peer.State == PeerState.Connecting; i++)
                {
                    SteamTransport.PumpFake();
                    host.Pump();                        // the host's Steam gets the packets; its game never reads them
                    peer.Poll();
                    now += 250;
                }
                Check(peer.State == PeerState.Disconnected && peer.DisconnectReason == "no answer", "guest trace: no 'no answer' (" + peer.State + ")");
                Check(Has(lines, "steam: first packet sent to 1001 on channel 0"), "guest trace: first send line: " + Text(lines));
                Check(Has(lines, "peer: connect request (hello) sent to"), "guest trace: hello line: " + Text(lines));
                Check(Has(lines, "peer: no answer after 10 s: 40 connect request(s) sent") && Has(lines, "0 packet(s) received (nothing came back)"),
                    "guest trace: no-answer line: " + Text(lines));
                Check(transport.HandshakeStep == "packets sent to the host, nothing received from it", "guest trace: step " + transport.HandshakeStep);
                Check(transport.HandshakeReport.IndexOf("40 sent, 0 refused by Steam, 0 received", StringComparison.Ordinal) >= 0,
                    "guest trace: report " + transport.HandshakeReport);
            }
        }
    }

    private static void Run(string name, Action<string> test)
    {
        cases++;
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-steam-" + Guid.NewGuid().ToString("N"));
        try { test(dir); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e); }
        finally
        {
            SteamTransport.ResetForTests();
            SteamApi.SetForTests(null);
            NetPeer.Trace = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    public static int Main()
    {
        Run("lobby flow", LobbyFlow);
        Run("T-219 discover rooms, status, version, capacity and targeted invitations", FriendRoomDirectory);
        Run("drop create", DropCreate);
        Run("drop enter", DropEnter);
        Run("late members", LateMembers);
        Run("P2P", P2P);
        Run("no session", NoSession);
        Run("transport host", TransportHost);
        Run("invite overlay enabled", dir => InviteOverlay(dir, "", true));
        Run("invite still opens when overlay disabled", dir => InviteOverlay(dir, "no-overlay", false));
        Run("transport guest", TransportGuest);
        Run("T-216 retry after a lost create result", RetryAfterLostCreate);
        Run("T-216 late create result is left", LateCreateIsLeft);
        Run("T-216 retry after a lost enter result", RetryAfterLostEnter);
        Run("T-216 late enter after switching to host", LateEnterAfterSwitchToHost);
        Run("T-216 late enter of the same lobby keeps it", LateEnterOfSameLobbyKeepsIt);
        Run("T-216 the given-up create's result comes first", OldCreateResultFirst);
        Run("T-216 the given-up create's failure comes first", OldCreateFailureFirst);
        Run("T-216 two given-up joins of one lobby", TwoGivenUpJoinsOfOneLobby);
        Run("T-216 late join result while joining another lobby", LateJoinWhileJoiningAnother);
        Run("T-216 nothing awaited after Steam restarts", NothingAwaitedAfterSteamRestart);
        Run("T-218 a throwing invite window keeps the lobby", InviteThrows);
        Run("T-253 a guest Steam lists late is held, then the handshake completes", GuestListedLate);
        Run("T-220 four Steam players, routed messages, full lobby and a replacement guest", FourPlayerHub);
        Run("T-253 strangers are refused, including while another guest is connected", StrangerRefused);
        Run("T-253 the guest's trace names the step the handshake stopped at", GuestTraceNamesStep);
        Console.WriteLine("Steam: " + cases + " cases, " + failures + " failed");
        return failures == 0 ? 0 : 1;
    }
}
