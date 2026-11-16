using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using BepInEx;

namespace OriCoop
{
    public static class RelayList
    {
        public class Entry
        {
            public string Name;
            public string Address;
            public bool Selected;
        }

        public static Func<bool> InUse;
        const string Header = "# OriCoop relay servers. * = the one in use. Edit with OriCoop-Relays.html or by hand.";
        const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static List<Entry> applied = new List<Entry>();
        static List<Entry> pending;
        static string filePath;
        static DateTime lastWrite;
        static long lastLength;
        static bool haveStamp;
        static long nextPoll;
        static int version;

        // Called on the game thread. Copies include the mutable Entry objects.
        public static List<Entry> Entries { get { return Copy(applied); } }
        public static string Current { get { return CurrentOf(applied); } }
        public static bool HasPending { get { return pending != null; } }
        public static int Version { get { return version; } }

        public static void Init(string legacyServer)
        {
            // Relay join identity: Steam account and Steam name (or ORICOOP_PLAYER_NAME). Never the Windows user name.
            OriCoop.Net.RelayTransport.LocalSteamIdSource = delegate { return OriCoop.Net.SteamTransport.LocalSteamId; };
            OriCoop.Net.RelayTransport.LocalNameSource = delegate
            {
                string name = OriCoop.Net.SteamTransport.LocalPersonaName;
                return string.IsNullOrEmpty(name) ? (Environment.GetEnvironmentVariable("ORICOOP_PLAYER_NAME") ?? "") : name;
            };
            filePath = Path.Combine(Paths.ConfigPath, "oricoop-relays.txt");
            haveStamp = false;
            nextPoll = Clock.ElapsedMilliseconds + 1000;
            try
            {
                if (!File.Exists(filePath))
                {
                    Directory.CreateDirectory(Paths.ConfigPath);
                    List<Entry> initial = new List<Entry>();
                    string address;
                    if (NormalizeAddress(legacyServer, out address))
                        initial.Add(new Entry { Name = "", Address = address, Selected = true });
                    // CreateNew avoids overwriting a file created by the web page meanwhile.
                    Write(initial, FileMode.CreateNew);
                }
                haveStamp = false; // Init must load even when it just wrote the file.
                ReadChanged(InUse != null && InUse());
            }
            catch (IOException) { haveStamp = false; }
            catch (UnauthorizedAccessException ex) { Warn(ex); }
        }

        public static void Tick()
        {
            if (filePath == null) return;
            bool inUse = InUse != null && InUse();
            long now = Clock.ElapsedMilliseconds;
            if (now >= nextPoll)
            {
                nextPoll = now + 1000;
                try { ReadChanged(inUse); }
                // Keep the old stamp on failure so the next poll retries the read.
                catch (IOException) { }
                catch (UnauthorizedAccessException ex) { Warn(ex); }
            }
            if (pending != null && !inUse)
            {
                List<Entry> ready = pending;
                pending = null;
                Apply(ready);
            }
        }

