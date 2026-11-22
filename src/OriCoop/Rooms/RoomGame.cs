using System;
using System.IO;
using HarmonyLib;
using OriCoop.Saves;
using OriCoop.Story;
using UnityEngine;

namespace OriCoop.Rooms
{
    public sealed class RoomGame : IRoomGame
    {
        public int Slot = -1;
        public Action<byte[]> PickCopySlot;
        public Action Starting;
        public static string ConflictMessage { get { return L10n.T("rooms.conflict"); } }
        string IRoomGame.ConflictMessage { get { return ConflictMessage; } }
        public string LiveCode
        {
            get
            {
                return NetPlay.RoomCode;
            }
        }
        public bool InGameplay
        {
            get
            {
                return GameStateMachine.Instance != null
                    && GameStateMachine.Instance.CurrentState == GameStateMachine.State.Game;
            }
        }
        public bool Together
        {
            get
            {
                return NetPlay.PartnerHere;
            }
        }
        public long PlayedSeconds
        {
            get
            {
                return Slot >= 0 ? SaveSlotsManager.SlotByIndex(Slot).TotalSeconds : 0;
            }
        }

        public static string PathFor(int slot)
        {
            return GameController.Instance.SaveGameController.GetSaveFilePath(slot);
        }

        static string Sidecar(int slot)
        {
            return Path.Combine(CoopSaves.Folder, "saveFile" + slot + ".oriroom");
        }

        public static string Summary(int slot)
        {
            SaveSlotInfo info = SaveSlotsManager.SlotByIndex(slot);
            if (info == null) return "";
            return FormatSummary(info.Completion, info.AreaName ?? "", info.TotalSeconds);
        }

        internal static string Summary(byte[] bytes)
        {
            CoopSaves.SaveHeader info = CoopSaves.ReadHeader(bytes);
            return info == null ? "" : FormatSummary(info.Completion, info.Area, info.Hours * 3600 + info.Minutes * 60 + info.Seconds);
        }

        static string FormatSummary(int completion, string area, int seconds)
        {
            if (area.Length > 100) area = area.Substring(0, 100);
            return "pct=" + completion + ";area=" + area + ";secs=" + seconds;
        }

        public static int EmptySlot()
        {
            for (int i = 0; i < SaveGameController.MAX_SAVES; i++)
                if (!File.Exists(CoopSaveFolder.SinglePlayerPath(i))) return i;
            return -1;
        }

        public int WriteRoomSlot(string room, string save, long version, byte[] data)
        {
            CoopSaveFolder.Enter();
            int chosen = -1;
            for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
            {
                if (!File.Exists(Sidecar(i))) continue;
                RoomWire.Fields f = RoomWire.Fields.Decode(File.ReadAllBytes(Sidecar(i)));
                if (f.GetString("room") == room && f.GetString("save") == save)
                {
                    chosen = i;
                    break;
                }
            }
            if (chosen < 0) chosen = CoopSaveFolder.FirstEmptyCoopSlot();
            if (chosen < 0) return -1;
            SkillRecord record;
            data = SeatSkillSync.ForLoad(data, "room save", out record);
            RoomWire.AtomicWrite(PathFor(chosen), data);
            SeatSkillSync.WriteSidecar(chosen, CoopSaves.Hash(data), record);
            RefreshSlot(chosen, data);
            UpdateVersion(chosen, room, save, version);
            Slot = chosen;
            return chosen;
        }

        public void UpdateVersion(int slot, string room, string save, long version)
        {
            if (slot < 0) return;
            RoomWire.Fields f = new RoomWire.Fields();
            f.Set("room", room);
            f.Set("save", save);
            f.Set("version", version);
            RoomWire.AtomicWrite(Sidecar(slot), f.Encode());
        }

