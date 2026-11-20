using System.Text;

namespace OriCoop.Ui
{
    // Text shown in the game's MessageBox goes through MessageParserUtility: # * $ @ ^ toggle colours / small text,
    // [Name] is a placeholder and <...> a tag. Names that come from players or servers (room names, nicks, save names
    // made by older clients) must not switch colours on the rest of the row (T-136): drop those characters for display.
    public static class MarkupText
    {
        public static bool IsMarkup(char c)
        {
            return c == '#' || c == '*' || c == '$' || c == '@' || c == '^' || c == '[' || c == ']' || c == '<' || c == '>';
        }

        public static bool HasMarkup(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (IsMarkup(c)) return true;
            return false;
        }

        public static string Plain(string s)
        {
            if (!HasMarkup(s)) return s ?? "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!IsMarkup(c)) sb.Append(c);
            // "room #2" -> "room 2", but no double spaces where a lone marker stood between words.
            return sb.ToString().Replace("  ", " ").Trim();
        }
    }
}
