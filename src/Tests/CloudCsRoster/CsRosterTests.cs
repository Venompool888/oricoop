using System;
using System.Collections.Generic;
using System.Net;
using OriCoop.Authority;

// CServerClient.Receive(CsRoster): a roster entry for a seat outside 1-4 (a bad or newer C-server) indexed past the
// 5-entry seat arrays and threw half-way: the roster was left half-replaced and no SeatOnline / SeatOffline /
// ArbiterChanged event was raised for the real seats in the same message.
static class CsRosterTests
{
    static int failures;

    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static byte[] Roster(params CsRosterEntry[] entries)
    {
        CsMessage m = new CsMessage { Type = CsWire.CsRoster };
        m.Roster.AddRange(entries);
        return CsWire.Encode(m);
    }

    static void Run(string name, Action<CServerClient> test)
    {
        CServerClient client = null;
        try
        {
            client = new CServerClient(new IPEndPoint(IPAddress.Loopback, 9), 1, "ABCDEF", null, 1,
                new CsIdentity[] { new CsIdentity { Steam = 1, Nick = "me" } });
            test(client);
            Console.WriteLine("ok " + name);
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message); }
        finally { if (client != null) try { client.Leave(); } catch (Exception) { } }
    }

    static int Main()
    {
        Run("roster entry past seat 4 is ignored", delegate(CServerClient c)
        {
            List<int> online = new List<int>();
            int arbiter = -1;
            c.SeatOnline += delegate(int seat) { online.Add(seat); };
            c.ArbiterChanged += delegate(int seat) { arbiter = seat; };
            c.Receive(Roster(
                new CsRosterEntry { Seat = 2, Online = true, Steam = 2, Nick = "two" },
                new CsRosterEntry { Seat = 9, Online = true, Steam = 9, Nick = "nine" },
                new CsRosterEntry { Seat = 3, Online = true, Steam = 3, Nick = "three" }));
            Check(c.SeatOnlineNow(2) && c.SeatOnlineNow(3), "real seats not online");
            Check(c.SeatNick(3) == "three" && c.SeatTaken(3), "seat 3 not taken");
            Check(online.Count == 2 && online.Contains(2) && online.Contains(3), "SeatOnline events: " + online.Count);
            Check(arbiter == 2 && c.ArbiterSeat == 2, "arbiter " + c.ArbiterSeat);
        });
        Run("roster entry for seat 0 changes nothing", delegate(CServerClient c)
        {
            c.Receive(Roster(new CsRosterEntry { Seat = 0, Online = true, Steam = 5, Nick = "zero" },
                new CsRosterEntry { Seat = 1, Online = true, Steam = 1, Nick = "me" }));
            Check(c.ArbiterSeat == 1 && c.SeatOnlineNow(1) && !c.SeatOnlineNow(0), "arbiter " + c.ArbiterSeat);
        });
        Run("a later roster still sees seats going offline", delegate(CServerClient c)
        {
            List<int> offline = new List<int>();
            c.SeatOffline += delegate(int seat) { offline.Add(seat); };
            c.Receive(Roster(new CsRosterEntry { Seat = 1, Online = true }, new CsRosterEntry { Seat = 4, Online = true }));
            c.Receive(Roster(new CsRosterEntry { Seat = 1, Online = true }, new CsRosterEntry { Seat = 200, Online = true }));
            Check(offline.Count == 1 && offline[0] == 4, "SeatOffline events: " + offline.Count);
        });
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
