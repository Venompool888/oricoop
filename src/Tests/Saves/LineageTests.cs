using System;
using System.IO;
using OriCoop.Saves;

partial class SaveTests
{
    static byte[] Encode(Action<BinaryWriter> write)
    {
        MemoryStream stream = new MemoryStream(); write(new BinaryWriter(stream)); return stream.ToArray();
    }
    static BinaryReader Read(byte[] bytes) { return new BinaryReader(new MemoryStream(bytes)); }
    static void EqualBytes(byte[] actual, params byte[] expected)
    {
        Check(actual.Length == expected.Length, "length " + actual.Length + " != " + expected.Length);
        for (int i = 0; i < actual.Length; i++) Check(actual[i] == expected[i], "byte " + i + " differs");
    }
    static void InvalidSave(byte[] bytes)
    {
        try { SaveWire.ReadSaveBytes(Read(bytes)); }
        catch (InvalidDataException) { return; }
        throw new Exception("invalid checkpoint accepted");
    }
    static void Trace(Scenario s, params byte[] types)
    {
        Check(s.Network.Sent.Count == types.Length, "message count " + s.Network.Sent.Count + " != " + types.Length);
        for (int i = 0; i < types.Length; i++) Check(s.Network.Sent[i].Type == types[i], "message " + i + " differs");
    }

