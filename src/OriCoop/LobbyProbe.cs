using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Game;
using HarmonyLib;
using UnityEngine;

namespace OriCoop
{
    // Screenshot probe for the online lobby and HUD (ORICOOP_LOBBY_PROBE=1, launched by src\run-lobbyprobe.ps1).
    // Drives the real flow: save slot -> native "how to play" page -> Online -> LAN host mode -> overlay lobby pages
    // (role, join input, join failure, direct host) -> Start -> in game: host chip, toast, disconnect dialog.
    // Screenshots go next to ORICOOP_TEST_OUT. (T-159: the relay pages and the relay server are gone.)
    public class LobbyProbe : MonoBehaviour
    {
        public static bool Enabled { get { return Environment.GetEnvironmentVariable("ORICOOP_LOBBY_PROBE") == "1"; } }
        static string Dir
        {
            get
            {
                string o = Environment.GetEnvironmentVariable("ORICOOP_TEST_OUT");
                return string.IsNullOrEmpty(o) ? Application.dataPath : Path.GetDirectoryName(Path.GetFullPath(o));
            }
        }
        static void Note(string s) { Plugin.Log.LogInfo("LOBBYPROBE " + s); }
        static void Shot(string name) { string p = Path.Combine(Dir, "lobby_" + name + ".png"); Application.CaptureScreenshot(p); Note("screenshot " + p); }

        void Start() { StartCoroutine(Run()); }

        static Component Overlay() { GameObject g = GameObject.Find("OriCoop_OnlineLobbyOverlay"); return g == null ? null : g.GetComponent("OnlineLobbyOverlay"); }
        static void Set(Component o, string field, object v) { Traverse.Create(o).Field(field).SetValue(v); }
        static void Call(Component o, string method, params object[] args) { Traverse.Create(o).Method(method, args).GetValue(); }
        static IEnumerator Wait(float s) { float end = Time.realtimeSinceStartup + s; while (Time.realtimeSinceStartup < end) yield return null; }

        IEnumerator Run()
        {
            Note("up");
            int slot = 1; int.TryParse(Environment.GetEnvironmentVariable("ORICOOP_SLOT") ?? "1", out slot);
            // 1. title -> save slots -> pick the slot (the native mode menu intercepts it).
            float t0 = Time.realtimeSinceStartup;
            while (!NativeModeMenu.IsOpen)
            {
                if (Time.realtimeSinceStartup - t0 > 90f) { Note("FAIL mode menu never opened"); Quit(); yield break; }
                SaveSlotsUI ui = SaveSlotsUI.Instance;
                if (ui != null && ui.IsVisible && ui.Active && ui.Items != null && ui.Items.Count > slot)
                { ui.SetCurrentItem(slot); ui.UsedSaveSlotSelected(); Note("selected slot " + slot); yield return StartCoroutine(Wait(1f)); continue; }
                if (Time.realtimeSinceStartup - t0 > 8f && (TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.PressStart || TitleScreenManager.CurrentScreen == TitleScreenManager.Screen.MainMenu))
                    TitleScreenManager.SetScreen(TitleScreenManager.Screen.SaveSlots);
                yield return StartCoroutine(Wait(1f));
            }
            yield return StartCoroutine(Wait(1f));
            MethodInfo press = AccessTools.Method(typeof(NativeModeMenu), "OnPressed");
            press.Invoke(null, new object[] { 2 }); yield return StartCoroutine(Wait(1.2f));           // Online
            Shot("0_native_online"); yield return StartCoroutine(Wait(1.5f));
            press.Invoke(null, new object[] { 1 }); yield return StartCoroutine(Wait(1.2f));           // LAN host mode -> lobby
            Component o = Overlay();
            if (o == null) { Note("FAIL lobby overlay not open (needs [Start] NativeLobby = false)"); Quit(); yield break; }
            yield return StartCoroutine(Wait(0.5f));
            Shot("1_role"); yield return StartCoroutine(Wait(1.5f));
            // 2. join input with a partly typed address
            Call(o, "GoTo", 1); yield return StartCoroutine(Wait(0.5f));
            Set(o, "m_directAddressInput", "192.168.1."); Set(o, "m_gridRow", 1); Set(o, "m_gridCol", 3); yield return StartCoroutine(Wait(0.8f));
            Shot("2_join_input"); yield return StartCoroutine(Wait(1.5f));
            // 3. join an address where nobody listens -> failure page
            Set(o, "m_directAddressInput", "127.0.0.1:1"); Call(o, "TryConnectDirect"); yield return StartCoroutine(Wait(1.0f));
            Shot("3_joining"); yield return StartCoroutine(Wait(1.5f));
            yield return StartCoroutine(Wait(6f));
            Note("phase after bad join: " + NetPlay.CurrentPhase + " problem='" + NetPlay.Problem + "'");
            Shot("4_join_failed"); yield return StartCoroutine(Wait(1.5f));
            NetPlay.End("probe"); Call(o, "GoTo", 0); yield return StartCoroutine(Wait(0.6f));
            // 4. direct host page
            OnlineLobby.Transport = "direct"; Call(o, "GoTo", 2); yield return StartCoroutine(Wait(2.5f));
            Note("direct host phase " + NetPlay.CurrentPhase);
            Shot("5_host_direct"); yield return StartCoroutine(Wait(1.5f));
            // 6. Start -> game loads; host chip + bars in game
            Call(o, "ActivateHostButton", 1);   // 0 Copy, 1 Start, 2 Cancel
            float t2 = Time.realtimeSinceStartup;
            while (!(Characters.Sein != null && Characters.Sein.gameObject.activeInHierarchy && GameController.Instance != null && !GameController.Instance.InputLocked))
            {
                if (Time.realtimeSinceStartup - t2 > 90f) { Note("FAIL never reached gameplay"); Quit(); yield break; }
                if (UI.MainMenuVisible) { UI.Menu.HideResumeScreen(); UI.Menu.HideMenuScreen(true); }
                yield return StartCoroutine(Wait(1f));
            }
            yield return StartCoroutine(Wait(4f));
            Shot("7_ingame_host_waiting"); yield return StartCoroutine(Wait(1.5f));
            NetHud.Toast("伙伴已加入", "Partner joined"); yield return StartCoroutine(Wait(0.8f));
            Shot("8_ingame_toast"); yield return StartCoroutine(Wait(1.5f));
            yield return StartCoroutine(Wait(4f));
            // 7. the guest's disconnect dialog (forced: no partner here)
            Traverse.Create(typeof(NetHud)).Field("s_disconnectModalOpen").SetValue(true);
            Traverse.Create(typeof(NetHud)).Field("s_disconnectWaitSec").SetValue(8f);
            yield return StartCoroutine(Wait(1f));
            Shot("9_disconnect_dialog"); yield return StartCoroutine(Wait(1.5f));
            Traverse.Create(typeof(NetHud)).Field("s_disconnectModalOpen").SetValue(false);
            yield return StartCoroutine(Wait(1f));
            Note("RESULT done");
            Quit();
        }

        static void Quit()
        {
            try { NetPlay.End("probe done"); } catch { }
            Application.Quit();
        }
    }
}
