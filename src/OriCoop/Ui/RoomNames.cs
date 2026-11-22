using System;
using System.Text;

namespace OriCoop.Ui
{
    // T-171: room names are English only. A room the player did not name gets two Ori-style words,
    // one from each list ("Glimmer Hollow"). Player-typed names are cut to printable ASCII by Clean.
    internal static class RoomNames
    {
        public const int MaxLength = 24;

        internal static readonly string[] First = {
            "Glimmer", "Spirit", "Ember", "Misty", "Sunken", "Moonlit", "Silent", "Whisper", "Hollow", "Thorn",
            "Lumen", "Ginso", "Horu", "Valley", "Gentle", "Starlit", "Wild", "Amber", "Silver", "Forlorn",
        };
        internal static readonly string[] Last = {
            "Hollow", "Glades", "Grove", "Ruins", "Wells", "Light", "Woods", "Marsh", "Peak", "Tree",
            "Lantern", "Spring", "Bloom", "Ridge", "Path", "Shrine", "Flame", "Haven", "Wisp", "Falls",
        };

        public static string Random(Random random)
        {
            string a = First[random.Next(First.Length)], b;
            do b = Last[random.Next(Last.Length)]; while (b == a);
            return a + " " + b;
        }

        // Printable ASCII only (no Chinese, no control or markup characters), spaces collapsed, at most MaxLength.
        public static string Clean(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name ?? "")
            {
                bool ok = c >= ' ' && c <= '~' && c != '*' && c != '<' && c != '>' && c != ';' && c != '=' && c != '#';
                if (!ok) continue;
                if (c == ' ' && (sb.Length == 0 || sb[sb.Length - 1] == ' ')) continue;
                sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length > MaxLength ? s.Substring(0, MaxLength).TrimEnd() : s;
        }
    }
}
