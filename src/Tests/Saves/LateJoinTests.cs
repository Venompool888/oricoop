using System;
using OriCoop.Saves;

// T-129: a seat that joins after the co-op save lineage was settled.
partial class SaveTests
{
    static int CommitsReceived(FakeNetwork network, int seat)
    {
        int n = 0;
        foreach (FakeNetwork.Packet p in network.Sent) if (p.To == seat && p.Type == LineageWire.Commit) n++;
        return n;
    }

    static void LateJoinerGetsCommit()
    {
        Scenario s = new Scenario(); s.Guest.Reply.FreeSlot = true; s.Offer();
        Check(s.HostMachine.HostGate() && s.Guest.ReceivedCommit != null, "setup: lineage with seat 2 not settled");
        // Seat 3 joins mid-session (e.g. from the title) once the host already proceeds.
        FakeBus bus3 = s.Network.Add(3); FakeLineage guest3 = new FakeLineage(bus3);
        LineageMachine machine3 = new LineageMachine(bus3, s.Clock, guest3); machine3.Init();
        s.Network.Pump();
        Check(guest3.ReceivedCommit != null && guest3.ReceivedCommit.Bytes != null && guest3.ReceivedCommit.Bytes[0] == 7,
            "the late joiner never got the host's co-op save (would load into the first empty slot or slot 0)");
        Check(CommitsReceived(s.Network, 3) == 1, "the late joiner got " + CommitsReceived(s.Network, 3) + " commits, expected 1");
        Check(CommitsReceived(s.Network, 2) == 1, "the seat already in the world got the commit again");
        Check(s.HostMachine.HostGate(), "the gate closed after the late commit");
    }

    static void OpenLineageJoinerGetsCommit()
    {
        Scenario s = new Scenario(); s.HostMachine.HostGate();   // offered, no reply yet
        FakeBus bus3 = s.Network.Add(3); FakeLineage guest3 = new FakeLineage(bus3);
        LineageMachine machine3 = new LineageMachine(bus3, s.Clock, guest3); machine3.Init();
        s.Network.Pump();
        Check(s.HostMachine.Stage == LineageStage.Proceed, "setup: seat 2's reply did not settle the lineage");
        Check(CommitsReceived(s.Network, 3) == 1 && s.Host.CommitsTo == 0,
            "a seat joining while the lineage is open gets exactly the decision's commit (" + CommitsReceived(s.Network, 3) + " / extra " + s.Host.CommitsTo + ")");
    }
}
