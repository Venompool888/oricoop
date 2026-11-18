using System;
using Game;
using J2i.Net.XInputWrapper;
using OriCoop.Ui;
using UnityEngine;

namespace OriCoop
{
    // In-game co-op settings panel. Opens with F8 or by holding Back/View on any controller for 1 s, from gameplay
    // or the pause menu. Offline F8 freezes gameplay; the native pause keeps its own pause while settings is open.
    // Works with mouse, keyboard (arrows/Enter/Esc) and any controller (D-pad/stick, A, B).
    //   - Settings lists only this device's local seats. Setup still shows the live controller slots.
    //   - "Choose controller" for P1 / P2: press any button on the controller you want (for P1, any keyboard key
    //     = keyboard/mouse only). If the other player had that controller, the two swap.
    //   - Story cutscenes: only P1 starts them, or either player.
    //   - P2 join / remove.
    //
    // Pages:
    //   Settings (F8 / hold Back): the panel above.
    //   ModeSelect (opened by PlayMode once a save is running): Single player / 2 players. Must pick one.
    //   Setup (after "2 players"): choose each player's controller, then Start (P2 joins). B / Esc goes back.
    public static class CoopMenu
    {
        public const int PageSettings = 0, PageModeSelect = 1, PageSetup = 2;
        public static bool IsOpen { get; private set; }
        // True while this panel froze the game itself (offline F8). Opened from the native pause it never does (tests).
        public static bool FrozeGame { get { return IsOpen && s_changedTimeScale; } }
        public static int Page { get; private set; }
        static string s_testPage = "";

        internal static string[] L10nTestPages()
        {
            return new string[] { "coop.settings", "coop.mode", "coop.setup", "coop.hint" };
        }

        internal static void L10nTestShow(string page, bool on)
        {
            if (!on)
            {
                if (s_testPage == page) { Close(); s_testPage = ""; }
                return;
            }
            s_testPage = page;
            if (page == "coop.settings") Open();
            else if (page == "coop.mode") OpenModeSelect();
            else if (page == "coop.setup") OpenSetup();
            else if (page == "coop.hint") Close();
        }

        static readonly LocalSeatsAdapter s_seats = new LocalSeatsAdapter();
        static int s_focus;
        static int s_waitFor;            // 0 none, positive = local seat, -1 = add local player
        static bool s_fromPause, s_changedTimeScale;
        static int[] s_baseline = new int[4];
        static float s_ignoreNavUntil, s_backHeld;
        static float s_savedTimeScale = 1f;
        static bool s_savedLockInput;
        static string s_message = "";
        static readonly bool[] s_connected = new bool[4];
        static readonly int[] s_buttons = new int[4];
        static readonly int[] s_prevButtons = new int[4];
        static float s_scanTimer;
        static XInputState s_state = new XInputState();

        // Test hook: when set, replaces the physical pads' buttons (and marks them connected).
        public static int[] TestPadButtons;

        const int A = 0x1000, B = 0x2000, BACK = 0x20, START = 0x10, DUP = 1, DDOWN = 2;
        static int Rows { get { return Page == PageModeSelect ? 2 : Page == PageSetup ? 5 : NetPlay.Enabled ? s_seats.LocalSeats.Length + 2 : s_seats.LocalSeats.Length + 5; } }

        static void PollPads()
        {
            s_scanTimer -= Time.unscaledDeltaTime;
            bool rescan = s_scanTimer <= 0f;
            if (rescan) s_scanTimer = 1f;
            for (int i = 0; i < 4; i++)
            {
                s_prevButtons[i] = s_buttons[i];
                if (TestPadButtons != null) { s_connected[i] = true; s_buttons[i] = TestPadButtons[i]; continue; }
                // Polling an empty XInput slot is slow; check unplugged slots only once a second.
                if (!s_connected[i] && !rescan) { s_buttons[i] = 0; continue; }
                s_connected[i] = XInput.XInputGetState(i, ref s_state) == 0;
                int b = 0;
                if (s_connected[i])
                {
                    XInputGamepad g = s_state.Gamepad;
                    b = g.wButtons;
                    if (g.sThumbLY > 16000) b |= DUP;
                    if (g.sThumbLY < -16000) b |= DDOWN;
                    if (g.bLeftTrigger > 20 || g.bRightTrigger > 20) b |= 0x10000;
                }
                s_buttons[i] = b;
            }
        }

