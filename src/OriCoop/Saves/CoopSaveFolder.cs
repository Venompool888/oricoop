using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using OriCoop.Seams;

namespace OriCoop.Saves
{
    public static partial class CoopSaveFolder
    {
        public static bool Active { get; private set; }
        public static string Dir { get { return Path.Combine(OutputFolder.PlayerDataFolderPath, "OriCoop-coop"); } }

        public static void Enter()
        {
            if (Active) return;
            Directory.CreateDirectory(Dir);
            Active = true;
            Reload();
            Services.Log.Info("saves: co-op save folder on");
        }

        // A config- or env-driven launch (the [Net] file, every test harness) is online from the start and never passes
        // NetPlay.Begin or the title menu: switch before the game reads its slots (CoopSaves.Init, right after
        // NetPlay.Init), so nothing loads or saves the single-player files. No slot is read yet: nothing to reload, and
        // the folder itself is made on first use (the save folder path needs the GameController).
        public static void OnlineFromLaunch()
        {
            if (!NetPlay.Enabled || Active) return;
            Active = true;
            Services.Log.Info("saves: co-op save folder on (online from launch)");
        }
        static bool s_dirMade;

        public static void Leave()
        {
            if (!Active) return;
            Active = false;
            Reload();
            Services.Log.Info("saves: co-op save folder off");
        }

        internal static string SinglePlayerPath(int slot)
        {
            return Path.Combine(OutputFolder.PlayerDataFolderPath, "saveFile" + slot + ".sav");
        }

        public static int FirstEmptyCoopSlot()
        {
            bool[] occupied = new bool[SaveGameController.MAX_SAVES];
            for (int i = 0; i < occupied.Length; i++)
                occupied[i] = File.Exists(MapPath(SinglePlayerPath(i), true, Dir));
            return FirstEmpty(occupied);
        }

        public static int[] SinglePlayerSlotsWithSaves()
        {
            List<int> slots = new List<int>();
            for (int i = 0; i < SaveGameController.MAX_SAVES; i++)
                if (File.Exists(SinglePlayerPath(i))) slots.Add(i);
            return slots.ToArray();
        }

        public static bool CopyFromSinglePlayer(int singleSlot, int coopSlot)
        {
            if (!Active || singleSlot < 0 || singleSlot >= SaveGameController.MAX_SAVES
                || coopSlot < 0 || coopSlot >= SaveGameController.MAX_SAVES) return false;
            string source = SinglePlayerPath(singleSlot), target = MapPath(SinglePlayerPath(coopSlot), true, Dir);
            if (File.Exists(target)) return false;
            try
            {
                File.Copy(source, target, false);
                // Keep the tracking ID so linked co-op copies still match.
                string link = Path.Combine(OutputFolder.PlayerDataFolderPath, "saveFile" + singleSlot + ".oricoop");
                if (File.Exists(link)) File.Copy(link, Path.Combine(Dir, "saveFile" + coopSlot + ".oricoop"), true);
                Reload();
                Services.Log.Info("saves: copied single-player slot " + singleSlot + " to co-op slot " + coopSlot);
                return true;
            }
            catch (IOException e)
            {
                Services.Log.Warn("saves: copy failed: " + e.Message);
                return false;
            }
        }

        static void Reload()
        {
            int version = SaveSlotInfo.CurrentInfoVersion;
            try { SaveSlotsManager.PrepareSlots(); }
            finally { SaveSlotInfo.CurrentInfoVersion = version; }
            SaveSlotsManager.BackupIndex = -1;
            SaveSlotBackupsManager backups = Traverse.Create(typeof(SaveSlotBackupsManager)).Field("m_instance").GetValue<SaveSlotBackupsManager>();
            if (backups != null)
            {
                Traverse.Create(backups).Field("m_currentReadingSlot").SetValue(-1);
                Traverse.Create(backups).Field("m_onFinishedReaded").SetValue(null);
                backups.OnGameReset();
            }
            SaveSlotsUI ui = SaveSlotsUI.Instance;
            if (ui == null) return;
            foreach (SaveSlotUI slot in ui.Items)
                if (slot != null) { slot.ChangeSelectionIndex(-1); slot.RefreshBackups(); }
            if (ui.IsVisible) ui.RefreshSlots();
        }

        [HarmonyPatch(typeof(NetPlay), "Begin")]
        static class OnlineStartPatch
        {
            static void Prefix() { Enter(); }
        }

        [HarmonyPatch(typeof(SaveGameController), "GetSaveFilePath")]
        static class SavePathPatch
        {
            static void Postfix(ref string __result)
            {
                if (Active && !s_dirMade) { Directory.CreateDirectory(Dir); s_dirMade = true; }
                __result = MapPath(__result, Active && !s_mutateNoFolder, Dir);
            }
        }
        // test-only: online saves go to the single-player files again (the bug T-043 rule 5 fixes)
        static readonly bool s_mutateNoFolder = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_NO_COOP_FOLDER") == "1";

        [HarmonyPatch(typeof(SaveSlotBackupsManager), "BackupName")]
        static class BackupPathPatch
        {
            static void Postfix(ref string __result) { __result = MapPath(__result, Active, Dir); }
        }
    }
}
