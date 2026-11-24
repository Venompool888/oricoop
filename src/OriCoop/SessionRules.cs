using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using HarmonyLib;
using UnityEngine;

using OriCoop.Seams;
using OriCoop.Rules;

namespace OriCoop
{
    public static class SessionRules
    {
        internal static IRulesHost Host { get; private set; }
        const byte MenuMessage = 53;
        class ScreenDebt
        {
            public HashSet<ISuspendable> Excluded, Restored;
            public int Vanilla, Skipped;
        }
        static readonly ScreenDebts<MenuScreenManager, ScreenDebt> Menus = new ScreenDebts<MenuScreenManager, ScreenDebt>();
        static readonly ScreenDebts<ResumeGameController, ScreenDebt> Focus = new ScreenDebts<ResumeGameController, ScreenDebt>();
        static bool s_paused, s_hold, s_ended;
        static readonly MenuReport s_report = new MenuReport();
        public static int SkippedPopupSuspends, SkippedMenuSuspends;
        // ORICOOP_NOPAUSE_OFF=1: screens pause online again.
        static readonly bool NoPauseOff = Environment.GetEnvironmentVariable("ORICOOP_NOPAUSE_OFF") == "1";
        internal static bool NoPause { get { return Online && !NoPauseOff && !s_ended; } }
        // Suspendables this PC froze because the partner paused, released exactly once.
        static List<ISuspendable> s_remoteHeld;
        static readonly FieldInfo RegisteredField = AccessTools.Field(typeof(SuspensionManager), "s_suspendedIterate");
        public static bool PausedByPartner { get { return s_remoteHeld != null; } }
        public static bool LocalPaused { get { return MenuPresence.LocalPaused(Online, s_paused); } }
        public static int RemotePauses;
        static SeinCharacter s_held, s_dead;
        static Vector3 s_anchor;
        static float s_settleFrom = -10f, s_settleUntil;
        static float s_respawnAt;
        static readonly MenuPresence s_menuSeats = new MenuPresence();
        public static bool PartnerInMenu
        {
            get { return s_menuSeats.HasPartner(Host.LocalSeat); }
        }
        public static bool InMenu(int seat) { return s_menuSeats.InMenu(seat); }
        public static string PauseText
        {
            get
            {
                int first = s_menuSeats.FirstSeat;
                return first == 0 ? "" : Host.Format("pause.by", "P" + first);
            }
        }
        public static int Disconnects, Sessions;
        public static int GuardedShaderWrites;
        static readonly HashSet<string> ShaderCallers = new HashSet<string>();
        public static bool Respawning { get { return s_dead != null; } }
        static bool Online { get { return Services.Net.Enabled && Services.Net.Connected; } }
        static SeinCharacter Own { get { return Host.P1 != null ? Host.P1 : Characters.Sein; } }
        static SeinCharacter PanelOri { get { return CoopMenu.OpeningSeat == 0 ? null : Players.OriOf(CoopMenu.OpeningSeat); } }
        static bool NativeMenuOpen { get { return UI.MainMenuVisible || CsRoomPage.InGame; } }
        internal static bool LiveScreen
        {
            get { return MenuPresence.ScreenRunsLive(NoPause && !s_paused, NativeMenuOpen, PopupOpen, Host.CoopMenuOpen); }
        }
        static bool LocalScreenInputLocked(int seat)
        {
            return NoPause && !s_paused && MenuPresence.ScreenLocksSeat(seat, Host.LocalSeat,
                NativeMenuOpen || PopupOpen, CoopMenu.OpeningSeat);
        }
        internal static bool ScreenInputLocked(SeinCharacter ori)
        {
            if (ori == null) return false;
            if (ori == Own) return LocalScreenInputLocked(Host.LocalSeat);
            Puppet puppet = Players.OfSein(ori);
            return puppet != null && (puppet.IsLocal ? LocalScreenInputLocked(puppet.Seat) : puppet.ScreenInputLocked);
        }
        // Offline screen locks compose with the native lock without writing to it.
        static readonly ScreenInputLocks s_inputLocks = new ScreenInputLocks();
        internal static void SetScreenInputLock(object owner, bool locked) { s_inputLocks.Set(owner, locked); }

