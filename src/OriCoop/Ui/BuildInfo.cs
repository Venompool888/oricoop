using System;
using System.IO;
using System.Reflection;

namespace OriCoop.Ui
{
    // Which build is running (T-032): the plugin version plus the git commit the DLL was built from. The build scripts
    // (src\build.ps1, src\build-commit.ps1) embed a one-line resource "OriCoop.build.txt" = "commit=<short sha>[+local]"
    // ("+local" = built from a working tree with uncommitted changes under src\OriCoop). A DLL built without it (an IDE,
    // an old script) says "unknown". Shown on the title screen and in the F8 panel so a playtest report names the build.
    public static class BuildInfo
    {
        public const string ResourceName = "OriCoop.build.txt";
        public const string Unknown = "unknown";
        static string s_commit;

        // "commit=abc1234" -> "abc1234". Anything else (missing, empty, malformed, not hex) -> Unknown.
        public static string ParseCommit(string text)
        {
            if (string.IsNullOrEmpty(text)) return Unknown;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("commit=")) continue;
                string value = line.Substring(7).Trim();
                string sha = value.EndsWith("+local") ? value.Substring(0, value.Length - 6) : value;
                if (sha.Length < 7 || sha.Length > 40) return Unknown;
                foreach (char c in sha)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return Unknown;
                return value;
            }
            return Unknown;
        }

        // The commit this DLL was built from (read once from the embedded resource).
        public static string Commit
        {
            get
            {
                if (s_commit != null) return s_commit;
                string text = null;
                try
                {
                    Stream s = typeof(BuildInfo).Assembly.GetManifestResourceStream(ResourceName);
                    if (s != null) using (StreamReader r = new StreamReader(s)) text = r.ReadToEnd();
                }
                catch (Exception) { text = null; }   // no resource = an unknown build, never a crash
                s_commit = ParseCommit(text);
                return s_commit;
            }
        }

        // "0.2.0 (abc1234)" — the {0}/{1} arguments of the "build.version" text.
        public static string Short(string version, string commit)
        {
            return (string.IsNullOrEmpty(version) ? "?" : version) + " (" + (string.IsNullOrEmpty(commit) ? Unknown : commit) + ")";
        }
    }
}
