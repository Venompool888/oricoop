using System;
using System.Collections.Generic;
using J2i.Net.XInputWrapper;
using UnityEngine;

namespace OriCoop
{
    // Online lobby overlay: opened from the native "Online" page (NativeModeMenu) after a transport is picked.
    // Pages (bilingual Chinese + English):
    //   Role     : 开房 Host / 加入 Join  (relay page also shows an editable relay server field)
    //   JoinInput: 6-char room code (relay) or IP[:port] (direct) with controller grid and keyboard input
    //   Hosting  : NetPlay.Begin(true, ...); shows RoomCode or local addresses; Start and Cancel buttons
    //   Joining  : NetPlay.Begin(false, ...); animated progress ring; auto-proceed on PartnerHere; friendly error on failure
    public static class OnlineLobby
    {
        public static bool IsOpen { get { return s_open; } }
        public static string Transport = "relay";

        static bool s_open;
        static GameObject s_overlayObject;

        public static void Open(string transport)
        {
            Transport = string.IsNullOrEmpty(transport) ? "relay" : transport.Trim().ToLowerInvariant();
            s_open = true;
            if (s_overlayObject != null)
            {
                UnityEngine.Object.Destroy(s_overlayObject);
                s_overlayObject = null;
            }
            s_overlayObject = new GameObject("OriCoop_OnlineLobbyOverlay");
            UnityEngine.Object.DontDestroyOnLoad(s_overlayObject);
            s_overlayObject.AddComponent<OnlineLobbyOverlay>();
            Plugin.Log.LogInfo("online lobby opened (" + Transport + ")");
        }

        public static void Close()
        {
            s_open = false;
            if (s_overlayObject != null)
            {
                UnityEngine.Object.Destroy(s_overlayObject);
                s_overlayObject = null;
            }
            Plugin.Log.LogInfo("online lobby closed");
        }
    }

    internal class OnlineLobbyOverlay : MonoBehaviour
    {
        const int PageRole = 0;
        const int PageJoinInput = 1;
        const int PageHosting = 2;
        const int PageJoining = 3;

        // The 8x4 key grid: new codes use 2-9 (T-046), older relay / room codes A-Z without I and O plus 2-9: exactly 32 keys.
        // (It held 0 and 1, which no code uses, and lacked Y and Z, so about a third of the old codes could not be typed: T-120.)
        internal const string RelayCodeAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        // XInput button masks
        const int A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000, START = 0x10, BACK = 0x20,
            DUP = 1, DDOWN = 2, DLEFT = 4, DRIGHT = 8;

        int m_page = PageRole;
        float m_ignoreInputUntil;

        // Role page state
        int m_roleFocus;
        bool m_editingRelayServer;
        string m_relayServerInput = "";

        // Join input state
        string m_roomCodeInput = "";
        string m_directAddressInput = "";
        string m_inputError = "";
        int m_gridRow;
        int m_gridCol;

        // Hosting state
        int m_hostFocus;
        float m_copyToastUntil;
        string m_copyToastMessage = "";

        // Joining state
        string m_lastJoinTarget = "";
        int m_failFocus;
        float m_connectedAt;

        // XInput polling
        XInputState m_xState = new XInputState();
        readonly bool[] m_padConnected = new bool[4];
        readonly int[] m_padButtons = new int[4];
        readonly int[] m_padPrevButtons = new int[4];
        float m_padScanTimer;

        // Procedural GUI textures
        static Texture2D s_vignetteTex;
        static Texture2D s_glowStreak;
        static Texture2D s_solidTex;
        static Texture2D s_dotTex;
        static Texture2D s_boxBgTex;

        // Cached GUIStyles
        static GUIStyle s_titleZh;
        static GUIStyle s_titleEn;
        static GUIStyle s_itemZh;
        static GUIStyle s_itemEn;
        static GUIStyle s_codeStyle;
        static GUIStyle s_helperZh;
        static GUIStyle s_helperEn;
        static GUIStyle s_promptStyle;
        static GUIStyle s_gridKeyStyle;
        static GUIStyle s_gridKeyFocusStyle;
        static GUIStyle s_boxStyle;

        void Awake()
        {
            InitTextures();
            GoTo(PageRole);
        }

