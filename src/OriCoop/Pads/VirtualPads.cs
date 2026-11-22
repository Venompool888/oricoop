using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using J2i.Net.XInputWrapper;

namespace OriCoop.Pads
{
    public static class VirtualPads
    {
        sealed class Routing
        {
            public PsPad Pad;
            public MirrorJudge Judge;
            public PsRoute Route, LoggedRoute;
        }

        static readonly object Gate = new object();
        static readonly Routing[] Slots = new Routing[4];
        static readonly List<Routing> Routes = new List<Routing>();
        static readonly List<PsPad> Pads = new List<PsPad>();
        static readonly Queue<long> Edges = new Queue<long>();
        static readonly bool[] RealConnected = new bool[4];
        static readonly int[] RealPacket = new int[4];
        static long s_nextEmptyPoll, s_lastXInputActivity;

        public static string Mode = "Auto";

        public static void Tick()
        {
            PsHid.Tick();
            bool anyRealConnected;
            bool xinputActive = PollReal(out anyRealConnected);
            long now = DateTime.UtcNow.Ticks;
            PsHid.CopyPads(Pads);

            string mode = Mode;
            bool forceNative = string.Equals(mode, "On", StringComparison.OrdinalIgnoreCase);
            bool off = string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase);
            long lastPsTicks = 0;
            lock (Gate)
            {
                Routes.RemoveAll(r => !Pads.Contains(r.Pad));
                foreach (PsPad pad in Pads)
                {
                    Routing routing = FindRoute(pad);
                    if (routing == null)
                    {
                        routing = new Routing { Pad = pad, Judge = new MirrorJudge(anyRealConnected) };
                        if (s_lastXInputActivity != 0) routing.Judge.OnXInputActive(s_lastXInputActivity);
                        Routes.Add(routing);
                    }
                    routing.Judge.OnXInputConnectedChanged(anyRealConnected);
                    lastPsTicks = Math.Max(lastPsTicks, pad.DrainEdges(Edges));
                    while (Edges.Count != 0) routing.Judge.OnPsEdge(Edges.Dequeue());
                    if (xinputActive) routing.Judge.OnXInputActive(now);
                    PsRoute judged = routing.Judge.Tick(now);
                    routing.Route = forceNative ? PsRoute.Native : judged;
                }
                UpdateSlots(off);
            }
            if (xinputActive) s_lastXInputActivity = now;
            PadDetector.Update(now, lastPsTicks, xinputActive);
        }

        static Routing FindRoute(PsPad pad)
        {
            foreach (Routing routing in Routes)
                if (routing.Pad == pad) return routing;
            return null;
        }

