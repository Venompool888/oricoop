using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Story;

namespace OriCoop.Saves
{
    // T-043: the per-seat skill record (Story\SeatSkills.cs) travels with each room checkpoint. A record belongs to one
    // save: it carries that save's hash (the timeline hash, SHA1 of the bare slot file) on the wire and in the sidecar,
    // and is ignored next to any other save.
    internal static class SeatSkillSync
    {
        const byte MsgSeatSkills = 0x58, ReportKind = 0, RecordKind = 1;
        static readonly Dictionary<int, KeyValuePair<string, ulong>> s_present = new Dictionary<int, KeyValuePair<string, ulong>>();
        static int s_reportedHost;
        static ulong s_reportedBits;
        static string s_checkpointHash;      // the save the host's Current belongs to
        static int s_hostSlot = -1;
        public static SkillRecord Current;
        static string s_guestHash;           // guest: the save its Current belongs to

        // The record for the room's current save: a guest's last record counts only while no newer commit came in.
        internal static SkillRecord ForRoomSave()
        {
            if (Current == null) return null;
            return Services.Net.IsHost || s_guestHash == CoopSaves.Timeline ? Current : null;
        }
        public static int RecordsReceived, RecordsSent, Restores;
        internal static bool HostRestorePending;

        public static void Init()
        {
            Services.Net.On(MsgSeatSkills, Receive);
            Services.Net.PlayerLeft += delegate(int seat) { s_present.Remove(seat); };
            Services.Net.SessionEnded += Reset;
        }

        static void Reset()
        {
            s_present.Clear(); s_reportedHost = 0; HostRestorePending = false;
            Current = null; s_checkpointHash = null; s_guestHash = null; Captures = 0; s_hostSlot = -1;
            RecordsReceived = RecordsSent = Restores = 0;
        }

        internal static void Loaded() { s_reportedHost = 0; }

        internal static void RebindDevice(int previous, int primary)
        {
            KeyValuePair<string, ulong> entry;
            if (!s_present.TryGetValue(previous, out entry)) return;
            s_present.Remove(previous);
            s_present[primary] = entry;
        }

        internal static void Report()
        {
            if (!Services.Net.Connected || Services.Net.IsHost || !SaveSync.Ready || !ProgressSync.Ready
                || ProgressSync.SkillTreeMask == 0 || GameController.Instance.IsLoadingGame) return;
            ulong bits = ProgressSync.OwnTreeBits;
            int host = Services.Net.HostSeat;
            if (s_reportedHost == host && s_reportedBits == bits) return;
            Services.Net.Send(MsgSeatSkills, true, delegate(BinaryWriter w) {
                w.Write(ReportKind); w.Write(CoopSaves.PlayerId); w.Write(bits);
            });
            s_reportedHost = host; s_reportedBits = bits;
        }

        static void Receive(BinaryReader r)
        {
            byte kind = r.ReadByte();
            if (kind == ReportKind && Services.Net.IsHost && ProgressSync.SkillTreeMask != 0)
            {
                string player = r.ReadString();
                ulong bits = r.ReadUInt64() & ProgressSync.SkillTreeMask;
                s_present[Services.Net.Sender] = new KeyValuePair<string, ulong>(player, bits);
            }
            else if (kind == RecordKind && !Services.Net.IsHost && Services.Net.Sender == Services.Net.HostSeat)
            {
                string hash = r.ReadString();
                SkillRecord record = SkillRecord.Read(r);
                if (hash.Length == 0) { SaveSync.ExpectCheckpointSkills(record); RecordsReceived++; return; }
                if (hash != CoopSaves.Timeline)
                {
                    Services.Log.Warn("net: skill record for save " + Short(hash) + " ignored (room save is " + Short(CoopSaves.Timeline) + ")");
                    return;
                }
                Current = record; s_guestHash = hash;
                RecordsReceived++;
                if (CoopSaves.SessionSlot >= 0) WriteSidecar(CoopSaves.SessionSlot, hash, Current);
            }
        }

        // Right after a lineage commit of the save `hash`: its record, if the host has one for exactly that save.
        internal static void SendRecord(int seat, string hash)
        {
            SkillRecord record = Current;
            if (record == null || string.IsNullOrEmpty(hash) || hash != s_checkpointHash) return;
            Action<BinaryWriter> body = delegate(BinaryWriter w) { w.Write(RecordKind); w.Write(hash); record.Write(w); };
            if (seat < 0) Services.Net.Send(MsgSeatSkills, true, body);
            else Services.Net.SendTo(seat, MsgSeatSkills, true, body);
            RecordsSent++;
        }