        static bool AnyPadPressed(int mask) { for (int i = 0; i < 4; i++) if ((s_buttons[i] & mask) != 0 && (s_prevButtons[i] & mask) == 0) return true; return false; }

        public static void Open() { OpenPage(PageSettings); }

        public static void OpenModeSelect()
        {
            OpenPage(PageModeSelect);
            s_focus = PlayMode.LastChoice == PlayMode.Single ? 0 : 1;
        }

        // 2 players already chosen (NativeModeMenu): straight to choosing controllers.
        public static void OpenSetup() { OpenPage(PageSetup); s_focus = 3; }

        static void GoTo(int page)
        {
            Page = page; s_focus = 0; s_waitFor = 0; s_message = "";
            s_settingsH = 0f;
            if (page == PageModeSelect) s_focus = PlayMode.LastChoice == PlayMode.Single ? 0 : 1;
            if (page == PageSetup) s_focus = 3;   // "Start" (the controllers shown are usually already right)
            s_ignoreNavUntil = Time.unscaledTime + 0.25f;
        }

        static void OpenPage(int page)
        {
            if (s_testPage == "" && !NetPlay.LocalInGameplay()) return;
            if (IsOpen) { GoTo(page); return; }
            IsOpen = true;
            Page = page;
            s_focus = 0; s_waitFor = 0; s_message = "";
            s_ignoreNavUntil = Time.unscaledTime + 0.3f;
            s_settingsH = 0f;
            s_fromPause = PauseMenuEntry.Suspend();
            if (!s_fromPause && UI.MainMenuVisible) { try { UI.Menu.HideMenuScreen(true); } catch (Exception) { } }
            s_savedTimeScale = Time.timeScale;
            s_changedTimeScale = !NetPlay.Enabled && !s_fromPause;
            if (s_changedTimeScale) Time.timeScale = 0f;
            if (GameController.Instance != null) { s_savedLockInput = GameController.Instance.LockInput; GameController.Instance.LockInput = true; }
            PollPads(); PollPads();
            Plugin.Log.LogInfo("co-op menu opened (" + (page == PageModeSelect ? "mode select" : page == PageSetup ? "2-player setup" : "settings") + ")");
        }

        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            s_waitFor = 0;
            if (s_changedTimeScale) Time.timeScale = s_savedTimeScale;
            if (GameController.Instance != null) GameController.Instance.LockInput = s_savedLockInput;
            P2Input.StartLatched = false;
            KeyboardOnlyP1.HoldUntilReleased = true;
            KeyboardOnlyP1.Refresh();
            if (s_fromPause) PauseMenuEntry.Return();
            s_fromPause = false;
            Plugin.Log.LogInfo("co-op menu closed");
        }

        // Called every frame from Plugin.Tick.
        public static void Tick()
        {
            PauseMenuEntry.Tick();
            bool inGame = NetPlay.LocalInGameplay();
            if (!IsOpen)
            {
                if (!inGame) { s_backHeld = 0f; return; }
                // Hold Back/View on any pad for 1 s, or press F8.
                PollPads();
                bool backDown = false; for (int i = 0; i < 4; i++) if ((s_buttons[i] & BACK) != 0) backDown = true;
                s_backHeld = backDown ? s_backHeld + Time.unscaledDeltaTime : 0f;
                if (Input.GetKeyDown(KeyCode.F8) || s_backHeld > 1f) { s_backHeld = 0f; Open(); }
                return;
            }
            if (!inGame && s_testPage == "") { Close(); return; }
            s_focus = Mathf.Clamp(s_focus, 0, Rows - 1);
            PollPads();

            if (s_waitFor != 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    int fresh = s_buttons[i] & ~s_baseline[i];
                    s_baseline[i] &= s_buttons[i]; // a button held at the start counts once released and pressed again
                    if (fresh != 0) { Assign(s_waitFor, i); return; }
                }
                if (Input.GetKeyDown(KeyCode.Escape)) { s_waitFor = 0; s_message = L10n.T("coop.cancelled"); s_ignoreNavUntil = Time.unscaledTime + 0.2f; }
                return; // keyboard "any key" for P1 is read in Draw (needs the GUI event)
            }

