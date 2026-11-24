using System;
using System.Collections.Generic;
using System.IO;
using OriCoop.Rooms;

// ListRooms must skip a torn or foreign room record instead of failing the whole list (the 我的房间 page).
static class RoomListTests
{
    static int s_failed;

    static void Check(bool ok, string what)
    {
        if (ok) Console.WriteLine("ok " + what);
        else { Console.WriteLine("FAIL " + what); s_failed++; }
    }

    static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "oricoop-roomlist-" + Guid.NewGuid().ToString("N"));
        return root;
    }

    static RoomBackupInfo Room(string id)
    {
        RoomBackupInfo info = new RoomBackupInfo();
        info.Server = "srv"; info.Id = id; info.Name = "room " + id; info.Role = "1"; info.Steam = "7";
        return info;
    }

    static List<RoomBackupInfo> List(RoomLocalStore store, out string error)
    {
        error = null;
        try { return store.ListRooms(); }
        catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
    }

    static void TornRecordSkipped()
    {
        string root = NewRoot();
        try
        {
            RoomLocalStore store = new RoomLocalStore(root);
            store.SaveRoom(Room("r1"));
            store.SaveRoom(Room("r2"));
            // A power cut on NTFS can leave a replaced file zero-filled; a crash can leave it cut short.
            File.WriteAllBytes(Path.Combine(Path.Combine(root, "r2"), "room.dat"), new byte[64]);
            Directory.CreateDirectory(Path.Combine(root, "r3"));
            byte[] good = File.ReadAllBytes(Path.Combine(Path.Combine(root, "r1"), "room.dat"));
            byte[] cut = new byte[good.Length / 2]; Buffer.BlockCopy(good, 0, cut, 0, cut.Length);
            File.WriteAllBytes(Path.Combine(Path.Combine(root, "r3"), "room.dat"), cut);
            string error;
            List<RoomBackupInfo> rooms = List(store, out error);
            Check(error == null, "a torn room.dat does not fail the room list" + (error == null ? "" : " (" + error + ")"));
            Check(rooms != null && rooms.Count == 1 && rooms[0].Id == "r1", "the readable room is still listed");
            string id, server;
            bool last = false; error = null;
            store.SaveLastRoom("r1", "srv");
            try { last = store.LoadLastRoom(out id, out server); } catch (Exception e) { error = e.Message; }
            Check(error == null && last, "the last played room is still found next to a torn record");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    static void EmptyRecordSkipped()
    {
        string root = NewRoot();
        try
        {
            RoomLocalStore store = new RoomLocalStore(root);
            store.SaveRoom(Room("r1"));
            // An empty file decodes to no fields: a room without an id could be neither entered nor deleted.
            Directory.CreateDirectory(Path.Combine(root, "r9"));
            File.WriteAllBytes(Path.Combine(Path.Combine(root, "r9"), "room.dat"), new byte[0]);
            string error;
            List<RoomBackupInfo> rooms = List(store, out error);
            Check(error == null && rooms != null && rooms.Count == 1 && rooms[0].Id == "r1",
                "an empty room.dat is not listed as a room without an id");
            bool deleted = true;
            foreach (RoomBackupInfo room in rooms)
                try { store.DeleteRoom(room.Id); } catch (Exception) { deleted = false; }
            Check(deleted, "every listed room can be deleted");
        }
        finally { try { Directory.Delete(root, true); } catch (Exception) { } }
    }

    static int Main()
    {
        TornRecordSkipped();
        EmptyRecordSkipped();
        Console.WriteLine(s_failed == 0 ? "ALL OK" : s_failed + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }
}
