using System;
using System.Collections;
using System.IO;
using Game;
using UnityEngine;

namespace OriCoop
{
    public static partial class NativeModeMenu
    {
        partial class TitleJoinTest
        {
            static bool WaitCancelTest { get { return Environment.GetEnvironmentVariable("ORICOOP_TITLEJOIN_WAIT_CANCEL") == "1"; } }

            IEnumerator WaitCancelAndRoomList()
            {
                yield return StartCoroutine(JoinAddress());
                float end = Time.realtimeSinceStartup + 20f;
                while (Time.realtimeSinceStartup < end && SessionScreens.Phase != SessionScreens.SavePhase.Waiting)
                {
                    DismissResume(); yield return null;
                }
                Check(GuestJoin.Waiting && GuestFromTitle && SessionScreens.Phase == SessionScreens.SavePhase.Waiting,
                    "T-211 guest reached the waiting-for-host-save screen while the host stayed in its lobby");
                int loads = GuestJoin.TitleLoads;
                Core.Input.Cancel.Update(false); Core.Input.Cancel.Update(true);
                try { SessionScreens.Tick(); }
                finally { Core.Input.Cancel.Update(false); }
                yield return StartCoroutine(WaitFor(() => !NetPlay.Enabled && !SessionScreens.Blocking && !GuestJoin.Waiting
                    && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu && s_tMain != null && s_tMain.IsActive && !s_tMain.IsLocked, 5f));
                Check(!NetPlay.Enabled && !SessionScreens.Blocking && !GuestJoin.Waiting && GuestJoin.TitleLoads == loads
                    && TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu && s_tMain != null && s_tMain.IsActive && !s_tMain.IsLocked,
                    "T-211 Cancel on the wait screen ends the session and returns to an interactive title without loading (" + GuestStatus + ")");
                // Return the fixture to the title even on the old build, so the list oracle also runs.
                if (NetPlay.Enabled) { NetPlay.End("test wait cleanup"); GuestJoin.Tick(); TitleTick(); }
                yield return StartCoroutine(TWait(1f));
                yield return StartCoroutine(PressCoop());
                yield return StartCoroutine(Row(TitleEntryRow("cserver", false), 0.6f));
                yield return StartCoroutine(Row(ServerModeRoomsRow + 1, 0.6f));
                yield return StartCoroutine(Row(2, 0.6f));
                yield return StartCoroutine(DeadServerList());
                File.WriteAllText(GuestDoneFile, "done");
            }
        }
    }
}
