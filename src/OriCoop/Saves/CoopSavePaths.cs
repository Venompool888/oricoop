using System.IO;

namespace OriCoop.Saves
{
    public static partial class CoopSaveFolder
    {
        public static string MapPath(string original, bool active, string coopDir)
        {
            if (!active || string.IsNullOrEmpty(original)) return original;
            return Path.Combine(coopDir, Path.GetFileName(original));
        }

        // Moves a fully written temp file over `path`. An existing file is swapped in one step (File.Replace): with
        // Delete + Move, a Move that failed after the Delete (the temp file still held open by a virus scanner, a crash
        // in between) left no save at all, only the temp file the game never reads.
        public static void ReplaceWith(string tmp, string path)
        {
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static int FirstEmpty(bool[] occupied)
        {
            for (int i = 0; i < occupied.Length; i++)
                if (!occupied[i]) return i;
            return -1;
        }
    }
}
