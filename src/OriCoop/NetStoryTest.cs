using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // S4 has its own durable, numbered observations; a failed action still publishes its barrier.
    public static class NetStoryTest
    {
        const byte BarrierMessage = 59;
        static readonly MoonGuid FixtureId = new MoonGuid(0x53345445, 0x53545345, 0x5155454e, 0x43453031);
        static readonly MoonGuid OrdinaryId = new MoonGuid(0x53344f52, 0x44494e41, 0x52595345, 0x51303031);
        static readonly Dictionary<int, Sample> Remote = new Dictionary<int, Sample>();
        static readonly Dictionary<int, Sample> Local = new Dictionary<int, Sample>();
        static readonly HashSet<int> Acknowledged = new HashSet<int>();
        static bool s_registered;
        static int s_performs, s_leafCalls, s_immediateCalls, s_delayedCalls, s_ordinaryCalls;
        static ActionSequence s_fixture;
        sealed class Sample
        {
            public int Step, Restores, Loads, Puppets, Performs, Disconnects;
            public bool Failed, Alive, Ready;
            public Vector3 Position;
            public ulong Digest;
            public int Objects;
        }
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        static float Now { get { return Time.realtimeSinceStartup; } }
        static string F(float value) { return value.ToString("F3"); }
        static void Bot(float x) { NetTestHarness.BotLX = x; NetTestHarness.BotButtons = 0; }
        static int Failures(TestHarness h)
        { return ((ICollection)AccessTools.Field(typeof(TestHarness), "m_fails").GetValue(h)).Count; }
        static bool Alive { get { return Own != null && Own.Active && Own.Mortality.Health.Amount > 0; } }
        static IEnumerator Wait(float seconds) { float end = Now + seconds; while (Now < end) yield return null; }
        static void Receive(BinaryReader r)
        {
            byte kind = r.ReadByte();
            if (kind == 1) { Acknowledged.Add(r.ReadInt32()); return; }
            if (kind != 0) throw new InvalidDataException("S4 unknown barrier packet");
            Sample p = new Sample(); p.Step = r.ReadInt32(); p.Failed = r.ReadBoolean();
            p.Restores = r.ReadInt32(); p.Loads = r.ReadInt32(); p.Puppets = r.ReadInt32();
            p.Performs = r.ReadInt32(); p.Disconnects = r.ReadInt32(); p.Alive = r.ReadBoolean(); p.Ready = r.ReadBoolean();
            p.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            p.Digest = r.ReadUInt64(); p.Objects = r.ReadInt32(); Remote[p.Step] = p;
            int step = p.Step;
            NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) { w.Write((byte)1); w.Write(step); });
        }
        static int PuppetCount()
        {
            int count = 0;
            foreach (SeinCharacter sein in Resources.FindObjectsOfTypeAll<SeinCharacter>())
                if (sein != null && sein != Own && sein.gameObject.activeInHierarchy
                    && sein.GetComponent<SeinController>() != null) count++;
            return count;
        }
        static void Publish(TestHarness h, int step, bool failed)
        {
            Sample p = new Sample(); p.Step = step; p.Failed = failed;
            try
            {
                p.Restores = TestHarness.RestoreCheckpointCalls; p.Loads = SaveSync.GuestLoads;
                p.Alive = Alive; p.Ready = SaveSync.Ready; p.Position = Own == null ? Vector3.zero : Own.Position;
                p.Puppets = PuppetCount(); p.Performs = s_performs; p.Disconnects = SessionRules.Disconnects;
                p.Digest = WorldSync.Digest(out p.Objects);
            }
            catch (Exception e) { p.Failed = true; h.Check(false, "S4 capture " + step + ": " + e.Message); }
            Local[step] = p; Send(p);
        }
        static void Send(Sample p)
        {
            NetPlay.Send(BarrierMessage, true, delegate(BinaryWriter w) {
                w.Write((byte)0);
                w.Write(p.Step); w.Write(p.Failed); w.Write(p.Restores); w.Write(p.Loads); w.Write(p.Puppets);
                w.Write(p.Performs); w.Write(p.Disconnects); w.Write(p.Alive); w.Write(p.Ready);
                w.Write(p.Position.x); w.Write(p.Position.y); w.Write(p.Position.z); w.Write(p.Digest); w.Write(p.Objects);
            });
        }
        static IEnumerator Barrier(TestHarness h, int step)
        {
            if (!Local.ContainsKey(step)) Publish(h, step, false);
            float end = Now + 20f, resend = Now + 1f;
            while ((!Remote.ContainsKey(step) || !Acknowledged.Contains(step)) && Now < end)
            {
                if (NetPlay.Connected && Now >= resend) { Send(Local[step]); resend = Now + 1f; }
                yield return null;
            }
            h.Check(Remote.ContainsKey(step), "S4 barrier " + step + " received");
            h.Check(Acknowledged.Contains(step), "S4 barrier " + step + " own observation acknowledged");
            if (Remote.ContainsKey(step)) h.Check(!Remote[step].Failed, "S4 partner step " + step + " completed");
            // Send our queued ACK before the caller can enable packet loss or tear gameplay down.
            if (NetPlay.Connected) NetPlay.Peer.Flush();
            yield return Wait(0.25f);
        }
        static void Register()
        {
            if (s_registered) return;
            s_registered = true; NetPlay.On(BarrierMessage, Receive);
        }
        public static IEnumerator PlaceAtStart(TestHarness h, Vector3 phaseAStart, int step)
        {
            Register(); Bot(0);
            Sample anchor = new Sample(); anchor.Step = step; anchor.Position = phaseAStart;
            Local[step] = anchor; Send(anchor); yield return Barrier(h, step);
            Vector3 position = NetPlay.IsHost ? phaseAStart : Remote.ContainsKey(step) ? Remote[step].Position + Vector3.right * 1.5f : phaseAStart;
            Plugin.Teleport(Own, position); SessionRules.PositionChanged();
            h.Note("S4/R clean placement step=" + step + " position=" + Own.Position);
            yield return Wait(1.5f); yield return Barrier(h, step + 1);
        }
        // Flatten child iterators here, so exceptions do not skip subsequent S4 steps.
        static IEnumerator Step(TestHarness h, int step, IEnumerator routine)
        {
            bool failed = false; int failures = Failures(h);
            Stack<IEnumerator> stack = new Stack<IEnumerator>(); stack.Push(routine);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator top = stack.Peek(); bool moved = false; object value = null;
                    try { moved = top.MoveNext(); if (moved) value = top.Current; }
                    catch (Exception e) { failed = true; h.Check(false, "S4 step " + step + " threw " + e); }
                    if (!moved)
                    {
                        stack.Pop(); IDisposable d = top as IDisposable;
                        if (d != null) { try { d.Dispose(); } catch (Exception e) { failed = true; h.Check(false, "S4 cleanup " + e.Message); } }
                    }
                    else { IEnumerator child = value as IEnumerator; if (child == null) yield return value; else stack.Push(child); }
                }
            }
            finally
            {
                try
                {
                    while (stack.Count > 0)
                    {
                        IDisposable d = stack.Pop() as IDisposable;
                        if (d != null) try { d.Dispose(); } catch (Exception e) { failed = true; h.Check(false, "S4 dispose " + e.Message); }
                    }
                }
                finally { Bot(0); Publish(h, step, failed || Failures(h) > failures); }
            }
            yield return Barrier(h, step);
        }
        static Dictionary<ISuspendable, int> SuspensionCounts()
        {
            return new Dictionary<ISuspendable, int>((Dictionary<ISuspendable, int>)
                AccessTools.Field(typeof(SuspensionManager), "s_suspended").GetValue(null));
        }
        static bool AnySuspended(Dictionary<ISuspendable, int> counts)
        {
            foreach (int count in counts.Values) if (count != 0) return true;
            return false;
        }
        static string Counts(Dictionary<ISuspendable, int> counts)
        {
            SortedDictionary<int, int> histogram = new SortedDictionary<int, int>();
            foreach (int count in counts.Values) { if (!histogram.ContainsKey(count)) histogram[count] = 0; histogram[count]++; }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<int, int> pair in histogram) parts.Add(pair.Key + ":" + pair.Value);
            return counts.Count + " objects [refCount:objects " + string.Join(",", parts.ToArray()) + "]";
        }
        static IEnumerator Menu(TestHarness h)
        {
            // T-073: on a busy PC the previous step's suspension (death / reload fade) could still hold when this step
            // started, so "before" caught 3157 objects at refCount 1 and the check failed although the pause restored
            // everything (net-relay, 2026-11-18). Every normal run starts at refCount 0: wait for that. The phase before
            // ends with R3's checkpoint reload, whose fades run in game time, so wait up to 8 GAME seconds (T-071): at a
            // forced 15 fps 5 real seconds were not enough. Still suspended after that = stuck, and the check fails.
            TestClock.Window settle = new TestClock.Window(8f);
            while (settle.Open && AnySuspended(SuspensionCounts())) yield return null;
            if (settle.RealElapsed > 0.1f)
                h.Note("S4 waited " + F(settle.GameElapsed) + " game s (" + F(settle.RealElapsed) + " real) for earlier suspensions to end ("
                    + Counts(SuspensionCounts()) + ")");
            Dictionary<ISuspendable, int> before = SuspensionCounts(); bool guest = !NetPlay.IsHost;
            Vector3 ownStart = Vector3.zero, puppetStart = Vector3.zero; long frames = 0;
            try
            {
                bool opened = !guest;
                try
                {
                    if (guest) AccessTools.Method(typeof(MenuScreenManager), "ShowMenuScreen",
                        new Type[] { typeof(MenuScreenManager.Screens), typeof(bool) }).Invoke(UI.Menu,
                        new object[] { MenuScreenManager.Screens.Pause, false });
                    opened = true;
                }
                finally { Publish(h, 11, !opened); }
                yield return Barrier(h, 11); yield return Wait(0.4f);
                ownStart = Own.Position; puppetStart = P2Context.P2.Position; frames = NetPlay.FramesPlayed;
                int disconnects = SessionRules.Disconnects;
                // T-044: online nothing pauses. The guest's pause menu holds only the guest's own Ori; the world keeps
                // running on both PCs and the host keeps playing. Hold it longer than NetPeer's 5 s timeout.
                // ORICOOP_S4_SKEW (seconds, host only) stretches the host's window to reproduce the race below on
                // purpose: the two windows start a few ms apart (each side starts when it sees barrier 11).
                float skew = 0f; if (!guest) float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_S4_SKEW") ?? "",
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out skew);
                Bot(1); float maxOwn = 0, maxPuppet = 0, start = Now; int suspendedFrames = 0, sampled = 0;
                while (Now - start < 6f + skew)
                {
                    maxOwn = Mathf.Max(maxOwn, Vector3.Distance(ownStart, Own.Position));
                    maxPuppet = Mathf.Max(maxPuppet, Vector3.Distance(puppetStart, P2Context.P2.Position));
                    sampled++; if (Paused(SuspensionCounts())) suspendedFrames++;
                    yield return null;
                }
                h.Note("S4 menu seconds=" + F(Now - start) + " ownMax=" + F(maxOwn) + " puppetMax=" + F(maxPuppet)
                    + " frames=" + (NetPlay.FramesPlayed - frames) + " remoteMenu=" + SessionRules.PartnerInMenu
                    + " pausedByPartner=" + SessionRules.PausedByPartner + " localPaused=" + SessionRules.LocalPaused);
                h.Note("S4 menu suspendedFrames=" + suspendedFrames + "/" + sampled + " skippedMenus=" + SessionRules.SkippedMenuSuspends);
                h.Check(suspendedFrames == 0, "S4 no pause: the game is not paused on this PC while the guest is in the menu");
                h.Check(!SessionRules.LocalPaused && !SessionRules.PausedByPartner && !SessionRules.PartnerInMenu, "S4 no pause: no pause reported or applied");
                if (guest)
                {
                    h.Check(maxOwn < 0.3f, "S4 no pause: the guest's own Ori is held while its menu is open");
                    h.Check(maxPuppet > 2f, "S4 no pause: the host keeps playing on the guest's screen");
                    h.Check(SessionRules.LocalLocked && SessionRules.SkippedMenuSuspends > 0, "S4 no pause: the menu skipped its suspend and locks own input");
                    h.Check(NetPlay.FramesPlayed - frames > 30, "S4 guest receives frames while its menu is open");
                }
                else
                {
                    h.Check(maxOwn > 2f, "S4 no pause: the host keeps playing while the guest is in the menu");
                    h.Check(maxPuppet < 0.3f, "S4 no pause: the guest's Ori stays still on the host's screen");
                }
                h.Check(NetPlay.Connected && SessionRules.Disconnects == disconnects, "S4 session survives a 6 s pause");
                // The guest keeps its menu open until both sides have checked. Without this the guest's window,
                // which can end a few ms earlier, closed the menu and its "resumed" message reached the host just
                // before the host's checks ("S4 partner pause reliable status received" flake, 2026-11-16).
                // Test-only mutation switch: ORICOOP_S4_NO_HOLD=1 skips this hold (with ORICOOP_S4_SKEW=0.3 the host's
                // pause checks then fail every time, which proves the hold is what makes them pass).
                if (Environment.GetEnvironmentVariable("ORICOOP_S4_NO_HOLD") != "1") { Publish(h, 12, false); yield return Barrier(h, 12); }
            }
            finally { Bot(0); if (guest && UI.Menu != null) UI.Menu.HideMenuScreen(true); }
            yield return Wait(0.5f);
            h.Check(!SessionRules.PausedByPartner && !SessionRules.LocalPaused && !SessionRules.LocalLocked, "S4 menu closed: nothing paused or locked");
            Dictionary<ISuspendable, int> after = SuspensionCounts(); int compared = 0, changed = 0;
            foreach (KeyValuePair<ISuspendable, int> pair in before)
            { int count; if (after.TryGetValue(pair.Key, out count)) { compared++; if (count != pair.Value) changed++; } }
            h.Note("S4 suspension before=" + Counts(before) + " after=" + Counts(after) + " common=" + compared + " changed=" + changed);
            h.Check(compared > 0 && changed == 0, "S4 surviving suspendables retain exact reference counts");
            // The host walked right through the whole menu step: at full game speed (a lightly loaded PC, ~330 frames)
            // it reached a wall 57.9 to the right and could not move right any more (ownDx=0, T-083). It walks back
            // left, the way it came; the guest stood still and walks right as before.
            ownStart = Own.Position; puppetStart = P2Context.P2.Position; Bot(guest ? 1 : -1); yield return Wait(1f); Bot(0);
            h.Note("S4 closed menu ownDx=" + F(Vector3.Distance(Own.Position, ownStart))
                + " puppetDx=" + F(Vector3.Distance(P2Context.P2.Position, puppetStart)));
            h.Check(Vector3.Distance(Own.Position, ownStart) > 0.5f && Vector3.Distance(P2Context.P2.Position, puppetStart) > 0.5f,
                "S4 both move after closing menu");
        }
        // T-044: the world map and an ability help popup do not pause online either. The guest opens them while both
        // walk right: nothing is suspended on either PC, the guest's Ori is held while a screen is open, the host walks on.
        static ShowHelpTextAction FindHelp()
        {
            foreach (ShowHelpTextAction a in Resources.FindObjectsOfTypeAll<ShowHelpTextAction>())
                if (a != null && a.HelpMessage != null && a.Avatar != null && a.gameObject.scene.IsValid()) return a;
            foreach (ShowHelpTextAction a in Resources.FindObjectsOfTypeAll<ShowHelpTextAction>())
                if (a != null && a.HelpMessage != null && a.Avatar != null) return a;
            return null;
        }
        static Vector3 s_runStart;
        // A pause suspends (nearly) everything; single objects the game suspends itself out of view
        // (SuspendWhenOutOfFrustrum, liana physics, force appliers) are not a pause.
        static bool Paused(Dictionary<ISuspendable, int> counts)
        {
            int suspended = 0; foreach (int n in counts.Values) if (n > 0) suspended++;
            return counts.Count > 0 && suspended * 4 > counts.Count;
        }
        // Which kinds of objects are suspended (top types), for the failure note.
        static string SuspendedKinds()
        {
            Dictionary<string, int> kinds = new Dictionary<string, int>();
            foreach (KeyValuePair<ISuspendable, int> pair in SuspensionCounts())
            {
                if (pair.Value <= 0) continue;
                Component c = pair.Key as Component;
                string name = pair.Key.GetType().Name + (c != null ? "@" + c.gameObject.name : "");
                int n; kinds.TryGetValue(name, out n); kinds[name] = n + 1;
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> pair in kinds) { parts.Add(pair.Key + "x" + pair.Value); if (parts.Count >= 8) break; }
            return kinds.Count + " kinds: " + string.Join(", ", parts.ToArray());
        }
        static IEnumerator NoPauseScreens(TestHarness h)
        {
            bool guest = !NetPlay.IsHost;
            // Back to the start of S4 (the menu / focus steps walked ~50 units right, into story / tutorial areas).
            yield return PlaceAtStart(h, s_runStart, 180);
            yield return Barrier(h, 18);
            string firstSuspended = "";
            int menus = SessionRules.SkippedMenuSuspends, popups = SessionRules.SkippedPopupSuspends, suspendedFrames = 0;
            float mapOwn = -1f, popupOwn = -1f, maxPuppet = 0f, hostTravel = 0f;
            bool mapShown = false, popupShown = false, popupClosed = false; string popupName = "none";
            Vector3 puppetStart = P2Context.P2.Position, hostStart = Own.Position;
            try
            {
                for (int phase = 0; phase < 2; phase++)
                {
                    Bot(phase == 0 ? 1 : -1);
                    if (guest && phase == 0)
                    {
                        AccessTools.Method(typeof(MenuScreenManager), "ShowMenuScreen", new Type[] { typeof(MenuScreenManager.Screens), typeof(bool) })
                            .Invoke(UI.Menu, new object[] { MenuScreenManager.Screens.WorldMap, false });
                        yield return null; mapShown = UI.Menu.MainMenuVisible;
                    }
                    if (guest && phase == 1)
                    {
                        UI.Menu.HideMenuScreen(true); yield return Wait(0.3f);
                        ShowHelpTextAction help = FindHelp();
                        if (help != null) { popupName = help.name; help.Perform(null); }
                        yield return null; popupShown = SessionRules.PopupOpen;
                    }
                    Vector3 anchor = Own.Position; float own = 0f, start = Now;
                    while (Now - start < 3f)
                    {
                        own = Mathf.Max(own, Vector3.Distance(anchor, Own.Position));
                        maxPuppet = Mathf.Max(maxPuppet, Vector3.Distance(puppetStart, P2Context.P2.Position));
                        hostTravel = Mathf.Max(hostTravel, Vector3.Distance(hostStart, Own.Position));
                        if (Paused(SuspensionCounts())) { suspendedFrames++; if (firstSuspended == "") firstSuspended = "phase" + phase + " " + SuspendedKinds(); }
                        yield return null;
                    }
                    if (phase == 0) mapOwn = own; else popupOwn = own;
                }
                if (guest)
                {
                    SessionRules.HideSkippedPopups();
                    float end = Now + 4f;
                    while (SessionRules.PopupOpen && Now < end) yield return null;
                    popupClosed = !SessionRules.PopupOpen;
                }
            }
            finally { Bot(0); if (guest && UI.Menu != null && UI.Menu.MainMenuVisible) UI.Menu.HideMenuScreen(true); }
            h.Note("S4 screens map=" + mapShown + " mapOwn=" + F(mapOwn) + " popup=" + popupName + "/" + popupShown + " popupOwn=" + F(popupOwn)
                + " closed=" + popupClosed + " puppetMax=" + F(maxPuppet) + " ownTravel=" + F(hostTravel) + " suspendedFrames=" + suspendedFrames
                + " skippedMenus=" + (SessionRules.SkippedMenuSuspends - menus) + " skippedPopups=" + (SessionRules.SkippedPopupSuspends - popups)
                + (firstSuspended == "" ? "" : " firstSuspended=" + firstSuspended));
            h.Check(suspendedFrames == 0, "S4 no pause: the game is not paused on this PC while the guest has the map / a help popup open");
            if (guest)
            {
                h.Check(mapShown && SessionRules.SkippedMenuSuspends > menus, "S4 no pause: the world map opens without suspending");
                h.Check(mapOwn >= 0f && mapOwn < 0.3f, "S4 no pause: own Ori held while the map is open");
                h.Check(popupShown && SessionRules.SkippedPopupSuspends > popups, "S4 no pause: an ability help popup opens without suspending");
                h.Check(popupOwn >= 0f && popupOwn < 0.3f, "S4 no pause: own Ori held while the help popup is open");
                h.Check(popupClosed && !SessionRules.LocalLocked, "S4 no pause: closing the popup frees own input");
                h.Check(maxPuppet > 2f, "S4 no pause: the host keeps playing on the guest's screen");
            }
            else h.Check(hostTravel > 2f, "S4 no pause: the host keeps playing while the guest has the map / a popup open");
            Publish(h, 19, false); yield return Barrier(h, 19);
            // Leave both players together at the S4 start, standing: the guest-death step respawns the guest beside the
            // host's last known position (this step left them ~25 units apart, the host possibly still falling).
            yield return PlaceAtStart(h, s_runStart, 184);
        }
        // Losing window focus online is not a pause: the game and the network keep running.
        static IEnumerator Focus(TestHarness h)
        {
            bool guest = !NetPlay.IsHost; int disconnects = SessionRules.Disconnects;
            int suppressed = SessionScreens.SuppressedResumeScreens;
            // T-044: the menu step no longer freezes the host, who walked ~45 units right into a wall; start again.
            yield return PlaceAtStart(h, s_runStart, 182);
            yield return Barrier(h, 16);
            Vector3 ownStart = Own.Position, puppetStart = P2Context.P2.Position;
            try
            {
                if (guest)
                {
                    Application.runInBackground = false;   // what vanilla does two frames after focus loss
                    GameController.Instance.SendMessage("OnApplicationFocus", false);
                    UI.Menu.ShowResumeScreen();             // vanilla's focus-loss / resolution-change pause
                }
                yield return Wait(0.3f);
                if (guest)
                {
                    h.Check(Application.runInBackground, "S4 focus: runInBackground stays on while unfocused");
                    h.Check(!GameController.IsFocused, "S4 focus: focus loss observed");
                    h.Check(!UI.Menu.ResumeScreenVisible && SessionScreens.SuppressedResumeScreens > suppressed, "S4 focus: no resume screen online");
                    h.Check(!SessionRules.LocalPaused && !SessionRules.LocalLocked, "S4 focus: losing focus is not a pause");
                }
                Bot(1); float maxOwn = 0, maxPuppet = 0, start = Now;
                while (Now - start < 6f)
                {
                    maxOwn = Mathf.Max(maxOwn, Vector3.Distance(ownStart, Own.Position));
                    maxPuppet = Mathf.Max(maxPuppet, Vector3.Distance(puppetStart, P2Context.P2.Position));
                    yield return null;
                }
                Bot(0);
                h.Note("S4 focus ownMax=" + F(maxOwn) + " puppetMax=" + F(maxPuppet) + " focused=" + GameController.IsFocused
                    + " runInBackground=" + Application.runInBackground + " suppressed=" + (SessionScreens.SuppressedResumeScreens - suppressed));
                h.Check(maxOwn > 2f && maxPuppet > 2f, "S4 focus: both keep playing while the guest is unfocused");
                h.Check(NetPlay.Connected && SessionRules.Disconnects == disconnects, "S4 focus: session survives 6 s unfocused");
                h.Check(!SessionRules.PausedByPartner && !SessionRules.PartnerInMenu, "S4 focus: an unfocused partner does not pause this game");
            }
            finally { Bot(0); if (guest && GameController.Instance != null) GameController.Instance.SendMessage("OnApplicationFocus", true); }
            yield return Wait(0.2f);
            if (guest) h.Check(GameController.IsFocused, "S4 focus: focus regained");
        }
        static void Kill()
        {
            Own.Mortality.DamageReciever.IsImmortal = false;
            Own.Mortality.DamageReciever.ResetInviciblity();
            Own.Mortality.DamageReciever.OnRecieveDamage(new Damage(1000f, Vector2.zero, Own.Position, DamageType.Spikes, null));
        }
        static IEnumerator GuestDeath(TestHarness h)
        {
            yield return Barrier(h, 21); int restores = TestHarness.RestoreCheckpointCalls;
            float start = Now; if (!NetPlay.IsHost) Kill();
            yield return Wait(0.1f);
            while ((!Alive || (!NetPlay.IsHost && SessionRules.Respawning)) && Now - start < 3f) yield return null;
            yield return Wait(0.25f);
            float distance = Vector3.Distance(Own.Position, NetPlay.LatestRemotePos);
            h.Note("S4 guest death elapsed=" + F(Now - start) + " restores=" + (TestHarness.RestoreCheckpointCalls - restores)
                + " health=" + Own.Mortality.Health.Amount + " distance=" + F(distance));
            if (!NetPlay.IsHost)
            {
                h.Check(TestHarness.RestoreCheckpointCalls == restores, "S4 guest death no restore");
                h.Check(Alive && !SessionRules.Respawning && Now - start <= 3f && distance <= 3f, "S4 guest alive beside host within 3s");
                h.Check(Own.Mortality.Health.Amount >= Own.Mortality.Health.MaxHealth, "S4 guest full health");
            }
        }
        static IEnumerator HostDeath(TestHarness h)
        {
            yield return Barrier(h, 31); int restores = TestHarness.RestoreCheckpointCalls, loads = SaveSync.GuestLoads;
            float start = Now; if (NetPlay.IsHost) Kill();
            while (Now - start < 10f)
            {
                bool completed = NetPlay.IsHost ? TestHarness.RestoreCheckpointCalls > restores : SaveSync.GuestLoads > loads && SaveSync.JoinedHost;
                if (completed && Alive && SaveSync.Ready && P2Context.P2 != null && !GameController.Instance.IsLoadingGame) break;
                yield return null;
            }
            h.Note("S4 host death elapsed=" + F(Now - start) + " restoreDelta=" + (TestHarness.RestoreCheckpointCalls - restores)
                + " loadDelta=" + (SaveSync.GuestLoads - loads) + " joinDistance=" + F(SaveSync.GuestJoinDistance));
            h.Check(Alive && SaveSync.Ready && Now - start < 10f, "S4 both restarted within 10s");
            if (NetPlay.IsHost) h.Check(TestHarness.RestoreCheckpointCalls == restores + 1, "S4 host exactly one restore");
            else h.Check(SaveSync.GuestLoads > loads && SaveSync.JoinedHost && SaveSync.GuestJoinDistance <= 3f, "S4 guest save push reloaded and placed");
            yield return Barrier(h, 32);
            if (Remote.ContainsKey(32)) h.Check(Remote[32].Alive && Vector3.Distance(Own.Position, Remote[32].Position) <= 3f,
                "S4 both alive and distance=" + F(Vector3.Distance(Own.Position, Remote[32].Position)));
            yield return Digests(h, 33);
        }
        public class NoStoryAction : ActionMethod
        {
            public bool Delayed;
            public override void Perform(IContext context)
            { s_leafCalls++; if (Delayed) s_delayedCalls++; else s_immediateCalls++; }
        }
        [HarmonyPatch(typeof(ActionSequence), "Perform")]
        static class CountFixture
        {
            static void Postfix(ActionSequence __instance)
            { if (__instance.MoonGuid == FixtureId) s_performs++; if (__instance.MoonGuid == OrdinaryId) s_ordinaryCalls++; }
        }
        static IEnumerator Story(TestHarness h)
        {
            GameObject go = new GameObject("OriCoop S4 harmless sequence");
            s_fixture = go.AddComponent<ActionSequence>(); s_fixture.MoonGuid = new MoonGuid(FixtureId);
            GameObject ordinaryObject = new GameObject("OriCoop S4 ordinary level sequence");
            ActionSequence ordinary = ordinaryObject.AddComponent<ActionSequence>(); ordinary.MoonGuid = new MoonGuid(OrdinaryId);
            ordinary.Actions.Add(ordinaryObject.AddComponent<NoStoryAction>());
            GameObject child = new GameObject("S4 nested sequence"); child.transform.parent = go.transform;
            ActionSequence nested = child.AddComponent<ActionSequence>();
            nested.MoonGuid = new MoonGuid(FixtureId); nested.MoonGuid.D++;
            LockPlayerInputAction storyLock = child.AddComponent<LockPlayerInputAction>();
            storyLock.DurationPlayerCantMove = 0.4f;
            GameObject lockObject = new GameObject("S4 nested lock sequence"); lockObject.transform.parent = go.transform;
            ActionSequence lockSequence = lockObject.AddComponent<ActionSequence>();
            lockSequence.MoonGuid = new MoonGuid(FixtureId); lockSequence.MoonGuid.D += 3;
            lockSequence.Actions.Add(storyLock); s_fixture.Actions.Add(lockSequence);
            StopActionAction stopOnly = ordinaryObject.AddComponent<StopActionAction>(); stopOnly.SequenceToRun = storyLock;
            ordinary.Actions.Add(stopOnly);
            nested.Actions.Add(child.AddComponent<NoStoryAction>()); s_fixture.Actions.Add(nested);
            WaitAction wait = go.AddComponent<WaitAction>(); wait.Duration = 0.2f; s_fixture.Actions.Add(wait);
            GameObject delayed = new GameObject("S4 delayed nested sequence"); delayed.transform.parent = go.transform;
            ActionSequence later = delayed.AddComponent<ActionSequence>(); later.MoonGuid = new MoonGuid(FixtureId); later.MoonGuid.D += 2;
            NoStoryAction laterAction = delayed.AddComponent<NoStoryAction>(); laterAction.Delayed = true;
            later.Actions.Add(laterAction); later.Actions.Add(nested); s_fixture.Actions.Add(later);
            try
            {
                h.Check(StorySync.IsStorySequence(s_fixture), "S4 recursive classifier accepts player-lock fixture");
                h.Check(!StorySync.IsStorySequence(ordinary), "S4 classifier rejects ordinary / stop-only sequence");
                int guarded = SessionRules.GuardedShaderWrites;
                UberShaderAPI.SetColorCustom(null, Color.white, "_Color", false);
                MeshRenderer missingMaterial = ordinaryObject.AddComponent<MeshRenderer>();
                UberShaderAPI.SetColorCustom(missingMaterial, Color.white, "_Color", false);
                h.Check(SessionRules.GuardedShaderWrites == guarded + 2, "S4 null renderer and null material writes guarded");
                h.Note("S4 story uses runtime nested sequence with 0.4s input lock guid=" + FixtureId + "; ordinary=" + OrdinaryId);
                int ordinaryCalls = s_ordinaryCalls;
                yield return Wait(0.6f); yield return Barrier(h, 42);
                if (NetPlay.IsHost) ordinary.Perform(null);
                yield return Wait(0.8f);
                h.Note("S4 ordinary performDelta=" + (s_ordinaryCalls - ordinaryCalls) + " watching=" + StorySync.Watching);
                h.Check(s_ordinaryCalls - ordinaryCalls == (NetPlay.IsHost ? 1 : 0), "S4 ordinary sequence is not mirrored");
                h.Check(!StorySync.Watching, "S4 ordinary sequence cannot enter watch state");
                if (!NetPlay.IsHost) Plugin.Teleport(Own, NetPlay.LatestRemotePos + Vector3.right * 20f);
                yield return Wait(0.2f);
                int calls = s_performs, leaves = s_leafCalls, immediate = s_immediateCalls, laterCalls = s_delayedCalls;
                yield return Barrier(h, 41); float start = Now;
                if (NetPlay.IsHost) s_fixture.Perform(null);
                while (s_performs == calls && Now - start < 1f) yield return null;
                float distance = Vector3.Distance(Own.Position, NetPlay.LatestRemotePos);
                h.Note("S4 story calls=" + (s_performs - calls) + " leafCalls=" + (s_leafCalls - leaves)
                    + " latency=" + F(Now - start) + " distance=" + F(distance));
                h.Check(s_performs == calls + 1 && Now - start < 1f, "S4 sequence performed once within 1s");
                h.Check(s_immediateCalls >= immediate + 1 && s_immediateCalls <= immediate + 2, "S4 first nested leaf performed");
                if (!NetPlay.IsHost) h.Check(distance <= 12f, "S4 remote story brings guest within 12");
                yield return Wait(0.8f);
                h.Note("S4 nested immediate+delayed leaves=" + (s_leafCalls - leaves));
                h.Check(s_leafCalls == leaves + 3 && s_immediateCalls == immediate + 2 && s_delayedCalls == laterCalls + 1,
                    "S4 repeated nested reference performs twice; delayed leaf once");
            }
            finally { UnityEngine.Object.Destroy(go); UnityEngine.Object.Destroy(ordinaryObject); s_fixture = null; }
        }
        static IEnumerator Digests(TestHarness h, int step)
        {
            yield return Wait(1f); WorldSync.SendDigest(); yield return Wait(0.5f); yield return Barrier(h, step);
            if (!Remote.ContainsKey(step)) yield break;
            Sample a = Local[step], b = Remote[step];
            h.Note("S4 world local=" + a.Digest.ToString("X16") + "/" + a.Objects + " remote=" + b.Digest.ToString("X16") + "/" + b.Objects);
            h.Check(a.Digest == b.Digest && a.Objects == b.Objects, "S4 world digests match");
        }
        static IEnumerator Reconnect(TestHarness h)
        {
            yield return Barrier(h, 51); int disconnects = SessionRules.Disconnects, sessions = SessionRules.Sessions;
            float start = Now;
            try
            {
                if (NetPlay.IsHost) NetPlay.SetSim(0, 0, 100, 0);
                // Longer than NetPeer.TimeoutMs (10 s since lag test ②) so the link really times out.
                yield return Wait(12f);
            }
            finally { if (NetPlay.IsHost) NetPlay.SetSim(0, 0, 0, 0); }
            float clearAt = Now;
            // Timeline after the link is back (T-030): when each rejoin condition first holds, seconds after clearAt.
            float tConn = -1f, tSession = -1f, tReady = -1f, tPuppet = -1f, tJoined = -1f, tDisc = -1f;
            int discAt0 = SessionRules.Disconnects;
            while (Now - clearAt < 15f && (!NetPlay.Connected || SessionRules.Sessions == sessions
                || !SaveSync.Ready || P2Context.P2 == null || (!NetPlay.IsHost && !SaveSync.JoinedHost)))
            {
                float t = Now - clearAt;
                if (tDisc < 0f && SessionRules.Disconnects > discAt0) tDisc = t;
                if (tConn < 0f && NetPlay.Connected) tConn = t;
                if (tSession < 0f && SessionRules.Sessions > sessions) tSession = t;
                if (tReady < 0f && SaveSync.Ready) tReady = t;
                if (tPuppet < 0f && P2Context.P2 != null) tPuppet = t;
                if (tJoined < 0f && SaveSync.JoinedHost) tJoined = t;
                yield return null;
            }
            h.Note("S4 rejoin timeline after clear: disconnected=" + F(tDisc) + " connected=" + F(tConn) + " session=" + F(tSession)
                + " saveReady=" + F(tReady) + " puppet=" + F(tPuppet) + " joinedHost=" + F(tJoined) + " (-1 = already true or never)"
                + " disconnectsDuringDrop=" + (discAt0 - disconnects));
            int puppets = PuppetCount(); float distance = Vector3.Distance(Own.Position, NetPlay.LatestRemotePos);
            h.Note("S4 reconnect total=" + F(Now - start) + " afterClear=" + F(Now - clearAt)
                + " disconnectDelta=" + (SessionRules.Disconnects - disconnects) + " sessionDelta=" + (SessionRules.Sessions - sessions)
                + " puppets=" + puppets + " distance=" + F(distance) + " " + NetPlay.StatusLine());
            h.Check(SessionRules.Disconnects > disconnects, "S4 observed Disconnected event");
            h.Check(NetPlay.Connected && SessionRules.Sessions > sessions && SaveSync.Ready && Now - clearAt < 15f, "S4 rejoined within 15s");
            // T-030: once the link is back, the rejoin (connect, session, save ready, guest placed) takes seconds, not 12 s.
            h.Check(NetPlay.Connected && SessionRules.Sessions > sessions && SaveSync.Ready && Now - clearAt < 5f,
                "S4 rejoined within 5 s after the link is back (" + F(Now - clearAt) + " s)");
            h.Check(puppets == 1 && P2Context.P2 != null, "S4 exactly one puppet");
            if (!NetPlay.IsHost) h.Check(SaveSync.JoinedHost && distance <= 3f, "S4 guest re-placed beside host");
            h.Check(!SessionRules.PartnerInMenu && !StorySync.Watching, "S4 no stale remote menu or watching lock");
            yield return Digests(h, 52);
        }
        // ORICOOP_NET_ONLY=S4R: just the drop + rejoin step (fast iteration on T-030).
        public static IEnumerator RunReconnect(TestHarness h)
        {
            h.Note("--- phase S4 reconnect only");
            yield return Step(h, 50, Reconnect(h));
        }

        public static IEnumerator Run(TestHarness h)
        {
            h.Note("--- phase S4 story / session rules"); s_performs = 0; s_runStart = Own != null ? Own.Position : Vector3.zero;
            Register(); bool revive = Revive.Enabled;
            bool immortal = Own != null && Own.Mortality.DamageReciever.IsImmortal;
            try
            {
                // Exercise only the normal death path after R has tested downed/revive.
                Revive.Enabled = false; Bot(0); yield return Barrier(h, 1);
                yield return Step(h, 10, Menu(h));
                yield return Step(h, 15, Focus(h));
                yield return Step(h, 17, NoPauseScreens(h));
                yield return Step(h, 20, GuestDeath(h));
                yield return Step(h, 30, HostDeath(h));
                yield return Step(h, 40, Story(h));
                yield return Step(h, 50, Reconnect(h));
            }
            finally
            {
                Revive.Enabled = revive; Bot(0); NetPlay.SetSim(0, 0, 0, 0);
                if (Own != null) Own.Mortality.DamageReciever.IsImmortal = immortal;
            }
        }
    }
}
