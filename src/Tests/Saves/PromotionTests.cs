using System;
using OriCoop.Saves;

// T-126: server mode hands the host role to a guest mid-session (arbiter change).
partial class SaveTests
{
    static void PromotionOnlyForJoinedGuest()
    {
        HostPromotion p = new HostPromotion();
        Check(!p.Check(true), "a player that was host from the start was treated as promoted");
        p.Joined();
        Check(!p.Check(false), "a guest that is still a guest was promoted");
        Check(p.Check(true) && p.Promoted, "a joined guest that became host was not promoted");
        Check(!p.Check(true), "promotion ran twice");
        p.Reset();
        Check(!p.Promoted && !p.Check(true), "promotion survived the session reset");
    }

    static void PromotedHostSaveSlot()
    {
        // The guest joined in-game from its own slot 7: slot 7 is never a candidate (firstEmpty excludes it).
        Check(HostPromotion.SaveSlot(4, true, 6) == 4, "the co-op copy kept this session must hold the promoted host's saves");
        Check(HostPromotion.SaveSlot(-1, true, 6) == -1, "the old host's save was never kept: saving must stay off, not overwrite a slot");
        Check(HostPromotion.SaveSlot(-1, true, -1) == -1, "all slots full: saving must stay off");
        Check(HostPromotion.SaveSlot(-1, false, 6) == 6, "the old host had no save yet: a fresh empty slot");
        Check(HostPromotion.SaveSlot(-1, false, -1) == -1, "no co-op copy and no empty slot: saving must stay off");
    }

    static void PromotedGuestDoesNotOffer()
    {
        Scenario s = new Scenario(); s.Guest.Reply.FreeSlot = true; s.Offer();
        Check(s.Host.Outcome == "new", "setup: the first host did not settle the lineage");
        // P1 leaves; the server makes P2 the host; P3 is about to join.
        s.Network.Remove(1); s.Network.Host = 2;
        FakeBus bus3 = s.Network.Add(3); FakeLineage guest3 = new FakeLineage(bus3);
        LineageMachine machine3 = new LineageMachine(bus3, s.Clock, guest3); machine3.Init();
        int before = s.Network.Sent.Count;
        s.GuestMachine.Promote();
        Check(s.GuestMachine.HostGate(), "the promoted host's gate stayed closed: joiners would never get a checkpoint");
        s.Network.Pump();
        for (int i = before; i < s.Network.Sent.Count; i++)
            Check(!(s.Network.Sent[i].From == 2 && s.Network.Sent[i].Type == LineageWire.Offer),
                "the promoted host offered its own slot as the co-op save");
    }

    static void PromotedHostSeatsReady()
    {
        SeatReadiness seats = new SeatReadiness();
        seats.JoinReady(3);
        Check(seats.ReadyForSeat(3, true, seats.AnyReady), "a seat already in the world was not ready after the promotion (no puppet respawn)");
        Check(!seats.HasUnsent, "a seat already in the world would get the checkpoint again (forced reload)");
        Check(!seats.Loaded(3), "a seat already in the world would get a second world snapshot");
    }
}
