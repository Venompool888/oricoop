using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OriCoop.Ui
{
    // T-171: what the C-server room page sends between players (no Unity; unit-tested).
    //  LobbyState (msg 94, every PC -> all, reliable, on change and every 2 s): byte count, then per local seat
    //    byte seat + byte ready (0/1) + ushort rtt ms (65535 = unknown); then string summary (the room's save,
    //    CsRoomSummary text; only the manager fills it, others send ""); then byte flags (bit 0 = this PC has left the
    //    page and is in the game, so a late joiner can go straight in).
    //  LobbyStart (msg 95, manager -> all, reliable): empty body. Every PC leaves the page and the game starts.
    internal struct CsLobbySeatState
    {
        public byte Seat;
        public bool Ready;
        public int RttMs;   // -1 = unknown
    }

    internal static class CsLobbyWire
    {
        public const byte MsgLobbyState = 94, MsgLobbyStart = 95;   // docs/net-msg-ids.md
        public const int MaxSummaryBytes = 255;

        public static void WriteState(BinaryWriter w, IList<CsLobbySeatState> seats, string summary, bool playing)
        {
            if (seats.Count > 4) throw new ArgumentException("at most 4 seats");
            w.Write((byte)seats.Count);
            foreach (CsLobbySeatState s in seats)
            {
                w.Write(s.Seat);
                w.Write((byte)(s.Ready ? 1 : 0));
                w.Write((ushort)(s.RttMs < 0 ? 65535 : Math.Min(s.RttMs, 65534)));
            }
            byte[] text = System.Text.Encoding.UTF8.GetBytes(summary ?? "");
            if (text.Length > MaxSummaryBytes) throw new ArgumentException("summary too long");
            w.Write((byte)text.Length); w.Write(text);
            w.Write((byte)(playing ? 1 : 0));
        }

        public static bool TryReadState(BinaryReader r, List<CsLobbySeatState> seats, out string summary, out bool playing)
        {
            summary = ""; playing = false;
            seats.Clear();
            try
            {
                int count = r.ReadByte();
                if (count > 4) return false;
                for (int i = 0; i < count; i++)
                {
                    byte seat = r.ReadByte();
                    bool ready = r.ReadByte() != 0;
                    ushort rtt = r.ReadUInt16();
                    if (seat < 1 || seat > 4) return false;
                    seats.Add(new CsLobbySeatState { Seat = seat, Ready = ready, RttMs = rtt == 65535 ? -1 : rtt });
                }
                int length = r.ReadByte();
                byte[] text = r.ReadBytes(length);
                if (text.Length != length) return false;
                summary = System.Text.Encoding.UTF8.GetString(text);
                playing = (r.ReadByte() & 1) != 0;
                return true;
            }
            catch (EndOfStreamException) { seats.Clear(); return false; }
        }
    }

    // The room's save as one line (C-server room info and LobbyState): name=Glimmer Hollow;slot=1;area=<area key>;pct=6;secs=4214;diff=1
    // name = the room's name (English only, RoomNames.Clean; T-171 owner decision).
    // area = SaveSlotInfo.AreaName (the key the game's slot screen looks up for the name and the orb picture),
    // diff = (int)DifficultyMode. Unknown or missing fields are left out / ignored.
    internal sealed class CsRoomSummary
    {
        public int Slot = -1;          // 0-based save slot on the creator's PC; -1 = not set
        public string Area = "";
        public string Name = "";
        public int Completion = -1, Seconds = -1, Difficulty = -1;

        public bool HasSave { get { return Area.Length > 0; } }

        public string Encode()
        {
            List<string> parts = new List<string>();
            string name = RoomNames.Clean(Name);
            if (name.Length > 0) parts.Add("name=" + name);
            if (Slot >= 0) parts.Add("slot=" + (Slot + 1).ToString(CultureInfo.InvariantCulture));
            if (Area.Length > 0) parts.Add("area=" + Clean(Area));
            if (Completion >= 0) parts.Add("pct=" + Completion.ToString(CultureInfo.InvariantCulture));
            if (Seconds >= 0) parts.Add("secs=" + Seconds.ToString(CultureInfo.InvariantCulture));
            if (Difficulty >= 0) parts.Add("diff=" + Difficulty.ToString(CultureInfo.InvariantCulture));
            string text = string.Join(";", parts.ToArray());
            while (System.Text.Encoding.UTF8.GetByteCount(text) > CsLobbyWire.MaxSummaryBytes) text = text.Substring(0, text.Length - 1);
            return text;
        }

        static string Clean(string value) { return value.Replace(";", "").Replace("=", ""); }

        public static CsRoomSummary Parse(string text)
        {
            CsRoomSummary s = new CsRoomSummary();
            if (string.IsNullOrEmpty(text)) return s;
            foreach (string part in text.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), value = part.Substring(eq + 1);
                int number;
                bool isNumber = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 0;
                switch (key)
                {
                    case "slot": if (isNumber && number >= 1) s.Slot = number - 1; break;
                    case "area": s.Area = value; break;
                    case "name": s.Name = RoomNames.Clean(value); break;
                    case "pct": if (isNumber) s.Completion = number; break;
                    case "secs": if (isNumber) s.Seconds = number; break;
                    case "diff": if (isNumber) s.Difficulty = number; break;
                }
            }
            return s;
        }

        // "01:10:14"
        public static string Clock(int seconds)
        {
            if (seconds < 0) return "";
            return (seconds / 3600).ToString("D2") + ":" + (seconds / 60 % 60).ToString("D2") + ":" + (seconds % 60).ToString("D2");
        }
    }
}
