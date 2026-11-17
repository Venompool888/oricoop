using System;
using System.Collections.Generic;

namespace OriCoop.Authority
{
    // A menu selection overrides the test environment and the last selected [Net] CServer.
    // Saved servers are separate from the selection; an empty list stays empty after migration.
    public static class CServerConfig
    {
        public const int DefaultPort = 47022;
        static BepInEx.Configuration.ConfigEntry<string> s_entry;
        static string s_selected;
        static BepInEx.Configuration.ConfigEntry<string> s_list;
        static BepInEx.Configuration.ConfigEntry<bool> s_migrated;
        static bool s_listReady;

        public static string Configured
        {
            get
            {
                if (s_entry == null && Plugin.Instance != null)
                    s_entry = Plugin.Instance.Config.Bind("Net", "CServer", "",
                        "Last selected C-server address host:port. No default server.");
                return s_entry != null ? (s_entry.Value ?? "").Trim() : "";
            }
        }

        public static string Address
        {
            get
            {
                if (s_selected != null) return s_selected;
                string env = Environment.GetEnvironmentVariable("ORICOOP_NET_CSERVER");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                return Configured;
            }
        }

        static bool LoadList()
        {
            if (s_listReady) return true;
            if (Plugin.Instance == null) return false;
            string legacy = Configured;
            if (s_list == null) s_list = Plugin.Instance.Config.Bind("Net", "CServerList", "",
                "Saved C-servers as host:port entries separated by ';'. No default servers.");
            if (s_migrated == null) s_migrated = Plugin.Instance.Config.Bind("Net", "CServerListMigrated", false,
                "Whether the legacy single CServer has been imported. Keep true even when the list is empty.");
            try
            {
                if (!s_migrated.Value)
                {
                    if (string.IsNullOrEmpty(s_list.Value) && IsValidAddress(legacy)) s_list.Value = legacy;
                    s_migrated.Value = true;
                    Plugin.Instance.Config.Save();
                }
                s_listReady = true;
                return true;
            }
            catch (Exception)
            {
                try { s_migrated.Value = false; } catch (Exception) { }
                return false;
            }
        }

        public static List<string> Servers
        {
            get
            {
                var result = new List<string>();
                if (!LoadList()) return result;
                foreach (string raw in (s_list.Value ?? "").Split(';'))
                {
                    string address = raw.Trim();
                    if (IsValidAddress(address)) result.Add(address);
                }
                return result;
            }
        }

        static bool SaveList(List<string> servers)
        {
            if (!LoadList()) return false;
            string previous = s_list.Value;
            try
            {
                s_list.Value = string.Join(";", servers.ToArray());
                Plugin.Instance.Config.Save();
                return true;
            }
            catch (Exception)
            {
                try { s_list.Value = previous; } catch (Exception) { }
                return false;
            }
        }

        public static bool AddServer(string address)
        {
            if (!IsValidAddress(address) || !LoadList()) return false;
            List<string> servers = Servers;
            servers.Add(address);
            return SaveList(servers);
        }

        public static bool RemoveServer(int index)
        {
            if (!LoadList()) return false;
            List<string> servers = Servers;
            if (index < 0 || index >= servers.Count) return false;
            servers.RemoveAt(index);
            return SaveList(servers);
        }

        public static bool IsValidAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return false;
            int colon = address.IndexOf(':');
            if (colon <= 0 || colon != address.LastIndexOf(':')) return false;
            string host = address.Substring(0, colon), port = address.Substring(colon + 1);
            return IsValidHost(host) && IsValidPort(port);
        }

        public static bool IsValidPort(string port)
        {
            if (string.IsNullOrEmpty(port) || port.Length > 10) return false;
            foreach (char c in port) if (c < '0' || c > '9') return false;
            int number;
            return int.TryParse(port, out number) && number >= 1 && number <= 65535;
        }

        public static bool IsValidHost(string host)
        {
            if (string.IsNullOrEmpty(host) || host.Length > 253) return false;
            string[] labels = host.Split('.');
            bool numeric = labels.Length > 1;
            foreach (string label in labels)
            {
                if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                foreach (char c in label)
                {
                    bool digit = c >= '0' && c <= '9';
                    if (!digit) numeric = false;
                    if (!digit && !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && c != '-') return false;
                }
            }
            if (numeric)
            {
                if (labels.Length != 4) return false;
                System.Net.IPAddress ip;
                if (!System.Net.IPAddress.TryParse(host, out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
                byte[] octets = ip.GetAddressBytes();
                int number;
                for (int i = 0; i < labels.Length; i++)
                    if (labels[i].Length > 3 || !int.TryParse(labels[i], out number) || number > 255 || number != octets[i]) return false;
            }
            return true;
        }

        public static bool SetAddress(string address)
        {
            if (!IsValidAddress(address)) return false;
            string previous = Configured;
            if (s_entry == null || Plugin.Instance == null) return false;
            if (s_selected == address && previous == address) return true;
            try
            {
                s_entry.Value = address;
                Plugin.Instance.Config.Save();
                s_selected = address;
                return true;
            }
            catch (Exception)
            {
                try { s_entry.Value = previous; } catch (Exception) { }
                return false;
            }
        }
    }
}
