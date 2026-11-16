using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Co-op save lineage (design 2026-11-15): both players keep the shared progress, and the two copies know they
    // come from the same co-op save.
    //
    // - Sidecar "saveFileN.oricoop" (text) next to the game's own slot file: co-op ID (GUID), the save's own
    //   Identifier (SaveSlotInfo, stable for a playthrough), partner name/ID, and the SHA1 of the slot file as it
    //   was at the last co-op sync. A sidecar whose save Identifier no longer matches the slot file (slot deleted
    //   or a new game started there) is ignored.
    // - Session start (host in gameplay, before the checkpoint goes out): host offers its co-op ID + fingerprint,
    //   the guest answers with its matching slot (if any). Identical -> play. Diverged -> the HOST chooses:
    //   host progress / partner progress / cancel. No lineage -> host save wins (today's behaviour), said in a toast.
    // - The guest writes the host's slot FILE bytes into its own slot (the linked one, else the first empty one;
    //   never an unrelated non-empty slot) at the start and after every host save. Both files are then byte-identical,
    //   so "changed since the last sync" = file hash != the sidecar's synced hash.
    // - Any slot we overwrite is copied to OriCoop-backups first (last 5 per slot kept).
    public static class CoopSaves
    {
        const byte MsgOffer = 16, MsgReply = 17, MsgCommit = 18, MsgRequest = 19, MsgUpload = 23;
        const byte ReqUpload = 1, ReqCancel = 2, ReqChoosing = 3;
        const int KeepBackups = 5, SlotCount = 10;
        const float ReplyTimeout = 10f, UploadTimeout = 15f;

        enum Stage { Idle, Offered, Choosing, Uploading, Adopting, Proceed, Cancelling, Done }
        static Stage s_stage;
        static float s_stageAt;

        // Host side
        static CoopLink s_hostLink;
        static int s_hostSlot = -1;
        static string s_hostHash = "", s_hostSummary = "";
        static Reply s_reply;
        static int s_focus;
        static bool s_partnerChanged, s_hostChanged;

        // Guest side
        static string s_offerCoop = "", s_offerHostId = "", s_offerHostName = "";
        static int s_guestMatch = -1;
        static string s_lastWrittenHash;
        static bool s_noSlotToldOnce, s_savedToldOnce;

        // Test / diagnostics
        public static string Outcome = "";          // new | same | diverged-host | diverged-partner | cancel | legacy
        public static bool DivergenceSeen, CancelledByHost;
        public static int GuestSlot = -1, GuestWrites, BackupsMade, Commits, AdoptedHours = -1;
        public static string CoopId = "", LastCommitHash = "";
        public static string TestChoice = Environment.GetEnvironmentVariable("ORICOOP_COOPSAVE_CHOICE") ?? "";
        public static bool TestReady;   // set by CoopSaveTest once the host's harness runs (a cancel must not beat it)
        // Guest still on the title, joining (GuestJoin.cs): no slot is being played, so the current index is not
        // excluded; GuestPickedSlot (an empty slot the player picked, -1 none) is preferred after a linked slot.
        public static bool GuestJoiningEmpty;
        public static int GuestPickedSlot = -1;
        public static int SessionSlot = -1;   // slot that holds the host's copy in THIS session (-1 none yet)

        class Reply
        {
            public bool Found, FreeSlot;
            public int Slot;
            public string Hash = "", LastSync = "", Summary = "", PlayerId = "", Name = "";
        }

        public static void Init()
        {
            NetPlay.On(MsgOffer, ReceiveOffer);
            NetPlay.On(MsgReply, ReceiveReply);
            NetPlay.On(MsgCommit, ReceiveCommit);
            NetPlay.On(MsgRequest, ReceiveRequest);
            NetPlay.On(MsgUpload, ReceiveUpload);
            NetPlay.SessionEnded += Reset;
        }

        static void Reset()
        {
            s_stage = Stage.Idle; s_reply = null; s_hostLink = null; s_hostSlot = -1;
            s_offerCoop = s_offerHostId = s_offerHostName = ""; s_guestMatch = -1;
            s_lastWrittenHash = null; s_noSlotToldOnce = s_savedToldOnce = false;
            GuestJoiningEmpty = false; GuestPickedSlot = -1; SessionSlot = -1; NoSlotForCopy = false;
            s_commitBytes = null; s_commitCoop = s_commitHostId = s_commitHostName = s_commitSaveId = null;
        }

        static void SetStage(Stage s) { s_stage = s; s_stageAt = Time.realtimeSinceStartup; }

        // ---------------------------------------------------------------- files

        public static string Folder { get { return OutputFolder.PlayerDataFolderPath; } }
        static string SavePath(int slot) { return GameController.Instance.SaveGameController.GetSaveFilePath(slot); }
        static string LinkPath(int slot) { return Path.Combine(Folder, "saveFile" + slot + ".oricoop"); }

        public static string Hash(byte[] bytes)
        {
            if (bytes == null) return "";
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(40);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static byte[] ReadSlot(int slot)
        {
            string p = SavePath(slot);
            if (!File.Exists(p)) return null;
            using (FileStream f = File.Open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] b = new byte[f.Length]; int read = 0;
                while (read < b.Length) { int n = f.Read(b, read, b.Length - read); if (n <= 0) break; read += n; }
                return b;
            }
        }

        public sealed class SaveHeader
        {
            public string Area = "", Id = "";
            public int Completion, Hours, Minutes, Seconds;
        }

        // Own parser of the SaveSlotInfo header (decomp SaveSlotInfo.SaveToWriter): SaveSlotInfo.LoadFromReader
        // would also set the static CurrentInfoVersion the game uses while loading.
        public static SaveHeader ReadHeader(byte[] bytes)
        {
            if (bytes == null) return null;
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
                {
                    if (r.ReadString() != "OriSave") return null;
                    int version = r.ReadInt32(); if (version < 10) return null;
                    SaveHeader h = new SaveHeader();
                    h.Area = r.ReadString(); h.Completion = r.ReadInt32();
                    r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();
                    h.Hours = r.ReadInt32(); h.Minutes = r.ReadInt32(); h.Seconds = r.ReadInt32();
                    r.ReadInt32(); r.ReadBoolean();
                    byte[] id = r.ReadBytes(16);
                    StringBuilder sb = new StringBuilder(32);
                    foreach (byte b in id) sb.Append(b.ToString("x2"));
                    h.Id = sb.ToString();
                    return h;
                }
            }
            catch (Exception) { return null; }
        }

        static string AreaTitle(string area)
        {
            try
            {
                if (SaveSlotsScreenshotManager.Instance != null)
                {
                    object n = SaveSlotsScreenshotManager.Instance.FindAreaName(area);
                    if (n != null && n.ToString().Length > 0) return n.ToString();
                }
            }
            catch (Exception) { }
            return area;
        }

        static string Summary(byte[] bytes)
        {
            SaveHeader h = ReadHeader(bytes);
            if (h == null) return "?";
            return L10n.F("saves.summary", AreaTitle(h.Area), h.Completion, h.Hours, h.Minutes, h.Seconds);
        }

        public sealed class CoopLink
        {
            public string Coop = "", SaveId = "", Partner = "", PartnerId = "", Synced = "", SyncedAt = "";

            public static CoopLink Read(int slot)
            {
                try
                {
                    string p = LinkPath(slot);
                    if (!File.Exists(p)) return null;
                    CoopLink l = new CoopLink();
                    foreach (string line in File.ReadAllLines(p, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('='); if (eq <= 0 || line.StartsWith("#")) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        if (k == "coop") l.Coop = v; else if (k == "saveId") l.SaveId = v;
                        else if (k == "partner") l.Partner = v; else if (k == "partnerId") l.PartnerId = v;
                        else if (k == "synced") l.Synced = v; else if (k == "syncedAt") l.SyncedAt = v;
                    }
                    return l.Coop.Length > 0 ? l : null;
                }
                catch (Exception e) { Plugin.Log.LogWarning("coop save: reading link " + slot + ": " + e.Message); return null; }
            }

            public void Write(int slot)
            {
                string text = "# OriCoop co-op save link: this slot shares progress with a co-op partner.\n"
                    + "coop=" + Coop + "\nsaveId=" + SaveId + "\npartner=" + Partner + "\npartnerId=" + PartnerId
                    + "\nsynced=" + Synced + "\nsyncedAt=" + SyncedAt + "\n";
                string p = LinkPath(slot), tmp = p + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
        }

        // A link only counts while the slot file is the same playthrough (same save Identifier).
        public static CoopLink ValidLink(int slot, byte[] bytes)
        {
            CoopLink l = CoopLink.Read(slot);
            if (l == null) return null;
            SaveHeader h = ReadHeader(bytes ?? ReadSlot(slot));
            if (h == null || (l.SaveId.Length > 0 && h.Id != l.SaveId)) return null;
            return l;
        }

        static void Backup(int slot)
        {
            string src = SavePath(slot);
            if (!File.Exists(src)) return;
            string dir = Path.Combine(Folder, "OriCoop-backups");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string dst = Path.Combine(dir, "saveFile" + slot + "-" + stamp + ".sav");
            File.Copy(src, dst, true);
            if (File.Exists(LinkPath(slot))) File.Copy(LinkPath(slot), Path.ChangeExtension(dst, ".oricoop"), true);
            BackupsMade++;
            Plugin.Log.LogInfo("coop save: backed up slot " + slot + " -> " + dst);
            string[] old = Directory.GetFiles(dir, "saveFile" + slot + "-*.sav");
            Array.Sort(old, StringComparer.Ordinal);
            for (int i = 0; i < old.Length - KeepBackups; i++)
            {
                try
                {
                    File.Delete(old[i]);
                    string side = Path.ChangeExtension(old[i], ".oricoop");
                    if (File.Exists(side)) File.Delete(side);
                }
                catch (Exception e) { Plugin.Log.LogWarning("coop save: pruning backup: " + e.Message); }
            }
        }

        static void WriteSlotFile(int slot, byte[] bytes)
        {
            string p = SavePath(slot), tmp = p + ".oricoop-tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
            RefreshSlotInfo(slot, bytes);
        }

        // Keep the title screen's slot list in step with the file.
        static void RefreshSlotInfo(int slot, byte[] bytes)
        {
            try
            {
                if (SaveSlotsManager.Instance != null && slot < SaveSlotsManager.Instance.SaveSlots.Count
                    && (slot != SaveSlotsManager.CurrentSlotIndex || GuestJoiningEmpty))   // still on the title: nothing plays it
                {
                    SaveSlotInfo info = new SaveSlotInfo();
                    int saved = SaveSlotInfo.CurrentInfoVersion;
                    using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
                        SaveSlotsManager.Instance.SaveSlots[slot] = info.LoadFromReader(r) ? info : null;
                    SaveSlotInfo.CurrentInfoVersion = saved;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("coop save: refreshing slot info: " + e.Message); }
        }

        // ---------------------------------------------------------------- identity

        static string s_playerId;
        public static string PlayerId
        {
            get
            {
                if (s_playerId != null) return s_playerId;
                string p = Path.Combine(Folder, "oricoop-player.txt");
                try
                {
                    if (File.Exists(p)) foreach (string line in File.ReadAllLines(p))
                            if (line.StartsWith("id=")) s_playerId = line.Substring(3).Trim();
                    if (string.IsNullOrEmpty(s_playerId))
                    {
                        s_playerId = Guid.NewGuid().ToString("N");
                        File.WriteAllText(p, "# OriCoop player id (identifies this PC's player in co-op save links)\nid=" + s_playerId + "\n");
                    }
                }
                catch (Exception) { if (string.IsNullOrEmpty(s_playerId)) s_playerId = Guid.NewGuid().ToString("N"); }
                return s_playerId;
            }
        }

        public static string LocalName
        {
            get
            {
                string n = Environment.GetEnvironmentVariable("ORICOOP_PLAYER_NAME");
                if (string.IsNullOrEmpty(n)) n = "Ori-" + PlayerId.Substring(0, 4);   // never the Windows account name: names go to the partner
                return Clean(n);
            }
        }

        // Names go into the game's MessageBox markup (* [ ] # @ are markup) and into a one-line text file.
        public static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '*' || c == '[' || c == ']' || c == '#' || c == '@' || c == '<' || c == '>' || c == '=' || char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= 16) break;
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "?" : r;
        }

        static string PartnerName(string sent)
        {
            string steam = NetPlay.UsesSteam ? NetPlay.SteamPartnerName : "";
            return Clean(string.IsNullOrEmpty(steam) ? sent : steam);
        }

        // ---------------------------------------------------------------- host

        // SaveSync asks before the first checkpoint: true once the save to play is settled.
        public static bool HostGate()
        {
            switch (s_stage)
            {
                case Stage.Idle: HostOffer(); return false;
                case Stage.Proceed: case Stage.Done: return true;
                case Stage.Offered:
                    if (Time.realtimeSinceStartup - s_stageAt > ReplyTimeout)
                    {
                        Plugin.Log.LogWarning("coop save: no lineage reply (older partner mod?); host save wins");
                        Outcome = "legacy";
                        NetHud.Toast(
                    L10n.T("saves.check_timeout"), "");
                        SetStage(Stage.Proceed);
                        return true;
                    }
                    return false;
                case Stage.Uploading:
                    if (Time.realtimeSinceStartup - s_stageAt > UploadTimeout)
                    {
                        Plugin.Log.LogWarning("coop save: partner progress never arrived; host save wins");
                        NetHud.Toast(
                    L10n.T("saves.upload_timeout"), "");
                        Decide(false);
                    }
                    return false;
                default: return false;
            }
        }

        static void HostOffer()
        {
            try
            {
                s_hostSlot = SaveSlotsManager.CurrentSlotIndex;
                byte[] bytes = ReadSlot(s_hostSlot);
                s_hostHash = Hash(bytes);
                s_hostSummary = bytes == null ? L10n.T("saves.new_game") : Summary(bytes);
                s_hostLink = bytes == null ? null : ValidLink(s_hostSlot, bytes);
                if (s_hostLink == null)
                {
                    s_hostLink = new CoopLink();
                    s_hostLink.Coop = Guid.NewGuid().ToString("N");
                    SaveHeader h = ReadHeader(bytes);
                    s_hostLink.SaveId = h != null ? h.Id : "";
                    s_hostChanged = false;
                }
                else s_hostChanged = s_hostLink.Synced.Length > 0 && s_hostLink.Synced != s_hostHash;
                CoopId = s_hostLink.Coop;
                string coop = s_hostLink.Coop;
                NetPlay.Send(MsgOffer, true, delegate(BinaryWriter w)
                {
                    w.Write((byte)1); w.Write(coop); w.Write(PlayerId); w.Write(LocalName); w.Write(s_hostHash); w.Write(s_hostSummary);
                });
                SetStage(Stage.Offered);
                Plugin.Log.LogInfo("coop save: offered lineage " + coop + " slot " + s_hostSlot + " hash " + Short(s_hostHash) + " changedSinceSync=" + s_hostChanged);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("coop save: offer failed, host save wins: " + e);
                SetStage(Stage.Proceed);
            }
        }

        static void ReceiveReply(BinaryReader r)
        {
            if (!NetPlay.IsHost || s_stage != Stage.Offered) return;
            Reply rep = new Reply();
            rep.Found = r.ReadBoolean(); rep.Slot = r.ReadInt32(); rep.Hash = r.ReadString(); rep.LastSync = r.ReadString();
            rep.Summary = r.ReadString(); rep.PlayerId = r.ReadString(); rep.Name = r.ReadString(); rep.FreeSlot = r.ReadBoolean();
            s_reply = rep;
            s_hostLink.Partner = PartnerName(rep.Name); s_hostLink.PartnerId = rep.PlayerId;
            Plugin.Log.LogInfo("coop save: partner " + s_hostLink.Partner + " found=" + rep.Found + " slot=" + rep.Slot + " hash=" + Short(rep.Hash) + " host=" + Short(s_hostHash));
            if (!rep.Found)
            {
                Outcome = "new";
                NetHud.Toast(
                    L10n.T("saves.new"), "");
                if (!rep.FreeSlot) NetHud.Toast(
                    L10n.T("saves.partner_no_slot"), "");
                Decide(false);
            }
            else if (rep.Hash == s_hostHash)
            {
                Outcome = "same";
                NetHud.Toast(
                    L10n.T("saves.match"), "");
                Decide(false);
            }
            else
            {
                DivergenceSeen = true;
                s_partnerChanged = rep.LastSync.Length == 0 || rep.Hash != rep.LastSync;
                s_focus = 0;
                SetStage(Stage.Choosing);
                NetPlay.Send(MsgRequest, true, delegate(BinaryWriter w) { w.Write(ReqChoosing); });
                Plugin.Log.LogInfo("coop save: lineage diverged (hostChanged=" + s_hostChanged + " partnerChanged=" + s_partnerChanged + "); asking the host");
            }
        }

        // Host's choice: false = host progress, true = partner progress.
        static void Decide(bool partner)
        {
            if (partner)
            {
                SetStage(Stage.Uploading);
                NetPlay.Send(MsgRequest, true, delegate(BinaryWriter w) { w.Write(ReqUpload); });
                return;
            }
            if (s_stage == Stage.Choosing || s_stage == Stage.Uploading) Outcome = "diverged-host";
            Commit();
            SetStage(Stage.Proceed);
        }

        static void Cancel()
        {
            Outcome = "cancel";
            NetPlay.Send(MsgRequest, true, delegate(BinaryWriter w) { w.Write(ReqCancel); });
            SetStage(Stage.Cancelling);
            NetHud.Toast(
                    L10n.T("saves.cancelled"), "");
            Plugin.Log.LogInfo("coop save: host cancelled at the divergence choice");
        }

        static void ReceiveUpload(BinaryReader r)
        {
            if (!NetPlay.IsHost || s_stage != Stage.Uploading) return;
            int n = r.ReadInt32();
            byte[] bytes = n > 0 && n <= r.BaseStream.Length - r.BaseStream.Position ? r.ReadBytes(n) : new byte[0];   // length is remote input
            if (n <= 0 || bytes.Length != n || ReadHeader(bytes) == null)
            {
                NetHud.Toast(
                    L10n.T("saves.invalid_progress"), "");
                Decide(false); return;
            }
            try
            {
                Backup(s_hostSlot);
                WriteSlotFile(s_hostSlot, bytes);
                s_hostHash = Hash(bytes);
                SaveHeader h = ReadHeader(bytes);
                s_hostLink.SaveId = h.Id;
                Outcome = "diverged-partner";
                SetStage(Stage.Adopting);
                Plugin.Log.LogInfo("coop save: adopting partner progress " + n + " bytes hash " + Short(s_hostHash));
                SaveSync.HostAdopt(bytes);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("coop save: adopting partner progress failed: " + e);
                NetHud.Toast(
                    L10n.T("saves.load_failed"), "");
                Decide(false);
            }
        }

        // SaveSync finished loading the partner's progress on the host.
        public static void HostAdoptFinished()
        {
            if (s_stage != Stage.Adopting) return;
            try { AdoptedHours = SaveSlotsManager.CurrentSaveSlot.Hours; } catch (Exception) { }
            NetHud.Toast(
                    L10n.T("saves.using_partner"), "");
            Commit();
            SetStage(Stage.Proceed);
        }

        // Send the host's slot file to the guest and record the sync in the host's own link.
        static void Commit()
        {
            if (!NetPlay.IsHost || !NetPlay.Connected || s_hostLink == null) return;
            try
            {
                byte[] bytes = ReadSlot(s_hostSlot);
                string hash = Hash(bytes);
                SaveHeader h = ReadHeader(bytes);
                if (h != null) s_hostLink.SaveId = h.Id;
                s_hostLink.Synced = hash;
                s_hostLink.SyncedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                if (bytes != null) s_hostLink.Write(s_hostSlot);
                s_hostHash = hash; LastCommitHash = hash; Commits++;
                string coop = s_hostLink.Coop, saveId = s_hostLink.SaveId;
                NetPlay.Send(MsgCommit, true, delegate(BinaryWriter w)
                {
                    w.Write(coop); w.Write(PlayerId); w.Write(LocalName); w.Write(saveId);
                    w.Write(bytes != null); if (bytes != null) { w.Write(bytes.Length); w.Write(bytes); }
                });
                Plugin.Log.LogInfo("coop save: commit " + (bytes == null ? "(no file yet)" : bytes.Length + " bytes hash " + Short(hash)) + " to " + s_hostLink.Partner);
            }
            catch (Exception e) { Plugin.Log.LogWarning("coop save: commit failed: " + e); }
        }

        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class HostSavedPatch
        {
            static void Postfix()
            {
                if (!NetPlay.Enabled || !NetPlay.IsHost || !NetPlay.Connected) return;
                if (s_stage != Stage.Proceed && s_stage != Stage.Done) return;
                if (SaveSlotsManager.CurrentSlotIndex != s_hostSlot) return;
                Commit();
            }
        }

        // ---------------------------------------------------------------- guest

        static void ReceiveOffer(BinaryReader r)
        {
            if (NetPlay.IsHost) return;
            r.ReadByte();
            s_offerCoop = r.ReadString(); s_offerHostId = r.ReadString(); s_offerHostName = PartnerName(r.ReadString());
            r.ReadString(); r.ReadString();
            CoopId = s_offerCoop;
            s_guestMatch = -1;
            string hash = "", last = "", summary = "";
            bool free = false;
            int current = SafeCurrentSlot();
            for (int i = 0; i < SlotCount; i++)
            {
                byte[] bytes = ReadSlot(i);
                if (bytes == null) { if (i != current || GuestJoiningEmpty) free = true; continue; }
                CoopLink l = ValidLink(i, bytes);
                if (l != null && l.Coop == s_offerCoop && s_guestMatch < 0)
                { s_guestMatch = i; hash = Hash(bytes); last = l.Synced; summary = Summary(bytes); }
            }
            bool found = s_guestMatch >= 0; int slot = s_guestMatch;
            NetPlay.Send(MsgReply, true, delegate(BinaryWriter w)
            {
                w.Write(found); w.Write(slot); w.Write(hash); w.Write(last); w.Write(summary);
                w.Write(PlayerId); w.Write(LocalName); w.Write(free);
            });
            Plugin.Log.LogInfo("coop save: host " + s_offerHostName + " offered " + s_offerCoop + "; local match slot " + slot + " hash " + Short(hash));
        }

        static void ReceiveRequest(BinaryReader r)
        {
            if (NetPlay.IsHost) return;
            byte kind = r.ReadByte();
            if (kind == ReqChoosing)
                NetHud.Toast(
                    L10n.T("saves.waiting_choice"), "");
            else if (kind == ReqCancel)
            {
                CancelledByHost = true;
                NetHud.Toast(
                    L10n.T("saves.host_cancelled"), "");
            }
            else if (kind == ReqUpload)
            {
                byte[] bytes = s_guestMatch >= 0 ? ReadSlot(s_guestMatch) : null;
                NetPlay.Send(MsgUpload, true, delegate(BinaryWriter w)
                {
                    if (bytes == null) { w.Write(0); return; }
                    w.Write(bytes.Length); w.Write(bytes);
                });
                Plugin.Log.LogInfo("coop save: host chose partner progress; sent slot " + s_guestMatch + " (" + (bytes == null ? 0 : bytes.Length) + " bytes)");
            }
        }

        static int SafeCurrentSlot()
        {
            try { return SaveSlotsManager.Instance != null ? SaveSlotsManager.CurrentSlotIndex : -1; } catch (Exception) { return -1; }
        }

        static void ReceiveCommit(BinaryReader r)
        {
            if (NetPlay.IsHost) return;
            string coop = r.ReadString(), hostId = r.ReadString(), hostName = PartnerName(r.ReadString()), saveId = r.ReadString();
            bool has = r.ReadBoolean();
            byte[] bytes = null;
            if (has) { int n = r.ReadInt32(); if (n < 0 || n > r.BaseStream.Length - r.BaseStream.Position) return; bytes = r.ReadBytes(n); if (bytes.Length != n) return; }
            CoopId = coop;
            if (bytes == null || ReadHeader(bytes) == null)
            {
                Plugin.Log.LogInfo("coop save: host has no save file yet; nothing to keep");
                return;
            }
            s_commitCoop = coop; s_commitHostId = hostId; s_commitHostName = hostName; s_commitSaveId = saveId; s_commitBytes = bytes;
            KeepCopy(-1);
        }

        // Last commit from the host this session: a guest still on the title may only learn its slot later
        // (GuestJoin: picked empty slot / no slot / a slot chosen to overwrite).
        static string s_commitCoop, s_commitHostId, s_commitHostName, s_commitSaveId;
        static byte[] s_commitBytes;
        public static bool HaveCommit { get { return s_commitBytes != null; } }
        public static bool NoSlotForCopy;   // last try found no linked or empty slot

        // Guest at the title: retry keeping the copy with the current title rules (no-op once kept).
        public static void RetryKeep() { if (s_commitBytes != null && SessionSlot < 0) KeepCopy(-1); }

        // The player chose this slot to hold the co-op copy (all full): it is backed up, then overwritten.
        public static bool KeepCopyIn(int slot) { if (s_commitBytes == null || slot < 0 || slot >= SlotCount) return false; KeepCopy(slot); return SessionSlot == slot; }

        static void KeepCopy(int forced)
        {
            string coop = s_commitCoop, hostId = s_commitHostId, hostName = s_commitHostName, saveId = s_commitSaveId;
            byte[] bytes = s_commitBytes;
            try
            {
                int slot = forced >= 0 ? forced : PickSlot(coop);
                NoSlotForCopy = slot < 0;
                if (slot < 0)
                {
                    // A guest joining from the title is asked for a slot instead (GuestJoin.NeedSlotChoice).
                    if (!s_noSlotToldOnce && !GuestJoiningEmpty) NetHud.Toast(
                    L10n.T("saves.no_slot"), "");
                    if (!GuestJoiningEmpty) s_noSlotToldOnce = true;
                    Plugin.Log.LogWarning("coop save: no linked or empty slot; not keeping a copy");
                    return;
                }
                string hash = Hash(bytes);
                byte[] old = ReadSlot(slot);
                string oldHash = Hash(old);
                if (old != null && oldHash != hash && oldHash != s_lastWrittenHash) Backup(slot);
                if (oldHash != hash) WriteSlotFile(slot, bytes);
                else RefreshSlotInfo(slot, bytes);
                CoopLink l = new CoopLink();
                l.Coop = coop; l.SaveId = saveId; l.Partner = hostName; l.PartnerId = hostId;
                l.Synced = hash; l.SyncedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                l.Write(slot);
                s_lastWrittenHash = hash; s_guestMatch = slot; GuestSlot = SessionSlot = slot; GuestWrites++; LastCommitHash = hash;
                if (!s_savedToldOnce)
                    NetHud.Toast(
                        L10n.F("saves.saved_slot", slot + 1, hostName), "");
                s_savedToldOnce = true;
                Plugin.Log.LogInfo("coop save: kept host progress in slot " + slot + " hash " + Short(hash) + " lineage " + coop);
            }
            catch (Exception e) { Plugin.Log.LogWarning("coop save: keeping a copy failed: " + e); }
        }

        // The linked slot if there is one, else the first empty slot (never the slot the guest itself is playing).
        static int PickSlot(string coop)
        {
            if (s_guestMatch >= 0)
            {
                CoopLink l = ValidLink(s_guestMatch, null);
                if (l != null && l.Coop == coop) return s_guestMatch;
            }
            int current = SafeCurrentSlot();
            for (int i = 0; i < SlotCount; i++)
            {
                byte[] b = ReadSlot(i);
                if (b == null) continue;
                CoopLink l = ValidLink(i, b);
                if (l != null && l.Coop == coop) return i;
            }
            // Joining from an empty slot: that slot becomes the co-op slot.
            if (GuestJoiningEmpty && GuestPickedSlot >= 0 && GuestPickedSlot < SlotCount && !File.Exists(SavePath(GuestPickedSlot))) return GuestPickedSlot;
            // Still on the title (no slot is being played): the title's default index is not "the guest's slot", and
            // only slots the slot list shows count.
            int count = SlotCount;
            if (GuestJoiningEmpty && SaveSlotsManager.Instance != null) count = Math.Min(SlotCount, SaveSlotsManager.SaveSlotCount);
            for (int i = 0; i < count; i++)
                if ((i != current || GuestJoiningEmpty) && !File.Exists(SavePath(i))) return i;
            return -1;
        }

        // ---------------------------------------------------------------- tick + dialog

        public static void Tick()
        {
            if (s_stage == Stage.Cancelling && Time.realtimeSinceStartup - s_stageAt > 0.6f)
            {
                SetStage(Stage.Done);
                NetPlay.End("co-op save choice cancelled");
                return;
            }
            if (s_stage != Stage.Choosing) return;
            if (TestChoice.Length > 0 && TestReady && Time.realtimeSinceStartup - s_stageAt > 1f)
            {
                Plugin.Log.LogInfo("coop save: test choice " + TestChoice);
                Choose(TestChoice == "partner" ? 1 : TestChoice == "cancel" ? 2 : 0);
                return;
            }
            if (Core.Input.Up.OnPressed || UnityEngine.Input.GetKeyDown(KeyCode.UpArrow)) { s_focus = (s_focus + 2) % 3; Core.Input.Up.Used = true; }
            if (Core.Input.Down.OnPressed || UnityEngine.Input.GetKeyDown(KeyCode.DownArrow)) { s_focus = (s_focus + 1) % 3; Core.Input.Down.Used = true; }
            if (Core.Input.Cancel.OnPressed || UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { s_focus = 2; Core.Input.Cancel.Used = true; }
            if (Core.Input.ActionButtonA.OnPressed || UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space))
            {
                Core.Input.ActionButtonA.Used = true;
                Choose(s_focus);
            }
        }

        static void Choose(int option)
        {
            if (s_stage != Stage.Choosing) return;
            if (option == 0) Decide(false);
            else if (option == 1) Decide(true);
            else Cancel();
        }

        public static bool DialogOpen { get { return s_stage == Stage.Choosing; } }

        static GUIStyle s_title, s_titleEn, s_item, s_itemEn, s_body, s_bodyEn;
        static Texture2D s_scrim, s_line;

        [HarmonyPatch(typeof(NetHud), "Draw")]
        static class DrawPatch
        {
            static void Postfix() { try { if (DialogOpen) DrawDialog(); } catch (Exception e) { Plugin.Log.LogError(e); } }
        }

        static GUIStyle Style(int size, FontStyle fs)
        {
            GUIStyle s = new GUIStyle(GUI.skin.label);
            s.font = GUI.skin.font; s.fontSize = size; s.fontStyle = fs;
            s.alignment = TextAnchor.MiddleCenter; s.richText = true; s.clipping = TextClipping.Overflow; s.wordWrap = false;
            return s;
        }

        static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.HideAndDontSave; return t;
        }

        static void Text(Rect r, string s, GUIStyle st, Color c)
        {
            Color prev = st.normal.textColor;
            st.normal.textColor = new Color(0.01f, 0.03f, 0.05f, 0.75f);
            GUI.Label(new Rect(r.x, r.y + 2f, r.width, r.height), s, st);
            st.normal.textColor = c;
            GUI.Label(r, s, st);
            st.normal.textColor = prev;
        }

        static readonly Color Zh = new Color(0.867f, 0.918f, 0.898f), En = new Color(0.541f, 0.655f, 0.671f),
            ZhOn = new Color(0.945f, 0.988f, 1f), EnOn = new Color(0.57f, 0.88f, 0.96f), Warm = new Color(0.98f, 0.86f, 0.55f);

        static void DrawDialog()
        {
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.45f, 2f);
            int px = Mathf.RoundToInt(48f * scale);
            if (s_title == null || s_title.fontSize != px)
            {
                s_title = Style(px, FontStyle.Normal); s_titleEn = Style(Mathf.RoundToInt(24f * scale), FontStyle.Normal);
                s_item = Style(Mathf.RoundToInt(38f * scale), FontStyle.Normal); s_itemEn = Style(Mathf.RoundToInt(22f * scale), FontStyle.Normal);
                s_body = Style(Mathf.RoundToInt(26f * scale), FontStyle.Normal); s_bodyEn = Style(Mathf.RoundToInt(19f * scale), FontStyle.Normal);
            }
            if (s_scrim == null) { s_scrim = Solid(new Color(0.008f, 0.031f, 0.055f, 0.78f)); s_line = Solid(new Color(0.39f, 0.87f, 0.96f, 0.85f)); }
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), s_scrim);
            float y = Screen.height * 0.12f, w = Screen.width;
            Text(new Rect(0, y, w, 56f * scale), L10n.T("saves.diverged"), s_title, Warm);
            y += 110f * scale;
            string partner = s_hostLink != null ? s_hostLink.Partner : "?";
            Text(new Rect(0, y, w, 32f * scale), Who(), s_body, Zh);
            y += 76f * scale;
            float colW = Mathf.Min(560f * scale, w * 0.44f);
            float lx = w * 0.5f - colW - 20f * scale, rx = w * 0.5f + 20f * scale;
            Column(lx, y, colW, scale, L10n.T("saves.host"), s_hostSummary, s_hostChanged);
            Column(rx, y, colW, scale, L10n.F("saves.partner", partner), s_reply != null ? s_reply.Summary : "?", s_partnerChanged);
            y += 140f * scale;
            Option(0, y, scale, L10n.T("saves.use_host")); y += 84f * scale;
            Option(1, y, scale, L10n.T("saves.use_partner")); y += 84f * scale;
            Option(2, y, scale, L10n.T("saves.cancel")); y += 96f * scale;
            Text(new Rect(0, y, w, 28f * scale), L10n.T("saves.backup_note"), s_bodyEn, Zh);
            Text(new Rect(0, Screen.height - 56f * scale, w - 60f * scale, 30f * scale),
                L10n.T("saves.prompts"), s_bodyEn, Zh);
        }

        static string Who()
        {
            if (s_hostChanged && s_partnerChanged) return L10n.T("saves.both_changed");
            if (s_hostChanged) return L10n.T("saves.host_changed");
            if (s_partnerChanged) return L10n.T("saves.partner_changed");
            return L10n.T("saves.sync_incomplete");
        }

        static void Column(float x, float y, float colW, float scale, string text, string summary, bool changed)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(x, y, colW, 2f), s_line);
            GUI.color = Color.white;
            Text(new Rect(x, y + 8f * scale, colW, 34f * scale), text, s_item, Zh);
            Text(new Rect(x, y + 72f * scale, colW, 30f * scale), summary, s_body, ZhOn);
            Text(new Rect(x, y + 102f * scale, colW, 24f * scale), changed ? L10n.T("saves.changed") : L10n.T("saves.unchanged"), s_bodyEn, changed ? Warm : En);
        }

        static void Option(int idx, float y, float scale, string text)
        {
            float w = 520f * scale, h = 76f * scale;
            Rect r = new Rect((Screen.width - w) * 0.5f, y, w, h);
            Event e = Event.current;
            if (e != null && r.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseMove) s_focus = idx;
                if (e.type == EventType.MouseDown) { s_focus = idx; Choose(idx); e.Use(); }
            }
            bool on = s_focus == idx;
            Text(new Rect(r.x, r.y, r.width, 40f * scale), text, s_item, on ? ZhOn : Zh);
            if (on)
            {
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(Screen.width * 0.5f - 170f * scale, y + 70f * scale, 340f * scale, 2f * Mathf.Max(1f, scale)), s_line);
            }
        }

        // ---------------------------------------------------------------- slot list marker

        [HarmonyPatch(typeof(SaveSlotUI), "Apply")]
        static class SlotMarkerPatch
        {
            static void Postfix(SaveSlotUI __instance)
            {
                try
                {
                    SaveSlotInfo info = __instance.SaveSlot;
                    if (info == null || __instance.AreaName == null) return;
                    CoopLink l = ValidLink(__instance.SaveSlotIndex, null);
                    if (l == null) return;
                    L10n.Set(__instance.AreaName, delegate { return L10n.F("saves.slot_label", __instance.SaveSlotIndex + 1, SaveSlotsScreenshotManager.Instance.FindAreaName(info.AreaName), info.Completion, L10n.F("saves.slot_tag", Clean(l.Partner))); });
                }
                catch (Exception e) { Plugin.Log.LogWarning("coop save: slot marker: " + e.Message); }
            }
        }

        // EnemySync.PartnerSceneFocus keeps the partner's position as a host streaming target. While the host
        // reloads (adopting the partner's progress) that kept the area scenes loaded through the restart, so their
        // GameplaySystemsCreator never ran again and no Ori was created (seen in run-coopsave, both Oris in one spot).
        [HarmonyPatch(typeof(ScenesManager), "UpdatePosition")]
        static class AdoptSceneFocus
        {
            [HarmonyPriority(Priority.Last)]
            static void Postfix(List<Vector3> ___m_cameraPositions)
            {
                if (SaveSync.HostAdopting && NetPlay.HaveRemotePos) ___m_cameraPositions.Remove(NetPlay.LatestRemotePos);
            }
        }

        [HarmonyPatch(typeof(SaveSlotsManager), "DeleteSlot")]
        static class DeleteSlotPatch
        {
            static void Postfix(int index)
            {
                try { if (File.Exists(LinkPath(index))) File.Delete(LinkPath(index)); }
                catch (Exception e) { Plugin.Log.LogWarning("coop save: removing link: " + e.Message); }
            }
        }

        static string Short(string h) { return string.IsNullOrEmpty(h) ? "-" : h.Substring(0, Math.Min(8, h.Length)); }
    }
}