    static void SameSave()
    {
        Scenario s = new Scenario(); s.Guest.Reply.Found = true; s.Guest.Reply.Hash = "host";
        Check(!s.HostMachine.HostGate(), "offer gate opened early");
        Check(s.HostMachine.Stage == LineageStage.Offered, "offer stage missing");
        s.Network.Pump();
        Check(s.HostMachine.HostGate() && s.Host.Outcome == "same", "matching lineage did not proceed");
        Check(s.Guest.ReceivedCommit != null && s.Guest.ReceivedCommit.Bytes[0] == 7, "guest missed commit");
        Trace(s, 16, 17, 18);
    }
    static void NewSave()
    {
        Scenario s = new Scenario(); s.Guest.Reply.FreeSlot = true; s.Offer();
        Check(s.Host.Outcome == "new" && s.HostMachine.HostGate(), "new lineage did not proceed");
        Check(!s.Host.NoFreeSlot && s.Guest.ReceivedCommit != null, "free slot or commit lost");
        Trace(s, 16, 17, 18);
    }
    static void NoFreeSlot()
    {
        Scenario s = new Scenario(); s.Offer();
        Check(s.Host.NoFreeSlot && s.Host.Commits == 1, "no-free-slot reply not preserved");
    }
    static void OlderGuestHostChosen()
    {
        Scenario s = new Scenario(); s.Host.Hash = "newer"; s.Diverge("older");
        Check(s.HostMachine.Stage == LineageStage.Choosing && !s.HostMachine.HostGate(), "divergence did not block");
        Check(s.Guest.Choosing == 1, "guest not told host is choosing");
        s.HostMachine.Decide(false); s.Network.Pump();
        Check(s.Host.DivergedHost && s.HostMachine.HostGate() && s.Guest.ReceivedCommit != null, "host choice failed");
        Trace(s, 16, 17, 19, 18); EqualBytes(s.Network.Sent[2].Body, 3);
    }
    static void NewerGuestUpload()
    {
        Scenario s = new Scenario(); s.Host.Hash = "older"; s.Guest.UploadBytes = new byte[] { 11, 12, 13 }; s.Diverge("newer");
        s.HostMachine.Decide(true);
        Check(s.HostMachine.Stage == LineageStage.Uploading, "upload request not staged");
        s.Network.Pump();
        Check(s.HostMachine.Stage == LineageStage.Adopting && s.Host.Adoptions == 1 && s.Host.Commits == 0, "adoption not pending");
        Check(!s.HostMachine.HostGate(), "gate opened before adoption completed");
        s.HostMachine.AdoptionFinished(); s.Network.Pump();
        Check(s.HostMachine.HostGate() && s.Host.Outcome == "diverged-partner", "adoption did not proceed");
        EqualBytes(s.Guest.ReceivedCommit.Bytes, 11, 12, 13);
        Trace(s, 16, 17, 19, 19, 23, 18); EqualBytes(s.Network.Sent[3].Body, 1);
    }
    static void InvalidUpload()
    {
        Scenario s = new Scenario(); s.Diverge("guest"); s.HostMachine.Decide(true); s.Network.Pump();
        Check(s.Host.InvalidUploads == 1 && s.Host.DivergedHost && s.Host.Commits == 1, "invalid upload did not fall back");
        Check(s.HostMachine.HostGate() && s.Host.Adoptions == 0, "invalid upload adopted");
        EqualBytes(s.Network.Sent[4].Body, 0, 0, 0, 0);
    }
    static void ReplyTimeout()
    {
        Scenario s = new Scenario(); s.HostMachine.HostGate();
        s.Clock.Now = 10f; Check(!s.HostMachine.HostGate(), "reply timed out at boundary");
        s.Clock.Now = 10.01f; Check(s.HostMachine.HostGate(), "reply timeout did not open gate");
        Check(s.Host.ReplyTimeouts == 1 && s.Host.Outcome == "legacy" && s.Host.Commits == 0, "wrong timeout outcome");
        s.Network.Pump(); Check(s.Host.Commits == 0, "late reply was accepted");
    }
    static void UploadTimeout()
    {
        Scenario s = new Scenario(); s.Diverge("guest"); s.HostMachine.Decide(true); s.Network.Remove(2);
        s.Clock.Now = 15f; Check(!s.HostMachine.HostGate() && s.Host.UploadTimeouts == 0, "upload timed out at boundary");
        s.Clock.Now = 15.01f; Check(!s.HostMachine.HostGate(), "upload timeout opened gate in same call");
        Check(s.Host.UploadTimeouts == 1 && s.Host.DivergedHost && s.Host.Commits == 1, "upload fallback failed");
        Check(s.HostMachine.HostGate(), "gate stayed closed after fallback");
    }
    static void CancelDelay()
    {
        Scenario s = new Scenario(); s.Diverge("guest"); s.HostMachine.Cancel(); s.Network.Pump();
        Check(s.Guest.Cancellations == 1 && s.Host.Outcome == "cancel", "cancel not sent");
        s.Clock.Now = 0.6f; Check(!s.HostMachine.Tick() && s.Host.Ends == 0, "cancel ended at boundary");
        s.Clock.Now = 0.61f; Check(s.HostMachine.Tick() && s.Host.Ends == 1, "cancel did not end");
        Check(s.HostMachine.Stage == LineageStage.Done && s.HostMachine.HostGate(), "cancel final state changed");
        EqualBytes(s.Network.Sent[3].Body, 2);
    }
    // T-128: was "first reply wins" (only the first guest's answer counted, a diverged copy of a later guest was lost).
    static void EveryReplyCountsFourSeats()
    {
        Scenario s = new Scenario();
        FakeBus bus3 = s.Network.Add(3), bus4 = s.Network.Add(4);
        FakeLineage guest3 = new FakeLineage(bus3), guest4 = new FakeLineage(bus4);
        guest3.Reply.Found = true; guest3.Reply.Hash = "different";
        guest4.Reply.Found = true; guest4.Reply.Hash = "different";
        LineageMachine machine3 = new LineageMachine(bus3, s.Clock, guest3), machine4 = new LineageMachine(bus4, s.Clock, guest4);
        machine3.Init(); machine4.Init(); s.Guest.Reply.Found = true; s.Guest.Reply.Hash = "host";
        s.Offer();
        Check(s.HostMachine.Stage == LineageStage.Choosing && s.Host.Choosing == 1 && s.Host.Commits == 0,
            "a later guest's diverged copy did not reach the host's choice (stage " + s.HostMachine.Stage + ", commits " + s.Host.Commits + ")");
        Check(s.Guest.Choosing == 1 && guest3.Choosing == 1 && guest4.Choosing == 1, "every guest is told the host is choosing");
        s.HostMachine.Decide(false); s.Network.Pump();
        Check(s.Guest.ReceivedCommit != null && guest3.ReceivedCommit != null && guest4.ReceivedCommit != null, "commit not broadcast");
    }
    static void DepartureDuringOffer()
    {
        Scenario s = new Scenario(); SeatReadiness seats = new SeatReadiness();
        s.HostBus.PlayerJoined += seats.Join; s.HostBus.PlayerLeft += seats.Leave;
        s.HostBus.Join(2); FakeBus bus3 = s.Network.Add(3); FakeLineage guest3 = new FakeLineage(bus3);
        LineageMachine machine3 = new LineageMachine(bus3, s.Clock, guest3); machine3.Init();
        s.HostMachine.HostGate(); s.Network.Remove(2); s.Network.Pump();
        Check(s.HostMachine.HostGate() && s.Host.Outcome == "new", "remaining seat could not complete handshake");
        seats.MarkSent(3); Check(seats.Loaded(3) && seats.ReadyForSeat(3, true, seats.AnyReady), "remaining seat not ready");
        Check(!seats.ReadyForSeat(2, true, seats.AnyReady), "departed seat ready");
    }
    static void SessionReset()
    {
        Scenario s = new Scenario(); s.Offer(); s.HostBus.End(); s.GuestBus.End();
        Check(s.HostMachine.Stage == LineageStage.Idle && s.Host.Resets == 1 && s.Guest.Resets == 1, "session did not reset");
        Check(!s.HostMachine.HostGate() && s.HostMachine.Stage == LineageStage.Offered, "new session could not offer");
    }
    static void FailedOffer()
    {
        Scenario s = new Scenario(); s.Host.FailOffer = true;
        Check(!s.HostMachine.HostGate() && s.Host.OfferFailures == 1, "offer error changed first gate result");
        Check(s.HostMachine.HostGate() && s.HostMachine.Stage == LineageStage.Proceed, "offer failure did not fall back");
    }
    static void UnsolicitedMessages()
    {
        Scenario s = new Scenario();
        s.GuestBus.Send(LineageWire.Upload, true, delegate(BinaryWriter w) { LineageWire.WriteUpload(w, new byte[] { 1 }); });
        s.GuestBus.Send(LineageWire.Reply, true, delegate(BinaryWriter w) { LineageWire.WriteReply(w, new LineageReply()); });
        s.Network.Pump();
        Check(s.HostMachine.Stage == LineageStage.Idle && s.Host.Adoptions == 0 && s.Host.Commits == 0, "unsolicited messages changed state");
    }