            if (Time.unscaledTime < s_ignoreNavUntil) return;
            bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);
            bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || AnyPadPressed(A);
            if (Page == PageSettings)
            {
                if (back || Input.GetKeyDown(KeyCode.F8) || AnyPadPressed(START)) { Close(); return; }
            }
            else if (Page == PageModeSelect)
            {
                if (AnyPadPressed(START)) confirm = true; // a choice is required; Esc / B do nothing here
            }
            else if (Page == PageSetup)
            {
                if (back) { GoTo(PageModeSelect); return; }
                if (AnyPadPressed(START)) { Activate(3); return; }
            }
            if (Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN)) s_focus = (s_focus + 1) % Rows;
            if (Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP)) s_focus = (s_focus + Rows - 1) % Rows;
            if (confirm) Activate(s_focus);
        }

        public static void Assign(int player, int pad)
        {
            if (Page == PageSettings)
            {
                InputDevice device = new InputDevice(pad < 0 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, pad);
                if (player < 0)
                {
                    // Spawn has an input-lock guard: restore gameplay first, or queue until native pause closes.
                    Close();
                    if (!s_seats.AddLocalPlayer(device)) { Open(); s_message = L10n.T("coop.input_failed"); }
                    return;
                }
                bool ok = s_seats.SetInput(player, device);
                s_waitFor = 0;
                s_settingsH = 0f;
                if (!ok) { s_message = L10n.T("coop.input_failed"); return; }
            }
            else Plugin.Instance.AssignPad(player, pad);
            s_waitFor = 0;
            s_message = pad < 0 ? L10n.T("coop.p1_keyboard")
                : L10n.F("coop.assigned", pad + 1, player);
            s_ignoreNavUntil = Time.unscaledTime + 0.35f;
        }

        public static void Activate(int row)
        {
            if (Page == PageModeSelect)
            {
                if (row == 0)
                {
                    PlayMode.SetMode(PlayMode.Single);
                    if (P2Context.P2 != null) Plugin.DespawnNow("single player chosen");
                    Close();
                }
                else GoTo(PageSetup);
                return;
            }
            if (Page == PageSettings) { ActivateSettings(row); return; }
            switch (row)
            {
                case 0:
                case 1:
                    s_waitFor = row + 1;
                    Array.Copy(s_buttons, s_baseline, 4);
                    s_message = "";
                    break;
                case 2:
                    Plugin.Instance.P2CanTriggerCutscenes = !Plugin.Instance.P2CanTriggerCutscenes;
                    break;
                case 3:
                    PlayMode.SetMode(PlayMode.Coop);
                    Close();
                    if (P2Context.P2 == null) s_seats.AddLocalPlayer(new InputDevice(InputDevice.DeviceKind.Pad, Plugin.Instance.P2PadInUse));
                    break;
                case 4:
                    GoTo(PageModeSelect);
                    break;
            }
        }

        static void WaitForDevice(int seat)
        {
            s_waitFor = seat;
            Array.Copy(s_buttons, s_baseline, 4);
            s_message = "";
            s_settingsH = 0f;
        }

        static void ActivateSettings(int row)
        {
            if (NetPlay.Enabled)
            {
                int[] local = s_seats.LocalSeats;
                if (row >= 0 && row < local.Length) WaitForDevice(local[row]);
                else if (row == local.Length) OpenLogFolder();
                else if (row == local.Length + 1) Close();
                return;
            }
            int[] seats = s_seats.LocalSeats;
            if (row < 0) return;
            if (row < seats.Length) { WaitForDevice(seats[row]); return; }
            switch (row - seats.Length)
            {
                case 0: Plugin.Instance.P2CanTriggerCutscenes = !Plugin.Instance.P2CanTriggerCutscenes; break;
                case 1:
                    if (seats.Length == 1) WaitForDevice(-1);
                    else { s_seats.RemoveLocalPlayer(seats[seats.Length - 1]); s_settingsH = 0f; }
                    break;
                case 2: GoTo(PageModeSelect); break;
                case 3: OpenLogFolder(); break;
                case 4: Close(); break;
            }
            s_focus = Mathf.Clamp(s_focus, 0, Rows - 1);
        }

        // ---- drawing --------------------------------------------------------------------------------------
        static Font s_font;
        static GUIStyle s_title, s_text, s_row, s_rowFocus, s_small;

        static void Styles()
        {
            if (s_title != null) return;
            int size = Mathf.Max(14, Screen.height / 42);
            try { s_font = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" }, size); } catch (Exception) { s_font = null; }
            s_text = new GUIStyle(GUI.skin.label); s_text.fontSize = size; s_text.wordWrap = true; s_text.richText = true;
            if (s_font != null) s_text.font = s_font;
            s_text.normal.textColor = new Color(0.92f, 0.94f, 1f);
            s_title = new GUIStyle(s_text); s_title.fontSize = (int)(size * 1.35f); s_title.fontStyle = FontStyle.Bold; s_title.normal.textColor = new Color(1f, 0.85f, 0.4f);
            s_small = new GUIStyle(s_text); s_small.fontSize = (int)(size * 0.8f); s_small.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            s_row = new GUIStyle(GUI.skin.button); s_row.fontSize = size; s_row.alignment = TextAnchor.MiddleLeft; s_row.richText = true;
            s_row.normal.background = s_row.hover.background = s_row.active.background = s_row.focused.background = null;
            if (s_font != null) s_row.font = s_font;
            s_row.padding = new RectOffset(14, 14, 6, 6);
            s_rowFocus = new GUIStyle(s_row); s_rowFocus.normal.textColor = new Color(1f, 0.9f, 0.5f);
        }

        static bool RowButton(string id, string text, bool focused, float height)
        {
            GUIStyle style = focused ? s_rowFocus : s_row;
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(height));
            Vector2 p = GUIUtility.GUIToScreenPoint(r.position);
            Rect screen = new Rect(p.x, p.y, r.width, r.height);
            NativeGui.Panel(id + ".background", screen, focused ? new Color(0.95f, 0.8f, 0.35f, 0.25f) : new Color(0.02f, 0.03f, 0.06f, 0.65f));
            if (!NativeGui.Available) return GUI.Button(r, text, style);
            Color color = style.normal.textColor * GUI.color * GUI.contentColor;
            if (!GUI.enabled) color.a *= 0.5f;
            if (GUI.enabled && r.Contains(Event.current.mousePosition))
                NativeGui.Panel(id + ".hover", screen, new Color(color.r, color.g, color.b, 0.14f * color.a));
            NativeGui.Label(id, style.padding.Remove(screen), text, style.fontSize, style.alignment, color);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        static string PlayerOfPad(int i)
        {
            if (KeyboardOnlyP1.P1Pad == i) return L10n.T("coop.p1");
            if (Plugin.Instance.P2PadInUse == i) return L10n.T("coop.p2");
            return L10n.T("coop.unused");
        }

        // Called from OnGUI.
        public static void Draw()
        {
            if (!IsOpen)
            {
                if (s_testPage == "coop.hint" || (UI.MainMenuVisible && Characters.Sein != null))
                {
                    Styles();
                    NativeGui.BeginPage("coop.hint");
                    NativeGui.Label("coop.hint.text", new Rect(16f, 12f, 900f, 40f),
                        L10n.T("coop.settings_hint"), s_small.fontSize, s_small.alignment, s_small.normal.textColor);
                    NativeGui.EndPage();
                }
                else if (OnTitleMenu()) DrawTitleBuild();
                return;
            }
            Styles();
            string page = Page == PageModeSelect ? "coop.mode" : Page == PageSetup ? "coop.setup" : "coop.settings";
            NativeGui.BeginPage(page);
            try { DrawPage(page); }
            finally { NativeGui.EndPage(); }
        }

        static void DrawPage(string page)
        {
            if (Page == PageModeSelect) { DrawModeSelect(); return; }
            if (Page == PageSettings) { DrawSettings(); return; }
            if (s_waitFor == 1)
            {
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None && e.keyCode != KeyCode.Escape && e.keyCode != KeyCode.F8)
                { Assign(1, -1); e.Use(); }
            }

            float w = Mathf.Min(Screen.width - 40f, 760f), lineH = s_text.fontSize * 1.9f, inner = w - 40f;
            string title = Page == PageSetup ? L10n.T("coop.choose_controllers") : L10n.T("coop.settings");
            string ctrlHint = L10n.T("coop.controller_hint");
            string[] padLines = new string[4];
            for (int i = 0; i < 4; i++)
            {
                bool pressed = s_buttons[i] != 0;
                string state = s_connected[i] ? L10n.T("coop.connected") : L10n.T("coop.not_connected");
                padLines[i] = (pressed ? "<color=#ffd166>●</color> " : "○ ") + L10n.F("coop.controller_state", i + 1, state) + (s_connected[i] ? "  →  " + PlayerOfPad(i) : "");
            }
            string kbLine = KeyboardOnlyP1.P1Pad < 0 ? L10n.T("coop.keyboard")
                : L10n.F("coop.keyboard_pad", KeyboardOnlyP1.P1Pad + 1);
            string warn = Page == PageSetup && !s_connected[Mathf.Clamp(Plugin.Instance.P2PadInUse, 0, 3)]
                ? L10n.F("coop.p2_disconnected", Plugin.Instance.P2PadInUse + 1) : "";
            string footer = L10n.T("coop.cutscene_hint");

            string p1 = KeyboardOnlyP1.P1Pad < 0 ? L10n.T("coop.keyboard_only") : L10n.F("coop.controller", KeyboardOnlyP1.P1Pad + 1);
            string p2 = L10n.F("coop.controller", Plugin.Instance.P2PadInUse + 1);
            string row0 = s_waitFor == 1 ? L10n.T("coop.choose_p1") : L10n.F("coop.p1_uses", p1);
            string row1 = s_waitFor == 2 ? L10n.T("coop.choose_p2") : L10n.F("coop.p2_uses", p2);
            string row2 = L10n.F("coop.cutscenes", Plugin.Instance.P2CanTriggerCutscenes ? L10n.T("coop.either") : L10n.T("coop.p1_only"));
            string[] rows;
            if (Page == PageSetup)
                rows = new string[] { row0, row1, row2,
                    L10n.T("coop.start"),
                    L10n.T("coop.back") };
            else
                rows = new string[] { row0, row1, row2,
                    P2Context.P2 == null ? L10n.T("coop.add_p2") : L10n.T("coop.remove_p2"),
                    L10n.F("coop.mode", PlayMode.Current == PlayMode.Single ? L10n.T("coop.single") : L10n.T("coop.two_players")),
                    L10n.T("coop.close") };

            // Panel height from what is actually drawn (the footer wraps, the message and warning come and go),
            // so the last line is never cut off.
            float h = 28f + LineH(s_title, title, inner) + 4f + LineH(s_small, ctrlHint, inner);
            for (int i = 0; i < 4; i++) h += LineH(s_text, padLines[i], inner);
            h += LineH(s_text, kbLine, inner) + 8f;
            h += rows.Length * (lineH * 1.15f + s_row.margin.vertical) + 6f;
            if (s_message != "") h += LineH(s_text, s_message, inner);
            if (warn != "") h += LineH(s_text, warn, inner);
            h += LineH(s_small, footer, inner) + 8f;
            // Once a frame has been laid out, use the real content height (the estimate above runs a bit high).
            if (s_settingsH > 0f && Mathf.Abs(s_settingsW - w) < 1f) h = s_settingsH;
            h = Mathf.Min(h, Screen.height - 20f);

            Rect r = new Rect((Screen.width - w) * 0.5f, Mathf.Max(10f, (Screen.height - h) * 0.5f), w, h);
            NativeGui.Panel(page + ".background", r, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 14f, r.width - 40f, r.height - 28f));
            NativeGui.LayoutLabel(page + ".title", title, s_title);
            GUILayout.Space(4f);
            NativeGui.LayoutLabel(page + ".controller_hint", ctrlHint, s_small);
            for (int i = 0; i < 4; i++) NativeGui.LayoutLabel(page + ".pad." + i, padLines[i], s_text);
            NativeGui.LayoutLabel(page + ".keyboard", kbLine, s_text);
            GUILayout.Space(8f);
            for (int i = 0; i < rows.Length; i++)
            {
                if (RowButton(page + ".row." + i, rows[i], i == s_focus, lineH * 1.15f))
                { s_focus = i; if (s_waitFor == 0) Activate(i); }
            }
            GUILayout.Space(6f);
            if (s_message != "") NativeGui.LayoutLabel(page + ".message", s_message, s_text);
            if (warn != "") NativeGui.LayoutLabel(page + ".warning", warn, s_text);
            NativeGui.LayoutLabel(page + ".footer", footer, s_small);
            if (Event.current.type == EventType.Repaint)
            {
                // Area starts 14 px below the panel top; keep 14 px under the last line.
                s_settingsH = GUILayoutUtility.GetLastRect().yMax + 28f;
                s_settingsW = w;
            }
            GUILayout.EndArea();
        }

        static void DrawSettings()
        {
            bool online = NetPlay.Enabled;
            int[] seats = s_seats.LocalSeats;
            if (online ? s_waitFor > 0 : s_waitFor == 1)
            {
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown && e.keyCode != KeyCode.None && e.keyCode != KeyCode.Escape && e.keyCode != KeyCode.F8)
                { Assign(s_waitFor, -1); e.Use(); }
            }
            string[] seatRows = new string[seats.Length];
            for (int i = 0; i < seats.Length; i++)
            {
                InputDevice device = s_seats.InputOf(seats[i]);
                string input = device.Kind == InputDevice.DeviceKind.KeyboardMouse ?
                    L10n.T("coop.keyboard_only") : L10n.F("coop.controller", device.Pad + 1);
                string label = L10n.F("coop.local_seat", seats[i], input);
                seatRows[i] = s_waitFor == seats[i] ? L10n.T(seats[i] == 1 ? "coop.choose_p1" : "coop.choose_p2") :
                    L10n.F("coop.change_device", label);
            }
            string[] actions = online ? new string[] { L10n.T("coop.open_logs"), L10n.T("coop.close") } : new string[] {
                L10n.F("coop.cutscenes", Plugin.Instance.P2CanTriggerCutscenes ? L10n.T("coop.either") : L10n.T("coop.p1_only")),
                s_waitFor < 0 ? L10n.T("coop.choose_local_pad") : seats.Length == 1 ? L10n.T("coop.add_local") : L10n.F("coop.remove_local", seats[seats.Length - 1]),
                L10n.F("coop.mode", PlayMode.Current == PlayMode.Single ? L10n.T("coop.single") : L10n.T("coop.two_players")),
                L10n.T("coop.open_logs"),
                L10n.T("coop.close") };
            string build = BuildLabel();
            float w = Mathf.Min(Screen.width - 40f, 760f), inner = w - 40f, lineH = s_text.fontSize * 1.9f;
            float h = 36f + LineH(s_title, L10n.T("coop.settings"), inner);
            foreach (string label in seatRows) h += Mathf.Max(lineH * 1.15f, LineH(s_row, label, inner)) + s_row.margin.vertical;
            foreach (string label in actions) h += Mathf.Max(lineH * 1.15f, LineH(s_row, label, inner)) + s_row.margin.vertical;
            if (s_message != "") h += LineH(s_text, s_message, inner);
            if (!online) h += LineH(s_small, L10n.T("coop.cutscene_hint"), inner);
            h += LineH(s_small, build, inner);
            h = Mathf.Min(h, Screen.height - 20f);
            Rect r = new Rect((Screen.width - w) * 0.5f, Mathf.Max(10f, (Screen.height - h) * 0.5f), w, h);
            NativeGui.Panel("coop.settings.background", r, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 14f, inner, r.height - 28f));
            NativeGui.LayoutLabel("coop.settings.title", L10n.T("coop.settings"), s_title);
            for (int i = 0; i < seatRows.Length; i++)
            {
                string id = "coop.settings.seat." + seats[i];
                if (RowButton(id, seatRows[i], s_focus == i, Mathf.Max(lineH * 1.15f, LineH(s_row, seatRows[i], inner))))
                { s_focus = i; if (s_waitFor == 0) Activate(i); }
            }
            for (int i = 0; i < actions.Length; i++)
            {
                int row = seats.Length + i;
                if (RowButton("coop.settings.action." + i, actions[i], s_focus == row, Mathf.Max(lineH * 1.15f, LineH(s_row, actions[i], inner))))
                { s_focus = row; if (s_waitFor == 0) Activate(row); }
            }
            if (s_message != "") NativeGui.LayoutLabel("coop.settings.message", s_message, s_text);
            if (!online) NativeGui.LayoutLabel("coop.settings.footer", L10n.T("coop.cutscene_hint"), s_small);
            NativeGui.LayoutLabel("coop.settings.build", build, s_small);
            GUILayout.EndArea();
        }

        // ---- build info and logs (T-032) ----------------------------------------------------------------------
        // Settings page rows (local play: seats, cutscenes, add/remove, mode, log folder, close; online: local seats, log folder, close).
        public static int SettingsCloseRow { get { return NetPlay.Enabled ? 1 : s_seats.LocalSeats.Length + 4; } }
        public static int SettingsLogsRow { get { return SettingsCloseRow - 1; } }

        public static string BuildLabel() { return L10n.F("build.version", BuildInfo.Short(Plugin.Version, BuildInfo.Commit)); }

        static bool OnTitleMenu()
        {
            if (GameStateMachine.Instance == null || !GameStateMachine.Instance.IsInExtendedTitleScreen()) return false;
            TitleScreenManager.Screen screen = TitleScreenManager.CurrentScreen;
            return screen == TitleScreenManager.Screen.MainMenu || screen == TitleScreenManager.Screen.PressStart;
        }

        // Bottom-right corner of the title screen, small and dim (the build a playtest report should name).
        static void DrawTitleBuild()
        {
            Styles();
            NativeGui.BeginPage("title.build");
            try
            {
                NativeGui.Label("title.build.text", new Rect(Screen.width - 616f, Screen.height - 16f - s_small.fontSize * 1.6f, 600f, s_small.fontSize * 1.6f),
                    BuildLabel(), s_small.fontSize, TextAnchor.LowerRight, new Color(0.7f, 0.75f, 0.85f, 0.75f));
            }
            finally { NativeGui.EndPage(); }
        }

        // The folder with BepInEx's LogOutput.log and the OriCoop trace files (trace dir if it was moved).
        public static string LogFolder()
        {
            string trace = CoopTrace.FilePath;
            if (!string.IsNullOrEmpty(trace)) { string dir = System.IO.Path.GetDirectoryName(trace); if (!string.IsNullOrEmpty(dir)) return dir; }
            return BepInEx.Paths.BepInExRootPath;
        }

        // Test hook: replaces the shell call (tests must never open a window on the owner's desktop).
        public static Action<string> OpenFolderHook;

        static void OpenLogFolder()
        {
            string dir = LogFolder();
            try
            {
                if (OpenFolderHook != null) OpenFolderHook(dir);
                // A test game (every launch through test-launch.ps1 sets ORICOOP_LOG_TAG) never starts Explorer: the
                // window would open on the owner's desktop, not the hidden test desktop.
                else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORICOOP_LOG_TAG")) || TestHarness.Enabled)
                    Plugin.Log.LogInfo("co-op menu: test game, not opening Explorer on " + dir);
                else System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                s_message = L10n.T("coop.logs_opened");
                Plugin.Log.LogInfo("co-op menu: opened the log folder");
            }
            catch (Exception ex)
            {
                s_message = L10n.F("coop.logs_failed", ex.Message);
                Plugin.Log.LogWarning("co-op menu: could not open the log folder: " + ex.Message);
            }
        }

        static float s_settingsH, s_settingsW;   // measured setup panel height (last repaint) and the width it was for

        // Height GUILayout gives a label of this text at this width (wrapping included), plus its margins.
        static float LineH(GUIStyle style, string text, float width)
        {
            return style.CalcHeight(new GUIContent(text), width) + style.margin.vertical;
        }

        static void DrawModeSelect()
        {
            float w = Mathf.Min(Screen.width - 40f, 680f), lineH = s_text.fontSize * 1.9f;
            float h = lineH * 9.4f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Mathf.Max(10f, (Screen.height - h) * 0.5f), w, h);
            NativeGui.Panel("coop.mode.background", r, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 14f, r.width - 40f, r.height - 28f));
            NativeGui.LayoutLabel("coop.mode.title", L10n.T("coop.play_how"), s_title);
            GUILayout.Space(8f);
            string[] rows = {
                L10n.F("coop.single_desc", s_small.fontSize),
                L10n.F("coop.two_desc", s_small.fontSize)
            };
            for (int i = 0; i < rows.Length; i++)
            {
                if (RowButton("coop.mode.row." + i, rows[i], i == s_focus, lineH * 2.1f))
                { s_focus = i; Activate(i); }
            }
            GUILayout.Space(8f);
            NativeGui.LayoutLabel("coop.mode.hint", L10n.T("coop.play_hint"), s_small);
            GUILayout.EndArea();
        }
    }
}
