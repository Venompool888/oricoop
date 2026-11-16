using System;
using System.Collections.Generic;
using HarmonyLib;

namespace OriCoop
{
    // Vanilla bug (not caused by co-op): Steamworks.LeaderboardFindResult indexes a dictionary with the name
    // Steam returns; when the name is unknown it throws KeyNotFoundException. The game re-requests every frame,
    // so the exception + stack trace is logged every frame (300k+ log lines), which slows the game to a crawl.
    // Swallow exactly that exception from exactly that method.
    [HarmonyPatch(typeof(Steamworks), "LeaderboardFindResult")]
    static class SteamLeaderboardFix
    {
        static int s_count;

        static Exception Finalizer(Exception __exception)
        {
            if (__exception is KeyNotFoundException)
            {
                if (s_count++ == 0) Plugin.Log.LogWarning("Suppressing vanilla Steam leaderboard KeyNotFoundException (repeats silently)");
                return null;
            }
            return __exception;
        }
    }
}
