using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using Microsoft.Win32;
using UnityEngine;

namespace OriCoop
{
    // Vanilla display bug, any resolution / aspect ratio / refresh rate: oriDE.exe (Unity 5.3) has no dpiAware entry in
    // its manifest, so with Windows display scaling above 100% the game runs "DPI-unaware": Windows gives it a shrunken
    // logical desktop (e.g. 2560x1440 at 125% looks like 2048x1152) and bitmap-stretches the window. The settings menu
    // shows Screen.width/height, which can say the native size while the picture is really scaled, blurry or cropped,
    // or the native size cannot be picked at all. The known community fix is the exe's
    // "Properties > Compatibility > Change high DPI settings > Override high DPI scaling: Application", which is the
    // per-user registry flag HIGHDPIAWARE under AppCompatFlags\Layers. The window already exists when plugins load, so
    // it cannot become DPI-aware in this run; the mod sets that same flag and it works from the next start.
    // Also: the game never re-applies its saved resolution (GameSettings.LoadSettings reads and drops it), so Unity's
    // own saved size is used. On the first DPI-aware start the old shrunken size is replaced by the native one, and a
    // fullscreen size bigger than the monitor is always clamped. Any other size the player chose is left alone.
    public static class DisplayFix
    {
        const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        const string Flag = "HIGHDPIAWARE";
        static ConfigEntry<bool> s_enabled, s_addedByMod;
        static ConfigEntry<string> s_oldSize;
        static bool s_showRestartNote;
        public static string Summary = "";

        public static void Init(ConfigFile cfg)
        {
            s_enabled = cfg.Bind("Display", "FixResolution", true,
                "Fixes the vanilla problem where the game does not really render at the resolution the screen and the settings show " +
                "(blurry, scaled or cut off with Windows display scaling above 100%). Sets Windows' per-user 'high DPI: Application' " +
                "flag for oriDE.exe (works from the next start) and, once, replaces a shrunken fullscreen size with the monitor's native one. " +
                "false = leave display handling to the game and remove the flag again if the mod added it.");
            s_addedByMod = cfg.Bind("Display", "DpiFlagAddedByMod", false, "Internal: true when FixResolution added the Windows flag (so turning it off removes it).");
            s_oldSize = cfg.Bind("Display", "PreFixSize", "", "Internal: the shrunken fullscreen size seen before the flag took effect (cleared after one fix).");
            try { CheckDpi(); }
            catch (Exception e) { Plugin.Log.LogWarning("display: DPI check failed: " + e.Message); }
            GameObject go = new GameObject("OriCoopDisplayFix");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Watcher>();
        }

        // ---- DPI awareness ----
        static void CheckDpi()
        {
            IntPtr hwnd = FindGameWindow();
            int aware = Awareness(hwnd);
            int physW, physH, hz, logW, logH;
            MonitorSizes(hwnd, out physW, out physH, out hz, out logW, out logH);
            int scale = logW > 0 ? Mathf.RoundToInt(100f * physW / logW) : 100;
            if (aware != 0 && hwnd != IntPtr.Zero)
            {
                try { scale = Mathf.RoundToInt(100f * GetDpiForWindow(hwnd) / 96f); } catch (EntryPointNotFoundException) { }
            }
            string exe = ExePath();
            Summary = "monitor " + physW + "x" + physH + " @" + hz + " Hz, Windows scaling " + scale + "%" +
                      ", game " + AwarenessName(aware);
            Plugin.Log.LogInfo("display: " + Summary + "; exe " + exe);

            string value = ReadLayer(exe);
            if (!s_enabled.Value)
            {
                if (s_addedByMod.Value && HasToken(value, Flag))
                {
                    WriteLayer(exe, RemoveToken(value, Flag));
                    Plugin.Log.LogInfo("display: FixResolution = false; removed the high-DPI flag the mod had added (from next start)");
                }
                s_addedByMod.Value = false; s_oldSize.Value = "";
                return;
            }
            if (aware != 0) { if (HasToken(value, Flag)) Plugin.Log.LogInfo("display: high-DPI flag is set; nothing to fix"); return; }
            if (scale == 100) { Plugin.Log.LogInfo("display: scaling is 100%, DPI-unaware is harmless here; no change"); return; }
            if (HasToken(value, Flag)) { Plugin.Log.LogWarning("display: high-DPI flag is set but not active yet (restart the game)"); return; }
            if (HasToken(value, "DPIUNAWARE") || HasToken(value, "GDIDPISCALING"))
            {
                Plugin.Log.LogInfo("display: the player chose a Windows DPI override for oriDE.exe (" + value.Trim() + "); left as is");
                return;
            }
            WriteLayer(exe, AddToken(value, Flag));
            s_addedByMod.Value = true;
            s_oldSize.Value = logW + "x" + logH;
            s_showRestartNote = true;
            Plugin.Log.LogInfo("display: game is DPI-unaware at " + scale + "% scaling, so Windows stretches a " + logW + "x" + logH +
                               " picture; set the per-user high-DPI flag for oriDE.exe. Restart the game once for true " + physW + "x" + physH);
        }