        static void BackupSingle(string path)
        {
            string dir = Path.Combine(Path.GetDirectoryName(path), "OriCoop-backups");
            Directory.CreateDirectory(dir);
            string name = Path.GetFileNameWithoutExtension(path);
            string dest = Path.Combine(dir, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".sav");
            File.Copy(path, dest, true);
            foreach (string ext in new[] { ".oricoop", ".oriroom" })
            {
                string link = Path.ChangeExtension(path, ext);
                if (File.Exists(link)) File.Copy(link, Path.ChangeExtension(dest, ext), true);
            }
            string[] old = Directory.GetFiles(dir, name + "-*.sav");
            Array.Sort(old, StringComparer.Ordinal);
            for (int i = 0; i < old.Length - 5; i++)
            {
                File.Delete(old[i]);
                File.Delete(Path.ChangeExtension(old[i], ".oricoop"));
                File.Delete(Path.ChangeExtension(old[i], ".oriroom"));
            }
        }

        static void RefreshSlot(int slot, byte[] bytes)
        {
            int previous = SaveSlotInfo.CurrentInfoVersion;
            try
            {
                SaveSlotInfo info = new SaveSlotInfo();
                using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
                    SaveSlotsManager.Instance.SaveSlots[slot] = info.LoadFromReader(reader) ? info : null;
            }
            finally
            {
                SaveSlotInfo.CurrentInfoVersion = previous;
            }
        }

        public static void WriteSingle(int slot, byte[] bytes)
        {
            bytes = SeatSkillSync.ForLoad(bytes, "room save to single player");
            string path = CoopSaveFolder.SinglePlayerPath(slot);
            if (File.Exists(path)) BackupSingle(path);
            RoomWire.AtomicWrite(path, bytes);
            File.Delete(Path.ChangeExtension(path, ".oriroom"));
            File.Delete(Path.ChangeExtension(path, ".oricoop"));
            if (!CoopSaveFolder.Active) RefreshSlot(slot, bytes);
        }

        public void Host(int slot, string server)
        {
            Starting();
            NativeModeMenu.RoomHostSlot(slot, server);
        }

        public void Join(string target)
        {
            MyRoomsPages.Page fromPage = MyRoomsPages.Current;   // T-176: Starting closes it
            Starting();
            NativeModeMenu.RoomJoin(target, fromPage);
        }

        public byte[] Snapshot(out string summary)
        {
            SaveSlotsManager.CurrentSaveSlot.FillData();
            summary = Summary(SaveSlotsManager.CurrentSlotIndex);
            return SkillTrailer.Append(GameController.Instance.SaveGameController.SaveToBytes(), SeatSkillSync.ReadSidecar(Slot));
        }

        public void CopySingle(byte[] data)
        {
            int empty = EmptySlot();
            if (empty < 0)
            {
                PickCopySlot(data);
                return;
            }
            WriteSingle(empty, data);
            Message(L10n.T("rooms.copied"));
        }

        public void Message(string text)
        {
            NetHud.Toast(text, "");
        }

        [HarmonyPatch(typeof(SaveGameController), "SaveToFile", new Type[] { typeof(string) })]
        static class SavePatch
        {
            static void Postfix(string __0)
            {
                RoomsPanel panel = RoomsPanel.Instance;
                if (panel == null || panel.Session == null || !panel.Session.Active
                    || !panel.Session.Holder || panel.Game.Slot < 0) return;
                if (!string.Equals(Path.GetFullPath(__0), Path.GetFullPath(PathFor(panel.Game.Slot)),
                    StringComparison.OrdinalIgnoreCase)) return;
                byte[] bytes = File.ReadAllBytes(__0);
                panel.Session.Saved(SkillTrailer.Append(bytes, SeatSkillSync.ForSave(panel.Game.Slot, CoopSaves.Hash(bytes))),
                    Summary(panel.Game.Slot));
            }
        }

        [HarmonyPatch(typeof(GameController), "RestartGame")]
        static class ReturnPatch
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                // T-130: the room holder adopting the partner's progress restarts the game to load it; that is not a
                // return to the title, so the room session (and its later uploads) goes on, as TitleReturn keeps
                // the online session for the same restart.
                if (SaveSync.RestartingForLoad) return;
                RoomsPanel panel = RoomsPanel.Instance;
                if (panel != null && panel.Session != null && panel.Session.Holder)
                    panel.Session.ReturnToTitle();
            }
        }
    }
}
