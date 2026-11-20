using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
            NetPlay.TestSignalReceived += LocalsSignal;
            try { yield return LocalPlayersCore(h); }
            finally
            {
                NetPlay.TestSignalReceived -= LocalsSignal;
                P2Input.BotActive = false; P2Input.BotLX = 0; localWallFixture = false; Bot(0, 0);
                if (OnlineRoomScreen.Visible) OnlineRoomScreen.Continue();
            }
        }
        // T-116: the extra local player (seat localExtra, on the device with seat 2) has a bottom status bar on every
        // device: its own device reads its Ori, the others get its Vitals (message 79). Its damage reaches every bar (the
        // host's within 1 s of the owner's report), and its hurt effect is sent by its device and played on the others'.
        static float s_extraHp = -1f, s_extraHpAt = -1f;
        static void LocalsSignal(int seat, string signal)
        {
            float v;
            if (signal.StartsWith("locals-hp:", StringComparison.Ordinal)
                && float.TryParse(signal.Substring(10), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            { s_extraHp = v; s_extraHpAt = Time.realtimeSinceStartup; }
        }

        static IEnumerator LocalExtraBars(TestHarness h)
        {
            bool owner = Players.LocalSeat == 2;
            float deadline = Time.realtimeSinceStartup + 5f;
            while (!BottomHud.Visible(localExtra) && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(BottomHud.Visible(localExtra), "locals: extra P" + localExtra + " has a bottom status bar on "
                + (owner ? "its own device" : "this device") + " (T-116)");
            int hurtSent = PartnerFx.HurtSent, hurtGot = PartnerFx.HurtReceived, hurtPlayed = PartnerFx.HurtPlayed;
            s_extraHp = -1f; s_extraHpAt = -1f;
            yield return ThreeRound(h, 601, ClientCount);   // every device has its counters before the extra is hurt
            float bh, bmh, be, bme;
            if (owner)
            {
                SeinCharacter extra = Players.OriOf(localExtra);
                h.Check(extra != null, "locals: the extra's Ori exists on its own device");
                if (extra == null) yield break;
                extra.Mortality.Health.RestoreAllHealth();
                float h0 = extra.Mortality.Health.Amount;
                extra.Mortality.DamageReciever.OnRecieveDamage(new Damage(3f, Vector2.zero, extra.Position, DamageType.Spikes, null));
                float hp = extra.Mortality.Health.Amount;
                NetPlay.SendTestSignal("locals-hp:" + hp.ToString("R", CultureInfo.InvariantCulture));
                h.Check(hp < h0, "locals: test damage reached the extra (" + F(h0) + " -> " + F(hp) + ")");
                h.Check(PartnerFx.HurtSent == hurtSent + 1, "locals: the extra's hurt effect is sent to the other devices ("
                    + (PartnerFx.HurtSent - hurtSent) + ")");
                float took = -1f, t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup < t0 + 3f)
                {
                    if (BottomHud.TryShown(localExtra, out bh, out bmh, out be, out bme) && Mathf.Abs(bh - hp) < 0.01f) { took = Time.realtimeSinceStartup - t0; break; }
                    yield return null;
                }
                h.Check(took >= 0f && took <= 1f, "locals: the extra's own device shows its damage on its bar within 1 s (" + F(took) + " s)");
            }
            else
            {
                deadline = Time.realtimeSinceStartup + 5f;
                if (NetPlay.IsHost) while (s_extraHpAt < 0f && Time.realtimeSinceStartup < deadline) yield return null;
                if (NetPlay.IsHost) h.Check(s_extraHpAt >= 0f, "locals: the extra's device reported its damage (" + F(s_extraHp) + ")");
                float took = -1f, t0 = NetPlay.IsHost && s_extraHpAt >= 0f ? s_extraHpAt : Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup < t0 + 3f)
                {
                    bool shown = BottomHud.TryShown(localExtra, out bh, out bmh, out be, out bme);
                    if (shown && (NetPlay.IsHost ? Mathf.Abs(bh - s_extraHp) < 0.01f : bh < bmh - 0.01f)) { took = Time.realtimeSinceStartup - t0; break; }
                    yield return null;
                }
                h.Check(took >= 0f && took <= (NetPlay.IsHost ? 1f : 3f), "locals: the extra's damage reaches this device's bar for it"
                    + (NetPlay.IsHost ? " within 1 s" : "") + " (" + F(took) + " s)");
                deadline = Time.realtimeSinceStartup + 3f;
                while (PartnerFx.HurtPlayed == hurtPlayed && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(PartnerFx.HurtReceived > hurtGot && PartnerFx.HurtPlayed > hurtPlayed, "locals: the extra's hurt effect plays on this device's puppet (received "
                    + (PartnerFx.HurtReceived - hurtGot) + ", played " + (PartnerFx.HurtPlayed - hurtPlayed) + ")");
            }
        }

        // T-138: the extra local player's soul link (the save flame; T-029's message 80) appears beside it on every
        // device. Its own device casts it the way the extra's own update does: in the extra's context.
        static readonly FieldInfo s_extraFlameField = typeof(SeinSoulFlame).GetField("m_soulFlame", BindingFlags.Instance | BindingFlags.NonPublic);
        static SoulFlame FlameOf(SeinCharacter s) { return s == null || s.SoulFlame == null ? null : s_extraFlameField.GetValue(s.SoulFlame) as SoulFlame; }

        static IEnumerator LocalExtraSoulLink(TestHarness h)
        {
            bool owner = Players.LocalSeat == 2;
            SoulFlame before = FlameOf(Players.OriOf(localExtra));
            int sent = PartnerFx.SoulLinksSent, shown = PartnerFx.SoulLinksShown;
            yield return ThreeRound(h, 603, ClientCount);   // every device has its "before" flame
            SeinCharacter extra = Players.OriOf(localExtra);
            h.Check(extra != null, "locals: the extra's Ori exists for the soul link");
            if (extra == null) yield break;
            if (owner)
            {
                Puppet puppet = PuppetAt(localExtra);
                P2Context.Enter(puppet);
                try { typeof(SeinSoulFlame).GetMethod("CastSoulFlame", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(extra.SoulFlame, null); }
                finally { P2Context.Exit(); }
                SoulFlame mine = FlameOf(extra);
                h.Check(mine != null && mine != before && Vector3.Distance(mine.Position, extra.Position) < 0.5f,
                    "locals: the extra casts a soul link at its own position (T-138)");
                h.Check(PartnerFx.SoulLinksSent == sent + 1, "locals: the extra's soul link is sent to the other devices ("
                    + (PartnerFx.SoulLinksSent - sent) + ")");
            }
            else
            {
                float end = Time.realtimeSinceStartup + 5f; SoulFlame seen = null;
                while (Time.realtimeSinceStartup < end) { seen = FlameOf(extra); if (seen != null && seen != before) break; yield return null; }
                bool appeared = seen != null && seen != before;
                float d = appeared ? Vector3.Distance(seen.Position, extra.Position) : -1f;
                h.Check(appeared && seen.gameObject.activeInHierarchy && d < 2f && PartnerFx.SoulLinksShown > shown,
                    "locals: the extra's soul link flame appears beside it on this device (T-138, " + (appeared ? F(d) : "none") + ")");
            }
        }

        static IEnumerator LocalPlayersCore(TestHarness h)
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            while ((Players.Remotes.Count != ClientCount - 1 || !SaveSync.Ready) && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Players.Remotes.Count == ClientCount - 1 && SaveSync.Ready, "locals: devices ready before add");
            yield return StartBarrier(h);
            Vector3 reviveStart = Own.Position;
            GameSeatView view = new GameSeatView();
            OnlineRoomScreen.Show(delegate { });
            yield return LocalWait(0.7f);
            if (Players.LocalSeat == 2)
                h.Check(OnlineRoomScreen.Click("room.add"), "locals: guest clicks drawn room add button");
            deadline = Time.realtimeSinceStartup + 15f;
            while (Players.Remotes.Count != ClientCount && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Players.Remotes.Count == ClientCount, "locals: everyone sees all Oris after add");
            if (Players.Remotes.Count != ClientCount) yield break;
            // T-056: the live seat view holds the ISeatView invariants on every device with extra local players.
            List<string> seatProblems = Seats.SeatViewChecks.Problems(view, true);
            h.Check(seatProblems.Count == 0, "seats: live seat view consistent (" + string.Join("; ", seatProblems.ToArray()) + ")");
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
            yield return LocalExtraBars(h);
            yield return LocalExtraSoulLink(h);
            yield return ThreeRound(h, 602, ClientCount);

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
            int downSent = PartnerFx.DownSent, downLocal = PartnerFx.LocalDownPlayed, downGot = PartnerFx.DownReceived, downPlayed = PartnerFx.DownPlayed;
            yield return ThreeRound(h, 600, ClientCount);
            int restoresBeforeRevive = TestHarness.RestoreCheckpointCalls, loadsBeforeRevive = SaveSync.GuestLoads;
            int sentBeforeRevive = Revive.NetRevivesSent, appliedBeforeRevive = Revive.NetRevivesApplied;
            if (Players.LocalSeat == 2)
            {
                extraPuppet = PuppetAt(localExtra);
                // Exercise the game's own damage path on the local puppet (it reaches OnKill itself, as spikes do), not a
                // direct call to Revive; T-116: that path also plays and sends the death burst.
                extraPuppet.Sein.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000, Vector2.zero, extraPuppet.Sein.Position, DamageType.Spikes, null));
            }
            yield return LocalWait(1f);
            h.Check(Revive.DownedAtSeat(localExtra) != null, "locals: local extra death is downed on every device");
            Revive.Downed localDown = Revive.DownedAtSeat(localExtra);
            h.Check(localDown != null && Vector3.Distance(localDown.Marker, reviveStart) < 4f,
                "locals: revive skull uses freshly grounded spawn terrain, not far wall");
            // T-116: the extra's death burst plays on its own device and on the others' puppet; its bar stays, dimmed.
            if (Players.LocalSeat == 2)
                h.Check(PartnerFx.LocalDownPlayed == downLocal + 1 && PartnerFx.DownSent == downSent + 1,
                    "locals: the extra's death burst plays on its own device and is sent to the others (played "
                    + (PartnerFx.LocalDownPlayed - downLocal) + ", sent " + (PartnerFx.DownSent - downSent) + ")");
            else
            {
                float until = Time.realtimeSinceStartup + 2f;
                while (PartnerFx.DownPlayed == downPlayed && Time.realtimeSinceStartup < until) yield return null;
                h.Check(PartnerFx.DownReceived > downGot && PartnerFx.DownPlayed > downPlayed,
                    "locals: the extra's death burst plays on this device's puppet (received " + (PartnerFx.DownReceived - downGot)
                    + ", played " + (PartnerFx.DownPlayed - downPlayed) + ")");
            }
            h.Check(BottomHud.Visible(localExtra) && BottomHud.Opacity(localExtra) < 0.6f,
                "locals: the downed extra's bar stays, dimmed, on every device (opacity " + F(BottomHud.Opacity(localExtra)) + ")");
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
