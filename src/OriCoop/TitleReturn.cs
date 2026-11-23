using HarmonyLib;

namespace OriCoop
{
    // Going back to the title screen ends the online session (pause menu -> quit, the disconnect dialog, any other
    // GameController.RestartGame). Before this the Steam lobby / direct host stayed open at the title:
    // NetPlay.Enabled kept the native mode menu off (picking a slot went straight into single player) and every
    // later save load ran as an online session nobody had chosen (found 2026-11-15).
    // Exception: a guest's checkpoint transfer (SaveSync) restarts the game on purpose to load the host's save.
    // Exception (T-203): a guest putting away its copy of the host's new-game prologue (SharedPrologue) goes back
    // to the title to wait for the host's save; the player leaving from that prologue's pause menu is still a leave.
    static class TitleReturn
    {
        public static int SessionsEnded;
        // Test-only mutation switch: proves the "title" checks fail without this fix.
        static readonly bool s_mutate = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_KEEP_NET") == "1";

        // T-157: the same rule as RoomGame (SaveSync.RestartingForLoad), so a quit after a failed load ends the session.
        static bool TransferRunning { get { return SaveSync.RestartingForLoad; } }

        [HarmonyPatch(typeof(GameController), "RestartGame")]
        static class RestartPatch
        {
            static void Prefix()
            {
                if (!NetPlay.Enabled || TransferRunning || SharedPrologue.OwnRestart || s_mutate) return;
                SessionsEnded++;
                NetPlay.End("returned to the title screen");
            }
        }
    }
}
