using System;
using System.Collections.Generic;
using Game;
using J2i.Net.XInputWrapper;
using UnityEngine;

namespace OriCoop
{
    // In-game online HUD (only while NetPlay.Enabled):
    //   - Signal bars top-right from NetPlay.RttMs / LossPercent, tiny "42 ms"
    //   - Status chips under signal bars (cutscene watching / partner in menu)
    //   - Host waiting chip top-left while host and not PartnerHere (room code / address / invite)
    //   - Bilingual toasts at top centre (fade in/out, ~3.5 s)
    //   - Partner name tag above partner's Ori (P2Context.P2) and off-screen arrow with distance
    //   - Guest disconnect dialog with reconnect countdown: Keep playing alone / Back to title
    public static class NetHud
    {
        public static bool ModalOpen { get { return s_disconnectModalOpen; } }
        // Test-only mutation switch: proves the title checks fail when the dialog outlives the session.
        static readonly bool s_mutateKeepModal = System.Environment.GetEnvironmentVariable("ORICOOP_MUTATE_KEEP_MODAL") == "1";

        struct ToastItem
        {
            public string Text;
            public float StartTime;
            public float Duration;
            public bool Shown;
        }

        static readonly Queue<ToastItem> s_toastQueue = new Queue<ToastItem>();
        static ToastItem s_currentToast;
        static bool s_hasCurrentToast;

        static bool s_hadSession;
        static bool s_hadPreviousDrop;
        static bool s_localInitiatedEnd;

        // Disconnect dialog & chip state
        static bool s_disconnectModalOpen;
        static bool s_collapsedChip;
        static float s_disconnectWaitSec;
        static int s_modalFocus; // 0: 继续单人, 1: 回到标题
        static float s_ignoreModalNavUntil;
        static string s_testPage;

        static string[] L10nTestPages()
        {
            return new[] { "hud.signal", "hud.chips", "hud.waiting", "hud.solo", "hud.toast", "hud.disconnect" };
        }

        static void L10nTestShow(string page, bool on)
        {
            s_testPage = on ? page : null;
        }

        // XInput polling for modal dialog
        static XInputState s_xState = new XInputState();
        static readonly bool[] s_padConnected = new bool[4];
        static readonly int[] s_padButtons = new int[4];
        static readonly int[] s_padPrevButtons = new int[4];
        static float s_padScanTimer;

        const int A = 0x1000, B = 0x2000, START = 0x10, BACK = 0x20,
            DUP = 1, DDOWN = 2, DLEFT = 4, DRIGHT = 8;

        // Textures
        static Texture2D s_solidTex;
        static Texture2D s_scrimTex;
        static Texture2D s_chipBgTex;
        static Texture2D s_glowStreak;
        static Texture2D s_dotTex;

        // Styles
        static GUIStyle s_toastZh;
        static GUIStyle s_signalText;
        static GUIStyle s_chipStyle;
        static GUIStyle s_arrowStyle;
        static GUIStyle s_modalTitleZh;
        static GUIStyle s_modalItemZh;
        static GUIStyle s_modalHelperZh;
        static GUIStyle s_promptStyle;

        public static void Init()
        {
            InitTextures();
        }

        static Texture2D MakeSolidTex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        static void InitTextures()
        {
            if (s_solidTex != null) return;
            s_solidTex = MakeSolidTex(Color.white);
            s_scrimTex = MakeSolidTex(new Color(0.008f, 0.031f, 0.055f, 0.65f)); // #02080E @ 65%
            s_chipBgTex = MakeSolidTex(new Color(0.027f, 0.075f, 0.122f, 0.75f));

            // Glowing cyan streak
            s_glowStreak = new Texture2D(256, 16, TextureFormat.ARGB32, false);
            s_glowStreak.wrapMode = TextureWrapMode.Clamp;
            s_glowStreak.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < 16; y++)
            {
                float vy = Mathf.Abs((y + 0.5f - 8f) / 8f);
                float core = Mathf.Clamp01(1f - vy * 3.5f);
                float bloom = Mathf.Clamp01(1f - vy);
                for (int x = 0; x < 256; x++)
                {
                    float ux = Mathf.Abs((x + 0.5f - 128f) / 128f);
                    float fx = Mathf.Clamp01(1f - ux * ux);
                    float a = (core * 0.85f + bloom * 0.28f) * fx;
                    Color c = Color.Lerp(new Color(0.39f, 0.87f, 0.96f, 0f), new Color(0.95f, 0.99f, 1f, 1f), core);
                    c.a = a;
                    s_glowStreak.SetPixel(x, y, c);
                }
            }
            s_glowStreak.Apply();
            s_glowStreak.hideFlags = HideFlags.HideAndDontSave;

