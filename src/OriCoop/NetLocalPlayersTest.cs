using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
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

        // End of the run, host only. While the guests are still connected the host adds an extra local player and it
        // goes down (online downed). The guests then leave: the room stays open with the host's local seats.
        static int s_hostExtra;
        static IEnumerator HostExtraDownedAtLeave(TestHarness h)
        {
            s_hostExtra = 0;
            GameSeatView view = new GameSeatView();
            h.Check(view.AddLocalPlayer(new InputDevice(InputDevice.DeviceKind.Pad, 3)), "locals: host adds an extra local player before the guests leave");
            float deadline = Time.realtimeSinceStartup + 12f;
            while (s_hostExtra == 0 && Time.realtimeSinceStartup < deadline)
            {
                foreach (int seat in view.LocalSeats) if (seat != Players.LocalSeat && Players.OriOf(seat) != null) s_hostExtra = seat;
                yield return null;
            }
            SeinCharacter extra = s_hostExtra == 0 ? null : Players.OriOf(s_hostExtra);
            h.Check(extra != null, "locals: host's extra local player spawns (seat " + s_hostExtra + ")");
            if (extra == null) { s_hostExtra = 0; yield break; }
            Plugin.Teleport(extra, Own.Position + Vector3.right * 1.5f);
            yield return TestClock.WaitGame(1f);
            extra.Mortality.DamageReciever.OnKill(new Damage(1000, Vector2.zero, extra.Position, DamageType.Spikes, null));
            yield return LocalWait(0.5f);
            h.Check(Revive.DownedAtSeat(s_hostExtra) != null && !extra.gameObject.activeSelf,
                "locals: host's extra is downed while the guests are connected");
        }

        static IEnumerator HostAloneExtra(TestHarness h)
        {
            float deadline = Time.realtimeSinceStartup + 45f;
            while (NetPlay.RemoteCount > 0 && Time.realtimeSinceStartup < deadline) yield return null;
            yield return LocalWait(1f);   // Revive notices the empty room on its next tick
            h.Check(NetPlay.Enabled && NetPlay.IsHost && NetPlay.RemoteCount == 0 && !NetPlay.Connected,
                "locals: host keeps its room open after every guest left (enabled=" + NetPlay.Enabled + ", remotes=" + NetPlay.RemoteCount + ")");
            if (s_hostExtra == 0 || !NetPlay.Enabled) yield break;
            SeinCharacter extra = Players.OriOf(s_hostExtra);
            // T-109: the last guest leaving clears the downed state; the extra must come back with health beside P1.
            h.Check(extra != null && extra.gameObject.activeInHierarchy && !Revive.IsDowned(extra) && extra.Mortality.Health.Amount > 0f
                && Vector3.Distance(extra.Position, Own.Position) < 3f,
                "locals: host's downed extra comes back with health beside P1 when the last guest leaves (health=" +
                (extra == null ? "-" : extra.Mortality.Health.Amount.ToString("F1")) + ")");
            if (extra == null) yield break;
            // T-103: alone in the room, an extra local player's death must not run the game's own death (whole-game reload).
            Plugin.Teleport(extra, Own.Position + Vector3.right * 1.5f);
            yield return TestClock.WaitGame(1f);
            int restores = TestHarness.RestoreCheckpointCalls;
            extra.Mortality.DamageReciever.OnKill(new Damage(1000, Vector2.zero, extra.Position, DamageType.Spikes, null));
            yield return TestClock.WaitGame(3f);
            h.Check(TestHarness.RestoreCheckpointCalls == restores && NetPlay.LocalInGameplay(),
                "locals: host-alone extra death does not reload the game (reloads=" + (TestHarness.RestoreCheckpointCalls - restores) + ")");
            SeinCharacter after = Players.OriOf(s_hostExtra);
            float health = after == null ? -1f : after.Mortality.Health.Amount;
            h.Check(after == extra && after.gameObject.activeInHierarchy && !Revive.IsDowned(after)
                && health >= after.Mortality.Health.MaxHealth - 0.01f && Vector3.Distance(after.Position, Own.Position) < 3f,
                "locals: host-alone extra comes back beside P1 with full health (health=" + health.ToString("F1") + ")");
            new GameSeatView().RemoveLocalPlayer(s_hostExtra);
        }
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

        static IEnumerator LocalExtraSoulLink(TestHarness h)
        {
            bool owner = Players.LocalSeat == 2;
            int notices = PartnerFx.SoulLinksReceived, sent = PartnerFx.SoulLinksSent;
            yield return ThreeRound(h, 603, ClientCount);
            SeinCharacter extra = Players.OriOf(localExtra);
            h.Check(extra != null, "locals: the extra's Ori exists for the soul link");
            if (extra == null) yield break;
            if (owner)
            {
                extra.Energy.RestoreAllEnergy();
                P2Context.Enter(PuppetAt(localExtra));
                try { NetReviveTest.CastSoulLink(extra); }
                finally { P2Context.Exit(); }
                h.Check(NetReviveTest.NativeLinkAt(Own, extra.Position) && extra.SoulFlame.InsideCheckpointMarker,
                    "locals: an extra casts and can use the primary's native link at the extra's spot");
                h.Check(PartnerFx.SoulLinksSent == sent + 1, "locals: the extra sends one cast notice");
            }
            float end = Time.realtimeSinceStartup + 8f;
            while ((Revive.RoomFlamePending || Revive.LinkSeat != localExtra || !NetReviveTest.NativeLinkAt(Own, Revive.LinkPosition))
                && Time.realtimeSinceStartup < end) yield return null;
            yield return TestClock.WaitGame(Time.fixedDeltaTime);
            string flameState;
            bool onlyOne = NetReviveTest.OnlyNativeFlame(Own, out flameState);
            h.Check(Revive.HaveLink && Revive.LinkSeat == localExtra && NetReviveTest.NativeLinkAt(Own, Revive.LinkPosition) && onlyOne,
                "locals: each device has one native flame on its primary Ori, at the extra's accepted save point (seat=" + Revive.LinkSeat
                + ", point=" + Revive.LinkPosition.ToString("F3") + ", " + flameState + ")");
            if (!owner) h.Check(PartnerFx.SoulLinksReceived == notices + 1, "locals: the extra's cast notice arrives");
        }

        // T-202 (#285): the extra local player (a second player on one PC) goes down and holds its own Y: it comes back
        // on its own at the room's save point with full health. Nobody revives it, nobody makes a soul link, no reload.
        static IEnumerator LocalExtraSelfRespawn(TestHarness h)
        {
            bool owner = Players.LocalSeat == 2;
            int restores = TestHarness.RestoreCheckpointCalls, self = Revive.SelfRevives, revives = Revive.Revives;
            yield return TestClock.WaitGame(1.6f);   // the cross-device revive's invincibility runs out
            yield return ThreeRound(h, 604, ClientCount);
            h.Check(Revive.HaveLink, "locals: this device knows the room's save point (T-202)");
            if (owner)
            {
                SeinCharacter extra = Players.OriOf(localExtra);
                h.Check(extra != null, "locals: the extra's Ori exists for its own comeback");
                if (extra == null) yield break;
                extra.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000, Vector2.zero, extra.Position, DamageType.Spikes, null));
            }
            yield return LocalWait(1f);
            h.Check(Revive.DownedAtSeat(localExtra) != null, "locals: the extra is down again on every device (T-202)");
            yield return ThreeRound(h, 605, ClientCount);
            if (owner)
            {
                Revive.TestHoldSelf = true;
                TestClock.Window hold = new TestClock.Window(4f);
                while (Revive.DownedAtSeat(localExtra) != null && hold.Open) yield return null;
                Revive.TestHoldSelf = false;
                SeinCharacter back = Players.OriOf(localExtra);
                float d = back != null ? Vector3.Distance(back.Position, Revive.LinkPosition) : -1f;
                h.Check(Revive.SelfRevives == self + 1 && back != null && back.gameObject.activeInHierarchy && d >= 0f && d < 1.5f,
                    "locals: the extra holds its own Y and comes back at the room's save point (T-202, d=" + F(d) + ")");
                h.Check(back != null && Mathf.Abs(back.Mortality.Health.Amount - back.Mortality.Health.MaxHealth) < 0.01f,
                    "locals: the extra is back with full health (T-202)");
            }
            float deadline = Time.realtimeSinceStartup + 8f;
            while (Revive.DownedAtSeat(localExtra) != null && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Revive.DownedAtSeat(localExtra) == null && Revive.Revives == revives && TestHarness.RestoreCheckpointCalls == restores,
                "locals: the extra's own comeback clears its skull on every device: no teammate revive, no reload (T-202)");
            yield return ThreeRound(h, 606, ClientCount);
        }

        // T-213 (6): the PC with the extra local player loads: here it reports loading and stops sending, as a real load
        // does, and its extra comes back 6 units away. On the other PCs the extra's last position must be forgotten
        // while its PC loads, and afterwards its Ori must appear at the new place without being pulled to the old one
        // (its last frames from before the load must not go out again). Mutations ORICOOP_MUTATE_EXTRA_KEEPS_STREAM=1
        // (the others keep the extra's old frames and position) and ORICOOP_MUTATE_EXTRA_KEEPS_HISTORY=1 (the old
        // frames are sent again) must each fail it.
        static IEnumerator LocalExtraAfterLoad(TestHarness h)
        {
            bool owner = Players.LocalSeat == 2;
            SeinCharacter extra = Players.OriOf(localExtra);
            h.Check(extra != null, "locals: the extra is there before its PC loads (T-213)");
            if (extra == null) yield break;
            Vector3 before = extra.Position, known;
            yield return ThreeRound(h, 610, ClientCount);
            long snaps = NetPlay.TestSnaps(localExtra);
            if (owner) NetPlay.TestReportLoading = true;
            float until = Time.realtimeSinceStartup + 6f;
            bool forgot = owner;
            while (!forgot && Time.realtimeSinceStartup < until) { forgot = !NetPlay.RemotePos(localExtra, out known); if (!forgot) yield return null; }
            if (!owner) h.Check(forgot, "locals: the extra's last position is forgotten while its PC loads (T-213)");
            yield return ThreeRound(h, 611, ClientCount);
            if (owner)
            {
                Plugin.Teleport(Players.OriOf(localExtra), before + Vector3.right * 6f);
                OnlineLocalPlayers.PositionChanged();
                yield return LocalWait(0.5f);
                NetPlay.TestReportLoading = false;
            }
            until = Time.realtimeSinceStartup + 12f;
            while ((Players.Remotes.Count != ClientCount || PuppetAt(localExtra) == null || PuppetAt(localExtra).Sein == null) && Time.realtimeSinceStartup < until) yield return null;
            yield return LocalWait(1f);
            if (!owner)
            {
                Puppet back = PuppetAt(localExtra);
                float moved = back != null && back.Sein != null ? Mathf.Abs(back.Sein.Position.x - before.x) : -1f;
                long pulled = NetPlay.TestSnaps(localExtra) - snaps;
                h.Check(moved > 3f && pulled == 0, "locals: after its PC loaded, the extra appears at its new place and is never pulled to the old one (T-213; moved "
                    + F(moved) + ", pulled " + pulled + " times)");
            }
            yield return ThreeRound(h, 612, ClientCount);
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
            bool extraOwner = Players.LocalSeat == 2;
            Vector3 start = extraPuppet.Sein.Position;
            bool sampled = false;
            try
            {
                if (extraOwner)
                {
                    UI.Menu.ShowAreaMap();
                    P2Input.BotActive = true; P2Input.BotLX = 1;
                }
                for (float end = Time.realtimeSinceStartup + 2f; Time.realtimeSinceStartup < end; )
                {
                    sampled |= extraPuppet.Input.Horizontal > 0.5f;
                    yield return null;
                }
                if (extraOwner) h.Check(GameMapUI.Instance.IsVisible && !Own.Controller.CanMove,
                    "locals: the primary seat has the map open and cannot steer its Ori");
                h.Check(sampled && extraPuppet.Sein.Position.x > start.x + 1f,
                    "locals: extra moves by independent input on every observer while its primary seat views the map");
            }
            finally
            {
                if (extraOwner) { P2Input.BotLX = 0; UI.Menu.HideMenuScreen(true); }
            }
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
            // T-109: a cutscene hands control to another character (P1 unavailable: puppets park) and back (they return
            // beside P1). The downed extra must stay switched off at its skull, not reappear frozen next to P1.
            if (Players.LocalSeat == 2 && localDown != null && Players.OriOf(1) != null)
            {
                ICharacter savedCurrent = Characters.Current;
                Characters.Current = Players.OriOf(1);
                yield return LocalWait(0.3f);
                Characters.Current = savedCurrent;
                yield return LocalWait(0.3f);
                SeinCharacter parkedExtra = Players.OriOf(localExtra);
                h.Check(parkedExtra != null && !parkedExtra.gameObject.activeSelf && Revive.DownedAtSeat(localExtra) == localDown,
                    "locals: P1 parked and back leaves the downed extra switched off at its skull (active=" +
                    (parkedExtra != null && parkedExtra.gameObject.activeSelf) + ")");
            }
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
            // T-114: stones see the extra local player as itself, not P1: standing without grabbing it is not "moving"
            // (that kept the stone's lease forever), and two Oris of this PC on one stone (one pushing, one holding) send
            // one push direction, not one flip per call.
            if (Players.LocalSeat == 2)
            {
                PlatformMovement extraPm = PuppetAt(localExtra).Sein.PlatformBehaviour.PlatformMovement;
                PushBlockSync.St stone = new PushBlockSync.St();
                int sent = PushBlockSync.CoPushSent;
                float t0 = Time.realtimeSinceStartup;
                for (int i = 0; i < 10; i++)
                {
                    PushBlockSync.NoteOwnPush(stone, Players.LocalSeat, 1, t0 + i * 0.005f);
                    PushBlockSync.NoteOwnPush(stone, localExtra, 0, t0 + i * 0.005f + 0.002f);
                }
                bool moving = PushBlockSync.Moving(extraPm);
                int dir = PushBlockSync.PushDir(extraPm), sends = PushBlockSync.CoPushSent - sent;
                h.Check(!moving && dir == 0 && stone.LocalPushDir == 1 && sends == 1,
                    "locals: stones tell the extra local player from P1 (moving=" + moving + ", dir=" + dir +
                    ", PC dir=" + stone.LocalPushDir + ", messages=" + sends + ")");
            }
            yield return LocalExtraSelfRespawn(h);
            // T-108: this device's own Ori goes down while its extra local player is alive: no spectating, which starts
            // only once every player on the PC is down (T-042). Then it comes back (the host revives it).
            int restoresBeforeOwn = TestHarness.RestoreCheckpointCalls;
            if (Players.LocalSeat == 2) Own.Mortality.DamageReciever.OnKill(new Damage(1000, Vector2.zero, Own.Position, DamageType.Spikes, null));
            yield return LocalWait(1f);
            h.Check(Revive.DownedAtSeat(2) != null, "locals: guest's own Ori is downed on every device");
            if (Players.LocalSeat == 2)
                h.Check(!Revive.SpectateActive && Revive.SpectateSein == null,
                    "locals: own Ori down with a living local extra does not spectate (target P" + Revive.SpectateTarget + ")");
            yield return ThreeRound(h, 601, ClientCount);
            if (NetPlay.IsHost)
            {
                Revive.Downed ownDown = Revive.DownedAtSeat(2);
                if (ownDown != null) { Plugin.Teleport(Own, ownDown.Marker); Own.Mortality.Health.RestoreAllHealth(); }
                Bot(0, P2Input.BTN_X);
            }
            deadline = Time.realtimeSinceStartup + 12;
            while (Revive.DownedAtSeat(2) != null && Time.realtimeSinceStartup < deadline) yield return null;
            h.Check(Revive.DownedAtSeat(2) == null && TestHarness.RestoreCheckpointCalls == restoresBeforeOwn,
                "locals: guest's own Ori comes back without a checkpoint reload");
            Bot(0, 0);
            yield return ThreeRound(h, 602, ClientCount);
            if (NetPlay.IsHost) UI.Menu.ShowAreaMap();
            try
            {
                yield return LocalExtraAfterLoad(h);
                if (NetPlay.IsHost) h.Check(GameMapUI.Instance.IsVisible && Players.Remotes.Count == ClientCount,
                    "locals: the host rebuilds the guest's puppets while the map is still open");
            }
            finally { if (NetPlay.IsHost) UI.Menu.HideMenuScreen(true); }
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
            if (NetPlay.IsHost)
            {
                yield return HostExtraDownedAtLeave(h);
                deadline = Time.realtimeSinceStartup + 12f;
                NetPlay.SendTestSignal("done");
                while (localFinishAcks.Count < ClientCount - 1 && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(localFinishAcks.Count == ClientCount - 1, "locals: all devices acknowledge final observations");
                yield return HostAloneExtra(h);
            }
            else
            {
                deadline = Time.realtimeSinceStartup + 30f;   // the host first downs its own extra (HostExtraDownedAtLeave)
                while (!s_done && Time.realtimeSinceStartup < deadline) yield return null;
                h.Check(s_done, "locals: host acknowledges final observations before guest quits");
                NetPlay.SendTestSignal("locals-finished");
                if (NetPlay.Peer != null) NetPlay.Peer.Flush();
            }
            yield return LocalWait(1f);
        }
    }
}
