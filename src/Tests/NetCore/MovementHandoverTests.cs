using System;
using System.IO;
using OriCoop.Net;

static partial class NetCoreTests
{
    static MovementEpochs Handshake(string version, byte[] tail)
    {
        return MovementEpochs.ReadHello(new BinaryReader(new MemoryStream(tail)), version);
    }

    static byte[] Advertise(MovementEpochs local)
    {
        MemoryStream body = new MemoryStream();
        local.Write(new BinaryWriter(body));
        return body.ToArray();
    }

    static bool Admit(MovementEpochs confirmed, int seat, ulong? movementId, int frames)
    {
        MemoryStream body = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(body);
        for (int i = 0; i < frames; i++)
        {
            writer.Write((uint)(100 + i)); writer.Write((ushort)0);
            for (int axisAndFlags = 0; axisAndFlags < 5; axisAndFlags++) writer.Write((byte)0);
            for (int positionAndVelocity = 0; positionAndVelocity < 5; positionAndVelocity++) writer.Write((float)i);
        }
        if (movementId.HasValue) writer.Write(movementId.Value);
        body.Position = 0;
        bool admitted = MovementEpochs.Accepts(new BinaryReader(body), frames, confirmed.Id[seat], confirmed.Legacy);
        Equal(0L, body.Position); // The replay decoder still starts on the first tick.
        return admitted;
    }

    static void MovementHandover()
    {
        Run("movement: a replacement's Hello admits only its own redundancy batches", delegate {
            MovementEpochs old = new MovementEpochs(), next = new MovementEpochs(), waiting = new MovementEpochs();
            old.SetLocal(2, true); next.SetLocal(2, true);
            byte[] hello = Advertise(next);
            Equal(32, hello.Length);
            MovementEpochs bound = Handshake("0.2.0-net16", hello);
            Check(old.Id[2] != next.Id[2], "replacement has a new identity");
            Check(!Admit(waiting, 2, old.Id[2], 1) && !Admit(waiting, 2, next.Id[2], 1), "wait for Hello");
            for (int count = 1; count <= 4; count++)
            {
                Check(Admit(bound, 2, next.Id[2], count), "new movement admitted");
                Check(!Admit(bound, 2, old.Id[2], count), "old movement ignored");
                Check(!Admit(bound, 2, null, count), "legacy movement ignored after tagged Hello");
            }
            Check(!Admit(bound, 3, 0, 1), "unowned seat cannot move");
        });
        Run("movement: releasing and replacing a secondary preserves the primary and other players", delegate {
            MovementEpochs device = new MovementEpochs();
            device.SetLocal(1, true); device.SetLocal(3, true); device.SetLocal(4, true);
            ulong primary = device.Id[1], departed = device.Id[3], other = device.Id[4];
            Check(!device.SetLocal(3, true), "duplicate seat table is stable");
            device.SetLocal(3, false);
            Check(!Admit(Handshake("0.2.0-net16", Advertise(device)), 3, departed, 2), "released seat cannot move");
            device.SetLocal(3, true);
            MovementEpochs bound = Handshake("0.2.0-net16", Advertise(device));
            Check(!Admit(bound, 3, departed, 2) && Admit(bound, 3, device.Id[3], 2), "only the new couch player moves");
            Check(Admit(bound, 1, primary, 2) && Admit(bound, 4, other, 2), "both continuing players keep their streams");
        });
        Run("movement: untagged batches require a complete legacy handshake", delegate {
            MovementEpochs legacy = Handshake("0.2.0-net15", new byte[0]);
            Check(Admit(legacy, 2, null, 1) && !Admit(legacy, 2, 7, 1), "old protocol is accepted only in old format");
            foreach (MovementEpochs unknown in new MovementEpochs[] {
                Handshake("0.2.0-net16", new byte[0]), Handshake("0.2.0-net16", new byte[31]),
                Handshake("unknown", new byte[0]) })
                Check(!Admit(unknown, 2, null, 1) && !Admit(unknown, 2, 7, 1), "missing or partial identity fails closed");
        });
        Run("movement: an unreliable Hello cannot introduce or replace a seat", delegate {
            Rig rig = new Rig(false, false, false); rig.Seat = 3;
            rig.Receive(2, 2, true, 0, Gameplay); rig.Receive(1, 2, false, 0, Hello);
            Check(!rig.Session.Remotes[2].Hello, "movement identity requires the ordered handshake");
            rig.Receive(1, 2, true, 0, Hello);
            Check(rig.Session.Remotes[2].Hello, "the usual reliable Hello introduces the seat");
        });
    }
}
