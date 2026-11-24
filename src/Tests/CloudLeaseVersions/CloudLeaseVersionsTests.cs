using System;
using System.Collections.Generic;
using OriCoop.Authority;

// 3-4 players: when a lease moves from one owner to the next, a third seat must take the new owner's states. Each
// publisher numbers its states with its own counter (raised to the newest one it received), so the new owner's first
// numbers can be at or below the last number a third seat got from the old owner when the new owner missed some of the
// old owner's unreliable states. The arbiter compares versions per epoch since T-146; the receivers must as well.
static class CloudLeaseVersionsTests
{
    static int failures;
    static void Run(string name, Action body)
    {
        try { body(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    // A C-server room: 3 seats, each with its own AuthorityClient. `drop(to)` loses unreliable states to that seat.
    sealed class Room
    {
        public readonly Arbiter Server = new Arbiter("", new ArbiterOptions(), 0);
        public readonly AuthorityClient[] Clients = new AuthorityClient[4];
        public Func<int, bool> Drop = to => false;
        public double Now;
        public readonly List<StateUpdate>[] Got = { null, new List<StateUpdate>(), new List<StateUpdate>(), new List<StateUpdate>() };

        public Room()
        {
            for (int seat = 1; seat <= 3; seat++)
            {
                int s = seat;
                Clients[s] = new AuthorityClient(AuthorityMode.Server, () => s,
                    (msg, reliable) => Route(Server.Handle(s, CsWire.Decode(msg), Now)), false);
                Clients[s].StateReceived += u => Got[s].Add(u);
            }
            for (int seat = 1; seat <= 3; seat++) Route(Server.JoinAt(seat, 0, "", Now));
        }

        void Route(List<Outgoing> output)
        {
            foreach (Outgoing o in output)
                for (int seat = 1; seat <= 3; seat++)
                    if (o.To == seat || o.To == 0 || (o.To < 0 && -o.To != seat))
                    {
                        if (o.Message[0] == CsWire.CsState && Drop(seat)) continue;
                        Clients[seat].Receive(o.Message);
                    }
        }
    }

    static int Main()
    {
        const string key = "w:push:7";
        Run("a third seat takes the new owner's first states after a handover", delegate {
            Room room = new Room();
            room.Clients[1].RequestLease(key, 1f, true);
            Check(room.Clients[1].Owns(key), "seat 1 did not get the lease");
            for (int i = 0; i < 10; i++) { room.Now += 50; room.Clients[1].PublishState(key, new byte[] { (byte)i }, true); }
            // Seat 2 misses the old owner's last three states (unreliable); seat 3 gets all of them.
            room.Drop = to => to == 2;
            for (int i = 10; i < 13; i++) { room.Now += 50; room.Clients[1].PublishState(key, new byte[] { (byte)i }, true); }
            room.Drop = to => false;
            room.Clients[1].ReleaseLease(key);
            room.Clients[2].RequestLease(key, 1f, true);
            Check(room.Clients[2].Owns(key), "seat 2 did not get the lease (owner " + room.Clients[2].OwnerOf(key) + ")");
            uint epoch = room.Clients[3].EpochOf(key);
            int before = room.Got[3].Count;
            room.Now += 50; room.Clients[2].PublishState(key, new byte[] { 100 }, true);
            room.Now += 50; room.Clients[2].PublishState(key, new byte[] { 101 }, true);
            List<StateUpdate> fresh = room.Got[3].GetRange(before, room.Got[3].Count - before);
            Check(fresh.Count == 2, "seat 3 took " + fresh.Count + " of the new owner's 2 states (epoch " + epoch + ")");
            StateUpdate latest = room.Clients[3].LatestState(key);
            Check(latest != null && latest.Epoch == epoch && latest.Data[0] == 101, "seat 3's latest state is "
                + (latest == null ? "none" : "epoch " + latest.Epoch + " data " + latest.Data[0]));
        });

        Run("an older or repeated state of the same epoch is still dropped", delegate {
            Room room = new Room();
            room.Clients[1].RequestLease(key, 1f, true);
            room.Clients[1].PublishState(key, new byte[] { 1 }, true);
            room.Clients[1].PublishState(key, new byte[] { 2 }, true);
            int before = room.Got[3].Count;
            uint epoch = room.Clients[3].EpochOf(key);
            // The same message delivered again (a duplicate on the wire).
            room.Clients[3].Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsState, Key = key, Epoch = epoch, Ver = 1, Active = 1, Data = new byte[] { 1 } }));
            room.Clients[3].Receive(CsWire.Encode(new CsMessage { Type = CsWire.CsState, Key = key, Epoch = epoch, Ver = 2, Active = 1, Data = new byte[] { 2 } }));
            Check(room.Got[3].Count == before, "a stale state of the current epoch was taken");
        });

        if (failures == 0) Console.WriteLine("ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