            // Dot texture
            s_dotTex = new Texture2D(16, 16, TextureFormat.ARGB32, false);
            s_dotTex.wrapMode = TextureWrapMode.Clamp;
            s_dotTex.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(8f, 8f)) / 8f;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;
                    s_dotTex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            s_dotTex.Apply();
            s_dotTex.hideFlags = HideFlags.HideAndDontSave;
        }

        // Callers often pass already-translated text first and "" second (saves, rooms): the empty side falls back to
        // the other one, so English players no longer get blank toasts (T-135).
        public static void Toast(string zh, string en)
        {
            Toast(s_noFallbackMutation ? (L10n.Zh ? zh : en) : L10n.Pick(zh, en));
        }
        // Test-only mutation switch (T-135): the old pick-by-language without fallback.
        static readonly bool s_noFallbackMutation = Environment.GetEnvironmentVariable("ORICOOP_MUTATE_TOAST_NO_FALLBACK") == "1";
        internal static event Action<string> ToastShown;
        internal static bool ToastsPending { get { return s_hasCurrentToast || s_toastQueue.Count > 0; } }
        internal static int QueuedToasts { get { return s_toastQueue.Count; } }
        internal static string LastQueuedToast { get { string last = null; foreach (ToastItem t in s_toastQueue) last = t.Text; return last; } }
        // Test helper: drop toasts queued after the first `keep` (a check's own toasts must not show for seconds).
        internal static void TestDropQueuedToasts(int keep)
        {
            ToastItem[] items = s_toastQueue.ToArray(); s_toastQueue.Clear();
            for (int i = 0; i < items.Length && i < keep; i++) s_toastQueue.Enqueue(items[i]);
        }

        public static void Toast(string text)
        {
            // An empty toast showed nothing yet held the queue for 3.5 s (T-135).
            if (string.IsNullOrEmpty(text) && !s_noFallbackMutation) return;
            ToastItem item;
            item.Text = text ?? "";
            item.StartTime = 0f;
            item.Duration = 3.5f;
            item.Shown = false;
            s_toastQueue.Enqueue(item);
        }

        public static void OnSessionStarted()
        {
            if (s_disconnectModalOpen || s_collapsedChip || s_hadPreviousDrop)
            {
                s_disconnectModalOpen = false;
                s_collapsedChip = false;
                s_hadPreviousDrop = false;
                Toast(
                    L10n.T("hud.reconnected"));
            }
            else if (s_hadSession)
            {
                Toast(
                    L10n.T("hud.reconnected"));
            }
            else
            {
                Toast(L10n.SessionText(NetPlay.IsHost ? "hud.joined.host" : "hud.joined.partner"));
                s_hadSession = true;
            }

            // Version check
            if (!string.IsNullOrEmpty(NetPlay.RemoteVersion) && NetPlay.RemoteVersion != NetPlay.ModVersion)
            {
                Toast(
                    L10n.SessionFormat("hud.version.mismatch", NetPlay.ModVersion, NetPlay.RemoteVersion));
            }
        }

        public static void OnSessionEnded()
        {
            if (NetPlay.IsHost)
            {
                if (!NetPlay.UsesCServer || !NetPlay.EndingByChoice) Toast(L10n.SessionText("hud.partner.left"));
            }
            // A server room stays open when the last remote leaves.
            else if (!s_localInitiatedEnd && !NetPlay.EndingByChoice && (!NetPlay.UsesCServer || !NetPlay.Connected))
            {
                s_hadPreviousDrop = true;
                s_disconnectModalOpen = true;
                s_disconnectWaitSec = 0f;
                s_modalFocus = 0;
                s_collapsedChip = false;
                s_ignoreModalNavUntil = Time.unscaledTime + 0.3f;
            }
            s_localInitiatedEnd = false;
        }

        static void PollPads()
        {
            s_padScanTimer -= Time.unscaledDeltaTime;
            bool rescan = s_padScanTimer <= 0f;
            if (rescan) s_padScanTimer = 1f;

            for (int i = 0; i < 4; i++)
            {
                s_padPrevButtons[i] = s_padButtons[i];
                if (CoopMenu.TestPadButtons != null)
                {
                    s_padConnected[i] = true;
                    s_padButtons[i] = CoopMenu.TestPadButtons[i];
                    continue;
                }
                if (!s_padConnected[i] && !rescan)
                {
                    s_padButtons[i] = 0;
                    continue;
                }
                s_padConnected[i] = Pads.VirtualPads.GetState(i, ref s_xState) == 0;
                int b = 0;
                if (s_padConnected[i])
                {
                    XInputGamepad g = s_xState.Gamepad;
                    b = g.wButtons;
                    if (g.sThumbLY > 16000) b |= DUP;
                    if (g.sThumbLY < -16000) b |= DDOWN;
                    if (g.sThumbLX > 16000) b |= DRIGHT;
                    if (g.sThumbLX < -16000) b |= DLEFT;
                }
                s_padButtons[i] = b;
            }
        }

        static bool AnyPadPressed(int mask)
        {
            for (int i = 0; i < 4; i++)
                if ((s_padButtons[i] & mask) != 0 && (s_padPrevButtons[i] & mask) == 0)
                    return true;
            return false;
        }

        static bool HudCovered
        {
            get { return !NetPlay.Enabled || OnlineRoomScreen.Visible || CoopMenu.IsOpen
                || (NetPlay.UsesCServer && (CsRoomPage.InGame || SessionScreens.Blocking)); }
        }

        public static void Tick()
        {
            // Toast queue progression
            if (s_hasCurrentToast)
            {
                if (Time.realtimeSinceStartup - s_currentToast.StartTime >= s_currentToast.Duration)
                {
                    s_hasCurrentToast = false;
                }
            }
            if (!s_hasCurrentToast && s_toastQueue.Count > 0)
            {
                s_currentToast = s_toastQueue.Dequeue();
                s_currentToast.StartTime = Time.realtimeSinceStartup;
                s_hasCurrentToast = true;
            }

            if (CoopTrace.Changed("ui.focused", GameController.IsFocused ? "1" : "0"))
                CoopTrace.Event("ui", "focus", "focused", GameController.IsFocused ? 1 : 0, "online", NetPlay.Enabled ? 1 : 0);
            // No session any more (back on the title, or ended any other way): the dialog is gone. It is only drawn
            // while NetPlay.Enabled, but its key handling below kept running invisibly on the title: Up/Down in the
            // title menu flipped it to 回到标题 and Enter / A then restarted the game under the player (playtest
            // 2026-11-16, P5: the title "couldn't be used").
            if (!NetPlay.Enabled && (s_disconnectModalOpen || s_collapsedChip) && !s_mutateKeepModal)
            {
                s_disconnectModalOpen = false; s_collapsedChip = false; s_hadPreviousDrop = false;
                Plugin.Log.LogInfo("net hud: disconnect dialog closed (no online session)");
                CoopTrace.Event("ui", "dialog.close", "why", "no session");
            }

            // Disconnect dialog & reconnect tracking
            if (s_disconnectModalOpen || s_collapsedChip)
            {
                s_disconnectWaitSec += Time.unscaledDeltaTime;

                // Auto-close when partner reconnects
                if (NetPlay.PartnerHere)
                {
                    s_disconnectModalOpen = false;
                    s_collapsedChip = false;
                    s_hadPreviousDrop = false;
                    Toast(
                        L10n.T("hud.reconnected"));
                    return;
                }
            }

            // Input handling for disconnect modal
            if (s_disconnectModalOpen)
            {
                PollPads();
                if (HudCovered || Time.unscaledTime < s_ignoreModalNavUntil) return;

                bool navLeft = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.UpArrow) ||
                    AnyPadPressed(DLEFT) || AnyPadPressed(DUP);
                bool navRight = Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.DownArrow) ||
                    AnyPadPressed(DRIGHT) || AnyPadPressed(DDOWN);

                if (navLeft || navRight)
                    s_modalFocus = (s_modalFocus == 0) ? 1 : 0;

                bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) || AnyPadPressed(A);
                bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);

                if (confirm)
                {
                    ActivateModalOption(s_modalFocus);
                }
                else if (back)
                {
                    // Back defaults to keep playing alone
                    ActivateModalOption(0);
                }
            }
        }

        static void ActivateModalOption(int opt)
        {
            if (opt == 0) // 继续单人 Keep playing alone
            {
                s_disconnectModalOpen = false;
                s_collapsedChip = true;
            }
            else // 回到标题 Back to title
            {
                s_disconnectModalOpen = false;
                s_collapsedChip = false;
                s_localInitiatedEnd = true;
                NetPlay.End("guest left");
                if (GameController.Instance != null)
                {
                    GameController.Instance.RestartGame();
                }
            }
        }

        static void EnsureStyles(float scale)
        {
            if (s_toastZh != null) return;

            s_toastZh = new GUIStyle(GUI.skin.label);
            s_toastZh.font = GUI.skin.font;
            s_toastZh.alignment = TextAnchor.MiddleCenter;
            s_toastZh.fontSize = Mathf.RoundToInt(34f * scale);
            s_toastZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f); // #DDEAE5

            s_signalText = new GUIStyle(GUI.skin.label);
            s_signalText.font = GUI.skin.font;
            s_signalText.alignment = TextAnchor.MiddleRight;
            s_signalText.fontSize = Mathf.RoundToInt(20f * scale);
            s_signalText.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_chipStyle = new GUIStyle(GUI.skin.label);
            s_chipStyle.font = GUI.skin.font;
            s_chipStyle.alignment = TextAnchor.MiddleLeft;
            s_chipStyle.fontSize = Mathf.RoundToInt(20f * scale);
            s_chipStyle.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_arrowStyle = new GUIStyle(GUI.skin.label);
            s_arrowStyle.font = GUI.skin.font;
            s_arrowStyle.alignment = TextAnchor.MiddleCenter;
            s_arrowStyle.fontSize = Mathf.RoundToInt(22f * scale);
            s_arrowStyle.normal.textColor = new Color(0.945f, 0.988f, 1.0f);

            s_modalTitleZh = new GUIStyle(GUI.skin.label);
            s_modalTitleZh.font = GUI.skin.font;
            s_modalTitleZh.alignment = TextAnchor.MiddleCenter;
            s_modalTitleZh.fontSize = Mathf.RoundToInt(56f * scale);
            s_modalTitleZh.normal.textColor = new Color(0.95f, 0.65f, 0.55f);

            s_modalItemZh = new GUIStyle(GUI.skin.label);
            s_modalItemZh.font = GUI.skin.font;
            s_modalItemZh.alignment = TextAnchor.MiddleCenter;
            s_modalItemZh.fontSize = Mathf.RoundToInt(42f * scale);
            s_modalItemZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_modalHelperZh = new GUIStyle(s_modalItemZh);
            s_modalHelperZh.fontSize = Mathf.RoundToInt(22f * scale);
            s_modalHelperZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_promptStyle = new GUIStyle(GUI.skin.label);
            s_promptStyle.font = GUI.skin.font;
            s_promptStyle.alignment = TextAnchor.MiddleRight;
            s_promptStyle.fontSize = Mathf.RoundToInt(22f * scale);
            s_promptStyle.normal.textColor = new Color(0.867f, 0.918f, 0.898f);
        }

        public static void Draw()
        {
            bool sample = L10nProbe.On && !string.IsNullOrEmpty(s_testPage);
            if (!NetPlay.Enabled && !sample && !Saves.CoopSaveFolder.Active) return;

            try
            {
                float scale = Screen.height / 1080f;
                InitTextures();
                EnsureStyles(scale);

                if (sample)
                {
                    if (s_testPage == "hud.signal")
                    {
                        DrawSignalBars(scale, 1f, true);
                        DrawPartnerIndicators(scale, 1f, true);
                    }
                    else if (s_testPage == "hud.chips") DrawStatusChips(scale, 1f, true);
                    else if (s_testPage == "hud.waiting") DrawHostWaitingChip(scale, 1f, true);
                    else if (s_testPage == "hud.solo") DrawCollapsedChip(scale, 1f);
                    else if (s_testPage == "hud.toast") DrawToast(scale, true);
                    else if (s_testPage == "hud.disconnect") DrawDisconnectModal(scale, true);
                    return;
                }

                if (HudCovered) { DrawToast(scale); return; }
                float hudOpacity = P2Hud.GameHudOpacity();

                // Draw gameplay HUD elements only when HUD is visible
                if (hudOpacity > 0.01f)
                {
                    DrawSignalBars(scale, hudOpacity);
                    DrawStatusChips(scale, hudOpacity);
                    DrawSpectate(scale, hudOpacity);
                    DrawHostWaitingChip(scale, hudOpacity);
                    DrawPartnerIndicators(scale, hudOpacity);
                    if (s_collapsedChip)
                        DrawCollapsedChip(scale, hudOpacity);
                }

                // Toast queue & Guest disconnect dialog draw regardless of GameHudOpacity
                DrawToast(scale);
                if (s_disconnectModalOpen)
                {
                    DrawDisconnectModal(scale);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("NetHud Draw error: " + ex);
            }
        }

        // Lit bars (lag test ⑦: old scale was flat above 250 ms): <80 ms & <2% loss = 4, <150 & <5% = 3,
        // <300 & <10% = 2, else 1; 0 when disconnected or silent past NetPlay.SilenceHintMs.
        public static int SignalBars(bool connected, bool lost, float rtt, float loss) { return Ui.SignalLevels.Bars(connected, lost, rtt, loss); }

        // Bar colour also separates high latencies: cyan < 250 ms, amber 250-500 ms, red >= 500 ms or silent.
        public static Color SignalColor(bool lost, float rtt, float alpha)
        {
            int tier = Ui.SignalLevels.ColorTier(lost, rtt);
            if (tier == 2) return new Color(0.95f, 0.36f, 0.32f, alpha);
            if (tier == 1) return new Color(0.98f, 0.78f, 0.30f, alpha);
            return new Color(0.39f, 0.87f, 0.91f, alpha);
        }

        // "信号中断 3 秒 · 正在恢复连接" while no packet has arrived for >= 1 s (before the 10 s timeout modal).
        public static string SignalLostText()
        {
            if (!NetPlay.SignalLost) return "";
            int seconds = Mathf.FloorToInt(NetPlay.SilenceMs / 1000f);
            return L10n.F("hud.signal.lost", seconds);
        }

        static void DrawSignalBars(float scale, float hudAlpha, bool sample = false)
        {
            NativeGui.BeginPage("hud.signal");
            try
            {
                float rightMargin = 40f * scale;
                float topY = 24f * scale;

                float barW = 6f * scale;
                float[] barHeights = new float[] { 8f * scale, 14f * scale, 20f * scale, 26f * scale };
                float barGap = 4f * scale;
                float totalBarsW = 4f * barW + 3f * barGap; // 36 * scale
                float barsX = Screen.width - rightMargin - totalBarsW;

                int lit = sample ? 4 : SignalBars(NetPlay.Connected, NetPlay.SignalLost, NetPlay.RttMs, NetPlay.LossPercent);
                Color litColor = SignalColor(!sample && NetPlay.SignalLost, sample ? 42f : NetPlay.RttMs, hudAlpha);

                // Draw bars
                for (int i = 0; i < 4; i++)
                {
                    float bh = barHeights[i];
                    float bx = barsX + i * (barW + barGap);
                    float by = topY + 26f * scale - bh; // align bottoms
                    Color barColor = (i < lit) ? litColor : new Color(0.25f, 0.32f, 0.36f, 0.45f * hudAlpha);
                    NativeGui.Panel("hud.signal.bar." + i, new Rect(bx, by, barW, bh), barColor);
                }

                // Latency text ("-- ms" while silent: the last RTT would be stale, lag test ②)
                string rttText = sample ? "42 ms" : NetPlay.Connected && !NetPlay.SignalLost ? (Mathf.RoundToInt(NetPlay.RttMs) + " ms") : "-- ms";
                Rect textR = new Rect(barsX - 110f * scale, topY, 100f * scale, 26f * scale);
                Color textC = new Color(0.867f, 0.918f, 0.898f, hudAlpha);
                DrawText("hud.signal.rtt", textR, rttText, s_signalText, textC);

                int me = sample ? 1 : Roles.Mine;
                float rttW = s_signalText.CalcSize(new GUIContent(rttText)).x;
                float roleRight = barsX - 10f * scale - rttW - 14f * scale;
                Color mc = Roles.ColorOf(me);
                string roleText = L10n.F("hud.role", Roles.Tag(me), sample ? L10n.T("roles.host") : Roles.Word(me));
                DrawText("hud.signal.role", new Rect(roleRight - 160f * scale, topY, 160f * scale, 26f * scale), roleText,
                    s_signalText, new Color(mc.r, mc.g, mc.b, hudAlpha));
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawStatusChips(float scale, float hudAlpha, bool sample = false)
        {
            NativeGui.BeginPage("hud.chips");
            try
            {
                string lost = sample ? "" : SignalLostText();
                string statusLine = sample ? L10n.T("hud.watching") : lost.Length > 0 ? lost
                    : StorySync.Watching ? L10n.SessionText("hud.watching")
                    : SessionRules.PauseText;

                if (string.IsNullOrEmpty(statusLine)) return;

                GUIContent c = new GUIContent(statusLine);
                Vector2 sz = s_chipStyle.CalcSize(c);
                float padX = 14f * scale;
                float padY = 6f * scale;
                float w = sz.x + padX * 2f;
                float h = sz.y + padY * 2f;

                float rightMargin = 40f * scale;
                float topY = 62f * scale;
                Rect chipR = new Rect(Screen.width - rightMargin - w, topY, w, h);

                Color bgC = new Color(0.027f, 0.075f, 0.122f, 0.8f * hudAlpha);
                NativeGui.Panel("hud.chips.background", chipR, bgC);
                DrawRectOutline("hud.chips.outline", chipR, new Color(0.39f, 0.87f, 0.91f, 0.6f * hudAlpha), 1.5f * scale);

                Color textC = new Color(0.95f, 0.88f, 0.65f, hudAlpha);
                DrawText("hud.chips.text", new Rect(chipR.x + padX, chipR.y + padY, sz.x, sz.y), statusLine, s_chipStyle, textC);
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawSpectate(float scale, float hudAlpha)
        {
            if (!Revive.SpectateActive) return;
            NativeGui.BeginPage("hud.spectate");
            try
            {
                DrawText("hud.spectate.text", new Rect(0f, 100f * scale, Screen.width, 32f * scale),
                    L10n.F("hud.spectate", Roles.Tag(Revive.SpectateTarget)), s_toastZh,
                    new Color(0.95f, 0.88f, 0.65f, hudAlpha));
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawHostWaitingChip(float scale, float hudAlpha, bool sample = false)
        {
            NativeGui.BeginPage("hud.waiting");
            try
            {
                if (!sample && (!NetPlay.IsHost || NetPlay.PartnerHere)) return;

                string line = "";
                if (sample) line = L10n.F("hud.room", "K7F Q2M") + "\n" + L10n.F("hud.invite", "F5");
                else if (NetPlay.UsesSteam)
                {
                    line = L10n.F("hud.invite", "F5");
                }
                // LAN addresses belong only on the direct host lobby page, never on the gameplay HUD.
                // C-server must not fall through to a local-address chip either.
                else return;

                GUIContent c = new GUIContent(line);
                Vector2 sz = s_chipStyle.CalcSize(c);
                float padX = 16f * scale;
                float padY = 8f * scale;
                float w = sz.x + padX * 2f;
                float h = sz.y + padY * 2f;

                Rect r = new Rect(30f * scale, 24f * scale, w, h);
                Color bgC = new Color(0.027f, 0.075f, 0.122f, 0.85f * hudAlpha);
                NativeGui.Panel("hud.waiting.background", r, bgC);
                DrawRectOutline("hud.waiting.outline", r, new Color(0.39f, 0.87f, 0.91f, 0.6f * hudAlpha), 1.5f * scale);

                Color textC = new Color(0.867f, 0.918f, 0.898f, hudAlpha);
                DrawText("hud.waiting.text", new Rect(r.x + padX, r.y + padY, sz.x, sz.y), line, s_chipStyle, textC);
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawCollapsedChip(float scale, float hudAlpha)
        {
            NativeGui.BeginPage("hud.solo");
            try
            {
                string line = L10n.T("hud.solo.reconnecting");
                GUIContent c = new GUIContent(line);
                Vector2 sz = s_chipStyle.CalcSize(c);
                float padX = 16f * scale;
                float padY = 8f * scale;
                float w = sz.x + padX * 2f;
                float h = sz.y + padY * 2f;

                Rect r = new Rect(30f * scale, 24f * scale, w, h);
                Color bgC = new Color(0.027f, 0.075f, 0.122f, 0.85f * hudAlpha);
                NativeGui.Panel("hud.solo.background", r, bgC);
                DrawRectOutline("hud.solo.outline", r, new Color(0.95f, 0.65f, 0.55f, 0.8f * hudAlpha), 1.5f * scale);

                Color textC = new Color(0.95f, 0.85f, 0.75f, hudAlpha);
                DrawText("hud.solo.text", new Rect(r.x + padX, r.y + padY, sz.x, sz.y), line, s_chipStyle, textC);
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawPartnerIndicators(float scale, float hudAlpha, bool sample = false)
        {
            NativeGui.BeginPage("hud.signal");
            try
            {
                if (sample)
                {
                    string label = "P2 42 m \u2192";
                    Vector2 sz = s_arrowStyle.CalcSize(new GUIContent(label));
                    Color rc = Roles.ColorOf(2);
                    DrawText("hud.signal.partner.2", new Rect(Screen.width - 80f * scale - sz.x * 0.5f,
                        Screen.height * 0.5f - sz.y * 0.5f, sz.x, sz.y), label, s_arrowStyle,
                        new Color(rc.r, rc.g, rc.b, hudAlpha));
                    return;
                }
                if (!NetPlay.PartnerHere) return;
                foreach (Puppet puppet in Players.Remotes) DrawPartnerIndicator(puppet, scale, hudAlpha);
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawPartnerIndicator(Puppet puppet, float scale, float hudAlpha)
        {
            if (puppet.Sein == null || !puppet.Sein.gameObject.activeInHierarchy) return;

            Camera cam = UI.Cameras.Current != null ? UI.Cameras.Current.Camera : null;
            if (cam == null) return;

            Vector3 partnerPos = puppet.Sein.Position;
            Vector3 headPos = new Vector3(partnerPos.x, partnerPos.y + 1.4f, partnerPos.z);
            Vector3 sp = cam.WorldToScreenPoint(headPos);
            float guiY = Screen.height - sp.y;

            bool onScreen = (sp.z > 0f && sp.x >= 50f * scale && sp.x <= Screen.width - 50f * scale &&
                guiY >= 50f * scale && guiY <= Screen.height - 50f * scale);

            // On screen, the partner's single tag (role, name, health) is P2Hud's, drawn by the game's HUD camera.
            if (!onScreen)
            {
                // Off-screen locator arrow + distance
                Vector3 p1Pos = P2Context.P1 != null ? P2Context.P1.Position : (Characters.Sein != null ? Characters.Sein.Position : Vector3.zero);
                float dist = Vector3.Distance(p1Pos, partnerPos);
                string distStr = Mathf.RoundToInt(dist) + " m";

                Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                Vector2 target = new Vector2(sp.x, Screen.height - sp.y);
                if (sp.z <= 0f) target = center - (target - center); // invert if behind camera

                Vector2 dir = (target - center).normalized;
                if (dir.sqrMagnitude < 0.001f) dir = Vector2.right;

                float marginX = 80f * scale;
                float marginY = 60f * scale;
                float halfW = Screen.width * 0.5f - marginX;
                float halfH = Screen.height * 0.5f - marginY;

                float tx = (dir.x > 0f ? halfW : -halfW) / dir.x;
                float ty = (dir.y > 0f ? halfH : -halfH) / dir.y;
                float t = Mathf.Min(Mathf.Abs(tx), Mathf.Abs(ty));
                Vector2 edge = center + dir * t;

                float angle = Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;
                string arrow;
                if (angle >= -22.5f && angle < 22.5f) arrow = "→";
                else if (angle >= 22.5f && angle < 67.5f) arrow = "↗";
                else if (angle >= 67.5f && angle < 112.5f) arrow = "↑";
                else if (angle >= 112.5f && angle < 157.5f) arrow = "↖";
                else if (angle >= -67.5f && angle < -22.5f) arrow = "↘";
                else if (angle >= -112.5f && angle < -67.5f) arrow = "↓";
                else if (angle >= -157.5f && angle < -112.5f) arrow = "↙";
                else arrow = "←";

                string who = TeammateIdentity.Label(puppet.Seat);
                string label = (edge.x > Screen.width * 0.5f) ? (who + " " + distStr + " " + arrow) : (arrow + " " + distStr + " " + who);
                GUIContent content = new GUIContent(label);
                Vector2 sz = s_arrowStyle.CalcSize(content);

                Rect r = new Rect(edge.x - sz.x * 0.5f, edge.y - sz.y * 0.5f, sz.x, sz.y);
                r.x = Mathf.Clamp(r.x, 0f, Mathf.Max(0f, Screen.width - r.width));
                Color rc = Roles.ColorOf(puppet.Seat);
                DrawText("hud.signal.partner." + puppet.Seat, r, label, s_arrowStyle, new Color(rc.r, rc.g, rc.b, hudAlpha));
            }
        }

        static void DrawToast(float scale, bool sample = false)
        {
            NativeGui.BeginPage("hud.toast");
            try
            {
                if (!sample && !s_hasCurrentToast) return;

                float elapsed = sample ? 1f : Time.realtimeSinceStartup - s_currentToast.StartTime;
                float alpha = 1f;
                if (elapsed < 0.35f) alpha = Mathf.Clamp01(elapsed / 0.35f);
                else if (!sample && elapsed > s_currentToast.Duration - 0.5f) alpha = Mathf.Clamp01((s_currentToast.Duration - elapsed) / 0.5f);

                float topY = 50f * scale;

                Color textC = new Color(0.867f, 0.918f, 0.898f, alpha);

                DrawText("hud.toast.text", new Rect(0, topY, Screen.width, 48f * scale), sample ? L10n.T("hud.reconnected") : s_currentToast.Text, s_toastZh, textC);
                if (!sample && !s_currentToast.Shown && alpha > 0f && Event.current.type == EventType.Repaint)
                {
                    s_currentToast.Shown = true;
                    if (ToastShown != null) ToastShown(s_currentToast.Text);
                }
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawDisconnectModal(float scale, bool sample = false)
        {
            NativeGui.BeginPage("hud.disconnect");
            try
            {
                // Scrim
                NativeGui.Texture("hud.disconnect.scrim", new Rect(0, 0, Screen.width, Screen.height), s_scrimTex, Color.white);

                float cx = Screen.width * 0.5f;
                float topY = 180f * scale;

                // Title
                DrawText("hud.disconnect.title", new Rect(0, topY, Screen.width, 64f * scale), L10n.SessionText("hud.disconnect.title"), s_modalTitleZh, new Color(0.95f, 0.65f, 0.55f));

                // Spinner & Reconnecting counter
                float spinY = topY + 90f * scale;
                DrawSpinner(cx - 150f * scale, spinY + 20f * scale, scale);
                int sec = sample ? 10 : Mathf.FloorToInt(s_disconnectWaitSec);
                string reconnecting = L10n.F("hud.disconnect.reconnecting", sec);
                DrawText("hud.disconnect.countdown", new Rect(cx - 100f * scale, spinY, 300f * scale, 40f * scale), reconnecting, s_modalHelperZh, new Color(0.867f, 0.918f, 0.898f));

                // Options: [0: 继续单人] [1: 回到标题]
                float optY = spinY + 70f * scale;
                DrawDisconnectOption(0, cx, optY, L10n.T("hud.disconnect.solo"), scale, sample || s_modalFocus == 0, sample);
                DrawDisconnectOption(1, cx, optY + 90f * scale, L10n.T("hud.disconnect.back"), scale, !sample && s_modalFocus == 1, sample);

                // Solo progress note
                float noteY = optY + 190f * scale;
                DrawText("hud.disconnect.note", new Rect(0, noteY, Screen.width, 30f * scale), L10n.T("hud.disconnect.note"), s_modalHelperZh, new Color(0.867f, 0.918f, 0.898f));

                // Prompts
                float promptY = Screen.height - 60f * scale;
                float rightX = Screen.width - 80f * scale;
                string promptText = L10n.F("hud.disconnect.prompt", "<color=#91C95B>(A)</color>", "<color=#D76659>(B)</color>");
                GUIContent pContent = new GUIContent(promptText);
                Vector2 pSz = s_promptStyle.CalcSize(pContent);
                DrawText("hud.disconnect.prompt", new Rect(rightX - pSz.x, promptY, pSz.x, pSz.y), promptText, s_promptStyle, new Color(0.867f, 0.918f, 0.898f));
            }
            finally { NativeGui.EndPage(); }
        }

        static void DrawDisconnectOption(int idx, float cx, float y, string text, float scale, bool selected, bool sample)
        {
            float w = 420f * scale;
            float h = 76f * scale;
            Rect r = new Rect(cx - w * 0.5f, y, w, h);

            if (!sample && r.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseMove)
                s_modalFocus = idx;

            Color textColor = selected ? new Color(0.945f, 0.988f, 1.0f) : new Color(0.867f, 0.918f, 0.898f);

            if (NativeGui.Button("hud.disconnect.option." + idx, r, text, s_modalItemZh.fontSize, selected, textColor) && !sample)
            {
                s_modalFocus = idx;
                ActivateModalOption(idx);
            }

            if (selected)
            {
                float streakW = Mathf.Clamp(340f * scale, 240f, 440f);
                NativeGui.Texture("hud.disconnect.option.glow." + idx,
                    new Rect(cx - streakW * 0.5f, y + 70f * scale, streakW, 12f * scale), s_glowStreak, Color.white);
            }
        }

        static void DrawSpinner(float cx, float cy, float scale)
        {
            float radius = 24f * scale;
            int count = 12;
            float baseAngle = Time.realtimeSinceStartup * 4f;

            for (int i = 0; i < count; i++)
            {
                float frac = (float)i / count;
                float angle = baseAngle + frac * Mathf.PI * 2f;
                float px = cx + Mathf.Cos(angle) * radius;
                float py = cy + Mathf.Sin(angle) * radius;

                float alpha = Mathf.Pow(frac, 1.8f);
                float dotSize = (3f + frac * 5f) * scale;

                Color c = new Color(0.39f, 0.87f, 0.96f, alpha);
                NativeGui.Texture("hud.disconnect.spinner." + i,
                    new Rect(px - dotSize * 0.5f, py - dotSize * 0.5f, dotSize, dotSize), s_dotTex, c);
            }
        }

        static void DrawText(string id, Rect r, string text, GUIStyle style, Color textColor)
        {
            NativeGui.Label(id, r, text, style.fontSize, style.alignment, textColor);
        }

        static void DrawRectOutline(string id, Rect r, Color c, float thickness)
        {
            NativeGui.Panel(id + ".top", new Rect(r.x, r.y, r.width, thickness), c);
            NativeGui.Panel(id + ".bottom", new Rect(r.x, r.yMax - thickness, r.width, thickness), c);
            NativeGui.Panel(id + ".left", new Rect(r.x, r.y + thickness, thickness, r.height - thickness * 2f), c);
            NativeGui.Panel(id + ".right", new Rect(r.xMax - thickness, r.y + thickness, thickness, r.height - thickness * 2f), c);
        }
    }
}
