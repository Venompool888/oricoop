using System;
using System.Collections.Generic;
using Game;
using OriCoop.Rules;
using UnityEngine;

namespace OriCoop
{
    // Adapter only: seat ownership and wall decisions live in Unity-free modules.
    internal static class OnlineLocalPlayers
    {
        static readonly Seats.SeatInputs inputs = new Seats.SeatInputs();
        static readonly Dictionary<int, Vector3> last = new Dictionary<int, Vector3>();
        static InputDevice? pendingInput;
        static bool primaryFixed; // T-188: the player picked the primary seat's device in F8, or the game started
        public static int EdgeBlocks;
        public static void Reset()
        {
            inputs.Clear(); last.Clear(); pendingInput = null; primaryFixed = false;
        }
        static InputDevice DefaultInput(int seat)
        {
            // T-188: the primary seat gets the device used last on the menus (keyboard/mouse only if nothing was pressed).
            if (seat == Players.LocalSeat) return Device(Seats.PrimaryInput.Default(inputs, KeyboardOnlyP1.MenuDevice));
            for (int pad = 0; pad < 4; pad++)
            {
                if (!inputs.Used(pad)) return new InputDevice(InputDevice.DeviceKind.Pad, pad);
            }
            return new InputDevice(InputDevice.DeviceKind.Pad, 0);
        }
        public static InputDevice InputOf(int seat)
        {
            int input = inputs.Get(seat);
            if (input == -2) { InputDevice d = DefaultInput(seat); input = Code(d); inputs.Add(seat, input); }
            return new InputDevice(input < 0 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, input);
        }
        static int Code(InputDevice d) { return d.Kind == InputDevice.DeviceKind.KeyboardMouse ? -1 : d.Pad; }
        static InputDevice Device(int code) { return new InputDevice(code < 0 ? InputDevice.DeviceKind.KeyboardMouse : InputDevice.DeviceKind.Pad, code); }
        static string Describe(int code) { return code < 0 ? "keyboard/mouse" : "pad " + code; }
        static bool Same(InputDevice a, InputDevice b) { return Code(a) == Code(b); }
        public static bool SetInput(int seat, InputDevice device)
        {
            if (!Players.IsLocal(seat) || device.Kind != InputDevice.DeviceKind.KeyboardMouse && device.Kind != InputDevice.DeviceKind.Pad
                || device.Kind == InputDevice.DeviceKind.Pad && (device.Pad < 0 || device.Pad > 3)) return false;
            foreach (int other in Players.LocalSeats) InputOf(other);
            if (!inputs.Set(seat, Code(device))) return false;
            if (seat == Players.LocalSeat) primaryFixed = true; // an explicit choice is never overridden
            return true;
        }

