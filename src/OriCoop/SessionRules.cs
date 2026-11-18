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
        sealed class MenuDebt
        {
            public HashSet<ISuspendable> Excluded;
            public int Vanilla, Skipped;
        }
        static readonly Dictionary<MenuScreenManager, MenuDebt> Menus = new Dictionary<MenuScreenManager, MenuDebt>();
        static readonly Dictionary<ResumeGameController, MenuDebt> Focus = new Dictionary<ResumeGameController, MenuDebt>();
        static bool s_menu, s_paused, s_reported, s_hold;
        // T-044: help / page popups and skill-tree item help whose native suspend was skipped online.
        static readonly PopupLedger<object> Popups = new PopupLedger<object>();
        public static int SkippedPopupSuspends, SkippedMenuSuspends;
        public static bool PopupOpen { get { return Popups.AnyOpen; } }
        // ORICOOP_NOPAUSE_OFF=1: screens pause online again (pre-T-044; mutation switch for the S4 checks).
        static readonly bool NoPauseOff = Environment.GetEnvironmentVariable("ORICOOP_NOPAUSE_OFF") == "1";
        static bool NoPause { get { return Online && !NoPauseOff; } }
        // Suspendables this PC froze because the partner paused, released exactly once.
        static List<ISuspendable> s_remoteHeld;
        static readonly FieldInfo RegisteredField = AccessTools.Field(typeof(SuspensionManager), "s_suspendedIterate");
        public static bool PausedByPartner { get { return s_remoteHeld != null; } }
        public static bool LocalPaused { get { return MenuPresence.LocalPaused(Online, s_paused); } }
        public static int RemotePauses;
        static SeinCharacter s_held, s_dead;
        static Vector3 s_anchor;
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
        public static bool LocalLocked { get { return Online && (s_menu || Popups.AnyOpen || Respawning || Host.StoryWatching); } }

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
            PruneScreens();
            ReleaseRemotePause(false);
            Sessions++; s_menuSeats.Clear(); s_reported = false;
            s_hold = false; s_held = null; Host.ResetStory();
            Host.SessionStarted();
            // A menu/focus screen opened offline owns a real suspend reference. Retire exactly that
            // reference now and remember that its eventual close must no longer Resume it.
            foreach (MenuDebt d in Menus.Values)
                while (d.Vanilla > 0) { SuspensionManager.ResumeExcluding(d.Excluded); d.Vanilla--; d.Skipped++; }
            foreach (MenuDebt d in Focus.Values)
                while (d.Vanilla > 0) { SuspensionManager.ResumeAll(); d.Vanilla--; d.Skipped++; }
            // SaveSync, WorldSync and EnemySync already subscribe to SessionStarted/Ended. Calling
            // them again here would erase the new handshake and ownership epoch.
        }
        static void Ended()
        {
            ReleaseRemotePause(false);
            Disconnects++; s_menuSeats.Clear(); s_menu = s_paused = s_reported = s_hold = false; s_held = null;
            Host.ResetStory();
            if (s_dead != null) RespawnGuest(false);
            Host.SessionEnded();
            // Keep the skip ledger until the corresponding close, even when disconnected.
        }
        static void MenuSuspend(HashSet<ISuspendable> excluded, MenuScreenManager menu)
        {
            MenuDebt d;
            if (!Menus.TryGetValue(menu, out d)) { d = new MenuDebt(); Menus.Add(menu, d); }
            d.Excluded = excluded;
            // T-044: online nothing pauses (pause menu, map, skill tree, inventory): the world and the enemies keep
            // running on every PC, only this player's Ori is held (LocalLocked) while the screen is open. The skipped
            // suspend is remembered so its close skips the resume too. Offline the menu pauses as before.
            if (NoPause) { d.Skipped++; SkippedMenuSuspends++; }
            else { d.Vanilla++; SuspensionManager.SuspendExcluding(excluded); }
            RefreshMenu(); HoldLocal();
        }
        static void MenuResume(HashSet<ISuspendable> excluded, MenuScreenManager menu)
        {
            MenuDebt d;
            if (Menus.TryGetValue(menu, out d))
            {
                if (d.Skipped > 0) d.Skipped--;
                else if (d.Vanilla > 0) { d.Vanilla--; SuspensionManager.ResumeExcluding(excluded); }
                if (d.Skipped + d.Vanilla == 0) Menus.Remove(menu);
            }
            else SuspensionManager.ResumeExcluding(excluded); // A pre-patch open, if injected live.
            RefreshMenu();
        }
        static void FocusSuspend(ResumeGameController screen)
        {
            MenuDebt d = new MenuDebt(); Focus[screen] = d;
            if (Online) d.Skipped = 1;
            else { d.Vanilla = 1; SuspensionManager.SuspendAll(); }
            RefreshMenu(); HoldLocal();
        }
        static void FocusResume(ResumeGameController screen)
        {
            MenuDebt d;
            if (Focus.TryGetValue(screen, out d)) { if (d.Vanilla > 0) SuspensionManager.ResumeAll(); Focus.Remove(screen); }
            else SuspensionManager.ResumeAll();
            RefreshMenu();
        }
        // T-044 popups. The receiver (the native screen) comes last: Replace() pushes it after the original arguments.
        // The message box each skipped popup opened (help and item help open theirs right after the suspend call; a page
        // popup keeps it in m_message). Its box going away without the hide event means the popup is gone.
        static readonly Dictionary<object, MessageBox> PopupBoxes = new Dictionary<object, MessageBox>();
        static object s_opening;
        // Test hook (NetStoryTest): close every popup whose suspend was skipped, the way the player's button press does.
        internal static int HideSkippedPopups()
        {
            int hidden = 0;
            foreach (MessageBox box in new List<MessageBox>(PopupBoxes.Values)) if (box != null) { box.HideMessageScreen(); hidden++; }
            return hidden;
        }
        static readonly FieldInfo PagesMessage = AccessTools.Field(AccessTools.TypeByName("ShowPagesAction"), "m_message");
        static void PopupSuspend(object owner)
        {
            s_opening = owner;
            if (Popups.Open(owner, NoPause)) SuspensionManager.SuspendAll(); else SkippedPopupSuspends++;
            HoldLocal();
        }
        static void PopupSuspendExcluding(HashSet<ISuspendable> excluded, object owner)
        {
            MessageBox box = PagesMessage == null ? null : PagesMessage.GetValue(owner) as MessageBox;
            if (box != null) PopupBoxes[owner] = box;
            if (Popups.Open(owner, NoPause)) SuspensionManager.SuspendExcluding(excluded); else SkippedPopupSuspends++;
            HoldLocal();
        }
        static void PopupResume(object owner) { if (Popups.Close(owner)) SuspensionManager.ResumeAll(); }
        static bool PopupGone(object owner)
        {
            // Help / page actions often live on inactive story objects, and ShowHelpTextAction never stores its box
            // (IsPerforming stays false): only the box it opened tells whether the popup is still up.
            if (owner as Component == null) return true;
            MessageBox box;
            if (!PopupBoxes.TryGetValue(owner, out box)) return false;
            return box == null || !box.gameObject.activeInHierarchy;
        }
        static readonly Predicate<object> s_popupGone = PopupGone;
        static void RefreshMenu()
        {
            PruneScreens();
            if (Popups.AnyOpen && Popups.Prune(s_popupGone) > 0) Services.Log.Info("net: forgot a popup that closed without its hide event");
            s_menu = s_paused = false;
            foreach (KeyValuePair<MenuScreenManager, MenuDebt> pair in Menus)
                if (pair.Key != null && pair.Key.MainMenuVisible && pair.Value.Skipped + pair.Value.Vanilla > 0)
                { s_menu = true; if (pair.Value.Vanilla > 0) s_paused = true; }
            foreach (ResumeGameController screen in Focus.Keys) if (screen != null) s_menu = true;
            s_menuSeats.Set(Host.LocalSeat, s_paused);
            // MenuMessage now means "this player explicitly paused": only a menu that really
            // suspended this game counts. Focus loss never pauses online (SessionScreens).
            if (Online && s_paused != s_reported)
            {
                s_reported = s_paused;
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
        static void PruneScreens()
        {
            List<MenuScreenManager> menus = new List<MenuScreenManager>();
            foreach (MenuScreenManager screen in Menus.Keys) if (screen == null) menus.Add(screen);
            foreach (MenuScreenManager screen in menus)
            {
                MenuDebt d = Menus[screen];
                while (d.Vanilla-- > 0) SuspensionManager.ResumeExcluding(d.Excluded);
                Menus.Remove(screen);
            }
            List<ResumeGameController> focus = new List<ResumeGameController>();
            foreach (ResumeGameController screen in Focus.Keys) if (screen == null) focus.Add(screen);
            foreach (ResumeGameController screen in focus)
            {
                MenuDebt d = Focus[screen];
                while (d.Vanilla-- > 0) SuspensionManager.ResumeAll();
                Focus.Remove(screen);
            }
            if (focus.Count > 0 && Focus.Count == 0) ResumeGameController.IsGameSuspended = false;
        }
        static readonly Action s_refreshMenu = RefreshMenu, s_respawnGuest = RespawnGuestIfDue;
        static readonly Action s_remotePause = ApplyRemotePause, s_screensTick = SessionScreens.Tick;
        static readonly Action s_storyTick = TickStory, s_holdLocal = HoldLocal;
        static void TickStory() { Host.TickStory(); }

        public static void Tick()
        {
            SubsystemGuard.Run("SessionRules.RefreshMenu", s_refreshMenu);
            SubsystemGuard.Run("SessionRules.RespawnGuest", s_respawnGuest);
            SubsystemGuard.Run("SessionRules.ApplyRemotePause", s_remotePause);
            SubsystemGuard.Run("SessionScreens.Tick", s_screensTick);
            SubsystemGuard.Run("StorySync.Tick", s_storyTick);
            SubsystemGuard.Run("SessionRules.HoldLocal", s_holdLocal);
        }
        static void RespawnGuestIfDue() { if (s_dead != null && Time.realtimeSinceStartup >= s_respawnAt) RespawnGuest(Online); }
        static void HoldLocal()
        {
            SeinCharacter own = Own;
            if (!LocalLocked || own == null || Respawning) { s_hold = false; s_held = null; return; }
            if (!s_hold || s_held != own) { s_anchor = own.Position; s_hold = true; s_held = own; }
            own.Position = s_anchor; own.Speed = Vector3.zero;
        }
        internal static void PositionChanged() { s_hold = false; s_held = null; }
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
        // Replace just these call sites. Never intercept the global SuspendAll/ResumeAll methods:
        // death, story text, PauseGameAction, and every other native suspension keep their semantics.
        static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, string original, string replacement)
        {
            MethodInfo from = AccessTools.Method(typeof(SuspensionManager), original);
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
            if (!found) throw new InvalidOperationException("S4 missing suspension hook " + original);
        }
        [HarmonyPatch(typeof(MenuScreenManager), "ShowMenuScreen", new Type[] { typeof(MenuScreenManager.Screens), typeof(bool) })]
        static class OpenMenu
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "SuspendExcluding", "MenuSuspend"); }
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
        [HarmonyPatch(typeof(MessageControllerB), "ShowMessageBoxB")]
        static class PopupBox
        {
            static void Postfix(MessageBox __result)
            {
                if (s_opening != null && __result != null) PopupBoxes[s_opening] = __result;
                s_opening = null;
            }
        }
        // T-044: native popups that freeze the game (ability help, page popups, skill-tree item help).
        [HarmonyPatch(typeof(ShowHelpTextAction), "Perform")]
        static class OpenHelp
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(Replace(instructions, "SuspendAll", "PopupSuspend"), "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch(typeof(ShowHelpTextAction), "OnMessageScreenHide")]
        static class CloseHelp
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch]
        static class OpenPages
        {
            static MethodBase TargetMethod() { return AccessTools.Method(AccessTools.TypeByName("ShowPagesAction"), "Perform"); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "SuspendExcluding", "PopupSuspendExcluding"); }
        }
        [HarmonyPatch]
        static class ClosePages
        {
            static MethodBase TargetMethod() { return AccessTools.Method(AccessTools.TypeByName("ShowPagesAction"), "OnFinishedReading"); }
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch(typeof(InventoryManager), "OnMenuItemPressed")]
        static class OpenItemHelp
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(Replace(instructions, "SuspendAll", "PopupSuspend"), "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch(typeof(InventoryManager), "OnMessageScreenHide")]
        static class CloseItemHelp
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            { return Replace(instructions, "ResumeAll", "PopupResume"); }
        }
        [HarmonyPatch(typeof(GameController), "get_InputLocked")]
        static class NeutralFrames
        {
            static void Postfix(ref bool __result) { if (!Host.Inside && LocalLocked) __result = true; }
        }
        [HarmonyPatch(typeof(SeinController), "get_InputLocked")]
        static class LockOwnInput
        {
            static void Postfix(SeinController __instance, ref bool __result)
            { if (Own != null && __instance == Own.Controller && LocalLocked) __result = true; }
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