        // ---- resolution, a moment after start ----
        class Watcher : MonoBehaviour
        {
            float m_noteUntil;
            int m_lastW, m_lastH; bool m_lastFull;

            IEnumerator Start()
            {
                float t = Time.realtimeSinceStartup + 2f;
                while (Time.realtimeSinceStartup < t) yield return null;
                if (s_showRestartNote) m_noteUntil = Time.realtimeSinceStartup + 15f;
                try { FixSize(); } catch (Exception e) { Plugin.Log.LogWarning("display: resolution check failed: " + e.Message); }
                while (true)
                {
                    if (Screen.width != m_lastW || Screen.height != m_lastH || Screen.fullScreen != m_lastFull)
                    {
                        m_lastW = Screen.width; m_lastH = Screen.height; m_lastFull = Screen.fullScreen;
                        Plugin.Log.LogInfo("display: game renders " + m_lastW + "x" + m_lastH + (m_lastFull ? " fullscreen" : " windowed") + ClientInfo());
                    }
                    float next = Time.realtimeSinceStartup + 1f;
                    while (Time.realtimeSinceStartup < next) yield return null;
                }
            }

            void OnGUI()
            {
                if (Time.realtimeSinceStartup > m_noteUntil) return;
                GUIStyle st = new GUIStyle(GUI.skin.box); st.fontSize = Mathf.Max(14, Screen.height / 50); st.wordWrap = true;
                float w = Screen.width * 0.6f;
                GUI.Box(new Rect((Screen.width - w) / 2f, Screen.height * 0.04f, w, st.fontSize * 5f),
                    L10n.T("display.restart"), st);
            }
        }

        static void FixSize()
        {
            if (!s_enabled.Value) return;
            IntPtr hwnd = FindGameWindow();
            if (Awareness(hwnd) == 0) { Plugin.Log.LogInfo("display: still DPI-unaware this run; resolution left to the game"); return; }
            int physW, physH, hz, logW, logH;
            MonitorSizes(hwnd, out physW, out physH, out hz, out logW, out logH);
            if (physW <= 0 || physH <= 0) return;
            int w = Screen.width, h = Screen.height;
            string old = s_oldSize.Value;
            if (old != "")
            {
                s_oldSize.Value = "";
                if (Screen.fullScreen && old == w + "x" + h && (w != physW || h != physH))
                {
                    Plugin.Log.LogInfo("display: first DPI-aware start: fullscreen " + w + "x" + h + " was the shrunken size; switching to native " + physW + "x" + physH + " @" + hz + " Hz");
                    Screen.SetResolution(physW, physH, true, hz);
                    return;
                }
            }
            if (Screen.fullScreen && (w > physW || h > physH))
            {
                Plugin.Log.LogInfo("display: fullscreen " + w + "x" + h + " is bigger than the monitor; switching to native " + physW + "x" + physH + " @" + hz + " Hz");
                Screen.SetResolution(physW, physH, true, hz);
                return;
            }
            Plugin.Log.LogInfo("display: " + w + "x" + h + (Screen.fullScreen ? " fullscreen" : " windowed") + " on a " + physW + "x" + physH + " monitor; keeping the player's choice");
        }

