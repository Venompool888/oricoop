using System;

namespace OriCoop.Authority
{
    // Where the C-server is. Order: env ORICOOP_NET_CSERVER, [Net] CServer, else the relay server's host on 47022
    // (C-server is deployed next to the relay; docs/c-server-design.md §6). Never ship a default address.
    public static class CServerConfig
    {
        public const int DefaultPort = 47022;
        static BepInEx.Configuration.ConfigEntry<string> s_entry;

        public static string Configured
        {
            get
            {
                if (s_entry == null && Plugin.Instance != null)
                    s_entry = Plugin.Instance.Config.Bind("Net", "CServer", "",
                        "C-server (equal mode) address host:port. Empty = the relay server's host on port 47022.");
                return s_entry != null ? (s_entry.Value ?? "").Trim() : "";
            }
        }

        public static string Address
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                if (Configured.Length > 0) return Configured;
                string relay = (NetPlay.ConfigRelayServer ?? "").Trim();
                if (relay.Length == 0) return "";
                int colon = relay.LastIndexOf(':');
                string host = colon > 0 && relay.IndexOf(':') == colon ? relay.Substring(0, colon) : relay;
                return host + ":" + DefaultPort;
            }
        }
    }
}
