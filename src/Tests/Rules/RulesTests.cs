using System;
using System.IO;
using OriCoop.Seams;
using OriCoop.Rules;

static class RulesTests
{
    sealed class Clock : IClock
    {
        public float Time { get; private set; }
        public float RealTime { get { return Time; } }
        public int Frame { get; private set; }
        public void Advance(float seconds) { Time += seconds; Frame++; }
    }

    sealed class Bus : INetBus
    {
        readonly Action<BinaryReader>[] handlers = new Action<BinaryReader>[256];
        public readonly int Seat;
        public Bus[] Peers;
        public bool Enabled { get { return true; } }
        public bool Connected { get { return true; } }
        public bool IsHost { get { return Seat == 1; } }
        public int Sender { get; private set; }
        public int HostSeat { get { return m_hostSeat; } set { m_hostSeat = value; } } int m_hostSeat = 1;
        public event Action SessionStarted;
        public event Action SessionEnded;
        public event Action<int> PlayerJoined;
        public event Action<int> PlayerLeft;
        public Bus(int seat) { Seat = seat; }
        public void On(byte type, Action<BinaryReader> handler) { handlers[type] = handler; }
        public void Send(byte type, bool reliable, Action<BinaryWriter> body)
        {
            byte[] bytes = Encode(body);
            foreach (Bus peer in Peers) if (peer != this) Deliver(peer, type, bytes);
        }
        public void SendTo(int seat, byte type, bool reliable, Action<BinaryWriter> body)
        {
            foreach (Bus peer in Peers) if (peer.Seat == seat) Deliver(peer, type, Encode(body));
        }
        static byte[] Encode(Action<BinaryWriter> body)
        {
            MemoryStream bytes = new MemoryStream(); body(new BinaryWriter(bytes)); return bytes.ToArray();
        }
        void Deliver(Bus peer, byte type, byte[] bytes)
        {
            if (peer.handlers[type] != null)
            {
                peer.Sender = Seat;
                peer.handlers[type](new BinaryReader(new MemoryStream(bytes)));
            }
        }
        public void Forward(params byte[] types) { }
        public void Leave(int seat) { if (PlayerLeft != null) PlayerLeft(seat); }
        public void Join(int seat) { if (PlayerJoined != null) PlayerJoined(seat); }
        public void Start() { if (SessionStarted != null) SessionStarted(); }
        public void End() { if (SessionEnded != null) SessionEnded(); }
    }

    static Bus[] Network(int count)
    {
        Bus[] buses = new Bus[count];
        for (int i = 0; i < count; i++) buses[i] = new Bus(i + 1);
        foreach (Bus bus in buses) bus.Peers = buses;
        return buses;
    }

