using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OriCoop
{
    // The online lobby as pages of the native "How do you want to play?" orb (same cloned difficulty screen, so the
    // game's font, glow, sounds and controller / keyboard / mouse navigation): after Online -> transport:
    //   Role    : Host / Join / (relay) Relay server
    //   Entry   : room code, IP[:port] or relay server, edited in place (arcade style: A to edit, up/down change the
    //             character, left/right move, X deletes, Y or Ctrl+V pastes, keyboard typing works) / Connect / Back
    //   Hosting : room code or local address (A copies; Steam: invite) / Start game / Cancel
    //   Joining : Connecting... / Cancel; on failure Retry / Back; proceeds by itself once the partner is there
    // NetPlay is driven exactly like OnlineLobby (the OnGUI overlay, still used when [Start] NativeLobby = false or
    // ORICOOP_LOBBY_PROBE=1). Descriptions go to the shared tooltip box under the orb.
    public static partial class NativeModeMenu
    {
        const int PageRole = 2, PageEntry = 3, PageHost = 4, PageJoin = 5, PageCoop = 6, PageServerMode = 7;
        const int PageServerList = 8, PageServerAdd = 9, PageServerRemove = 10;
        const int PageRooms = 11, PageRoomView = 12, PageRoomCreate = 13, PageRoomJoin = 14, PageRoomPassword = 15, PageRoomSlots = 16;
        static readonly string[] PageNames = { "main", "online", "role", "entry", "hosting", "joining", "coop", "servermode", "serverlist", "serveradd", "serverremove", "rooms", "roomview", "roomcreate", "roomjoin", "roompassword", "roomslots" };
        const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // legacy letter codes (text entry); new codes are digits 2-9 typed on the dial (T-046)
        const string IpAlphabet = "0123456789.:";
        const string ServerAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789.-:";
        const string CServerHostAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789.-";
        const string CServerPortAlphabet = "0123456789";

        static BepInEx.Configuration.ConfigEntry<bool> s_nativeLobby;
        static string s_transport = "relay";
        static int s_entryKind;             // 0 room code, 1 IP, 2 relay server, 3 C-server host, 4 C-server port, 5 room password
        static string s_entry = "";
        static string s_cserverHost = "", s_cserverPort = "";
        static int s_cursor;
        static bool s_editing;
        static float s_editStartedAt;
        static string s_joinTarget = "";
        static string s_cserverBeginTarget = "";
        static float s_connectedAt;
        static string s_toastKey = "";
        static float s_toastUntil;
        static float s_nextRefresh;
        static string s_cursorStyle;        // a highlight text style of the menu font, if it has one
        static string[] s_labelCache = new string[MaxItems];
        static string[] s_descCache = new string[MaxItems];

        // Direct join (s_entryKind 1): the orb has 3 rows, so they are IP / Port / Connect (B or Esc goes back).
        // The IP is 4 octet slots, each checked as you type (0-255, no leading zero); the port is checked 1-65535.
        const int DefaultPort = 47011;
        static readonly string[] s_ip = { "", "", "", "" };
        static int s_ipOct, s_ipPos;        // cursor: octet 0-3, digit 0-2 (== length means the empty slot after it)
        static bool s_ipAdvanced;           // the last digit filled an octet and moved on: swallow a following '.'
        static string s_port = "";
        static int s_portPos;
        // T-046: the IP, the port and the room code are edited with combination-lock dials (Ui/DigitDial). The dial is the
        // editor; s_ip / s_port / s_entry stay the values Connect reads, written back on every change.
        static OriCoop.Ui.DigitDial s_dial;
        static string s_dialBefore = "";
        static bool s_legacyCode;   // the room code being typed is an older letter code: plain text entry instead of the dial
        static bool DialMode { get { return IpMode || s_entryKind == 5 || (s_entryKind == 0 && !s_legacyCode); } }
        static bool HasLegacyLetter(string s) { foreach (char c in s ?? "") if (char.ToUpperInvariant(c) >= 'A' && char.ToUpperInvariant(c) <= 'Z') return true; return false; }
        static int s_editField;             // direct join: 0 = IP row, 1 = port row
        static bool IpMode { get { return s_entryKind == 1; } }

        static bool UseNativeLobby
        {
            get
            {
                if (s_nativeLobby == null && Plugin.Instance != null)
                    s_nativeLobby = Plugin.Instance.Config.Bind("Start", "NativeLobby", true,
                        "Online host / join pages in the game's own menu style. false = the overlay lobby (OnlineLobby).");
                if (LobbyProbeOn) return false;          // the overlay's own probe drives OnlineLobby
                return s_nativeLobby == null || s_nativeLobby.Value;
            }
        }

        // A Steam invite accepted outside the Join page (SteamInviteHook): show the joining page if the menu is
        // open. The page then proceeds by itself once the host is there (LobbyTick).
        public static void ShowSteamJoinPage()
        {
            if (!UseNativeLobby) return;
            if (!IsOpen) OpenTitleOrbForInvite();          // invite accepted on the title: the join page there
            if (!IsOpen || s_mgr == null) return;
            s_transport = "steam";
            s_joinTarget = "";
            GoPage(PageJoin, 1);
        }

        public static bool OnSteamJoinPage { get { return IsOpen && UseNativeLobby && s_page == PageJoin && s_transport == "steam"; } }

        public static bool LobbyEditing { get { return s_editing; } }
        public static string LobbyEntry { get { return s_entry; } }

        static bool IsLobbyPage { get { return (s_page >= PageRole && s_page <= PageJoin) || s_page >= PageServerList; } }
        static bool UsesRoomCode { get { return s_transport == "relay" || s_transport == "cserver"; } }
        static string LobbyServer { get { return s_transport == "cserver" ? OriCoop.Authority.CServerConfig.Address : (NetPlay.ConfigRelayServer ?? "").Trim(); } }
        static bool CServerEntry { get { return s_entryKind == 3 || s_entryKind == 4; } }

        // ---- rows -----------------------------------------------------------------------------------------

        static string[] Row(string labelKey, string descKey)
        {
            return new[] { L10n.T(labelKey, true), L10n.T(descKey, true), L10n.T(labelKey, false), L10n.T(descKey, false) };
        }

        static string[][] CServerRoleRows(bool zh)
        {
            string help = L10n.T("cserver.edit.desc", zh);
            return new[] {
                new[] { L10n.T("cserver.addr", zh) + ": [" + (s_cserverHost.Length == 0 ? "_" : s_cserverHost) + "]", help },
                new[] { L10n.T("cserver.port", zh) + ": " + (s_cserverPort.Length == 0 ? "_" : s_cserverPort), help }
            };
        }

        static string[][] LobbyRows(bool zh)
        {
            if (IsRoomsPage) return RoomRows(zh);
            bool relay = UsesRoomCode, direct = s_transport == "direct";
            var r = new List<string[]>();
            if (s_page == PageServerList || s_page == PageServerRemove)
            {
                if (s_page == PageServerList)
                {
                    r.Add(new[] { L10n.T("server.add", zh), L10n.T("server.add.desc", zh) });
                    r.Add(new[] { L10n.T("server.remove", zh), L10n.T("server.remove.desc", zh) });
                }
                foreach (string server in OriCoop.Authority.CServerConfig.Servers)
                    r.Add(new[] { server, L10n.T(s_page == PageServerRemove ? "server.remove.pick" : "server.pick", zh) });
                if (r.Count == 0) r.Add(new[] { L10n.T("common.back", zh), L10n.T("common.back.desc", zh) });
            }
            else if (s_page == PageServerAdd)
            {
                r.AddRange(CServerRoleRows(zh));
                r.Add(new[] { L10n.T("common.save", zh), L10n.T("server.add.desc", zh) });
            }
            else if (s_page == PageRole)
            {
                r.Add(new[] { L10n.T("lobby.host", zh), L10n.T(relay ? "lobby.host.room.desc" : direct ? "lobby.host.direct.desc" : "lobby.host.steam.desc", zh) });
                r.Add(new[] { L10n.T("lobby.join", zh), L10n.T(relay ? "lobby.join.room.desc" : direct ? "lobby.join.direct.desc" : "lobby.join.steam.desc", zh) });
                if (s_transport == "relay")
                {
                    r.Add(new[] { L10n.T("lobby.rooms", zh), L10n.T("lobby.rooms.desc", zh) });
                    string srv = (NetPlay.ConfigRelayServer ?? "").Trim();
                    r.Add(new[] { L10n.T("lobby.relay.server", zh), srv.Length == 0 ? L10n.T("lobby.server.unset", zh) : string.Format(L10n.T("lobby.server.current", zh), srv) });
                }
            }
            else if (s_page == PageEntry && IpMode)
            {
                bool ipEdit = s_editing && s_editField == 0, portEdit = s_editing && s_editField == 1;
                r.Add(new[] { IpDisplay(zh), L10n.T(ipEdit ? "lobby.dial.help" : "lobby.ip.desc", zh) });
                r.Add(new[] { PortDisplay(zh), portEdit ? L10n.T("lobby.dial.help", zh) : string.Format(L10n.T("lobby.port.desc", zh), DefaultPort) });
                r.Add(new[] { L10n.T("lobby.connect", zh), L10n.T("lobby.connect.back.desc", zh) + SaveNote(zh) });
            }
            else if (s_page == PageEntry)
            {
                string titleKey = s_entryKind == 0 ? "lobby.room.code" : s_entryKind == 1 ? "lobby.ip" : s_entryKind == 3 ? "cserver.addr" : s_entryKind == 4 ? "cserver.port" : "lobby.relay.address";
                string help = s_editing ? L10n.T(CServerEntry ? "cserver.typing.desc" : s_entryKind == 0 ? "lobby.dial.help" : "lobby.edit.chars", zh) : string.Format(L10n.T("lobby.entry.desc", zh), L10n.T(titleKey, zh));
                if (s_entryKind == 0 && !s_editing && !s_legacyCode) help += "\n" + L10n.T("lobby.room.legacy", zh);   // T-046: older letter codes still work
                r.Add(new[] { EntryDisplay(zh), help });
                if (s_entryKind == 2 || CServerEntry) r.Add(new[] { L10n.T("common.save", zh), L10n.T(CServerEntry ? "cserver.save.desc" : "lobby.server.save.desc", zh) });
                else r.Add(new[] { L10n.T("lobby.connect", zh), L10n.T("lobby.connect.desc", zh) + SaveNote(zh) });
                r.Add(new[] { L10n.T("common.back", zh), L10n.T("common.back.desc", zh) });
            }
            else if (s_page == PageHost)
            {
                if (relay)
                {
                    string code = NetPlay.RoomCode ?? "";
                    if (code.Length == 0) r.Add(new[] { L10n.T("lobby.creating", zh), L10n.T("lobby.creating.desc", zh) });
                    else
                    {
                        string spaced = code.Length >= 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code;
                        r.Add(new[] { spaced, L10n.T("lobby.room.copy.desc", zh) });
                    }
                }
                else if (direct)
                {
                    string[] addrs = NetPlay.LocalAddresses();
                    string addr = (addrs != null && addrs.Length > 0 ? addrs[0] : "127.0.0.1") + ":" + NetPlay.ConfigPort;
                    r.Add(new[] { addr, L10n.T("lobby.address.copy.desc", zh) });
                }
                else
                    r.Add(new[] { L10n.T("lobby.invite", zh), L10n.T(NetPlay.SteamReady ? "lobby.invite.desc" : "lobby.steam.preparing", zh) });

                bool canStart = NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected;
                string status;
                if (NetPlay.PartnerHere)
                {
                    string who = !string.IsNullOrEmpty(NetPlay.SteamPartnerName) ? NetPlay.SteamPartnerName : L10n.T("lobby.partner", zh);
                    status = string.Format(L10n.T("lobby.partner.joined", zh), who);
                }
                else if (!string.IsNullOrEmpty(NetPlay.Problem)) status = L10n.T(FriendlyError(NetPlay.Problem, false), zh);
                else status = L10n.T(canStart ? "lobby.start.ready" : "lobby.start.preparing", zh);
                r.Add(new[] { L10n.T("lobby.start", zh), status });
                r.Add(new[] { L10n.T("common.cancel", zh), L10n.T("lobby.host.cancel.desc", zh) });
            }
            else if (s_page == PageJoin)
            {
                string dots = new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3);
                if (JoinFailed)
                {
                    string errorKey = FriendlyError(NetPlay.Problem, Time.realtimeSinceStartup - NetPlay.StartedAt > 15f);
                    string error = L10n.T(errorKey, zh);
                    if (!zh && errorKey == "lobby.error.failed" && !string.IsNullOrEmpty(NetPlay.Problem)) error += " (" + NetPlay.Problem + ")";
                    r.Add(new[] { L10n.T("common.retry", zh), error });
                    r.Add(new[] { L10n.T("common.back", zh), error });
                }
                else if (NetPlay.PartnerHere)
                    r.Add(new[] { L10n.T("lobby.connected", zh), L10n.T("lobby.connected.desc", zh) });
                else
                {
                    string status;
                    if (s_transport == "steam") status = L10n.T("lobby.steam.accept", zh);
                    else if (NetPlay.CurrentPhase == NetPlay.Phase.Connecting)
                        status = relay ? L10n.T("lobby.room.found", zh) : string.Format(L10n.T("lobby.connecting.target", zh), s_joinTarget);
                    else status = L10n.T("lobby.preparing", zh);
                    r.Add(new[] { L10n.T("lobby.connecting_bare", zh) + dots, status + SaveNote(zh) });
                    r.Add(new[] { L10n.T("common.cancel", zh), L10n.T("lobby.join.cancel.desc", zh) });
                }
            }
            return r.ToArray();
        }

        // Joining from the title (no save slot): the host's progress is kept on this PC by itself (CoopSaves).
        static string SaveNote(bool zh)
        {
            return s_onTitle ? L10n.T("lobby.save.note", zh) : "";
        }

        static bool JoinFailed
        {
            get
            {
                return NetPlay.CurrentPhase == NetPlay.Phase.Retrying ||
                    (NetPlay.CurrentPhase == NetPlay.Phase.Connecting && Time.realtimeSinceStartup - NetPlay.StartedAt > 15f);
            }
        }

        static string FriendlyError(string raw, bool timedOut)
        {
            string lower = (raw ?? "").ToLowerInvariant();
            if (timedOut || lower.Contains("timeout") || lower.Contains("timed out")) return "lobby.error.timeout";
            if (lower.Contains("not found") || lower.Contains("no such room")) return "lobby.error.room";
            if (lower.Contains("full")) return "lobby.error.full";
            if (lower.Contains("resolve") || lower.Contains("host is known")) return "lobby.error.address";
            if (lower.Contains("steam")) return "lobby.error.steam";
            return "lobby.error.failed";
        }

        // The value being edited, with the cursor marked while editing.
        static string EntryDisplay(bool zh)
        {
            int max = EntryMax;
            if (s_entryKind == 0 && s_editing && s_dial != null) return s_dial.Render(true, Mark);
            if (s_entryKind == 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < max; i++)
                {
                    if (i > 0) sb.Append(' ');
                    string c = i < s_entry.Length ? s_entry[i].ToString() : "_";
                    sb.Append(s_editing && i == s_cursor ? Mark(c) : c);
                }
                return sb.ToString();
            }
            if (s_entry.Length == 0 && !s_editing)
                return L10n.T(s_entryKind == 1 ? "lobby.ip.enter" : s_entryKind == 4 ? "cserver.port" : "lobby.server.enter", zh);
            if (!s_editing) return s_entry;
            string before = s_entry.Substring(0, s_cursor);
            string at = s_cursor < s_entry.Length ? s_entry[s_cursor].ToString() : "_";
            string after = s_cursor < s_entry.Length ? s_entry.Substring(s_cursor + 1) : "";
            return before + Mark(at) + after;
        }

        static string IpDisplay(bool zh)
        {
            bool cur = s_editing && s_editField == 0;
            if (cur && s_dial != null) return s_dial.Render(true, Mark);
            if (!cur && IpText.Replace(".", "").Length == 0) return L10n.T("lobby.ip.enter", zh);
            var sb = new StringBuilder();
            for (int o = 0; o < 4; o++)
            {
                if (o > 0) sb.Append('.');
                string s = s_ip[o];
                if (!cur || o != s_ipOct) { sb.Append(s.Length > 0 ? s : "_"); continue; }
                for (int i = 0; i < s.Length; i++) sb.Append(i == s_ipPos ? Mark(s[i].ToString()) : s[i].ToString());
                if (s_ipPos >= s.Length) sb.Append(Mark("_"));
            }
            return sb.ToString();
        }

        static string PortDisplay(bool zh)
        {
            string head = L10n.T("lobby.port.prefix", zh);
            if (s_editing && s_editField == 1 && s_dial != null) return head + s_dial.Render(true, Mark);
            if (!(s_editing && s_editField == 1)) return head + (s_port.Length > 0 ? s_port : "_");
            var sb = new StringBuilder(head);
            for (int i = 0; i < s_port.Length; i++) sb.Append(i == s_portPos ? Mark(s_port[i].ToString()) : s_port[i].ToString());
            if (s_portPos >= s_port.Length) sb.Append(Mark("_"));
            return sb.ToString();
        }

        static string Mark(string c) { return s_cursorStyle != null ? "<" + s_cursorStyle + ">" + c + "</>" : "[" + c + "]"; }

        static string EntryAlphabet { get { return s_entryKind == 0 ? RoomAlphabet : s_entryKind == 1 ? IpAlphabet : s_entryKind == 3 ? CServerHostAlphabet : s_entryKind == 4 ? CServerPortAlphabet : ServerAlphabet; } }
        static int EntryMax { get { return s_entryKind == 0 ? 6 : s_entryKind == 1 ? 21 : s_entryKind == 3 ? 253 : s_entryKind == 4 ? 10 : 48; } }

        // ---- navigation -----------------------------------------------------------------------------------

        // Online page -> a transport was pressed.
        static void LobbyOpen(string transport)
        {
            s_transport = transport;
            if (transport == "cserver") LoadCServer();
            Plugin.Log.LogInfo("mode menu: native lobby (" + transport + ")");
            GoPage(transport == "cserver" ? PageServerList : PageRole, 0);
        }

        static void LoadCServer()
        {
            string address = (OriCoop.Authority.CServerConfig.Address ?? "").Trim();
            int colon = address.LastIndexOf(':');
            s_cserverHost = colon >= 0 ? address.Substring(0, colon) : address;
            s_cserverPort = colon >= 0 ? address.Substring(colon + 1) : OriCoop.Authority.CServerConfig.DefaultPort.ToString();
        }

        static bool ValidateCServer()
        {
            if (!OriCoop.Authority.CServerConfig.IsValidAddress(OriCoop.Authority.CServerConfig.Address))
            { ToastKey("cserver.addr.invalid"); return false; }
            return true;
        }

        static void BeginCServer(bool host, string target)
        {
            s_cserverBeginTarget = target;
            Plugin.Log.LogInfo("mode menu: C-server Begin " + (host ? "host " : "join ") + target);
            NetPlay.Begin(host, "cserver", target);
        }

        static void SaveCServerEntry()
        {
            bool host = s_entryKind == 3;
            if (host ? !OriCoop.Authority.CServerConfig.IsValidHost(s_entry) : !OriCoop.Authority.CServerConfig.IsValidPort(s_entry))
            { ToastKey("cserver.addr.invalid"); return; }
            if (host) s_cserverHost = s_entry; else s_cserverPort = s_entry;
            GoPage(PageServerAdd, host ? 0 : 1);
        }

        static void GoPage(int page, int focus)
        {
            s_page = page;
            s_editing = false;
            s_connectedAt = 0f;
            s_toastUntil = 0f;
            s_listTop = 0;
            Relabel(focus);
            OnHighlightChanged();
            s_mgr.IsLocked = true;                          // the press that got us here is still down
            s_unlockAt = Time.realtimeSinceStartup + 0.25f;
            Plugin.Log.LogInfo("mode menu: page " + LastPage + " (" + string.Join(" | ", LastLabels.ToArray()) + ")");
        }

        static void ToastKey(string key) { s_toastKey = key; s_toastUntil = Time.realtimeSinceStartup + 2.5f; RefreshTexts(true); }

        static void LobbyPressed(int index)
        {
            if (IsRoomsPage) { RoomPressed(index); return; }
            bool relay = UsesRoomCode;
            if (s_page == PageServerList)
            {
                if (index == 0) { LoadCServer(); GoPage(PageServerAdd, 0); }
                else if (index == 1) GoPage(PageServerRemove, 0);
                else
                {
                    List<string> servers = OriCoop.Authority.CServerConfig.Servers;
                    if (index - 2 < servers.Count && OriCoop.Authority.CServerConfig.SetAddress(servers[index - 2]))
                    { LoadCServer(); GoPage(PageRole, 0); }
                    else ToastKey("cserver.addr.save_failed");
                }
            }
            else if (s_page == PageServerRemove)
            {
                if (OriCoop.Authority.CServerConfig.Servers.Count == 0) { LobbyBack(); return; }
                if (OriCoop.Authority.CServerConfig.RemoveServer(index)) GoPage(PageServerList, 1);
                else ToastKey("cserver.addr.save_failed");
            }
            else if (s_page == PageServerAdd)
            {
                if (index < 2)
                {
                    s_entryKind = index == 0 ? 3 : 4;
                    s_entry = index == 0 ? s_cserverHost : s_cserverPort;
                    s_cursor = Math.Min(s_entry.Length, EntryMax - 1);
                    GoPage(PageEntry, 0);
                }
                else
                {
                    string address = s_cserverHost + ":" + s_cserverPort;
                    if (!OriCoop.Authority.CServerConfig.IsValidAddress(address)) { ToastKey("cserver.addr.invalid"); return; }
                    if (!OriCoop.Authority.CServerConfig.AddServer(address)) { ToastKey("cserver.addr.save_failed"); return; }
                    GoPage(PageServerList, OriCoop.Authority.CServerConfig.Servers.Count + 1);
                }
            }
            else if (s_page == PageRole)
            {
                if (s_transport == "cserver" && index < 2 && !ValidateCServer()) return;
                if (index == 0)
                {
                    string srv = LobbyServer;
                    if (relay && srv.Length == 0) { ToastKey("lobby.server.required"); return; }
                    GoPage(PageHost, 1);
                    if (s_transport == "cserver") BeginCServer(true, srv + "|");
                    else NetPlay.Begin(true, s_transport, relay ? srv + "|" : "");
                }
                else if (index == 1)
                {
                    if (s_transport == "steam") { s_joinTarget = ""; GoPage(PageJoin, 1); NetPlay.Begin(false, "steam", ""); return; }
                    s_entryKind = relay ? 0 : 1;
                    if (relay)
                    {
                        s_legacyCode = HasLegacyLetter(NetPlay.ConfigRoomCode);   // a remembered older letter code stays a text entry
                        s_entry = Filter((NetPlay.ConfigRoomCode ?? "").Trim().ToUpperInvariant());
                        s_cursor = Math.Min(s_entry.Length, EntryMax - 1);
                        GoPage(PageEntry, s_entry.Length > 0 ? 1 : 0);
                    }
                    else
                    {
                        LoadAddress(NetPlay.ConfigAddress ?? "", NetPlay.ConfigPort);
                        GoPage(PageEntry, IpComplete && ValidPort(s_port) ? 2 : 0);
                    }
                }
                else if (s_transport == "relay" && index == 2)
                {
                    OpenRooms();
                }
                else if (s_transport == "relay")
                {
                    s_entryKind = 2;
                    s_entry = Filter((NetPlay.ConfigRelayServer ?? "").Trim().ToLowerInvariant());
                    s_cursor = Math.Min(s_entry.Length, EntryMax - 1);
                    GoPage(PageEntry, 0);
                }
            }
            else if (s_page == PageEntry && IpMode)
            {
                if (index == 0) StartEdit(0);
                else if (index == 1) StartEdit(1);
                else Submit();
            }
            else if (s_page == PageEntry)
            {
                if (index == 0) StartEdit();
                else if (index == 1) Submit();
                else LobbyBack();
            }
            else if (s_page == PageHost)
            {
                if (index == 0)
                {
                    if (relay)
                    {
                        if (!string.IsNullOrEmpty(NetPlay.RoomCode)) { GUIUtility.systemCopyBuffer = NetPlay.RoomCode; ToastKey("lobby.room.copied"); }
                    }
                    else if (s_transport == "direct")
                    {
                        string[] addrs = NetPlay.LocalAddresses();
                        GUIUtility.systemCopyBuffer = (addrs != null && addrs.Length > 0 ? addrs[0] : "127.0.0.1") + ":" + NetPlay.ConfigPort;
                        ToastKey("lobby.address.copied");
                    }
                    else if (NetPlay.SteamReady) NetPlay.OpenSteamInvite();
                }
                else if (index == 1)
                {
                    if (NetPlay.CurrentPhase == NetPlay.Phase.Waiting || NetPlay.CurrentPhase == NetPlay.Phase.Connected) Proceed();
                }
                else LobbyBack();
            }
            else if (s_page == PageJoin)
            {
                if (JoinFailed && index == 0)
                {
                    s_connectedAt = 0f;
                    Plugin.Log.LogInfo("mode menu: retry join " + s_transport + " " + s_joinTarget);
                    if (s_transport == "cserver") BeginCServer(false, s_joinTarget);
                    else NetPlay.Begin(false, s_transport, s_joinTarget);
                    RefreshTexts(true);
                }
                else if (index == 1) LobbyBack();
            }
        }

        static void Submit()
        {
            if (CServerEntry) { SaveCServerEntry(); return; }
            if (s_entryKind == 2)
            {
                NetPlay.SetRelayServer(s_entry);
                try { if (Plugin.Instance != null) Plugin.Instance.Config.Save(); } catch (Exception e) { Plugin.Log.LogWarning("mode menu: could not save the relay server: " + e.Message); }
                Plugin.Log.LogInfo("mode menu: relay server set to '" + s_entry + "'");
                GoPage(PageRole, 0);
                return;
            }
            if (s_entryKind == 0)
            {
                if (string.IsNullOrEmpty(s_transport == "cserver" ? OriCoop.Authority.CServerConfig.Address : NetPlay.ConfigRelayServer)) { ToastKey("lobby.server.required"); return; }
                if (s_entry.Length != 6) { ToastKey("lobby.room.required"); return; }
            }
            else
            {
                if (!IpComplete) { s_mgr.SetCurrentItem(0); ToastKey("lobby.ip.required"); return; }
                if (!ValidPort(s_port)) { s_mgr.SetCurrentItem(1); ToastKey("lobby.port.required"); return; }
            }
            s_joinTarget = s_entryKind == 0 ? s_entry.ToUpperInvariant() : IpText + ":" + s_port;
            if (s_transport == "cserver") s_joinTarget = OriCoop.Authority.CServerConfig.Address + "|" + s_joinTarget;
            GoPage(PageJoin, 1);
            Plugin.Log.LogInfo("mode menu: join " + s_transport + " " + s_joinTarget);
            if (s_transport == "cserver") BeginCServer(false, s_joinTarget);
            else NetPlay.Begin(false, s_transport, s_joinTarget);
        }

        static void LobbyBack()
        {
            if (IsRoomsPage) { RoomBack(); return; }
            if (s_page == PageServerList) { GoPage(PageServerMode, 0); return; }
            if (s_page == PageServerAdd || s_page == PageServerRemove) { GoPage(PageServerList, s_page == PageServerAdd ? 0 : 1); return; }
            if (s_page == PageRole)
            {
                if (s_transport == "cserver") { GoPage(PageServerList, 0); return; }
                if (UsesRoomCode) { GoPage(PageServerMode, 1); return; }
                s_page = 1;
                Relabel(s_transport == "direct" ? 1 : 2);
                OnHighlightChanged();
                Plugin.Log.LogInfo("mode menu: online page");
            }
            else if (s_page == PageEntry)
                GoPage(CServerEntry ? PageServerAdd : PageRole, s_entryKind == 3 ? 0 : s_entryKind == 4 ? 1 : s_entryKind == 2 ? 3 : 1);
            else if (s_page == PageHost) { NetPlay.End("host cancelled"); GoPage(PageRole, 0); }
            else if (s_page == PageJoin) { NetPlay.End("join cancelled"); GoPage(s_transport == "steam" ? PageRole : PageEntry, 1); }
        }

        static void Proceed()
        {
            Plugin.Log.LogInfo("mode menu: online session running (" + s_transport + "), loading the save");
            OnlineProceed();
        }

        // ---- per frame (from Driver) -------------------------------------------------------------------

        static void LobbyTick()
        {
            if (!IsOpen || !IsLobbyPage) return;
            if (IsRoomsPage) { RoomTick(); return; }
            if (s_editing) EditTick();
            else if (s_page == PageEntry && !s_mgr.IsLocked && s_entryKind == 0 && !s_legacyCode && s_mgr.Index == 0 && HasLegacyLetter(Input.inputString))
            {
                s_legacyCode = true; s_entry = ""; s_cursor = 0;   // an older letter code: plain text entry
                StartEdit(0); Insert(TypedChars()); RefreshTexts(true);
            }
            else if (s_page == PageEntry && !s_mgr.IsLocked && DialMode && (s_mgr.Index == 0 || (IpMode && s_mgr.Index == 1)))
            {
                int key = DigitKeyDown();
                if (key >= 0) { StartEdit(IpMode ? s_mgr.Index : 0); s_dial.Type(key); DialWriteBack(); RefreshTexts(true); }
            }
            else if (s_page == PageEntry && !s_mgr.IsLocked && (s_mgr.Index == 0 || (IpMode && s_mgr.Index == 1)))
            {
                string typed = TypedChars();
                if (typed.Length > 0) { StartEdit(IpMode ? s_mgr.Index : 0); Insert(typed); RefreshTexts(true); }
            }
            if (s_page == PageJoin && NetPlay.PartnerHere)
            {
                if (s_connectedAt <= 0f) { s_connectedAt = Time.realtimeSinceStartup; RefreshTexts(true); }
                else if (Time.realtimeSinceStartup - s_connectedAt >= 0.6f) { Proceed(); return; }
            }
            if (Time.realtimeSinceStartup >= s_nextRefresh) RefreshTexts(false);
        }

        static void StartEdit() { StartEdit(0); }

        static void StartEdit(int field)
        {
            s_editing = true;
            s_editField = IpMode ? field : 0;
            s_editStartedAt = Time.realtimeSinceStartup;
            s_ipAdvanced = false;
            s_cursor = Mathf.Clamp(s_cursor, 0, Math.Min(s_entry.Length, EntryMax - 1));
            s_ipOct = Mathf.Clamp(s_ipOct, 0, 3);
            s_ipPos = Mathf.Clamp(s_ipPos, 0, IpMaxPos(s_ipOct));
            s_portPos = Mathf.Clamp(s_portPos, 0, PortMaxPos);
            if (DialMode) BeginDial();
            s_mgr.IsLocked = true;          // we read the input ourselves while editing
            RefreshTexts(true);
        }

        static void StopEdit()
        {
            if (IsRoomsPage) { StopRoomEdit(); return; }
            s_editing = false;
            s_mgr.IsLocked = true;
            s_unlockAt = Time.realtimeSinceStartup + 0.25f;
            if (IpMode)
            {
                // IP done -> Connect if the port is fine too, else the port row; port done -> Connect.
                if (s_editField == 0 && IpComplete) s_mgr.SetCurrentItem(ValidPort(s_port) ? 2 : 1);
                else if (s_editField == 1 && ValidPort(s_port)) s_mgr.SetCurrentItem(IpComplete ? 2 : 0);
                else s_mgr.SetCurrentItem(s_editField);
                RefreshTexts(true);
                OnHighlightChanged();
                return;
            }
            bool complete = s_entryKind == 0 ? s_entry.Length == 6 : s_entry.Trim().Length > 0;
            if (complete) s_mgr.SetCurrentItem(1);   // straight to Connect / Save
            RefreshTexts(true);
            OnHighlightChanged();
        }

        static void EditTick()
        {
            if (Time.realtimeSinceStartup - s_editStartedAt < 0.15f) return;   // the press that started editing
            if (DialMode && s_dial != null) { DialEditTick(); return; }
            if (CServerEntry)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || Core.Input.Cancel.OnPressed) { LobbyBack(); return; }
                bool cChanged = CServerEditInput(Input.inputString);
                if (s_page != PageEntry) return;
                bool cCtrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if ((cCtrl && Input.GetKeyDown(KeyCode.V)) || Core.Input.Delete.OnPressed) { Paste(GUIUtility.systemCopyBuffer ?? ""); cChanged = true; }
                else if (Core.Input.Copy.OnPressed && Input.inputString.Length == 0) { Backspace(); cChanged = true; }
                if (Input.inputString.Length == 0)
                {
                    if (Core.Input.MenuLeft.OnPressed) { CursorMove(-1); cChanged = true; }
                    if (Core.Input.MenuRight.OnPressed) { CursorMove(+1); cChanged = true; }
                    if (Core.Input.MenuUp.OnPressed) { Cycle(+1); cChanged = true; }
                    if (Core.Input.MenuDown.OnPressed) { Cycle(-1); cChanged = true; }
                    if (Core.Input.ActionButtonA.OnPressed) { SaveCServerEntry(); return; }
                }
                if (cChanged) RefreshTexts(true);
                return;
            }
            string typed = TypedChars();
            bool changed = false;
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b') { Backspace(); changed = true; }
                else if (ch == '\n' || ch == '\r') { StopEdit(); return; }
            }
            if (typed.Length > 0) { Insert(typed); changed = true; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if ((ctrl && Input.GetKeyDown(KeyCode.V)) || Core.Input.Delete.OnPressed) { Paste(GUIUtility.systemCopyBuffer ?? ""); changed = true; }
            else if (Core.Input.Copy.OnPressed && typed.Length == 0) { Backspace(); changed = true; }
            if (typed.Length == 0 && Input.inputString.Length == 0)
            {
                if (Core.Input.MenuLeft.OnPressed) { CursorMove(-1); changed = true; }
                if (Core.Input.MenuRight.OnPressed) { CursorMove(+1); changed = true; }
                if (Core.Input.MenuUp.OnPressed) { Cycle(+1); changed = true; }
                if (Core.Input.MenuDown.OnPressed) { Cycle(-1); changed = true; }
                if (Core.Input.ActionButtonA.OnPressed || Core.Input.Cancel.OnPressed) { StopEdit(); return; }
            }
            if (changed) RefreshTexts(true);
        }

        static bool CServerEditInput(string input)
        {
            bool changed = false;
            foreach (char ch in input ?? "")
            {
                if (ch == '\b') { Backspace(); changed = true; }
                else if (ch == '\n' || ch == '\r') { SaveCServerEntry(); return changed; }
                else if (ch == '\x1b') { LobbyBack(); return changed; }
                else { Insert(ch.ToString()); changed = true; }
            }
            return changed;
        }

        static string TypedChars()
        {
            string s = Input.inputString;
            if (string.IsNullOrEmpty(s)) return "";
            return Filter(s);
        }

        static string Filter(string s)
        {
            string alpha = EntryAlphabet;
            var sb = new StringBuilder();
            foreach (char raw in s)
            {
                char c = s_entryKind == 0 ? char.ToUpperInvariant(raw) : s_entryKind == 2 || s_entryKind == 3 ? char.ToLowerInvariant(raw) : raw;
                if (alpha.IndexOf(c) >= 0) sb.Append(c);
            }
            return sb.ToString();
        }

        static void Insert(string text)
        {
            if (IpMode) { foreach (char c in Filter(text)) IpTypeChar(c); return; }
            foreach (char c in Filter(text))
            {
                if (s_entryKind == 0 && s_cursor < s_entry.Length)
                    s_entry = s_entry.Substring(0, s_cursor) + c + s_entry.Substring(s_cursor + 1);   // room code: overwrite
                else if (s_entry.Length < EntryMax)
                    s_entry = s_entry.Insert(s_cursor, c.ToString());
                else break;
                s_cursor = Math.Min(s_cursor + 1, EntryMax - 1);
            }
        }

        static void Backspace()
        {
            if (IpMode) { if (s_editField == 0) IpBackspace(); else PortBackspace(); return; }
            if (s_entry.Length == 0) return;
            // Delete the character before the cursor; on the last slot (the cursor stops there) delete the one under it.
            int at = s_cursor < s_entry.Length && s_cursor == EntryMax - 1 ? s_cursor : s_cursor - 1;
            if (at < 0) return;
            s_entry = s_entry.Remove(at, 1);
            s_cursor = Mathf.Clamp(at, 0, s_entry.Length);
        }

        static void Cycle(int dir)
        {
            if (IpMode)
            {
                s_ipAdvanced = false;
                if (s_editField == 0) { string s = s_ip[s_ipOct]; if (CycleDigit(ref s, s_ipPos, dir, ValidOctet)) s_ip[s_ipOct] = s; }
                else CycleDigit(ref s_port, s_portPos, dir, ValidPort);
                return;
            }
            string alpha = EntryAlphabet;
            if (s_cursor >= s_entry.Length)
            {
                if (s_entry.Length >= EntryMax) return;
                s_entry += alpha[dir > 0 ? 0 : alpha.Length - 1];
                return;
            }
            int i = alpha.IndexOf(s_entry[s_cursor]);
            i = ((i < 0 ? 0 : i + dir) % alpha.Length + alpha.Length) % alpha.Length;
            s_entry = s_entry.Substring(0, s_cursor) + alpha[i] + s_entry.Substring(s_cursor + 1);
        }

        // ---- digit dials (T-046) ----------------------------------------------------------------------------
        // Keys come from key codes, never from typed text: an IME (Chinese input) swallows typed characters, a key code
        // still arrives. Pad and arrow keys come through the game's own menu input.
        static int DigitKeyDown()
        {
            if (TestDialKey >= 0) { int k = TestDialKey; TestDialKey = -1; return k; }
            for (int d = 0; d <= 9; d++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + d) || Input.GetKeyDown(KeyCode.Keypad0 + d)) return d;
            return -1;
        }
        internal static int TestDialKey = -1;   // test hook: one digit key press for the next tick
        internal static bool TestDialConfirm;   // test hook: Enter / A on the next tick

        static void BeginDial()
        {
            if (IpMode && s_editField == 0)
            {
                s_dial = OriCoop.Ui.DigitDial.Ip();
                bool any = false; foreach (string part in s_ip) if (part.Length > 0) any = true;
                if (any) s_dial.Load(IpText);
                s_dialBefore = string.Join(".", s_ip);
            }
            else if (IpMode)
            {
                s_dial = OriCoop.Ui.DigitDial.Port();
                s_dial.Load(s_port.Length > 0 ? s_port : DefaultPort.ToString());
                s_dialBefore = s_port;
            }
            else
            {
                s_dial = s_entryKind == 5 ? OriCoop.Ui.DigitDial.Password() : OriCoop.Ui.DigitDial.Room();
                s_dial.Load(s_entry);
                s_dialBefore = s_entry;
            }
            DialWriteBack();
        }

        // The dial -> the values Connect reads (numbers without leading zeros; the room code keeps all six digits).
        static void DialWriteBack()
        {
            if (s_dial == null) return;
            if (IpMode && s_editField == 0) { for (int o = 0; o < 4; o++) s_ip[o] = int.Parse(s_dial.Group(o)).ToString(); }
            else if (IpMode) s_port = int.Parse(s_dial.Digits).ToString();
            else s_entry = s_dial.Digits;
        }

        static void DialCancel()
        {
            if (IpMode && s_editField == 0)
            {
                string[] parts = (s_dialBefore ?? "").Split('.');
                for (int o = 0; o < 4; o++) s_ip[o] = o < parts.Length ? parts[o] : "";
            }
            else if (IpMode) s_port = s_dialBefore ?? "";
            else s_entry = s_dialBefore ?? "";
            s_dial = null;
            StopEdit();
        }

        static void DialEditTick()
        {
            bool changed = false;
            if (s_entryKind == 0 && HasLegacyLetter(Input.inputString))
            {
                // A letter on the room-code dial: an older letter code. Switch to the text entry and keep typing there.
                string typedLetters = Filter(Input.inputString);
                s_dial = null; s_legacyCode = true; s_entry = ""; s_cursor = 0;
                Insert(typedLetters); RefreshTexts(true); return;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Core.Input.Cancel.OnPressed) { DialCancel(); return; }
            bool testConfirm = TestDialConfirm; TestDialConfirm = false;
            if (testConfirm || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Core.Input.ActionButtonA.OnPressed)
            { DialWriteBack(); s_dial = null; StopEdit(); return; }
            int key = DigitKeyDown();
            if (key >= 0) { s_dial.Type(key); changed = true; }
            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete) || (Core.Input.Copy.OnPressed && key < 0)) { s_dial.Back(); changed = true; }
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if ((ctrl && Input.GetKeyDown(KeyCode.V)) || Core.Input.Delete.OnPressed) { DialPaste(GUIUtility.systemCopyBuffer ?? ""); changed = true; }
            if (key < 0)
            {
                if (Core.Input.MenuLeft.OnPressed) { s_dial.Left(); changed = true; }
                if (Core.Input.MenuRight.OnPressed) { s_dial.Right(); changed = true; }
                if (Core.Input.MenuUp.OnPressed) { s_dial.Up(); changed = true; }
                if (Core.Input.MenuDown.OnPressed) { s_dial.Down(); changed = true; }
            }
            if (changed) { DialWriteBack(); RefreshTexts(true); }
        }

        // Ctrl+V: "a.b.c.d[:port]" on the IP dial fills both dials (an address that doesn't check out is refused);
        // otherwise the digits of the clipboard fill the dial.
        static void DialPaste(string text)
        {
            text = (text ?? "").Trim();
            if (IpMode && s_editField == 0)
            {
                string beforeIp = string.Join(".", s_ip), beforePort = s_port;
                int keepPort; int.TryParse(s_port, out keepPort);
                LoadAddress(text, keepPort);
                if (!IpComplete) { LoadAddress(beforeIp + ":" + beforePort, keepPort); ToastKey("lobby.ip.clipboard"); }
                s_dial.Load(IpText);
                return;
            }
            s_dial.Load(text);
        }

        // ---- direct join: IP octets + port ------------------------------------------------------------------

        static string IpText { get { return string.Join(".", s_ip); } }
        static bool IpComplete
        {
            get
            {
                for (int o = 0; o < 4; o++) if (!ValidOctet(s_ip[o])) return false;
                return IpText != "0.0.0.0";   // an untouched IP dial reads 0.0.0.0: not an address to join (T-046)
            }
        }
        static int IpMaxPos(int o) { return s_ip[o].Length < 3 ? s_ip[o].Length : 2; }
        static int PortMaxPos { get { return s_port.Length < 5 ? s_port.Length : 4; } }

        static bool AllDigits(string s) { foreach (char c in s) if (c < '0' || c > '9') return false; return true; }

        static bool ValidOctet(string s)
        {
            if (s == null || s.Length == 0 || s.Length > 3 || !AllDigits(s)) return false;
            if (s.Length > 1 && s[0] == '0') return false;
            return int.Parse(s) <= 255;
        }

        static bool ValidPort(string s)
        {
            if (s == null || s.Length == 0 || s.Length > 5 || !AllDigits(s) || s[0] == '0') return false;
            return int.Parse(s) <= 65535;
        }

        // Fill the fields from "a.b.c.d[:port]"; parts that don't check out stay empty.
        static void LoadAddress(string addr, int port)
        {
            for (int o = 0; o < 4; o++) s_ip[o] = "";
            addr = (addr ?? "").Trim();
            string portText = port > 0 ? port.ToString() : DefaultPort.ToString();
            int colon = addr.LastIndexOf(':');
            if (colon >= 0) { portText = addr.Substring(colon + 1).Trim(); addr = addr.Substring(0, colon); }
            string[] parts = addr.Split('.');
            if (parts.Length == 4)
            {
                bool ok = true;
                foreach (string p in parts) if (!ValidOctet(p.Trim())) ok = false;
                if (ok) for (int o = 0; o < 4; o++) s_ip[o] = parts[o].Trim();
            }
            s_port = ValidPort(portText) ? portText : DefaultPort.ToString();
            s_ipOct = 0; s_ipPos = 0; s_portPos = 0;
        }

        static void Paste(string text)
        {
            if (!IpMode) { Insert(text); return; }
            text = (text ?? "").Trim();
            if (s_editField == 1 && ValidPort(text)) { s_port = text; s_portPos = PortMaxPos; return; }
            string before = IpText + ":" + s_port;
            int keepPort; int.TryParse(s_port, out keepPort);
            LoadAddress(text, keepPort);
            if (!IpComplete)
            {
                LoadAddress(before, keepPort);
                ToastKey("lobby.ip.clipboard");
                return;
            }
            s_ipOct = 3; s_ipPos = IpMaxPos(3);
        }

        static void CursorMove(int dir)
        {
            if (!IpMode)
            {
                s_cursor = dir < 0 ? Math.Max(0, s_cursor - 1) : Math.Min(Math.Min(s_entry.Length, EntryMax - 1), s_cursor + 1);
                return;
            }
            s_ipAdvanced = false;
            if (s_editField == 1) { s_portPos = Mathf.Clamp(s_portPos + dir, 0, PortMaxPos); return; }
            if (dir < 0)
            {
                if (s_ipPos > 0) s_ipPos--;
                else if (s_ipOct > 0) { s_ipOct--; s_ipPos = IpMaxPos(s_ipOct); }
            }
            else
            {
                if (s_ipPos < IpMaxPos(s_ipOct)) s_ipPos++;
                else if (s_ipOct < 3) { s_ipOct++; s_ipPos = 0; }
            }
        }

        static void IpTypeChar(char c)
        {
            if (s_editField == 1)
            {
                if (c >= '0' && c <= '9' && !TypeDigit(ref s_port, ref s_portPos, 5, c, ValidPort))
                    ToastKey("lobby.port.range");
                return;
            }
            if (c == ':') { s_editField = 1; s_portPos = s_port.Length < 5 ? s_port.Length : 4; s_ipAdvanced = false; return; }
            if (c == '.')
            {
                if (s_ipAdvanced) { s_ipAdvanced = false; return; }   // "192." after "192" already moved on
                if (s_ipOct < 3) { s_ipOct++; s_ipPos = 0; }
                return;
            }
            if (c < '0' || c > '9') return;
            string s = s_ip[s_ipOct];
            int p = s_ipPos;
            if (s == "0" && p >= 1) { s = ""; p = 0; }                 // "0" then a digit: replace, no leading zero
            if (!TypeDigit(ref s, ref p, 3, c, ValidOctet))
            {
                s_ipAdvanced = false;
                ToastKey("lobby.ip.range");
                return;
            }
            s_ip[s_ipOct] = s;
            s_ipPos = p;
            // The octet can't take another digit (3 digits, "0", or > 25): move to the next one.
            bool full = s.Length == 3 || s == "0" || int.Parse(s) > 25;
            s_ipAdvanced = false;
            if (full && s_ipPos >= s.Length - (s.Length == 3 ? 1 : 0) && s_ipOct < 3)
            {
                s_ipOct++; s_ipPos = 0; s_ipAdvanced = true;
            }
            s_ipPos = Math.Min(s_ipPos, IpMaxPos(s_ipOct));
        }

        // Overwrite the digit under the cursor, or append at the end; false (and no change) if the result is invalid.
        static bool TypeDigit(ref string s, ref int p, int maxLen, char d, Func<string, bool> valid)
        {
            string next;
            if (p < s.Length) next = s.Substring(0, p) + d + s.Substring(p + 1);
            else if (s.Length < maxLen) next = s + d;
            else return false;
            if (!valid(next)) return false;
            s = next;
            p = Math.Min(p + 1, next.Length < maxLen ? next.Length : maxLen - 1);
            return true;
        }

        // Up/Down on a digit: the next value that keeps the field valid (the empty slot at the end adds a digit).
        static bool CycleDigit(ref string s, int p, int dir, Func<string, bool> valid)
        {
            int cur = p < s.Length ? s[p] - '0' : (dir > 0 ? -1 : 10);
            for (int k = 1; k <= 10; k++)
            {
                int d = ((cur + dir * k) % 10 + 10) % 10;
                string next = p < s.Length ? s.Substring(0, p) + (char)('0' + d) + s.Substring(p + 1) : s + (char)('0' + d);
                if (valid(next)) { s = next; return true; }
            }
            return false;
        }

        static void IpBackspace()
        {
            s_ipAdvanced = false;
            string s = s_ip[s_ipOct];
            if (s.Length == 3 && s_ipPos == 2) { s_ip[s_ipOct] = s.Substring(0, 2); return; }   // last slot: the digit under it
            if (s_ipPos > 0) { s_ip[s_ipOct] = s.Remove(s_ipPos - 1, 1); s_ipPos--; return; }
            if (s_ipOct == 0) return;
            s_ipOct--;                                                   // at the start of a part: into the previous one
            string prev = s_ip[s_ipOct];
            if (prev.Length > 0) s_ip[s_ipOct] = prev.Substring(0, prev.Length - 1);
            s_ipPos = s_ip[s_ipOct].Length;
        }

        static void PortBackspace()
        {
            if (s_port.Length == 0) return;
            if (s_port.Length == 5 && s_portPos == 4) { s_port = s_port.Substring(0, 4); return; }
            if (s_portPos == 0) return;
            string next = s_port.Remove(s_portPos - 1, 1);
            if (next.Length > 0 && next[0] == '0') { ToastKey("lobby.port.zero"); return; }
            s_port = next; s_portPos--;
        }

        // Re-set the texts that changed (status, room code, editing) without touching focus.
        static void RefreshTexts(bool force)
        {
            s_nextRefresh = Time.realtimeSinceStartup + 0.25f;
            if (s_mgr == null) return;
            if (force) { for (int i = 0; i < s_labelCache.Length; i++) { s_labelCache[i] = null; s_descCache[i] = null; } }
            string[][] rows = PageRows;
            ApplyRows(rows);
            int f = s_mgr.Index;
            if (s_tooltip == null || f < 0 || f >= rows.Length) return;
            string text = TooltipFor(rows, f);
            if (text != LastTooltip) { LastTooltip = text; s_tooltip.gameObject.SetActive(true); Set(s_tooltip, text); }
        }

        static string TooltipFor(string[][] rows, int i)
        {
            if (IsLobbyPage && Time.realtimeSinceStartup < s_toastUntil) return PickResolved(L10n.T(s_toastKey, true), L10n.T(s_toastKey, false));
            return PickResolved(rows[i][1], rows[i][3]);
        }

        static void EnsureRowItems(int count)
        {
            while (s_items.Count < count)
            {
                int index = s_items.Count;
                CleverMenuItem source = s_items[0];
                GameObject copy = (GameObject)InstantiateUtility.Instantiate(source.gameObject);
                copy.name = "OriCoop_ServerRow_" + index;
                copy.transform.parent = source.transform.parent;
                copy.transform.localScale = source.transform.localScale;
                copy.transform.localRotation = source.transform.localRotation;
                CleverMenuItem item = copy.GetComponent<CleverMenuItem>();
                item.Pressed = null; item.Activated = null; item.Visible = null;
                item.PressedCallback += () => OnPressed(index);
                s_items.Add(item);
                MessageBox[] boxes = copy.GetComponentsInChildren<MessageBox>(true);
                s_itemBoxes.Add(boxes);
                for (int b = 0; b < boxes.Length && b < s_itemBoxes[0].Length; b++)
                {
                    AnimationCurve curve;
                    if (s_baseCurves.TryGetValue(s_itemBoxes[0][b], out curve)) s_baseCurves[boxes[b]] = curve;
                }
                s_itemPositions.Add(s_itemPositions[0]);
                s_itemSizes.Add(s_itemSizes[0]);
            }
            if (s_labelCache.Length < s_items.Count)
            { s_labelCache = new string[s_items.Count]; s_descCache = new string[s_items.Count]; }
        }

        // Keep all logical rows navigable, with a four-row window in the existing native orb.
        // Off-window items stay active for keyboard/controller navigation but have no opacity or mouse bounds.
        static void LayoutServerRows(int count)
        {
            if (s_mgr == null || s_items.Count < 2) return;
            bool list = s_page == PageServerList || s_page == PageServerRemove || IsRoomsPage;
            s_mgr.ItemDirection = list ? CleverMenuItemSelectionManager.Direction.TopToBottom : s_itemDirection;
            int focus = Mathf.Clamp(s_mgr.Index, 0, Math.Max(0, count - 1));
            if (focus < s_listTop) s_listTop = focus;
            if (focus >= s_listTop + MaxItems) s_listTop = focus - MaxItems + 1;
            s_listTop = Mathf.Clamp(s_listTop, 0, Math.Max(0, count - MaxItems));
            Vector3 step = s_itemPositions[1] - s_itemPositions[0];
            for (int i = 0; i < s_items.Count; i++)
            {
                bool visible = !list || (i >= s_listTop && i < s_listTop + MaxItems);
                Vector3 position = list ? s_itemPositions[0] + step * (i - s_listTop) : s_itemPositions[i];
                s_items[i].transform.position = s_root.transform.TransformPoint(position);
                s_items[i].Size = visible ? s_itemSizes[i] : Vector2.zero;
                s_items[i].SetOpacity(visible ? 1f : 0f);
            }
        }

        // Write labels / descriptions into the items, show only as many items as there are rows.
        static void ApplyRows(string[][] rows)
        {
            EnsureRowItems(rows.Length);
            LastLabels.Clear();
            for (int i = 0; i < s_items.Count; i++)
            {
                bool on = i < rows.Length;
                if (s_items[i].gameObject.activeSelf != on)
                {
                    s_items[i].gameObject.SetActive(on);
                    if (on) s_items[i].TweenColor(s_items[i].IsHighlighted ? s_items[i].Transition.HighlightedColor : s_items[i].Transition.NormalColor);
                }
                if (!on) { s_labelCache[i] = null; s_descCache[i] = null; continue; }
                string label = PickResolved(rows[i][0], rows[i][2]), desc = PickResolved(rows[i][1], rows[i][3]);
                LastLabels.Add(label);
                MessageBox[] boxes = s_itemBoxes[i];
                if (label != s_labelCache[i]) { s_labelCache[i] = label; if (boxes.Length > 0) { FitLabel(boxes[0], label); Set(boxes[0], label); } }
                if (desc != s_descCache[i]) { s_descCache[i] = desc; for (int b = 1; b < boxes.Length; b++) Set(boxes[b], desc); }
            }
            // Only the shown items can be focused (a hidden item still has bounds the mouse could hover).
            int shown = Math.Min(rows.Length, s_items.Count);
            if (s_mgr.MenuItems.Count != shown) { s_mgr.MenuItems.Clear(); for (int i = 0; i < shown; i++) s_mgr.MenuItems.Add(s_items[i]); }
            if (s_mgr.Index >= rows.Length && rows.Length > 0) { bool q = s_quiet; s_quiet = true; try { s_mgr.SetCurrentItem(0); } finally { s_quiet = q; } }
            LayoutServerRows(rows.Length);
        }

        // Long labels (a full IP:port, a relay address) are wider than the orb. MessageBox scales its text by
        // ScaleOverLetterCount on every redraw, but it counts letters from the previous layout, so we give each
        // label box a flat curve worth the size this label needs. Width units: ASCII 1, CJK 2; the orb fits ~16.
        const float LabelFitUnits = 16f;
        static readonly Dictionary<MessageBox, AnimationCurve> s_baseCurves = new Dictionary<MessageBox, AnimationCurve>();

        static void FitLabel(MessageBox box, string label)
        {
            if (box == null) return;
            AnimationCurve baseCurve;
            if (!s_baseCurves.TryGetValue(box, out baseCurve))
            {
                baseCurve = box.ScaleOverLetterCount ?? AnimationCurve.Linear(0f, 1f, 150f, 1f);
                s_baseCurves[box] = baseCurve;
            }
            float units = 0f; int letters = 0; bool inTag = false;
            foreach (char c in label ?? "")
            {
                if (c == '<') { inTag = true; continue; }
                if (inTag) { if (c == '>') inTag = false; continue; }
                letters++;
                units += c >= 0x2E80 ? 2f : 1f;
            }
            float size = baseCurve.Evaluate(letters) * (units > LabelFitUnits ? LabelFitUnits / units : 1f);
            box.ScaleOverLetterCount = AnimationCurve.Linear(0f, size, 1000f, size);
        }

        // Pick a highlight style of the menu font for the edit cursor (logged once).
        static void FindCursorStyle(MessageBox box)
        {
            if (box == null || box.LanguageStyles == null) return;
            try
            {
                CatlikeCoding.TextBox.TextStyleCollection col = box.LanguageStyles.Current;
                if (col == null || col.styles == null) return;
                var names = new List<string>();
                foreach (CatlikeCoding.TextBox.TextStyle st in col.styles)
                {
                    if (st == null || string.IsNullOrEmpty(st.name)) continue;
                    names.Add(st.name);
                    string n = st.name.ToLowerInvariant();
                    if (s_cursorStyle == null && st.hasColor && (n.Contains("yellow") || n.Contains("highlight") || n.Contains("gold") || n.Contains("orange")))
                        s_cursorStyle = st.name;
                }
                Plugin.Log.LogInfo("mode menu: text styles: " + string.Join(", ", names.ToArray()) + "; edit cursor style: " + (s_cursorStyle ?? "(none, brackets)"));
            }
            catch (Exception e) { Plugin.Log.LogWarning("mode menu: style scan failed: " + e.Message); }
        }
    }
}