        void OnDestroy()
        {
            // Textures stay cached across opens; hideFlags protect them
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
            s_boxBgTex = MakeSolidTex(new Color(0.035f, 0.09f, 0.13f, 0.85f));

            // Radial vignette texture
            s_vignetteTex = new Texture2D(128, 128, TextureFormat.ARGB32, false);
            s_vignetteTex.wrapMode = TextureWrapMode.Clamp;
            s_vignetteTex.filterMode = FilterMode.Bilinear;
            Color centerColor = new Color(0.047f, 0.161f, 0.208f, 0.82f); // Deep teal #0C2935
            Color edgeColor = new Color(0.027f, 0.075f, 0.122f, 0.96f);   // Navy #07131F
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(64f, 64f)) / 64f;
                    float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d));
                    s_vignetteTex.SetPixel(x, y, Color.Lerp(centerColor, edgeColor, t));
                }
            }
            s_vignetteTex.Apply();
            s_vignetteTex.hideFlags = HideFlags.HideAndDontSave;

            // Glowing cyan streak for selected items
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

            // Soft circular dot for progress ring and particles
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

        void GoTo(int page)
        {
            m_page = page;
            m_ignoreInputUntil = Time.unscaledTime + 0.3f;
            m_inputError = "";
            m_connectedAt = 0f;
            m_copyToastUntil = 0f;

            if (page == PageRole)
            {
                m_roleFocus = 0;
                m_editingRelayServer = false;
            }
            else if (page == PageJoinInput)
            {
                m_gridRow = 0;
                m_gridCol = 0;
                if (OnlineLobby.Transport == "relay")
                {
                    m_roomCodeInput = (NetPlay.ConfigRoomCode ?? "").Trim().ToUpperInvariant();
                    if (m_roomCodeInput.Length > 6) m_roomCodeInput = m_roomCodeInput.Substring(0, 6);
                }
                else if (OnlineLobby.Transport == "direct")
                {
                    m_directAddressInput = NetPlay.ConfigAddress ?? "";
                    if (NetPlay.ConfigPort > 0 && NetPlay.ConfigPort != 47011)
                        m_directAddressInput += ":" + NetPlay.ConfigPort;
                }
            }
            else if (page == PageHosting)
            {
                m_hostFocus = 0;
                string target = "";
                if (OnlineLobby.Transport == "relay" && !string.IsNullOrEmpty(NetPlay.ConfigRelayServer))
                    target = NetPlay.ConfigRelayServer.Trim() + "|";
                NetPlay.Begin(true, OnlineLobby.Transport, target);
            }
            else if (page == PageJoining)
            {
                m_failFocus = 0;
            }
        }

        void PollPads()
        {
            m_padScanTimer -= Time.unscaledDeltaTime;
            bool rescan = m_padScanTimer <= 0f;
            if (rescan) m_padScanTimer = 1f;

            for (int i = 0; i < 4; i++)
            {
                m_padPrevButtons[i] = m_padButtons[i];
                if (CoopMenu.TestPadButtons != null)
                {
                    m_padConnected[i] = true;
                    m_padButtons[i] = CoopMenu.TestPadButtons[i];
                    continue;
                }
                if (!m_padConnected[i] && !rescan)
                {
                    m_padButtons[i] = 0;
                    continue;
                }
                m_padConnected[i] = XInput.XInputGetState(i, ref m_xState) == 0;
                int b = 0;
                if (m_padConnected[i])
                {
                    XInputGamepad g = m_xState.Gamepad;
                    b = g.wButtons;
                    if (g.sThumbLY > 16000) b |= DUP;
                    if (g.sThumbLY < -16000) b |= DDOWN;
                    if (g.sThumbLX > 16000) b |= DRIGHT;
                    if (g.sThumbLX < -16000) b |= DLEFT;
                }
                m_padButtons[i] = b;
            }
        }

        bool AnyPadPressed(int mask)
        {
            for (int i = 0; i < 4; i++)
                if ((m_padButtons[i] & mask) != 0 && (m_padPrevButtons[i] & mask) == 0)
                    return true;
            return false;
        }

        void Update()
        {
            if (OnlineRoomScreen.Visible) return;
            if (m_page == PageHosting && !OnlineRoomScreen.Confirmed && (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected))
            { OnlineRoomScreen.Show(delegate { OnlineLobby.Close(); NativeModeMenu.OnlineProceed(); }); return; }
            PollPads();
            if (Time.unscaledTime < m_ignoreInputUntil) return;

            if (m_page == PageRole)
            {
                UpdateRoleInput();
            }
            else if (m_page == PageJoinInput)
            {
                UpdateJoinInput();
            }
            else if (m_page == PageHosting)
            {
                UpdateHostingInput();
            }
            else if (m_page == PageJoining)
            {
                UpdateJoiningInput();
            }
        }

        void UpdateRoleInput()
        {
            if (m_editingRelayServer)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B))
                {
                    m_editingRelayServer = false;
                    m_ignoreInputUntil = Time.unscaledTime + 0.2f;
                }
                return;
            }

            int rows = OnlineLobby.Transport == "relay" ? 3 : 2;
            if (Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP))
                m_roleFocus = (m_roleFocus + rows - 1) % rows;
            if (Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN))
                m_roleFocus = (m_roleFocus + 1) % rows;

            bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                Input.GetKeyDown(KeyCode.Space) || AnyPadPressed(A);
            bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);

            if (back)
            {
                OnlineLobby.Close();
                NativeModeMenu.OnlineBack();
                return;
            }

            if (confirm)
            {
                ActivateRole(m_roleFocus);
            }
        }

        void ActivateRole(int row)
        {
            if (row == 0) // Host
            {
                GoTo(PageHosting);
            }
            else if (row == 1) // Join
            {
                if (OnlineLobby.Transport == "steam")
                {
                    m_lastJoinTarget = "";
                    GoTo(PageJoining);
                    NetPlay.Begin(false, "steam", "");
                }
                else
                {
                    GoTo(PageJoinInput);
                }
            }
            else if (row == 2) // Relay server edit
            {
                m_editingRelayServer = true;
                m_relayServerInput = NetPlay.ConfigRelayServer ?? "";
                m_ignoreInputUntil = Time.unscaledTime + 0.2f;
            }
        }

        void SaveRelayServer()
        {
            string s = (m_relayServerInput ?? "").Trim();
            try
            {
                if (Plugin.Instance != null && Plugin.Instance.Config != null)
                {
                    Plugin.Instance.Config.Bind("Net", "RelayServer", "").Value = s;
                    Plugin.Instance.Config.Save();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not save relay server config: " + ex.Message);
            }
            m_editingRelayServer = false;
            m_ignoreInputUntil = Time.unscaledTime + 0.2f;
        }

        void UpdateJoinInput()
        {
            bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);
            if (back)
            {
                GoTo(PageRole);
                return;
            }

            if (OnlineLobby.Transport == "relay")
            {
                // 4x8 letters grid + row 4: [Delete] [Connect]
                if (Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP))
                    m_gridRow = (m_gridRow + 5 - 1) % 5;
                if (Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN))
                    m_gridRow = (m_gridRow + 1) % 5;
                if (Input.GetKeyDown(KeyCode.LeftArrow) || AnyPadPressed(DLEFT))
                {
                    if (m_gridRow == 4) m_gridCol = m_gridCol < 4 ? 4 : 0;
                    else m_gridCol = (m_gridCol + 8 - 1) % 8;
                }
                if (Input.GetKeyDown(KeyCode.RightArrow) || AnyPadPressed(DRIGHT))
                {
                    if (m_gridRow == 4) m_gridCol = m_gridCol < 4 ? 4 : 0;
                    else m_gridCol = (m_gridCol + 1) % 8;
                }

                if (AnyPadPressed(X) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    if (m_roomCodeInput.Length > 0)
                        m_roomCodeInput = m_roomCodeInput.Substring(0, m_roomCodeInput.Length - 1);
                }

                if (AnyPadPressed(A) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (m_gridRow < 4)
                    {
                        int index = m_gridRow * 8 + m_gridCol;
                        if (index >= 0 && index < RelayCodeAlphabet.Length && m_roomCodeInput.Length < 6)
                            m_roomCodeInput += RelayCodeAlphabet[index];
                    }
                    else
                    {
                        if (m_gridCol < 4) // Delete
                        {
                            if (m_roomCodeInput.Length > 0)
                                m_roomCodeInput = m_roomCodeInput.Substring(0, m_roomCodeInput.Length - 1);
                        }
                        else // Connect
                        {
                            TryConnectRelay();
                        }
                    }
                }
            }
            else // direct IP
            {
                // 2x6 keys (0-9 . :) + row 2: [Delete] [Connect]
                if (Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP))
                    m_gridRow = (m_gridRow + 3 - 1) % 3;
                if (Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN))
                    m_gridRow = (m_gridRow + 1) % 3;
                if (Input.GetKeyDown(KeyCode.LeftArrow) || AnyPadPressed(DLEFT))
                {
                    if (m_gridRow == 2) m_gridCol = m_gridCol < 3 ? 3 : 0;
                    else m_gridCol = (m_gridCol + 6 - 1) % 6;
                }
                if (Input.GetKeyDown(KeyCode.RightArrow) || AnyPadPressed(DRIGHT))
                {
                    if (m_gridRow == 2) m_gridCol = m_gridCol < 3 ? 3 : 0;
                    else m_gridCol = (m_gridCol + 1) % 6;
                }

                if (AnyPadPressed(X) || Input.GetKeyDown(KeyCode.Backspace))
                {
                    if (m_directAddressInput.Length > 0)
                        m_directAddressInput = m_directAddressInput.Substring(0, m_directAddressInput.Length - 1);
                }

                if (AnyPadPressed(A) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (m_gridRow < 2)
                    {
                        string directKeys = "1234567890.:";
                        int index = m_gridRow * 6 + m_gridCol;
                        if (index >= 0 && index < directKeys.Length)
                            m_directAddressInput += directKeys[index];
                    }
                    else
                    {
                        if (m_gridCol < 3) // Delete
                        {
                            if (m_directAddressInput.Length > 0)
                                m_directAddressInput = m_directAddressInput.Substring(0, m_directAddressInput.Length - 1);
                        }
                        else // Connect
                        {
                            TryConnectDirect();
                        }
                    }
                }
            }
        }

        void TryConnectRelay()
        {
            if (string.IsNullOrEmpty(NetPlay.ConfigRelayServer))
            {
                m_inputError = L10n.T("lobby.set_relay_first");
                return;
            }
            if (m_roomCodeInput.Length != 6)
            {
                m_inputError = L10n.T("lobby.enter_code");
                return;
            }
            m_inputError = "";
            m_lastJoinTarget = m_roomCodeInput.Trim().ToUpperInvariant();
            GoTo(PageJoining);
            NetPlay.Begin(false, "relay", m_lastJoinTarget);
        }

        void TryConnectDirect()
        {
            string addr = (m_directAddressInput ?? "").Trim();
            if (string.IsNullOrEmpty(addr))
            {
                m_inputError = L10n.T("lobby.enter_address");
                return;
            }
            m_inputError = "";
            m_lastJoinTarget = addr;
            GoTo(PageJoining);
            NetPlay.Begin(false, "direct", addr);
        }

        void UpdateHostingInput()
        {
            bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);
            if (back)
            {
                NetPlay.End("host cancelled");
                GoTo(PageRole);
                return;
            }

            int buttons = 3;
            if (Input.GetKeyDown(KeyCode.LeftArrow) || AnyPadPressed(DLEFT) || Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP))
                m_hostFocus = (m_hostFocus + buttons - 1) % buttons;
            if (Input.GetKeyDown(KeyCode.RightArrow) || AnyPadPressed(DRIGHT) || Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN))
                m_hostFocus = (m_hostFocus + 1) % buttons;

            bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                Input.GetKeyDown(KeyCode.Space) || AnyPadPressed(A);

            if (confirm)
            {
                ActivateHostButton(m_hostFocus);
            }
        }

        void ActivateHostButton(int idx)
        {
            if (idx == 0) // Copy code / Copy address / Invite
            {
                if (OnlineLobby.Transport == "relay")
                {
                    if (!string.IsNullOrEmpty(NetPlay.RoomCode))
                    {
                        GUIUtility.systemCopyBuffer = NetPlay.RoomCode;
                        m_copyToastMessage = L10n.T("lobby.code_copied");
                        m_copyToastUntil = Time.realtimeSinceStartup + 2f;
                    }
                }
                else if (OnlineLobby.Transport == "direct")
                {
                    string[] addrs = NetPlay.LocalAddresses();
                    string addr = addrs != null && addrs.Length > 0 ? addrs[0] : "127.0.0.1";
                    GUIUtility.systemCopyBuffer = addr + ":" + NetPlay.ConfigPort;
                    m_copyToastMessage = L10n.T("lobby.address_copied");
                    m_copyToastUntil = Time.realtimeSinceStartup + 2f;
                }
                else if (OnlineLobby.Transport == "steam")
                {
                    if (NetPlay.SteamReady)
                        NetPlay.OpenSteamInvite();
                }
            }
            else if (idx == 1) // Start game
            {
                if (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected)
                {
                    OnlineLobby.Close();
                    NativeModeMenu.OnlineProceed();
                }
            }
            else if (idx == 2) // Cancel
            {
                NetPlay.End("host cancelled");
                GoTo(PageRole);
            }
        }

        void UpdateJoiningInput()
        {
            bool isFailed = NetPlay.CurrentPhase == NetPlay.Phase.Retrying ||
                (NetPlay.CurrentPhase == NetPlay.Phase.Connecting && (Time.realtimeSinceStartup - NetPlay.StartedAt > 15f));

            if (NetPlay.PartnerHere)
            {
                if (m_connectedAt <= 0f) m_connectedAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - m_connectedAt >= 0.6f)
                {
                    OnlineLobby.Close();
                    NativeModeMenu.OnlineProceed();
                    return;
                }
            }

            if (isFailed)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow) || AnyPadPressed(DLEFT) || Input.GetKeyDown(KeyCode.UpArrow) || AnyPadPressed(DUP))
                    m_failFocus = (m_failFocus + 2 - 1) % 2;
                if (Input.GetKeyDown(KeyCode.RightArrow) || AnyPadPressed(DRIGHT) || Input.GetKeyDown(KeyCode.DownArrow) || AnyPadPressed(DDOWN))
                    m_failFocus = (m_failFocus + 1) % 2;

                bool confirm = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) ||
                    Input.GetKeyDown(KeyCode.Space) || AnyPadPressed(A);
                bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);

                if (back)
                {
                    NetPlay.End("join cancelled");
                    GoTo(OnlineLobby.Transport == "steam" ? PageRole : PageJoinInput);
                    return;
                }

                if (confirm)
                {
                    if (m_failFocus == 0) // Retry
                    {
                        m_connectedAt = 0f;
                        NetPlay.Begin(false, OnlineLobby.Transport, m_lastJoinTarget);
                    }
                    else // Back
                    {
                        NetPlay.End("join cancelled");
                        GoTo(OnlineLobby.Transport == "steam" ? PageRole : PageJoinInput);
                    }
                }
            }
            else
            {
                bool back = Input.GetKeyDown(KeyCode.Escape) || AnyPadPressed(B);
                if (back)
                {
                    NetPlay.End("join cancelled");
                    GoTo(OnlineLobby.Transport == "steam" ? PageRole : PageJoinInput);
                }
            }
        }

        void EnsureStyles(float scale)
        {
            if (s_titleZh != null) return;

            s_titleZh = new GUIStyle(GUI.skin.label);
            s_titleZh.font = GUI.skin.font;
            s_titleZh.alignment = TextAnchor.MiddleCenter;
            s_titleZh.fontSize = Mathf.RoundToInt(60f * scale);
            s_titleZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f); // Ivory #DDEAE5

            s_titleEn = new GUIStyle(s_titleZh);
            s_titleEn.fontSize = Mathf.RoundToInt(26f * scale);
            s_titleEn.normal.textColor = new Color(0.541f, 0.655f, 0.671f); // Secondary #8AA7AB

            s_itemZh = new GUIStyle(GUI.skin.label);
            s_itemZh.font = GUI.skin.font;
            s_itemZh.alignment = TextAnchor.MiddleCenter;
            s_itemZh.fontSize = Mathf.RoundToInt(44f * scale);
            s_itemZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_itemEn = new GUIStyle(s_itemZh);
            s_itemEn.fontSize = Mathf.RoundToInt(24f * scale);
            s_itemEn.normal.textColor = new Color(0.541f, 0.655f, 0.671f);

            s_codeStyle = new GUIStyle(GUI.skin.label);
            s_codeStyle.font = GUI.skin.font;
            s_codeStyle.alignment = TextAnchor.MiddleCenter;
            s_codeStyle.fontSize = Mathf.RoundToInt(110f * scale);
            s_codeStyle.normal.textColor = new Color(0.945f, 0.988f, 1.0f); // #F1FCFF

            s_helperZh = new GUIStyle(s_itemZh);
            s_helperZh.fontSize = Mathf.RoundToInt(24f * scale);
            s_helperZh.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_helperEn = new GUIStyle(s_itemEn);
            s_helperEn.fontSize = Mathf.RoundToInt(20f * scale);
            s_helperEn.normal.textColor = new Color(0.541f, 0.655f, 0.671f);

            s_promptStyle = new GUIStyle(GUI.skin.label);
            s_promptStyle.font = GUI.skin.font;
            s_promptStyle.alignment = TextAnchor.MiddleRight;
            s_promptStyle.fontSize = Mathf.RoundToInt(22f * scale);
            s_promptStyle.normal.textColor = new Color(0.867f, 0.918f, 0.898f);

            s_gridKeyStyle = new GUIStyle(GUI.skin.button);
            s_gridKeyStyle.font = GUI.skin.font;
            s_gridKeyStyle.alignment = TextAnchor.MiddleCenter;
            s_gridKeyStyle.fontSize = Mathf.RoundToInt(34f * scale);
            s_gridKeyStyle.normal.textColor = new Color(0.867f, 0.918f, 0.898f);
            s_gridKeyStyle.normal.background = s_boxBgTex;

            s_gridKeyFocusStyle = new GUIStyle(s_gridKeyStyle);
            s_gridKeyFocusStyle.normal.textColor = new Color(0.945f, 0.988f, 1.0f);

            s_boxStyle = new GUIStyle(GUI.skin.box);
            s_boxStyle.normal.background = s_boxBgTex;
        }

        void OnGUI()
        {
            try
            {
                float scale = Screen.height / 1080f;
                EnsureStyles(scale);

                // Full-screen backdrop & soft vignette
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), s_vignetteTex, ScaleMode.StretchToFill);

                // Handle keyboard typing when appropriate
                HandleTypingEvents();

                // Draw active page
                if (m_page == PageRole)
                {
                    DrawRolePage(scale);
                }
                else if (m_page == PageJoinInput)
                {
                    DrawJoinInputPage(scale);
                }
                else if (m_page == PageHosting)
                {
                    DrawHostingPage(scale);
                }
                else if (m_page == PageJoining)
                {
                    DrawJoiningPage(scale);
                }

                // Controller prompts bottom-right
                DrawControllerPrompts(scale);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("OnlineLobby OnGUI error: " + ex);
            }
        }

        void HandleTypingEvents()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            if (m_page == PageRole && m_editingRelayServer)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    SaveRelayServer();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    m_editingRelayServer = false;
                    e.Use();
                }
                else if (e.keyCode == KeyCode.Backspace)
                {
                    if (m_relayServerInput.Length > 0)
                        m_relayServerInput = m_relayServerInput.Substring(0, m_relayServerInput.Length - 1);
                    e.Use();
                }
                else if (e.character != '\0' && !char.IsControl(e.character))
                {
                    m_relayServerInput += e.character;
                    e.Use();
                }
            }
            else if (m_page == PageJoinInput)
            {
                if (OnlineLobby.Transport == "relay")
                {
                    if (e.keyCode == KeyCode.Backspace)
                    {
                        if (m_roomCodeInput.Length > 0)
                            m_roomCodeInput = m_roomCodeInput.Substring(0, m_roomCodeInput.Length - 1);
                        e.Use();
                    }
                    else if (e.character != '\0')
                    {
                        char c = char.ToUpperInvariant(e.character);
                        if (RelayCodeAlphabet.IndexOf(c) >= 0 && m_roomCodeInput.Length < 6)
                        {
                            m_roomCodeInput += c;
                            e.Use();
                        }
                    }
                }
                else if (OnlineLobby.Transport == "direct")
                {
                    if (e.keyCode == KeyCode.Backspace)
                    {
                        if (m_directAddressInput.Length > 0)
                            m_directAddressInput = m_directAddressInput.Substring(0, m_directAddressInput.Length - 1);
                        e.Use();
                    }
                    else if (e.character != '\0' && !char.IsControl(e.character))
                    {
                        m_directAddressInput += e.character;
                        e.Use();
                    }
                }
            }
        }

        void DrawRolePage(float scale)
        {
            float cx = Screen.width * 0.5f;
            float topY = 160f * scale;

            // Title
            string transport = OnlineLobby.Transport == "relay" ? L10n.T("lobby.server_room") : OnlineLobby.Transport == "direct" ? L10n.T("lobby.direct") : L10n.T("lobby.steam");

            DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), L10n.F("lobby.title", transport), s_titleZh, new Color(0.867f, 0.918f, 0.898f));

            if (m_editingRelayServer)
            {
                DrawRelayServerEdit(scale, cx, topY + 160f * scale);
                return;
            }

            // Options: Host, Join, (Relay Server)
            float startY = topY + 180f * scale;
            float rowHeight = 90f * scale;

            // Row 0: 开房 Host
            DrawMenuOption(0, cx, startY, L10n.T("lobby.host"), scale, m_roleFocus == 0);

            // Row 1: 加入 Join / 等待邀请 Wait for an invite
            string joinText = OnlineLobby.Transport == "steam" ? L10n.T("lobby.wait_invite") : L10n.T("rooms.join");
            DrawMenuOption(1, cx, startY + rowHeight, joinText, scale, m_roleFocus == 1);

            // Row 2 (Relay only): 中继服务器 Relay server
            if (OnlineLobby.Transport == "relay")
            {
                string srv = string.IsNullOrEmpty(NetPlay.ConfigRelayServer) ? L10n.T("lobby.not_set") : NetPlay.ConfigRelayServer;
                string serverText = L10n.F("lobby.relay_server", srv);
                DrawMenuOption(2, cx, startY + rowHeight * 2f, serverText, scale, m_roleFocus == 2);
            }
        }

        void DrawRelayServerEdit(float scale, float cx, float y)
        {
            DrawShadowedText(new Rect(0, y, Screen.width, 36f * scale), L10n.T("lobby.set_relay"), s_helperZh, new Color(0.867f, 0.918f, 0.898f));
            DrawShadowedText(new Rect(0, y + 36f * scale, Screen.width, 28f * scale), L10n.T("lobby.relay_example"), s_helperEn, new Color(0.541f, 0.655f, 0.671f));

            float boxW = 600f * scale;
            float boxH = 56f * scale;
            Rect boxR = new Rect(cx - boxW * 0.5f, y + 80f * scale, boxW, boxH);
            DrawRectOutline(boxR, new Color(0.39f, 0.87f, 0.96f, 0.8f), 2f * scale);
            GUI.Box(boxR, GUIContent.none, s_boxStyle);

            string disp = m_relayServerInput + (Mathf.Repeat(Time.realtimeSinceStartup * 2f, 2f) < 1f ? "|" : "");
            DrawShadowedText(boxR, disp, s_itemZh, Color.white);

            // Buttons: 保存 Save / 取消 Cancel
            float btnY = y + 160f * scale;
            Rect saveR = new Rect(cx - 220f * scale, btnY, 180f * scale, 50f * scale);
            Rect cancelR = new Rect(cx + 40f * scale, btnY, 180f * scale, 50f * scale);

            if (GUI.Button(saveR,
                L10n.T("lobby.save"), s_gridKeyStyle))
                SaveRelayServer();
            if (GUI.Button(cancelR,
                L10n.T("lobby.cancel"), s_gridKeyStyle))
            {
                m_editingRelayServer = false;
                m_ignoreInputUntil = Time.unscaledTime + 0.2f;
            }
        }

        void DrawMenuOption(int index, float cx, float y, string text, float scale, bool selected)
        {
            float w = 500f * scale;
            float h = 76f * scale;
            Rect r = new Rect(cx - w * 0.5f, y, w, h);

            // Mouse hover / click
            if (r.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseMove) m_roleFocus = index;
                if (Event.current.type == EventType.MouseDown)
                {
                    m_roleFocus = index;
                    ActivateRole(index);
                }
            }

            Color zhColor = selected ? new Color(0.945f, 0.988f, 1.0f) : new Color(0.867f, 0.918f, 0.898f);
            Color enColor = selected ? new Color(0.57f, 0.88f, 0.96f) : new Color(0.541f, 0.655f, 0.671f);

            DrawShadowedText(new Rect(r.x, r.y, r.width, 44f * scale), text, s_itemZh, zhColor);

            if (selected)
            {
                float streakW = Mathf.Clamp(360f * scale, 260f, 480f);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(cx - streakW * 0.5f, y + 74f * scale, streakW, 12f * scale), s_glowStreak);
            }
        }

        void DrawJoinInputPage(float scale)
        {
            float cx = Screen.width * 0.5f;
            float topY = 120f * scale;

            DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), L10n.T("lobby.join_room"), s_titleZh, new Color(0.867f, 0.918f, 0.898f));

            if (OnlineLobby.Transport == "relay")
            {
                // 6 Code slots
                float slotW = 100f * scale;
                float slotH = 90f * scale;
                float gap = 24f * scale;
                float totalW = 6f * slotW + 5f * gap;
                float startX = cx - totalW * 0.5f;
                float slotY = topY + 120f * scale;

                for (int i = 0; i < 6; i++)
                {
                    Rect slotR = new Rect(startX + i * (slotW + gap), slotY, slotW, slotH);
                    bool activeSlot = (i == m_roomCodeInput.Length);
                    Color borderColor = activeSlot ? new Color(0.39f, 0.87f, 0.96f, 0.95f) : new Color(0.2f, 0.35f, 0.42f, 0.6f);
                    DrawRectOutline(slotR, borderColor, (activeSlot ? 2.5f : 1.5f) * scale);
                    GUI.Box(slotR, GUIContent.none, s_boxStyle);

                    string ch = i < m_roomCodeInput.Length ? m_roomCodeInput.Substring(i, 1) : (activeSlot ? "_" : "");
                    if (ch.Length > 0)
                        DrawShadowedText(slotR, ch, s_itemZh, activeSlot ? new Color(0.95f, 0.99f, 1f) : new Color(0.867f, 0.918f, 0.898f));
                }

                // 4x8 Alphabet Grid
                float keyW = 76f * scale;
                float keyH = 58f * scale;
                float gridGapX = 14f * scale;
                float gridGapY = 12f * scale;
                float gridTotalW = 8f * keyW + 7f * gridGapX;
                float gridStartX = cx - gridTotalW * 0.5f;
                float gridStartY = slotY + slotH + 34f * scale;

                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 8; c++)
                    {
                        int idx = r * 8 + c;
                        char letter = RelayCodeAlphabet[idx];
                        Rect keyR = new Rect(gridStartX + c * (keyW + gridGapX), gridStartY + r * (keyH + gridGapY), keyW, keyH);
                        bool focused = (m_gridRow == r && m_gridCol == c);

                        if (focused)
                        {
                            DrawRectOutline(keyR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                            if (GUI.Button(keyR, letter.ToString(), s_gridKeyFocusStyle))
                            {
                                m_gridRow = r; m_gridCol = c;
                                if (m_roomCodeInput.Length < 6) m_roomCodeInput += letter;
                            }
                        }
                        else
                        {
                            if (GUI.Button(keyR, letter.ToString(), s_gridKeyStyle))
                            {
                                m_gridRow = r; m_gridCol = c;
                                if (m_roomCodeInput.Length < 6) m_roomCodeInput += letter;
                            }
                        }
                    }
                }

                // Bottom row: [Delete] and [Connect]
                float actionY = gridStartY + 4f * (keyH + gridGapY) + 14f * scale;
                Rect delR = new Rect(cx - 300f * scale, actionY, 260f * scale, 56f * scale);
                Rect connR = new Rect(cx + 40f * scale, actionY, 260f * scale, 56f * scale);

                bool delFocus = (m_gridRow == 4 && m_gridCol < 4);
                bool connFocus = (m_gridRow == 4 && m_gridCol >= 4);

                if (delFocus)
                {
                    DrawRectOutline(delR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                    if (GUI.Button(delR,
                        L10n.T("lobby.delete"), s_gridKeyFocusStyle))
                    {
                        m_gridRow = 4; m_gridCol = 0;
                        if (m_roomCodeInput.Length > 0)
                            m_roomCodeInput = m_roomCodeInput.Substring(0, m_roomCodeInput.Length - 1);
                    }
                }
                else
                {
                    if (GUI.Button(delR,
                        L10n.T("lobby.delete"), s_gridKeyStyle))
                    {
                        m_gridRow = 4; m_gridCol = 0;
                        if (m_roomCodeInput.Length > 0)
                            m_roomCodeInput = m_roomCodeInput.Substring(0, m_roomCodeInput.Length - 1);
                    }
                }

                if (connFocus)
                {
                    DrawRectOutline(connR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                    if (GUI.Button(connR,
                        L10n.T("lobby.connect"), s_gridKeyFocusStyle))
                    {
                        m_gridRow = 4; m_gridCol = 4;
                        TryConnectRelay();
                    }
                }
                else
                {
                    if (GUI.Button(connR,
                        L10n.T("lobby.connect"), s_gridKeyStyle))
                    {
                        m_gridRow = 4; m_gridCol = 4;
                        TryConnectRelay();
                    }
                }
            }
            else // direct
            {
                float boxW = 660f * scale;
                float boxH = 64f * scale;
                Rect boxR = new Rect(cx - boxW * 0.5f, topY + 120f * scale, boxW, boxH);
                DrawRectOutline(boxR, new Color(0.39f, 0.87f, 0.96f, 0.8f), 2f * scale);
                GUI.Box(boxR, GUIContent.none, s_boxStyle);

                string disp = m_directAddressInput + (Mathf.Repeat(Time.realtimeSinceStartup * 2f, 2f) < 1f ? "|" : "");
                DrawShadowedText(boxR, disp, s_itemZh, Color.white);

                // Grid for 0-9 . :
                string directKeys = "1234567890.:";
                float keyW = 80f * scale;
                float keyH = 58f * scale;
                float gapX = 16f * scale;
                float gapY = 14f * scale;
                float gridW = 6f * keyW + 5f * gapX;
                float startX = cx - gridW * 0.5f;
                float startY = boxR.yMax + 30f * scale;

                for (int r = 0; r < 2; r++)
                {
                    for (int c = 0; c < 6; c++)
                    {
                        int idx = r * 6 + c;
                        char k = directKeys[idx];
                        Rect keyR = new Rect(startX + c * (keyW + gapX), startY + r * (keyH + gapY), keyW, keyH);
                        bool focused = (m_gridRow == r && m_gridCol == c);

                        if (focused)
                        {
                            DrawRectOutline(keyR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                            if (GUI.Button(keyR, k.ToString(), s_gridKeyFocusStyle))
                            {
                                m_gridRow = r; m_gridCol = c;
                                m_directAddressInput += k;
                            }
                        }
                        else
                        {
                            if (GUI.Button(keyR, k.ToString(), s_gridKeyStyle))
                            {
                                m_gridRow = r; m_gridCol = c;
                                m_directAddressInput += k;
                            }
                        }
                    }
                }

                // Row 2: Delete and Connect
                float actionY = startY + 2f * (keyH + gapY) + 16f * scale;
                Rect delR = new Rect(cx - 300f * scale, actionY, 260f * scale, 56f * scale);
                Rect connR = new Rect(cx + 40f * scale, actionY, 260f * scale, 56f * scale);

                bool delFocus = (m_gridRow == 2 && m_gridCol < 3);
                bool connFocus = (m_gridRow == 2 && m_gridCol >= 3);

                if (delFocus)
                {
                    DrawRectOutline(delR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                    if (GUI.Button(delR,
                        L10n.T("lobby.delete"), s_gridKeyFocusStyle))
                    {
                        m_gridRow = 2; m_gridCol = 0;
                        if (m_directAddressInput.Length > 0)
                            m_directAddressInput = m_directAddressInput.Substring(0, m_directAddressInput.Length - 1);
                    }
                }
                else
                {
                    if (GUI.Button(delR,
                        L10n.T("lobby.delete"), s_gridKeyStyle))
                    {
                        m_gridRow = 2; m_gridCol = 0;
                        if (m_directAddressInput.Length > 0)
                            m_directAddressInput = m_directAddressInput.Substring(0, m_directAddressInput.Length - 1);
                    }
                }

                if (connFocus)
                {
                    DrawRectOutline(connR, new Color(0.39f, 0.87f, 0.96f, 0.9f), 2f * scale);
                    if (GUI.Button(connR,
                        L10n.T("lobby.connect"), s_gridKeyFocusStyle))
                    {
                        m_gridRow = 2; m_gridCol = 3;
                        TryConnectDirect();
                    }
                }
                else
                {
                    if (GUI.Button(connR,
                        L10n.T("lobby.connect"), s_gridKeyStyle))
                    {
                        m_gridRow = 2; m_gridCol = 3;
                        TryConnectDirect();
                    }
                }
            }

            // Inline error if any
            if (!string.IsNullOrEmpty(m_inputError))
            {
                float errY = Screen.height - 120f * scale;
                DrawShadowedText(new Rect(0, errY, Screen.width, 36f * scale), m_inputError, s_helperZh, new Color(0.95f, 0.65f, 0.55f));
            }
        }

        void DrawHostingPage(float scale)
        {
            float cx = Screen.width * 0.5f;
            float topY = 140f * scale;

            DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), L10n.T("lobby.waiting_partner"), s_titleZh, new Color(0.867f, 0.918f, 0.898f));

            float contentY = topY + 130f * scale;

            if (OnlineLobby.Transport == "relay")
            {
                string code = NetPlay.RoomCode;
                if (!string.IsNullOrEmpty(code))
                {
                    string spaced = code.Length >= 6 ? (code.Substring(0, 3) + " " + code.Substring(3)) : code;
                    DrawShadowedText(new Rect(0, contentY, Screen.width, 130f * scale), spaced, s_codeStyle, new Color(0.945f, 0.988f, 1.0f));
                    DrawShadowedText(new Rect(0, contentY + 125f * scale, Screen.width, 34f * scale), L10n.T("lobby.share_code"), s_helperZh, new Color(0.867f, 0.918f, 0.898f));
                }
                else
                {
                    DrawProgressRing(cx, contentY + 80f * scale, scale);
                    DrawShadowedText(new Rect(0, contentY + 160f * scale, Screen.width, 34f * scale), L10n.T("lobby.creating"), s_helperZh, new Color(0.867f, 0.918f, 0.898f));
                }
            }
            else if (OnlineLobby.Transport == "direct")
            {
                DrawShadowedText(new Rect(0, contentY, Screen.width, 36f * scale), L10n.T("lobby.direct_mode"), s_helperZh, new Color(0.57f, 0.88f, 0.96f));
                DrawShadowedText(new Rect(0, contentY + 36f * scale, Screen.width, 32f * scale), L10n.T("lobby.your_address"), s_helperZh, new Color(0.867f, 0.918f, 0.898f));

                string[] addrs = NetPlay.LocalAddresses();
                string addrText = "";
                if (addrs != null && addrs.Length > 0)
                {
                    for (int i = 0; i < addrs.Length; i++)
                    {
                        if (i > 0) addrText += "  |  ";
                        addrText += addrs[i] + ":" + NetPlay.ConfigPort;
                    }
                }
                else
                {
                    addrText = "127.0.0.1:" + NetPlay.ConfigPort;
                }

                DrawShadowedText(new Rect(0, contentY + 76f * scale, Screen.width, 50f * scale), addrText, s_itemZh, new Color(0.945f, 0.988f, 1.0f));
                DrawShadowedText(new Rect(0, contentY + 134f * scale, Screen.width, 30f * scale), L10n.T("lobby.direct_hint"), s_helperEn, new Color(0.541f, 0.655f, 0.671f));
            }
            else if (OnlineLobby.Transport == "steam")
            {
                DrawShadowedText(new Rect(0, contentY, Screen.width, 36f * scale), L10n.T("lobby.steam_friends"), s_helperZh, new Color(0.57f, 0.88f, 0.96f));
                string steamStatus = NetPlay.SteamReady ? L10n.T("lobby.steam_ready")
                    : L10n.T("lobby.steam_preparing");
                DrawShadowedText(new Rect(0, contentY + 44f * scale, Screen.width, 32f * scale), steamStatus, s_helperEn, new Color(0.867f, 0.918f, 0.898f));
            }

            // Partner status line
            if (NetPlay.PartnerHere)
            {
                string partnerName = !string.IsNullOrEmpty(NetPlay.SteamPartnerName) ? NetPlay.SteamPartnerName : L10n.T("roles.partner");
                DrawShadowedText(new Rect(0, contentY + 196f * scale, Screen.width, 36f * scale), L10n.F("lobby.partner_joined", partnerName), s_helperZh, new Color(0.57f, 0.95f, 0.65f));
            }
            else if (!string.IsNullOrEmpty(NetPlay.Problem))
            {
                string problem = GetFriendlyError(NetPlay.Problem, false);
                DrawShadowedText(new Rect(0, contentY + 196f * scale, Screen.width, 36f * scale), problem, s_helperZh, new Color(0.95f, 0.65f, 0.55f));
            }

            // Copy toast message
            if (Time.realtimeSinceStartup < m_copyToastUntil && !string.IsNullOrEmpty(m_copyToastMessage))
            {
                DrawShadowedText(new Rect(0, contentY + 230f * scale, Screen.width, 32f * scale), m_copyToastMessage, s_helperZh, new Color(0.39f, 0.87f, 0.96f));
            }

            // Action buttons: [0: Copy/Invite] [1: Start] [2: Cancel]
            float btnY = Screen.height - 230f * scale;
            float btnSpacing = 340f * scale;

            string buttonText = OnlineLobby.Transport == "steam" ? L10n.T("lobby.invite") : L10n.T("lobby.copy");
            DrawHostActionButton(0, cx - btnSpacing, btnY, buttonText, scale, m_hostFocus == 0, true, "");

            bool canStart = (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected);
            DrawHostActionButton(1, cx, btnY, L10n.T("lobby.start"), scale, m_hostFocus == 1, canStart, L10n.T("lobby.join_anytime"));

            DrawHostActionButton(2, cx + btnSpacing, btnY, L10n.T("lobby.cancel"), scale, m_hostFocus == 2, true, "");
        }

        void DrawHostActionButton(int idx, float cx, float y, string text, float scale, bool selected, bool enabled, string note)
        {
            float w = 280f * scale;
            float h = 76f * scale;
            Rect r = new Rect(cx - w * 0.5f, y, w, h);

            if (r.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseMove) m_hostFocus = idx;
                if (Event.current.type == EventType.MouseDown && enabled)
                {
                    m_hostFocus = idx;
                    ActivateHostButton(idx);
                }
            }

            Color zhColor = !enabled ? new Color(0.4f, 0.48f, 0.5f) : (selected ? new Color(0.945f, 0.988f, 1.0f) : new Color(0.867f, 0.918f, 0.898f));
            Color enColor = !enabled ? new Color(0.35f, 0.4f, 0.42f) : (selected ? new Color(0.57f, 0.88f, 0.96f) : new Color(0.541f, 0.655f, 0.671f));

            DrawShadowedText(new Rect(r.x, r.y, r.width, 42f * scale), text, s_itemZh, zhColor);

            if (selected && enabled)
            {
                float streakW = Mathf.Clamp(260f * scale, 180f, 360f);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(cx - streakW * 0.5f, y + 70f * scale, streakW, 10f * scale), s_glowStreak);
            }

            if (!string.IsNullOrEmpty(note))
            {
                DrawShadowedText(new Rect(cx - 300f * scale, y + 84f * scale, 600f * scale, 24f * scale), note, s_helperEn, new Color(0.541f, 0.655f, 0.671f));
            }
        }

        void DrawJoiningPage(float scale)
        {
            float cx = Screen.width * 0.5f;
            float topY = 160f * scale;

            bool isFailed = NetPlay.CurrentPhase == NetPlay.Phase.Retrying ||
                (NetPlay.CurrentPhase == NetPlay.Phase.Connecting && (Time.realtimeSinceStartup - NetPlay.StartedAt > 15f));

            if (isFailed)
            {
                string friendly = GetFriendlyError(NetPlay.Problem, Time.realtimeSinceStartup - NetPlay.StartedAt > 15f);

                DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), friendly, s_titleZh, new Color(0.95f, 0.65f, 0.55f));


                // Buttons: 重试 Retry / 返回 Back
                float btnY = topY + 220f * scale;
                DrawJoinFailButton(0, cx, btnY, L10n.T("lobby.retry"), scale, m_failFocus == 0);
                DrawJoinFailButton(1, cx, btnY + 90f * scale, L10n.T("common.back"), scale, m_failFocus == 1);
            }
            else
            {
                if (NetPlay.PartnerHere)
                {
                    DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), L10n.T("coop.connected"), s_titleZh, new Color(0.57f, 0.95f, 0.65f));
                    DrawProgressRing(cx, topY + 180f * scale, scale, true);
                }
                else
                {
                    DrawShadowedText(new Rect(0, topY, Screen.width, 68f * scale), L10n.T("lobby.connecting"), s_titleZh, new Color(0.867f, 0.918f, 0.898f));
                    DrawProgressRing(cx, topY + 180f * scale, scale, false);

                    // Stage text
                    string stage;
                    if (OnlineLobby.Transport == "steam")
                    {
                        stage = L10n.T("lobby.accept_invite");
                    }
                    else if (NetPlay.CurrentPhase == NetPlay.Phase.Connecting)
                    {
                        if (OnlineLobby.Transport == "relay")
                        {
                            stage = L10n.T("lobby.opening_path");
                        }
                        else
                        {
                            stage = L10n.T("lobby.connecting_stage");
                        }
                    }
                    else
                    {
                        stage = L10n.T("lobby.preparing");
                    }

                    DrawShadowedText(new Rect(0, topY + 280f * scale, Screen.width, 36f * scale), stage, s_helperZh, new Color(0.867f, 0.918f, 0.898f));

                    // Cancel button
                    float cancelY = Screen.height - 200f * scale;
                    DrawJoinFailButton(2, cx, cancelY, L10n.T("lobby.cancel"), scale, true);
                }
            }
        }

        void DrawJoinFailButton(int idx, float cx, float y, string text, float scale, bool selected)
        {
            float w = 360f * scale;
            float h = 76f * scale;
            Rect r = new Rect(cx - w * 0.5f, y, w, h);

            if (r.Contains(Event.current.mousePosition))
            {
                if (Event.current.type == EventType.MouseMove) m_failFocus = idx;
                if (Event.current.type == EventType.MouseDown)
                {
                    if (idx == 0) // Retry
                    {
                        m_connectedAt = 0f;
                        NetPlay.Begin(false, OnlineLobby.Transport, m_lastJoinTarget);
                    }
                    else // Back or Cancel
                    {
                        NetPlay.End("join cancelled");
                        GoTo(OnlineLobby.Transport == "steam" ? PageRole : PageJoinInput);
                    }
                }
            }

            Color zhColor = selected ? new Color(0.945f, 0.988f, 1.0f) : new Color(0.867f, 0.918f, 0.898f);
            Color enColor = selected ? new Color(0.57f, 0.88f, 0.96f) : new Color(0.541f, 0.655f, 0.671f);

            DrawShadowedText(new Rect(r.x, r.y, r.width, 42f * scale), text, s_itemZh, zhColor);

            if (selected)
            {
                float streakW = Mathf.Clamp(280f * scale, 200f, 400f);
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(cx - streakW * 0.5f, y + 70f * scale, streakW, 10f * scale), s_glowStreak);
            }
        }

        void DrawProgressRing(float cx, float cy, float scale, bool completed = false)
        {
            float radius = 70f * scale;
            int count = 18;
            float baseAngle = completed ? 0f : (Time.realtimeSinceStartup * 3.5f);

            for (int i = 0; i < count; i++)
            {
                float frac = (float)i / count;
                float angle = baseAngle + frac * Mathf.PI * 2f;
                float px = cx + Mathf.Cos(angle) * radius;
                float py = cy + Mathf.Sin(angle) * radius;

                float alpha = completed ? 0.95f : Mathf.Pow(frac, 1.6f);
                float dotSize = (completed ? 8f : (5f + frac * 7f)) * scale;

                Color c = completed ? new Color(0.57f, 0.95f, 0.65f, alpha) : new Color(0.39f, 0.87f, 0.96f, alpha);
                GUI.color = c;
                GUI.DrawTexture(new Rect(px - dotSize * 0.5f, py - dotSize * 0.5f, dotSize, dotSize), s_dotTex);
            }
            GUI.color = Color.white;
        }

        void DrawControllerPrompts(float scale)
        {
            float y = Screen.height - 60f * scale;
            float rightX = Screen.width - 80f * scale;

            string text = L10n.T("lobby.prompts");
            GUIContent content = new GUIContent(text);
            Vector2 sz = s_promptStyle.CalcSize(content);
            Rect r = new Rect(rightX - sz.x, y, sz.x, sz.y);

            DrawShadowedText(r, text, s_promptStyle, new Color(0.867f, 0.918f, 0.898f));
        }

        static string GetFriendlyError(string raw, bool timedOut)
        {
            string lower = (raw ?? "").ToLowerInvariant();
            if (timedOut || lower.Contains("timeout") || lower.Contains("timed out"))
            {
                return L10n.T("lobby.err.timeout");
            }
            else if (lower.Contains("not found") || lower.Contains("no such room"))
            {
                return L10n.T("lobby.err.no_room");
            }
            else if (lower.Contains("full"))
            {
                return L10n.T("lobby.err.full");
            }
            else if (lower.Contains("resolve") || lower.Contains("host is known"))
            {
                return L10n.T("lobby.err.no_address");
            }
            else if (lower.Contains("steam"))
            {
                return L10n.T("lobby.err.steam");
            }
            else
            {
                return L10n.T("lobby.err.failed");
            }
        }

        static void DrawShadowedText(Rect r, string text, GUIStyle style, Color textColor)
        {
            style.clipping = TextClipping.Overflow;   // descenders (g, p, y) must not be cut by tight rects
            Color prev = style.normal.textColor;
            style.normal.textColor = new Color(0.01f, 0.03f, 0.05f, 0.75f);
            GUI.Label(new Rect(r.x, r.y + 2f, r.width, r.height), text, style);
            style.normal.textColor = textColor;
            GUI.Label(r, text, style);
            style.normal.textColor = prev;
        }

        static void DrawRectOutline(Rect r, Color c, float thickness)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, thickness), s_solidTex);
            GUI.DrawTexture(new Rect(r.x, r.yMax - thickness, r.width, thickness), s_solidTex);
            GUI.DrawTexture(new Rect(r.x, r.y + thickness, thickness, r.height - thickness * 2f), s_solidTex);
            GUI.DrawTexture(new Rect(r.xMax - thickness, r.y + thickness, thickness, r.height - thickness * 2f), s_solidTex);
            GUI.color = prev;
        }
    }
}
