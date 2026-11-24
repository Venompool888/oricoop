using System;
using System.IO;
using OriCoop.Rooms;

// StoreVersion prunes history/vN.sav: a stray file matching v*.sav must not make it throw.
static class RoomHistoryTests
{
    static int s_failed;

    static void Check(bool ok, string what)
    {
        if (ok) Console.WriteLine("ok " + what);
        else { Console.WriteLine("FAIL " + what); s_failed++; }
    }

    static void StrayHistoryFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "oricoop-roomhistory-" + Guid.NewGuid().ToString("N"));
        try
        {
            RoomLocalStore store = new RoomLocalStore(root);
            store.StoreVersion("r1", "s1", 1, new byte[] { 1 }, "one");
            string history = Path.Combine(Path.Combine(Path.Combine(Path.Combine(root, "r1"), "saves"), "s1"), "history");
            // What Windows Explorer names a copy made in the same folder; also a zero-padded name.
            File.WriteAllBytes(Path.Combine(history, "v1 - Copy.sav"), new byte[] { 9 });
            File.WriteAllBytes(Path.Combine(history, "v007.sav"), new byte[] { 9 });
            string error = null;
            for (long v = 2; v <= 13 && error == null; v++)
                try { store.StoreVersion("r1", "s1", v, new byte[] { (byte)v }, "v" + v); }
                catch (Exception e) { error = e.GetType().Name + ": " + e.Message; }
            Check(error == null, "a stray v*.sav file does not make StoreVersion throw" + (error == null ? "" : " (" + error + ")"));
            long version; string summary;
            byte[] latest = store.LoadLatest("r1", "s1", out version, out summary);
            Check(latest != null && latest.Length == 1 && latest[0] == 13 && version == 13 && summary == "v13", "the latest version is stored");
            int ours = 0;
            foreach (string f in Directory.GetFiles(history, "v*.sav"))
            {
                string n = Path.GetFileName(f);
                if (n != "v1 - Copy.sav" && n != "v007.sav") ours++;
            }
            Check(ours == 10, "history keeps the last 10 of its own versions (" + ours + ")");
            Check(File.Exists(Path.Combine(history, "v13.sav")) && File.Exists(Path.Combine(history, "v4.sav")) && !File.Exists(Path.Combine(history, "v3.sav")),
                "the oldest of its own versions are pruned");
            Check(File.Exists(Path.Combine(history, "v1 - Copy.sav")) && File.Exists(Path.Combine(history, "v007.sav")), "foreign files are left alone");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    static int Main()
    {
        StrayHistoryFile();
        Console.WriteLine(s_failed == 0 ? "ALL OK" : s_failed + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }
}
