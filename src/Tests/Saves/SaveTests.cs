using System;
using System.IO;
using OriCoop.Saves;

partial class SaveTests
{
    static int failures;
    static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    static void SaveRoundTrip()
    {
        MemoryStream stream = new MemoryStream();
        SaveWire.WriteSave(new BinaryWriter(stream), true, 1.25f, -2f, 3f, new byte[] { 4, 5, 6 });
        stream.Position = 0;
        BinaryReader reader = new BinaryReader(stream);
        SavePosition position = SaveWire.ReadPosition(reader);
        byte[] bytes = SaveWire.ReadSaveBytes(reader);
        Check(position.HasPosition && position.X == 1.25f && position.Y == -2f && position.Z == 3f, "position changed");
        Check(bytes.Length == 3 && bytes[0] == 4 && bytes[2] == 6, "save changed");
    }

    static void SeatReady()
    {
        SeatReadiness seats = new SeatReadiness();
        seats.Join(2); seats.Join(3);
        seats.MarkSent(2);
        Check(seats.Loaded(2), "sent seat did not load");
        Check(seats.ReadyForSeat(2, true, seats.AnyReady), "loaded seat not ready");
        Check(!seats.ReadyForSeat(3, true, seats.AnyReady), "unsent seat ready");
        seats.Leave(2);
        Check(!seats.AnyReady, "departed seat remains ready");
    }

    static int Main()
    {
        Run("save message round trip", SaveRoundTrip);
        Run("seat readiness after departure", SeatReady);
        Run("matching save handshake", SameSave);
        Run("guest without save", NewSave);
        Run("guest without free slot", NoFreeSlot);
        Run("older guest host chosen", OlderGuestHostChosen);
        Run("newer guest upload and adoption", NewerGuestUpload);
        Run("invalid upload falls back", InvalidUpload);
        Run("reply timeout boundary and late reply", ReplyTimeout);
        Run("upload timeout boundary", UploadTimeout);
        Run("cancel request and delay", CancelDelay);
        Run("first reply wins with four seats", FirstReplyWinsFourSeats);
        Run("player leaves during offer", DepartureDuringOffer);
        Run("session reset", SessionReset);
        Run("offer failure falls back", FailedOffer);
        Run("unsolicited messages ignored", UnsolicitedMessages);
        Run("save message exact bytes", SaveGoldenBytes);
        Run("loaded message wire round trip", LoadedRoundTrip);
        Run("invalid checkpoint lengths", InvalidSaveLengths);
        Run("maximum checkpoint length", SaveMaximum);
        Run("offer wire round trip", OfferRoundTrip);
        Run("reply wire round trip", ReplyRoundTrip);
        Run("commit wire round trip", CommitRoundTrip);
        Run("commit without save", EmptyCommit);
        Run("invalid commit lengths", BadCommitLength);
        Run("request subcodes round trip", RequestRoundTrip);
        Run("upload wire round trip", UploadRoundTrip);
        Run("host seat readiness and departure", MoreSeatReadiness);
        Run("guest seat readiness", GuestReadiness);
        Run("four seat checkpoint and loaded flow", CheckpointFlow);
        return failures == 0 ? 0 : 1;
    }
}
