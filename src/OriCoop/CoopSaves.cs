using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game;
using HarmonyLib;
using OriCoop.Seams;
using OriCoop.Saves;
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
    public static partial class CoopSaves
    {
        const int KeepBackups = 5, SlotCount = 10;
        static LineageMachine s_machine;

        // Host side
        static CoopLink s_hostLink;
        static int s_hostSlot = -1;
        static string s_hostHash = "", s_hostSummary = "";
        static LineageReply s_reply;
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
        public static int LateCommits, CommitsReceived, OffersSeen;   // T-129: commits to a seat that joined late (host) / commits and offers heard (guest)
        public static string CoopId = "", LastCommitHash = "";
        public static string TestChoice = Environment.GetEnvironmentVariable("ORICOOP_COOPSAVE_CHOICE") ?? "";
        public static bool TestReady;   // set by CoopSaveTest once the host's harness runs (a cancel must not beat it)
        // Guest still on the title, joining (GuestJoin.cs): no slot is being played, so the current index is not
        // excluded; GuestPickedSlot (an empty slot the player picked, -1 none) is preferred after a linked slot.
        public static bool GuestJoiningEmpty;
        public static int GuestPickedSlot = -1;
        public static int SessionSlot = -1;   // slot that holds the host's copy in THIS session (-1 none yet)

        public static void Init()
        {
            s_machine = new LineageMachine(Services.Net, Services.Clock, new LineageActions());
            s_machine.Init();
            CoopSaveFolder.OnlineFromLaunch();   // T-043: online from launch -> co-op folder before any slot is read
        }

        static void Reset()
        {
            s_reply = null; s_hostLink = null; s_hostSlot = -1;
            s_offerCoop = s_offerHostId = s_offerHostName = ""; s_guestMatch = -1;
            s_lastWrittenHash = null; s_noSlotToldOnce = s_savedToldOnce = false;
            GuestJoiningEmpty = false; GuestPickedSlot = -1; SessionSlot = -1; NoSlotForCopy = false; SavesBlocked = false; PromotedFrom = -1;
            s_commitBytes = null; s_commitCoop = s_commitHostId = s_commitHostName = s_commitSaveId = null;
            s_timeline = "";
        }

        // T-043 (room-owned saves): the room's save timeline is the last commit. Host: the hash it last committed;
        // guest: the hash of the last commit it received (kept even when no slot could hold the copy). A promoted guest
        // (T-126) keeps the one it received, so the others' offers still match until its first own commit.
        static string s_timeline = "";
        public static string Timeline { get { return s_timeline; } }
        // Host: the lineage is decided and the live game plays the room's slot, so a save lands there and commits.
        public static bool RoomSlotLive
        {
            get
            {
                return s_machine != null && (s_machine.Stage == LineageStage.Proceed || s_machine.Stage == LineageStage.Done)
                    && s_hostLink != null && s_hostSlot >= 0 && SafeCurrentSlot() == s_hostSlot;
            }
        }

        // ---------------------------------------------------------------- files

        public static string Folder { get { return CoopSaveFolder.Active ? CoopSaveFolder.Dir : OutputFolder.PlayerDataFolderPath; } }
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

        internal static string Summary(byte[] bytes)
        {
            SaveHeader h = ReadHeader(bytes);
            if (h == null) return "?";
            return SavesHost.Current.Format("saves.summary", AreaTitle(h.Area), h.Completion, h.Hours, h.Minutes, h.Seconds);
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
                catch (Exception e) { Services.Log.Warn("coop save: reading link " + slot + ": " + e.Message); return null; }
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
            Services.Log.Info("coop save: backed up slot " + slot + " -> " + dst);
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
                catch (Exception e) { Services.Log.Warn("coop save: pruning backup: " + e.Message); }
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
            catch (Exception e) { Services.Log.Warn("coop save: refreshing slot info: " + e.Message); }
        }

        // ---------------------------------------------------------------- identity

        static string s_playerId;
        public static string PlayerId
        {
            get
            {
                if (s_playerId != null) return s_playerId;
                string p = Path.Combine(OutputFolder.PlayerDataFolderPath, "oricoop-player.txt");
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
            string steam = SavesHost.Current.UsesSteam ? SavesHost.Current.SteamPartnerName : "";
            return Clean(string.IsNullOrEmpty(steam) ? sent : steam);
        }

        // ---------------------------------------------------------------- host

        [HarmonyPatch(typeof(SaveGameController), "PerformSave")]
        static class HostSavedPatch
        {
            static void Postfix()
            {
                if (!Services.Net.Enabled || !Services.Net.IsHost || !Services.Net.Connected) return;
                if (s_machine.Stage != LineageStage.Proceed && s_machine.Stage != LineageStage.Done) return;
                if (SaveSlotsManager.CurrentSlotIndex != s_hostSlot) return;
                s_checkpointCommit = true;
                try { Commit(); } finally { s_checkpointCommit = false; }
            }
        }

        // ---------------------------------------------------------------- guest

        static int SafeCurrentSlot()
        {
            try { return SaveSlotsManager.Instance != null ? SaveSlotsManager.CurrentSlotIndex : -1; } catch (Exception) { return -1; }
        }

        // Last commit from the host this session: a guest still on the title may only learn its slot later
        // (GuestJoin: picked empty slot / no slot / a slot chosen to overwrite).
        static string s_commitCoop, s_commitHostId, s_commitHostName, s_commitSaveId;
        static byte[] s_commitBytes;
        internal static byte[] CommitBytes { get { return s_commitBytes; } }
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
                    if (!s_noSlotToldOnce && !GuestJoiningEmpty) SavesHost.Current.Toast(
                    SavesHost.Current.Translate("saves.no_slot"), "");
                    if (!GuestJoiningEmpty) s_noSlotToldOnce = true;
                    Services.Log.Warn("coop save: no linked or empty slot; not keeping a copy");
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
                    SavesHost.Current.Toast(
                        SavesHost.Current.Format("saves.saved_slot", slot + 1, hostName), "");
                s_savedToldOnce = true;
                Services.Log.Info("coop save: kept host progress in slot " + slot + " hash " + Short(hash) + " lineage " + coop);
            }
            catch (Exception e) { Services.Log.Warn("coop save: keeping a copy failed: " + e); }
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
            if (s_machine.Tick()) return;
            if (s_machine.Stage != LineageStage.Choosing) return;
            if (TestChoice.Length > 0 && TestReady && Services.Clock.RealTime - s_machine.StageAt > 1f)
            {
                Services.Log.Info("coop save: test choice " + TestChoice);
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
            if (s_machine.Stage != LineageStage.Choosing) return;
            if (option == 0) s_machine.Decide(false);
            else if (option == 1) s_machine.Decide(true);
            else s_machine.Cancel();
        }

        public static bool DialogOpen { get { return s_machine != null && s_machine.Stage == LineageStage.Choosing; } }

        internal static bool TestShow;
        static int s_element;
        static GUIStyle s_title, s_titleEn, s_item, s_itemEn, s_body, s_bodyEn;
        static Texture2D s_scrim, s_line;

        [HarmonyPatch(typeof(NetHud), "Draw")]
        static class DrawPatch
        {
            static void Postfix() { try { if (DialogOpen || (SavesHost.Current.LocalizationProbeOn && TestShow)) DrawDialog(); } catch (Exception e) { Services.Log.Error(e.ToString()); } }
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
            string id = "saves.dialog.text." + s_element++;
            if (NativeGui.Available) { NativeGui.Label(id, r, s, st.fontSize, st.alignment, c); return; }
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
            NativeGui.BeginPage("saves.dialog"); s_element = 0;
            try
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
                if (NativeGui.Available) NativeGui.Panel("saves.dialog.scrim", new Rect(0, 0, Screen.width, Screen.height), new Color(0.008f, 0.031f, 0.055f, 0.78f));
                else GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), s_scrim);
                float y = Screen.height * 0.12f, w = Screen.width;
                Text(new Rect(0, y, w, 56f * scale), SavesHost.Current.Translate("saves.diverged"), s_title, Warm);
                y += 110f * scale;
                string partner = SavesHost.Current.LocalizationProbeOn && TestShow ? SavesHost.Current.Translate("steam.friend") : s_hostLink != null ? s_hostLink.Partner : "?";
                Text(new Rect(0, y, w, 32f * scale), Who(), s_body, Zh);
                y += 76f * scale;
                float colW = Mathf.Min(560f * scale, w * 0.44f);
                float lx = w * 0.5f - colW - 20f * scale, rx = w * 0.5f + 20f * scale;
                Column(lx, y, colW, scale, SavesHost.Current.Translate("saves.host"), SavesHost.Current.LocalizationProbeOn && TestShow ? SavesHost.Current.Translate("saves.new_game") : s_hostSummary, s_hostChanged);
                Column(rx, y, colW, scale, SavesHost.Current.Format("saves.partner", partner), SavesHost.Current.LocalizationProbeOn && TestShow ? SavesHost.Current.Translate("saves.new_game") : s_reply != null ? s_reply.Summary : "?", s_partnerChanged);
                y += 140f * scale;
                Option(0, y, scale, SavesHost.Current.Translate("saves.use_host")); y += 84f * scale;
                Option(1, y, scale, SavesHost.Current.Translate("saves.use_partner")); y += 84f * scale;
                Option(2, y, scale, SavesHost.Current.Translate("saves.cancel")); y += 96f * scale;
                Text(new Rect(0, y, w, 28f * scale), SavesHost.Current.Translate("saves.backup_note"), s_bodyEn, Zh);
                Text(new Rect(0, Screen.height - 56f * scale, w - 60f * scale, 30f * scale),
                    SavesHost.Current.Translate("saves.prompts"), s_bodyEn, Zh);
            }
            finally { NativeGui.EndPage(); }
        }

        static string Who()
        {
            if (s_hostChanged && s_partnerChanged) return SavesHost.Current.Translate("saves.both_changed");
            if (s_hostChanged) return SavesHost.Current.Translate("saves.host_changed");
            if (s_partnerChanged) return SavesHost.Current.Translate("saves.partner_changed");
            return SavesHost.Current.Translate("saves.sync_incomplete");
        }

        static void Column(float x, float y, float colW, float scale, string text, string summary, bool changed)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            if (NativeGui.Available) NativeGui.Panel("saves.dialog.column." + s_element++, new Rect(x, y, colW, 2f), new Color(0.39f, 0.87f, 0.96f, 0.85f * 0.35f));
            else GUI.DrawTexture(new Rect(x, y, colW, 2f), s_line);
            GUI.color = Color.white;
            Text(new Rect(x, y + 8f * scale, colW, 34f * scale), text, s_item, Zh);
            Text(new Rect(x, y + 72f * scale, colW, 30f * scale), summary, s_body, ZhOn);
            Text(new Rect(x, y + 102f * scale, colW, 24f * scale), changed ? SavesHost.Current.Translate("saves.changed") : SavesHost.Current.Translate("saves.unchanged"), s_bodyEn, changed ? Warm : En);
        }

        static void Option(int idx, float y, float scale, string text)
        {
            float w = 520f * scale, h = 76f * scale;
            Rect r = new Rect((Screen.width - w) * 0.5f, y, w, h);
            Event e = Event.current;
            if (e != null && r.Contains(e.mousePosition) && e.type == EventType.MouseMove) s_focus = idx;
            bool on = s_focus == idx;
            if (NativeGui.Available)
            {
                if (NativeGui.Button("saves.dialog.option." + idx, r, "", s_item.fontSize, on, on ? ZhOn : Zh))
                { s_focus = idx; Choose(idx); }
            }
            else
            {
                if (e != null && r.Contains(e.mousePosition) && e.type == EventType.MouseDown) { s_focus = idx; Choose(idx); e.Use(); }
            }
            Text(new Rect(r.x, r.y, r.width, 40f * scale), text, s_item, s_focus == idx ? ZhOn : Zh);
            if (on)
            {
                GUI.color = Color.white;
                Rect line = new Rect(Screen.width * 0.5f - 170f * scale, y + 70f * scale, 340f * scale, 2f * Mathf.Max(1f, scale));
                if (NativeGui.Available) NativeGui.Panel("saves.dialog.option.line." + idx, line, new Color(0.39f, 0.87f, 0.96f, 0.85f));
                else GUI.DrawTexture(line, s_line);
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
                    SavesHost.Current.SetText(__instance.AreaName, delegate { return SavesHost.Current.Format("saves.slot_label", __instance.SaveSlotIndex + 1, SaveSlotsScreenshotManager.Instance.FindAreaName(info.AreaName), info.Completion, SavesHost.Current.Format("saves.slot_tag", Clean(l.Partner))); });
                }
                catch (Exception e) { Services.Log.Warn("coop save: slot marker: " + e.Message); }
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
                if (SaveSync.HostAdopting && SavesHost.Current.HaveRemotePosition)
                {
                    SavePosition p = SavesHost.Current.LatestRemotePosition;
                    ___m_cameraPositions.Remove(new Vector3(p.X, p.Y, p.Z));
                }
            }
        }

        [HarmonyPatch(typeof(SaveSlotsManager), "DeleteSlot")]
        static class DeleteSlotPatch
        {
            static void Postfix(int index)
            {
                try { if (File.Exists(LinkPath(index))) File.Delete(LinkPath(index)); }
                catch (Exception e) { Services.Log.Warn("coop save: removing link: " + e.Message); }
            }
        }

        static string Short(string h) { return string.IsNullOrEmpty(h) ? "-" : h.Substring(0, Math.Min(8, h.Length)); }
    }
}
