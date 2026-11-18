using System;
using Game;
using HarmonyLib;

namespace OriCoop
{
    internal struct InputDevice
    {
        internal enum DeviceKind { KeyboardMouse, Pad }
        public DeviceKind Kind;
        public int Pad;

        public InputDevice(DeviceKind kind, int pad) { Kind = kind; Pad = pad; }
    }

    // Settings-facing seat view. Online management uses the host's authoritative seat table;
    // offline P2 retains the existing join/swap path. Remote puppets are never input targets.
    internal sealed class LocalSeatsAdapter
    {
        static SeinCharacter s_pendingOwner;
        static int s_pendingPad;

        public int[] LocalSeats
        {
            get
            {
                if (NetPlay.Enabled) return Players.LocalSeats;
                return P2Context.P2 == null ? new int[] { 1 } : new int[] { 1, 2 };
            }
        }

        public InputDevice InputOf(int seat)
        {
            if (Array.IndexOf(LocalSeats, seat) < 0) throw new ArgumentException("Not a local seat", "seat");
            if (NetPlay.Enabled) return OnlineLocalPlayers.InputOf(seat);
            int pad = seat == 1 ? KeyboardOnlyP1.P1Pad : Plugin.Instance.P2PadInUse;
            return new InputDevice(pad < 0 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, pad);
        }

        public bool SetInput(int seat, InputDevice device)
        {
            if (NetPlay.Enabled) return OnlineLocalPlayers.SetInput(seat, device);
            if (!NetPlay.LocalInGameplay() || Plugin.Instance == null || Array.IndexOf(LocalSeats, seat) < 0) return false;
            if (device.Kind == InputDevice.DeviceKind.KeyboardMouse)
            {
                if (seat != 1) return false; // Keyboard P2 needs the future per-seat sampler.
                Plugin.Instance.AssignPad(seat, -1);
                return true;
            }
            if (device.Kind != InputDevice.DeviceKind.Pad || device.Pad < 0 || device.Pad > 3) return false;
            Plugin.Instance.AssignPad(seat, device.Pad); // Preserves the existing swap behaviour.
            return true;
        }

        public bool AddLocalPlayer(InputDevice device)
        {
            if (NetPlay.Enabled) return OnlineLocalPlayers.Add(device);
            if (!NetPlay.LocalInGameplay() || Plugin.Instance == null || P2Context.P2 != null ||
                device.Kind != InputDevice.DeviceKind.Pad || device.Pad < 0 || device.Pad > 3) return false;
            Plugin.Instance.AssignPad(2, device.Pad);
            PlayMode.SetMode(PlayMode.Coop);
            // The existing spawn guard rejects joins during native pause. Honour it: the new local
            // player joins when Continue actually resumes gameplay, not by releasing/reopening the pause.
            if (UI.MainMenuVisible && Characters.Sein != null)
            {
                s_pendingOwner = Characters.Sein;
                s_pendingPad = device.Pad;
                return true;
            }
            Plugin.Instance.Spawn();
            return P2Context.P2 != null;
        }

        public void RemoveLocalPlayer(int seat)
        {
            if (NetPlay.Enabled) { NetPlay.ReleaseLocalSeat(seat); return; }
            if (!NetPlay.LocalInGameplay() || seat != 2) return;
            s_pendingOwner = null;
            if (P2Context.P2 != null) Plugin.DespawnNow("removed from the co-op menu");
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
                if (owner != Characters.Sein || NetPlay.Enabled ||
                    PlayMode.Current != PlayMode.Coop || P2Context.P2 != null || Plugin.Instance == null) return;
                Plugin.Instance.AssignPad(2, s_pendingPad);
                Plugin.Instance.Spawn();
            }
        }
    }
}
