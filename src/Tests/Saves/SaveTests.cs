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
        Run("room save selection cannot redirect a live or linked save", delegate {
            Check(RoomSaveSelection.CanSelect(true, false, false, false), "host may pick before connecting/loading");
            Check(!RoomSaveSelection.CanSelect(false, false, false, false), "guest cannot change host save");
            Check(!RoomSaveSelection.CanSelect(true, true, false, false), "live game must not autosave into another slot");
            Check(!RoomSaveSelection.CanSelect(true, false, true, false), "negotiated network save is fixed");
            Check(!RoomSaveSelection.CanSelect(true, false, false, true), "persistent room backup target is fixed");
        });
        Run("T-214 seat readiness: who has not reported its checkpoint load, and for how long", delegate {
            SeatReadiness waiting = new SeatReadiness(); waiting.Join(2); waiting.Join(4); float waited;
            Check(waiting.LongestWait(10f, out waited) == 0, "a seat without a sent checkpoint is waiting");
            waiting.MarkSent(2, 10f); waiting.MarkSent(4, 30f);
            Check(waiting.LongestWait(40f, out waited) == 2 && waited == 30f, "longest wait is seat 2 for 30 s, got " + waited);
            waiting.Loaded(2);
            Check(waiting.LongestWait(40f, out waited) == 4 && waited == 10f, "after seat 2 loaded: seat 4 for 10 s, got " + waited);
            waiting.Loaded(4);
            Check(waiting.LongestWait(40f, out waited) == 0, "everyone loaded, still waiting");
        });
        Run("save message round trip", SaveRoundTrip);
        Run("seat readiness after departure", SeatReady);
        Run("device primary rebind preserves save handshake", delegate {
            SeatReadiness seats = new SeatReadiness(); seats.Join(3); seats.MarkSent(3); seats.Loaded(3);
            seats.Rebind(3, 2);
            Check(seats.ReadyForSeat(2, true, true) && !seats.ReadyForSeat(3, true, true), "ready state must follow device primary");
            seats.Join(4); seats.Rebind(4, 3);
            Check(seats.HasUnsent && !seats.ReadyForSeat(3, true, true), "pending device stays pending after rebind");
        });
        Run("matching save handshake", SameSave);
        Run("guest without save", NewSave);
        Run("guest without free slot", NoFreeSlot);
        Run("older guest host chosen", OlderGuestHostChosen);
        Run("newer guest upload and adoption", NewerGuestUpload);
        Run("invalid upload falls back", InvalidUpload);
        Run("reply timeout boundary and late reply", ReplyTimeout);
        Run("upload timeout boundary", UploadTimeout);
        Run("cancel request and delay", CancelDelay);
        Run("every reply counts with four seats", EveryReplyCountsFourSeats);
        Run("T-128 guest without a copy answering first does not skip the choice", NoCopyFirstStillChooses);
        Run("T-128 partner choice uploads from the diverged guest only", UploadFromChosenGuest);
        Run("T-128 another guest's upload is ignored", OtherGuestUploadIgnored);
        Run("T-128 host waits for every guest's reply", WaitsForEveryReply);
        Run("T-128 reply timeout decides on the replies that came", PartialRepliesTimeout);
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
        Run("T-223 guest restores the newest complete checkpoint during a load", NewestCheckpointDuringLoad);
        Run("T-223 checkpoints belong to the current host and session", CheckpointHostAndSession);
        Run("T-223 each native load uses its checkpoint's own seat skill record", CheckpointSkillsDuringLoad);
        Run("T-129 seat joining after the lineage settled gets the commit", LateJoinerGetsCommit);
        Run("T-129 seat joining while the lineage is open gets the decision commit", OpenLineageJoinerGetsCommit);
        Run("T-126 promotion only for a guest that joined", PromotionOnlyForJoinedGuest);
        Run("T-126 promoted host never saves into its own slot", PromotedHostSaveSlot);
        Run("T-126 promoted host does not offer its own slot", PromotedGuestDoesNotOffer);
        Run("T-126 seats in the world are ready after promotion", PromotedHostSeatsReady);
        Run("T-213 promoted host sends its save to a seat still waiting for its first one", PromotedHostSendsToWaitingSeat);
        Run("T-213 a waiting mark ends with the seat, the session and the promotion", WaitingSeatMarkEnds);
        Run("T-213 the promotion keeps a seat that joined after the role change unsent", PromotionKeepsSeatJoinedAfterRoleChange);
        // T-157: after SaveSync disabled itself (failed load) a restart is a real quit to the title, not its load.
        Run("load restart: only while loading and not disabled", delegate {
            Check(LoadRestart.IsOwn(true, false), "a running load restart was taken for a quit");
            Check(!LoadRestart.IsOwn(true, true), "a quit after a failed (disabled) load was taken for the load restart");
            Check(!LoadRestart.IsOwn(false, false) && !LoadRestart.IsOwn(false, true), "no load running, yet taken for one");
        });
        Run("co-op save paths", FolderPathTests);
        Run("first empty co-op slot", EmptyCoopSlotTests);
        RunRoomSaveTests();
        RunWipeRestoreTests();
        return failures == 0 ? 0 : 1;
    }
}
