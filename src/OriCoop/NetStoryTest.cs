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
        static readonly MoonGuid WellId = new MoonGuid(0x53345745, 0x4c4c5341, 0x56455345, 0x51303031);
        static readonly Dictionary<int, Sample> Remote = new Dictionary<int, Sample>();
        static readonly Dictionary<int, Sample> Local = new Dictionary<int, Sample>();
        static readonly HashSet<int> Acknowledged = new HashSet<int>();
        static bool s_registered;
        static int s_performs, s_leafCalls, s_immediateCalls, s_delayedCalls, s_ordinaryCalls, s_wellCalls;
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
        static void Bot(float x, int buttons = 0) { NetTestHarness.BotLX = x; NetTestHarness.BotButtons = buttons; }
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
            // Both snapshots precede opening either menu. T-044 skips online suspension, but keep the fence:
            // the pause mutation / offline debt paths must not capture an already-open menu as the baseline.
            yield return Barrier(h, 13);
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
                // Keep the menu open longer than NetPeer's 5 s timeout.
                // ORICOOP_S4_SKEW (seconds, host only) stretches the host's window to reproduce the race below on
                // purpose: the two windows start a few ms apart (each side starts when it sees barrier 11).
                float skew = 0f; if (!guest) float.TryParse(Environment.GetEnvironmentVariable("ORICOOP_S4_SKEW") ?? "",
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out skew);
                Bot(1); float maxOwn = 0, maxPuppet = 0, start = Now; int suspendedFrames = 0, sampled = 0;
                while (Now - start < 6f + skew)
                {
                    maxOwn = Mathf.Max(maxOwn, Mathf.Abs(ownStart.x - Own.Position.x));
                    maxPuppet = Mathf.Max(maxPuppet, Mathf.Abs(puppetStart.x - P2Context.P2.Position.x));
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
                    h.Check(maxOwn < 0.3f, "S4 no pause: the guest's menu doesn't steer its Ori");
                    h.Check(maxPuppet > 2f, "S4 no pause: the host keeps playing on the guest's screen");
                    h.Check(SessionRules.LiveScreen && !SessionRules.LocalLocked && !Own.Controller.CanMove
                        && SessionRules.SkippedMenuSuspends > 0, "S4 no pause: the menu skipped its suspend and blocks only the guest's input");
                    h.Check(NetPlay.FramesPlayed - frames > 30, "S4 guest receives frames while its menu is open");
                }
                else
                {
                    h.Check(maxOwn > 2f, "S4 no pause: the host keeps playing while the guest is in the menu");
                    h.Check(maxPuppet < 0.3f && Players.First.ScreenInputLocked, "S4 no pause: the guest sends neutral input while its menu is open");
                }
                h.Check(NetPlay.Connected && SessionRules.Disconnects == disconnects, "S4 session survives a 6 s pause");
                // Keep the menu open until both sides have sampled it.
                if (Environment.GetEnvironmentVariable("ORICOOP_S4_NO_HOLD") != "1") { Publish(h, 12, false); yield return Barrier(h, 12); }
                // A teleport during the menu must still stick.
                if (guest)
                {
                    Vector3 target = Own.Position + Vector3.left * 3f;
                    Plugin.Teleport(Own, target);
                    yield return Wait(1.5f);
                    float dx = Mathf.Abs(Own.Position.x - target.x), dy = Own.Position.y - target.y;
                    bool ground = Own.PlatformBehaviour.PlatformMovement.IsOnGround;
                    h.Note("S4 menu teleport dx=" + F(dx) + " dy=" + F(dy) + " ground=" + ground);
                    h.Check(dx < 0.3f && Mathf.Abs(dy) < 2.5f, "S4 a teleport while the menu is open sticks (T-112)");
                    h.Check(ground && SessionRules.LiveScreen, "S4 Ori lands after a teleport while the menu stays open");
                }
                // (13 is the fence before the menu opens; reusing it let the host run ahead of the guest.)
                Publish(h, 14, false); yield return Barrier(h, 14);
            }
            finally { Bot(0); if (guest && UI.Menu != null) UI.Menu.HideMenuScreen(true); }
            yield return Wait(0.5f);
            h.Check(!SessionRules.PausedByPartner && !SessionRules.LocalPaused && !SessionRules.LocalLocked && !SessionRules.LiveScreen,
                "S4 menu closed: nothing paused or locked");
            // T-132: online the F8 panel must not write back the LockInput it saw when it opened. A story box that lets go
            // while the panel is open stays released; one that takes the lock while it is open keeps it after closing.
            if (guest && GameController.Instance != null)
            {
                GameController gc = GameController.Instance;
                gc.LockInput = true; CoopMenu.Open(); yield return null;
                bool openLive = CoopMenu.IsOpen && SessionRules.LiveScreen && !SessionRules.LocalLocked && !CoopMenu.FrozeGame;
                gc.LockInput = false; yield return null; CoopMenu.Close(); yield return null;
                bool released = !gc.LockInput;
                gc.LockInput = false; CoopMenu.Open(); yield return null; gc.LockInput = true; yield return null; CoopMenu.Close(); yield return null;
                bool kept = gc.LockInput;
                gc.LockInput = false;
                h.Check(openLive, "S4 F8 online: the open panel keeps this player live");
                h.Check(released, "S4 F8 online: a story lock released while the panel was open stays released (T-132)");
                h.Check(kept, "S4 F8 online: a story lock taken while the panel was open survives closing it (T-132)");
            }
            // T-179: the B that closes a menu must not also be a Soul Flame tap that reopens the skill tree.
            // It takes the guest ~3 s; both walk together again afterwards ("S4 both move after closing menu").
            if (guest) yield return MenuSoulFlameTest.SkillTreeShortB(h, Own);
            Publish(h, 25, false); yield return Barrier(h, 25);
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
                + " puppetDx=" + F(Vector3.Distance(P2Context.P2.Position, puppetStart))
                + " ownStart=" + ownStart + " ownNow=" + Own.Position + " speed=" + Own.Speed
                + " wallLeft=" + Own.PlatformBehaviour.PlatformMovement.HasWallLeft + " wallRight=" + Own.PlatformBehaviour.PlatformMovement.HasWallRight
                + " suspended=" + Own.PlatformBehaviour.PlatformMovement.IsSuspended + " canMove=" + Own.Controller.CanMove
                + " input=" + Core.Input.HorizontalAnalogLeft + " localLocked=" + SessionRules.LocalLocked
                + " gameLocked=" + GameController.Instance.InputLocked);
            h.Check(Vector3.Distance(Own.Position, ownStart) > 0.5f && Vector3.Distance(P2Context.P2.Position, puppetStart) > 0.5f,
                "S4 both move after closing menu");
        }
        static ShowHelpTextAction FindHelp()
        {
            foreach (ShowHelpTextAction a in Resources.FindObjectsOfTypeAll<ShowHelpTextAction>())
                if (a != null && a.HelpMessage != null && a.Avatar != null && a.gameObject.scene.IsValid()) return a;
            foreach (ShowHelpTextAction a in Resources.FindObjectsOfTypeAll<ShowHelpTextAction>())
                if (a != null && a.HelpMessage != null && a.Avatar != null) return a;
            return null;
        }
        internal static bool TryOpenHelp()
        {
            ShowHelpTextAction help = FindHelp();
            if (help == null) return false;
            help.Perform(null);
            return SessionRules.PopupOpen;
        }
        static Vector3 s_runStart;
        // A pause suspends (nearly) everything; single objects the game suspends itself out of view
        // (SuspendWhenOutOfFrustrum, liana physics, force appliers) are not a pause.
        static bool Paused(Dictionary<ISuspendable, int> counts)
        {
            int suspended = 0; foreach (int n in counts.Values) if (n > 0) suspended++;
            return counts.Count > 0 && suspended * 4 > counts.Count;
        }
        static IEnumerator Map(TestHarness h)
        {
            bool host = NetPlay.IsHost;
            SeinCharacter ori = Own;
            SeinDamageReciever receiver = ori.Mortality.DamageReciever;
            bool immortal = receiver.IsImmortal, revive = Revive.Enabled;
            float health = ori.Mortality.Health.Amount;
            yield return Barrier(h, 18);
            float peak = host ? ori.Position.y : P2Context.P2.Position.y;
            float sentPeak = NetPlay.LatestRemotePos.y, drop = 0f, sentDrop = 0f, scrollStart = 0f;
            long frames = NetPlay.FramesPlayed;
            try
            {
                Revive.Enabled = true;
                if (host)
                {
                    yield return Wait(0.5f); // Let the guest start sampling before the fall.
                    int skipped = SessionRules.SkippedMenuSuspends;
                    ori.Mortality.Health.RestoreAllHealth();
                    receiver.IsImmortal = false;
                    receiver.ResetInviciblity();
                    ori.Position += Vector3.up * 5f;
                    ori.Speed = Vector3.down * 2f;
                    UI.Menu.ShowAreaMap();
                    h.Check(GameMapUI.Instance.IsVisible && GameMapTransitionManager.Instance.InAreaMapMode
                        && SessionRules.SkippedMenuSuspends > skipped && ori.Speed.y < -1f, "S4 map: P1 opens the area map without suspending or zeroing speed");
                    scrollStart = AreaMapUI.Instance.Navigation.ScrollPosition.x;
                    Bot(1);
                }
                bool running = true, accelerated = false;
                for (float end = Now + 3f; Now < end; )
                {
                    if (host) accelerated |= ori.Speed.y < -3f;
                    float y = host ? ori.Position.y : P2Context.P2.Position.y;
                    peak = Mathf.Max(peak, y); drop = Mathf.Max(drop, peak - y);
                    float sentY = NetPlay.LatestRemotePos.y;
                    sentPeak = Mathf.Max(sentPeak, sentY); sentDrop = Mathf.Max(sentDrop, sentPeak - sentY);
                    running &= Time.timeScale > 0f && !Own.IsSuspended && !Paused(SuspensionCounts());
                    yield return null;
                }
                h.Check(running, "S4 map: gameplay keeps running while P1 has the map open");
                if (host)
                {
                    h.Check(GameMapUI.Instance.IsVisible && accelerated && drop > 2f && ori.PlatformBehaviour.LeftRightMovement.HorizontalInput == 0f,
                        "S4 map: gravity accelerates P1's fall while map navigation does not steer Ori (drop=" + F(drop) + ")");
                    h.Check(Mathf.Abs(AreaMapUI.Instance.Navigation.ScrollPosition.x - scrollStart) > 1f,
                        "S4 map: the stick pans the area map");
                    h.Check(!GameController.Instance.InputLocked, "S4 map: global input lock stays off");
                    Bot(0); NetTestHarness.BotLT = true;
                    TestClock.Window zoom = new TestClock.Window(2.5f);
                    while (!GameMapTransitionManager.Instance.InWorldMapMode && zoom.Open) yield return null;
                    NetTestHarness.BotLT = false;
                    h.Check(GameMapUI.Instance.IsVisible && GameMapTransitionManager.Instance.InWorldMapMode,
                        "S4 map: the zoom-out input switches to the world map");
                    float before = ori.Mortality.Health.Amount;
                    receiver.OnRecieveDamage(new Damage(4f, Vector2.right, ori.Position, DamageType.Enemy, null));
                    h.Check(GameMapUI.Instance.IsVisible && ori.Mortality.Health.Amount < before,
                        "S4 map: an enemy hit damages P1 in the world map");
                    Bot(0, P2Input.BTN_BACK); yield return TestClock.WaitGame(0.2f);
                    Bot(1); yield return TestClock.WaitGame(0.3f); Bot(0);
                    h.Check(!GameMapUI.Instance.IsVisible && ori.PlatformBehaviour.LeftRightMovement.HorizontalInput > 0f,
                        "S4 map: the map button closes it and restores P1's movement input");
                    yield return OpenMapWhileAiming(h, ori);
                    UI.Menu.HideMenuScreen(true);
                    yield return DownedInMap(h, ori, receiver);
                }
                else h.Check(drop > 2f && sentDrop > 2f && NetPlay.FramesPlayed > frames,
                    "S4 map: P2 receives P1's falling positions and sees the puppet fall (sent=" + F(sentDrop) + ", puppet=" + F(drop) + ")");
                yield return Barrier(h, 186);
            }
            finally
            {
                Bot(0); NetTestHarness.BotLT = false;
                Revive.Enabled = revive;
                if (Revive.AnyDowned) Revive.ClearAll("S4 map test cleanup", false);
                if (host)
                {
                    if (GameMapUI.Instance.IsVisible) UI.Menu.HideMenuScreen(true);
                    receiver.IsImmortal = immortal;
                    ori.Mortality.Health.SetAmount(health);
                }
            }
        }
        static IEnumerator OpenMapWhileAiming(TestHarness h, SeinCharacter ori)
        {
            bool hadGrenade = ori.PlayerAbilities.Grenade.HasAbility;
            float energy = ori.Energy.Current, cap = ori.Energy.Max;
            if (!hadGrenade) ori.PlayerAbilities.SetAbility(AbilityType.Grenade, true);
            SeinGrenadeAttack grenade = ori.Abilities.Grenade;
            try
            {
                ori.Energy.Max = Mathf.Max(cap, grenade.EnergyCost);
                ori.Energy.RestoreAllEnergy();
                TestClock.Window settle = new TestClock.Window(2f);
                while (!grenade.CanAim && settle.Open) yield return null;
                Bot(0, P2Input.BTN_LB);
                TestClock.Window aim = new TestClock.Window(1f);
                while (!grenade.IsAiming && aim.Open) yield return null;
                h.Check(grenade.IsAiming, "S4 map: P1 is aiming a grenade with LB held");
                UI.Menu.ShowWorldMap(false);
                yield return TestClock.WaitGame(0.2f);
                Bot(0); yield return TestClock.WaitGame(0.2f);
                h.Check(GameMapUI.Instance.IsVisible && !grenade.IsAiming && Mathf.Approximately(ori.Energy.Current, ori.Energy.Max),
                    "S4 map: opening it cancels the grenade and refunds its cost instead of throwing it");
            }
            finally
            {
                Bot(0); grenade.CancelAiming();
                ori.Energy.Max = cap; ori.Energy.SetCurrent(energy);
                if (!hadGrenade) ori.PlayerAbilities.SetAbility(AbilityType.Grenade, false);
            }
        }
        static IEnumerator DownedInMap(TestHarness h, SeinCharacter ori, SeinDamageReciever receiver)
        {
            bool hadCharge = ori.PlayerAbilities.ChargeFlame.HasAbility;
            float energy = ori.Energy.Current, cap = ori.Energy.Max;
            if (!hadCharge) ori.PlayerAbilities.SetAbility(AbilityType.ChargeFlame, true);
            SeinChargeFlameAbility flame = ori.Abilities.ChargeFlame;
            try
            {
                ori.Energy.Max = Mathf.Max(cap, flame.EnergyCost);
                ori.Energy.RestoreAllEnergy();
                Bot(0, P2Input.BTN_X);
                TestClock.Window charge = new TestClock.Window(flame.ChargeDuration + 1.5f);
                while (Mathf.Approximately(ori.Energy.Current, ori.Energy.Max) && charge.Open) yield return null;
                h.Check(flame.IsCharging && ori.Energy.Current < ori.Energy.Max, "S4 map: P1 has fully charged Flame and spent energy");

                int self = Revive.SelfRevives, restores = TestHarness.RestoreCheckpointCalls;
                Revive.LastSelfHint = null;
                UI.Menu.ShowWorldMap(false);
                h.Check(GameMapUI.Instance.IsVisible, "S4 map: world map open before the fatal hit");
                receiver.OnRecieveDamage(new Damage(1000f, Vector2.zero, ori.Position, DamageType.Enemy, null));
                h.Check(Revive.IsDowned(ori) && !GameMapUI.Instance.IsVisible && !UI.Menu.MainMenuVisible,
                    "S4 map: the fatal hit downs P1 and closes the map");
                h.Check(!flame.IsCharging && Mathf.Approximately(ori.Energy.Current, ori.Energy.Max),
                    "S4 map: opening the map cancels and refunds Charge Flame before the same-frame fatal hit");
                yield return SelfRevive(h, "S4 map: ", ori, self, restores);
            }
            finally
            {
                Bot(0); flame.OnRestoreCheckpoint();
                ori.Energy.Max = cap; ori.Energy.SetCurrent(energy);
                if (!hadCharge) ori.PlayerAbilities.SetAbility(AbilityType.ChargeFlame, false);
            }
        }
        static IEnumerator SelfRevive(TestHarness h, string label, SeinCharacter ori, int self, int restores)
        {
            Bot(0);
            TestClock.Window hint = new TestClock.Window(1f);
            while (Revive.LastSelfHint == null && hint.Open) yield return null;
            h.Check(Revive.LastSelfHint != null && GameObject.Find("OriCoop_SelfReviveHint_P" + Players.LocalSeat) != null,
                label + "the hold-Y/Q self-revive hint appears");
            Bot(0, P2Input.BTN_Y);
            TestClock.Window hold = new TestClock.Window(3f);
            while (Revive.IsDowned(ori) && hold.Open) yield return null;
            Bot(0);
            h.Check(!Revive.IsDowned(ori) && Revive.SelfRevives == self + 1 && TestHarness.RestoreCheckpointCalls == restores,
                label + "holding Y revives P1 without a checkpoint reload");
        }
        static IEnumerator CheckLiveScreen(TestHarness h, string screen, Action open, Func<bool> visible, int step, bool retryClose = false)
        {
            string label = "S4 " + screen + ": ";
            bool host = NetPlay.IsHost, revive = Revive.Enabled;
            SeinCharacter ori = Own;
            SeinDamageReciever receiver = ori.Mortality.DamageReciever;
            bool immortal = receiver.IsImmortal;
            float health = ori.Mortality.Health.Amount;
            Action close = delegate {
                SessionRules.HidePopups(); CoopMenu.Close();
                if (UI.MainMenuVisible) UI.Menu.HideMenuScreen(true);
            };
            yield return Barrier(h, step);
            try
            {
                Revive.Enabled = true;
                if (host)
                {
                    yield return Wait(0.5f);
                    ori.Position = s_runStart + Vector3.up * 5f;
                    ori.Speed = Vector3.down * 2f;
                    ori.Mortality.Health.RestoreAllHealth();
                    receiver.IsImmortal = false;
                    open();
                    h.Check(visible() && SessionRules.LiveScreen && !SessionRules.LocalLocked && ori.Speed.y < -1f,
                        label + "opens without holding Ori or zeroing speed");
                    Bot(1);
                }
                float peak = (host ? ori : P2Context.P2).Position.y, drop = 0f;
                float sentPeak = float.MinValue, sentDrop = 0f;
                bool running = true, visibleThroughout = true, remoteLocked = false;
                for (float end = Now + 3f; Now < end; )
                {
                    float y = (host ? ori : P2Context.P2).Position.y;
                    if (host || Players.First.ScreenInputLocked)
                    {
                        peak = Mathf.Max(peak, y); drop = Mathf.Max(drop, peak - y);
                        sentPeak = Mathf.Max(sentPeak, NetPlay.LatestRemotePos.y);
                        sentDrop = Mathf.Max(sentDrop, sentPeak - NetPlay.LatestRemotePos.y);
                        remoteLocked |= !host;
                    }
                    running &= Time.timeScale > 0f && !Own.IsSuspended && !Paused(SuspensionCounts());
                    visibleThroughout &= !host || visible();
                    yield return null;
                }
                h.Check(running, label + "the world keeps running");
                if (host)
                {
                    h.Check(visibleThroughout && drop > 2f && ori.PlatformBehaviour.LeftRightMovement.HorizontalInput == 0f
                        && !GameController.Instance.InputLocked, label + "P1 falls but the screen input does not steer it (drop=" + F(drop) + ")");
                    float before = ori.Mortality.Health.Amount;
                    receiver.ResetInviciblity();
                    receiver.OnRecieveDamage(new Damage(4f, Vector2.right, ori.Position, DamageType.Enemy, null));
                    h.Check(visible() && ori.Mortality.Health.Amount < before, label + "P1 takes an enemy hit while it stays open");
                }
                else h.Check(remoteLocked && drop > 2f && sentDrop > 2f,
                    label + "P2 receives P1's fall and sees the puppet fall (sent=" + F(sentDrop) + ", puppet=" + F(drop) + ")");
                yield return Barrier(h, step + 1);
                if (host)
                {
                    close();
                    yield return TestClock.WaitGame(0.5f);
                    h.Check(!visible() && !SessionRules.LiveScreen && ori.Controller.CanMove
                        && ori.PlatformBehaviour.LeftRightMovement.HorizontalInput > 0f, label + "closing restores control");
                    Bot(0);
                    Revive.LastSelfHint = null;
                    int self = Revive.SelfRevives, restores = TestHarness.RestoreCheckpointCalls;
                    open();
                    h.Check(visible(), label + "open before fatal hit");
                    if (retryClose) UI.Menu.LockClosingMenu = true;
                    try
                    {
                        receiver.OnRecieveDamage(new Damage(1000f, Vector2.zero, ori.Position, DamageType.Enemy, null));
                        if (retryClose)
                        {
                            Bot(0, P2Input.BTN_Y);
                            yield return TestClock.WaitGame(0.3f);
                            Revive.Downed down = Revive.DownedAtSeat(Players.LocalSeat);
                            h.Check(UI.MainMenuVisible && down != null && down.SelfHold > 0f,
                                label + "self-revive hold advances while native closing is locked");
                        }
                    }
                    finally { if (retryClose) UI.Menu.LockClosingMenu = false; }
                    if (retryClose) yield return TestClock.WaitGame(0.1f);
                    h.Check(Revive.IsDowned(ori) && !UI.MainMenuVisible && !CoopMenu.IsOpen && !SessionRules.PopupOpen
                        && !GameController.Instance.LockInput, label + "the fatal hit closes the screen");
                    yield return SelfRevive(h, label, ori, self, restores);
                }
                yield return Barrier(h, step + 2);
            }
            finally
            {
                Bot(0); Revive.Enabled = revive;
                if (Revive.AnyDowned) Revive.ClearAll("S4 screen test cleanup", false);
                if (host)
                {
                    close(); receiver.IsImmortal = immortal; ori.Mortality.Health.SetAmount(health);
                }
            }
        }
        static IEnumerator CheckMenu(TestHarness h, MenuScreenManager.Screens screen, Action open, int step)
        {
            return CheckLiveScreen(h, screen.ToString(), open, () => UI.Menu.MainMenuVisible && UI.Menu.CurrentScreen == screen, step,
                screen == MenuScreenManager.Screens.Pause);
        }
        static void OpenInventoryItem()
        {
            UI.Menu.ShowInventory();
            InventoryManager inventory = InventoryManager.Instance;
            foreach (CleverMenuItem item in inventory.NavigationManager.MenuItems)
            {
                InventoryAbilityItem ability = item.GetComponent<InventoryAbilityItem>();
                if (item.GetComponent<InventoryItemHelpText>() == null || (ability != null && !ability.HasAbility)) continue;
                inventory.NavigationManager.SetCurrentMenuItem(item);
                inventory.OnMenuItemPressed();
                break;
            }
        }
        static IEnumerator NoPauseScreens(TestHarness h)
        {
            yield return PlaceAtStart(h, s_runStart, 180);
            yield return Map(h);
            yield return PlaceAtStart(h, s_runStart, 188);
            yield return CheckMenu(h, MenuScreenManager.Screens.Pause, () => UI.Menu.ShowMenuScreen(), 200);
            yield return CheckMenu(h, MenuScreenManager.Screens.SkillTree, () => UI.Menu.ShowSkillTree(), 203);
            yield return CheckMenu(h, MenuScreenManager.Screens.Inventory, () => UI.Menu.ShowInventory(), 206);
            yield return CheckMenu(h, MenuScreenManager.Screens.Options, () => UI.Menu.ShowOptions(), 209);
            yield return CheckLiveScreen(h, "inventory item", OpenInventoryItem, () => UI.Menu.IsInventoryVisible() && SessionRules.PopupOpen, 212);
            yield return CheckLiveScreen(h, "help popup", () => TryOpenHelp(), () => SessionRules.PopupOpen, 215);
            yield return CheckLiveScreen(h, "F8", CoopMenu.Open, () => CoopMenu.IsOpen, 218);
            yield return TeleportPicker(h);
            yield return PlaceAtStart(h, s_runStart, 184);
        }
        static IEnumerator TeleportPicker(TestHarness h)
        {
            yield return Barrier(h, 221);
            if (NetPlay.IsHost)
            {
                SeinCharacter ori = Own;
                Vector3 start = ori.Position;
                GameMapTeleporter origin = TeleporterController.Instance.Teleporters[0];
                GameMapTeleporter destination = TeleporterController.Instance.Teleporters.Find(w => w != origin && Vector3.Distance(w.WorldPosition, start) > 15f);
                h.Check(destination != null, "S4 picker: a distant destination exists");
                if (destination != null)
                {
                    MenuScreenManager menu = UI.Menu;
                    bool closingLocked = menu.LockClosingMenu;
                    bool originActive = origin.Activated, destinationActive = destination.Activated;
                    GameObject go = new GameObject("S4 departure well");
                    try
                    {
                        go.transform.position = start;
                        go.AddComponent<SceneTeleporter>().Identifier = origin.Identifier;
                        SavePedestal well = go.AddComponent<SavePedestal>(); well.enabled = false;
                        AccessTools.Field(typeof(SavePedestal), "m_hasBeenUsedBefore").SetValue(well, true);
                        origin.Activated = destination.Activated = true;
                        AccessTools.Method(typeof(SavePedestal), "TeleportOnPedestal").Invoke(well, null);
                        h.Check(menu.MainMenuVisible && GameMapUI.Instance.ShowingTeleporters && !ori.IsSuspended
                            && !ori.Controller.CanMove, "S4 picker: stays live at the departure well");
                        GameMapUI.Instance.Teleporters.Select(destination.Identifier);
                        menu.LockClosingMenu = true;
                        ori.Position = start + Vector3.right * (well.Radius + 1f);
                        Core.Input.InputButtonProcessor confirm = Core.Input.ActionButtonA;
                        bool pressed = confirm.IsPressed, wasPressed = confirm.WasPressed;
                        try { confirm.WasPressed = false; confirm.IsPressed = true; GameMapUI.Instance.Teleporters.Advance(); }
                        finally { confirm.IsPressed = pressed; confirm.WasPressed = wasPressed; }
                        h.Check(menu.MainMenuVisible && !(bool)AccessTools.Field(typeof(TeleporterController), "m_isTeleporting").GetValue(TeleporterController.Instance),
                            "S4 picker: moving out of range blocks confirmation even while closing is locked");
                        ori.Position = start;
                        menu.LockClosingMenu = false;
                        yield return TestClock.WaitGame(0.1f);
                        h.Check(!menu.MainMenuVisible, "S4 picker: a cancelled picker closes when unlocked");
                    }
                    finally
                    {
                        try
                        {
                            if (menu != null && menu.MainMenuVisible && menu.CurrentScreen == MenuScreenManager.Screens.WorldMap)
                            {
                                menu.LockClosingMenu = false;
                                menu.HideMenuScreen(true);
                            }
                        }
                        finally
                        {
                            if (menu != null) menu.LockClosingMenu = closingLocked;
                            origin.Activated = originActive; destination.Activated = destinationActive;
                            UnityEngine.Object.Destroy(go);
                        }
                    }
                }
            }
            yield return Barrier(h, 223);
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
            bool guest = !NetPlay.IsHost, died = false;
            if (guest)
            {
                // Damage ignores a guest whose input is still locked by a story.
                float ready = Now;
                while (!Own.Controller.CanMove && Now - ready < 10f) yield return null;
                GameController gc = GameController.Instance;
                h.Check(Own.Controller.CanMove, "S4 guest can move before its death (waited " + F(Now - ready) + " s; lockInput="
                    + gc.LockInput + " byAction=" + gc.LockInputByAction + " lever=" + (Own.Abilities.Lever && Own.Abilities.Lever.InputLocked)
                    + " animation=" + Own.Controller.IsPlayingAnimation + " held=" + SessionRules.LocalLocked + " popup=" + SessionRules.PopupOpen + ")");
            }
            float start = Now;
            if (guest)
            {
                UI.Menu.ShowMenuScreen();
                Kill(); died = !Alive || SessionRules.Respawning;
                h.Check(!UI.MainMenuVisible, "S4 guest death closes the pause menu without revive rules");
                if (UI.MainMenuVisible) UI.Menu.HideMenuScreen(true);
            }
            while (Now - start < 0.1f) { if (guest && (!Alive || SessionRules.Respawning)) died = true; yield return null; }
            while ((!Alive || (guest && SessionRules.Respawning)) && Now - start < 3f) { died = true; yield return null; }
            yield return Wait(0.25f);
            float distance = Vector3.Distance(Own.Position, NetPlay.LatestRemotePos);
            h.Note("S4 guest death elapsed=" + F(Now - start) + " died=" + died + " restores=" + (TestHarness.RestoreCheckpointCalls - restores)
                + " health=" + Own.Mortality.Health.Amount + " distance=" + F(distance));
            if (guest)
            {
                h.Check(died, "S4 guest died (the kill took effect)");
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
            {
                if (__instance.MoonGuid == FixtureId) s_performs++;
                if (__instance.MoonGuid == OrdinaryId) s_ordinaryCalls++;
                if (__instance.MoonGuid == WellId) s_wellCalls++;
            }
        }
        static IEnumerator LocalPresentations(TestHarness h, ActionSequence projectile)
        {
            bool host = NetPlay.IsHost;
            GameObject objectiveRoot = new GameObject("*objectiveSetup");
            GameObject menuButton = new GameObject("*actionSequence");
            menuButton.transform.parent = UI.Menu.transform;
            GameObject hint = new GameObject("objectiveSetupAction");
            hint.transform.parent = objectiveRoot.transform;
            ActionSequence objective = hint.AddComponent<ActionSequence>();
            objective.MoonGuid = new MoonGuid(OrdinaryId); objective.MoonGuid.D++;
            ActionSequence menu = menuButton.AddComponent<ActionSequence>();
            menu.MoonGuid = new MoonGuid(OrdinaryId); menu.MoonGuid.D += 2;
            foreach (ActionSequence sequence in new ActionSequence[] { objective, menu })
            {
                LockPlayerInputAction action = sequence.gameObject.AddComponent<LockPlayerInputAction>();
                action.DurationPlayerCantMove = 0.15f; sequence.Actions.Add(action);
            }
            // Keep the native return action behind a wait so the test can observe its start, then
            // stop the sequence before it actually tears down the shared test session.
            WaitAction confirm = menuButton.AddComponent<WaitAction>(); confirm.Duration = 60f;
            menu.Actions.Add(confirm); menu.Actions.Add(menuButton.AddComponent<ReturnToTitleScreenAction>());
            PlatformMovement movement = Own.PlatformBehaviour.PlatformMovement;
            bool suspended = movement.IsSuspended;
            Vector3 start = Own.Position;
            GameObject cameraObject = new GameObject("S4 camera pan");
            ActionSequence pan = cameraObject.AddComponent<ActionSequence>();
            pan.MoonGuid = new MoonGuid(OrdinaryId); pan.MoonGuid.D += 3;
            MoveCameraAction camera = cameraObject.AddComponent<MoveCameraAction>();
            camera.Target = Own.gameObject; pan.Actions.Add(camera);
            try
            {
                h.Check(!StorySync.IsStorySequence(objective) && !StorySync.IsTakeoverAction(objective),
                    "S4 local objective setup cannot start a story or an all-player gate");
                h.Check(!StorySync.IsStorySequence(menu), "S4 pause menu action stays local even with an input lock");
                if (!host)
                {
                    movement.IsSuspended = true;
                    Vector3 away = NetPlay.LatestRemotePos + Vector3.right * 20f;
                    if (!Core.Scenes.Manager.IsInsideASceneBoundary(away + Vector3.up * 0.5f))
                        away = NetPlay.LatestRemotePos - Vector3.right * 20f;
                    Plugin.Teleport(Own, away);
                }
                int ordinaryCalls = s_ordinaryCalls, sent = StorySync.StartsSent, mirrored = StorySync.StartsMirrored;
                Vector3 before = Own.Position;
                yield return Barrier(h, 42);
                if (host)
                {
                    projectile.Perform(null);
                    // A separate local script locks the player just as the plant starts firing (playtest 6).
                    objective.Perform(null);
                    menu.Perform(null);
                }
                yield return Wait(0.8f);
                h.Check(s_ordinaryCalls - ordinaryCalls == (host ? 1 : 0), "S4 projectile sequence runs only on the triggering PC");
                h.Check(StorySync.StartsSent == sent && StorySync.StartsMirrored == mirrored && !StorySync.Watching,
                    "S4 firing, objective setup and pause menu actions send and mirror no story");
                if (!host) h.Check(Vector3.Distance(Own.Position, before) < 0.5f,
                    "S4 teammate stays in place while the other player fires a projectile and uses the menu");

                yield return Barrier(h, 43);
                mirrored = StorySync.StartsMirrored;
                if (host) pan.Perform(null);
                float end = Now + 3f;
                while (!host && StorySync.StartsMirrored == mirrored && Now < end) yield return null;
                if (!host) h.Check(StorySync.StartsMirrored == mirrored + 1 && Vector3.Distance(Own.Position, before) < 0.5f,
                    "S4 camera-only sequence mirrors without moving the teammate");
                yield return Barrier(h, 44);
            }
            finally
            {
                if (!host) { movement.IsSuspended = suspended; Plugin.Teleport(Own, start); }
                if (menu.IsRunning) menu.Stop();
                UnityEngine.Object.Destroy(menuButton); UnityEngine.Object.Destroy(objectiveRoot); UnityEngine.Object.Destroy(cameraObject);
            }
        }
        static IEnumerator Story(TestHarness h)
        {
            GameObject go = new GameObject("OriCoop S4 harmless sequence");
            s_fixture = go.AddComponent<ActionSequence>(); s_fixture.MoonGuid = new MoonGuid(FixtureId);
            GameObject ordinaryObject = new GameObject("OriCoop S4 ordinary level sequence");
            ActionSequence ordinary = ordinaryObject.AddComponent<ActionSequence>(); ordinary.MoonGuid = new MoonGuid(OrdinaryId);
            ordinary.Actions.Add(ordinaryObject.AddComponent<SpawnProjectileAction>());
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
                yield return Wait(0.6f);
                yield return LocalPresentations(h, ordinary);
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
        internal static IEnumerator StoryRevive(TestHarness h, int seats, Func<int, IEnumerator> barrier)
        {
            bool revive = Revive.Enabled, immortal = Own.Mortality.DamageReciever.IsImmortal;
            float previousHealth = Own.Mortality.Health.Amount, previousEnergy = Own.Energy.Current;
            int seat = Players.LocalSeat;
            GameObject go = new GameObject("T-246 cutscene");
            ActionSequence sequence = go.AddComponent<ActionSequence>();
            sequence.MoonGuid = new MoonGuid(FixtureId); sequence.MoonGuid.D += 246;
            LockPlayerInputAction storyLock = go.AddComponent<LockPlayerInputAction>();
            storyLock.DurationPlayerCantMove = 1.2f; sequence.Actions.Add(storyLock);
            WaitAction ending = go.AddComponent<WaitAction>(); ending.Duration = 1.5f; sequence.Actions.Add(ending);
            ActionSequence puzzle = go.AddComponent<ActionSequence>();
            puzzle.MoonGuid = new MoonGuid(sequence.MoonGuid); puzzle.MoonGuid.D++;
            LetterboxAction show = go.AddComponent<LetterboxAction>(); show.ShowLetterboxes = true;
            LetterboxAction hide = go.AddComponent<LetterboxAction>(); hide.ShowLetterboxes = false;
            WaitAction pause = go.AddComponent<WaitAction>(); pause.Duration = 0.3f;
            puzzle.Actions.Add(show); puzzle.Actions.Add(pause); puzzle.Actions.Add(hide);
            try
            {
                Revive.Enabled = true;
                Own.Mortality.DamageReciever.IsImmortal = true;
                if (seat != 2)
                {
                    Own.Mortality.Health.SetAmount(seat == 1 ? 4f : seat == 3 ? 8f : 12f);
                    Own.Energy.Current = seats == 2 || seat != 1 ? 1f : 0f;
                }
                yield return barrier(0);
                if (seat == 2) { Own.Energy.Current = 0f; Kill(); }
                float end = Now + 6f;
                while (Revive.DownedAtSeat(2) == null && Now < end) yield return null;
                h.Check(Revive.DownedAtSeat(2) != null, "story revive: P2 down before the performance");
                yield return Wait(0.8f);   // each device's normal Vitals sender publishes the setup
                int starts = seat == 1 ? StorySync.StartsSent : StorySync.StartsMirrored;
                yield return barrier(1);
                if (seat == 1) puzzle.Perform(null);
                end = Now + 8f;
                while (((seat == 1 ? StorySync.StartsSent : StorySync.StartsMirrored) == starts || StorySync.StoryActive)
                    && Now < end) yield return null;
                h.Check((seat == 1 ? StorySync.StartsSent : StorySync.StartsMirrored) == starts + 1 && !StorySync.StoryActive,
                    "story revive: letterbox-only puzzle finished on each seat");
                h.Check(Revive.DownedAtSeat(2) != null, "story revive: a letterbox-only puzzle leaves P2 down");
                yield return barrier(3);
                if (seat == 1) sequence.Perform(null);
                yield return Wait(0.2f);
                h.Check(Revive.DownedAtSeat(2) != null, "story revive: P2 stays down during the performance");
                if (seat == 1)
                {
                    end = Now + 4f;
                    while (!ending.IsPerforming && Now < end) yield return null;
                    yield return Wait(0.4f);
                    h.Check(ending.IsPerforming && StorySync.StoryActive && Revive.DownedAtSeat(2) != null,
                        "story revive: the unlocked native ending finishes before recovery");
                }
                end = Now + 8f;
                while (Revive.DownedAtSeat(2) != null && Now < end) yield return null;
                h.Check(Revive.DownedAtSeat(2) == null, "story revive: P2 up after the performance on every seat");
                if (seat == 2)
                {
                    float nearest = float.PositiveInfinity;
                    for (int survivor = 1; survivor <= seats; survivor++)
                    {
                        Vector3 at;
                        if (survivor != 2 && NetPlay.RemotePos(survivor, out at))
                            nearest = Mathf.Min(nearest, Vector3.Distance(Own.Position, at));
                    }
                    h.Check(nearest < 3f, "story revive: P2 arrives beside a survivor (distance " + F(nearest) + ")");
                    float expectedHealth = seats == 2 ? 4f : 8f;
                    h.Check(Own.Mortality.Health.Amount == expectedHealth && Own.Energy.Current == 1f,
                        "story revive: P2 receives the living players' average health and energy (got "
                        + F(Own.Mortality.Health.Amount) + "/" + F(Own.Energy.Current) + ")");
                }
                yield return barrier(2);
            }
            finally
            {
                Revive.Enabled = revive;
                Own.Mortality.DamageReciever.IsImmortal = immortal;
                Own.Mortality.Health.SetAmount(previousHealth);
                Own.Energy.Current = previousEnergy;
                UnityEngine.Object.Destroy(go);
            }
        }
        static IEnumerator SpiritWell(TestHarness h)
        {
            Vector3 start = Own.Position;
            GameObject go = new GameObject("S4 spirit well");
            SavePedestal well = go.AddComponent<SavePedestal>(); well.enabled = false;
            ActionSequence opened = go.AddComponent<ActionSequence>(); opened.MoonGuid = new MoonGuid(WellId);
            WaitAction wait = go.AddComponent<WaitAction>(); wait.Duration = 0.2f;
            opened.Actions.Add(wait); well.OnOpenedAction = opened;
            Type playType = AccessTools.TypeByName("PlayCharacterAnimation");
            ActionMethod play = (ActionMethod)go.AddComponent(playType);
            opened.Actions.Add(play);
            opened.Actions.Add((ActionMethod)go.AddComponent(AccessTools.TypeByName("StopCharacterAnimation")));
            MethodInfo save = AccessTools.Method(typeof(SavePedestal), "SaveOnPedestal");
            FieldInfo used = AccessTools.Field(typeof(SavePedestal), "m_hasBeenUsedBefore");
            bool host = NetPlay.IsHost;
            PlatformMovement guestMovement = host ? null : Own.PlatformBehaviour.PlatformMovement;
            bool wasSuspended = guestMovement != null && guestMovement.IsSuspended;
            List<GameMapTeleporter> teleporters = TeleporterController.Instance.Teleporters;
            GameMapTeleporter mapWell = teleporters[teleporters.Count - 1];
            bool wasActivated = mapWell.Activated;
            try
            {
                AccessTools.Field(playType, "Animation").SetValue(play, Own.Abilities.Idle.IdleAnimation);
                // The game registers native pedestals in the scene save registry. Exercise its real world replication.
                SaveSceneManager.Master.AddSaveObject(well, new MoonGuid(WellId));
                mapWell.Activated = false;
                go.transform.position = host ? start : NetPlay.LatestRemotePos;
                Vector3 away = go.transform.position + Vector3.right * 20f;
                if (!host)
                {
                    if (!Core.Scenes.Manager.IsInsideASceneBoundary(away + Vector3.up * 0.5f))
                        away = go.transform.position - Vector3.right * 20f;
                    // The offset may have no floor. Suspend only physics; story teleports still write the position.
                    guestMovement.IsSuspended = true;
                    Plugin.Teleport(Own, away);
                }
                yield return Wait(1.5f);   // let both world scans establish the unopened baseline
                yield return Barrier(h, 71);
                h.Check(!(bool)used.GetValue(well) && !mapWell.Activated, "S4 well: starts unopened");
                for (int visit = 0; visit < 2; visit++)
                {
                    string label = visit == 0 ? "S4 well: first activation " : "S4 well: re-save ";
                    Vector3 before = Own.Position;
                    int restores = TestHarness.RestoreCheckpointCalls, plays = s_wellCalls;
                    int sent = StorySync.StartsSent, anims = CutsceneAnimSync.Sent, ownPlays = CutsceneAnimSync.OwnPlays;
                    if (!host) h.Check(Vector3.Distance(before, go.transform.position) > 12f, label + "starts outside gather range");
                    yield return Barrier(h, 72 + 2 * visit);
                    if (host)
                    {
                        TeleporterController.Activate(mapWell.Identifier);   // native SavePedestal.Highlight on approach
                        save.Invoke(well, null);
                    }
                    float end = Now + 4f;
                    while ((!(bool)used.GetValue(well) || !mapWell.Activated) && Now < end) yield return null;
                    yield return TestClock.WaitGame(0.8f);  // include the opening's delayed animation
                    yield return Barrier(h, 73 + 2 * visit);
                    h.Check((bool)used.GetValue(well) && mapWell.Activated, label + "activates the native pedestal and map destination on both PCs");
                    h.Check(s_wellCalls == plays + (host ? 1 : 0), label + "plays only on the saver");
                    h.Check(StorySync.StartsSent == sent && CutsceneAnimSync.Sent == anims, label + "broadcasts no story or animation");
                    if (!host)
                    {
                        float moved = Own != null ? Vector3.Distance(Own.Position, before) : float.PositiveInfinity;
                        int restored = TestHarness.RestoreCheckpointCalls - restores, played = CutsceneAnimSync.OwnPlays - ownPlays;
                        h.Note(label + "guest movement=" + F(moved) + " checkpoint restores=" + restored + " animations=" + played);
                        h.Check(Alive && restored == 0 && moved < 0.5f && played == 0, label + "does not teleport or animate the guest");
                    }
                    if (visit == 0) yield return Wait(3.3f);   // beyond the generic story dedupe
                }
            }
            finally
            {
                mapWell.Activated = wasActivated;
                if (SaveSceneManager.Master != null) SaveSceneManager.Master.UnregisterGameObject(go);
                UnityEngine.Object.Destroy(go);
                if (guestMovement != null) guestMovement.IsSuspended = wasSuspended;
                if (Own != null) Plugin.Teleport(Own, start);
            }
            yield return Wait(0.6f);
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
            if (NetPlay.IsHost)
            {
                NetPlay.TestCaptureNextMovement();
                float captureBy = Now + 3f;
                while (NetPlay.TestCapturedMovement == null && Now < captureBy) yield return null;
            }
            yield return Barrier(h, 51); int disconnects = SessionRules.Disconnects, sessions = SessionRules.Sessions;
            byte[] previousMovement = NetPlay.TestCapturedMovement;
            float start = Now;
            // T-106: while the guest is cut off from the host (session ended, retrying), its own Ori stays visible and free.
            int cutFrames = 0, heldFrames = 0;
            try
            {
                if (NetPlay.IsHost) NetPlay.SetSim(0, 0, 100, 0);
                // Longer than NetPeer.TimeoutMs (10 s since lag test ②) so the link really times out.
                float dropEnd = Now + 12f;
                while (Now < dropEnd)
                {
                    if (!NetPlay.IsHost && SessionRules.Disconnects > disconnects && !NetPlay.Connected && Own != null)
                    {
                        cutFrames++;
                        bool shown = false;
                        foreach (Renderer r in Own.GetComponentsInChildren<Renderer>(true)) if (r.enabled && r.gameObject.activeInHierarchy) { shown = true; break; }
                        if (StorySync.IntroHeld(Players.LocalSeat) || !shown) heldFrames++;
                    }
                    yield return null;
                }
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
            if (!NetPlay.IsHost)
            {
                if (cutFrames == 0) h.Note("S4 guest never saw a disconnected frame during the drop: T-106 visibility not judged");
                else h.Check(heldFrames == 0, "S4 guest's own Ori visible and not intro-held while cut off from the host ("
                    + heldFrames + " of " + cutFrames + " disconnected frames held)");
            }
            h.Check(!SessionRules.PartnerInMenu && !StorySync.Watching, "S4 no stale remote menu or watching lock");
            if (NetPlay.IsHost) NetPlay.TestCaptureNextMovement();
            yield return Digests(h, 52);
            if (NetPlay.IsHost)
            {
                h.Check(NetPlay.TestMovementAdmission(previousMovement, false),
                    "S4 a delayed pre-disconnect movement packet leaves the rejoined Ori's position and play buffer unchanged");
                h.Check(NetPlay.TestMovementAdmission(NetPlay.TestCapturedMovement, true),
                    "S4 movement from the guest's new connection reaches its Ori");
            }
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
                yield return Step(h, 70, SpiritWell(h));
                yield return Step(h, 80, StoryRevive(h, 2, n => Barrier(h, 81 + n)));
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
