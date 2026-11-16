using System;
using System.IO;
using System.Net;
using System.Globalization;

namespace OriCoop.Rooms
{
    public sealed class RoomAuth
    {
        public string Room;
        public string Steam;
        public string Token;
    }

    public sealed class RoomReply
    {
        public bool Ok;
        public string Error;
        public string Message;
        public RoomWire.Fields Fields;
    }

    public sealed class RoomClient
    {
        readonly string url;
        public int TimeoutMs = 15000;
        public int TransferTimeoutMs = 120000;
        public string ClientVersion = "OriCoop";

        public RoomClient(string baseUrl)
        {
            url = baseUrl.TrimEnd('/') + "/";
        }

        public RoomReply Call(string op, RoomWire.Fields request)
        {
            try
            {
                request.Set("proto", 1);
                request.Set("client", ClientVersion);
                HttpWebRequest http = (HttpWebRequest)WebRequest.Create(url + "v1/" + op);
                http.Method = "POST";
                http.ContentType = "application/octet-stream";
                http.Proxy = null;
                http.KeepAlive = false;
                bool transfer = op == "upload" || op == "download" || op == "create"
                    || op == "new_save" || op == "restore";
                http.Timeout = transfer ? TransferTimeoutMs : TimeoutMs;
                http.ReadWriteTimeout = http.Timeout;
                byte[] bytes = request.Encode();
                http.ContentLength = bytes.Length;
                using (Stream stream = http.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }
                using (WebResponse response = http.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream memory = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        memory.Write(buffer, 0, n);
                        if (memory.Length > RoomWire.MaximumBodyBytes) throw new InvalidDataException("rooms reply too large");
                    }
                    RoomWire.Fields f = RoomWire.Fields.Decode(memory.ToArray());
                    return new RoomReply
                    {
                        Ok = f.GetString("ok") == "1",
                        Error = f.GetString("error"),
                        Message = f.GetString("message"),
                        Fields = f
                    };
                }
            }
            catch (Exception ex)
            {
                return new RoomReply
                {
                    Ok = false,
                    Error = "network",
                    Message = ex.Message,
                    Fields = new RoomWire.Fields()
                };
            }
        }

