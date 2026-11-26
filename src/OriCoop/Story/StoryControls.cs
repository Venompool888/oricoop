using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop.Story
{
    // One start token may run several text or skip actions, including the same action more than once.
    // Only the host that currently owns this session can lease control of one such native action.
    internal sealed class StoryControls
    {
        internal const int Native = -2;
        internal const int Closed = -1;
        internal const int MaxActionKey = 2048;
        internal const float Lease = 5;
        sealed class Entry { internal int Page; internal float Last; }
        readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        readonly List<string> expired = new List<string>();
        int authority;

        // BinaryWriter.Write(string) uses a 7-bit encoded UTF-8 byte length. Bound the declared
        // size against this exact packet before allocating or decoding a guest's request.
        internal static bool ReadAction(BinaryReader reader, out string key, out int page)
        {
            key = null; page = 0;
            Stream stream = reader.BaseStream;
            int size = 0;
            for (int shift = 0; shift <= 7 && stream.Position < stream.Length; shift += 7)
            {
                int part = reader.ReadByte();
                size |= (part & 127) << shift;
                if ((part & 128) != 0) continue;
                if (size == 0 || size > 4 * MaxActionKey || stream.Length - stream.Position != size + 4) return false;
                key = Encoding.UTF8.GetString(reader.ReadBytes(size));
                page = reader.ReadInt32();
                return key.Length <= MaxActionKey;
            }
            return false;
        }

        internal static bool AcceptRequest(string activeKey, int activePage, string key, int page, ref int shownPage)
        {
            if (activeKey != key || page < 0 || activePage != page || shownPage == page) return false;
            shownPage = page; return true;
        }

        internal static string Key(int owner, uint run, string action, int occurrence)
        { return owner + "/" + run + "/" + occurrence + "/" + action; }

        internal bool Receive(int sender, int host, string key, int page, float now)
        {
            if (host <= 0 || sender != host || string.IsNullOrEmpty(key) || key.Length > MaxActionKey || page < Native || page > 4096)
                return false;
            if (authority != host) { entries.Clear(); authority = host; }
            Entry entry;
            if (!entries.TryGetValue(key, out entry))
            {
                if (entries.Count >= 64) return false;
                entries.Add(key, entry = new Entry());
            }
            if (page != Native && (entry.Page == Closed || page >= 0 && page < entry.Page)) return false;
            entry.Page = page; entry.Last = now;
            return true;
        }

        internal bool TryGet(int host, string key, float now, out int page)
        {
            page = 0;
            Entry entry;
            if (authority != host || key == null || !entries.TryGetValue(key, out entry) || now - entry.Last >= Lease) return false;
            page = entry.Page; return true;
        }

        // Once this host controls a live action, delayed heartbeats cannot grant local input.
        // The short initial wait still allows an older host that never claims this action.
        internal bool ControlsAction(int host, string key, float now, float matchedAt, ref int confirmedHost, out int page)
        {
            if (confirmedHost != host) confirmedHost = 0;
            if (TryGet(host, key, now, out page))
            {
                confirmedHost = page == Native ? 0 : host;
                return page != Native;
            }
            return confirmedHost != 0 || now - matchedAt < Lease;
        }

        internal void Expire(int host, float now)
        {
            if (authority != host) { Clear(); return; }
            if (entries.Count == 0) return;
            expired.Clear();
            foreach (var item in entries) if (now - item.Value.Last >= Lease) expired.Add(item.Key);
            foreach (string key in expired) entries.Remove(key);
        }
        internal void Clear() { entries.Clear(); authority = 0; }
    }
}
