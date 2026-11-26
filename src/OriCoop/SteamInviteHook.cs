using System;
using Game;
using OriCoop.Net;
using OriCoop.Rules;
using UnityEngine;

namespace OriCoop
{
    // Steam invites accepted outside the "Online -> Steam -> Join" page.
    //
    // ManagedSteam drops a callback when nothing is subscribed at the moment it is pumped, and before this
    // class only NetPlay (while a Steam join was already running) polled SteamTransport.PendingInviteLobby,
    // which is what registers the invite hook. A friend who accepted at the title screen or in the menus lost
    // the invite, and nothing ever acted on it. Now, every frame from game start:
    //   * the Steam hooks are registered (polling PendingInviteLobby; also reads +connect_lobby once);
    //   * outside gameplay (title, save slots, menus) with online play off, an invite starts the Steam join
    //     by itself (NetPlay.Begin(false, "steam")); a banner shows the progress;
    //   * on the title main menu the title orb opens on the Steam join page, which proceeds by itself; with no menu
    //     open (save slots, other screens) the guest loads the host's save once the host is there, with no save
    //     slot (NativeModeMenu.StartGuestNoSlot -> GuestJoin.BeginNoSlot); an open slot orb proceeds at once;
    //   * in gameplay the invite is kept and a banner says to return to the main menu (we do not tear down a
    //     running game); it is acted on as soon as the player is out of gameplay, and dropped after 5 minutes;
    //   * while another online session runs: in the menus with nobody in it (a host page waiting, the slots after
    //     开房), the invite ends it and joins (T-031); while playing with someone the banner says to return to the
    //     title, which ends that session, and the join then starts by itself (Rules\InviteRules).
    public static class SteamInviteHook
    {
        const double InviteLifetimeSeconds = 300.0;
        const float GiveUpSeconds = 60f;

