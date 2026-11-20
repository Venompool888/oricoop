using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop.World
{
    // T-168: the two world save objects that hold MANY independent things are split into items, so two players changing
    // different things in the same RTT no longer overwrite each other (before, the whole object was sent and replaced).
    //  - GameController (decomp GameController.Serialize:426-443): WorldEventsManager GUID -> int, the string-trigger list,
    //    a local tail (0f, GameTime, 0, 0, RequireInitialValues: 17 bytes, never shared), the objective index list.
    //    Items: "e:<guid hex>" -> int (4 bytes), "t:<trigger>" -> count, "o:<index>" -> count.
    //  - GameWorld (decomp GameWorld.Serialize:157 + RuntimeGameWorldArea.Serialize:217): per area a state dict int -> int
    //    and an icon list GUID -> int, then the revealed icon GUIDs, then one int.
    //    Items: "a<i>.s<key>" -> int, "a<i>.i<guid hex>" -> int, "r:<guid hex>" -> count, "z" -> int.
    // Unity-free (BinaryReader over the Archive bytes: Archive is a BinaryWriter/Reader) so the unit tests run it.
    public static class WorldItems
    {
        // Same numbers as WorldSync.Meaning (Raw, Pickup, Controller, ... Map = 9); pinned by the unit tests.
        public const byte SchemaController = 2, SchemaMap = 9;
        const int TailBytes = 17, MaxCount = 200000;

        public static bool IsAggregate(byte schema) { return schema == SchemaController || schema == SchemaMap; }

        // Monotonic items: a revealed map icon never becomes hidden again through a live change (only a full snapshot,
        // e.g. the host's checkpoint rollback, can do that).
        public static bool NeverRemoved(string key) { return key.StartsWith("r:"); }

        static int Count(BinaryReader r)
        {
            int count = r.ReadInt32();
            if (count < 0 || count > MaxCount) throw new InvalidDataException("invalid world item count");
            return count;
        }

        static byte[] Int(int value) { return BitConverter.GetBytes(value); }
        static int ToInt(byte[] value) { return value != null && value.Length == 4 ? BitConverter.ToInt32(value, 0) : 0; }

        static string Hex(byte[] bytes)
        {
            StringBuilder s = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) s.Append(b.ToString("x2"));
            return s.ToString();
        }

        static byte[] FromHex(string hex)
        {
            if (hex.Length != 32) throw new InvalidDataException("invalid GUID item key");
            byte[] bytes = new byte[16];
            for (int i = 0; i < 16; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        static byte[] Guid(BinaryReader r)
        {
            byte[] guid = r.ReadBytes(16);
            if (guid.Length != 16) throw new EndOfStreamException();
            return guid;
        }

        static void AddCount(SortedDictionary<string, byte[]> items, string key)
        {
            byte[] old;
            items[key] = Int(items.TryGetValue(key, out old) ? ToInt(old) + 1 : 1);
        }

        // ---------- parsed forms (keep the template's order so a rewrite changes as little as possible) ----------

        sealed class Controller
        {
            public readonly List<KeyValuePair<string, int>> Events = new List<KeyValuePair<string, int>>();
            public readonly List<string> Triggers = new List<string>();
            public byte[] Tail;
            public readonly List<int> Objectives = new List<int>();
        }

        sealed class Area
        {
            public readonly List<KeyValuePair<int, int>> States = new List<KeyValuePair<int, int>>();
            public readonly List<KeyValuePair<string, int>> Icons = new List<KeyValuePair<string, int>>();
        }

        sealed class Map
        {
            public readonly List<Area> Areas = new List<Area>();
            public readonly List<string> Revealed = new List<string>();
            public int Last;
        }

        static Controller ReadController(byte[] bytes)
        {
            Controller c = new Controller();
            using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
            {
                int events = Count(r);
                for (int i = 0; i < events; i++) { string id = Hex(Guid(r)); c.Events.Add(new KeyValuePair<string, int>(id, r.ReadInt32())); }
                int triggers = Count(r);
                for (int i = 0; i < triggers; i++) c.Triggers.Add(r.ReadString());
                c.Tail = r.ReadBytes(TailBytes);
                if (c.Tail.Length != TailBytes) throw new EndOfStreamException();
                int objectives = Count(r);
                for (int i = 0; i < objectives; i++) c.Objectives.Add(r.ReadInt32());
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("unrecognized GameController suffix");
            }
            return c;
        }

        static Map ReadMap(byte[] bytes)
        {
            Map m = new Map();
            using (BinaryReader r = new BinaryReader(new MemoryStream(bytes)))
            {
                int areas = Count(r);
                for (int a = 0; a < areas; a++)
                {
                    Area area = new Area();
                    int states = Count(r);
                    for (int i = 0; i < states; i++) { int key = r.ReadInt32(); area.States.Add(new KeyValuePair<int, int>(key, r.ReadInt32())); }
                    int icons = Count(r);
                    for (int i = 0; i < icons; i++) { string id = Hex(Guid(r)); area.Icons.Add(new KeyValuePair<string, int>(id, r.ReadInt32())); }
                    m.Areas.Add(area);
                }
                int revealed = Count(r);
                for (int i = 0; i < revealed; i++) m.Revealed.Add(Hex(Guid(r)));
                m.Last = r.ReadInt32();
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("unrecognized GameWorld suffix");
            }
            return m;
        }

        public static SortedDictionary<string, byte[]> Decompose(byte schema, byte[] bytes)
        {
            SortedDictionary<string, byte[]> items = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            if (schema == SchemaController)
            {
                Controller c = ReadController(bytes);
                foreach (KeyValuePair<string, int> e in c.Events) items["e:" + e.Key] = Int(e.Value);
                foreach (string t in c.Triggers) AddCount(items, "t:" + t);
                foreach (int o in c.Objectives) AddCount(items, "o:" + o);
            }
            else if (schema == SchemaMap)
            {
                Map m = ReadMap(bytes);
                for (int a = 0; a < m.Areas.Count; a++)
                {
                    foreach (KeyValuePair<int, int> s in m.Areas[a].States) items["a" + a + ".s" + s.Key] = Int(s.Value);
                    foreach (KeyValuePair<string, int> i in m.Areas[a].Icons) items["a" + a + ".i" + i.Key] = Int(i.Value);
                }
                foreach (string g in m.Revealed) AddCount(items, "r:" + g);
                items["z"] = Int(m.Last);
            }
            else throw new ArgumentException("not an aggregate world schema: " + schema);
            return items;
        }

        // Rewrites `template` (this PC's current bytes) so its items equal `items`. Local parts (the controller tail, the
        // number of map areas) come from the template; items of a map area this PC does not have are ignored.
        public static byte[] Compose(byte schema, byte[] template, IDictionary<string, byte[]> items)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                if (schema == SchemaController) WriteController(w, ReadController(template), items);
                else if (schema == SchemaMap) WriteMap(w, ReadMap(template), items);
                else throw new ArgumentException("not an aggregate world schema: " + schema);
                w.Flush(); return stream.ToArray();
            }
        }

        // Keys with `prefix`, in template order first, then the new ones (sorted).
        static List<string> Ordered(IEnumerable<string> templateKeys, IDictionary<string, byte[]> items, string prefix)
        {
            List<string> keys = new List<string>(); HashSet<string> seen = new HashSet<string>();
            foreach (string key in templateKeys) if (items.ContainsKey(prefix + key) && seen.Add(key)) keys.Add(key);
            List<string> extra = new List<string>();
            foreach (string full in items.Keys)
                if (full.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string key = full.Substring(prefix.Length);
                    if (!seen.Contains(key)) extra.Add(key);
                }
            extra.Sort(StringComparer.Ordinal); keys.AddRange(extra);
            return keys;
        }

        static void WriteController(BinaryWriter w, Controller t, IDictionary<string, byte[]> items)
        {
            List<string> events = Ordered(t.Events.ConvertAll(e => e.Key), items, "e:");
            w.Write(events.Count);
            foreach (string id in events) { w.Write(FromHex(id)); w.Write(ToInt(items["e:" + id])); }
            List<string> triggers = Ordered(t.Triggers, items, "t:");
            List<string> expanded = new List<string>();
            foreach (string s in triggers) for (int n = ToInt(items["t:" + s]); n > 0; n--) expanded.Add(s);
            w.Write(expanded.Count); foreach (string s in expanded) w.Write(s);
            w.Write(t.Tail);
            List<string> objectives = Ordered(t.Objectives.ConvertAll(o => o.ToString()), items, "o:");
            List<int> list = new List<int>();
            foreach (string o in objectives) for (int n = ToInt(items["o:" + o]); n > 0; n--) list.Add(int.Parse(o));
            w.Write(list.Count); foreach (int o in list) w.Write(o);
        }

        static void WriteMap(BinaryWriter w, Map t, IDictionary<string, byte[]> items)
        {
            w.Write(t.Areas.Count);
            for (int a = 0; a < t.Areas.Count; a++)
            {
                string prefix = "a" + a + ".s";
                List<string> states = Ordered(t.Areas[a].States.ConvertAll(s => s.Key.ToString()), items, prefix);
                w.Write(states.Count);
                foreach (string key in states) { w.Write(int.Parse(key)); w.Write(ToInt(items[prefix + key])); }
                prefix = "a" + a + ".i";
                List<string> icons = Ordered(t.Areas[a].Icons.ConvertAll(i => i.Key), items, prefix);
                w.Write(icons.Count);
                foreach (string id in icons) { w.Write(FromHex(id)); w.Write(ToInt(items[prefix + id])); }
            }
            List<string> revealed = Ordered(t.Revealed, items, "r:");
            List<string> expanded = new List<string>();
            foreach (string g in revealed) for (int n = ToInt(items["r:" + g]); n > 0; n--) expanded.Add(g);
            w.Write(expanded.Count); foreach (string g in expanded) w.Write(FromHex(g));
            byte[] last;
            w.Write(items.TryGetValue("z", out last) ? ToInt(last) : t.Last);
        }
    }
}