    static void SaveGoldenBytes()
    {
        byte[] data = Encode(delegate(BinaryWriter w) { SaveWire.WriteSave(w, true, 1.25f, -2f, 3f, new byte[] { 4, 5, 6 }); });
        EqualBytes(data, 1, 0, 0, 160, 63, 0, 0, 0, 192, 0, 0, 64, 64, 3, 0, 0, 0, 4, 5, 6);
    }
    static void LoadedRoundTrip()
    {
        byte[] data = Encode(SaveWire.WriteLoaded); EqualBytes(data); SaveWire.ReadLoaded(Read(data));
        BinaryReader extra = Read(new byte[] { 9 }); SaveWire.ReadLoaded(extra);
        Check(extra.BaseStream.Position == 0, "loaded decoder consumed payload");
    }
    static void InvalidSaveLengths()
    {
        InvalidSave(new byte[] { 0, 0, 0, 0 }); InvalidSave(new byte[] { 2, 0, 0, 0, 1 });
        InvalidSave(new byte[] { 1, 0, 0, 0, 1, 2 }); InvalidSave(new byte[] { 255, 255, 255, 255 });
        InvalidSave(Encode(delegate(BinaryWriter w) { w.Write(SaveWire.MaximumSaveBytes + 1); }));
    }
    static void SaveMaximum()
    {
        byte[] bytes = new byte[SaveWire.MaximumSaveBytes]; bytes[bytes.Length - 1] = 23;
        byte[] data = Encode(delegate(BinaryWriter w) { w.Write(bytes.Length); w.Write(bytes); });
        byte[] result = SaveWire.ReadSaveBytes(Read(data));
        Check(result.Length == SaveWire.MaximumSaveBytes && result[result.Length - 1] == 23, "maximum save changed");
    }
    static void OfferRoundTrip()
    {
        LineageOffer offer = new LineageOffer { Coop = "c", PlayerId = "h", Name = "n", Hash = "x", Summary = "s" };
        byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteOffer(w, offer); });
        EqualBytes(data, 1, 1, 99, 1, 104, 1, 110, 1, 120, 1, 115);
        LineageOffer read = LineageWire.ReadOffer(Read(data));
        Check(read.Version == 1 && read.Coop == "c" && read.PlayerId == "h" && read.Name == "n" && read.Hash == "x" && read.Summary == "s", "offer changed");
    }
    static void ReplyRoundTrip()
    {
        LineageReply reply = new LineageReply { Found = true, Slot = -1, Hash = "a", LastSync = "b", Summary = "c", PlayerId = "d", Name = "e", FreeSlot = true };
        byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteReply(w, reply); });
        EqualBytes(data, 1, 255, 255, 255, 255, 1, 97, 1, 98, 1, 99, 1, 100, 1, 101, 1);
        LineageReply read = LineageWire.ReadReply(Read(data));
        Check(read.Found && read.Slot == -1 && read.Hash == "a" && read.LastSync == "b" && read.Summary == "c" && read.PlayerId == "d" && read.Name == "e" && read.FreeSlot, "reply changed");
    }
    static void CommitRoundTrip()
    {
        LineageCommit commit = new LineageCommit { Coop = "c", PlayerId = "i", Name = "n", SaveId = "s", Bytes = new byte[] { 3, 4 } };
        byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteCommit(w, commit); });
        EqualBytes(data, 1, 99, 1, 105, 1, 110, 1, 115, 1, 2, 0, 0, 0, 3, 4);
        LineageCommit read = LineageWire.ReadCommit(Read(data));
        Check(read.Coop == "c" && read.PlayerId == "i" && read.Name == "n" && read.SaveId == "s", "commit metadata changed");
        EqualBytes(read.Bytes, 3, 4);
    }
    static void EmptyCommit()
    {
        LineageCommit commit = new LineageCommit { Coop = "c", PlayerId = "i", Name = "n", SaveId = "s" };
        byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteCommit(w, commit); });
        EqualBytes(data, 1, 99, 1, 105, 1, 110, 1, 115, 0);
        Check(LineageWire.ReadCommit(Read(data)).Bytes == null, "no-save commit changed");
    }
    static void BadCommitLength()
    {
        byte[] data = Encode(delegate(BinaryWriter w) { w.Write(""); w.Write(""); w.Write(""); w.Write(""); w.Write(true); w.Write(2); w.Write((byte)1); });
        Check(LineageWire.ReadCommit(Read(data)) == null, "short commit accepted");
        data = Encode(delegate(BinaryWriter w) { w.Write(""); w.Write(""); w.Write(""); w.Write(""); w.Write(true); w.Write(-1); });
        Check(LineageWire.ReadCommit(Read(data)) == null, "negative commit accepted");
    }
    static void RequestRoundTrip()
    {
        for (byte i = 1; i <= 3; i++)
        {
            byte kind = i; byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteRequest(w, kind); });
            EqualBytes(data, kind); Check(LineageWire.ReadRequest(Read(data)) == kind, "request changed");
        }
    }
    static void UploadRoundTrip()
    {
        byte[] data = Encode(delegate(BinaryWriter w) { LineageWire.WriteUpload(w, new byte[] { 8, 9 }); });
        EqualBytes(data, 2, 0, 0, 0, 8, 9); int length; byte[] bytes = LineageWire.ReadUpload(Read(data), out length);
        Check(length == 2, "upload length changed"); EqualBytes(bytes, 8, 9);
        bytes = LineageWire.ReadUpload(Read(new byte[] { 3, 0, 0, 0, 8 }), out length);
        Check(length == 3 && bytes.Length == 0, "truncated upload accepted");
    }
    static void MoreSeatReadiness()
    {
        SeatReadiness seats = new SeatReadiness(); seats.Join(2); seats.Join(3); seats.Join(4);
        Check(seats.HasUnsent && !seats.AnyReady && !seats.Loaded(2), "unsent seat accepted");
        seats.MarkSent(2); seats.MarkSent(3);
        Check(seats.Loaded(2) && !seats.Loaded(2) && !seats.Loaded(9), "duplicate or unknown load accepted");
        Check(seats.ReadyForSeat(2, true, seats.AnyReady) && !seats.ReadyForSeat(3, true, seats.AnyReady), "wrong host seat readiness");
        seats.Leave(2); Check(!seats.AnyReady, "departed seat remains ready");
        Check(seats.Loaded(3) && seats.AnyReady, "remaining seat not ready");
        seats.ResetGuests(); Check(!seats.AnyReady && seats.HasUnsent, "reset did not clear readiness");
        seats.Clear(); Check(!seats.HasUnsent, "clear retained seats");
    }
    static void GuestReadiness()
    {
        SeatReadiness seats = new SeatReadiness();
        Check(seats.ReadyForSeat(4, false, true), "ready guest gate closed");
        Check(!seats.ReadyForSeat(4, false, false) && !seats.ReadyForSeat(4, true, false), "global gate ignored");
    }

    static void CheckpointGuest(FakeBus guest, int[] received)
    {
        guest.On(SaveWire.SaveMessage, delegate(BinaryReader r)
        {
            SavePosition p = SaveWire.ReadPosition(r);
            byte[] bytes = SaveWire.ReadSaveBytes(r);
            Check(p.HasPosition && p.X == 4 && bytes.Length == 1 && bytes[0] == 42, "checkpoint changed in transit");
            received[0]++;
            guest.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        });
    }

    static void CheckpointFlow()
    {
        FakeNetwork network = new FakeNetwork(); FakeBus host = network.Add(1);
        SeatReadiness seats = new SeatReadiness();
        host.PlayerJoined += seats.Join; host.PlayerLeft += seats.Leave;
        FakeBus guest2 = network.Add(2), guest3 = network.Add(3), guest4 = network.Add(4);
        int[] received = new int[1]; int loaded = 0;
        CheckpointGuest(guest2, received); CheckpointGuest(guest3, received); CheckpointGuest(guest4, received);
        host.On(SaveWire.LoadedMessage, delegate(BinaryReader r)
        {
            if (!seats.Loaded(host.Sender)) return;
            SaveWire.ReadLoaded(r); loaded++;
        });
        for (int seat = 2; seat <= 4; seat++)
        {
            host.SendTo(seat, SaveWire.SaveMessage, true, delegate(BinaryWriter w)
            { SaveWire.WriteSave(w, true, 4, 5, 6, new byte[] { 42 }); });
            seats.MarkSent(seat);
        }
        network.Pump();
        Check(received[0] == 3 && loaded == 3, "not all guests completed checkpoint transfer");
        Check(seats.ReadyForSeat(2, true, seats.AnyReady) && seats.ReadyForSeat(3, true, seats.AnyReady)
            && seats.ReadyForSeat(4, true, seats.AnyReady), "checkpoint readiness missing");
        Check(network.Sent.Count == 6 && network.Sent[0].Type == 10 && network.Sent[3].Type == 11, "checkpoint message order changed");
        guest2.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded); network.Pump();
        Check(loaded == 3, "duplicate loaded triggered another snapshot");
        network.Remove(3);
        Check(!seats.ReadyForSeat(3, true, seats.AnyReady) && seats.ReadyForSeat(4, true, seats.AnyReady), "departure affected other seats");
    }
}
