using System;
using System.Collections;
using HarmonyLib;
using OriCoop.Authority;
using UnityEngine;

namespace OriCoop
{
    internal static class NetSaveHandoverTest
    {
        internal static readonly bool Enabled = TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_TEST_SAVE_HANDOVER") == "1";
        const string EarlyHello = "handover-early-hello", StillWaiting = "handover-waiting", Received = "handover-restored";
        static bool s_earlyHello, s_waiting, s_restored;

        // Schedule only the old host's first transfers. The receivers process the real Hello, checkpoint,
        // native restore, arbiter change and Loaded messages with the normal game handlers.
        [HarmonyPatch(typeof(SaveSync), "SendCheckpoint")]
        static class OldHostTransfers
        {
            static bool Prepare() { return Enabled; }
            static bool Prefix(int seat) { return Players.LocalSeat != 1 || seat == 2 && s_earlyHello; }
        }

        static void Signal(int from, string message)
        {
            if (from == 2 && message == EarlyHello) s_earlyHello = true;
            if (from == 3 && message == StillWaiting) s_waiting = true;
            if (from == 3 && message == Received) s_restored = true;
        }

        static IEnumerator Until(Func<bool> done, float seconds, string send = null)
        {
            float end = Time.realtimeSinceStartup + seconds, again = 0f;
            while (!done() && Time.realtimeSinceStartup < end)
            {
                if (send != null && Time.realtimeSinceStartup >= again)
                { NetPlay.SendTestSignal(send); again = Time.realtimeSinceStartup + 0.5f; }
                yield return null;
            }
        }

        internal static IEnumerator Run(TestHarness h)
        {
            NetPlay.TestSignalReceived += Signal;
            try
            {
                yield return Until(() => CServerLink.Client != null && CServerLink.Client.PrimarySeat > 0, 90f);
                if (CServerLink.Client == null || CServerLink.Client.PrimarySeat == 0)
                { h.Check(false, "handover local C-server assigned a seat"); yield break; }
                int seat = CServerLink.Client.PrimarySeat;
                if (seat == 1)
                {
                    yield return Until(() => s_earlyHello && s_waiting && SaveSync.ReadyForSeat(2), 90f);
                    h.Check(s_earlyHello && s_waiting && SaveSync.CheckpointsSent == 1 && SaveSync.ReadyForSeat(2) && !SaveSync.ReadyForSeat(3),
                        "handover P1 leaves after P2 loaded and before sending P3 its first save (sent=" + SaveSync.CheckpointsSent + ")");
                    CServerLink.Client.ReleaseAll();
                    yield return Until(() => NetRoute.HostSeat == 2, 10f);
                    h.Check(NetRoute.HostSeat == 2, "handover C-server elected P2");
                }
                else if (seat == 2)
                {
                    yield return Until(() => NetPlay.RemoteInSession(3), 90f);
                    bool early = NetPlay.RemoteInSession(3) && SaveSync.GuestLoads == 0 && !SaveSync.Ready && !SaveSync.HavePendingCheckpoint;
                    h.Check(early, "handover P2 received P3 Hello before P2 received or loaded its checkpoint");
                    if (!early) yield break;
                    yield return Until(() => NetPlay.IsHost, 90f, EarlyHello);
                    h.Check(NetPlay.IsHost && SaveSync.GuestLoads == 1, "handover P2 loaded the old host's save before becoming host");
                    yield return Until(() => SaveSync.CheckpointsSent == 1 && SaveSync.ReadyForSeat(3) && s_restored, 45f);
                    h.Check(SaveSync.CheckpointsSent == 1 && SaveSync.ReadyForSeat(3),
                        "handover P2 sent its first checkpoint to waiting P3 and received its load acknowledgement (sent=" + SaveSync.CheckpointsSent + ")");
                    h.Check(s_restored, "handover P3 confirmed receipt of the new host's snapshots");
                }
                else if (seat == 3)
                {
                    h.Check(NetRoute.HostSeat == 1 && SaveSync.GuestLoads == 0 && !SaveSync.Ready,
                        "handover P3 entered before the old host left and is still waiting for its first save");
                    int snapshots = WorldSync.SnapshotsEnded;
                    yield return Until(() => NetRoute.HostSeat == 2, 90f, StillWaiting);
                    yield return Until(() => SaveSync.Ready && SaveSync.GuestLoads == 1 && ProgressSync.SnapshotGuestLoad == 1
                        && WorldSync.SnapshotsEnded > snapshots, 40f);
                    bool restored = NetRoute.HostSeat == 2 && SaveSync.GuestLoads == 1 && SaveSync.Ready;
                    h.Check(restored, "handover P3 restored exactly one checkpoint from P2 (loads=" + SaveSync.GuestLoads + ")");
                    bool snapshotsDone = restored && ProgressSync.SnapshotGuestLoad == 1 && WorldSync.SnapshotsEnded > snapshots;
                    h.Check(snapshotsDone, "handover P3 applied the new host's world and progress snapshots");
                    if (snapshotsDone) NetPlay.SendTestSignal(Received);
                    yield return Until(() => !NetPlay.RemoteInSession(2), 6f);
                }
                else h.Check(false, "handover requires three distinct devices");
            }
            finally { NetPlay.TestSignalReceived -= Signal; }
        }
    }
}
