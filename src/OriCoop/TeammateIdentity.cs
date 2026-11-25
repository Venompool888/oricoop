using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace OriCoop
{
    internal static class TeammateIdentity
    {
        const byte Message = 103;
        const int MaxBytes = 64;
        static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, true);
        static readonly GameSeatView s_view = new GameSeatView();
        static readonly ConfigEntry<string>[] s_profiles = new ConfigEntry<string>[6];
        static ConfigEntry<bool> s_useEnvironment;
        static readonly string[] s_local = new string[5];
        static readonly string[,] s_remote = new string[5, 5];
        static int s_mask, s_originalPrimary; // 0 follows the primary, -1 means that original local player left.
        static bool s_changed;
        static float s_nextCheck;

        internal static void Init(ConfigFile config)
        {
            if (s_useEnvironment != null) return;
            string[] keys = { "Primary", "Keyboard", "Controller1", "Controller2", "Controller3", "Controller4" };
            for (int i = 0; i < keys.Length; i++)
                s_profiles[i] = config.Bind("TeammateNames", keys[i], "", "Optional game display name for this local player.");
            s_useEnvironment = config.Bind("TeammateNames", "UseEnvironmentName", true,
                "Use ORICOOP_PLAYER_NAME as the primary player's default until their name is edited in the game.");
            NetPlay.On(Message, Receive);
            NetPlay.Forward(Message);
            NetPlay.PlayerJoined += delegate(int seat) { Refresh(true); };
            NetPlay.PlayerLeft += Left;
            NetPlay.SessionEnded += delegate { TransportReset(NetPlay.IsHost); };
            Players.SeatsRenamed += Renamed;
            Refresh(false);
        }

        // The device can add a couch player to a lower free network seat. The original primary remains the
        // same local player on their old seat; the newly added input has its own name, even as network primary.
        static void Renamed(int[] renamed)
        {
            for (int old = 1; old <= 4; old++)
            {
                if (renamed[old] == 0) continue;
                if (renamed[old] == Players.LocalSeat && Players.IsLocal(old) && s_originalPrimary == 0)
                    s_originalPrimary = old;
                Left(old); Left(renamed[old]);
            }
            s_changed = true;
        }

        static void Left(int seat)
        {
            if (seat < 1 || seat > 4) return;
            for (int i = 1; i <= 4; i++) { s_remote[seat, i] = null; s_remote[i, seat] = null; }
        }

        internal static void LocalSeatsChanged()
        {
            if (s_originalPrimary > 0 && !Players.IsLocal(s_originalPrimary)) s_originalPrimary = -1;
            s_changed = true;
        }

        internal static void TransportReset(bool keepLocalPlayers)
        {
            Array.Clear(s_remote, 0, s_remote.Length);
            if (!keepLocalPlayers) s_originalPrimary = 0;
            s_changed = true;
        }

        static int Profile(int seat)
        {
            if (seat == (s_originalPrimary == 0 ? Players.LocalSeat : s_originalPrimary)) return 0;
            InputDevice input = s_view.InputOf(seat);
            return input.Kind == InputDevice.DeviceKind.KeyboardMouse ? 1 : 2 + Math.Max(0, Math.Min(3, input.Pad));
        }

        internal static string GetLocalName(int seat)
        {
            if (s_useEnvironment == null || seat < 1 || seat > 4 || !Players.IsLocal(seat)) return "";
            int profile = Profile(seat);
            string name = s_profiles[profile].Value;
            if (profile == 0 && String.IsNullOrEmpty(name) && s_useEnvironment.Value)
                return Clean(Environment.GetEnvironmentVariable("ORICOOP_PLAYER_NAME"));
            return Clean(name);
        }

        internal static void SetLocalName(int seat, string name)
        {
            if (s_useEnvironment == null || seat < 1 || seat > 4 || !Players.IsLocal(seat)) return;
            int profile = Profile(seat);
            if (profile == 0) s_useEnvironment.Value = false;
            s_profiles[profile].Value = Clean(name);
            Refresh(true);
        }

        internal static string Name(int seat)
        {
            if (seat < 1 || seat > 4) return "";
            if ((s_mask & Bit(seat)) != 0) return s_local[seat] ?? "";
            return s_remote[NetPlay.DeviceSeat(seat), seat] ?? "";
        }

        internal static string Label(int seat)
        {
            string name = Name(seat), tag = Roles.Tag(seat);
            return name.Length == 0 ? tag : tag + " " + name;
        }

        static int Bit(int seat) { return 1 << (seat - 1); }

        internal static void Tick()
        {
            if (s_useEnvironment == null) return;
            float now = Time.realtimeSinceStartup;
            if (!s_changed && now < s_nextCheck) return;
            s_nextCheck = now + 0.5f;
            Refresh(s_changed);
        }

        static void Refresh(bool force)
        {
            s_changed = false;
            if (!NetPlay.Enabled) s_originalPrimary = 0;
            int mask = 0;
            foreach (int seat in Players.LocalSeats)
            {
                mask |= Bit(seat);
                string name = GetLocalName(seat);
                if (name != s_local[seat]) { s_local[seat] = name; force = true; }
            }
            if (mask != s_mask) { s_mask = mask; force = true; }
            if (force && NetPlay.Enabled && NetPlay.Connected)
                NetPlay.Send(Message, true, delegate(BinaryWriter w) {
                    w.Write((byte)s_mask);
                    for (int seat = 1; seat <= 4; seat++)
                    {
                        if ((s_mask & Bit(seat)) == 0) continue;
                        byte[] name = s_utf8.GetBytes(s_local[seat]);
                        w.Write((byte)name.Length); w.Write(name);
                    }
                });
        }

        // A full device snapshot is applied atomically. Only the sender's current seats can take a name.
        static void Receive(BinaryReader reader)
        {
            int sender = NetPlay.Sender;
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (sender < 1 || sender > 4 || remaining < 2 || remaining > 1 + 4 * (1 + MaxBytes)) return;
            int mask = reader.ReadByte();
            if ((mask & ~15) != 0 || (mask & Bit(sender)) == 0) return;
            string[] incoming = new string[5];
            try
            {
                for (int seat = 1; seat <= 4; seat++)
                {
                    if ((mask & Bit(seat)) == 0) continue;
                    int length = reader.ReadByte();
                    if (length > MaxBytes || length > reader.BaseStream.Length - reader.BaseStream.Position) return;
                    incoming[seat] = Clean(s_utf8.GetString(reader.ReadBytes(length)));
                }
            }
            catch (EndOfStreamException) { return; }
            catch (DecoderFallbackException) { return; }
            if (reader.BaseStream.Position != reader.BaseStream.Length) return;
            for (int seat = 1; seat <= 4; seat++)
                s_remote[sender, seat] = NetPlay.DeviceSeat(seat) == sender ? incoming[seat] : null;
        }

        internal static string Clean(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            StringBuilder clean = new StringBuilder();
            int count = 0;
            for (int i = 0; i < value.Length && count < 16; i++)
            {
                char c = value[i];
                bool pair = char.IsSurrogatePair(value, i);
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(value, i);
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator ||
                    char.IsSurrogate(c) && !pair || "*[]#@<>=".IndexOf(c) >= 0) continue;
                clean.Append(c);
                if (pair) clean.Append(value[++i]);
                count++;
            }
            return clean.ToString().Trim();
        }
    }
}
