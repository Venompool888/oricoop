using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OriCoop.Rooms
{
    public sealed class RoomBackupMember
    {
        public int Seat;
        public string Steam;
        public string Nick;
    }

    public sealed class RoomBackupSave
    {
        public string Id;
        public string Name;
        public string Summary;
        public long Version;
    }

    public sealed class RoomBackupInfo
    {
        public string Server;
        public string Id;
        public string Name;
        public string Role;
        public string Steam;
        public int Seats = 2;
        public bool Solo;
        public List<RoomBackupMember> Members = new List<RoomBackupMember>();
        public List<RoomBackupSave> Saves = new List<RoomBackupSave>();

        public static RoomBackupInfo FromInfoReply(RoomReply reply)
        {
            return Read(reply.Fields);
        }

        internal static RoomBackupInfo Read(RoomWire.Fields f)
        {
            RoomBackupInfo info = new RoomBackupInfo
            {
                Server = f.GetString("server"),
                Id = f.GetString("id"),
                Name = f.GetString("name"),
                Role = f.GetString("role"),
                Steam = f.GetString("steam"),
                Seats = (int)f.GetLong("seats", 2),
                Solo = f.GetString("solo") == "1"
            };
            if (info.Id.Length == 0)
            {
                info.Id = f.GetString("room");
            }
            for (int i = 0; i < Math.Min(f.GetLong("m.count", 0), 16); i++)
            {
                string p = "m." + i + ".";
                info.Members.Add(new RoomBackupMember
                {
                    Seat = (int)f.GetLong(p + "seat", 0),
                    Steam = f.GetString(p + "steam"),
                    Nick = f.GetString(p + "nick")
                });
            }
            for (int i = 0; i < Math.Min(f.GetLong("save.count", 0), 256); i++)
            {
                string p = "save." + i + ".";
                info.Saves.Add(new RoomBackupSave
                {
                    Id = f.GetString(p + "id"),
                    Name = f.GetString(p + "name"),
                    Version = f.GetLong(p + "version", 0),
                    Summary = f.GetString(p + "summary")
                });
            }
            return info;
        }

        internal void WriteMembers(RoomWire.Fields f)
        {
            f.Set("m.count", Members.Count);
            for (int i = 0; i < Members.Count; i++)
            {
                string p = "m." + i + ".";
                f.Set(p + "seat", Members[i].Seat);
                f.Set(p + "steam", Members[i].Steam);
                f.Set(p + "nick", Members[i].Nick);
            }
        }

        internal RoomWire.Fields Encode()
        {
            RoomWire.Fields f = new RoomWire.Fields();
            f.Set("server", Server);
            f.Set("id", Id);
            f.Set("name", Name);
            f.Set("role", Role);
            f.Set("steam", Steam);
            f.Set("seats", Seats);
            f.Set("solo", Solo ? "1" : "0");
            WriteMembers(f);
            f.Set("save.count", Saves.Count);
            for (int i = 0; i < Saves.Count; i++)
            {
                string p = "save." + i + ".";
                RoomBackupSave save = Saves[i];
                f.Set(p + "id", save.Id);
                f.Set(p + "name", save.Name);
                f.Set(p + "version", save.Version);
                f.Set(p + "summary", save.Summary);
            }
            return f;
        }
    }

    public sealed class RoomLocalStore
    {
        readonly string root;

        public RoomLocalStore(string rootFolder)
        {
            root = rootFolder;
            Directory.CreateDirectory(root);
        }

        // Room and save ids come from the rooms server and become folder names: only plain ids (the server makes
        // "r"/"s" + hex) are accepted, so a hostile server cannot write or delete outside the store ("..\..", "C:\").
        static string Id(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) throw new ArgumentException("bad room/save id");
            foreach (char c in id)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    throw new ArgumentException("bad room/save id");
            return id;
        }

        public void SaveRoom(RoomBackupInfo info)
        {
            RoomWire.AtomicWrite(Path.Combine(Path.Combine(root, Id(info.Id)), "room.dat"), info.Encode().Encode());
        }

        public List<RoomBackupInfo> ListRooms()
        {
            List<RoomBackupInfo> list = new List<RoomBackupInfo>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                string path = Path.Combine(directory, "room.dat");
                if (File.Exists(path))
                {
                    list.Add(RoomBackupInfo.Read(RoomWire.Fields.Decode(File.ReadAllBytes(path))));
                }
            }
            return list;
        }

        // T-095: the room played last, for the 上次游玩 row (file "last-room.txt": room id, server).
        public void SaveLastRoom(string roomId, string server)
        {
            RoomWire.AtomicWrite(Path.Combine(root, "last-room.txt"), Encoding.UTF8.GetBytes(Id(roomId) + "\n" + (server ?? "")));
        }

        public bool LoadLastRoom(out string roomId, out string server)
        {
            roomId = server = "";
            string path = Path.Combine(root, "last-room.txt");
            if (!File.Exists(path)) return false;
            string[] parts = File.ReadAllText(path, Encoding.UTF8).Split('\n');
            if (parts.Length < 1 || parts[0].Length == 0) return false;
            roomId = parts[0].Trim(); server = parts.Length > 1 ? parts[1].Trim() : "";
            foreach (RoomBackupInfo room in ListRooms()) if (room.Id == roomId) return true;
            return false;
        }

        public string LoadToken(string roomId)
        {
            string path = Path.Combine(Path.Combine(root, Id(roomId)), "token");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void SaveToken(string roomId, string token)
        {
            string path = Path.Combine(Path.Combine(root, Id(roomId)), "token");
            RoomWire.AtomicWrite(path, Encoding.UTF8.GetBytes(token));
        }

        string SavePath(string room, string save)
        {
            return Path.Combine(Path.Combine(Path.Combine(root, Id(room)), "saves"), Id(save));
        }

        public void StoreVersion(string roomId, string saveId, long version, byte[] data, string summary)
        {
            string path = SavePath(roomId, saveId);
            string history = Path.Combine(path, "history");
            RoomWire.AtomicWrite(Path.Combine(history, "v" + version + ".sav"), data);
            RoomWire.AtomicWrite(Path.Combine(path, "current.sav"), data);
            RoomWire.Fields f = new RoomWire.Fields();
            f.Set("version", version);
            f.Set("summary", summary);
            f.Set("at", RoomWire.Now);
            RoomWire.AtomicWrite(Path.Combine(path, "current.dat"), f.Encode());
            List<long> versions = new List<long>();
            foreach (string file in Directory.GetFiles(history, "v*.sav"))
            {
                versions.Add(long.Parse(Path.GetFileNameWithoutExtension(file).Substring(1)));
            }
            versions.Sort();
            while (versions.Count > 10)
            {
                File.Delete(Path.Combine(history, "v" + versions[0] + ".sav"));
                versions.RemoveAt(0);
            }
        }

        public byte[] LoadLatest(string roomId, string saveId, out long version, out string summary)
        {
            string path = SavePath(roomId, saveId);
            version = 0;
            summary = "";
            if (!File.Exists(Path.Combine(path, "current.dat")))
            {
                return null;
            }
            RoomWire.Fields f = RoomWire.Fields.Decode(File.ReadAllBytes(Path.Combine(path, "current.dat")));
            version = f.GetLong("version", 0);
            summary = f.GetString("summary");
            return File.ReadAllBytes(Path.Combine(path, "current.sav"));
        }

        public void DeleteRoom(string roomId)
        {
            string path = Path.Combine(root, Id(roomId));
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