    static int failed, cases;
    static void Run(string name, Action test)
    {
        cases++;
        try { test(); Console.WriteLine("ok " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Near(float actual, float expected) { Check(Math.Abs(actual - expected) < 0.0001f, "expected " + expected + ", got " + actual); }
    static void Bytes(byte[] actual, params byte[] expected)
    {
        Check(actual.Length == expected.Length, "byte count");
        for (int i = 0; i < expected.Length; i++)
            Check(actual[i] == expected[i], "byte " + i + ": expected " + expected[i] + ", got " + actual[i]);
    }
    static DownedState Down(int seat) { return new DownedState { Player = seat }; }
    static DownedRoster<DownedState> Roster() { return new DownedRoster<DownedState>(4); }

    static float Hold(DownedState d, float health, float dt, bool multi, int rescuer)
    {
        d.Holding = false;
        if (multi && d.Rescuer != rescuer) { ReviveLogic.Release(d); ReviveLogic.SetDrainRate(d, health, 1.5f); }
        if (d.NeedRelease) return health;
        ReviveLogic.BeginHold(d, rescuer);
        if (d.Progress <= 0f) ReviveLogic.SetDrainRate(d, health, 1.5f);
        float drain = ReviveLogic.Drain(d, health, dt);
        health -= drain;
        ReviveLogic.Advance(d, dt, drain, 1.5f);
        return health;
    }

    static MenuPresence[] Menus(Bus[] buses)
    {
        MenuPresence[] states = new MenuPresence[buses.Length];
        for (int i = 0; i < buses.Length; i++)
        {
            int index = i;
            states[i] = new MenuPresence();
            buses[i].On(53, delegate(BinaryReader r) { states[index].Receive(buses[index].Sender, r); });
            buses[i].PlayerLeft += delegate(int seat) { states[index].Set(seat, false); };
        }
        return states;
    }
    static void Pause(Bus b, bool paused) { b.Send(53, true, delegate(BinaryWriter w) { w.Write(paused); }); }
    // One SessionRules.RefreshMenu: tell the others when this PC's pause state changed.
    static void Report(Bus b, MenuReport report, bool online, bool paused) { if (report.ShouldSend(online, paused)) Pause(b, paused); }
    static byte[] Write(Action<BinaryWriter> action)
    {
        MemoryStream m = new MemoryStream(); action(new BinaryWriter(m)); return m.ToArray();
    }
    static BinaryReader Read(byte[] data) { return new BinaryReader(new MemoryStream(data)); }

    static int Main()
    {
        PopupDebtTests.RunAll(Run, Check);
        // T-104: the host opened its pause menu before anyone joined (offline: a real pause). A guest connects; the
        // host's per-frame menu refresh runs while connected but before its session starts, and its session start turns
        // the offline pause into a skipped one. The guest must not end up believing the host is paused.
        Run("join while the host's pause menu is open does not leave the guest paused", delegate {
            Bus[] b = Network(2); MenuPresence[] m = Menus(b); MenuReport host = new MenuReport();
            Report(b[0], host, false, true);          // offline pause menu: nothing to tell
            m[1].Clear();                             // guest: host's Hello -> guest session starts (SessionRules.Started)
            Report(b[0], host, true, true);           // host frame: connected, session not started yet
            host.SessionStarted();                    // host: guest's Hello -> session starts, the pause no longer pauses
            Report(b[0], host, true, false);          // host frames from now on
            Report(b[0], host, true, false);
            Check(!m[1].InMenu(1), "guest still records the host as paused: its world stays frozen");
        });
        Run("pause states sent after the session started reach the partner", delegate {
            Bus[] b = Network(2); MenuPresence[] m = Menus(b); MenuReport guest = new MenuReport();
            guest.SessionStarted();
            Report(b[1], guest, true, true); Check(m[0].InMenu(2), "pause not delivered");
            Report(b[1], guest, true, true); Report(b[1], guest, true, false); Check(!m[0].InMenu(2), "resume not delivered");
            guest.SessionEnded(); guest.SessionStarted(); m[0].Clear();
            Report(b[1], guest, true, true); Check(m[0].InMenu(2), "pause after a reconnect not delivered");
        });
        Run("only the rescuer device advances online holds even with two seats", delegate {
            Check(ReviveLogic.CanSimulateHold(true, true), "local saver must advance");
            Check(!ReviveLogic.CanSimulateHold(true, false), "victim must not simulate the remote saver and finish early");
            Check(ReviveLogic.CanSimulateHold(false, false), "offline two-player path unchanged");
        });
        Run("downed seat is recorded", delegate {
            DownedRoster<DownedState> r = Roster(); Check(r.TryAdd(2, Down(2)), "first down rejected");
            Check(r.AnyDowned && r.Seats[2].Player == 2, "seat not downed");
        });
        Run("duplicate downed retains original state", delegate {
            DownedRoster<DownedState> r = Roster(); DownedState first = Down(2); first.Progress = 0.3f;
            r.TryAdd(2, first); Check(!r.TryAdd(2, Down(2)) && r.Seats[2] == first, "duplicate replaced state");
        });
        Run("all down with dying player", delegate {
            DownedRoster<DownedState> r = Roster(); r.TryAdd(2, Down(2));
            Check(r.AllDown(new int[] { 1, 2 }, 1), "expected checkpoint death");
        });
        Run("third living player prevents wipe", delegate {
            DownedRoster<DownedState> r = Roster(); r.TryAdd(2, Down(2));
            Check(!r.AllDown(new int[] { 1, 2, 3 }, 1), "living seat ignored");
        });
        Run("leaving downed player is cleared", delegate {
            Bus[] b = Network(3); DownedRoster<DownedState> r = Roster(); DownedState d = Down(2); r.TryAdd(2, d);
            DownedState removed = null; b[0].PlayerLeft += delegate(int seat) { removed = r.Take(seat); };
            b[0].Leave(2); Check(removed == d && !r.AnyDowned, "left seat still down");
        });
        Run("living player leaves and remaining downed wipe", delegate {
            Bus[] b = Network(3); DownedRoster<DownedState> r = Roster(); r.TryAdd(1, Down(1)); r.TryAdd(2, Down(2));
            b[0].PlayerLeft += delegate(int seat) { r.Take(seat); };
            Check(!r.AllDown(new int[] { 1, 2, 3 }, 0), "living player ignored");
            b[0].Leave(3);
            Check(r.AnyDowned && r.AllDown(new int[] { 1, 2 }, 0), "remaining players not wiped");
        });
        Run("hold advances with clock", delegate {
            Clock clock = new Clock(); DownedState d = Down(2); float before = clock.Time; clock.Advance(0.5f);
            float h = Hold(d, 6f, clock.Time - before, false, 1); Near(d.Progress, 1f / 3f); Near(h, 5f); Near(d.Given, 1f);
        });
        Run("full hold completes at one", delegate {
            DownedState d = Down(2); Hold(d, 6f, 2f, false, 1); Near(d.Progress, 1f);
        });
        Run("drain keeps quarter health", delegate {
            DownedState d = Down(2); float h = Hold(d, 1f, 10f, false, 1); Near(h, 0.25f);
        });
        Run("release retains progress and health", delegate {
            DownedState d = Down(2); Hold(d, 6f, 0.5f, false, 1); d.Holding = false; ReviveLogic.Release(d);
            Near(d.Progress, 1f / 3f); Near(d.Given, 1f); Check(!d.NeedRelease, "release blocked");
        });
        Run("damage interrupts until release", delegate {
            DownedState d = Down(2); float h = Hold(d, 6f, 0.5f, false, 1); Check(ReviveLogic.Interrupt(d), "not interrupted");
            Hold(d, h, 0.5f, false, 1); Near(d.Progress, 1f / 3f); ReviveLogic.Release(d);
            Hold(d, h, 0.5f, false, 1); Near(d.Progress, 2f / 3f);
        });
        Run("damage outside hold does not interrupt", delegate {
            DownedState d = Down(2); Check(!ReviveLogic.Interrupt(d) && !d.NeedRelease, "spurious interruption");
        });
        Run("changing multi rescuer starts new drain rate", delegate {
            DownedState d = Down(2); Hold(d, 6f, 0.5f, true, 1); ReviveLogic.Interrupt(d);
            Hold(d, 3f, 0.5f, true, 3); Near(d.DrainRate, 1f); Check(!d.NeedRelease && d.Rescuer == 3, "new rescuer blocked");
        });
        Run("progress send is throttled", delegate {
            Clock clock = new Clock(); DownedState d = Down(2); d.Progress = 0.1f; d.Rescuer = 1; d.Holding = true;
            Check(ReviveLogic.ShouldSend(d, 1, false, clock.Time), "first send denied");
            clock.Advance(0.09f); Check(!ReviveLogic.ShouldSend(d, 1, false, clock.Time), "sent too early");
            clock.Advance(0.02f); Check(ReviveLogic.ShouldSend(d, 1, false, clock.Time), "next send denied");
        });
        Run("multi progress requires active local hold", delegate {
            DownedState d = Down(2); d.Progress = 0.5f; d.Rescuer = 1;
            Check(!ReviveLogic.ShouldSend(d, 1, true, 0f), "sent without holding");
        });
        Run("stale progress cannot decrease values", delegate {
            DownedRoster<DownedState> r = Roster(); DownedState d = Down(2); r.TryAdd(2, d);
            r.MergeProgress(2, 3, 3, 1, 0.8f, 2f); r.MergeProgress(2, 3, 3, 1, 0.2f, 1f);
            Near(d.Progress, 0.8f); Near(d.Given, 2f);
        });
        Run("duplicate progress is idempotent", delegate {
            DownedRoster<DownedState> r = Roster(); DownedState d = Down(2); r.TryAdd(2, d);
            r.MergeProgress(2, 3, 3, 1, 0.4f, 1f); r.MergeProgress(2, 3, 3, 1, 0.4f, 1f);
            Near(d.Progress, 0.4f); Near(d.Given, 1f);
        });
        Run("progress from wrong sender ignored", delegate {
            DownedRoster<DownedState> r = Roster(); DownedState d = Down(2); r.TryAdd(2, d);
            r.MergeProgress(2, 3, 4, 1, 0.8f, 2f); Near(d.Progress, 0f);
        });
        Run("progress before downed ignored", delegate {
            DownedRoster<DownedState> r = Roster(); r.MergeProgress(2, 3, 3, 1, 0.8f, 2f);
            DownedState d = Down(2); r.TryAdd(2, d); Near(d.Progress, 0f);
        });
        Run("remote progress cannot override local holder", delegate {
            DownedRoster<DownedState> r = Roster(); DownedState d = Down(2); d.Rescuer = 1; d.Holding = true; r.TryAdd(2, d);
            r.MergeProgress(2, 3, 3, 1, 0.8f, 2f); Near(d.Progress, 0f); Check(d.Rescuer == 1, "rescuer changed");
        });
        Run("downed and revived position messages round trip", delegate {
            byte[] bytes = Write(delegate(BinaryWriter w) { ReviveWire.WritePosition(w, 4, 1.25f, -2f, 3f); });
            Check(bytes.Length == 13 && bytes[0] == 4, "position layout"); ReviveWire.Position p = ReviveWire.ReadPosition(Read(bytes));
            Check(p.Seat == 4, "seat"); Near(p.X, 1.25f); Near(p.Y, -2f); Near(p.Z, 3f);
        });
        Run("revive message round trip", delegate {
            byte[] bytes = Write(delegate(BinaryWriter w) { ReviveWire.WriteRevive(w, 2, 3.5f); });
            Check(bytes.Length == 5 && bytes[0] == 2, "revive layout"); ReviveWire.Revive p = ReviveWire.ReadRevive(Read(bytes));
            Check(p.Seat == 2, "seat"); Near(p.Health, 3.5f);
        });
        Run("progress message round trip", delegate {
            byte[] bytes = Write(delegate(BinaryWriter w) { ReviveWire.WriteProgress(w, 2, 4, 0.75f, 1.5f); });
            Check(bytes.Length == 10 && bytes[0] == 2 && bytes[1] == 4, "progress layout");
            ReviveWire.Progress p = ReviveWire.ReadProgress(Read(bytes)); Check(p.Seat == 2 && p.Rescuer == 4, "seats"); Near(p.Value, 0.75f); Near(p.Given, 1.5f);
        });
        Run("room link message (T-041 msg 69) round trip", delegate {
            byte[] bytes = Write(delegate(BinaryWriter w) { ReviveWire.WriteLink(w, 3, 10.5f, -4f, 0f, 6.5f); });
            Check(bytes.Length == ReviveWire.LinkBytes && bytes.Length == 17 && bytes[0] == 3, "room link layout (" + bytes.Length + " bytes)");
            ReviveWire.Link l = ReviveWire.ReadLink(Read(bytes));
            Check(l.Seat == 3, "seat"); Near(l.X, 10.5f); Near(l.Y, -4f); Near(l.Z, 0f); Near(l.Health, 6.5f);
        });
        Run("both down has empty body", delegate { Check(Write(delegate(BinaryWriter w) { }).Length == 0, "nonempty body"); });
        Run("partner pause and resume over bus", delegate {
            Bus[] b = Network(2); MenuPresence[] m = Menus(b); Pause(b[1], true);
            Check(MenuPresence.WantRemotePause(true, m[0].HasPartner(1), true, true, false, true), "partner did not pause");
            Pause(b[1], false); Check(!MenuPresence.WantRemotePause(true, m[0].HasPartner(1), true, true, false, true), "partner did not resume");
        });
        Run("multiple partners pause independently", delegate {
            Bus[] b = Network(4); MenuPresence[] m = Menus(b); Pause(b[1], true); Pause(b[2], true); Pause(b[1], false);
            Check(m[0].HasPartner(1) && m[0].FirstSeat == 3, "one resume cleared another pause");
            Pause(b[2], false); Check(!m[0].HasPartner(1), "pause stuck");
        });
        Run("duplicate pause and resume are idempotent", delegate {
            Bus[] b = Network(2); MenuPresence[] m = Menus(b); Pause(b[1], true); Pause(b[1], true);
            Check(m[0].HasPartner(1), "duplicate pause lost"); Pause(b[1], false); Pause(b[1], false);
            Check(!m[0].HasPartner(1), "duplicate resume stuck");
        });
        Run("disconnect clears partner pause", delegate {
            Bus[] b = Network(3); MenuPresence[] m = Menus(b); Pause(b[1], true); b[0].Leave(2);
            Check(!m[0].HasPartner(1), "departed player still paused");
        });
        Run("local pause alone is not partner pause", delegate {
            MenuPresence m = new MenuPresence(); m.Set(1, true); Check(!m.HasPartner(1), "local seat counted as partner");
            Check(MenuPresence.LocalPaused(true, true) && !MenuPresence.LocalPaused(false, true), "local pause gate");
        });
        Run("remote pause waits for session and gameplay", delegate {
            MenuPresence m = new MenuPresence(); m.Set(2, true);
            Check(!MenuPresence.WantRemotePause(true, m.HasPartner(1), false, true, false, true), "paused before ready");
            Check(!MenuPresence.WantRemotePause(true, m.HasPartner(1), true, true, false, false), "paused outside gameplay");
            Check(!MenuPresence.WantRemotePause(true, m.HasPartner(1), true, true, true, true), "paused while respawning");
            Check(!MenuPresence.WantRemotePause(false, m.HasPartner(1), true, true, false, true), "paused offline");
        });
        Run("four seat bus keeps message sender", delegate {
            Bus[] b = Network(4); int sender = 0; ReviveWire.Position p = new ReviveWire.Position();
            b[2].On(60, delegate(BinaryReader r) { sender = b[2].Sender; p = ReviveWire.ReadPosition(r); });
            b[3].Send(60, true, delegate(BinaryWriter w) { ReviveWire.WritePosition(w, 4, 1f, 2f, 3f); });
            Check(sender == 4 && p.Seat == 4, "wrong sender or seat"); Near(p.Z, 3f);
        });
        Run("local both down triggers checkpoint", delegate {
            Check(ReviveLogic.DecideLocalDeath(true, false, false) == DeathOutcome.Checkpoint, "missing checkpoint");
            Check(ReviveLogic.DecideLocalDeath(false, true, false) == DeathOutcome.Downed, "living partner did not enable revive");
        });
        Run("online only host decides checkpoint", delegate {
            Check(ReviveLogic.DecideOnlineDeath(true, true) == DeathOutcome.Checkpoint, "host did not wipe");
            Check(ReviveLogic.DecideOnlineDeath(true, false) == DeathOutcome.Downed, "guest decided wipe");
        });
        Run("multi respawn timer reaches thirty seconds", delegate {
            Clock clock = new Clock(); DownedState d = Down(1);
            clock.Advance(29f); Check(!ReviveLogic.RespawnDue(d, clock.Time), "respawn early");
            float start = clock.Time; clock.Advance(1f); Check(ReviveLogic.RespawnDue(d, clock.Time - start), "respawn late");
        });
        Run("duplicate and forged revived messages over bus", delegate {
            Bus[] b = Network(3); DownedRoster<DownedState> roster = Roster(); roster.TryAdd(2, Down(2)); int accepted = 0;
            b[0].On(62, delegate(BinaryReader reader) {
                ReviveWire.Position p = ReviveWire.ReadPosition(reader);
                if (roster.TakeRevived(p.Seat, b[0].Sender) != null) accepted++;
            });
            Action<BinaryWriter> revived = delegate(BinaryWriter w) { ReviveWire.WritePosition(w, 2, 1f, 2f, 3f); };
            b[2].Send(62, true, revived); Check(roster.Seats[2] != null, "wrong sender cleared seat");
            b[1].Send(62, true, revived); b[1].Send(62, true, revived);
            Check(accepted == 1 && roster.Seats[2] == null, "duplicate applied");
        });
        Run("wire bytes match original protocol", delegate {
            Bytes(Write(delegate(BinaryWriter w) { ReviveWire.WritePosition(w, 4, 1.25f, -2f, 3f); }),
                4, 0, 0, 160, 63, 0, 0, 0, 192, 0, 0, 64, 64);
            Bytes(Write(delegate(BinaryWriter w) { ReviveWire.WriteRevive(w, 2, 3.5f); }), 2, 0, 0, 96, 64);
            Bytes(Write(delegate(BinaryWriter w) { ReviveWire.WriteProgress(w, 2, 4, 0.75f, 1.5f); }),
                2, 4, 0, 0, 64, 63, 0, 0, 192, 63);
        });
        Run("network broadcast serializes once", delegate {
            Bus[] b = Network(4); int serializations = 0;
            b[0].Send(60, true, delegate(BinaryWriter w) { serializations++; w.Write((byte)1); });
            Check(serializations == 1, "serialized " + serializations + " times");
        });
        Run("old downed after revival is accepted by unsequenced protocol", delegate {
            DownedRoster<DownedState> r = Roster(); r.TryAdd(2, Down(2)); r.TakeRevived(2, 2);
            Check(r.TryAdd(2, Down(2)), "old packet unexpectedly distinguishable");
        });
        Run("old menu update follows arrival order", delegate {
            Bus[] b = Network(2); MenuPresence[] m = Menus(b);
            Pause(b[1], true); Pause(b[1], false); Pause(b[1], true);
            Check(m[0].InMenu(2), "last update was ignored");
        });
        // T-031: a Steam invite accepted while an online game is already running.
        Run("invite replaces an idle session in the menus", delegate {
            Check(InviteRules.ReplacesSession(true, false, false, false), "idle host page / slots after 开房 kept the old session");
        });
        Run("invite never replaces a session with a partner, in gameplay or loading", delegate {
            Check(!InviteRules.ReplacesSession(true, true, false, false), "partner present: session ended");
            Check(!InviteRules.ReplacesSession(true, false, true, false), "in gameplay: session ended");
            Check(!InviteRules.ReplacesSession(true, false, false, true), "loading a save: session ended");
            Check(!InviteRules.ReplacesSession(false, false, false, false), "offline: nothing to replace");
        });
        Run("invite banner names what the player must do", delegate {
            Check(InviteRules.BannerKey(true, true) == "steam.invite_end", "playing with someone: " + InviteRules.BannerKey(true, true));
            Check(InviteRules.BannerKey(true, false) == "steam.invite_menu", "online alone: " + InviteRules.BannerKey(true, false));
            Check(InviteRules.BannerKey(false, false) == "steam.invite_menu", "offline in gameplay: " + InviteRules.BannerKey(false, false));
        });
        Run("online menus (including the map and picker), popups and F8 all keep Ori live", delegate {
            Check(MenuPresence.ScreenRunsLive(true, true, false, false), "menu isn't live");
            Check(MenuPresence.ScreenRunsLive(true, false, true, false), "popup isn't live");
            Check(MenuPresence.ScreenRunsLive(true, false, false, true), "F8 isn't live");
            Check(!MenuPresence.HoldOri(true, false, false), "an open screen holds Ori");
        });
        Run("online story and respawn still hold Ori", delegate {
            Check(MenuPresence.HoldOri(true, true, false), "story no longer holds Ori");
            Check(MenuPresence.HoldOri(true, false, true), "respawn no longer holds Ori");
        });
        Run("closed screens don't block online; offline screens keep native behaviour", delegate {
            Check(!MenuPresence.ScreenRunsLive(true, false, false, false), "no open screen");
            Check(!MenuPresence.ScreenRunsLive(false, true, true, true), "offline screen went live");
            Check(!MenuPresence.HoldOri(false, true, true), "offline state overrides native hold");
        });
        Run("F8 and popup restore independent input locks on disconnect", delegate {
            ScreenInputLocks locks = new ScreenInputLocks();
            object panel = new object(), popup = new object();
            foreach (bool popupFirst in new bool[] { false, true })
            {
                locks.Set(popupFirst ? popup : panel, true);
                locks.Set(popupFirst ? panel : popup, true);
                locks.Set(popup, false);
                Check(locks.IsLocked(false), "closing the popup unlocked the open F8 panel");
                locks.Set(panel, false);
                Check(!locks.IsLocked(false), "closing F8 left the popup lock behind");
                Check(locks.IsLocked(true), "closing the screens cleared the game's own lock");
            }
        });
        Run("the F8 panel locks its opening seat; native screens still lock the primary", delegate {
            Check(MenuPresence.ScreenLocksSeat(4, 2, false, 4), "F8 did not lock the extra seat");
            Check(!MenuPresence.ScreenLocksSeat(2, 2, false, 4), "F8 locked the primary instead of the opening seat");
            Check(MenuPresence.ScreenLocksSeat(2, 2, true, 4), "native menu lost the primary lock under F8");
            Check(!MenuPresence.ScreenLocksSeat(2, 2, false, 0), "unassigned pad locked the primary");
        });
        Run("disconnect restores skipped menu and popup suspends exactly once", delegate {
            int skipped = 1, vanilla = 0, popupSkipped = 1, popupVanilla = 0;
            Check(MenuPresence.RestoreScreenSuspends(ref skipped, ref vanilla) == 1, "native menu suspend was not restored");
            Check(MenuPresence.RestoreScreenSuspends(ref popupSkipped, ref popupVanilla) == 1, "native popup suspend was not restored");
            Check(MenuPresence.RestoreScreenSuspends(ref skipped, ref vanilla) == 0, "native suspend was restored twice");
            Check(MenuPresence.ConsumeMenuResume(ref popupSkipped, ref popupVanilla), "popup close skipped the restored resume");
            Check(popupSkipped == 0 && popupVanilla == 0 && !MenuPresence.ConsumeMenuResume(ref popupSkipped, ref popupVanilla), "popup close left another resume owed");
            Check(MenuPresence.ConsumeMenuResume(ref skipped, ref vanilla), "menu close skipped the restored resume");
            Check(skipped == 0 && vanilla == 0 && !MenuPresence.ConsumeMenuResume(ref skipped, ref vanilla), "menu close left another resume owed");
        });
        Run("closing an online screen leaves no suspend to restore on disconnect", delegate {
            int skipped = 1, vanilla = 0;
            Check(!MenuPresence.ConsumeMenuResume(ref skipped, ref vanilla), "close resumed a suspension it never took");
            Check(MenuPresence.RestoreScreenSuspends(ref skipped, ref vanilla) == 0, "closed screen was suspended on disconnect");
        });
        SpectateTests.Run(Run);
        SoulLinkTests.Run(Run);
        ReviveLeftoverTests.Run(Run);
        Console.WriteLine("RULES" + (cases - failed) + "/" + cases + (failed == 0 ? " PASS" : " FAIL"));
        return failed == 0 ? 0 : 1;
    }
}
