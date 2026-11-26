using OriCoop.Saves;

partial class SaveTests
{
    static void HandoverDuringFirstLoad()
    {
        FakeNetwork room = new FakeNetwork();
        FakeBus host = room.Add(1), playing = room.Add(4);
        GuestCheckpoints playingSave = new GuestCheckpoints(playing, () => true, (p, bytes) => bytes, () => null);
        SendCheckpoint(host, 4, 14); room.Pump();
        Check(playingSave.Begin()[0] == 14 && playingSave.Finish(), "P4 did not restore the original host's world");
        FakeBus next = room.Add(2), waiting = room.Add(3);
        SeatReadiness seats = new SeatReadiness(), established = new SeatReadiness();
        HostPromotion promotion = new HostPromotion();
        GuestCheckpoints nextSave = new GuestCheckpoints(next, () => true, (p, bytes) => bytes, () => null);
        GuestCheckpoints waitingSave = new GuestCheckpoints(waiting, () => true, (p, bytes) => bytes, () => null);
        bool nextLoaded = false;
        int snapshots = 0;
        seats.Observe(next, () => nextLoaded, seat => snapshots++);
        next.PlayerLeft += seats.Leave;
        established.Observe(playing, () => true, seat => { });

        // P4 was already playing when P2 arrived. P3 then said hello before P2's first load finished.
        next.Join(1); next.Join(4); playing.Join(2);
        SendCheckpoint(host, 2, 12); room.Pump();
        Check(nextSave.Begin()[0] == 12, "P2 did not begin the original host's checkpoint");
        next.Join(3);
        Check(!waitingSave.HasPending && nextSave.Finish(), "wrong checkpoint order before promotion");
        nextLoaded = true; promotion.Joined();
        next.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded); room.Pump();
        room.Remove(1); next.Leave(1); waiting.Leave(1);
        room.Host = next.HostSeat = waiting.HostSeat = playing.HostSeat = 2;
        Check(promotion.Check(next.IsHost), "loaded P2 was not promoted");
        seats.Promote(new int[] { 3, 4 });
        Check(!seats.IsSent(3) && !seats.ReadyForSeat(3, true, true), "P3 never received a checkpoint but P2 treated it as loaded");
        Check(seats.ReadyForSeat(4, true, true), "P4 was playing before P2 joined and would be forced to reload");
        Check(!seats.Loaded(3), "acknowledged P3 before any transfer");

        foreach (int seat in seats.Seats)
            if (!seats.IsSent(seat)) { SendCheckpoint(next, seat, 23); seats.MarkSent(seat); }
        room.Pump();
        Check(!playingSave.HasPending && room.Sent.FindAll(p => p.From == 2 && p.Type == SaveWire.SaveMessage).Count == 1,
            "new host must send exactly one checkpoint to the waiting guest");
        Check(waitingSave.Begin()[0] == 23 && waitingSave.Finish(), "P3 did not restore P2's checkpoint");
        waiting.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        waiting.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        room.Pump();
        Check(snapshots == 1 && seats.ReadyForSeat(3, true, true), "first completed restore must start exactly one snapshot");
    }

    static void HandoverAfterSeatReuse()
    {
        FakeNetwork room = new FakeNetwork();
        room.Add(1); FakeBus next = room.Add(2), oldGuest = room.Add(3);
        SeatReadiness seats = new SeatReadiness();
        seats.Observe(next, () => false, seat => { });
        next.PlayerLeft += seats.Leave;
        next.Join(3);
        oldGuest.SendTo(2, SaveWire.LoadedMessage, true, SaveWire.WriteLoaded);
        room.Pump();
        room.Remove(3); next.Leave(3);
        room.Add(3); next.Join(3);
        room.Host = next.HostSeat = 2;
        seats.Promote(new int[] { 3 });
        Check(!seats.IsSent(3), "the new occupant inherited the former occupant's completion");
    }

    static void PeerPlayingIsNotACheckpointAck()
    {
        FakeNetwork room = new FakeNetwork();
        room.Add(1); FakeBus next = room.Add(2), playing = room.Add(4);
        SeatReadiness seats = new SeatReadiness(), established = new SeatReadiness();
        int snapshots = 0;
        seats.Observe(next, () => false, seat => snapshots++);
        established.Observe(playing, () => true, seat => { });
        next.Join(4); playing.Join(2);
        Check(room.Sent.Count == 1, "already loaded guest did not send one introduction");
        FakeNetwork.Packet introduction = room.Sent[0];
        Check(introduction.From == 4 && introduction.To == 2 && introduction.Type == 102 && introduction.Body.Length == 0,
            "expected a targeted zero-payload Msg102 from P4 to P2");
        room.Host = next.HostSeat = playing.HostSeat = 2;
        seats.Promote(new int[] { 4 }); seats.ResetGuests(); seats.MarkSent(4);
        room.Pump();
        Check(snapshots == 0 && !seats.AnyReady, "an introduction sent before promotion acknowledged the new host's transfer");
        playing.Send(SaveWire.LoadedMessage, true, SaveWire.WriteLoaded); room.Pump();
        Check(snapshots == 1 && seats.AnyReady, "only a real host checkpoint acknowledgement can start the snapshot");
    }

    static void PeerPlayingBeforeHello()
    {
        FakeNetwork room = new FakeNetwork();
        room.Add(1); FakeBus next = room.Add(2), playing = room.Add(4);
        SeatReadiness seats = new SeatReadiness(), established = new SeatReadiness();
        seats.Observe(next, () => false, seat => { });
        established.Observe(playing, () => true, seat => { });
        playing.Join(2); room.Pump(); next.Join(4);
        room.Host = next.HostSeat = 2; seats.Promote(new int[] { 4 });
        Check(seats.ReadyForSeat(4, true, true), "a later Hello erased an already confirmed load");
    }
}
