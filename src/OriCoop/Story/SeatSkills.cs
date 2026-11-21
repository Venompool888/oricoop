using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Story
{
    // T-043 (owner, #17): the room save also keeps every seat's own skill tree. A wipe or checkpoint reload puts EVERY
    // seat back to its own tree at that save point; the points follow from the save's `earned` (Story\SkillSplit.cs), so
    // nobody keeps purchases made after the checkpoint, and host / guest / saver / non-saver behave the same.
    //
    // The record: player id (CoopSaves.PlayerId) -> that player's skill-tree bits. At each checkpoint the seats that are
    // in the game replace their entry; a seat that left (or joins later) keeps its last recorded entry.
    internal sealed class SkillRecord
    {
        public const int MaximumPlayers = 64;
        readonly Dictionary<string, ulong> m_trees = new Dictionary<string, ulong>();

        public int Count { get { return m_trees.Count; } }
        public bool TryGet(string player, out ulong tree)
        {
            tree = 0;
            return !string.IsNullOrEmpty(player) && m_trees.TryGetValue(player, out tree);
        }
        public void Set(string player, ulong tree) { if (!string.IsNullOrEmpty(player)) m_trees[player] = tree; }
        public IEnumerable<string> Players { get { return m_trees.Keys; } }

        // A new checkpoint: the seats present now (player -> live tree bits) replace their entries; the rest stay.
        public SkillRecord Checkpoint(IDictionary<string, ulong> present)
        {
            SkillRecord next = new SkillRecord();
            foreach (KeyValuePair<string, ulong> e in m_trees) next.Set(e.Key, e.Value);
            if (present != null) foreach (KeyValuePair<string, ulong> e in present) next.Set(e.Key, e.Value);
            return next;
        }

        public void Write(BinaryWriter w)
        {
            List<string> players = new List<string>(m_trees.Keys);
            players.Sort(StringComparer.Ordinal);                    // the same record is the same bytes
            w.Write((ushort)players.Count);
            foreach (string p in players) { w.Write(p); w.Write(m_trees[p]); }
        }

        public static SkillRecord Read(BinaryReader r)
        {
            int count = r.ReadUInt16();
            if (count > MaximumPlayers) throw new InvalidDataException("skill record: " + count + " players");
            SkillRecord record = new SkillRecord();
            for (int i = 0; i < count; i++) { string p = r.ReadString(); record.Set(p, r.ReadUInt64()); }
            return record;
        }

        public byte[] ToBytes()
        {
            using (MemoryStream m = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(m)) { Write(w); w.Flush(); return m.ToArray(); }
        }

        public static SkillRecord FromBytes(byte[] bytes)
        {
            if (bytes == null) return null;
            using (BinaryReader r = new BinaryReader(new MemoryStream(bytes))) return Read(r);
        }
    }

    // The room server keeps only the save bytes, so a server-mode upload carries the record as a trailer after the save:
    //   [save][record][int32 record length][int32 version][8-byte magic "OCSKILLS"]
    // Every way into the game strips it first (SeatSkillSync.ForLoad, the one helper). The stripped save is the exact
    // bytes the game wrote. No trailer: the save loads as before and seats keep their trees (an older room). A broken
    // trailer (magic present, rest unreadable) is reported and ignored.
    // The timeline hash (CoopSaves.Timeline) is SHA1 of the bare save, never of the trailer: a record changes only with
    // a new save write, and the record message names the save hash it belongs to (docs/save-model-design.md).
    internal static class SkillTrailer
    {
        public const int Version = 1;
        static readonly byte[] Magic = { (byte)'O', (byte)'C', (byte)'S', (byte)'K', (byte)'I', (byte)'L', (byte)'L', (byte)'S' };
        const int Footer = 4 + 4 + 8;

        public static byte[] Append(byte[] save, SkillRecord record)
        {
            if (save == null || save.Length == 0 || record == null) return save;
            byte[] body = record.ToBytes();
            byte[] all = new byte[save.Length + body.Length + Footer];
            Buffer.BlockCopy(save, 0, all, 0, save.Length);
            Buffer.BlockCopy(body, 0, all, save.Length, body.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, all, save.Length + body.Length, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(Version), 0, all, save.Length + body.Length + 4, 4);
            Buffer.BlockCopy(Magic, 0, all, all.Length - Magic.Length, Magic.Length);
            return all;
        }

        public static byte[] Split(byte[] data, out SkillRecord record) { string problem; return Split(data, out record, out problem); }

        // The save without the trailer. `record` is null when there is none or it is broken; `problem` says why it was
        // ignored (null = no trailer or a good one). A broken trailer leaves the data unchanged.
        public static byte[] Split(byte[] data, out SkillRecord record, out string problem)
        {
            record = null; problem = null;
            if (data == null || data.Length < Footer) return data;
            for (int i = 0; i < Magic.Length; i++) if (data[data.Length - Magic.Length + i] != Magic[i]) return data;
            int version = BitConverter.ToInt32(data, data.Length - Magic.Length - 4);
            int length = BitConverter.ToInt32(data, data.Length - Footer);
            if (version != Version) { problem = "skill trailer version " + version; return data; }
            if (length < 0 || length > data.Length - Footer - 1) { problem = "skill trailer length " + length; return data; }
            int start = data.Length - Footer - length;
            byte[] body = new byte[length];
            Buffer.BlockCopy(data, start, body, 0, length);
            try { record = SkillRecord.FromBytes(body); }
            catch (Exception e) { record = null; problem = "skill trailer unreadable: " + e.Message; return data; }
            byte[] save = new byte[start];
            Buffer.BlockCopy(data, 0, save, 0, start);
            return save;
        }
    }

    internal static class SkillRestore
    {
        // After a checkpoint load: the loaded save's own tree and points give `earned` (the same for every seat); the
        // seat's recorded tree replaces the loaded one (which is the saver's), and its points follow from `earned`.
        public static void Restore(int loadedPoints, ulong loadedOwned, ulong recorded, ulong tree, int[] cost, out ulong owned, out int points)
        {
            int earned = SkillSplit.Earned(loadedPoints, loadedOwned, tree, cost);
            owned = (loadedOwned & ~tree) | (recorded & tree);
            points = SkillSplit.Points(earned, owned, tree, cost);
        }

        // A guest about to load a host checkpoint: which skill-tree bits its next snapshot merge keeps.
        //   recorded at the checkpoint -> that tree (the rollback);
        //   not recorded, already in this session -> its live tree (T-142: a seat absent at every checkpoint so far);
        //   not recorded, joining -> nothing held: it starts from the host's save (T-047).
        public static bool GuestHold(bool recordedHere, ulong recordedTree, bool joined, ulong live, out ulong hold)
        {
            hold = recordedHere ? recordedTree : live;
            return recordedHere || joined;
        }
    }
}
