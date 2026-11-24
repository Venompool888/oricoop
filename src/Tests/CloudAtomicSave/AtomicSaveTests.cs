using System;
using System.IO;
using OriCoop.Saves;

// CoopSaveFolder.ReplaceWith: how the co-op slot file, its .oricoop link and its .skills sidecar go to disk.
static class AtomicSaveTests
{
    static int s_failed;

    static void Check(bool ok, string what)
    {
        if (ok) Console.WriteLine("ok " + what);
        else { Console.WriteLine("FAIL " + what); s_failed++; }
    }

    static string Text(string path) { return File.Exists(path) ? File.ReadAllText(path) : null; }

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "oricoop-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "saveFile3.sav"), tmp = path + ".oricoop-tmp";

            File.WriteAllText(tmp, "first");
            CoopSaveFolder.ReplaceWith(tmp, path);
            Check(Text(path) == "first" && !File.Exists(tmp), "a new file is moved into place");

            File.WriteAllText(tmp, "second");
            CoopSaveFolder.ReplaceWith(tmp, path);
            Check(Text(path) == "second" && !File.Exists(tmp), "an existing file is replaced");

            // The move of the temp file cannot happen (here: it is gone; in the game: held open by a scanner).
            // The old save must still be there: Delete + Move had already deleted it.
            bool threw = false;
            try { CoopSaveFolder.ReplaceWith(tmp, path); } catch (IOException) { threw = true; }
            Check(threw, "a replace that cannot happen is reported");
            Check(Text(path) == "second", "the existing save survives a replace that could not happen");
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        Console.WriteLine(s_failed == 0 ? "ALL OK" : s_failed + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }
}
