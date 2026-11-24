using System;
using System.Collections.Generic;
using OriCoop.World;

// T-254: the host's queue of full world snapshots it still owes. Oracle (from the ticket): one guest load produces ONE
// snapshot for that guest; other seats get no extra copy; with 3-4 seats every seat that needs an update still gets one.
// The host model below follows WorldSync: resync request -> Request(sender); a send -> Sent(seat); Tick -> Take.
static class SnapshotRequestsTests
{
    static void Check(bool ok, string what) { if (!ok) throw new Exception(what); }

    // Snapshots the host actually sends, as "P<seat>" or "all".
    sealed class Host
    {
        public readonly SnapshotRequests Q = new SnapshotRequests();
        public readonly List<string> Sent = new List<string>();
        public int[] Remote = { 2 };
        public readonly HashSet<int> Loading = new HashSet<int>();   // seats still loading a checkpoint
        public void Send(int seat) { Q.Sent(seat); Sent.Add(seat == 0 ? "all" : "P" + seat); }
        // A seat's LoadedMessage: it plays the world again and gets its own snapshot (SaveSync).
        public void Loaded(int seat) { Loading.Remove(seat); Send(seat); }
        public void Tick()
        {
            if (!Q.Any) return;
            bool all; int[] seats = Q.Take(Remote, delegate(int s) { return !Loading.Contains(s); }, out all);
            if (all) Send(0);
            foreach (int s in seats) Send(s);
        }
        public string Log { get { return string.Join(",", Sent.ToArray()); } }
    }

    public static void Register(Action<string, Action> run)
    {
        run("T-254 guest load after its own resync request: one snapshot to that guest only", delegate {
            Host h = new Host(); h.Loading.Add(2);
            h.Q.Request(2);   // guest RestoreCheckpoint -> MsgResync, queued while the host waits for the load
            h.Tick();         // still loading: nothing yet
            Check(h.Log == "", "sent while loading: " + h.Log);
            h.Loaded(2);      // LoadedMessage -> snapshot to the loading seat
            h.Tick(); h.Tick();
            Check(h.Log == "P2", "sent " + h.Log);
        });
        run("T-254 a guest resync request alone is answered once, to that guest (not broadcast)", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3 };
            h.Q.Request(3);
            h.Tick(); h.Tick();
            Check(h.Log == "P3", "sent " + h.Log);
        });
        run("T-254 a request that arrives after the load snapshot is still answered", delegate {
            Host h = new Host();
            h.Send(2);        // load snapshot first
            h.Q.Request(2);   // a later digest mismatch: the guest now waits for a new snapshot
            h.Tick();
            Check(h.Log == "P2,P2", "sent " + h.Log);
        });
        run("T-254 host rollback with no loads yet: one broadcast", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3, 4 };
            h.Q.Request(0);
            h.Q.Request(3);   // a guest request meanwhile is covered by the broadcast
            h.Tick(); h.Tick();
            Check(h.Log == "all", "sent " + h.Log);
        });
        run("T-254 3P host rollback, guests load one after another: one snapshot each", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3 };
            h.Loading.Add(2); h.Loading.Add(3);   // PushCheckpointNow: every guest reloads
            h.Q.Request(0);                        // RestorePatch on the host
            h.Tick();
            Check(h.Log == "", "sent while every guest loads: " + h.Log);
            h.Loaded(2);
            h.Tick();                              // P3 still loading: not sent yet
            Check(h.Log == "P2", "after P2 loaded: " + h.Log);
            h.Loaded(3);
            h.Tick(); h.Tick();
            Check(h.Log == "P2,P3", "sent " + h.Log);
        });
        run("T-254 4P host rollback, two guests already loaded, the third plays on: it gets the queued one", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3, 4 };
            h.Q.Request(0);
            h.Send(3); h.Send(2);   // their LoadedMessage snapshots
            Check(h.Q.Pending(4) && !h.Q.Pending(2) && !h.Q.Pending(3), "pending after two loads");
            h.Tick(); h.Tick();
            Check(h.Log == "P3,P2,P4", "sent " + h.Log);
        });
        run("T-254 an owed seat that left is dropped", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3 };
            h.Q.Request(3); h.Loading.Add(3);
            h.Remote = new int[] { 2 };
            h.Tick();
            Check(h.Log == "" && !h.Q.Any, "sent " + h.Log + " pending " + h.Q.Any);
        });
        run("T-254 a new rollback after a partial one owes everyone again", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3 };
            h.Q.Request(0); h.Send(2);
            h.Q.Request(0);   // second rollback before the tick
            h.Tick();
            Check(h.Log == "P2,all", "sent " + h.Log);
        });
        run("T-254 a broadcast pays off every seat's own request", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3 };
            h.Q.Request(2); h.Q.Request(3);
            h.Send(0);
            Check(!h.Q.Any, "still pending after a broadcast");
        });
        run("T-254 per-seat requests go out in seat order, once each", delegate {
            Host h = new Host(); h.Remote = new int[] { 2, 3, 4 };
            h.Q.Request(4); h.Q.Request(2); h.Q.Request(4);
            h.Tick(); h.Tick();
            Check(h.Log == "P2,P4", "sent " + h.Log);
        });
        run("T-254 session reset forgets everything", delegate {
            SnapshotRequests q = new SnapshotRequests();
            q.Request(0); q.Request(2); q.Sent(3); q.Clear();
            bool all; int[] seats = q.Take(new int[] { 2, 3 }, delegate(int x) { return true; }, out all);
            Check(!q.Any && !all && seats.Length == 0, "after clear: all=" + all + " seats=" + seats.Length);
        });
    }
}
