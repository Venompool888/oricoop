using System;
using System.Text;

namespace OriCoop.Ui
{
    // T-046: combination-lock style digit dials for the IP address, the port and the room code (no text field, so no
    // IME problems). Pure state; NativeLobby reads the keys / pad and draws it.
    //   Up / Down    turn the focused wheel (9 -> 0 and 0 -> 9 wrap around)
    //   Left / Right move to the neighbouring wheel (stops at the ends)
    //   digit key    sets the focused wheel and moves right
    //   Backspace    sets the focused wheel to 0 and moves left
    // Groups are drawn with separators: IP = four groups of 3 digits, port = 5, room code = two groups of 3.
    public sealed class DigitDial
    {
        readonly int[] m_groups;
        readonly string m_separator;
        readonly int[] m_digits;
        public int Focus { get; private set; }
        public int Count { get { return m_digits.Length; } }

        public DigitDial(int[] groups, string separator)
        {
            if (groups == null || groups.Length == 0) throw new ArgumentException("groups");
            m_groups = (int[])groups.Clone(); m_separator = separator ?? "";
            int n = 0; foreach (int g in m_groups) { if (g <= 0) throw new ArgumentException("groups"); n += g; }
            m_digits = new int[n];
        }

        public static DigitDial Ip() { return new DigitDial(new[] { 3, 3, 3, 3 }, "."); }
        public static DigitDial Port() { return new DigitDial(new[] { 5 }, ""); }
        public static DigitDial Room() { return new DigitDial(new[] { 3, 3 }, " "); }

        public int Digit(int i) { return m_digits[i]; }

        public void Up() { m_digits[Focus] = (m_digits[Focus] + 1) % 10; }
        public void Down() { m_digits[Focus] = (m_digits[Focus] + 9) % 10; }
        public void Left() { if (Focus > 0) Focus--; }
        public void Right() { if (Focus < m_digits.Length - 1) Focus++; }
        public void Type(int digit)
        {
            if (digit < 0 || digit > 9) return;
            m_digits[Focus] = digit; Right();
        }
        public void Back() { m_digits[Focus] = 0; Left(); }
        public void Home() { Focus = 0; }

        // Group g as a string of its digits ("007").
        public string Group(int g)
        {
            int start = 0; for (int i = 0; i < g; i++) start += m_groups[i];
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < m_groups[g]; i++) sb.Append((char)('0' + m_digits[start + i]));
            return sb.ToString();
        }
        public int Groups { get { return m_groups.Length; } }

        // All digits, no separators ("192168001023").
        public string Digits
        {
            get { StringBuilder sb = new StringBuilder(); foreach (int d in m_digits) sb.Append((char)('0' + d)); return sb.ToString(); }
        }

        // Each group's number without leading zeros (an address a.b.c.d, a port, the room code keeps all six digits).
        public string Numbers(string separator)
        {
            StringBuilder sb = new StringBuilder();
            for (int g = 0; g < m_groups.Length; g++)
            {
                if (g > 0) sb.Append(separator);
                sb.Append(int.Parse(Group(g)).ToString());
            }
            return sb.ToString();
        }

        // Fill from text: every group takes the digits of the matching part (split on any non-digit), right-aligned and
        // zero-padded (a.b.c.d with short parts -> each part padded to 3 digits). With one group, all digits of the text in order. Too many
        // digits for a group keep the last ones. Focus returns to the first wheel.
        public void Load(string text)
        {
            Array.Clear(m_digits, 0, m_digits.Length); Focus = 0;
            if (string.IsNullOrEmpty(text)) return;
            string[] parts;
            if (m_groups.Length == 1) parts = new[] { OnlyDigits(text) };
            else if (m_separator.Trim().Length == 0) { string all = OnlyDigits(text); parts = SplitFixed(all); }
            else parts = text.Split(m_separator.Trim()[0]);
            int start = 0;
            for (int g = 0; g < m_groups.Length; g++)
            {
                string p = g < parts.Length ? OnlyDigits(parts[g]) : "";
                if (p.Length > m_groups[g]) p = p.Substring(p.Length - m_groups[g]);
                int pad = m_groups[g] - p.Length;
                for (int i = 0; i < p.Length; i++) m_digits[start + pad + i] = p[i] - '0';
                start += m_groups[g];
            }
        }

        string[] SplitFixed(string all)
        {
            string[] parts = new string[m_groups.Length]; int at = 0;
            for (int g = 0; g < m_groups.Length; g++)
            {
                int take = Math.Max(0, Math.Min(m_groups[g], all.Length - at));
                parts[g] = all.Substring(at, take).PadRight(m_groups[g], '0'); at += take;   // a code fills from the left, as typed
            }
            return parts;
        }

        static string OnlyDigits(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "") if (c >= '0' && c <= '9') sb.Append(c);
            return sb.ToString();
        }

        // The dial as text with the focused wheel passed through mark (when focused), groups joined by the separator.
        public string Render(bool focused, Func<string, string> mark)
        {
            StringBuilder sb = new StringBuilder(); int i = 0;
            for (int g = 0; g < m_groups.Length; g++)
            {
                if (g > 0) sb.Append(m_separator);
                for (int k = 0; k < m_groups[g]; k++, i++)
                {
                    string c = ((char)('0' + m_digits[i])).ToString();
                    sb.Append(focused && i == Focus && mark != null ? mark(c) : c);
                }
            }
            return sb.ToString();
        }

        // Rules for the values the dials produce.
        public static bool ValidIp(DigitDial d)
        {
            for (int g = 0; g < d.Groups; g++) if (int.Parse(d.Group(g)) > 255) return false;
            return d.Digits.Trim('0').Length > 0;   // 0.0.0.0 is not an address to join
        }
        public static bool ValidPort(DigitDial d) { int p = int.Parse(d.Digits); return p >= 1 && p <= 65535; }
    }
}
