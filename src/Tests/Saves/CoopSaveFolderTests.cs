using System;
using System.IO;
using OriCoop.Saves;

partial class SaveTests
{
    static void FolderPathTests()
    {
        string original = Path.Combine("single", "saveFile2.sav");
        Check(CoopSaveFolder.MapPath(original, false, "coop") == original, "inactive path changed");
        Check(CoopSaveFolder.MapPath(original, true, "coop") == Path.Combine("coop", "saveFile2.sav"), "slot not mapped");
        Check(CoopSaveFolder.MapPath(Path.Combine("single", "saveFile9_bkup4.sav"), true, "coop") == Path.Combine("coop", "saveFile9_bkup4.sav"), "backup name changed");
        Check(CoopSaveFolder.MapPath(null, true, "coop") == null, "null path changed");
        Check(CoopSaveFolder.MapPath("", true, "coop") == "", "empty path changed");
        Check(CoopSaveFolder.MapPath(null, false, "coop") == null, "inactive null changed");
        Check(CoopSaveFolder.MapPath("", false, "coop") == "", "inactive empty changed");
    }

    static void EmptyCoopSlotTests()
    {
        Check(CoopSaveFolder.FirstEmpty(new bool[10]) == 0, "empty folder must pick slot zero");
        Check(CoopSaveFolder.FirstEmpty(new[] { true, true, false, true }) == 2, "first gap not chosen");
        Check(CoopSaveFolder.FirstEmpty(new[] { true, true }) == -1, "full folder has a free slot");
        Check(CoopSaveFolder.FirstEmpty(new bool[0]) == -1, "empty slot range has a free slot");
    }
}