        internal static void CloseScreensOnDeath(SeinCharacter ori)
        {
            if (!NoPause || !ScreenInputLocked(ori)) return;
            if (ori == PanelOri) CoopMenu.Close();
            if (ori != Own || !(NativeMenuOpen || PopupOpen)) return;
            s_departureWell = null;
            HidePopups();
            if (CsRoomPage.InGame) CsRoomPage.Close();
            PauseMenuEntry.Release();
            if (UI.Menu != null)
            {
                UI.Menu.HideResumeScreen();
                UI.Menu.HideMenuScreen(UI.Menu.CurrentScreen != MenuScreenManager.Screens.Options);
            }
        }
        // T-248 (#336): only the screens that were open when an Ori went down are closed (retried until they are gone);
        // a downed player may open the pause menu or the co-op panel again afterwards, to leave or change settings.
        static readonly DownedScreens s_downedScreens = new DownedScreens(4);
        static readonly bool[] s_downedScreenOpen = new bool[5];
        static void CloseInvalidScreens()
        {
            if (!NoPause) { s_downedScreens.Reset(); Array.Clear(s_downedScreenOpen, 0, s_downedScreenOpen.Length); return; }
            foreach (int seat in Players.LocalSeats)
            {
                if (seat < 1 || seat > 4) continue;
                SeinCharacter ori = seat == Host.LocalSeat ? Own : Players.OriOf(seat);
                bool downed = ori != null && Revive.IsDowned(ori), open = downed && ScreenInputLocked(ori);
                bool wasClosing = s_downedScreens.Closing(seat);
                bool close = s_downedScreens.Tick(seat, downed, open);
                if (close)
                {
                    if (!wasClosing) Host.Trace("hud", "screen.close_downed", "who", "P" + seat, "reason", "open when it went down",
                        "menu", NativeMenuOpen, "popup", PopupOpen, "panel", CoopMenu.OpeningSeat == seat);
                    CloseScreensOnDeath(ori);
                }
                else if (open && !s_downedScreenOpen[seat])
                    Host.Trace("hud", "screen.open_downed", "who", "P" + seat, "menu", NativeMenuOpen, "popup", PopupOpen,
                        "panel", CoopMenu.OpeningSeat == seat, "msg", "kept open: a downed player may leave or change settings");
                s_downedScreenOpen[seat] = open && !close;
            }
            KeepTeleportPicker();
        }
        public static bool LocalLocked { get { return MenuPresence.HoldOri(Online, Host.StoryWatching, Respawning); } }