        // Host: the game made a new checkpoint (save point, purchase, auto checkpoint) or took a guest's save as the room
        // checkpoint. A wipe reloads exactly this checkpoint, so the trees are recorded now: present seats' latest reports
        // and the host's own tree; absent seats keep theirs. Not while a checkpoint is being restored (the trees then are
        // the ones about to be rolled back).
        internal static void Capture(string why)
        {
            if (!Services.Net.Enabled || !Services.Net.Connected || !Services.Net.IsHost || !ProgressSync.Ready
                || ProgressSync.SkillTreeMask == 0 || HostRestorePending || GameController.Instance.IsLoadingGame) return;
            Dictionary<string, ulong> present = new Dictionary<string, ulong>();
            foreach (KeyValuePair<string, ulong> entry in s_present.Values) present[entry.Key] = entry.Value;
            present[CoopSaves.PlayerId] = ProgressSync.OwnTreeBits;
            Current = (Current ?? new SkillRecord()).Checkpoint(present);
            s_checkpointHash = null;   // not written to a file yet
            Captures++;
            Services.Log.Info("net: skill record captured at " + why + " (" + Current.Count + " players)");
        }
        public static int Captures;

        [HarmonyPatch(typeof(GameController), "CreateCheckpoint")]
        static class CheckpointPatch
        {
            static void Postfix() { Capture("checkpoint"); }
        }

        // Host: the room slot was written (hash = SHA1 of the file). The record of the current checkpoint now belongs to
        // that file: a save written without a new checkpoint (e.g. the one the game writes while reloading) only moves it.
        internal static SkillRecord ForSave(int slot, string hash)
        {
            s_hostSlot = slot;
            if (Current == null || string.IsNullOrEmpty(hash) || hash == s_checkpointHash) return Current;
            s_checkpointHash = hash;
            WriteSidecar(slot, hash, Current);
            return Current;
        }

        // Host, right before a checkpoint transfer (wipe reload or join): the record of that checkpoint, which the guest
        // holds its own tree from for that one load.
        internal static void SendPushRecord(int seat)
        {
            SkillRecord record = Current;
            if (record == null) return;
            Services.Net.SendTo(seat, MsgSeatSkills, true, delegate(BinaryWriter w) { w.Write(RecordKind); w.Write(""); record.Write(w); });
            RecordsSent++;
        }

        // The one way bytes from outside become a save the game loads (room enter, checkpoint transfer, LAN commit copy,
        // a guest's save offer, partner progress, single-player copy-in): strip a skill trailer. No trailer = the bytes
        // as they are; a broken one is logged and ignored.
        internal static byte[] ForLoad(byte[] data, string where, out SkillRecord record)
        {
            string problem;
            byte[] save = SkillTrailer.Split(data, out record, out problem);
            if (problem != null) Services.Log.Warn("saves: " + where + ": " + problem + "; loading the save without a skill record");
            return save;
        }
        internal static byte[] ForLoad(byte[] data, string where) { SkillRecord unused; return ForLoad(data, where, out unused); }

        static string Short(string hash) { return string.IsNullOrEmpty(hash) ? "-" : hash.Substring(0, Math.Min(8, hash.Length)); }

        static string Sidecar(int slot) { return Path.Combine(CoopSaves.Folder, "saveFile" + slot + ".skills"); }

        // Sidecar "saveFileN.skills": string save hash + record. `hash` null = any save (an upload of the current state).
        internal static SkillRecord ReadSidecar(int slot, string hash = null)
        {
            string path = Sidecar(slot);
            if (!File.Exists(path)) return null;
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(File.ReadAllBytes(path))))
                {
                    string saved = r.ReadString();
                    SkillRecord record = SkillRecord.Read(r, true);
                    if (hash != null && saved != hash) { Services.Log.Info("saves: skill record of slot " + slot + " is for another save; not used"); return null; }
                    return record;
                }
            }
            catch (Exception e) { Services.Log.Warn("saves: skill record of slot " + slot + " unreadable: " + e.Message); return null; }
        }

        // The host's room slot is known (lineage settled, or the partner's progress adopted): its record, if it belongs
        // to that exact save. None: seats keep their trees (an older room, or the partner's save).
        internal static void SettleHostSlot(int slot, string hash)
        {
            s_hostSlot = slot;
            Current = ReadSidecar(slot, hash ?? "");
            s_checkpointHash = Current == null ? null : hash;
            HostRestorePending = Current != null;
        }

        internal static void WriteSidecar(int slot, string hash, SkillRecord record)
        {
            string path = Sidecar(slot);
            if (record == null || string.IsNullOrEmpty(hash)) { if (File.Exists(path)) File.Delete(path); return; }
            using (MemoryStream m = new MemoryStream()) using (BinaryWriter w = new BinaryWriter(m))
            {
                // Temp file + replace: a crash mid-write left a cut sidecar, and the save lost its skill record.
                w.Write(hash); record.Write(w); w.Flush();
                File.WriteAllBytes(path + ".tmp", m.ToArray());
                CoopSaveFolder.ReplaceWith(path + ".tmp", path);
            }
        }
    }
}
