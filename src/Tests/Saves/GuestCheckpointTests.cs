using System.IO;
using OriCoop.Saves;

partial class SaveTests
{
    static void SendCheckpoint(FakeBus sender, int seat, byte value)
    {
        sender.SendTo(seat, SaveWire.SaveMessage, true, w => SaveWire.WriteSave(w, true, value, 0, 0, new byte[] { value }));
    }

    static void NewestCheckpointDuringLoad()
    {
        FakeNetwork room = new FakeNetwork();
        FakeBus host = room.Add(1), guest = room.Add(2);
        GuestCheckpoints saves = new GuestCheckpoints(guest, () => true, (position, bytes) => {
            Check(position.HasPosition && position.X == bytes[0], "position does not belong to its save");
            return bytes;
        });
        SendCheckpoint(host, 2, 11); room.Pump();
        Check(saves.Begin()[0] == 11, "first restore did not start");
        SendCheckpoint(host, 2, 22); room.Pump();
        saves.ExpectWipe();
        SendCheckpoint(host, 2, 33); room.Pump();
        Check(saves.HasPending && !saves.CanFinish, "a newer save received during a restore was discarded");
        Check(saves.Begin() == null && !saves.LoadingWipe, "the pending wipe changed an active native load");

        host.SendTo(2, SaveWire.SaveMessage, true, w => { w.Write(true); w.Write(99f); w.Write(0f); w.Write(0f); w.Write(4); w.Write((byte)99); });
        try { room.Pump(); throw new System.Exception("truncated checkpoint accepted"); }
        catch (InvalidDataException) { }
        Check(!saves.Finish(), "superseded restore acknowledged");
        Check(saves.Begin()[0] == 33 && saves.LoadingWipe, "a truncated or earlier packet replaced the newest complete save");
        Check(saves.CanFinish, "latest restore cannot acknowledge completion");
        Check(saves.Finish(), "latest restore not acknowledged");

        SendCheckpoint(host, 2, 44); SendCheckpoint(host, 2, 55); room.Pump();
        Check(saves.Begin()[0] == 55 && !saves.LoadingWipe, "older pending save won before loading began");
    }

    static void CheckpointHostAndSession()
    {
        FakeNetwork room = new FakeNetwork();
        FakeBus oldHost = room.Add(1), guest = room.Add(2), newHost = room.Add(3);
        bool inSession = true;
        GuestCheckpoints saves = new GuestCheckpoints(guest, () => inSession, (p, bytes) => bytes);
        SendCheckpoint(oldHost, 2, 11); room.Pump(); saves.Begin();
        saves.ExpectWipe();
        SendCheckpoint(oldHost, 2, 22); room.Pump();
        guest.Leave(1); room.Host = 3; guest.HostSeat = 3;
        SendCheckpoint(oldHost, 2, 33); room.Pump();
        Check(!saves.CanFinish && !saves.HasPending, "former host can complete or queue a load");
        SendCheckpoint(newHost, 2, 44); room.Pump(); saves.Finish();
        Check(saves.Begin()[0] == 44 && !saves.LoadingWipe, "new host inherited a former host's checkpoint or wipe");
        SendCheckpoint(newHost, 2, 55); room.Pump();
        guest.End();
        inSession = false;
        SendCheckpoint(newHost, 2, 66); room.Pump();
        Check(!saves.CanFinish && !saves.HasPending, "a checkpoint survived session teardown");
    }
}
