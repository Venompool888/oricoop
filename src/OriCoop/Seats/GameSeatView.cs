using System;
using Game;
using HarmonyLib;
using OriCoop.Seats;

namespace OriCoop
{
    // T-056: the game's ISeatView (was LocalSeatsAdapter). Online management uses the host's authoritative seat table;
    // offline P2 retains the existing join/swap path and seats 3/4 have their own input assignments.
    internal sealed class GameSeatView : ISeatView<SeinCharacter>
    {
        static SeinCharacter s_pendingOwner;
        static InputDevice s_pendingDevice;
        static readonly SeatInputs s_extraInputs = new SeatInputs();

        public int[] LocalSeats { get { return Players.LocalSeats; } }

        public int PrimarySeat { get { return Players.LocalSeat; } }

        public int[] RemoteSeats
        {
            get
            {
                if (!NetPlay.Enabled) return new int[0];
                System.Collections.Generic.List<int> seats = new System.Collections.Generic.List<int>();
                foreach (Puppet p in Players.Remotes)
                    if (!p.IsLocal && p.Seat > 0 && !seats.Contains(p.Seat)) seats.Add(p.Seat);
                seats.Sort();
                return seats.ToArray();
            }
        }

        public bool IsLocal(int seat) { return Array.IndexOf(LocalSeats, seat) >= 0; }

        public SeinCharacter OriOf(int seat) { return Players.OriOf(seat); }
        public int SeatOf(SeinCharacter ori) { return Players.SeatOf(ori); }

        public InputDevice InputOf(int seat)
        {
            if (Array.IndexOf(LocalSeats, seat) < 0) throw new ArgumentException("Not a local seat", "seat");
            if (NetPlay.Enabled) return OnlineLocalPlayers.InputOf(seat);
            int input = seat == 1 ? Math.Max(-1, KeyboardOnlyP1.P1Pad) : seat == 2 ? Plugin.Instance.P2PadInUse : ExtraInput(seat);
            return new InputDevice(input == -1 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, input);
        }

        internal static int ExtraInput(int seat) { return s_extraInputs.Get(seat); }
        internal static bool ExtraUsesInput(int input) { return s_extraInputs.Used(input); }
        internal static void ForgetExtra(int seat) { s_extraInputs.Remove(seat); }

        static bool TryInputCode(InputDevice device, out int input)
        {
            input = device.Kind == InputDevice.DeviceKind.KeyboardMouse ? -1 : device.Pad;
            return device.Kind == InputDevice.DeviceKind.KeyboardMouse ||
                device.Kind == InputDevice.DeviceKind.Pad && input >= 0 && input <= 3;
        }

        bool InputTaken(int input, int seat)
        {
            if (seat != 1 && Math.Max(-1, KeyboardOnlyP1.P1Pad) == input) return true;
            if (seat != 2 && Players.OriOf(2) != null && Plugin.Instance.P2PadInUse == input) return true;
            return ExtraUsesInput(input) && ExtraInput(seat) != input;
        }

        public bool SetInput(int seat, InputDevice device)
        {
            if (NetPlay.Enabled) return OnlineLocalPlayers.SetInput(seat, device);
            int input;
            if (!NetPlay.LocalInGameplay() || Plugin.Instance == null || !IsLocal(seat) || !TryInputCode(device, out input)) return false;
            if (seat > 2) return !InputTaken(input, seat) && s_extraInputs.Set(seat, input);
            if (seat == 2 && input < 0 || ExtraUsesInput(input)) return false;
            Plugin.Instance.AssignPad(seat, input); // Preserves the existing P1/P2 swap behaviour.
            return true;
        }

        public bool AddLocalPlayer(InputDevice device)
        {
            if (NetPlay.Enabled) return OnlineLocalPlayers.Add(device);
            int input;
            if (!NetPlay.LocalInGameplay() || Plugin.Instance == null || s_pendingOwner == Characters.Sein ||
                !TryInputCode(device, out input) || InputTaken(input, 0)) return false;
            int seat = OfflineSeats.NextExtra(LocalSeats);
            if (seat == 0 || seat == 2 && input < 0) return false;
            if (seat == 2)
            {
                Plugin.Instance.AssignPad(2, input);
                PlayMode.SetMode(PlayMode.Coop);
            }
            // The existing spawn guard rejects joins during native pause. Honour it: the new local
            // player joins when Continue actually resumes gameplay, not by releasing/reopening the pause.
            if (UI.MainMenuVisible && Characters.Sein != null)
            {
                s_pendingOwner = Characters.Sein;
                s_pendingDevice = device;
                return true;
            }
            if (seat == 2) return Plugin.Instance.SpawnPuppet(seat) != null;
            if (!s_extraInputs.Add(seat, input)) return false;
            try { return Plugin.Instance.SpawnPuppet(seat) != null; }
            finally { if (OriOf(seat) == null) ForgetExtra(seat); }
        }

        public void RemoveLocalPlayer(int seat)
        {
            if (NetPlay.Enabled) { NetPlay.ReleaseLocalSeat(seat); return; }
            if (!NetPlay.LocalInGameplay()) return;
            if (seat == 2 && LocalSeats.Length <= 2)
            {
                s_pendingOwner = null;
                if (P2Context.P2 != null) Plugin.DespawnNow("removed from the co-op menu");
            }
            else if (seat > 2)
            {
                Puppet puppet = Players.OfSein(OriOf(seat));
                if (puppet != null && puppet.IsLocal) Plugin.DespawnPuppet(puppet);
            }
        }

        [HarmonyPatch(typeof(MenuScreenManager), "HideMenuScreen")]
        static class ResumeJoinPatch
        {
            static void Postfix()
            {
                if (s_pendingOwner == null) return;
                if (!NetPlay.LocalInGameplay()) { s_pendingOwner = null; return; }
                if (UI.MainMenuVisible) return;
                SeinCharacter owner = s_pendingOwner;
                s_pendingOwner = null;
                if (owner != Characters.Sein || NetPlay.Enabled || PlayMode.Current != PlayMode.Coop) return;
                new GameSeatView().AddLocalPlayer(s_pendingDevice);
            }
        }
    }
}
