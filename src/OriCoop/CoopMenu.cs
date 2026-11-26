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
    //   - Local player join / remove.
    //
    // Pages:
    //   Settings (F8 / hold Back): the panel above.
    //   ModeSelect (opened by PlayMode once a save is running): Single player / 2 players. Must pick one.
    //   Setup (after "2 players"): choose each player's controller, then Start (P2 joins). B / Esc goes back.
    public static partial class CoopMenu
    {
        public const int PageSettings = 0, PageModeSelect = 1, PageSetup = 2;
        public static bool IsOpen { get; private set; }
        internal static int OpeningSeat { get; private set; }
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

        static readonly GameSeatView s_seats = new GameSeatView();
        static int s_focus;
        static int s_waitFor;            // 0 none, positive = local seat, -1 = add local player
        static bool s_fromPause, s_changedTimeScale;
        static int[] s_baseline = new int[4];
        static float s_ignoreNavUntil;
        static readonly float[] s_backHeld = new float[4];
        static float s_savedTimeScale = 1f;
        static bool s_mutationSavedLock, s_mutatedLock;
        // Test-only: save and restore the native lock in the live panel.
        static readonly bool s_staleLockMutation = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_MENU_STALE_LOCK") == "1";
        static string s_message = "";
        static readonly bool[] s_connected = new bool[4];
        static readonly int[] s_buttons = new int[4];
        static readonly int[] s_prevButtons = new int[4];
        static float s_scanTimer;
        static XInputState s_state = new XInputState();

        // Test hook: when set, replaces the physical pads' buttons (and marks them connected).
        public static int[] TestPadButtons;

        const int A = 0x1000, B = 0x2000, BACK = 0x20, START = 0x10, DUP = 1, DDOWN = 2;
        static bool HasExtraJoinRow(int count) { return count > 1 && count < Players.MaxSeats; }
        static int Rows
        {
            get
            {
                if (Page == PageModeSelect) return 2;
                if (Page == PageSetup) return 5;
                int count = s_seats.LocalSeats.Length;
                return NetPlay.Enabled ? count + (KeystoneRecovery.CanGrant(OpeningSeat) ? 3 : 2) : count + (HasExtraJoinRow(count) ? 6 : 5);
            }
        }

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
                s_connected[i] = Pads.VirtualPads.GetState(i, ref s_state) == 0;
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
            EndNameEdit(false);
            Page = page; s_focus = 0; s_waitFor = 0; s_message = "";
            s_settingsH = 0f;
            if (page == PageModeSelect) s_focus = PlayMode.LastChoice == PlayMode.Single ? 0 : 1;
            if (page == PageSetup) s_focus = 3;   // "Start" (the controllers shown are usually already right)
            s_ignoreNavUntil = Time.unscaledTime + 0.25f;
        }

        static int SeatForInput(int pad)
        {
            foreach (int seat in s_seats.LocalSeats)
            {
                InputDevice input = s_seats.InputOf(seat);
                if (pad < 0 ? input.Kind == InputDevice.DeviceKind.KeyboardMouse : input.Kind == InputDevice.DeviceKind.Pad && input.Pad == pad)
                    return seat;
            }
            return pad < 0 ? s_seats.PrimarySeat : 0;
        }

        // The last reason the panel would not open or closed by itself, logged once per change (F8 after the escape, #431).
        static string s_refused = "";
        internal static string LastRefusal { get { return s_refused; } }
        static void Refused(string why)
        {
            if (why == s_refused) return;
            s_refused = why;
            Plugin.Log.LogInfo("co-op menu: " + why);
        }

        static void OpenPage(int page, int seat = -1)
        {
            string notPlaying = s_testPage == "" ? NetPlay.NotInGameplayReason() : null;
            if (notPlaying != null) { Refused("not opened, not in gameplay: " + notPlaying + " (" + GameplayDetail() + ")"); return; }
            if (CsRoomPage.InGame || OnlineRoomScreen.SteamInGame) { Refused("not opened over the in-game room page"); return; }   // T-189: one pause hold, see Tick
            if (IsOpen) { GoTo(page); return; }
            s_refused = "";
            IsOpen = true;
            OpeningSeat = seat < 0 ? s_seats.PrimarySeat : seat;
            Page = page;
            s_focus = 0; s_waitFor = 0; s_message = "";
            s_ignoreNavUntil = Time.unscaledTime + 0.3f;
            s_settingsH = 0f;
            s_fromPause = PauseMenuEntry.Suspend();
            if (!s_fromPause && UI.MainMenuVisible) { try { UI.Menu.HideMenuScreen(true); } catch (Exception) { } }
            SessionPauseChanged(SessionRules.NoPause);
            SessionRules.ScreenOpened();
            PollPads(); PollPads();
            Plugin.Log.LogInfo("co-op menu opened (" + (page == PageModeSelect ? "mode select" : page == PageSetup ? "2-player setup" : "settings") + ")");
        }

        internal static void SessionPauseChanged(bool live)
        {
            SetPause(IsOpen && !live && !(s_fromPause && UI.MainMenuVisible), IsOpen && !live);
        }

        static void SetPause(bool freeze, bool lockInput)
        {
            if (freeze != s_changedTimeScale)
            {
                if (freeze) { s_savedTimeScale = Time.timeScale; Time.timeScale = 0f; }
                else Time.timeScale = s_savedTimeScale;
                s_changedTimeScale = freeze;
            }
            SessionRules.SetScreenInputLock(typeof(CoopMenu), lockInput);
            if (!s_staleLockMutation) return;
            bool mutateLock = IsOpen && !lockInput;
            if (mutateLock != s_mutatedLock && GameController.Instance != null)
            {
                if (mutateLock) s_mutationSavedLock = GameController.Instance.LockInput;
                GameController.Instance.LockInput = mutateLock || s_mutationSavedLock;
            }
            s_mutatedLock = mutateLock;
        }

        // What the gameplay test saw, for the log line when the panel won't open.
        internal static string GameplayDetail()
        {
            SeinCharacter s = Characters.Sein;
            string state = GameStateMachine.Instance != null ? GameStateMachine.Instance.CurrentState.ToString() : "no state machine";
            return "Sein=" + (s == null ? "null" : s.name + (s.gameObject.activeInHierarchy ? " active" : " inactive"))
                + (P2Context.P1 != null && P2Context.P1 != s ? ", P1 is another Ori (" + P2Context.P1.name + ")" : "")
                + ", state=" + state + ", loading=" + (GameController.Instance != null && GameController.Instance.IsLoadingGame);
        }

        public static void Close()
        {
            if (!IsOpen) return;
            EndNameEdit(false);
            IsOpen = false;
            OpeningSeat = 0;
            s_waitFor = 0;
            SetPause(false, false);
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
                // T-189: not over the in-game room page: its View is 踢出, and opening here would take the page's pause hold
                // (PauseMenuEntry has one), leaving the native pause menu live under the page.
                if (!inGame || CsRoomPage.InGame || OnlineRoomScreen.SteamInGame) { Array.Clear(s_backHeld, 0, s_backHeld.Length); return; }
                PollPads();
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    Array.Clear(s_backHeld, 0, s_backHeld.Length);
                    OpenPage(PageSettings, SeatForInput(-1));
                    return;
                }
                for (int i = 0; i < s_backHeld.Length; i++)
                {
                    s_backHeld[i] = (s_buttons[i] & BACK) != 0 ? s_backHeld[i] + Time.unscaledDeltaTime : 0f;
                    if (s_backHeld[i] <= 1f) continue;
                    Array.Clear(s_backHeld, 0, s_backHeld.Length);
                    OpenPage(PageSettings, SeatForInput(i));
                    break;
                }
                return;
            }
            if (!inGame && s_testPage == "")
            {
                Refused("closed, not in gameplay: " + NetPlay.NotInGameplayReason() + " (" + GameplayDetail() + ")");
                Close(); return;
            }
            s_focus = Mathf.Clamp(s_focus, 0, Rows - 1);
            PollPads();

            if (EditingName) { TickNameEdit(); return; }
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
                if (Input.GetKeyDown(KeyCode.F2) || AnyPadPressed(0x4000)) // X on an Xbox controller
                {
                    int[] local = s_seats.LocalSeats;
                    if (s_focus < local.Length) BeginNameEdit(local[s_focus]);
                    return;
                }
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
            s_message = pad < 0 ? (!NetPlay.Enabled && player > 2 ? L10n.F("coop.seat_keyboard", player) : L10n.T("coop.p1_keyboard"))
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
                else if (row == local.Length + 2 && KeystoneRecovery.TryGrant(OpeningSeat))
                    s_message = L10n.F("coop.recovered_keystone", Characters.Sein.Inventory.Keystones);
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
                case 5: if (HasExtraJoinRow(seats.Length)) WaitForDevice(-1); break;
            }
            s_focus = Mathf.Clamp(s_focus, 0, Rows - 1);
        }

        // ---- drawing --------------------------------------------------------------------------------------
        static Font s_font;
        static GUIStyle s_title, s_text, s_row, s_rowFocus, s_small;

        static int s_stylesH;   // the screen height the styles were made for (T-093: a window that grows must not keep small fonts)
        static int s_fontSize;
        static readonly string[] FontNames = { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" };

        // T-153: a new font only when the size really changes, and the old one is destroyed (it leaked on every resize).
        internal static void EnsureFont(int size)
        {
            if (s_font != null && s_fontSize == size && !OriCoop.Ui.OsFonts.LeakMutated) return;
            s_font = OriCoop.Ui.OsFonts.Replace(s_font, FontNames, size); s_fontSize = size;
            s_stylesH = 0;   // the styles still point at the old (destroyed) font: rebuild them at the next draw
        }

        static void Styles()
        {
            if (s_title != null && s_stylesH == Screen.height) return;
            int size = Mathf.Max(14, Screen.height / 42);
            EnsureFont(size);
            s_stylesH = Screen.height; s_modeH = 0;
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

        static bool RowButton(string id, string text, bool focused, float height, float width = 0f, Font font = null)
        {
            GUIStyle style = focused ? s_rowFocus : s_row;
            Rect r = width > 0f ? GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(height), GUILayout.Width(width))
                : GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(height));
            Vector2 p = GUIUtility.GUIToScreenPoint(r.position);
            Rect screen = new Rect(p.x, p.y, r.width, r.height);
            NativeGui.Panel(id + ".background", screen, focused ? new Color(0.95f, 0.8f, 0.35f, 0.25f) : new Color(0.02f, 0.03f, 0.06f, 0.65f));
            if (!NativeGui.Available) return GUI.Button(r, text, style);
            Color color = style.normal.textColor * GUI.color * GUI.contentColor;
            if (!GUI.enabled) color.a *= 0.5f;
            if (GUI.enabled && r.Contains(Event.current.mousePosition))
                NativeGui.Panel(id + ".hover", screen, new Color(color.r, color.g, color.b, 0.14f * color.a));
            NativeGui.Label(id, style.padding.Remove(screen), text, style.fontSize, style.alignment, color, font);
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
                // T-189: not over the in-game room page (the panel does not open there).
                if (s_testPage == "coop.hint" || (UI.MainMenuVisible && Characters.Sein != null && !CsRoomPage.InGame && !OnlineRoomScreen.SteamInGame))
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
            if (Page == PageSettings) { if (EditingName) DrawNameEdit(); else DrawSettings(); return; }
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
            if (online ? s_waitFor > 0 : s_waitFor == 1 || s_waitFor > 2 || s_waitFor < 0 && HasExtraJoinRow(seats.Length))
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
                string label = L10n.F("coop.named_input", TeammateIdentity.Label(seats[i]), input);
                string choose = seats[i] == 1 ? L10n.T("coop.choose_p1") : !online && seats[i] > 2
                    ? L10n.F("coop.choose_extra_device", seats[i]) : L10n.T("coop.choose_p2");
                seatRows[i] = s_waitFor == seats[i] ? choose :
                    L10n.F("coop.change_device", label);
            }
            string[] actions = online ? new string[] { L10n.T("coop.open_logs"), L10n.T("coop.close") } : new string[] {
                L10n.F("coop.cutscenes", Plugin.Instance.P2CanTriggerCutscenes ? L10n.T("coop.either") : L10n.T("coop.p1_only")),
                seats.Length == 1 ? L10n.T(s_waitFor < 0 ? "coop.choose_local_pad" : "coop.add_local") : L10n.F("coop.remove_local", seats[seats.Length - 1]),
                L10n.F("coop.mode", PlayMode.Current == PlayMode.Single ? L10n.T("coop.single") :
                    seats.Length > 2 ? L10n.F("coop.players_count", seats.Length) : L10n.T("coop.two_players")),
                L10n.T("coop.open_logs"),
                L10n.T("coop.close") };
            if (!online && HasExtraJoinRow(seats.Length))
            {
                Array.Resize(ref actions, actions.Length + 1);
                actions[actions.Length - 1] = L10n.T(s_waitFor < 0 ? "coop.choose_extra_join_device" : "coop.add_local");
            }
            if (online && KeystoneRecovery.CanGrant(OpeningSeat))
            {
                Array.Resize(ref actions, actions.Length + 1);
                actions[actions.Length - 1] = L10n.T("coop.recover_keystone");
            }
            string build = BuildLabel();
            float w = Mathf.Min(Screen.width - 40f, 760f), inner = w - 40f, lineH = s_text.fontSize * 1.9f;
            string title = L10n.T(!online && seats.Length > 2 ? "pause.multiplayer_settings" : "coop.settings");
            float h = 36f + LineH(s_title, title, inner);
            float editWidth = Mathf.Min(150f, inner * 0.3f);
            foreach (string label in seatRows) h += Mathf.Max(lineH * 1.15f, LineH(s_row, label, inner - editWidth)) + s_row.margin.vertical;
            foreach (string label in actions) h += Mathf.Max(lineH * 1.15f, LineH(s_row, label, inner)) + s_row.margin.vertical;
            if (s_message != "") h += LineH(s_text, s_message, inner);
            if (!online) h += LineH(s_small, L10n.T("coop.cutscene_hint"), inner);
            h += LineH(s_small, build, inner);
            h = Mathf.Min(h, Screen.height - 20f);
            Rect r = new Rect((Screen.width - w) * 0.5f, Mathf.Max(10f, (Screen.height - h) * 0.5f), w, h);
            NativeGui.Panel("coop.settings.background", r, new Color(0.05f, 0.07f, 0.12f, 0.92f));
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 14f, inner, r.height - 28f));
            NativeGui.LayoutLabel("coop.settings.title", title, s_title);
            for (int i = 0; i < seatRows.Length; i++)
            {
                string id = "coop.settings.seat." + seats[i];
                float height = Mathf.Max(lineH * 1.15f, LineH(s_row, seatRows[i], inner - editWidth));
                GUILayout.BeginHorizontal();
                Font nameFont = s_waitFor != seats[i] && TeammateIdentity.Name(seats[i]).Length != 0 ? NativeGui.NameFont : null;
                if (RowButton(id, seatRows[i], s_focus == i, height, 0f, nameFont))
                { s_focus = i; if (s_waitFor == 0) Activate(i); }
                if (RowButton(id + ".name", L10n.T("coop.rename_player"), false, height, editWidth) && s_waitFor == 0)
                { s_focus = i; BeginNameEdit(seats[i]); }
                GUILayout.EndHorizontal();
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
        internal static int KeystoneRow { get { return KeystoneRecovery.CanGrant(OpeningSeat) ? s_seats.LocalSeats.Length + 2 : -1; } }
        public static int SettingsCloseRow { get { return s_seats.LocalSeats.Length + (NetPlay.Enabled ? 1 : 4); } }
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

        // T-093: the chooser's own styles, sized from ModeSelectLayout for the current screen.
        static int s_modeH;
        static GUIStyle s_modeTitle, s_modeSmall, s_modeRow, s_modeRowFocus;

        static void ModeStyles(OriCoop.Ui.ModeSelectLayout l)
        {
            if (s_modeH == Screen.height && s_modeRow != null) return;
            s_modeH = Screen.height;
            s_modeTitle = new GUIStyle(s_title); s_modeTitle.fontSize = (int)l.TitlePx; s_modeTitle.normal.textColor = new Color(0.92f, 0.97f, 1f);
            s_modeSmall = new GUIStyle(s_small); s_modeSmall.fontSize = (int)l.SmallPx;
            s_modeRow = new GUIStyle(s_row); s_modeRow.fontSize = (int)l.FontPx; s_modeRow.richText = true;
            s_modeRow.padding = new RectOffset((int)(l.FontPx * 0.6f), (int)(l.FontPx * 0.6f), (int)(l.FontPx * 0.25f), (int)(l.FontPx * 0.25f));
            s_modeRowFocus = new GUIStyle(s_modeRow); s_modeRowFocus.normal.textColor = new Color(1f, 1f, 1f);
        }

        // The selected row gets Ori's blue wisp (a slow soft pulse) instead of the old yellow bar.
        static bool ModeRow(string id, string text, bool focused, float height)
        {
            GUIStyle style = focused ? s_modeRowFocus : s_modeRow;
            Rect r = GUILayoutUtility.GetRect(new GUIContent(text), style, GUILayout.Height(height));
            Vector2 p = GUIUtility.GUIToScreenPoint(r.position);
            Rect screen = new Rect(p.x, p.y, r.width, r.height);
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3f);
            NativeGui.Panel(id + ".background", screen, focused ? new Color(0.25f, 0.55f, 1f, 0.22f + 0.14f * pulse) : new Color(0.02f, 0.04f, 0.09f, 0.55f));
            if (focused) NativeGui.Panel(id + ".glow", new Rect(screen.x - screen.height * 0.4f, screen.y - screen.height * 0.12f, screen.width + screen.height * 0.8f, screen.height * 1.24f),
                new Color(0.3f, 0.6f, 1f, 0.07f + 0.05f * pulse));
            if (!NativeGui.Available) return GUI.Button(r, text, style);
            Color color = style.normal.textColor * GUI.color * GUI.contentColor;
            if (GUI.enabled && r.Contains(Event.current.mousePosition))
                NativeGui.Panel(id + ".hover", screen, new Color(0.4f, 0.7f, 1f, 0.14f * color.a));
            NativeGui.Label(id, style.padding.Remove(screen), text, style.fontSize, style.alignment, color);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // Test hook (scenario modeselect): the layout the chooser was last drawn with and the screen it was for.
        public static OriCoop.Ui.ModeSelectLayout LastModeLayout;
        public static int LastModeScreenW, LastModeScreenH;

        static void DrawModeSelect()
        {
            OriCoop.Ui.ModeSelectLayout l = OriCoop.Ui.ModeSelectLayout.Compute(Screen.width, Screen.height);
            LastModeLayout = l; LastModeScreenW = Screen.width; LastModeScreenH = Screen.height;
            ModeStyles(l);
            float pad = l.FontPx * 0.7f;
            Rect r = new Rect((Screen.width - l.PanelW) * 0.5f, Mathf.Max(10f, (Screen.height - l.PanelH) * 0.5f), l.PanelW, l.PanelH);
            NativeGui.Panel("coop.mode.background", r, new Color(0.04f, 0.07f, 0.14f, 0.92f));
            GUILayout.BeginArea(new Rect(r.x + pad, r.y + pad * 0.7f, r.width - 2f * pad, r.height - 1.4f * pad));
            NativeGui.LayoutLabel("coop.mode.title", L10n.T("coop.play_how"), s_modeTitle);
            GUILayout.Space(l.FontPx * 0.3f);
            string[] rows = {
                L10n.F("coop.single_desc", (int)l.SmallPx),
                L10n.F("coop.two_desc", (int)l.SmallPx)
            };
            for (int i = 0; i < rows.Length; i++)
            {
                if (ModeRow("coop.mode.row." + i, rows[i], i == s_focus, l.RowH))
                { s_focus = i; Activate(i); }
            }
            GUILayout.Space(l.FontPx * 0.3f);
            NativeGui.LayoutLabel("coop.mode.hint", L10n.T("coop.play_hint"), s_modeSmall);
            GUILayout.EndArea();
        }
    }
}
