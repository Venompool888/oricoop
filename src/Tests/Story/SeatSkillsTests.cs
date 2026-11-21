using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Story;

// T-043 (#17): every seat rolls back to its own skill tree at the room save point (Story\SeatSkills.cs).
static partial class StoryTests
{
    static void SeatSkillCases()
    {
        // Abilities 0..6: tree A=1 (1 pt), B=3 (2 pt), C=5 (1 pt), D=6 (3 pt); 0, 2, 4 are story abilities.
        ulong tree = (1UL << 1) | (1UL << 3) | (1UL << 5) | (1UL << 6);
        int[] prices = { 0, 1, 0, 2, 0, 1, 3 };
        ulong a = 1UL << 1, b = 1UL << 3, c = 1UL << 5, d = 1UL << 6, story = (1UL << 0) | (1UL << 2);

        Check("seat skills: a wipe puts every seat back to its own checkpoint tree and points, whoever saved", delegate {
            int earned = 10;
            Dictionary<string, ulong> atCheckpoint = new Dictionary<string, ulong>();
            atCheckpoint["host"] = a; atCheckpoint["g2"] = c; atCheckpoint["g3"] = 0;
            SkillRecord record = new SkillRecord().Checkpoint(atCheckpoint);
            foreach (string saver in new[] { "host", "g2", "g3" })
            {
                ulong saverTree = atCheckpoint[saver];
                // The loaded checkpoint is the saver's save: story abilities, the saver's tree and the saver's points.
                ulong loaded = story | saverTree; int loadedPoints = SkillSplit.Points(earned, loaded, tree, prices);
                foreach (string seat in atCheckpoint.Keys)
                {
                    ulong recorded; Equal(true, record.TryGet(seat, out recorded));
                    ulong owned; int points;
                    SkillRestore.Restore(loadedPoints, loaded, recorded, tree, prices, out owned, out points);
                    Equal(story | atCheckpoint[seat], owned);
                    Equal(SkillSplit.Points(earned, atCheckpoint[seat], tree, prices), points);
                }
            }
        });
        Check("seat skills: purchases after the checkpoint are undone on every seat (no free skills)", delegate {
            int earned = 10;
            SkillRecord record = new SkillRecord(); record.Set("host", a); record.Set("g2", c);
            // After the checkpoint the host buys B and the guest D; then the wipe loads the host's save (host saver).
            ulong loaded = story | a; int loadedPoints = earned - 1;
            ulong owned; int points;
            SkillRestore.Restore(loadedPoints, loaded, a, tree, prices, out owned, out points);
            Equal(story | a, owned); Equal(9, points);
            ulong guestHold;
            Equal(true, SkillRestore.GuestHold(true, c, true, c | d, out guestHold));
            Equal(c, guestHold);                                         // not c|d: D was bought after the checkpoint
            ulong merged = SkillSplit.Merge(guestHold, SkillSplit.Shared(story | a, tree), tree);
            Equal(story | c, merged); Equal(9, SkillSplit.Points(earned, merged, tree, prices));
        });
        Check("seat skills: a seat absent at a checkpoint keeps its last recorded tree", delegate {
            SkillRecord first = new SkillRecord(); first.Set("host", a); first.Set("g2", c);
            Dictionary<string, ulong> present = new Dictionary<string, ulong>(); present["host"] = a | b;
            SkillRecord second = first.Checkpoint(present);
            ulong t;
            Equal(true, second.TryGet("host", out t)); Equal(a | b, t);
            Equal(true, second.TryGet("g2", out t)); Equal(c, t);
            Equal(true, first.TryGet("host", out t)); Equal(a, t);      // the old record is not changed
            Equal(false, second.TryGet("g3", out t)); Equal(false, second.TryGet(null, out t));
        });
        Check("seat skills: guest hold without a record (T-142 absent seat; T-047 join)", delegate {
            ulong hold;
            Equal(true, SkillRestore.GuestHold(false, 0, true, c | d, out hold)); Equal(c | d, hold);
            Equal(false, SkillRestore.GuestHold(false, 0, false, c | d, out hold));
            Equal(true, SkillRestore.GuestHold(true, c, false, a, out hold)); Equal(c, hold);   // a returning player joins
        });
        Check("seat skills: record round trip, same record same bytes, too many players rejected", delegate {
            SkillRecord r1 = new SkillRecord(); r1.Set("zeta", a | d); r1.Set("alpha", c);
            SkillRecord r2 = new SkillRecord(); r2.Set("alpha", c); r2.Set("zeta", a | d);
            Equal(Convert.ToBase64String(r1.ToBytes()), Convert.ToBase64String(r2.ToBytes()));
            SkillRecord back = SkillRecord.FromBytes(r1.ToBytes());
            ulong t; Equal(2, back.Count); Equal(true, back.TryGet("zeta", out t)); Equal(a | d, t);
            MemoryStream m = new MemoryStream(); BinaryWriter w = new BinaryWriter(m); w.Write((ushort)65); w.Flush();
            bool threw = false;
            try { SkillRecord.FromBytes(m.ToArray()); } catch (InvalidDataException) { threw = true; }
            Equal(true, threw);
        });
        Check("seat skills: the stripped save is byte-identical to what the game wrote; no trailer or a broken one is left alone", delegate {
            byte[] save = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
            SkillRecord record = new SkillRecord(); record.Set("host", a); record.Set("g2", c);
            byte[] up = SkillTrailer.Append(save, record);
            SkillRecord got; byte[] back = SkillTrailer.Split(up, out got);
            Equal(Convert.ToBase64String(save), Convert.ToBase64String(back));
            ulong t; Equal(true, got != null && got.TryGet("g2", out t) && t == c);
            Equal(true, ReferenceEquals(save, SkillTrailer.Split(save, out got))); Equal(true, got == null);
            string problem;
            byte[] badLength = (byte[])up.Clone(); badLength[badLength.Length - 13] = 0x7F;   // length's high byte
            Equal(badLength.Length, SkillTrailer.Split(badLength, out got, out problem).Length);
            Equal(true, got == null && problem != null && problem.Contains("length"));
            byte[] badVersion = (byte[])up.Clone(); badVersion[badVersion.Length - 12] = 9;
            Equal(badVersion.Length, SkillTrailer.Split(badVersion, out got, out problem).Length);
            Equal(true, got == null && problem != null && problem.Contains("version"));
            byte[] badBody = (byte[])up.Clone(); badBody[save.Length] = 0xFF; badBody[save.Length + 1] = 0xFF;   // 65535 players
            Equal(badBody.Length, SkillTrailer.Split(badBody, out got, out problem).Length);
            Equal(true, got == null && problem != null && problem.Contains("unreadable"));
            SkillTrailer.Split(save, out got, out problem); Equal(true, got == null && problem == null);
            Equal(true, ReferenceEquals(save, SkillTrailer.Append(save, null)));
        });
    }
}
