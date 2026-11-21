using System;
using System.IO;
using OriCoop.Saves;

// T-043: the room owns the save. A guest's save point becomes the room's save only when it is based on the room's
// current timeline; expectations come from the ticket (docs/tickets/T-043.md, rules 3 and 4).
partial class SaveTests
{
    static void SaveOfferRoundTrip()
    {
        byte[] wire = Encode(delegate(BinaryWriter w) { SaveWire.WriteSaveOffer(w, "abc123", new byte[] { 9, 8, 7 }); });
        string timeline;
        byte[] bytes = SaveWire.ReadSaveOffer(Read(wire), out timeline);
        Check(timeline == "abc123", "base timeline changed: " + timeline);
        EqualBytes(bytes, 9, 8, 7);
        byte[] empty = Encode(delegate(BinaryWriter w) { SaveWire.WriteSaveOffer(w, null, new byte[] { 1 }); });
        SaveWire.ReadSaveOffer(Read(empty), out timeline);
        Check(timeline == "", "no timeline must travel as empty, got " + timeline);
        // Pinned wire format (T-140 rule: changing it needs a new ProtocolVersion): string "ab" (1+2) + int 1 + 1 byte.
        byte[] pinned = Encode(delegate(BinaryWriter w) { SaveWire.WriteSaveOffer(w, "ab", new byte[] { 5 }); });
        EqualBytes(pinned, 2, (byte)'a', (byte)'b', 1, 0, 0, 0, 5);
        Check(SaveWire.SaveOffer == 87, "SaveOffer must stay message 87 (docs/net-msg-ids.md), got " + SaveWire.SaveOffer);
    }

    static void OfferTruncatedRejected()
    {
        byte[] wire = Encode(delegate(BinaryWriter w) { SaveWire.WriteSaveOffer(w, "t", new byte[] { 1, 2, 3 }); });
        byte[] cut = new byte[wire.Length - 1]; Array.Copy(wire, cut, cut.Length);
        string timeline;
        try { SaveWire.ReadSaveOffer(Read(cut), out timeline); }
        catch (InvalidDataException) { return; }
        throw new Exception("truncated save offer accepted");
    }

    static void Verdict(OfferVerdict expected, OfferVerdict actual, string what)
    {
        Check(actual == expected, what + ": expected " + expected + ", got " + actual);
    }

    static void SaverRules()
    {
        // Normal: the guest's base is the room's current save.
        Verdict(OfferVerdict.Accept, SaverPick.Judge(true, false, true, true, "h1", "h1"), "current base");
        // New game: no save file yet on either side ("" == ""), lineage settled.
        Verdict(OfferVerdict.Accept, SaverPick.Judge(true, false, true, true, "", ""), "first save of a new room");
        Verdict(OfferVerdict.Accept, SaverPick.Judge(true, false, true, true, null, ""), "null timeline is empty");
        // Only the authority judges.
        Verdict(OfferVerdict.NotHost, SaverPick.Judge(false, false, true, true, "h1", "h1"), "guest receiving an offer");
        // Loading / adopting / disabled.
        Verdict(OfferVerdict.Busy, SaverPick.Judge(true, true, true, true, "h1", "h1"), "host busy");
        // A seat that does not play the world yet (checkpoint not loaded).
        Verdict(OfferVerdict.SeatNotLoaded, SaverPick.Judge(true, false, false, true, "h1", "h1"), "seat still loading");
        // Lineage still being decided (offer / choosing): no room timeline to extend.
        Verdict(OfferVerdict.NoTimeline, SaverPick.Judge(true, false, true, false, "h1", "h1"), "lineage undecided");
    }

    static void StaleOfferNeverOverwrites()
    {
        // The room moved on (another seat or the host saved: h2); this guest still bases on h1.
        Verdict(OfferVerdict.Stale, SaverPick.Judge(true, false, true, true, "h2", "h1"), "older base");
        // A guest without any commit yet offers against a room that has a save.
        Verdict(OfferVerdict.Stale, SaverPick.Judge(true, false, true, true, "h2", ""), "no base against a saved room");
        // A guest that somehow holds a different timeline (another room's save).
        Verdict(OfferVerdict.Stale, SaverPick.Judge(true, false, true, true, "h2", "zz"), "foreign base");
    }

    static void OneSaverPerCheckpoint()
    {
        // Seats 2 and 3 touch the same save point together; both base on h1. The first accepted save commits h2,
        // so the second offer is stale and dropped: one saver per checkpoint, symmetric in which seat came first.
        foreach (int first in new int[] { 2, 3 })
        {
            string room = "h1";
            int saver = -1, taken = 0;
            int[] order = first == 2 ? new int[] { 2, 3 } : new int[] { 3, 2 };
            foreach (int seat in order)
            {
                if (SaverPick.Judge(true, false, true, true, room, "h1") != OfferVerdict.Accept) continue;
                saver = seat; taken++; room = "h2-from-" + seat;
            }
            Check(taken == 1, "first=" + first + ": " + taken + " savers named for one checkpoint");
            Check(saver == first, "first=" + first + ": saver was seat " + saver);
        }
    }

    static void RunRoomSaveTests()
    {
        Run("save offer round trip (T-043)", SaveOfferRoundTrip);
        Run("truncated save offer rejected (T-043)", OfferTruncatedRejected);
        Run("saver rules: authority, loading, lineage (T-043)", SaverRules);
        Run("stale save offer never overwrites a newer room save (T-043)", StaleOfferNeverOverwrites);
        Run("one saver per checkpoint (T-043)", OneSaverPerCheckpoint);
    }
}