        static RoomWire.Fields F(params object[] pairs)
        {
            RoomWire.Fields f = new RoomWire.Fields();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                object value = pairs[i + 1];
                if (value is byte[])
                {
                    f.Set((string)pairs[i], (byte[])value);
                }
                else
                {
                    f.Set((string)pairs[i], Convert.ToString(value, CultureInfo.InvariantCulture));
                }
            }
            return f;
        }

        static RoomWire.Fields A(RoomAuth auth, params object[] pairs)
        {
            RoomWire.Fields f = F(pairs);
            f.Set("room", auth.Room);
            f.Set("steam", auth.Steam);
            f.Set("token", auth.Token);
            return f;
        }

        RoomReply WithPassword(string op, RoomWire.Fields f, string password)
        {
            if (password == null || password.Length < 4)
            {
                return new RoomReply
                {
                    Error = "bad_request",
                    Message = "Password must have at least 4 characters",
                    Fields = new RoomWire.Fields()
                };
            }
            byte[] salt = RoomWire.RandomBytes(16);
            f.Set("salt", salt);
            f.Set("verifier", RoomWire.Verifier(password, salt));
            return Call(op, f);
        }

        public RoomReply Hello()
        {
            return Call("hello", F());
        }

        public RoomReply Create(string steam, string nick, string roomName, string password,
            byte[] save, string summary, string saveName)
        {
            RoomWire.Fields f = F("steam", steam, "nick", nick, "name", roomName,
                "summary", summary, "savename", saveName);
            if (save != null)
            {
                f.Set("save", save);
            }
            return WithPassword("create", f, password);
        }

        RoomReply Login(string op, string key, string id, string password, string steam, string nick)
        {
            RoomReply challenge = Call("challenge", F(key, id));
            if (!challenge.Ok)
            {
                return challenge;
            }
            byte[] nonce = challenge.Fields.GetBytes("nonce");
            byte[] verifier = RoomWire.Verifier(password, challenge.Fields.GetBytes("salt"));
            RoomWire.Fields f = F(key, id, "steam", steam, "nick", nick,
                "nonce", nonce, "proof", RoomWire.Proof(verifier, nonce));
            return Call(op, f);
        }

        public RoomReply Join(string code, string password, string steam, string nick)
        {
            return Login("join", "code", code, password, steam, nick);
        }

        public RoomReply Rebind(string room, string password, string steam)
        {
            return Login("rebind", "room", room, password, steam, "");
        }

        public RoomReply Info(RoomAuth auth)
        {
            return Call("info", A(auth));
        }

        public RoomReply Enter(RoomAuth auth, string save)
        {
            return Call("enter", A(auth, "save", save));
        }

        public RoomReply Live(RoomAuth auth, string save, string liveCode)
        {
            return Call("live", A(auth, "save", save, "live", liveCode));
        }

        public RoomReply Beat(RoomAuth auth, string save, bool waiting)
        {
            return Call("beat", A(auth, "save", save, "waiting", waiting ? "1" : "0"));
        }

        public RoomReply Leave(RoomAuth auth, string save)
        {
            return Call("leave", A(auth, "save", save));
        }

        public RoomReply Download(RoomAuth auth, string save, long haveVersion)
        {
            return Call("download", A(auth, "save", save, "have", haveVersion));
        }

        public RoomReply Upload(RoomAuth auth, string save, long baseVersion, byte[] data,
            string summary, long secs, bool together)
        {
            return Call("upload", A(auth, "save", save, "base", baseVersion, "data", data,
                "summary", summary, "secs", secs, "together", together ? "1" : "0"));
        }

        public RoomReply NewSave(RoomAuth auth, string name, byte[] data, string summary, string origin)
        {
            return Call("new_save", A(auth, "name", name, "data", data, "summary", summary, "origin", origin));
        }

        public RoomReply RenameRoom(RoomAuth auth, string name)
        {
            return Call("rename_room", A(auth, "name", name));
        }

        public RoomReply RenameSave(RoomAuth auth, string save, string name)
        {
            return Call("rename_save", A(auth, "save", save, "name", name));
        }

        public RoomReply DeleteSave(RoomAuth auth, string save)
        {
            return Call("delete_save", A(auth, "save", save));
        }

        public RoomReply SetSolo(RoomAuth auth, bool solo)
        {
            return Call("set_solo", A(auth, "solo", solo ? "1" : "0"));
        }

        public RoomReply SetSeats(RoomAuth auth, int seats)
        {
            return Call("set_seats", A(auth, "seats", seats));
        }

        public RoomReply SetPassword(RoomAuth auth, string password)
        {
            return WithPassword("set_password", A(auth), password);
        }

        public RoomReply Kick(RoomAuth auth, int seat)
        {
            return Call("kick", A(auth, "seat", seat));
        }

        public RoomReply Quit(RoomAuth auth)
        {
            return Call("quit", A(auth));
        }

        public RoomReply DeleteRoom(RoomAuth auth)
        {
            return Call("delete_room", A(auth));
        }

        public RoomReply Copy(RoomAuth auth, string save, string newName, string password, int seat = 1)
        {
            return WithPassword("copy", A(auth, "save", save, "name", newName, "seat", seat), password);
        }

        public RoomReply Versions(RoomAuth auth, string save)
        {
            return Call("versions", A(auth, "save", save));
        }

        public RoomReply Revert(RoomAuth auth, string save, long version)
        {
            return Call("revert", A(auth, "save", save, "version", version));
        }

        public RoomReply Restore(string steam, string nick, RoomBackupInfo backup, string password,
            byte[] data, string summary, string saveName)
        {
            RoomWire.Fields f = F("steam", steam, "nick", nick, "room", backup.Id, "name", backup.Name,
                "seats", backup.Seats, "solo", backup.Solo ? "1" : "0", "savename", saveName,
                "data", data, "summary", summary);
            backup.WriteMembers(f);
            return WithPassword("restore", f, password);
        }
    }
}
