namespace OriCoop.Saves
{
    // T-043 (room-owned saves): what the room's save authority (the host) does with a guest's save point.
    //
    // The room has one save timeline. A guest touching a save point offers its save together with the timeline it is
    // based on (the hash of the last commit it received). The authority names that guest the saver only when the base
    // is the room's current timeline; anything else would overwrite a newer save (another seat saved first, or this
    // guest has not caught up yet), so it is dropped. Two seats saving the same checkpoint therefore get one saver:
    // the first accepted save commits a new timeline and the other offer's base is stale.
    public enum OfferVerdict { Accept, NotHost, Busy, SeatNotLoaded, NoTimeline, Stale }

    public static class SaverPick
    {
        // host: this PC is the save authority. busy: not ready, loading, adopting or disabled.
        // seatLoaded: the offering seat plays this world. settled: the room's lineage is decided (Proceed / Done).
        // timeline: hash of the room's current commit ("" before the first save file). offerBase: the guest's base.
        public static OfferVerdict Judge(bool host, bool busy, bool seatLoaded, bool settled, string timeline, string offerBase)
        {
            if (!host) return OfferVerdict.NotHost;
            if (busy) return OfferVerdict.Busy;
            if (!seatLoaded) return OfferVerdict.SeatNotLoaded;
            if (!settled) return OfferVerdict.NoTimeline;
            if ((timeline ?? "") != (offerBase ?? "")) return OfferVerdict.Stale;
            return OfferVerdict.Accept;
        }
    }
}