        public static void AddOrSelect(string address, string name)
        {
            string normalized;
            if (!NormalizeAddress(address, out normalized))
            {
                Plugin.Log.LogWarning("relay list: invalid relay address");
                return;
            }
            if (filePath == null) Init("");
            List<Entry> entries = Copy(pending ?? applied);
            Entry selected = null;
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].Selected = false;
                if (selected == null && string.Equals(entries[i].Address, normalized, StringComparison.OrdinalIgnoreCase))
                    selected = entries[i];
            }
            if (selected == null)
            {
                selected = new Entry { Name = SafeName(name), Address = normalized };
                entries.Add(selected);
            }
            selected.Selected = true;
            try
            {
                Directory.CreateDirectory(Paths.ConfigPath);
                Write(entries, FileMode.Create);
                Accept(entries, InUse != null && InUse());
            }
            catch (IOException ex) { Warn(ex); }
            catch (UnauthorizedAccessException ex) { Warn(ex); }
        }

        static void Warn(Exception ex)
        {
            Plugin.Log.LogWarning("relay list: " + ex.Message);
        }

        static void ReadChanged(bool inUse)
        {
            FileInfo info = new FileInfo(filePath);
            DateTime stamp = info.LastWriteTimeUtc;
            long length = info.Length;
            if (haveStamp && stamp == lastWrite && length == lastLength) return;
            string text;
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                text = reader.ReadToEnd();
            List<Entry> entries = Parse(text);
            // Remember the pre-read stamp: an edit during the read triggers another poll.
            lastWrite = stamp;
            lastLength = length;
            haveStamp = true;
            Accept(entries, inUse);
        }

        static void Write(List<Entry> entries, FileMode mode)
        {
            // Deny concurrent writers until the bytes and our remembered stamp agree.
            using (FileStream stream = new FileStream(filePath, mode, FileAccess.Write, FileShare.Read))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(Serialize(entries));
                writer.Flush();
                FileInfo info = new FileInfo(filePath);
                lastWrite = info.LastWriteTimeUtc;
                lastLength = info.Length;
                haveStamp = true;
            }
        }

        static void Accept(List<Entry> entries, bool inUse)
        {
            if (inUse)
            {
                if (pending == null)
                    Plugin.Log.LogInfo("relay list: file changed; applying after the relay session ends");
                pending = entries;
                return;
            }
            pending = null;
            Apply(entries);
        }

        static void Apply(List<Entry> entries)
        {
            if (!Same(applied, entries)) version++;
            applied = entries;
            Plugin.Log.LogInfo("relay list: " + entries.Count + " servers, using " + Current);
        }

        static List<Entry> Copy(List<Entry> entries)
        {
            List<Entry> result = new List<Entry>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
                result.Add(new Entry { Name = entries[i].Name, Address = entries[i].Address, Selected = entries[i].Selected });
            return result;
        }

        public static string SelfTest()
        {
            try
            {
                string address, room, name;
                string[] links = { "oricoop://relay.example:47020", "oricoop://relay.example:47020/abcd23", "relay.example", "127.0.0.1", " 127.0.0.1:1 ", "relay.example:65535" };
                string[] addresses = { "relay.example:47020", "relay.example:47020", "relay.example:47020", "127.0.0.1:47020", "127.0.0.1:1", "relay.example:65535" };
                for (int i = 0; i < links.Length; i++)
                {
                    if (!ParseLink(links[i], out address, out room, out name) || address != addresses[i] || room != (i == 1 ? "ABCD23" : "") || name != "")
                        return "ParseLink example " + i;
                }
                if (!ParseLink("oricoop://relay.example:47020/abCD23?name=My+Relay+%26+%E4%B8%AD", out address, out room, out name) || room != "ABCD23" || name != "My Relay & \u4e2d")   // l10n-ok: URL-decoding self-test
                    return "room and URL-decoded name";
                string[] badRooms = { "ROOM", "ABC0I1", "ABC23O", "ABCDEFG", "ABCD23/extra" };
                for (int i = 0; i < badRooms.Length; i++)
                    if (!ParseLink("oricoop://relay.example/" + badRooms[i], out address, out room, out name) || room != "")
                        return "invalid room " + i;
                string[] invalid = { null, "", " ", "oricoop://", ":47020", "host:", "host:0", "host:65536", "host:-1", "host:+1", "host:abc", "ho st:47020", "host:47 020", "http://host:47020", "host/path", "user@host:47020", "host:1:2" };
                for (int i = 0; i < invalid.Length; i++)
                    if (ParseLink(invalid[i], out address, out room, out name)) return "accepted invalid address " + i;

                List<Entry> entries = Parse("\ufeff# comment\r\n\t127.0.0.1\r\n*First\trelay.example:47020\r\n*Second\tother.example:1234\r\nmissing tab\r\nBad\thost:0\r\nBad\tbad host:5\r\n*Bad\t:12\r\n");
                if (entries.Count != 3 || entries[0].Name != "" || entries[0].Address != "127.0.0.1:47020" || entries[0].Selected || !entries[1].Selected || entries[2].Selected || CurrentOf(entries) != "relay.example:47020")
                    return "BOM, CRLF, empty name, selection or malformed lines";
                if (!Same(entries, Parse(Serialize(entries)))) return "serialize round trip";
                if (Serialize(entries).IndexOf('\r') >= 0 || Serialize(entries)[0] == '\ufeff') return "serialized LF / BOM";
                if (CurrentOf(Parse("\t127.0.0.1\nOther\thost:123\n")) != "127.0.0.1:47020" || CurrentOf(Parse("# only comments\ninvalid")) != "")
                    return "unselected or empty list fallback";
                entries = Parse("*\thost:123\n*Later\tother:456\n");
                if (entries.Count != 2 || !entries[0].Selected || entries[1].Selected || entries[0].Name != "") return "first selected empty name";
                if (!Same(entries, Parse(Serialize(entries)))) return "selected empty name round trip";
                return "";
            }
            catch (Exception ex) { return "SelfTest exception: " + ex.Message; }
        }

        public static bool ParseLink(string text, out string address, out string room, out string name)
        {
            address = room = name = "";
            if (text == null) return false;
            text = text.Trim();
            if (!text.StartsWith("oricoop://", StringComparison.OrdinalIgnoreCase))
                return NormalizeAddress(text, out address);

            string target = text.Substring(10);
            string query = "";
            int question = target.IndexOf('?');
            if (question >= 0)
            {
                query = target.Substring(question + 1);
                target = target.Substring(0, question);
            }
            int slash = target.IndexOf('/');
            string candidateRoom = slash < 0 ? "" : target.Substring(slash + 1).ToUpperInvariant();
            string host = slash < 0 ? target : target.Substring(0, slash);
            // Do not trim inside the URI authority; whitespace there is invalid.
            if (host != host.Trim() || !NormalizeAddress(host, out address)) return false;
            if (candidateRoom.Length == 6)
            {
                bool valid = true;
                for (int i = 0; i < candidateRoom.Length; i++)
                    if (RoomAlphabet.IndexOf(candidateRoom[i]) < 0) valid = false;
                if (valid) room = candidateRoom;
            }
            string[] fields = query.Split('&');
            for (int i = 0; i < fields.Length; i++)
            {
                if (!fields[i].StartsWith("name=", StringComparison.Ordinal)) continue;
                try { name = Uri.UnescapeDataString(fields[i].Substring(5).Replace("+", " ")); }
                catch (UriFormatException) { name = ""; }
                break;
            }
            return true;
        }

        static bool NormalizeAddress(string text, out string address)
        {
            address = "";
            if (text == null) return false;
            text = text.Trim();
            if (text.Length == 0) return false;
            for (int i = 0; i < text.Length; i++)
                if (char.IsWhiteSpace(text[i]) || char.IsControl(text[i]) || "/\\?#@".IndexOf(text[i]) >= 0)
                    return false;

            string host;
            string portText = null;
            if (text[0] == '[')
            {
                int close = text.IndexOf(']');
                if (close <= 1) return false;
                IPAddress ip;
                string literal = text.Substring(1, close - 1);
                if (literal.IndexOf(':') < 0 || !IPAddress.TryParse(literal, out ip)) return false;
                host = text.Substring(0, close + 1);
                if (close + 1 < text.Length)
                {
                    if (text[close + 1] != ':') return false;
                    portText = text.Substring(close + 2);
                }
            }
            else
            {
                int colon = text.IndexOf(':');
                host = colon < 0 ? text : text.Substring(0, colon);
                if (host.Length == 0 || host.IndexOf('[') >= 0 || host.IndexOf(']') >= 0) return false;
                if (colon >= 0) portText = text.Substring(colon + 1);
            }
            int port = 47020;
            if (portText != null && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535))
                return false;
            address = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        // Pure format helpers: neither the parser nor serializer touches global state.
        static List<Entry> Parse(string text)
        {
            List<Entry> result = new List<Entry>();
            bool selectedSeen = false;
            using (StringReader reader = new StringReader((text ?? "").TrimStart('\ufeff')))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    bool selected = line[0] == '*';
                    int start = selected ? 1 : 0;
                    int tab = line.IndexOf('\t', start);
                    string address;
                    if (tab < 0 || !NormalizeAddress(line.Substring(tab + 1), out address)) continue;
                    result.Add(new Entry { Name = line.Substring(start, tab - start), Address = address, Selected = selected && !selectedSeen });
                    if (selected) selectedSeen = true;
                }
            }
            return result;
        }

        static string Serialize(List<Entry> entries)
        {
            StringBuilder text = new StringBuilder(Header).Append('\n');
            bool selectedSeen = false;
            for (int i = 0; i < entries.Count; i++)
            {
                string address;
                if (!NormalizeAddress(entries[i].Address, out address)) continue;
                if (entries[i].Selected && !selectedSeen)
                {
                    text.Append('*');
                    selectedSeen = true;
                }
                text.Append(SafeName(entries[i].Name)).Append('\t').Append(address).Append('\n');
            }
            return text.ToString();
        }

        static string SafeName(string name)
        {
            name = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            // Leading format markers cannot be represented literally in an unselected name.
            if (name.Length > 0 && (name[0] == '*' || name[0] == '#' || name[0] == '\ufeff')) name = " " + name;
            return name;
        }

        static bool Same(List<Entry> a, List<Entry> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name || a[i].Address != b[i].Address || a[i].Selected != b[i].Selected) return false;
            return true;
        }

        static string CurrentOf(List<Entry> entries)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Selected) return entries[i].Address;
            return entries.Count == 0 ? "" : entries[0].Address;
        }
    }
}
