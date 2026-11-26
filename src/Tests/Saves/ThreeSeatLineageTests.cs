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

    static ThreeSeats ForksWithEvidence(bool hostLast, bool firstLast, bool secondLast, bool matched)
    {
        ThreeSeats t = new ThreeSeats(true);
        string round = Guid.NewGuid().ToString("N");
        t.S.Host.PreviousRound = round; t.S.Host.WasLast = hostLast;
        t.B.Reply.Found = true; t.B.Reply.Hash = "fork-b";
        t.B.Reply.PreviousRound = round; t.B.Reply.WasLast = firstLast;
        t.A.Reply.PreviousRound = matched ? round : ""; t.A.Reply.WasLast = secondLast;
        t.S.Offer();
        Check(t.S.HostMachine.ForkCount == 2 && t.S.Host.Commits == 0 && t.S.Host.Adoptions == 0,
            "the three-party choice must wait for the host to confirm");
        return t;
    }

    static void LastRoomMember()
    {
        ThreeSeats t = ForksWithEvidence(false, false, true, true);
        Check(t.S.HostMachine.Suggested == 2 && t.S.HostMachine.ForkSeat(1) == 3, "later guest's copy not focused");
        t.B.UploadBytes = new byte[] { 42 };
        t.S.HostMachine.ChooseFork(0); t.S.Network.Pump();
        Check(RequestsTo(t.S.Network, 2, LineageWire.UploadRequest) == 1 && t.S.Host.StoredBytes[0] == 42,
            "the host could not override the suggested member");
        Check(ForksWithEvidence(true, false, false, true).S.HostMachine.Suggested == 0, "host was last");
        Check(ForksWithEvidence(false, true, true, true).S.HostMachine.Suggested == -1, "conflicting evidence");
        Check(ForksWithEvidence(false, true, false, false).S.HostMachine.Suggested == -1, "legacy copy with no round");
        Check(ForksWithEvidence(false, false, false, true).S.HostMachine.Suggested == -1, "no last member observed");
    }

    static void LastRoomWire()
    {
        string round = Guid.NewGuid().ToString("N");
        var commit = new LineageCommit { Coop = "room", Bytes = new byte[] { 1, 2 } };
        byte[] old = Encode(w => LineageWire.WriteCommit(w, commit));
        Check(LineageWire.ReadCommit(Read(old)).Round == "", "legacy commit invented a marker");
        commit.Round = round;
        byte[] newer = Encode(w => LineageWire.WriteCommit(w, commit));
        byte[] prefix = new byte[old.Length]; Array.Copy(newer, prefix, old.Length); EqualBytes(prefix, old);
        Check(LineageWire.ReadCommit(Read(newer)).Round == round, "shared commit marker changed on the wire");
        var reply = new LineageReply { Found = true, Hash = "fork" };
        old = Encode(w => LineageWire.WriteReply(w, reply));
        Check(!LineageWire.ReadReply(Read(old)).WasLast, "legacy reply invented evidence");
        reply.PreviousRound = round; reply.WasLast = true;
        newer = Encode(w => LineageWire.WriteReply(w, reply));
        prefix = new byte[old.Length]; Array.Copy(newer, prefix, old.Length); EqualBytes(prefix, old);
        LineageReply read = LineageWire.ReadReply(Read(newer));
        Check(read.PreviousRound == round && read.WasLast, "last member's evidence lost");
        Array.Resize(ref newer, newer.Length - 1);
        Check(LineageWire.ReadReply(Read(newer)).PreviousRound == "", "partial extension used as evidence");
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

    static void ChosenGuestLeaves(bool uploading)
    {
        ThreeSeats t = new ThreeSeats(true);
        t.S.Offer();
        if (uploading) t.S.HostMachine.Decide(true);
        t.B.Reply.Found = true;
        t.B.Reply.Hash = "other-fork";
        t.S.Network.Remove(3);
        t.S.Network.Pump();
        Check(t.S.HostMachine.Stage == LineageStage.Choosing && t.S.Host.Choosing == 2 && t.S.Host.Commits == 0,
            "the remaining guest's fork was lost or the missing guest was still offered");
        t.S.Network.Remove(2);
        Check(t.S.HostMachine.HostGate() && t.S.Host.Commits == 1, "the last guest left and blocked the host");
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
