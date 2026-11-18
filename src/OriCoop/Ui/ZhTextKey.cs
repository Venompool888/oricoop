using System.Security.Cryptography;
using System.Text;

namespace OriCoop.Ui
{
    // zh_extra.txt is keyed by a hash of the game's English line, so the published data file holds only our Chinese
    // text, never the game's English (owner's choice, T-052). Key = first 16 hex digits (lower case) of SHA-1 over the
    // UTF-8 bytes of the English text exactly as the game stores it (after file unescaping). tools/zh-fonts and
    // src/assets/zh-text/hash-zh-extra.py compute the same key.
    internal static class ZhTextKey
    {
        internal static string Of(string english)
        {
            byte[] hash;
            using (SHA1 sha = SHA1.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(english ?? ""));
            StringBuilder sb = new StringBuilder(16);
            for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
            return sb.ToString();
        }

        internal static bool IsKey(string s)
        {
            if (s == null || s.Length != 16) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }
}
