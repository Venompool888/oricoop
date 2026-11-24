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
        Check("entering a waiting gate locks only that player at the entry position", delegate {
            GateWait<float> first = new GateWait<float>(), second = new GateWait<float>();
            Equal(true, first.TryEnter("gate", 12f, 11.5f, 10f));
            Equal("gate", first.Key); Equal(12f, first.Anchor);
            Equal(null, second.Key);
            Equal(false, first.TryEnter("other", 13f, 12f, 10.1f));
        });
        Check("leaving a gate restores the pre-entry position and suppresses re-entry for half a second", delegate {
            GateWait<float> wait = new GateWait<float>();
            wait.TryEnter("gate", 12f, 11.5f, 10f);
            float? target;
            Equal(true, wait.TryLeave(11f, out target));
            Equal((float?)11.5f, target); Equal(null, wait.Key);
            Equal(false, wait.TryEnter("gate", 12f, target, 11.49f));
            Equal(true, wait.TryEnter("gate", 12f, target, 11.5f));
        });
        Check("leaving after the return spot was invalidated suppresses presence until outside", delegate {
            GateWait<float> wait = new GateWait<float>();
            wait.TryEnter("gate", 12f, 11.5f, 10f);
            wait.ClearBeforeEntry();
            float? target;
            Equal(true, wait.TryLeave(11f, out target));
            Equal(null, target); Equal(null, wait.Key);
            Equal(true, wait.CoolingDown(20f));
            Equal(false, wait.TryEnter("gate", 12f, null, 20f));
            wait.ExitedBounds();
            Equal(true, wait.TryEnter("gate", 12f, null, 20f));
        });
        Check("gate return samples expire and clear on a scene change", delegate {
            GatePositions<float> history = new GatePositions<float>();
            history.SetScene(1);
            history.Sample(11.5f, false, 10f);
            Equal((float?)11.5f, history.BeforeEntry(12f, p => p < 12f));
            history.SetScene(2);
            history.Sample(12.5f, true, 12.1f);
            Equal(null, history.BeforeEntry(12.2f, p => p < 12f));
            history.SetScene(1);
            Equal(null, history.BeforeEntry(12.3f, p => p < 12f));
            history.Sample(11.5f, true, 13f);
            Equal(null, history.BeforeEntry(16.1f, p => p < 12f));
        });
        Check("clearing positions for a teleport or respawn also discards grounded fallback", delegate {
            GatePositions<float> history = new GatePositions<float>();
            history.SetScene(1);
            history.Sample(11.5f, true, 10f);
            for (int i = 0; i < 16; i++) history.Sample(12f + i, false, 10.1f + i * 0.02f);
            Equal((float?)11.5f, history.BeforeEntry(11f, p => p < 12f));
            history.Clear(); history.SetScene(1);
            history.Sample(12f, true, 11.5f);
            Equal(null, history.BeforeEntry(11.5f, p => p < 12f));
        });
        Check("everyone present starts the gate and releases both locks", delegate {
            GateTable table = new GateTable();
            GateWait<float> first = new GateWait<float>(), second = new GateWait<float>();
            first.TryEnter("gate", 12f, 11.5f, 10f); table.SetInside("gate", 1, true, 10f);
            Equal(false, table.AllHere("gate", (byte)3, 10f));
            second.TryEnter("gate", 12f, 13f, 10.5f); table.SetInside("gate", 2, true, 10.5f);
            Equal(true, table.AllHere("gate", (byte)3, 10.5f));
            Equal(false, first.JoinStory(true)); Equal(false, second.JoinStory(true));
            Equal(null, first.Key); Equal(null, second.Key);
        });
        Check("joining a story releases overlapping pillar locks even without a pull", delegate {
            GateWait<float> first = new GateWait<float>(), second = new GateWait<float>();
            first.TryEnter("overlap", 12f, 11.5f, 10f);
            second.TryEnter("elsewhere", 42f, 43f, 10f);
            Equal(false, first.JoinStory(true));
            Equal(null, first.Key); Equal("elsewhere", second.Key);
            Equal(true, second.JoinStory(false));
            Equal(null, second.Key);
        });
        Check("dropping a gate releases its wait without releasing a different gate", delegate {
            GateWait<float> wait = new GateWait<float>();
            wait.TryEnter("gate", 12f, 11.5f, 10f);
            wait.Release("other"); Equal("gate", wait.Key);
            wait.Release("gate"); Equal(null, wait.Key);
        });
        Check("downed teammates are not required or counted even with a fresh presence", delegate {
            GateTable table = new GateTable();
            byte eligible = (byte)(5 | (GateSeats.CanUseGate(true, true, 4f, false) ? 2 : 0));
            table.SetInside("gate", 1, true, 10f); table.SetInside("gate", 2, true, 10f);
            Equal((byte)4, table.Missing("gate", eligible, 10f));
            Equal(1, GateSeats.Count(eligible) - GateSeats.Count(table.Missing("gate", eligible, 10f)));
            Equal(false, table.AllHere("gate", eligible, 10f));
            Equal(true, table.AllHere("gate", (byte)1, 10f));
            Equal(false, table.AllHere("gate", (byte)0, 10f));
        });
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
        Check("gate completed by its own trigger does not fizzle without a new story", delegate {
            GateTable t = new GateTable(); t.MarkFired("done", 10); t.MarkFired("other", 10);
            t.ConfirmFired("done");
            Equal(true, t.RecentlyFired("done", 13f));
            Equal(false, t.Fizzled("done", 13f, -1f)); Equal(true, t.Fizzled("other", 13f, -1f));
            t.ClearFired(); t.MarkFired("done", 20f);
            Equal(true, t.Fizzled("done", 23f, -1f));
        });
        Check("gate parent inactive: retry keeps the seats that gathered before the fire", delegate {
            GateTable t = new GateTable();
            t.SetInside("a", 1, true, 10f); t.SetInside("a", 2, true, 10f);
            t.MarkFired("a", 10.25f, true);
            Equal(true, t.RecentlyFired("a", 10.4f));
            t.Release("a");
            Equal(2, t.Count("a", 10.85f));
            Equal(0, t.Count("a", 11.51f));
        });
        Check("gate deactivated by the level: drop clears only that gate's count and pending fire", delegate {
            using (BinaryReader r = GatePacket(w => GateWire.WriteRelease(w, "deactivated", true)))
            {
                bool drop;
                string key = GateWire.ReadRelease(r, out drop);
                Equal(true, drop);
                GateTable t = new GateTable();
                t.SetInside(key, 1, true, 10f); t.SetInside("overlap", 1, true, 10f);
                t.MarkFired(key, 10f, true); t.SetQueued(key, true);
                t.Drop(key);
                Equal(0, t.Count(key, 10.5f));
                Equal(false, t.Queued(key)); Equal(false, t.RecentlyFired(key, 10.5f));
                Equal(1, t.Count("overlap", 10.5f));
            }
            using (BinaryReader r = GatePacket(w => GateWire.WriteRelease(w, "retry")))
            { bool drop; Equal("retry", GateWire.ReadRelease(r, out drop)); Equal(false, drop); }
        });
        Check("gate presence and prompts require a live, upright, non-spectating Ori", delegate {
            Equal(true, GateSeats.CanUseGate(true, false, 4f, false));
            Equal(false, GateSeats.CanUseGate(true, true, 4f, false));
            Equal(false, GateSeats.CanUseGate(true, false, 0f, false));
            Equal(false, GateSeats.CanUseGate(true, false, 4f, true));
            Equal(false, GateSeats.CanUseGate(false, false, 4f, false));
        });
        Check("gate missing seats preserve identity when one leaves and another arrives", delegate {
            GateTable t = new GateTable();
            t.SetInside("a", 2, true, 0f);
            Equal((byte)0x09, t.Missing("a", 0x0B, 0f)); // P1, P2, P4 in the room; only P2 present
            t.SetInside("a", 1, true, 0.5f); t.SetInside("a", 2, false, 0.5f);
            Equal((byte)0x0A, t.Missing("a", 0x0B, 0.5f));
            Equal((byte)0x0B, t.Missing("a", 0x0B, 2.1f));
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
        Check("gate count carries the missing-seat mask and still reads the original body", delegate {
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteCount(w, "a", 1, 3, 0xA); }))
            { byte n, total, missing; Equal("a", GateWire.ReadCount(r, out n, out total, out missing)); Equal((byte)1, n); Equal((byte)3, total); Equal((byte)0xA, missing); }
            using (BinaryReader r = GatePacket(delegate(BinaryWriter w) { GateWire.WriteCount(w, "a", 1, 2); }))
            { byte n, total, missing; Equal("a", GateWire.ReadCount(r, out n, out total, out missing)); Equal((byte)0, missing); }
            GateInvalid(delegate(BinaryWriter w) { GateWire.WriteCount(w, "a", 1, 3, 0x10); },
                delegate(BinaryReader r) { byte n, total, missing; GateWire.ReadCount(r, out n, out total, out missing); });
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
