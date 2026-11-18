using System;
using System.Collections.Generic;

namespace OriCoop
{
    // The input a local seat plays with: the keyboard and mouse (at most one per device) or pad 0-3.
    internal struct InputDevice
    {
        internal enum DeviceKind { KeyboardMouse, Pad }
        public DeviceKind Kind;
        public int Pad;

        public InputDevice(DeviceKind kind, int pad) { Kind = kind; Pad = pad; }
    }
}

namespace OriCoop.Seats
{
    // T-056: one view of the seats (1-4) for every module that needs "who is local, which Ori is whose, which input".
    // Unity-free so rules and tests can use it; the game implementation is GameSeatView (TOri = SeinCharacter).
    //   LocalSeats    seats played on this device, ascending; never empty.
    //   PrimarySeat   this device's lowest local seat (Players.LocalSeat); it runs the device's simulation.
    //   RemoteSeats   seats played on other devices, ascending.
    //   OriOf/SeatOf  seat <-> character; null / 0 when there is none.
    //   InputOf       the input of a local seat (ArgumentException for a seat that is not local).
    internal interface ISeatView<TOri> where TOri : class
    {
        int[] LocalSeats { get; }
        int PrimarySeat { get; }
        int[] RemoteSeats { get; }
        bool IsLocal(int seat);
        TOri OriOf(int seat);
        int SeatOf(TOri ori);
        InputDevice InputOf(int seat);
        bool SetInput(int seat, InputDevice device);
        bool AddLocalPlayer(InputDevice device);
        void RemoveLocalPlayer(int seat);
    }

    internal static class OfflineSeats
    {
        public static int NextExtra(int[] localSeats)
        {
            for (int seat = 2; seat <= SeatViewChecks.MaxSeats; seat++)
                if (Array.IndexOf(localSeats, seat) < 0) return seat;
            return 0;
        }
    }

    // The invariants every ISeatView must hold; used by the unit tests and by the game tests on the live view.
    internal static class SeatViewChecks
    {
        public const int MaxSeats = 4;

        // requireOris: every listed seat must have a character (true once everyone is in gameplay).
        public static List<string> Problems<TOri>(ISeatView<TOri> view, bool requireOris) where TOri : class
        {
            List<string> problems = new List<string>();
            int[] local = view.LocalSeats ?? new int[0];
            int[] remote = view.RemoteSeats ?? new int[0];
            if (local.Length == 0) problems.Add("no local seat");
            CheckList("local", local, problems);
            CheckList("remote", remote, problems);
            if (local.Length > 0 && view.PrimarySeat != local[0])
                problems.Add("primary seat " + view.PrimarySeat + " is not the lowest local seat " + local[0]);
            foreach (int seat in remote)
                if (Array.IndexOf(local, seat) >= 0) problems.Add("seat " + seat + " is both local and remote");
            if (local.Length + remote.Length > MaxSeats) problems.Add("more than " + MaxSeats + " seats");
            for (int seat = 1; seat <= MaxSeats; seat++)
                if (view.IsLocal(seat) != (Array.IndexOf(local, seat) >= 0))
                    problems.Add("IsLocal(" + seat + ") disagrees with LocalSeats");
            List<TOri> seen = new List<TOri>();
            foreach (int[] list in new int[][] { local, remote })
                foreach (int seat in list)
                {
                    TOri ori = view.OriOf(seat);
                    if (ori == null) { if (requireOris) problems.Add("seat " + seat + " has no character"); continue; }
                    if (seen.Contains(ori)) problems.Add("seat " + seat + " shares its character with another seat");
                    seen.Add(ori);
                    int back = view.SeatOf(ori);
                    if (back != seat) problems.Add("SeatOf(OriOf(" + seat + ")) is " + back);
                }
            bool keyboard = false; List<int> pads = new List<int>();
            foreach (int seat in local)
            {
                InputDevice d;
                try { d = view.InputOf(seat); }
                catch (Exception e) { problems.Add("InputOf(" + seat + ") threw " + e.GetType().Name); continue; }
                if (d.Kind == InputDevice.DeviceKind.KeyboardMouse)
                {
                    if (keyboard) problems.Add("keyboard on more than one local seat");
                    keyboard = true;
                }
                else if (d.Pad < 0 || d.Pad > 3) problems.Add("seat " + seat + " has pad " + d.Pad);
                else if (pads.Contains(d.Pad)) problems.Add("pad " + d.Pad + " on more than one local seat");
                else pads.Add(d.Pad);
            }
            return problems;
        }

        static void CheckList(string name, int[] seats, List<string> problems)
        {
            for (int i = 0; i < seats.Length; i++)
            {
                if (seats[i] < 1 || seats[i] > MaxSeats) problems.Add(name + " seat " + seats[i] + " out of range");
                if (i > 0 && seats[i] <= seats[i - 1]) problems.Add(name + " seats not ascending and unique");
            }
        }
    }
}