        static string ClientInfo()
        {
            IntPtr hwnd = FindGameWindow();
            RECT r;
            if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out r)) return "";
            return " (window client " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + ", " + AwarenessName(Awareness(hwnd)) + ")";
        }

        // ---- registry flag (same value the exe's Compatibility tab writes) ----
        static string ReadLayer(string exe)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(LayersKey))
                return k == null ? "" : (k.GetValue(exe) as string ?? "");
        }

        static void WriteLayer(string exe, string value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(LayersKey))
            {
                if (value.Replace("~", "").Trim() == "") k.DeleteValue(exe, false);
                else k.SetValue(exe, value, RegistryValueKind.String);
            }
        }

        static bool HasToken(string v, string t) { return Array.IndexOf(v.Split(' '), t) >= 0; }
        static string AddToken(string v, string t)
        {
            v = v.Trim();
            if (v == "") return "~ " + t;
            return (v.StartsWith("~") ? v : "~ " + v) + " " + t;
        }
        static string RemoveToken(string v, string t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string p in v.Split(' ')) if (p != "" && p != t) { if (sb.Length > 0) sb.Append(' '); sb.Append(p); }
            return sb.ToString();
        }

        // ---- Win32 ----
        static string AwarenessName(int a) { return a == 0 ? "DPI-unaware (Windows scales it)" : a == 1 ? "system DPI-aware" : a == 2 ? "per-monitor DPI-aware" : "DPI awareness unknown"; }

        // 0 unaware, 1 system aware, 2 per-monitor aware, -1 unknown
        static int Awareness(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                try { return GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)); }
                catch (EntryPointNotFoundException) { }
            }
            try { int a; if (GetProcessDpiAwareness(IntPtr.Zero, out a) == 0) return a; }
            catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            try { return IsProcessDPIAware() ? 1 : 0; } catch (EntryPointNotFoundException) { return -1; }
        }

        static void MonitorSizes(IntPtr hwnd, out int physW, out int physH, out int hz, out int logW, out int logH)
        {
            physW = physH = hz = logW = logH = 0;
            IntPtr mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            MONITORINFOEX mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            string device = null;
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                logW = mi.rcMonitor.Right - mi.rcMonitor.Left; logH = mi.rcMonitor.Bottom - mi.rcMonitor.Top; device = mi.szDevice;
            }
            DEVMODE dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(device, -1 /* ENUM_CURRENT_SETTINGS */, ref dm))
            { physW = dm.dmPelsWidth; physH = dm.dmPelsHeight; hz = dm.dmDisplayFrequency; }
            else { physW = Screen.currentResolution.width; physH = Screen.currentResolution.height; hz = Screen.currentResolution.refreshRate; }
            if (logW <= 0) { logW = physW; logH = physH; }
        }

        static IntPtr FindGameWindow()
        {
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowEx(IntPtr.Zero, w, "UnityWndClass", null)) != IntPtr.Zero)
            {
                uint pid; GetWindowThreadProcessId(w, out pid);
                if (pid == me) return w;
            }
            return IntPtr.Zero;
        }

        static string ExePath()
        {
            StringBuilder sb = new StringBuilder(1024);
            GetModuleFileName(IntPtr.Zero, sb, sb.Capacity);
            return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("shcore.dll")] static extern int GetProcessDpiAwareness(IntPtr process, out int value);
        [DllImport("user32.dll")] static extern bool IsProcessDPIAware();
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT r);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")] static extern int GetModuleFileName(IntPtr module, StringBuilder sb, int size);
    }
}
