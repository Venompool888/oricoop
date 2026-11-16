using System;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // An online GUEST that is still on the title loads the host's checkpoint straight from there: no new game, no
    // prologue, no RestartGame teardown (playtest 2026-11-15: from an empty slot the new-game prologue ran while
    // SaveSync's teardown waited, "restart/restore did not finish", the guest stayed alone).
    // Two ways in:
    //   - Intercept: the player picked an EMPTY slot (difficulty -> new game): SaveSlotsUI.SetDifficulty is skipped
    //     (NativeModeMenu.NewGamePatch). That slot becomes the co-op slot (a linked co-op slot still wins).
    //   - BeginNoSlot: the title 加入 path never shows the save slots (TitleMenu / NativeModeMenu / SteamInviteHook).
    //     The co-op copy goes to the linked slot, else the first empty one; all full -> NeedSlotChoice, the caller
    //     shows the slots and calls UseSlot(i) (slot i is backed up, then overwritten).
    // Then: the transfer screen shows "waiting" (SessionScreens); once the checkpoint is in, the game's own
    // load-from-title action (SaveSlotsUI.UsedSaveSlotPressedAction) runs and its PerformLoad reads the host's
    // checkpoint bytes (SaveSync.BeginTitleLoad); SaveSync's FinishLoading hook places the guest beside the host.
    // Failure (partner gone, nothing within 90 s): a toast that says why; Aborts / LastAbort; the connection is kept.
    // Slot mode re-activates the slot list; no-slot mode leaves the UI to the caller.
    public static class GuestJoin
    {
        const float WaitLimit = 90f;
        static bool s_waiting, s_noSlot, s_toldTitleWait;
        static SaveSlotsUI s_ui;         // slot mode: the slot list to give back on abort
        static int s_slot = -1;          // slot mode: the picked empty slot; no-slot mode: UseSlot choice
        static float s_since;

        public static bool Waiting { get { return s_waiting; } }
        public static bool NeedSlotChoice { get; private set; }
        public static bool Loading { get; private set; }   // the title load action has run (until the session ends)
        public static int Intercepts, TitleLoads, Aborts;
        public static string LastAbort = "";

        // From NativeModeMenu.NewGamePatch (prefix of SaveSlotsUI.SetDifficulty). true = skip the new game.
        public static bool Intercept(SaveSlotsUI ui)
        {
            if (!NetPlay.Enabled || NetPlay.IsHost || ui == null || s_waiting || Loading) return false;
            try
            {
                GameObject diff = Traverse.Create(ui).Field("m_difficultyScreen").GetValue<GameObject>();
                if (diff != null)
                {
                    CleverMenuItemSelectionManager m = diff.GetComponent<CleverMenuItemSelectionManager>();
                    if (m != null) m.SetVisible(false);
                    InstantiateUtility.Destroy(diff, 2f);
                    Traverse.Create(ui).Field("m_difficultyScreen").SetValue(null);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("guest join: closing the difficulty screen: " + e.Message); }
            ui.Active = false;
            Start(ui, SaveSlotsManager.CurrentSlotIndex, false);
            Plugin.Log.LogInfo("guest join: empty slot " + s_slot + " picked while joining: no new game; waiting for the host's save");
            return true;
        }

        // Title 加入 path: the guest is connected and never picked a slot. false = not an online guest / already busy.
        public static bool BeginNoSlot()
        {
            if (!NetPlay.Enabled || NetPlay.IsHost || s_waiting || Loading) return false;
            Start(null, -1, true);
            Plugin.Log.LogInfo("guest join: joining from the title without a save slot; waiting for the host's save");
            return true;
        }

        // After NeedSlotChoice: the player picked this slot for the co-op copy (it is backed up, then overwritten).
        public static void UseSlot(int slot)
        {
            if (!s_waiting || !NeedSlotChoice) return;
            if (!CoopSaves.KeepCopyIn(slot)) { Plugin.Log.LogWarning("guest join: keeping the copy in chosen slot " + slot + " failed"); return; }
            s_slot = slot; NeedSlotChoice = false;
            Plugin.Log.LogInfo("guest join: co-op copy kept in chosen slot " + slot);
        }

        static void Start(SaveSlotsUI ui, int slot, bool noSlot)
        {
            s_ui = ui; s_slot = slot; s_noSlot = noSlot; s_since = Time.realtimeSinceStartup;
            s_waiting = true; NeedSlotChoice = false; s_toldTitleWait = false;
            CoopSaves.GuestJoiningEmpty = true; CoopSaves.GuestPickedSlot = noSlot ? -1 : slot;
            Intercepts++;
            CoopSaves.RetryKeep();   // a commit may have arrived before this (picked / title rules apply only now)
        }

        public static void Tick()
        {
            if (!NetPlay.Enabled) { if (s_waiting) Abort("guest.ended", "session ended"); Loading = false; return; }
            if (!s_waiting) return;
            if (NeedSlotChoice) { s_since = Time.realtimeSinceStartup; return; }   // the player is choosing; no timeout
            // The title's load action only works once the title is up: run while it still boots (screen Undefined),
            // it sets the Game state but no scene loads and no Ori appears (nettest -GuestNoSlot, 2026-11-15).
            if (SaveSync.HavePendingCheckpoint && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.Undefined)
            {
                if (!s_toldTitleWait) { s_toldTitleWait = true; Plugin.Log.LogInfo("guest join: checkpoint here; waiting for the title to finish loading"); }
                s_since = Time.realtimeSinceStartup;
                return;
            }
            if (SaveSync.HavePendingCheckpoint)
            {
                // CoopSaves gets the host's slot file before the checkpoint (same reliable channel, sent first).
                CoopSaves.RetryKeep();
                if (CoopSaves.SessionSlot < 0 && s_noSlot && CoopSaves.HaveCommit && CoopSaves.NoSlotForCopy)
                {
                    NeedSlotChoice = true;
                    Plugin.Log.LogInfo("guest join: every save slot is in use and none is linked: asking for a slot to overwrite");
                    return;
                }
                Load(CoopSaves.SessionSlot >= 0 ? CoopSaves.SessionSlot : FallbackSlot());
                return;
            }
            if (Time.realtimeSinceStartup - s_since > WaitLimit)
                Abort("guest.no_save", "no checkpoint in " + WaitLimit + " s");
        }

        // No co-op copy kept (the host has no save file yet): any slot index works, a guest never writes its slot.
        static int FallbackSlot()
        {
            if (s_slot >= 0) return s_slot;
            try
            {
                for (int i = 0; i < SaveSlotsManager.SaveSlotCount; i++)
                    if (!GameController.Instance.SaveGameController.SaveExists(i)) return i;
            }
            catch (Exception) { }
            return 0;
        }

        static void Load(int slot)
        {
            SaveSlotsUI ui = s_ui != null ? s_ui : SaveSlotsUI.Instance;
            if (ui == null) { Abort("guest.no_screen", "no SaveSlotsUI"); return; }
            s_waiting = false;
            CoopSaves.GuestJoiningEmpty = false; CoopSaves.GuestPickedSlot = -1;
            try
            {
                SaveSlotsManager.CurrentSlotIndex = slot;
                SaveSlotsManager.BackupIndex = -1;
                SaveSlotsManager.FindOrCreateSaveSlot(slot);
                if (!SaveSync.BeginTitleLoad()) { Fail(ui, "guest.load_failed", "checkpoint not available"); return; }
                if (NativeModeMenu.Pending == PlayMode.Undecided) NativeModeMenu.Pending = PlayMode.Single;
                TitleLoads++; Loading = true;
                Plugin.Log.LogInfo("guest join: loading the host's checkpoint from the title into slot " + slot
                    + (slot == CoopSaves.SessionSlot ? " (co-op copy)" : " (no co-op copy)") + (s_noSlot ? ", no slot was picked" : ""));
                ui.UsedSaveSlotPressedAction.Perform(null);
                s_ui = null;
            }
            catch (Exception e)
            {
                Loading = false;
                SaveSync.TitleLoadFailed(e);
                Fail(ui, "guest.load_failed", e.Message);
            }
        }

        static void Abort(string key, string why) { Fail(s_ui, key, why); }

        static void Fail(SaveSlotsUI ui, string key, string why)
        {
            bool noSlot = s_noSlot;
            s_waiting = false; s_ui = null; NeedSlotChoice = false;
            CoopSaves.GuestJoiningEmpty = false; CoopSaves.GuestPickedSlot = -1;
            Aborts++; LastAbort = why;
            Plugin.Log.LogWarning("guest join: not loading the host's save (" + why + ")" + (noSlot ? "" : "; back to the save slots"));
            if (NetPlay.Enabled && NetPlay.Connected) NetHud.Toast(
                L10n.F("guest.still_connected", L10n.T(key)), "");
            else NetHud.Toast(
                L10n.T(key), "");
            if (!noSlot && ui != null) ui.Active = true;
        }
    }
}