        public static void Init()
        {
            Host = new GameRulesHost();
            Services.Net.On(MenuMessage, delegate(BinaryReader r)
            {
                s_menuSeats.Receive(Services.Net.Sender, r);
            });
            Services.Net.SessionStarted += Started; Services.Net.SessionEnded += Ended;
            Services.Net.PlayerLeft += delegate(int seat) { s_menuSeats.Set(seat, false); };
            Services.Net.PlayerJoined += delegate(int seat)
            { RefreshMenu(); if (s_paused) Services.Net.Send(MenuMessage, true, w => w.Write(true)); };
            Host.InitStory();
        }
        static void Started()
        {
            PruneScreens(); PrunePopups();
            ReleaseRemotePause(false);
            Sessions++; s_ended = false; s_menuSeats.Clear(); s_report.SessionStarted();
            s_hold = false; s_held = null; Host.ResetStory();
            Host.SessionStarted();
            if (!NoPause) return;
            foreach (var pair in Menus.CurrentSnapshot()) SkipNativeSuspends(pair.Value);
            foreach (var pair in Focus.CurrentSnapshot()) SkipNativeSuspends(pair.Value);
            CoopMenu.SessionPauseChanged(true);
            StartLivePopups();
            RefreshMenu();
            ScreenOpened();
        }
        static void Ended()
        {
            ReleaseRemotePause(false);
            Disconnects++; s_ended = true; s_menuSeats.Clear(); s_paused = s_hold = false; s_report.SessionEnded(); s_held = null;
            Host.ResetStory();
            if (s_dead != null) RespawnGuest(false);
            PruneScreens(); PrunePopups();
            HashSet<ISuspendable> popupUi = PopupSuspendables();
            foreach (var pair in Menus.CurrentSnapshot())
                if (pair.Key != null && pair.Key.MainMenuVisible) RestoreSuspends(pair.Value, popupUi);
            foreach (var pair in Focus.CurrentSnapshot()) RestoreSuspends(pair.Value, popupUi);
            CoopMenu.SessionPauseChanged(false);
            RestorePopups(popupUi);
            Host.SessionEnded();
        }
        static void MenuSuspend(HashSet<ISuspendable> excluded, MenuScreenManager menu)
        {
            ScreenDebt d;
            if (!Menus.TryGet(menu, out d)) { d = new ScreenDebt(); Menus.Set(menu, d); }
            d.Excluded = excluded;
            // A skipped suspend must also skip its matching resume.
            if (NoPause) { d.Skipped++; SkippedMenuSuspends++; }
            else { d.Vanilla++; SuspensionManager.SuspendExcluding(excluded); }
            RefreshMenu();
        }
        static void MenuResume(HashSet<ISuspendable> excluded, MenuScreenManager menu)
        {
            ScreenDebt d;
            if (Menus.TryGet(menu, out d))
            {
                bool resume = MenuPresence.ConsumeMenuResume(ref d.Skipped, ref d.Vanilla);
                if (d.Skipped + d.Vanilla == 0) Menus.Remove(menu, d);
                if (resume) ResumeSuspend(d);
            }
            else SuspensionManager.ResumeExcluding(excluded); // A pre-patch open, if injected live.
            RefreshMenu();
        }
        static void FocusSuspend(ResumeGameController screen)
        {
            ScreenDebt d = new ScreenDebt(); Focus.Set(screen, d);
            if (NoPause) d.Skipped = 1;
            else { d.Vanilla = 1; SuspensionManager.SuspendAll(); }
            ScreenOpened();
        }
        static void FocusResume(ResumeGameController screen)
        {
            ScreenDebt d;
            if (Focus.TryGet(screen, out d)) { Focus.Remove(screen, d); if (d.Vanilla > 0) ResumeSuspend(d); }
            else SuspensionManager.ResumeAll();
            RefreshMenu();
        }
        static void ResumeSuspend(ScreenDebt d)
        {
            if (d.Restored != null) SuspensionManager.Resume(d.Restored);
            else if (d.Excluded != null) SuspensionManager.ResumeExcluding(d.Excluded);
            else SuspensionManager.ResumeAll();
        }
        static void SkipNativeSuspends(ScreenDebt d)
        {
            while (d.Vanilla > 0) { d.Vanilla--; d.Skipped++; ResumeSuspend(d); }
            d.Restored = null;
        }
        static void RestoreSuspends(ScreenDebt d, HashSet<ISuspendable> popupUi)
        {
            int restored = MenuPresence.RestoreScreenSuspends(ref d.Skipped, ref d.Vanilla);
            if (restored == 0) return;
            // The boxes now exist, unlike when the menu/help suspend was first skipped.
            d.Restored = new HashSet<ISuspendable>((IEnumerable<ISuspendable>)RegisteredField.GetValue(null));
            d.Restored.ExceptWith(popupUi);
            if (d.Excluded != null) d.Restored.ExceptWith(d.Excluded);
            while (restored-- > 0) SuspensionManager.Suspend(d.Restored);
        }
        static void RefreshMenu()
        {
            PruneScreens(); PrunePopups();
            s_paused = false;
            foreach (KeyValuePair<MenuScreenManager, ScreenDebt> pair in Menus)
                if (pair.Key != null && pair.Key.MainMenuVisible && pair.Value.Vanilla > 0) s_paused = true;
            s_menuSeats.Set(Host.LocalSeat, s_paused && Online && !s_ended);
            // MenuMessage now means "this player explicitly paused": only a menu that really
            // suspended this game counts. Focus loss never pauses online (SessionScreens).
            if (s_report.ShouldSend(Online && !s_ended, s_paused))
            {
                bool paused = s_paused;
                Services.Net.Send(MenuMessage, true, delegate(BinaryWriter w) { w.Write(paused); });
            }
        }
        static void ApplyRemotePause()
        {
            bool online = Online;
            bool partner = online && PartnerInMenu;
            bool ready = partner && Services.Session.Ready;
            bool hasPlayer = ready && Characters.Sein != null;
            bool respawning = hasPlayer && Respawning;
            bool gameplay = hasPlayer && !respawning && Services.Session.LocalInGameplay;
            bool want = MenuPresence.WantRemotePause(online, partner, ready, hasPlayer, respawning, gameplay);
            if (want == (s_remoteHeld != null)) return;
            if (!want) { ReleaseRemotePause(Online); return; }
            // Freeze everything the pause menu would freeze, but leave the menu hierarchy running
            // so this player can still open their own pause menu while waiting.
            HashSet<ISuspendable> keep = new HashSet<ISuspendable>();
            if (UI.Menu != null) SuspensionManager.GetSuspendables(keep, true, UI.Menu.gameObject);
            List<ISuspendable> all = new List<ISuspendable>((IEnumerable<ISuspendable>)RegisteredField.GetValue(null));
            s_remoteHeld = new List<ISuspendable>();
            foreach (ISuspendable s in all)
                if (s as Component != null && !keep.Contains(s)) { SuspensionManager.Suspend(s); s_remoteHeld.Add(s); }
            RemotePauses++;
            Services.Log.Info("net: a player paused the game; suspended " + s_remoteHeld.Count + " objects here");
        }
        static void ReleaseRemotePause(bool announce)
        {
            if (s_remoteHeld == null) return;
            List<ISuspendable> held = s_remoteHeld; s_remoteHeld = null;
            foreach (ISuspendable s in held) if (s as Component != null) SuspensionManager.Resume(s);
            Services.Log.Info("net: remote pause released (" + held.Count + " objects)");
            if (announce && !PartnerInMenu) Host.Toast(Host.Text("session.resumed"));
        }
        static void ReleasePrunedScreen(ScreenDebt debt)
        {
            SubsystemGuard.Run("SessionRules.ReleasePrunedScreen", () => SkipNativeSuspends(debt));
        }
        static void PruneScreens()
        {
            Menus.Prune(screen => screen == null, ReleasePrunedScreen);
            if (Focus.Prune(screen => screen == null, ReleasePrunedScreen) > 0 && Focus.Count == 0)
                ResumeGameController.IsGameSuspended = false;
        }
        static readonly Action s_refreshMenu = RefreshMenu, s_respawnGuest = RespawnGuestIfDue;
        static readonly Action s_remotePause = ApplyRemotePause, s_screensTick = SessionScreens.Tick, s_closeInvalidScreens = CloseInvalidScreens;
        static readonly Action s_storyTick = TickStory, s_holdLocal = HoldLocal;
        static void TickStory() { Host.TickStory(); }

