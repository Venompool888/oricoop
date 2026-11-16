using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    public static class SessionRules
    {
        const byte MenuMessage = 53;
        sealed class MenuDebt
        {
            public HashSet<ISuspendable> Excluded;
            public int Vanilla, Skipped;
        }
        static readonly Dictionary<MenuScreenManager, MenuDebt> Menus = new Dictionary<MenuScreenManager, MenuDebt>();
        static readonly Dictionary<ResumeGameController, MenuDebt> Focus = new Dictionary<ResumeGameController, MenuDebt>();
        static bool s_menu, s_paused, s_reported, s_hold;
        // Suspendables this PC froze because the partner paused, released exactly once.
        static List<ISuspendable> s_remoteHeld;
        static readonly FieldInfo RegisteredField = AccessTools.Field(typeof(SuspensionManager), "s_suspendedIterate");
        public static bool PausedByPartner { get { return s_remoteHeld != null; } }
        public static bool LocalPaused { get { return Online && s_paused; } }
        public static int RemotePauses;
        static SeinCharacter s_held, s_dead;
        static Vector3 s_anchor;
        static float s_respawnAt;
        public static bool PartnerInMenu;
        public static int Disconnects, Sessions;
        public static int GuardedShaderWrites;
        static readonly HashSet<string> ShaderCallers = new HashSet<string>();
        public static bool Respawning { get { return s_dead != null; } }
        static bool Online { get { return NetPlay.Enabled && NetPlay.Connected; } }
        static SeinCharacter Own { get { return P2Context.P1 != null ? P2Context.P1 : Characters.Sein; } }
        public static bool LocalLocked { get { return Online && (s_menu || Respawning || StorySync.Watching); } }

        public static void Init()
        {
            NetPlay.On(MenuMessage, delegate(BinaryReader r) { PartnerInMenu = r.ReadBoolean(); });
            NetPlay.SessionStarted += Started; NetPlay.SessionEnded += Ended;
            StorySync.Init();
        }
        static void Started()
        {
            PruneScreens();
            ReleaseRemotePause(false);
            Sessions++; PartnerInMenu = false; s_reported = false;
            s_hold = false; s_held = null; StorySync.Reset();
            NetHud.OnSessionStarted();
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
            Disconnects++; PartnerInMenu = false; s_menu = s_paused = s_reported = s_hold = false; s_held = null;
            StorySync.Reset();
            if (s_dead != null) RespawnGuest(false);
            NetHud.OnSessionEnded();
            // Keep the skip ledger until the corresponding close, even when disconnected.
        }
        static void MenuSuspend(HashSet<ISuspendable> excluded, MenuScreenManager menu)
        {
            MenuDebt d;
            if (!Menus.TryGetValue(menu, out d)) { d = new MenuDebt(); Menus.Add(menu, d); }
            d.Excluded = excluded;
            // An explicit pause (Esc / pause menu / map) pauses this game online too; RefreshMenu
            // reports it and the partner pauses as well. Only menus opened before the session
            // started were retired to Skipped in Started().
            d.Vanilla++; SuspensionManager.SuspendExcluding(excluded);
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
        static void RefreshMenu()
        {
            PruneScreens();
            s_menu = s_paused = false;
            foreach (KeyValuePair<MenuScreenManager, MenuDebt> pair in Menus)
                if (pair.Key != null && pair.Key.MainMenuVisible && pair.Value.Skipped + pair.Value.Vanilla > 0)
                { s_menu = true; if (pair.Value.Vanilla > 0) s_paused = true; }
            foreach (ResumeGameController screen in Focus.Keys) if (screen != null) s_menu = true;
            // MenuMessage now means "this player explicitly paused": only a menu that really
            // suspended this game counts. Focus loss never pauses online (SessionScreens).
            if (Online && s_paused != s_reported)
            {
                s_reported = s_paused;
                bool paused = s_paused;
                NetPlay.Send(MenuMessage, true, delegate(BinaryWriter w) { w.Write(paused); });
            }
        }
        static void ApplyRemotePause()
        {
            bool want = Online && PartnerInMenu && SaveSync.Ready && Characters.Sein != null && !Respawning
                && NetPlay.LocalInGameplay();
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
            Plugin.Log.LogInfo("net: partner paused the game; suspended " + s_remoteHeld.Count + " objects here");
        }
        static void ReleaseRemotePause(bool announce)
        {
            if (s_remoteHeld == null) return;
            List<ISuspendable> held = s_remoteHeld; s_remoteHeld = null;
            foreach (ISuspendable s in held) if (s as Component != null) SuspensionManager.Resume(s);
            Plugin.Log.LogInfo("net: remote pause released (" + held.Count + " objects)");
            if (announce && !PartnerInMenu) NetHud.Toast(L10n.T("session.resumed"));
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
        public static void Tick()
        {
            RefreshMenu();
            if (s_dead != null && Time.realtimeSinceStartup >= s_respawnAt) RespawnGuest(Online);
            ApplyRemotePause();
            SessionScreens.Tick();
            StorySync.Tick(); HoldLocal();
        }
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
            if (!Online || own == null || receiver != own.Mortality.DamageReciever || NetPlay.IsHost) return false;
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
            if (besideHost && NetPlay.HaveRemotePos) Plugin.Teleport(own, NetPlay.LatestRemotePos + Vector3.right);
            own.Speed = Vector3.zero; own.Mortality.DamageReciever.enabled = true;
            own.Mortality.Health.RestoreAllHealth();
            own.Mortality.DamageReciever.SpriteMaterialTintColor(new Color(0, 0, 0, 0));
            own.Mortality.DamageReciever.MakeInvincible(1f);
            GoThroughPlatformHandler platforms = own.GetComponent<GoThroughPlatformHandler>();
            if (platforms != null) platforms.UpdateColliders();
            own.PlatformBehaviour.CapsuleController.EnableCollider(true);
            Plugin.Log.LogInfo("net: guest respawn without checkpoint at " + own.Position);
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
        [HarmonyPatch(typeof(GameController), "get_InputLocked")]
        static class NeutralFrames
        {
            static void Postfix(ref bool __result) { if (!P2Context.Inside && LocalLocked) __result = true; }
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
            { if (Own != null && __instance == Own.Controller && !P2Context.Inside) HoldLocal(); }
        }
        [HarmonyPatch(typeof(RestoreCheckpointController), "FinishLoading")]
        static class HostRestored
        {
            static void Postfix()
            {
                if (Online && NetPlay.IsHost && SaveSync.Ready) SaveSync.PushCheckpointNow();
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
                if (!NetPlay.Enabled) return true;
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
                if (ShaderCallers.Add(caller)) Plugin.Log.LogInfo("net: skipped invalid material write from " + caller
                    + "; renderer=" + (gameObject == null ? "destroyed/null" : gameObject.name + " material destroyed/null"));
                return false;
            }
        }
        [HarmonyPatch(typeof(NetPlay), "DrawGUI")]
        static class Status
        {
            static void Postfix()
            {
                // NetHud owns the status display and toasts now.
            }
        }
    }
}
