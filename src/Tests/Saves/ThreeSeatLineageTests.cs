using System;
using System.IO;
using OriCoop.Saves;

// T-128: 3-4 players. Guest B (seat 2) has no co-op copy; guest A (seat 3) has a diverged one.
partial class SaveTests
{
    sealed class ThreeSeats
    {
        public readonly Scenario S = new Scenario();
        public readonly FakeBus Bus3;
        public readonly FakeLineage A;
        public FakeLineage B { get { return S.Guest; } }
        public ThreeSeats(bool withMachine3)
        {
            Bus3 = S.Network.Add(3); A = new FakeLineage(Bus3);
            if (withMachine3) new LineageMachine(Bus3, S.Clock, A).Init();
            B.Reply.Found = false; B.Reply.FreeSlot = true;
            A.Reply.Found = true; A.Reply.Slot = 4; A.Reply.Hash = "diverged"; A.Reply.LastSync = "old";
        }
    }

    static int RequestsTo(FakeNetwork network, int seat, byte kind)
    {
        int n = 0;
        foreach (FakeNetwork.Packet p in network.Sent)
            if (p.To == seat && p.Type == LineageWire.Request && p.Body.Length == 1 && p.Body[0] == kind) n++;
        return n;
    }

    static void NoCopyFirstStillChooses()
    {
        ThreeSeats t = new ThreeSeats(true);
        t.S.Offer();   // B's "no copy" reply is delivered before A's diverged one
        Check(t.S.HostMachine.Stage == LineageStage.Choosing && t.S.Host.Choosing == 1,
            "B's empty reply decided the lineage; A's diverged copy was never offered (stage " + t.S.HostMachine.Stage + ")");
        Check(t.S.Host.Commits == 0 && t.A.ReceivedCommit == null, "a commit overwrote A's diverged slot before the host chose");
    }

    static void UploadFromChosenGuest()
    {
        ThreeSeats t = new ThreeSeats(true); t.A.UploadBytes = new byte[] { 21, 22, 23 };
        t.S.Offer(); t.S.HostMachine.Decide(true);
        Check(RequestsTo(t.S.Network, 3, LineageWire.UploadRequest) == 1, "A was not asked for its progress");
        Check(RequestsTo(t.S.Network, 2, LineageWire.UploadRequest) == 0, "B (no copy) was asked to upload: its empty upload can void the choice");
        t.S.Network.Pump();
        Check(t.S.Host.InvalidUploads == 0 && t.S.Host.Adoptions == 1 && t.S.HostMachine.Stage == LineageStage.Adopting,
            "the host did not adopt A's progress (invalid " + t.S.Host.InvalidUploads + ", adoptions " + t.S.Host.Adoptions + ")");
        t.S.HostMachine.AdoptionFinished(); t.S.Network.Pump();
        EqualBytes(t.B.ReceivedCommit.Bytes, 21, 22, 23); EqualBytes(t.A.ReceivedCommit.Bytes, 21, 22, 23);
        Check(t.S.Host.Outcome == "diverged-partner", "outcome " + t.S.Host.Outcome);
    }

    static void OtherGuestUploadIgnored()
    {
        ThreeSeats t = new ThreeSeats(true); t.A.UploadBytes = new byte[] { 31, 32 };
        t.S.Offer(); t.S.HostMachine.Decide(true);
        // B sends an empty upload anyway (e.g. an older client answering a broadcast request) before A's arrives.
        t.S.GuestBus.Send(LineageWire.Upload, true, delegate(BinaryWriter w) { LineageWire.WriteUpload(w, null); });
        t.S.Network.Pump();
        Check(t.S.Host.InvalidUploads == 0 && t.S.Host.Adoptions == 1 && t.S.HostMachine.Stage == LineageStage.Adopting,
            "B's empty upload voided the host's choice of A's progress (stage " + t.S.HostMachine.Stage + ", invalid " + t.S.Host.InvalidUploads + ")");
        EqualBytes(t.S.Host.StoredBytes, 31, 32);
    }

    static void WaitsForEveryReply()
    {
        ThreeSeats t = new ThreeSeats(true); t.A.Reply.Hash = "host";   // A holds the same lineage, B none
        t.S.HostMachine.HostGate();
        // Deliver: offer->2, offer->3, reply 2->1. A's reply is still in flight.
        t.S.Network.PumpOne(); t.S.Network.PumpOne(); t.S.Network.PumpOne();
        Check(t.S.HostMachine.Stage == LineageStage.Offered && t.S.Host.Commits == 0, "the host decided before every guest answered");
        t.S.Network.Pump();
        Check(t.S.HostMachine.HostGate() && t.S.Host.Outcome == "same" && t.S.Host.Commits == 1,
            "replies new+same: expected same with one commit (" + t.S.Host.Outcome + ", " + t.S.Host.Commits + ")");
    }

    static void PartialRepliesTimeout()
    {
        ThreeSeats t = new ThreeSeats(false);   // seat 3 runs no lineage code (older mod): it never answers
        t.S.Offer();
        Check(t.S.HostMachine.Stage == LineageStage.Offered, "the host decided without seat 3's answer");
        t.S.Clock.Now = 10.01f;
        Check(t.S.HostMachine.HostGate() && t.S.Host.Outcome == "new" && t.S.Host.ReplyTimeouts == 0 && t.S.Host.Commits == 1,
            "after the timeout the host must decide on B's answer (" + t.S.Host.Outcome + ", timeouts " + t.S.Host.ReplyTimeouts + ")");
    }
}
