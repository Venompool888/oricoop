using System;
using System.Collections;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    public static partial class NetTestHarness
    {
        static bool localWallFixture;
        static Vector3 localWallAnchor;
        static int localExtra;
        static readonly HashSet<int> localFinishAcks = new HashSet<int>();
        [HarmonyPatch(typeof(GameController), "RestoreCheckpoint")]
        static class LocalCheckpointTrace
        {
            static void Prefix()
            {
                if (TestHarness.Enabled && Environment.GetEnvironmentVariable("ORICOOP_NET_LOCAL_PLAYERS") == "1")
                    Plugin.Log.LogInfo("locals checkpoint restore caller: " + new System.Diagnostics.StackTrace(1, false));
            }
        }
        // Keep the fixture above terrain while leaving the runner's actual sampled horizontal movement intact.
        [HarmonyPatch(typeof(Plugin), "DoLateUpdate")]
        static class LocalWallFixture
        {
            static void Prefix()
            {
                if (!localWallFixture || Players.LocalSeat != 2) return;
                Own.Position = localWallAnchor; Own.Speed = Vector3.zero;
                SeinCharacter extra = Players.OriOf(localExtra);
                if (extra == null) return;
                Vector3 p = extra.Position; p.y = localWallAnchor.y; extra.Position = p;
                Vector3 v = extra.Speed; v.y = 0; extra.Speed = v;
            }
        }
        static IEnumerator LocalWait(float seconds)
        { float until = Time.realtimeSinceStartup + seconds; while (Time.realtimeSinceStartup < until) yield return null; }
        static IEnumerator LocalPlayersChecks(TestHarness h)
        {
            localFinishAcks.Clear();
            try { yield return LocalPlayersCore(h); }
            finally
            {
                P2Input.BotActive = false; P2Input.BotLX = 0; localWallFixture = false; Bot(0, 0);
                if (OnlineRoomScreen.Visible) OnlineRoomScreen.Continue();
            }
        }
        static IEnumerator LocalPlayersCore(TestHarness h)
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            while ((Players.Remotes.Count != ClientCount - 1 || !SaveSync.Ready) && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Players.Remotes.Count == ClientCount - 1 && SaveSync.Ready, "locals: devices ready before add");
            yield return StartBarrier(h);
            Vector3 reviveStart = Own.Position;
            LocalSeatsAdapter view = new LocalSeatsAdapter();
            OnlineRoomScreen.Show(delegate { });
            yield return LocalWait(0.7f);
            if (Players.LocalSeat == 2)
                h.Check(OnlineRoomScreen.Click("room.add"), "locals: guest clicks drawn room add button");
            deadline = Time.realtimeSinceStartup + 15f;
            while (Players.Remotes.Count != ClientCount && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Players.Remotes.Count == ClientCount, "locals: everyone sees all Oris after add");
            if (Players.Remotes.Count != ClientCount) yield break;
            int expectedMask = (1 << (ClientCount + 2)) - 2;
            yield return ThreeRound(h, 590, ClientCount);
            for (int device = 1; device <= ClientCount; device++)
            {
                ThreePacket p = Three(590, device);
                h.Check(p.Unique && p.SeatMask == expectedMask, "locals: unique complete seats on device " + device);
            }
            yield return LocalWait(0.5f);
            h.Check(OnlineRoomScreen.DrawnSeatMask == expectedMask, "locals: room member list updates on every device");
            h.Check(view.LocalSeats.Length == (Players.LocalSeat == 2 ? 2 : 1), "locals: only own seats in settings");
            localExtra = ClientCount + 1;
            h.Check(Array.IndexOf(Story.StoryHost.Current.IntroSeats, localExtra) >= 0, "locals: IntroSeats includes extra local or remote seat");
            yield return ThreeRound(h, 598, ClientCount); // Everyone finishes the three-seat UI observations before host adds P4.
            if (ClientCount == 2 && NetPlay.IsHost) h.Check(OnlineRoomScreen.Click("room.add"), "locals: host adds own extra through room");
            deadline = Time.realtimeSinceStartup + 12;
            while (Players.Remotes.Count != 3 && Time.realtimeSinceStartup < deadline) yield return null;
            yield return ThreeRound(h, 591, ClientCount);
            yield return LocalWait(0.5f);
            h.Check(Players.Remotes.Count == 3 && OnlineRoomScreen.DrawnSeatMask == 30, "locals: full room contains four seats");
            h.Check(!OnlineRoomScreen.DrawnAddEnabled && !OnlineRoomScreen.Click("room.add") && !NetPlay.CanAddLocal, "locals: add button really disabled at four seats");
            yield return ThreeRound(h, 599, ClientCount); // Do not remove P4 while another device still checks the full-room page.
            if (ClientCount == 2 && NetPlay.IsHost) h.Check(OnlineRoomScreen.Click("room.remove.4"), "locals: host removes own extra");
            deadline = Time.realtimeSinceStartup + 12;
            while (Players.Remotes.Count != ClientCount && Time.realtimeSinceStartup < deadline) yield return null;
            yield return ThreeRound(h, 592, ClientCount);
            OnlineRoomScreen.Continue();
            Puppet extraPuppet = PuppetAt(localExtra);
            Vector3 start = extraPuppet.Sein.Position;
            if (Players.LocalSeat == 2) { P2Input.BotActive = true; P2Input.BotLX = 1; }
            yield return LocalWait(2f);
            if (Players.LocalSeat == 2) P2Input.BotLX = 0;
            h.Check(extraPuppet.Sein.Position.x > start.x + 1f, "locals: extra moves by independent input on every observer");
            yield return ThreeRound(h, 593, ClientCount);

            Vector2 now, max;
            bool haveView = CoopCamera.ViewSize(out now, out max);
            h.Check(haveView, "locals: maximum view size available for wall oracle");
            float width = Mathf.Max(4, max.x - 2 * CoopCamera.EdgeMargin);
            h.Note("locals wall width=" + F(width) + " zoom=" + F(CoopCamera.MaxZoomOut));
            int blocks = OnlineLocalPlayers.EdgeBlocks;
            Vector3 hostStart = Own.Position;
            if (Players.LocalSeat == 2)
            {
                localWallAnchor = Own.Position;
                extraPuppet.Sein.Position = localWallAnchor + Vector3.right * (width - 0.5f);
                extraPuppet.Sein.Speed = Vector3.zero;
                OnlineLocalPlayers.PositionChanged(); localWallFixture = true;
                P2Input.BotLX = 1;
            }
            if (NetPlay.IsHost) Bot(1, 0);
            yield return LocalWait(2.5f);
            if (Players.LocalSeat == 2)
            {
                float separation = extraPuppet.Sein.Position.x - Own.Position.x;
                h.Check(separation <= width + 0.15f && separation >= width - 1f && OnlineLocalPlayers.EdgeBlocks > blocks,
                    "locals: own-input runner stops at local screen edge");
                P2Input.BotLX = 0; localWallFixture = false;
            }
            if (NetPlay.IsHost)
                h.Check(Own.Position.x > hostStart.x + 1f && OnlineLocalPlayers.EdgeBlocks == blocks,
                    "locals: host on other device is not constrained by guest wall");
            Bot(0, 0);
            yield return ThreeRound(h, 594, ClientCount);
            // The wall fixture can leave Revive's last grounded marker far from the loaded spawn terrain.
            // Reproduce that retained state before (not after) the isolation step.
            if (Players.LocalSeat == 2 && Environment.GetEnvironmentVariable("ORICOOP_LOCAL_TEST_FAR_MARKER") == "1")
            {
                ((Vector3[])AccessTools.Field(typeof(Revive), "s_safe").GetValue(null))[localExtra] = new Vector3(-253.6f, -305.5f, 0f);
                ((bool[])AccessTools.Field(typeof(Revive), "s_haveSafe").GetValue(null))[localExtra] = true;
            }
            Plugin.Teleport(Own, reviveStart - Vector3.up * 0.5f);
            if (Players.LocalSeat == 2) Plugin.Teleport(Players.OriOf(localExtra), reviveStart + Vector3.right * 1.5f);
            OnlineLocalPlayers.PositionChanged();
            // Let native physics land and TrackSafeGround sample the fresh location before the kill.
            yield return TestClock.WaitGame(1f);
            yield return ThreeRound(h, 600, ClientCount);
            int restoresBeforeRevive = TestHarness.RestoreCheckpointCalls, loadsBeforeRevive = SaveSync.GuestLoads;
            int sentBeforeRevive = Revive.NetRevivesSent, appliedBeforeRevive = Revive.NetRevivesApplied;
            if (Players.LocalSeat == 2)
            {
                extraPuppet = PuppetAt(localExtra);
                // Exercise the actual local-puppet OnKill path, not a direct call to Revive.
                extraPuppet.Sein.Mortality.DamageReciever.OnKill(new Damage(1000, Vector2.zero, extraPuppet.Sein.Position, DamageType.Spikes, null));
            }
            yield return LocalWait(1f);
            h.Check(Revive.DownedAtSeat(localExtra) != null, "locals: local extra death is downed on every device");
            Revive.Downed localDown = Revive.DownedAtSeat(localExtra);
            h.Check(localDown != null && Vector3.Distance(localDown.Marker, reviveStart) < 4f,
                "locals: revive skull uses freshly grounded spawn terrain, not far wall");
            yield return ThreeRound(h, 595, ClientCount);
            if (NetPlay.IsHost)
            {
                Revive.Downed down = Revive.DownedAtSeat(localExtra);
                if (down != null) { Plugin.Teleport(Own, down.Marker); Own.Mortality.Health.RestoreAllHealth(); }
                Bot(0, P2Input.BTN_X);
            }
            deadline = Time.realtimeSinceStartup + 12;
            while (Revive.DownedAtSeat(localExtra) != null && Time.realtimeSinceStartup < deadline) yield return null;
            h.Note("locals revive lifetime cached=" + (extraPuppet.Sein != null) + " current=" + (Players.OriOf(localExtra) != null)
                + " ready=" + SaveSync.Ready + " restores=" + TestHarness.RestoreCheckpointCalls + " loads=" + SaveSync.GuestLoads);
            SeinCharacter revivedExtra = Players.OriOf(localExtra);
            h.Check(Revive.DownedAtSeat(localExtra) == null && revivedExtra != null && revivedExtra.gameObject.activeInHierarchy,
                "locals: cross-device revive restores extra and clears skull everywhere");
            h.Check(SaveSync.Ready && TestHarness.RestoreCheckpointCalls == restoresBeforeRevive && SaveSync.GuestLoads == loadsBeforeRevive,
                "locals: cross-device revive completes without checkpoint recovery or puppet teardown");
            if (NetPlay.IsHost || Players.LocalSeat == 2)
                h.Check(NetPlay.IsHost ? Revive.NetRevivesSent == sentBeforeRevive + 1 : Revive.NetRevivesApplied == appliedBeforeRevive + 1,
                    "locals: cross-device revive is acknowledged through the real revive protocol");
            Bot(0, 0);
            yield return ThreeRound(h, 596, ClientCount);
            OnlineRoomScreen.Show(delegate { }); yield return LocalWait(0.5f);
            if (Players.LocalSeat == 2) h.Check(OnlineRoomScreen.Click("room.remove." + localExtra), "locals: guest clicks drawn remove button");
            deadline = Time.realtimeSinceStartup + 12f;
            while (Players.Remotes.Count != ClientCount - 1 && Time.realtimeSinceStartup < deadline) yield return null;
            yield return LocalWait(0.4f);
            h.Check(Players.Remotes.Count == ClientCount - 1 && OnlineRoomScreen.DrawnSeatMask == (1 << (ClientCount + 1)) - 2,
                "locals: remove updates every member list and puppet");
            yield return ThreeRound(h, 597, ClientCount);
            // Seeing all samples locally is not proof that our final reliable sample was flushed to the host.
            // Keep guests alive until the host explicitly observes that round and acknowledges it.
            deadline = Time.realtimeSinceStartup + 12f;
            if (NetPlay.IsHost)
            {
                NetPlay.SendTestSignal("done");
                while (localFinishAcks.Count < ClientCount - 1 && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(localFinishAcks.Count == ClientCount - 1, "locals: all devices acknowledge final observations");
            }
            else
            {
                while (!s_done && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(s_done, "locals: host acknowledges final observations before guest quits");
                NetPlay.SendTestSignal("locals-finished");
                if (NetPlay.Peer != null) NetPlay.Peer.Flush();
            }
            yield return LocalWait(1f);
        }
    }
}
