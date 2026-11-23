using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Story;

static partial class StoryTests
{
    static BinaryReader GatePacket(Action<BinaryWriter> write)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream); write(writer); writer.Flush(); stream.Position = 0;
        return new BinaryReader(stream);
    }
    static void GateInvalid(Action<BinaryWriter> write, Action<BinaryReader> read)
    {
        using (BinaryReader reader = GatePacket(write))
        {
            try { read(reader); }
            catch (InvalidDataException) { return; }
            throw new Exception("expected InvalidDataException");
        }
    }
    static void GateCases()
    {
        Check("gate counts 0/1/2 seats", delegate {
            GateTable t = new GateTable(); Equal(0, t.Count("a", 0));
            t.SetInside("a", 1, true, 0); Equal(1, t.Count("a", 0));
            t.SetInside("a", 2, true, 0); Equal(2, t.Count("a", 0));
            t.SetInside("a", 2, true, 0); Equal(2, t.Count("a", 0));
            Equal(0, t.Count("b", 0));
        });
        Check("gate all-here boundaries", delegate {
            GateTable t = new GateTable(); Equal(false, t.AllHere("a", 0, 0));
            t.SetInside("a", 1, true, 0); Equal(false, t.AllHere("a", 2, 0));
            t.SetInside("a", 2, true, 0); Equal(true, t.AllHere("a", 2, 0));
            t.SetInside("a", 3, true, 0); Equal(false, t.AllHere("a", 4, 0));
            Equal(true, t.AllHere("a", 3, 0));
        });
        Check("gate heartbeat timeout and refresh", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 1, true, 0);
            Equal(1, t.Count("a", 1.4f)); Equal(1, t.Count("a", 1.5f)); Equal(0, t.Count("a", 1.6f));
            t.SetInside("a", 1, true, 1.6f); Equal(1, t.Count("a", 2));
        });
        Check("gate forget seat across keys", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 1, true, 0); t.SetInside("b", 1, true, 0);
            t.SetInside("a", 2, true, 0); t.Forget(1);
            Equal(1, t.Count("a", 0)); Equal(0, t.Count("b", 0));
        });
        Check("gate seat-specific presence for force authorization", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 2, true, 0);
            Equal(true, t.IsInside("a", 2, 1.4f)); Equal(false, t.IsInside("a", 1, 1.4f));
            Equal(false, t.IsInside("a", 2, 1.6f));
            t.SetInside("a", 2, false, 0.5f); Equal(false, t.IsInside("a", 2, 0.5f));
        });
        Check("gate leave removes presence", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 1, true, 0); t.SetInside("a", 1, false, 0.1f);
            Equal(0, t.Count("a", 0.1f));
        });
        Check("gate fired ignores presence for 15 seconds", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 1, true, 0); t.MarkFired("a", 0);
            Equal(true, t.RecentlyFired("a", 14.9f)); Equal(0, t.Count("a", 0));
            t.SetInside("a", 2, true, 14.9f); Equal(0, t.Count("a", 14.9f));
            Equal(false, t.RecentlyFired("a", 15f)); Equal(false, t.RecentlyFired("a", 15.1f));
            t.SetInside("a", 2, true, 15.1f); Equal(1, t.Count("a", 15.1f));
        });
        Check("gate seat order symmetry", delegate {
            GateTable a = new GateTable(), b = new GateTable();
            a.SetInside("g", 1, true, 0); a.SetInside("g", 2, true, 0);
            b.SetInside("g", 2, true, 0); b.SetInside("g", 1, true, 0);
            Equal(2, a.Count("g", 0)); Equal(a.Count("g", 0), b.Count("g", 0));
            Equal(true, a.AllHere("g", 2, 0)); Equal(a.AllHere("g", 2, 0), b.AllHere("g", 2, 0));
        });
        Check("gate active keys exclude expired and fired", delegate {
            GateTable t = new GateTable(); t.SetInside("old", 1, true, 0); t.SetInside("live", 2, true, 1);
            t.SetInside("fired", 1, true, 1); t.MarkFired("fired", 1);
            List<string> keys = new List<string>(t.ActiveKeys(1.6f)); Equal(1, keys.Count); Equal("live", keys[0]);
        });
        // T-196 (playtest 4, Sein's pickup): the gate fired at 0 and the native trigger refused it, so no story began.
        // Expected from the ticket: the area takes presses again after 3 s instead of swallowing them for 15 s.
        Check("gate fizzle: a fire no story followed is released after 3 s (boundary both sides)", delegate {
            GateTable t = new GateTable(); t.SetInside("a", 1, true, 10); t.MarkFired("a", 10);
            Equal(false, t.Fizzled("a", 12.9f, 4f)); Equal(true, t.Fizzled("a", 13f, 4f)); Equal(true, t.Fizzled("a", 13.1f, 4f));
            Equal(1, t.FizzledKeys(13f, 4f).Count); Equal("a", t.FizzledKeys(13f, 4f)[0]);
            t.Release("a"); Equal(false, t.RecentlyFired("a", 13f)); Equal(false, t.Fizzled("a", 13f, 4f));
            t.SetInside("a", 2, true, 13f); Equal(1, t.Count("a", 13f));
        });
        Check("gate fizzle: a fire whose story began (or ended) after it is not released", delegate {
            GateTable t = new GateTable(); t.MarkFired("a", 10);
            Equal(false, t.Fizzled("a", 13f, 10.6f)); Equal(false, t.Fizzled("a", 14.9f, 10f));
            Equal(true, t.Fizzled("a", 13f, 9.99f));   // the last story was before the fire
            Equal(0, t.FizzledKeys(13f, 10.6f).Count); Equal(true, t.RecentlyFired("a", 13f));
        });
        Check("gate fizzle: never a story (-1), unknown area, and an expired mark", delegate {
            GateTable t = new GateTable(); t.MarkFired("a", 10);
            Equal(true, t.Fizzled("a", 13f, -1f)); Equal(false, t.Fizzled("b", 13f, -1f));
            Equal(false, t.Fizzled("a", 25f, -1f)); Equal(false, t.RecentlyFired("a", 25f));
        });
        Check("gate hold fills once after 15 tenths", delegate {
            HoldMeter m = new HoldMeter(); Equal(1.5f, HoldMeter.FillSeconds);
            for (int i = 0; i < 14; i++) Equal(false, m.Update(true, 0.1f));
            Equal(true, m.Update(true, 0.1f)); Equal(1f, m.Progress);
            Equal(false, m.Update(true, 1)); Equal(false, m.Update(false, 1)); Equal(1f, m.Progress);
            m.Reset(); Equal(0f, m.Progress);
        });
        Check("gate release resets partial hold", delegate {
            HoldMeter m = new HoldMeter(); for (int i = 0; i < 10; i++) Equal(false, m.Update(true, 0.1f));
            Equal(false, m.Update(false, 0.1f)); Equal(0f, m.Progress);
            for (int i = 0; i < 14; i++) Equal(false, m.Update(true, 0.1f));
            Equal(true, m.Update(true, 0.1f)); Equal(1f, m.Progress);
        });
        Check("gate wire state round trip", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteState(w, "洞穴", 1, true); }))
            { bool inside; int seat; Equal("洞穴", GateWire.ReadState(r, out seat, out inside)); Equal(1, seat); Equal(true, inside); }
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteState(w, "a", 4, false); }))
            { bool inside; int seat; Equal("a", GateWire.ReadState(r, out seat, out inside)); Equal(4, seat); Equal(false, inside); }
        });
        // T-110: a guest with an extra local seat reports seat 3 as well as its own seat 2.
        Check("gate wire state carries the reported seat", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteState(w, "a", 3, true); }))
            { bool inside; int seat; GateWire.ReadState(r, out seat, out inside); Equal(3, seat); }
        });
        Check("gate wire rejects seat 0 and 5", delegate {
            foreach (byte seat in new byte[] { 0, 5 })
                GateInvalid(delegate(BinaryWriter w) { w.Write("a"); w.Write(seat); w.Write(true); },
                    delegate(BinaryReader r) { bool inside; int s; GateWire.ReadState(r, out s, out inside); });
        });
        Check("gate total counts every seat of every PC", delegate {
            Equal(2, GateSeats.Total(1, 1));   // host + guest
            Equal(3, GateSeats.Total(1, 2));   // host + guest + the guest's extra local player
            Equal(3, GateSeats.Total(2, 1));   // host + its extra local player + guest
            Equal(4, GateSeats.Total(2, 2));
            Equal(1, GateSeats.Total(0, 0));
        });
        Check("gate a PC reports only its own seats", delegate {
            Equal(true, GateSeats.MayReport(2, 2, 2));    // guest's primary
            Equal(true, GateSeats.MayReport(2, 3, 2));    // guest's extra local seat
            Equal(false, GateSeats.MayReport(2, 1, 1));   // not the host's seat
            Equal(false, GateSeats.MayReport(2, 4, 3));   // not another guest's seat
            Equal(false, GateSeats.MayReport(2, 0, 2)); Equal(false, GateSeats.MayReport(2, 5, 2));
        });
        Check("gate fills when every seat of two PCs is inside", delegate {
            GateTable t = new GateTable(); int total = GateSeats.Total(1, 2);
            t.SetInside("g", 1, true, 0); t.SetInside("g", 2, true, 0);
            Equal(false, t.AllHere("g", total, 0));   // the guest's extra player is still outside: 2/3
            t.SetInside("g", 3, true, 0);
            Equal(true, t.AllHere("g", total, 0));
        });
        Check("gate force: any seat of the forcing PC inside", delegate {
            GateTable t = new GateTable(); t.SetInside("g", 3, true, 0);
            Equal(true, t.AnyInside("g", new int[] { 2, 3 }, 0));
            Equal(false, t.AnyInside("g", new int[] { 2 }, 0));
            Equal(false, t.AnyInside("g", new int[] { 2, 3 }, 1.6f));
        });
        Check("gate wire count round trip", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteCount(w, "a", 2, 4); }))
            { byte n, total; Equal("a", GateWire.ReadCount(r, out n, out total)); Equal((byte)2, n); Equal((byte)4, total); }
        });
        Check("gate wire force round trip", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteForce(w, "a"); })) Equal("a", GateWire.ReadForce(r));
        });
        Check("gate wire fire round trip", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteFire(w, "a", -1.5f, 2.25f, 0.5f); }))
            { float x, y, z; Equal("a", GateWire.ReadFire(r, out x, out y, out z)); Equal(-1.5f, x); Equal(2.25f, y); Equal(0.5f, z); }
        });
        Check("gate wire rejects count 9", delegate {
            GateInvalid(delegate(BinaryWriter w) { w.Write("a"); w.Write((byte)9); w.Write((byte)2); },
                delegate(BinaryReader r) { byte n, total; GateWire.ReadCount(r, out n, out total); });
        });
        Check("gate wire rejects total 0 and 9", delegate {
            foreach (byte total in new byte[] { 0, 9 })
                GateInvalid(delegate(BinaryWriter w) { w.Write("a"); w.Write((byte)0); w.Write(total); },
                    delegate(BinaryReader r) { byte n, size; GateWire.ReadCount(r, out n, out size); });
        });
        Check("gate wire rejects nonfinite coordinates", delegate {
            foreach (float invalid in new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                for (int axis = 0; axis < 3; axis++)
                    GateInvalid(delegate(BinaryWriter w) { w.Write("a"); for (int i = 0; i < 3; i++) w.Write(i == axis ? invalid : 0f); },
                        delegate(BinaryReader r) { float x, y, z; GateWire.ReadFire(r, out x, out y, out z); });
        });
        Check("gate wire rejects long keys in every message", delegate {
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteState(w, new string('a', 1025), 1, true); },
                delegate(BinaryReader r) { bool inside; int seat; GateWire.ReadState(r, out seat, out inside); });
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteCount(w, new string('a', 1025), 1, 2); },
                delegate(BinaryReader r) { byte n, total; GateWire.ReadCount(r, out n, out total); });
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteForce(w, new string('a', 1025)); },
                delegate(BinaryReader r) { GateWire.ReadForce(r); });
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteFire(w, new string('a', 1025), 0, 0, 0); },
                delegate(BinaryReader r) { float x, y, z; GateWire.ReadFire(r, out x, out y, out z); });
        });
        Check("gate wire accepts boundary key and counts", delegate {
            string key = new string('a', 1024);
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteCount(w, key, 8, 8); }))
            { byte n, total; Equal(key, GateWire.ReadCount(r, out n, out total)); Equal((byte)8, n); Equal((byte)8, total); }
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteCount(w, "a", 0, 1); }))
            { byte n, total; Equal("a", GateWire.ReadCount(r, out n, out total)); Equal((byte)0, n); Equal((byte)1, total); }
        });
    }
}