        // T-188: on the menus, until the player picks a device in F8, the primary seat follows the device used last;
        // once the game starts it stays put (so e.g. a key pressed in the pause menu doesn't take a controller away).
        static void FollowMenuDevice()
        {
            if (primaryFixed) return;
            int seat = Players.LocalSeat;
            if (!KeyboardOnlyP1.InMenu())
            {
                primaryFixed = true;
                if (inputs.Get(seat) != -2) Plugin.Log.LogInfo("online P" + seat + " input at game start: " + Describe(inputs.Get(seat)));
                return;
            }
            int was = inputs.Get(seat);
            if (Seats.PrimaryInput.Follow(inputs, seat, KeyboardOnlyP1.MenuDevice))
                Plugin.Log.LogInfo("online P" + seat + " input follows the menu device: " + Describe(was) + " -> " + Describe(inputs.Get(seat)));
        }
        public static bool Add(InputDevice device)
        {
            if (!NetPlay.CanAddLocal || device.Kind != InputDevice.DeviceKind.KeyboardMouse && device.Kind != InputDevice.DeviceKind.Pad
                || device.Kind == InputDevice.DeviceKind.Pad && (device.Pad < 0 || device.Pad > 3)) return false;
            foreach (int seat in Players.LocalSeats) if (Same(InputOf(seat), device)) return false;
            pendingInput = device;
            if (NetPlay.AddLocalSeat()) return true;
            pendingInput = null; return false;
        }
        public static void ChangePrimary(int previous, int primary)
        {
            InputOf(previous); // Existing seat/input identity survives the device-primary change.
            InputDevice added = pendingInput.HasValue && !inputs.Used(Code(pendingInput.Value)) ? pendingInput.Value : DefaultInput(primary);
            inputs.AddAtNewPrimary(previous, primary, Code(added));
            pendingInput = null;
        }
        public static void Reconcile()
        {
            for (int seat = 1; seat <= 4; seat++)
                if (!Players.IsLocal(seat)) { inputs.Remove(seat); last.Remove(seat); }
            foreach (int seat in Players.LocalSeats)
            {
                if (inputs.Get(seat) != -2) continue;
                InputDevice device = pendingInput.HasValue && !inputs.Used(Code(pendingInput.Value)) ? pendingInput.Value : DefaultInput(seat);
                inputs.Add(seat, Code(device));
                pendingInput = null;
            }
            foreach (Puppet p in new List<Puppet>(Players.Remotes))
                if (p.IsLocal && (!Players.IsLocal(p.Seat) || p.Seat == Players.LocalSeat)) Plugin.DespawnPuppet(p);
        }
        public static void Tick()
        {
            if (!NetPlay.Enabled) return;
            Reconcile();
            FollowMenuDevice();
            if (!NetPlay.LocalInGameplay() || !SaveSync.Ready && NetPlay.Connected) return;
            foreach (int seat in Players.LocalSeats)
            {
                if (seat == Players.LocalSeat || Players.OriOf(seat) != null) continue;
                Puppet p = Plugin.Instance.SpawnPuppet(seat);
                if (p != null) p.IsLocal = true;
            }
        }
        static void Sample(int seat, InputState state)
        {
            if (NetPlay.Enabled)
            {
                InputDevice d = InputOf(seat);
                if (d.Kind == InputDevice.DeviceKind.Pad) P2Input.Sample(d.Pad, state);
                else SampleKeyboard(state);
            }
            else
            {
                int input = GameSeatView.ExtraInput(seat);
                if (input >= 0) P2Input.SampleExtra(input, state);
                else if (input == -1) SampleKeyboard(state);
            }
            GameController gc = GameController.Instance;
            if (P2Input.HoldStill || gc == null || gc.InputLocked || gc.GameplaySuspended || gc.GameplaySuspendedForUI
                || StorySync.IntroHeld(seat) || Revive.IsDowned(Players.OriOf(seat)))
                P2Input.Fill(state, 0, 0, 0, 0, 0, false, false, false);
        }
        public static void SampleExtras()
        {
            foreach (Puppet p in Players.Remotes)
                if (p.IsLocal && p.Sein != null && (NetPlay.Enabled || p.Seat > 2)) Sample(p.Seat, p.Input);
        }
        static void SampleKeyboard(InputState s)
        {
            int b = 0;
            if (Input.GetKey(KeyCode.Space)) b |= 0x1000;
            if (Input.GetMouseButton(0) || Input.GetKey(KeyCode.X)) b |= 0x4000;
            if (Input.GetKey(KeyCode.E)) b |= 0x8000;
            if (Input.GetKey(KeyCode.S)) b |= 0x2000;
            float x = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0);
            float y = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0) - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0);
            P2Input.Fill(s, b, x, y, 0, 0, Input.GetKey(KeyCode.LeftShift), Input.GetKey(KeyCode.LeftControl), false);
            s.CursorPosition = Input.mousePosition; s.CursorMoved = true;
        }
        static EdgePoint[] Points(bool previous)
        {
            List<EdgePoint> points = new List<EdgePoint>();
            foreach (int seat in Players.LocalSeats)
            {
                SeinCharacter ori = Players.OriOf(seat);
                if (ori == null || !ori.gameObject.activeInHierarchy || Revive.IsDowned(ori)) continue;
                if (!NetPlay.Enabled && ori.Mortality.Health.Amount <= 0f) continue;
                Vector3 pos = ori.Position, old;
                if (!previous || !last.TryGetValue(seat, out old)) old = pos;
                points.Add(new EdgePoint(seat, true, pos.x, pos.y, old.x, old.y));
            }
            return points.ToArray();
        }
        // T-058 can consume this box/centre without changing its online camera implementation here.
        public static bool LocalGroupBounds(out Vector3 centre, out Vector2 size)
        {
            float minX, minY, maxX, maxY;
            bool found = LocalEdgeWall.Bounds(Points(false), out minX, out minY, out maxX, out maxY);
            centre = found ? new Vector3((minX + maxX) / 2, (minY + maxY) / 2, 0) : Vector3.zero;
            size = found ? new Vector2(maxX - minX, maxY - minY) : Vector2.zero;
            return found;
        }
        public static void PositionChanged() { last.Clear(); }
        internal static void ForgetPosition(int seat) { last.Remove(seat); }
        public static void ApplyWall()
        {
            GameController gc = GameController.Instance;
            if (!NetPlay.Enabled || StorySync.IntroActive || gc == null || gc.InputLocked || gc.GameplaySuspendedForUI)
            { last.Clear(); return; }
            Vector2 now, max;
            if (!CoopCamera.ViewSize(out now, out max)) { last.Clear(); return; }
            ApplyWall(Mathf.Max(4, max.x - 2 * CoopCamera.EdgeMargin), Mathf.Max(4, max.y - 2 * CoopCamera.EdgeMargin));
        }
        internal static void ApplyWall(float width, float height)
        {
            EdgePoint[] points = Points(true);
            LocalEdgeWall.Apply(points, width, height);
            foreach (EdgePoint p in points)
            {
                SeinCharacter ori = Players.OriOf(p.Seat); Vector3 pos = ori.Position, speed = ori.Speed;
                if (!NetPlay.Enabled && pos.x == p.X && pos.y == p.Y) { last[p.Seat] = pos; continue; }
                if (Mathf.Abs(pos.x - p.X) > 0.0001f) { if ((pos.x - p.X) * speed.x > 0) speed.x = 0; EdgeBlocks++; }
                if (Mathf.Abs(pos.y - p.Y) > 0.0001f) { if ((pos.y - p.Y) * speed.y > 0) speed.y = 0; EdgeBlocks++; }
                ori.Position = new Vector3(p.X, p.Y, pos.z); ori.Speed = speed; last[p.Seat] = ori.Position;
            }
        }
    }
}