        static bool s_startedByInvite;
        static float s_startedAt;
        static ulong s_retryLobby;
        internal static bool CanRetry { get { return s_retryLobby != 0; } }
        internal static void ForgetRetry() { s_retryLobby = 0; s_failUntil = 0f; }
        internal static void Retry()
        {
            if (!CanRetry) return;
            StartJoin(s_retryLobby.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        static string s_inviter = "";
        static float s_partnerSince;
        static ulong s_announced;          // lobby whose "can't join now" state was logged
        static float s_failUntil;
        static GUIStyle s_style;
        internal static bool TestShow;

        public static void Tick()
        {
            // Local gameplay with online play off: do nothing per frame. Polling Steam here broke local co-op
            // (run-test revive/teleport/enemy failed). The hooks were registered at the title screen, so an invite
            // accepted now is still stored and handled once the player is back in the menus.
            if (!NetPlay.Enabled && NetPlay.LocalInGameplay()) return;
            ulong lobby = SteamTransport.PendingInviteLobby;     // also keeps the Steam hooks registered
            if (lobby != 0 && SteamTransport.PendingInviteAgeSeconds > InviteLifetimeSeconds)
            {
                SteamTransport.ClearPendingInvite("not used within " + (int)InviteLifetimeSeconds + " s");
                lobby = 0;
            }
            if (lobby != 0) HandleInvite(lobby);
            if (s_startedByInvite) FollowJoin();
        }

        static void HandleInvite(ulong lobby)
        {
            if (NetPlay.Enabled)
            {
                // A Steam join page that is already waiting picks the invite up itself (NetPlay.PumpSteam).
                if (!NetPlay.IsHost && NetPlay.UsesSteam) return;
                if (!InviteReplacesSession)
                {
                    Announce(lobby, "online play is already running (" + (NetPlay.IsHost ? "host" : "guest") + " via " + NetPlay.TransportName + ")");
                    return;
                }
                // T-031: a session nobody joined yet, in the menus (a host page waiting, a join page, the save slots after
                // 开房): accepting a friend's invite means "join them instead", so end it and join.
                Plugin.Log.LogInfo("net: Steam: invite: ending the idle " + (NetPlay.IsHost ? "host" : "guest") + " session via " +
                    NetPlay.TransportName + " to join the invite");
                NetPlay.End("Steam invite accepted");
            }
            if (NetPlay.LocalInGameplay() || Loading)
            {
                Announce(lobby, NetPlay.LocalInGameplay() ? "in gameplay; it starts when you return to the main menu" : "a save is loading");
                return;
            }
            s_inviter = SteamTransport.FriendName(SteamTransport.PendingInviteFrom);
            Plugin.Log.LogInfo("net: Steam: invite: starting the Steam join by itself (lobby " + lobby +
                (s_inviter.Length > 0 ? " from " + s_inviter : "") + ")");
            s_announced = 0;
            StartJoin("");   // OpenSteam consumes the new invitation on the next update.
            if (s_startedByInvite) NativeModeMenu.ShowSteamJoinPage();
        }

        static void StartJoin(string target)
        {
            ForgetRetry();
            NetPlay.Begin(false, "steam", target);
            s_startedByInvite = NetPlay.Enabled && !NetPlay.IsHost && NetPlay.UsesSteam;
            s_startedAt = Time.realtimeSinceStartup;
            s_partnerSince = 0f;
        }

        static void Announce(ulong lobby, string why)
        {
            if (s_announced == lobby) return;
            s_announced = lobby;
            Plugin.Log.LogInfo("net: Steam: invite for lobby " + lobby + " kept for later: " + why);
        }

        // An online session the invite may replace: nobody else is in it, and this player is not in gameplay or loading.
        internal static bool InviteReplacesSession
        {
            get { return InviteRules.ReplacesSession(NetPlay.Enabled, NetPlay.PartnerHere, NetPlay.LocalInGameplay(), Loading); }
        }

        static bool Loading
        {
            get { GameController gc = GameController.Instance; return gc != null && gc.IsLoadingGame; }
        }

        // The join this class started: proceed once the host is there; give up if it never gets there.
        static void FollowJoin()
        {
            if (!NetPlay.Enabled || NetPlay.IsHost || !NetPlay.UsesSteam)
            {
                s_startedByInvite = false;          // ended, or the player chose something else in the menu
                return;
            }
            if (NetPlay.PartnerHere)
            {
                if (NetPlay.LocalInGameplay()) { s_startedByInvite = false; return; }   // the save is loading/loaded
                if (!NativeModeMenu.IsOpen)
                {
                    // No menu open (save slots, another title screen): load the host's save with no slot pick.
                    NativeModeMenu.StartGuestNoSlot("Steam invite");
                    if (NativeModeMenu.GuestFromTitle) s_startedByInvite = false;
                    return;
                }
                if (NativeModeMenu.OnSteamJoinPage) { s_startedByInvite = false; return; }   // the join page proceeds itself
                if (s_partnerSince <= 0f) { s_partnerSince = Time.realtimeSinceStartup; return; }
                if (Time.realtimeSinceStartup - s_partnerSince < 0.6f) return;         // the slot press is still down
                Plugin.Log.LogInfo("net: Steam: invite: host connected and the mode menu is open; loading the save");
                s_startedByInvite = false;
                NativeModeMenu.OnlineProceed();
                return;
            }
            s_partnerSince = 0f;
            if (Time.realtimeSinceStartup - s_startedAt >= GiveUpSeconds)
            {
                string problem = NetPlay.Problem;
                Plugin.Log.LogWarning("net: Steam: invite: gave up joining after " + (int)GiveUpSeconds + " s" +
                    (string.IsNullOrEmpty(problem) ? "" : " (" + problem + ")"));
                s_startedByInvite = false;
                if (NativeModeMenu.OnSteamJoinPage)
                    s_retryLobby = NetPlay.SteamLobby != 0 ? NetPlay.SteamLobby : SteamTransport.PendingInviteRaw;
                NetPlay.End("Steam invite join gave up");
                s_failUntil = Time.realtimeSinceStartup + 10f;
            }
        }

        // ---- banner ---------------------------------------------------------------------------------------

        static bool Banner(out string text)
        {
            text = null;
            if (Time.realtimeSinceStartup < s_failUntil) { text = L10n.T("steam.failed"); return true; }
            ulong lobby = SteamTransport.PendingInviteRaw;
            if (lobby != 0)
            {
                if (NetPlay.Enabled && !NetPlay.IsHost && NetPlay.UsesSteam) return false;
                string who = SteamTransport.FriendName(SteamTransport.PendingInviteFrom);
                if (who.Length == 0) who = L10n.T("steam.friend");
                text = L10n.F(InviteRules.BannerKey(NetPlay.Enabled, NetPlay.PartnerHere), who);
                return true;
            }
            if (!s_startedByInvite || NetPlay.LocalInGameplay() || NativeModeMenu.OnSteamJoinPage) return false;
            string host = NetPlay.SteamPartnerName;
            if (string.IsNullOrEmpty(host)) host = s_inviter;
            if (string.IsNullOrEmpty(host)) host = L10n.T("steam.friend");
            if (NetPlay.PartnerHere)
            {
                text = NativeModeMenu.IsOpen ? L10n.F("steam.starting", host) : L10n.F("steam.receiving", host);
                return true;
            }
            string dots = new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3);
            text = !string.IsNullOrEmpty(NetPlay.Problem) ? L10n.F("steam.retry", host, dots) : L10n.F("steam.joining", host, dots);
            return true;
        }

        public static void DrawGUI()
        {
            string text;
            if (L10nProbe.On && TestShow) text = L10n.F("steam.joining", L10n.T("steam.friend"), "...");
            else if (!Banner(out text)) return;
            NativeGui.BeginPage("steam.invite");
            try
            {
                if (s_style == null)
                {
                    s_style = new GUIStyle(GUI.skin.box);
                    s_style.alignment = TextAnchor.MiddleCenter;
                    s_style.wordWrap = true;
                    s_style.normal.textColor = new Color(0.867f, 0.918f, 0.898f);
                }
                s_style.fontSize = Mathf.Max(12, Screen.height / 45);
                float w = Mathf.Min(Screen.width - 40f, Screen.height * 1.4f);
                float h = s_style.fontSize * 4.2f;
                Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.06f, w, h);
                if (NativeGui.Available)
                {
                    NativeGui.Panel("steam.invite.panel", r, new Color(0.01f, 0.03f, 0.06f, 0.8f));
                    NativeGui.Label("steam.invite.text", r, text, s_style.fontSize, s_style.alignment, s_style.normal.textColor);
                }
                else GUI.Box(r, text, s_style);
            }
            finally { NativeGui.EndPage(); }
        }
    }
}
