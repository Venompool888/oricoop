using System;
using System.IO;
using OriCoop.Story;

static partial class StoryTests
{
    static void AnimInvalidWrite(byte seat, byte op, string anim)
    {
        using (BinaryWriter w = new BinaryWriter(new MemoryStream()))
        {
            try { AnimWire.Write(w, seat, op, 0, anim); }
            catch (InvalidDataException) { return; }
            throw new Exception("expected InvalidDataException");
        }
    }
    static void AnimCases()
    {
        Check("anim wire round trip and 64 character boundary", delegate {
            foreach (byte operation in new byte[] { 0, 1 })
                foreach (string name in new string[] { "抬头", new string('a', 64) })
                    using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { AnimWire.Write(w, 2, operation, 1, name); }))
                    {
                        byte seat, op, flags; string anim;
                        Equal(true, AnimWire.TryRead(r, out seat, out op, out flags, out anim));
                        Equal((byte)2, seat); Equal(operation, op); Equal((byte)1, flags); Equal(name, anim);
                    }
        });
        Check("anim wire rejects invalid names on write and read", delegate {
            foreach (string name in new string[] { null, "", new string('a', 65) })
                AnimInvalidWrite(1, 0, name);
            foreach (string name in new string[] { "", new string('a', 65) })
                using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { w.Write((byte)1); w.Write((byte)0); w.Write((byte)0); w.Write(name); }))
                { byte seat, op, flags; string anim; Equal(false, AnimWire.TryRead(r, out seat, out op, out flags, out anim)); }
        });
        Check("anim wire rejects op 2 and seat 0", delegate {
            AnimInvalidWrite(1, 2, "look");
            AnimInvalidWrite(0, 0, "look");
            foreach (byte[] fields in new byte[][] { new byte[] { 1, 2, 0 }, new byte[] { 0, 0, 0 } })
                using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { w.Write(fields); w.Write("look"); }))
                { byte seat, op, flags; string anim; Equal(false, AnimWire.TryRead(r, out seat, out op, out flags, out anim)); }
        });
        Check("anim wire rejects truncated streams", delegate {
            byte[] packet;
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { AnimWire.Write(w, 1, 0, 1, "look"); }))
                packet = ((MemoryStream)r.BaseStream).ToArray();
            for (int length = 0; length < packet.Length; length++)
                using (BinaryReader r = new BinaryReader(new MemoryStream(packet, 0, length)))
                { byte seat, op, flags; string anim; Equal(false, AnimWire.TryRead(r, out seat, out op, out flags, out anim)); }
        });
        Check("anim follow stops only for the requesting seat", delegate {
            AnimFollow follow = new AnimFollow(); Equal(false, follow.Active); Equal(false, follow.Stop(1));
            follow.Start(2, "look", 10f);
            Equal(true, follow.Active); Equal(2, follow.Seat); Equal("look", follow.Anim);
            Equal(false, follow.Stop(1)); Equal(true, follow.Active);
            Equal(true, follow.Stop(2)); Equal(false, follow.Active);
            Equal(0, follow.Seat); Equal((string)null, follow.Anim); Equal(false, follow.Stop(2));
        });
        Check("anim follow timeout fires once after eight seconds", delegate {
            AnimFollow follow = new AnimFollow(); Equal(8f, AnimFollow.Timeout); Equal(false, follow.Expired(100f));
            follow.Start(2, "look", 10f); Equal(false, follow.Expired(18f));
            Equal(true, follow.Expired(18.1f)); Equal(false, follow.Active);
            Equal(0, follow.Seat); Equal((string)null, follow.Anim); Equal(false, follow.Expired(30f));
            follow.Start(3, "listen", 30f); Equal(false, follow.Expired(31f));
            Equal(3, follow.Seat); Equal("listen", follow.Anim); Equal(true, follow.Stop(3));
            Equal(false, follow.Expired(50f));
        });
    }
}