        static void UpdateSlots(bool off)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                Routing routing = Slots[i];
                if (routing != null && (off || RealConnected[i] || routing.Route != PsRoute.Native || !Pads.Contains(routing.Pad)))
                    Slots[i] = null;
            }

            foreach (Routing routing in Routes)
            {
                int newSlot = -1;
                if (!off && routing.Route == PsRoute.Native && Array.IndexOf(Slots, routing) < 0)
                {
                    for (int i = 0; i < Slots.Length; i++)
                    {
                        if (RealConnected[i] || Slots[i] != null) continue;
                        Slots[i] = routing;
                        newSlot = i;
                        break;
                    }
                }
                if (newSlot < 0 && routing.Route == routing.LoggedRoute) continue;
                routing.LoggedRoute = routing.Route;
                string label = routing.Pad.Name + (routing.Pad.IsBluetooth ? " (Bluetooth)" : " (USB)");
                if (newSlot >= 0)
                    Plugin.Log.LogInfo("pads: " + label + " -> native on XInput slot " + newSlot);
                else if (routing.Route == PsRoute.Native)
                    Plugin.Log.LogInfo("pads: " + label + (off ? " -> native (mapping off)" : " -> native (no free XInput slot)"));
                else if (routing.Route == PsRoute.Mirrored)
                    Plugin.Log.LogInfo("pads: " + label + " mirrored by Steam Input or another XInput mapper, not mapped");
                else
                    Plugin.Log.LogInfo("pads: " + label + " -> unknown, waiting for input");
            }
        }

        // Bypass the game's virtual-state hook when checking which slots are really occupied.
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        static extern int GetRealState(int slot, ref XInputState state);

        static bool PollReal(out bool anyConnected)
        {
            long now = DateTime.UtcNow.Ticks;
            bool pollEmpty = now >= s_nextEmptyPoll;
            if (pollEmpty) s_nextEmptyPoll = now + 2 * TimeSpan.TicksPerSecond;
            anyConnected = false;
            bool active = false;
            for (int i = 0; i < RealConnected.Length; i++)
            {
                bool wasConnected = RealConnected[i];
                if (!wasConnected && !pollEmpty) continue;
                XInputState state = new XInputState();
                RealConnected[i] = GetRealState(i, ref state) == 0;
                if (!RealConnected[i]) continue;
                anyConnected = true;
                bool changed = wasConnected && RealPacket[i] != state.PacketNumber;
                RealPacket[i] = state.PacketNumber;
                if (changed && IsRealActive(state.Gamepad)) active = true;
            }
            return active;
        }

        static bool IsRealActive(XInputGamepad pad)
        {
            return pad.wButtons != 0 || pad.bLeftTrigger > 60 || pad.bRightTrigger > 60 ||
                   Math.Abs((int)pad.sThumbLX) > 16000 || Math.Abs((int)pad.sThumbLY) > 16000 ||
                   Math.Abs((int)pad.sThumbRX) > 16000 || Math.Abs((int)pad.sThumbRY) > 16000;
        }

        public static int GetState(int slot, ref XInputState s)
        {
            return TryGetVirtual(slot, ref s) ? 0 : XInput.XInputGetState(slot, ref s);
        }

        static PsPad MappedPad(int slot)
        {
            lock (Gate)
            {
                if (slot < 0 || slot >= Slots.Length || Slots[slot] == null) return null;
                return Slots[slot].Pad;
            }
        }

        // GlyphProbe only: a simulated PlayStation pad on TestSlot (the HID side cannot be pressed by a test).
        internal static int TestSlot = -1;
        internal static PadSnapshot TestSnapshot;
        internal static int TestPacket;

        public static bool TryGetVirtual(int slot, ref XInputState s)
        {
            if (slot == TestSlot && slot >= 0)
            {
                PadSnapshot t = TestSnapshot;
                s.PacketNumber = TestPacket;
                s.Gamepad.wButtons = unchecked((short)t.Buttons); s.Gamepad.bLeftTrigger = t.LT; s.Gamepad.bRightTrigger = t.RT;
                s.Gamepad.sThumbLX = t.LX; s.Gamepad.sThumbLY = t.LY; s.Gamepad.sThumbRX = t.RX; s.Gamepad.sThumbRY = t.RY;
                return true;
            }
            PsPad pad = MappedPad(slot);
            PadSnapshot snapshot;
            int packet;
            if (pad == null || !pad.TryRead(out snapshot, out packet)) return false;
            s.PacketNumber = packet;
            s.Gamepad.wButtons = unchecked((short)snapshot.Buttons);
            s.Gamepad.bLeftTrigger = snapshot.LT;
            s.Gamepad.bRightTrigger = snapshot.RT;
            s.Gamepad.sThumbLX = snapshot.LX;
            s.Gamepad.sThumbLY = snapshot.LY;
            s.Gamepad.sThumbRX = snapshot.RX;
            s.Gamepad.sThumbRY = snapshot.RY;
            return true;
        }

        public static string Describe(int slot)
        {
            PsPad pad = MappedPad(slot);
            PadSnapshot snapshot;
            int packet;
            return pad != null && pad.TryRead(out snapshot, out packet) ? pad.Name : null;
        }

        public static PsRoute RouteOf(int padIndex)
        {
            PsPad pad = PsHid.GetPad(padIndex);
            lock (Gate)
            {
                Routing routing = FindRoute(pad);
                return routing == null ? PsRoute.Unknown : routing.Route;
            }
        }
    }
}
