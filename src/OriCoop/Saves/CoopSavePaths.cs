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

        public static int FirstEmpty(bool[] occupied)
        {
            for (int i = 0; i < occupied.Length; i++)
                if (!occupied[i]) return i;
            return -1;
        }
    }
}
