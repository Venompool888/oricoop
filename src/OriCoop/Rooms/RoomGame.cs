using System;
using System.IO;
using HarmonyLib;
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
            string area = info.AreaName ?? "";
            if (area.Length > 100) area = area.Substring(0, 100);
            return "pct=" + info.Completion + ";area=" + area + ";secs=" + info.TotalSeconds;
        }

        public static int EmptySlot()
        {
            for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
                if (!File.Exists(PathFor(i))) return i;
            return -1;
        }

        public int WriteRoomSlot(string room, string save, long version, byte[] data)
        {
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
            if (chosen < 0) chosen = EmptySlot();
            if (chosen < 0) return -1;
            Write(chosen, data, false);
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

        public static void Write(int slot, byte[] bytes, bool backup)
        {
            string path = PathFor(slot);
            if (backup && File.Exists(path))
            {
                string dir = Path.Combine(CoopSaves.Folder, "OriCoop-backups");
                Directory.CreateDirectory(dir);
                string name = "saveFile" + slot + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                string dest = Path.Combine(dir, name + ".sav");
                File.Copy(path, dest, true);
                string coop = Path.Combine(CoopSaves.Folder, "saveFile" + slot + ".oricoop");
                if (File.Exists(coop)) File.Copy(coop, Path.ChangeExtension(dest, ".oricoop"), true);
                if (File.Exists(Sidecar(slot)))
                    File.Copy(Sidecar(slot), Path.ChangeExtension(dest, ".oriroom"), true);
                string[] old = Directory.GetFiles(dir, "saveFile" + slot + "-*.sav");
                Array.Sort(old, StringComparer.Ordinal);
                for (int i = 0; i < old.Length - 5; i++)
                {
                    File.Delete(old[i]);
                    string coopLink = Path.ChangeExtension(old[i], ".oricoop");
                    string roomLink = Path.ChangeExtension(old[i], ".oriroom");
                    if (File.Exists(coopLink)) File.Delete(coopLink);
                    if (File.Exists(roomLink)) File.Delete(roomLink);
                }
            }
            RoomWire.AtomicWrite(path, bytes);
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
            Write(slot, bytes, true);
            if (File.Exists(Sidecar(slot))) File.Delete(Sidecar(slot));
            string coop = Path.Combine(CoopSaves.Folder, "saveFile" + slot + ".oricoop");
            if (File.Exists(coop)) File.Delete(coop);
        }

        public void Host(int slot, string relay)
        {
            Starting();
            NativeModeMenu.RoomHostSlot(slot, relay);
        }

        public void Join(string target)
        {
            Starting();
            NativeModeMenu.RoomJoin(target);
        }

        public byte[] Snapshot(out string summary)
        {
            SaveSlotsManager.CurrentSaveSlot.FillData();
            summary = Summary(SaveSlotsManager.CurrentSlotIndex);
            return GameController.Instance.SaveGameController.SaveToBytes();
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
                panel.Session.Saved(File.ReadAllBytes(__0), Summary(panel.Game.Slot));
            }
        }

        [HarmonyPatch(typeof(GameController), "RestartGame")]
        static class ReturnPatch
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix()
            {
                RoomsPanel panel = RoomsPanel.Instance;
                if (panel != null && panel.Session != null && panel.Session.Holder)
                    panel.Session.ReturnToTitle();
            }
        }
    }
}
