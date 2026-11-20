using System;
using System.Collections.Generic;
using System.IO;

namespace OriCoop.Seats
{
    // A device is identified by its primary (lowest) seat, never by a name or network address.
    public sealed class SeatTable
    {
        readonly int[] owners = new int[5];
        public int Count { get { int n = 0; for (int i = 1; i <= 4; i++) if (owners[i] != 0) n++; return n; } }
        public int Owner(int seat) { return seat >= 1 && seat <= 4 ? owners[seat] : 0; }
        public int Add(int device)
        {
            if (device < 1 || device > 4) return 0;
            for (int i = 1; i <= 4; i++)
                if (owners[i] == 0)
                {
                    // A new connection must take the lowest free seat as its identity.
                    if (owners[device] != device && i != device) return 0;
                    owners[i] = device;
                    if (i < device)
                        for (int j = 1; j <= 4; j++) if (owners[j] == device) owners[j] = i;
                    return i;
                }
            return 0;
        }
        public bool Release(int device, int seat)
        {
            if (Owner(seat) != device || device == 0) return false;
            if (seat == device && SeatsOf(device).Length > 1) return false;
            owners[seat] = 0; return true;
        }
        public void RemoveDevice(int device) { for (int i = 1; i <= 4; i++) if (owners[i] == device) owners[i] = 0; }
        public int[] SeatsOf(int device)
        {
            List<int> result = new List<int>();
            for (int i = 1; i <= 4; i++) if (device != 0 && owners[i] == device) result.Add(i);
            return result.ToArray();
        }
        public bool Accepts(int source, int sender, int seat)
        { return source >= 1 && source <= 4 && sender == source && Owner(source) == source && Owner(seat) == source; }
        public void Write(BinaryWriter writer) { for (int i = 1; i <= 4; i++) writer.Write((byte)owners[i]); }
        public void Read(BinaryReader reader)
        {
            int[] next = new int[5];
            for (int i = 1; i <= 4; i++) next[i] = reader.ReadByte();
            for (int i = 1; i <= 4; i++)
                if (next[i] > 4 || next[i] != 0 && (next[next[i]] != next[i] || next[i] > i))
                    throw new InvalidDataException("Invalid seat ownership");
            Array.Copy(next, owners, 5);
        }
        public void KeepDevice(int device)
        { for (int i = 1; i <= 4; i++) if (owners[i] != device) owners[i] = 0; }
        public void Clear() { Array.Clear(owners, 0, owners.Length); }

        // T-133: which device primaries changed number since `before` (a copy of an earlier table, see CopyOwners).
        // Add relabels every seat of the adding device, so its old primary seat now belongs to its new primary.
        // renamed[oldPrimary] = newPrimary; 0 = unchanged (or the device left: that is PlayerLeft, not a rename).
        public int[] Renames(int[] before)
        {
            int[] renamed = new int[5];
            for (int seat = 1; seat <= 4; seat++)
                if (before[seat] == seat && owners[seat] != 0 && owners[seat] != seat) renamed[seat] = owners[seat];
            return renamed;
        }
        public void CopyOwners(int[] into) { Array.Copy(owners, into, 5); }
    }
}
