using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace OriCoop.Pads
{
    public static class PsHid
    {
        static readonly ushort[] SonyPids = { 0x05C4, 0x09CC, 0x0BA0, 0x0CE6, 0x0DF2 };
        static readonly object Gate = new object();
        static readonly List<PsPad> Pads = new List<PsPad>();
        static long s_nextScan;
        static bool s_stopped;

        public static int Count { get { lock (Gate) return Pads.Count; } }

        public static PsPad GetPad(int padIndex)
        {
            lock (Gate) return padIndex >= 0 && padIndex < Pads.Count ? Pads[padIndex] : null;
        }

        internal static void CopyPads(List<PsPad> list)
        {
            lock (Gate)
            {
                list.Clear();
                list.AddRange(Pads);
            }
        }

        public static void Tick()
        {
            long now = DateTime.UtcNow.Ticks;
            lock (Gate)
            {
                if (s_stopped || now < s_nextScan) return;
                s_nextScan = now + 3 * TimeSpan.TicksPerSecond;
            }
            try { Scan(); }
            catch (Exception e) { Plugin.Log.LogWarning("pads: HID scan failed: " + e.Message); }
        }

        public static void Shutdown()
        {
            PsPad[] pads;
            lock (Gate)
            {
                s_stopped = true;
                pads = Pads.ToArray();
                Pads.Clear();
            }
            foreach (PsPad pad in pads) pad.Stop();
        }

        internal static void Remove(PsPad pad)
        {
            lock (Gate) Pads.Remove(pad);
        }

        static void Scan()
        {
            Guid guid;
            HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.Size = Marshal.SizeOf(data);
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    int size;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(size);
                    string path = null;
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : (Marshal.SystemDefaultCharSize == 2 ? 6 : 5));
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero))
                            path = Marshal.PtrToStringAuto(new IntPtr(detail.ToInt64() + 4));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                    if (path == null || path.IndexOf("054c", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    lock (Gate)
                    {
                        if (Pads.Exists(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    TryOpen(path);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        static void TryOpen(string path)
        {
            ushort pid, inputLength;
            using (SafeFileHandle probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (probe.IsInvalid) return;
                var attributes = new HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(attributes);
                if (!HidD_GetAttributes(probe, ref attributes) || attributes.VendorID != 0x054C ||
                    Array.IndexOf(SonyPids, attributes.ProductID) < 0) return;
                IntPtr preparsed;
                if (!HidD_GetPreparsedData(probe, out preparsed)) return;
                HIDP_CAPS caps;
                try { HidP_GetCaps(preparsed, out caps); }
                finally { HidD_FreePreparsedData(preparsed); }
                if (caps.UsagePage != 1 || (caps.Usage != 4 && caps.Usage != 5) || caps.InputLength < 10) return;
                pid = attributes.ProductID;
                inputLength = caps.InputLength;
            }

            // GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED.
            SafeFileHandle handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                Plugin.Log.LogWarning("pads: could not open PlayStation HID (error " + error + ")");
                return;
            }

            var pad = new PsPad(path, pid, inputLength, handle);
            lock (Gate)
            {
                if (s_stopped) { handle.Dispose(); return; }
                Pads.Add(pad);
                try { pad.Start(); }
                catch
                {
                    Pads.Remove(pad);
                    handle.Dispose();
                    throw;
                }
            }
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Auto)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr device);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, Version; }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort a, b, c, d, e, f, g, h, i, j;
        }
    }

    public sealed class PsPad
    {
        readonly object m_gate = new object();
        readonly SafeFileHandle m_handle;
        readonly int m_inputLength;
        readonly bool m_dualSenseUsb;
        readonly Queue<long> m_edges = new Queue<long>();
        PadSnapshot m_latest;
        int m_packet;
        long m_lastActiveTicks;
        bool m_activeNow, m_stopped;

        internal string Path { get; private set; }
        public string Name { get; private set; }
        public bool IsBluetooth { get; private set; }

        internal PsPad(string path, ushort pid, int inputLength, SafeFileHandle handle)
        {
            Path = path;
            IsBluetooth = path.IndexOf("00001124", StringComparison.OrdinalIgnoreCase) >= 0;
            bool dualSense = pid == 0x0CE6 || pid == 0x0DF2;
            Name = dualSense ? "DualSense" : "DualShock 4";
            m_dualSenseUsb = dualSense && !IsBluetooth;
            m_inputLength = inputLength;
            m_handle = handle;
        }

        public bool TryRead(out PadSnapshot snapshot, out int packet)
        {
            lock (m_gate)
            {
                snapshot = m_latest;
                packet = m_packet;
                return !m_stopped;
            }
        }

        internal long DrainEdges(Queue<long> destination)
        {
            lock (m_gate)
            {
                while (m_edges.Count != 0) destination.Enqueue(m_edges.Dequeue());
                return m_lastActiveTicks;
            }
        }

        internal void Start()
        {
            new Thread(ReadLoop) { IsBackground = true, Name = "OriCoop HID" }.Start();
        }

        internal void Stop()
        {
            lock (m_gate)
            {
                if (m_stopped) return;
                m_stopped = true;
                try { CancelIoEx(m_handle, IntPtr.Zero); }
                catch (Exception e) { Plugin.Log.LogWarning("pads: HID cancel failed: " + e.Message); }
            }
        }

        void ReadLoop()
        {
            try
            {
                var report = new byte[m_inputLength];
                IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                try
                {
                    var buffer = GCHandle.Alloc(report, GCHandleType.Pinned);
                    try
                    {
                        // With only one outstanding read, the file handle itself signals completion.
                        Marshal.StructureToPtr(new NativeOverlapped(), overlapped, false);
                        IntPtr address = buffer.AddrOfPinnedObject();
                        while (BeginRead(address, overlapped))
                        {
                            int length;
                            if (!GetOverlappedResult(m_handle, overlapped, out length, true)) break;
                            PadSnapshot snapshot;
                            if (PsReport.TryParse(report, length, m_dualSenseUsb, out snapshot)) Publish(snapshot);
                        }
                    }
                    finally { buffer.Free(); }
                }
                finally { Marshal.FreeHGlobal(overlapped); }
            }
            catch (Exception e) { Plugin.Log.LogWarning("pads: HID read failed: " + e.Message); }
            finally
            {
                lock (m_gate) m_stopped = true;
                PsHid.Remove(this);
                m_handle.Dispose();
            }
        }

        bool BeginRead(IntPtr buffer, IntPtr overlapped)
        {
            lock (m_gate)
            {
                if (m_stopped) return false;
                int unused;
                return ReadFile(m_handle, buffer, m_inputLength, out unused, overlapped) ||
                       Marshal.GetLastWin32Error() == 997; // ERROR_IO_PENDING
            }
        }

        void Publish(PadSnapshot s)
        {
            bool active = PsReport.IsActive(s);
            lock (m_gate)
            {
                if (s.Buttons != m_latest.Buttons || s.LT != m_latest.LT || s.RT != m_latest.RT ||
                    s.LX != m_latest.LX || s.LY != m_latest.LY || s.RX != m_latest.RX || s.RY != m_latest.RY)
                {
                    m_latest = s;
                    m_packet = unchecked(m_packet + 1);
                }
                if (active)
                {
                    m_lastActiveTicks = DateTime.UtcNow.Ticks;
                    if (!m_activeNow) m_edges.Enqueue(m_lastActiveTicks);
                }
                m_activeNow = active;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int count, out int read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out int read, bool wait);
        [DllImport("kernel32.dll")] static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}
