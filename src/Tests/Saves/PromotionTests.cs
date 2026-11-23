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

    // T-213 (10): a guest that becomes the host sends its save to a seat that is still waiting for its first one.
    static void PromotedHostSendsToWaitingSeat()
    {
        SeatReadiness seats = new SeatReadiness();
        // As a guest: seat 3 joined after this player's own join and announced its load; seat 4 joined and has not yet.
        seats.SawJoin(3); seats.SawLoaded(3); seats.SawJoin(4);
        Check(!seats.HasUnsent, "a guest's notes about the other guests counted as host-side unsent seats");
        seats.Promote(new int[] { 1, 3, 4 });
        Check(seats.ReadyForSeat(1, true, true) && seats.ReadyForSeat(3, true, true), "seats already in the world were not ready after the promotion");
        Check(seats.HasUnsent && !seats.IsSent(4), "a seat still waiting for its first save was marked sent: it would wait for ever");
        Check(seats.IsSent(1) && seats.IsSent(3), "a seat already in the world would get the checkpoint again (forced reload)");
        Check(!seats.ReadyForSeat(4, true, true), "a seat still waiting for its first save was ready (its Ori would be spawned)");
        seats.MarkSent(4);
        Check(seats.Loaded(4) && seats.ReadyForSeat(4, true, true), "the waiting seat's load was not accepted after the new host's checkpoint");
    }

    // T-213 review (1): the new guest's hello and the role change arrive in one receive loop: the hello is handled
    // as the host (Join: unsent) before the promotion runs. The promotion must not turn that seat into "ready".
    static void PromotionKeepsSeatJoinedAfterRoleChange()
    {
        SeatReadiness seats = new SeatReadiness();
        seats.Join(4);
        seats.Promote(new int[] { 1, 4 });
        Check(seats.HasUnsent && !seats.IsSent(4), "a seat that joined between the role change and the promotion was marked sent: it would wait for its first save for ever");
        Check(!seats.ReadyForSeat(4, true, true), "a seat that joined between the role change and the promotion was ready before it had a save");
        Check(seats.IsSent(1) && seats.ReadyForSeat(1, true, true), "a seat already in the world was not ready after the promotion");
    }

    static void WaitingSeatMarkEnds()
    {
        SeatReadiness seats = new SeatReadiness();
        seats.SawJoin(4); seats.Leave(4); seats.Promote(new int[] { 4 });   // another player on seat 4 by then: unknown, taken as playing
        Check(!seats.HasUnsent, "a seat that left kept its waiting mark for the next player on it");
        seats = new SeatReadiness();
        seats.SawJoin(3); seats.Clear(); seats.Promote(new int[] { 3 });
        Check(!seats.HasUnsent, "waiting marks survived the session reset");
        seats = new SeatReadiness();
        seats.SawJoin(3); seats.Promote(new int[] { 3 }); seats.MarkSent(3); seats.Loaded(3); seats.Promote(new int[] { 3 });
        Check(!seats.HasUnsent, "a waiting mark was used twice");
    }
}
