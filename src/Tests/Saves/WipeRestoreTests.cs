using OriCoop.Saves;

// T-242: a team wipe's checkpoint round. The host pushes its checkpoint to every seat (SaveSync.PushCheckpointNow ->
// SeatReadiness.ResetGuests); the round is over only once every seat has reported its load or left. In the net-4p
// failure seat 3 loaded first, its game saved natively right after the reload, and the host took that save as the
// room's checkpoint while seats 2 and 4 were still loading: their skill trees and points then came back wrong.
partial class SaveTests
{
    // The host's judgement exactly as SaveSync.ReceiveOffer makes it, for a seat whose offer bases on the room's save.
    static OfferVerdict HostJudges(SeatReadiness seats, int seat)
    {
        return SaverPick.Judge(true, !seats.AnyReady, seats.Restoring, seats.ReadyForSeat(seat, true, seats.AnyReady), true, "h1", "h1");
    }

    static void WipeRoundIgnoresFirstReloadedSeat()
    {
        SeatReadiness seats = new SeatReadiness(); seats.Join(2); seats.Join(3); seats.Join(4);
        foreach (int seat in new int[] { 2, 3, 4 }) { seats.MarkSent(seat); seats.Loaded(seat); }
        Check(!seats.Restoring, "a join is not a checkpoint round");
        Verdict(OfferVerdict.Accept, HostJudges(seats, 3), "save point in normal play");

        // Team wipe: the host restored and pushes its checkpoint to everyone.
        seats.ResetGuests();
        foreach (int seat in new int[] { 2, 3, 4 }) seats.MarkSent(seat);
        Check(seats.Restoring, "the wipe round did not start");
        Verdict(OfferVerdict.Busy, HostJudges(seats, 3), "nobody has reloaded yet");

        // Seat 3 reloads first and its native save after the reload reaches the host.
        Check(seats.Loaded(3), "seat 3's load not taken");
        Check(seats.Restoring, "one reloaded seat ended the round");
        Verdict(OfferVerdict.Restoring, HostJudges(seats, 3), "seat 3's save while 2 and 4 still load the checkpoint");
        Check(seats.Loaded(2), "seat 2's load not taken");
        Verdict(OfferVerdict.Restoring, HostJudges(seats, 2), "seat 2's save while 4 still loads");

        // The last seat reloads: the round is over and save points work again.
        Check(seats.Loaded(4), "seat 4's load not taken");
        Check(!seats.Restoring, "the round outlived the last load");
        Verdict(OfferVerdict.Accept, HostJudges(seats, 3), "save point after everyone reloaded");
    }

    static void WipeRoundEndsWhenTheLastWaitingSeatLeaves()
    {
        SeatReadiness seats = new SeatReadiness(); seats.Join(2); seats.Join(3);
        seats.ResetGuests(); seats.MarkSent(2); seats.MarkSent(3);
        seats.Loaded(2);
        Check(seats.Restoring, "round ended with seat 3 still loading");
        seats.Leave(3);
        Check(!seats.Restoring, "the round waits for a seat that left");
        Verdict(OfferVerdict.Accept, HostJudges(seats, 2), "seat 2's save after the loading seat left");

        // A session reset ends any round; a host with no guests has no round to wait for.
        seats.ResetGuests(); seats.MarkSent(2);
        Check(seats.Restoring, "second round did not start");
        seats.Clear();
        Check(!seats.Restoring, "clear kept the round");
        seats.ResetGuests();
        Check(!seats.Restoring, "a round with no seats never ends");
    }

    static void SeatJoiningDuringWipeRoundKeepsItOpen()
    {
        SeatReadiness seats = new SeatReadiness(); seats.Join(2);
        seats.ResetGuests(); seats.MarkSent(2);
        seats.Join(3);   // a new player arrives while the room reloads; it gets the same checkpoint
        seats.Loaded(2);
        Check(seats.Restoring, "the round ended before the new seat loaded the checkpoint");
        Verdict(OfferVerdict.Restoring, HostJudges(seats, 2), "seat 2's save while the new seat loads");
        seats.MarkSent(3); seats.Loaded(3);
        Check(!seats.Restoring, "round did not end with the new seat's load");
    }

    static void RestoringVerdictOrder()
    {
        // Only the host judges, and a busy host stays Busy; the round is checked before the seat and the timeline.
        Verdict(OfferVerdict.NotHost, SaverPick.Judge(false, false, true, true, true, "h1", "h1"), "guest");
        Verdict(OfferVerdict.Busy, SaverPick.Judge(true, true, true, true, true, "h1", "h1"), "busy host");
        Verdict(OfferVerdict.Restoring, SaverPick.Judge(true, false, true, true, true, "h1", "h1"), "round running");
        Verdict(OfferVerdict.Restoring, SaverPick.Judge(true, false, true, false, true, "h2", "h1"), "round before stale");
        Verdict(OfferVerdict.Accept, SaverPick.Judge(true, false, false, true, true, "h1", "h1"), "no round");
        // The old six-argument judgement never sees a round.
        Verdict(OfferVerdict.Accept, SaverPick.Judge(true, false, true, true, "h1", "h1"), "six-argument judge");
    }

    static void RunWipeRestoreTests()
    {
        Run("T-242 a wipe's checkpoint round ignores the save of the seat that reloaded first", WipeRoundIgnoresFirstReloadedSeat);
        Run("T-242 the wipe round ends when the last waiting seat leaves; reset ends it", WipeRoundEndsWhenTheLastWaitingSeatLeaves);
        Run("T-242 a seat joining during the wipe round keeps it open until it loads", SeatJoiningDuringWipeRoundKeepsItOpen);
        Run("T-242 restoring verdict order", RestoringVerdictOrder);
    }
}