        public static void Tick()
        {
            SubsystemGuard.Run("SessionRules.RefreshMenu", s_refreshMenu);
            SubsystemGuard.Run("SessionRules.RespawnGuest", s_respawnGuest);
            SubsystemGuard.Run("SessionRules.ApplyRemotePause", s_remotePause);
            SubsystemGuard.Run("SessionRules.CloseInvalidScreens", s_closeInvalidScreens);
            SubsystemGuard.Run("SessionScreens.Tick", s_screensTick);
            SubsystemGuard.Run("StorySync.Tick", s_storyTick);
            SubsystemGuard.Run("SessionRules.HoldLocal", s_holdLocal);
        }
        static void RespawnGuestIfDue() { if (s_dead != null && Time.realtimeSinceStartup >= s_respawnAt) RespawnGuest(Online); }
        static void HoldLocal()
        {
            SeinCharacter own = Own;
            if (!LocalLocked || own == null || Respawning) { s_hold = false; s_held = null; return; }
            if (!s_hold || s_held != own)
            {
                // After a teleport (gate pull, revive, pedestal) let the Ori land first (input stays locked), then
                // anchor where it landed instead of in mid-air.
                // (IsOnGround still says the old spot right after the teleport: give physics 0.2 s first.)
                float now = Time.realtimeSinceStartup;
                if (now < s_settleUntil && (now < s_settleFrom + 0.2f || !own.PlatformBehaviour.PlatformMovement.IsOnGround)) return;
                s_anchor = own.Position; s_hold = true; s_held = own;
            }
            own.Position = s_anchor; own.Speed = Vector3.zero;
        }
        // A teleport during a story hold re-anchors at the new spot.
        internal static void PositionChanged()
        { s_hold = false; s_held = null; s_settleFrom = Time.realtimeSinceStartup; s_settleUntil = s_settleFrom + 1f; }
        public static bool HandleOwnDeathOnline(SeinDamageReciever receiver)
        {
            SeinCharacter own = Own;
            if (!Online || own == null || receiver != own.Mortality.DamageReciever || Services.Net.IsHost) return false;
            if (s_dead != null) return true;
            // Skip the entire vanilla OnKill: no DisableLate, OnPlayerDeath, or restore coroutine
            // is allowed to rewind the guest's copy. Only our short fade owns this local death.
            s_dead = own; s_respawnAt = Time.realtimeSinceStartup + 0.45f;
            own.Speed = Vector3.zero; own.Active = false;
            UI.Fader.Fade(0.2f, 0.25f, 0.2f, null, null);
            return true;
        }
        static void RespawnGuest(bool besideHost)
        {
            SeinCharacter own = s_dead; s_dead = null; s_hold = false;
            if (own == null) return;
            own.Active = true;
            if (besideHost)
            {
                Vector3 position;
                if (!Revive.IsDowned(Revive.SeinAt(1)) && Host.RemotePos(1, out position))
                    Host.Teleport(own, position + Vector3.right);
                else
                {
                    SeinCharacter teammate = Revive.NearestLiving(own.Position, own);
                    if (teammate != null) Host.Teleport(own, teammate.Position + Vector3.right);
                }
            }
            own.Speed = Vector3.zero; own.Mortality.DamageReciever.enabled = true;
            own.Mortality.Health.RestoreAllHealth();
            own.Mortality.DamageReciever.SpriteMaterialTintColor(new Color(0, 0, 0, 0));
            own.Mortality.DamageReciever.MakeInvincible(1f);
            GoThroughPlatformHandler platforms = own.GetComponent<GoThroughPlatformHandler>();
            if (platforms != null) platforms.UpdateColliders();
            own.PlatformBehaviour.CapsuleController.EnableCollider(true);
            if (besideHost) PartnerFx.PlayRevive(own, own.Position);
            Services.Log.Info("net: guest respawn without checkpoint at " + own.Position);
        }
        // Replace only the screen's call sites, passing its receiver after the native arguments.
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, string original, string replacement, Type type = null)
        {
            MethodInfo from = AccessTools.Method(type ?? typeof(SuspensionManager), original);
            MethodInfo to = AccessTools.Method(typeof(SessionRules), replacement);
            bool found = false;
            foreach (CodeInstruction instruction in code)
            {
                if (instruction.Calls(from))
                {
                    CodeInstruction receiver = new CodeInstruction(OpCodes.Ldarg_0);
                    receiver.labels.AddRange(instruction.labels); instruction.labels.Clear();
                    receiver.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                    yield return receiver;
                    instruction.opcode = OpCodes.Call; instruction.operand = to; found = true;
                }
                yield return instruction;
            }
            if (!found) throw new InvalidOperationException("Missing screen hook: " + original);
        }
        [HarmonyPatch(typeof(MenuScreenManager), "ShowMenuScreen", new Type[] { typeof(MenuScreenManager.Screens), typeof(bool) })]
        static class OpenMenu
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "SuspendExcluding", "MenuSuspend"); }
            static void Postfix() { ScreenOpened(); }
        }
        [HarmonyPatch(typeof(MenuScreenManager), "HideMenuScreen")]
        static class CloseMenu
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "ResumeExcluding", "MenuResume"); }
        }
        [HarmonyPatch(typeof(ResumeGameController), "FixedUpdate")]
        static class OpenFocus
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "SuspendAll", "FocusSuspend"); }
        }
        [HarmonyPatch(typeof(ResumeGameController), "Hide")]
        static class CloseFocus
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "ResumeAll", "FocusResume"); }
        }
        static SavePedestal s_departureWell;
        [HarmonyPatch(typeof(SavePedestal), "TeleportOnPedestal")]
        static class OpenTeleportPicker
        {
            static void Postfix(SavePedestal __instance) { s_departureWell = __instance; }
        }
        static bool KeepTeleportPicker()
        {
            if (!NoPause || GameMapUI.Instance == null || !GameMapUI.Instance.ShowingTeleporters) return true;
            if (UI.Menu == null || !UI.Menu.MainMenuVisible || UI.Menu.CurrentScreen != MenuScreenManager.Screens.WorldMap)
            {
                s_departureWell = null;
                return false;
            }
            if (s_departureWell != null && Own != null && !Revive.IsDowned(Own) && s_departureWell.DistanceToSein <= s_departureWell.Radius)
                return true;
            s_departureWell = null;
            UI.Menu.HideMenuScreen(true);
            return false;
        }
        [HarmonyPatch(typeof(GameMapTeleporters), "Advance")]
        static class CancelTeleportPicker
        {
            static bool Prefix() { return KeepTeleportPicker(); }
        }
        // Popup suspends and their matching hide callbacks are tracked per message box.
        sealed class PopupDebt : ScreenDebt
        {
            public bool Live, WasLive, Closed, LocksInput;
            public InteractiveMessageBox Interactive;
        }
        static readonly ScreenDebts<MessageBox, PopupDebt> Popups = new ScreenDebts<MessageBox, PopupDebt>();
        static PopupDebt s_openingPopup, s_closingPopup;
        public static bool PopupOpen
        {
            get { foreach (var pair in Popups) if (!pair.Value.Closed && pair.Key != null) return true; return false; }
        }
        static InteractiveMessageBox InteractiveBox(MessageBox box, PopupDebt d)
        {
            if (d.Interactive != null) return d.Interactive;
            InteractiveMessageBox interactive = box.GetComponentInParent<InteractiveMessageBox>();
            return interactive != null && interactive.MessageBox == box ? interactive : null;
        }
        internal static int HidePopups()
        {
            int hidden = 0;
            foreach (var pair in Popups.CurrentSnapshot())
            {
                if (pair.Value.Closed || PopupGone(pair.Key)) continue;
                InteractiveMessageBox interactive = InteractiveBox(pair.Key, pair.Value);
                if (interactive != null) interactive.EndMessageBox();
                else pair.Key.HideMessageScreen();
                hidden++;
            }
            return hidden;
        }
        static void SuspendPopup(PopupDebt d, HashSet<ISuspendable> excluded)
        {
            d.Excluded = excluded;
            if (d.Live) { d.Skipped++; SkippedPopupSuspends++; }
            else
            {
                d.Vanilla++;
                if (excluded != null) SuspensionManager.SuspendExcluding(excluded);
                else SuspensionManager.SuspendAll();
            }
        }
        static void PopupSuspend(object owner)
        {
            s_openingPopup = new PopupDebt { Live = NoPause };
            SuspendPopup(s_openingPopup, null);
        }
        static readonly Type PagesAction = AccessTools.TypeByName("ShowPagesAction");
        static readonly FieldInfo PagesMessage = AccessTools.Field(PagesAction, "m_message");
        static void PopupSuspendExcluding(HashSet<ISuspendable> excluded, object owner)
        {
            SuspendTrackedPopup((MessageBox)PagesMessage.GetValue(owner), excluded);
        }
        static readonly FieldInfo WispMessage = AccessTools.Field(typeof(ShowWispTextAction), "m_messageBox");
        static void WispSuspendExcluding(HashSet<ISuspendable> excluded, ShowWispTextAction owner)
        {
            SuspendTrackedPopup((MessageBox)WispMessage.GetValue(owner), excluded);
        }
        static void SuspendTrackedPopup(MessageBox box, HashSet<ISuspendable> excluded)
        {
            PopupDebt d;
            if (box != null && Popups.TryGet(box, out d) && !d.Closed) SuspendPopup(d, excluded);
            else if (!NoPause) SuspensionManager.SuspendExcluding(excluded);
        }
        static void UpdatePopupLock(PopupDebt d) { SetScreenInputLock(d, !d.Closed && !d.Live && d.LocksInput); }
        static void PopupResume(object owner)
        {
            PopupDebt d = s_closingPopup;
            if (d == null) { if (!NoPause) SuspensionManager.ResumeAll(); return; }
            if (!d.WasLive)
            {
                if (d.Vanilla > 0) { d.Vanilla = 0; SuspensionManager.ResumeAll(); }
                return;
            }
            if (MenuPresence.ConsumeMenuResume(ref d.Skipped, ref d.Vanilla)) ResumeSuspend(d);
        }
        static bool PopupGone(MessageBox box)
        {
            if (box == null) return true;
            GameObject go = box.gameObject;
            return go == null || !go.activeInHierarchy;
        }
        static void RetirePopup(PopupDebt d)
        {
            d.Closed = true;
            SetScreenInputLock(d, false);
            ReleasePrunedScreen(d);
        }
        static void PrunePopups() { Popups.Prune(PopupGone, RetirePopup); }
        static HashSet<ISuspendable> PopupSuspendables()
        {
            HashSet<ISuspendable> keep = new HashSet<ISuspendable>();
            foreach (var pair in Popups.CurrentSnapshot())
            {
                if (pair.Value.Closed || PopupGone(pair.Key)) continue;
                keep.UnionWith(pair.Key.GetSuspendables());
                InteractiveMessageBox interactive = InteractiveBox(pair.Key, pair.Value);
                if (interactive != null) keep.Add(interactive);
            }
            return keep;
        }
        static void StartLivePopups()
        {
            foreach (var pair in Popups.CurrentSnapshot())
            {
                PopupDebt d = pair.Value;
                if (d.Closed || PopupGone(pair.Key)) continue;
                d.Live = d.WasLive = true;
                UpdatePopupLock(d);
                SkipNativeSuspends(d);
            }
        }
        static void RestorePopups(HashSet<ISuspendable> popupUi)
        {
            foreach (var pair in Popups.CurrentSnapshot())
            {
                PopupDebt d = pair.Value;
                if (d.Closed || !d.Live || PopupGone(pair.Key)) continue;
                d.Live = false;
                UpdatePopupLock(d);
                RestoreSuspends(d, popupUi);
            }
        }
        [HarmonyPatch(typeof(MessageControllerB), "ShowMessageBoxB")]
        static class PopupBox
        {
            static void Postfix(MessageControllerB __instance, GameObject messageBoxPrefab, MessageBox __result)
            {
                PopupDebt d = s_openingPopup;
                s_openingPopup = null;
                if (__result == null) return;
                if (d == null)
                {
                    if (messageBoxPrefab != __instance.HelpMessage && messageBoxPrefab != __instance.AbilityMessage
                        && messageBoxPrefab != __instance.PickupMessage) return;
                    d = new PopupDebt { Live = NoPause };
                }
                d.WasLive = d.Live;
                PopupDebt previous = Popups.Set(__result, d);
                if (previous != null) RetirePopup(previous);
                if (!Popups.IsCurrent(__result, d)) return;
                UpdatePopupLock(d);
                ScreenOpened();
            }
        }
        [HarmonyPatch(typeof(MessageBox), "HideMessageScreen")]
        static class HidingPopup
        {
            static bool Prefix(MessageBox __instance, out PopupDebt __state)
            {
                __state = s_closingPopup;
                if (!Popups.TryGet(__instance, out s_closingPopup)) return true;
                if (s_closingPopup.Closed) return false;
                s_closingPopup.Closed = true;
                UpdatePopupLock(s_closingPopup);
                return true;
            }
            static void Finalizer(PopupDebt __state) { s_closingPopup = __state; }
        }
        static void SetInteractiveLock(GameController controller, bool locked, InteractiveMessageBox interactive)
        {
            PopupDebt d;
            if (interactive.MessageBox != null && Popups.TryGet(interactive.MessageBox, out d))
            {
                if (locked) d.Interactive = interactive;
                d.LocksInput = locked;
                UpdatePopupLock(d);
                return;
            }
            controller.LockInput = locked;
        }
        static void SetPagesLock(GameController controller, bool locked, object owner)
        {
            if (locked) { s_openingPopup = new PopupDebt { Live = NoPause, LocksInput = true }; return; }
            MessageBox box = (MessageBox)PagesMessage.GetValue(owner);
            PopupDebt d;
            if (box != null && Popups.TryGet(box, out d))
            {
                d.LocksInput = false;
                UpdatePopupLock(d);
            }
        }
        [HarmonyPatch]
        static class InteractiveLock
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(InteractiveMessageBox), "Start");
                yield return AccessTools.Method(typeof(InteractiveMessageBox), "EndMessageBox");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(code, "set_LockInput", "SetInteractiveLock", typeof(GameController)); }
        }
        [HarmonyPatch]
        static class OpenHelp
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ShowHelpTextAction), "Perform");
                yield return AccessTools.Method(typeof(InventoryManager), "OnMenuItemPressed");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(Replace(code, "SuspendAll", "PopupSuspend"), "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch(typeof(ShowWispTextAction), "Perform")]
        static class OpenWisp
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(code, "SuspendExcluding", "WispSuspendExcluding"); }
        }
        [HarmonyPatch]
        static class ClosePopup
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(ShowHelpTextAction), "OnMessageScreenHide");
                yield return AccessTools.Method(typeof(InventoryManager), "OnMessageScreenHide");
                yield return AccessTools.Method(typeof(ShowWispTextAction), "OnFinishedReading");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(code, "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch]
        static class OpenPages
        {
            static MethodBase TargetMethod() { return AccessTools.Method(PagesAction, "Perform"); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(Replace(code, "set_LockInput", "SetPagesLock", typeof(GameController)), "SuspendExcluding", "PopupSuspendExcluding"); }
        }
        [HarmonyPatch]
        static class ClosePages
        {
            static MethodBase TargetMethod() { return AccessTools.Method(PagesAction, "OnFinishedReading"); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(Replace(code, "set_LockInput", "SetPagesLock", typeof(GameController)), "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch]
        static class StopPages
        {
            static MethodBase TargetMethod() { return AccessTools.Method(PagesAction, "Stop"); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            { return Replace(code, "set_LockInput", "SetPagesLock", typeof(GameController)); }
        }
        [HarmonyPatch(typeof(MenuScreenManager), "FixedUpdate")]
        static class BlockMenuKeysUnderOverlay
        {
            static bool Prefix() { return !NoPause || !(PopupOpen || Host.CoopMenuOpen || CsRoomPage.InGame); }
        }
        [HarmonyPatch(typeof(CleverMenuItemSelectionManager), "FixedUpdate")]
        static class BlockNavigationUnderPopup
        {
            static bool Prefix(CleverMenuItemSelectionManager __instance)
            { return !NoPause || !PopupOpen || !UI.MainMenuVisible || !__instance.transform.IsChildOf(UI.Menu.transform); }
        }
        [HarmonyPatch(typeof(GameController), "get_InputLocked")]
        static class NeutralFrames
        {
            static void Postfix(ref bool __result) { __result = s_inputLocks.IsLocked(__result) || (!Host.Inside && LocalLocked); }
        }
        [HarmonyPatch(typeof(SeinController), "get_InputLocked")]
        static class LockOwnInput
        {
            static void Postfix(SeinController __instance, ref bool __result)
            {
                if (s_inputLocks.IsLocked(__result) || (!s_ignoreScreenInputLock && ScreenInputLocked(__instance.Sein))
                    || (Own != null && __instance == Own.Controller && LocalLocked)) __result = true;
            }
        }
        static bool s_ignoreScreenInputLock;
        internal static bool CanMoveIgnoringScreen(SeinController controller)
        {
            if (!ScreenInputLocked(controller.Sein)) return controller.CanMove;
            s_ignoreScreenInputLock = true;
            try { return controller.CanMove; }
            finally { s_ignoreScreenInputLock = false; }
        }
        // Damage, enemy proximity and menu navigation use the underlying movement state.
        [HarmonyPatch]
        static class LiveScreenObservers
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SeinDamageReciever), "OnRecieveDamage");
                yield return AccessTools.PropertyGetter(typeof(EntityController), "NearSein");
                yield return AccessTools.Method(typeof(MenuScreenManager), "FixedUpdate");
            }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                MethodInfo from = AccessTools.PropertyGetter(typeof(SeinController), "CanMove");
                MethodInfo to = AccessTools.Method(typeof(SessionRules), "CanMoveIgnoringScreen");
                foreach (CodeInstruction instruction in code)
                {
                    if (instruction.Calls(from)) { instruction.opcode = OpCodes.Call; instruction.operand = to; }
                    yield return instruction;
                }
            }
        }
        // Ability states and some world scripts read buttons without checking the controller lock.
        [HarmonyPatch]
        static class ScreenGameplayInput
        {
            static readonly InputState Neutral = new InputState();
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SeinInput), "Update");
                yield return AccessTools.Method(typeof(SeinLogicCycle), "FixedUpdate");
                yield return AccessTools.Method(typeof(RockTreeSetup), "FixedUpdate");
                yield return AccessTools.Method(typeof(ForcedSync), "Tick");
            }
            [HarmonyPriority(Priority.Last)]
            static void Prefix(out bool __state)
            {
                __state = !Host.Inside && ScreenInputLocked(Own);
                if (__state) Neutral.Swap();
            }
            static void Finalizer(bool __state) { if (__state) Neutral.Swap(); }
        }
        [HarmonyPatch(typeof(ButtonPressedCondition), "Validate")]
        static class BlockScreenButtonTriggers
        {
            static bool Prefix() { return !ScreenInputLocked(Characters.Sein); }
        }
        internal static void ScreenOpened()
        {
            SeinCharacter own = Own, panel = PanelOri;
            if (ScreenInputLocked(own)) CancelCharges(own);
            if (panel != own && ScreenInputLocked(panel)) CancelCharges(panel);
        }
        static void CancelCharges(SeinCharacter ori)
        {
            SeinAbilities abilities = ori.Abilities;
            if (abilities.Grenade != null) abilities.Grenade.CancelAiming();
            // OnExit cancels a partial charge and calls the patched burst for a full charge.
            if (abilities.ChargeFlame != null) abilities.ChargeFlame.OnExit();
            if (ori.SoulFlame != null) CancelScreenSoulFlame.CancelPendingCast(ori.SoulFlame);
        }
        [HarmonyPatch(typeof(SeinChargeFlameAbility), "ReleaseChargeBurst")]
        static class CancelScreenChargeFlame
        {
            static bool Prefix(SeinChargeFlameAbility __instance, SeinCharacter ___m_sein)
            {
                if (!ScreenInputLocked(___m_sein)) return true;
                __instance.RestoreEnergy();
                __instance.OnRestoreCheckpoint();
                return false;
            }
        }
        [HarmonyPatch(typeof(SeinSoulFlame), "UpdateCharacterState")]
        static class CancelScreenSoulFlame
        {
            static readonly FieldInfo Casting = AccessTools.Field(typeof(SeinSoulFlame), "m_isCasting");
            static readonly FieldInfo TapTime = AccessTools.Field(typeof(SeinSoulFlame), "m_tapRemainingTime");
            internal static void CancelPendingCast(SeinSoulFlame flame)
            {
                Casting.SetValue(flame, false);
                TapTime.SetValue(flame, 0f);
            }
            static void Prefix(SeinCharacter ___m_sein, ref bool ___m_isCasting, ref float ___m_tapRemainingTime)
            {
                if (!ScreenInputLocked(___m_sein)) return;
                ___m_isCasting = false;
                ___m_tapRemainingTime = 0f;
            }
        }
        [HarmonyPatch(typeof(SeinController), "FixedUpdate")]
        static class KeepOwnStill
        {
            static void Postfix(SeinController __instance)
            { if (Own != null && __instance == Own.Controller && !Host.Inside) HoldLocal(); }
        }
        [HarmonyPatch(typeof(RestoreCheckpointController), "FinishLoading")]
        static class HostRestored
        {
            static void Postfix()
            {
                if (Online && Services.Net.IsHost && Services.Session.Ready) Host.PushCheckpoint();
                s_hold = false;
            }
        }
        // Native restart destroys pooled effect materials before all legacy fade/flash samples stop.
        // Guard the actual write boundary, not a guessed ReviveHud/damage-tint caller. A renderer
        // can still be alive here while its material has already been destroyed.
        [HarmonyPatch(typeof(UberShaderAPI), "SetColorCustom")]
        static class TeardownColorWrite
        {
            static bool Prefix(Renderer gameObject)
            {
                if (!Services.Net.Enabled) return true;
                if (gameObject != null && gameObject.sharedMaterial != null) return true;
                GuardedShaderWrites++;
                string caller = "unknown";
                StackFrame[] frames = new StackTrace(1, false).GetFrames();
                if (frames != null) foreach (StackFrame frame in frames)
                {
                    MethodBase method = frame.GetMethod(); Type type = method == null ? null : method.DeclaringType;
                    if (type != null && typeof(LegacyAnimator).IsAssignableFrom(type))
                    { caller = type.Name + "." + method.Name; break; }
                }
                if (ShaderCallers.Add(caller)) Services.Log.Info("net: skipped invalid material write from " + caller
                    + "; renderer=" + (gameObject == null ? "destroyed/null" : gameObject.name + " material destroyed/null"));
                return false;
            }
        }
    }
}
