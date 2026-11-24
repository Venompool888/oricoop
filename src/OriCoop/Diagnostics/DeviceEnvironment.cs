using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OriCoop.Authority;

namespace OriCoop.Diagnostics
{
    internal static class DeviceEnvironment
    {
        internal static Dictionary<string, string> Collect(string gameLanguage, string gameDirectory, string saveDirectory)
        {
            Dictionary<string, string> fields = new Dictionary<string, string>();
            Put(fields, "gameLang", gameLanguage);
            Try(delegate
            {
                VersionInfo info = new VersionInfo();
                info.Size = Marshal.SizeOf(typeof(VersionInfo));
                if (RtlGetVersion(ref info) == 0)
                    Put(fields, "os", new Version(info.Major, info.Minor, info.Build).ToString());
            });
            Try(delegate
            {
                // GetNativeSystemInfo reports the OS architecture even in 32-bit Ori.
                NativeSystemInfo info;
                GetNativeSystemInfo(out info);
                string arch = null;
                switch (info.Architecture)
                {
                    case 0: arch = "x86"; break;
                    case 5: arch = "arm"; break;
                    case 6: arch = "ia64"; break;
                    case 9: arch = "x64"; break;
                    case 12: arch = "arm64"; break;
                }
                Put(fields, "arch", arch);
            });
            Try(delegate { Put(fields, "systemLang", CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).Name); });
            Try(delegate
            {
                Zone zone;
                uint state = GetDynamicTimeZoneInformation(out zone);
                if (state == uint.MaxValue) return;
                Put(fields, "timezone", zone.KeyName);
                int offset = -zone.Bias;
                if (state == 1) offset -= zone.StandardBias;
                if (state == 2) offset -= zone.DaylightBias;
                Put(fields, "utcOffset", offset.ToString(CultureInfo.InvariantCulture));
            });

            string gameRoot = null;
            string gameVolume = null;
            Try(delegate
            {
                gameRoot = VolumeRoot(gameDirectory);
                gameVolume = VolumeKey(gameRoot);
                AddDisk(fields, "game", gameRoot);
            });
            Try(delegate
            {
                string saveRoot = VolumeRoot(saveDirectory);
                if (saveRoot == null || string.Equals(gameRoot, saveRoot, StringComparison.OrdinalIgnoreCase)) return;
                if (gameVolume != null && string.Equals(gameVolume, VolumeKey(saveRoot), StringComparison.OrdinalIgnoreCase)) return;
                AddDisk(fields, "save", saveRoot);
            });
            return fields;
        }

        static void Try(Action read)
        {
            try { read(); } catch (Exception) { } // An unavailable field does not invalidate the report.
        }

        static void Put(Dictionary<string, string> fields, string key, string value)
        {
            if (JoinDiagnosticsFields.Valid(key, value)) fields[key] = value;
        }

        static string VolumeRoot(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return null;
            StringBuilder root = new StringBuilder(1024);
            return GetVolumePathName(path, root, root.Capacity) ? root.ToString() : null;
        }

        // A volume can have several mount points. The local comparison key is never
        // returned to the caller or included among the report fields.
        static string VolumeKey(string root)
        {
            if (root == null) return null;
            StringBuilder key = new StringBuilder(64);
            return GetVolumeNameForVolumeMountPoint(root, key, key.Capacity) ? key.ToString() : null;
        }

        static void AddDisk(Dictionary<string, string> fields, string prefix, string root)
        {
            if (root == null) return;
            StringBuilder fs = new StringBuilder(64);
            if (GetVolumeInformation(root, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, fs, fs.Capacity))
                Put(fields, prefix + "Fs", fs.ToString());
            long available;
            if (GetDiskFreeSpaceEx(root, out available, IntPtr.Zero, IntPtr.Zero) && available >= 0)
                Put(fields, prefix + "Free", FreeBucket(available));
        }

        internal static string FreeBucket(long bytes)
        {
            long gib = bytes >> 30;
            if (gib < 1) return "lt1GiB";
            if (gib < 10) return "1to10GiB";
            return gib < 50 ? "10to50GiB" : "ge50GiB";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct VersionInfo
        {
            internal int Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeSystemInfo
        {
            internal ushort Architecture, Reserved;
            internal uint PageSize;
            internal IntPtr MinAddress, MaxAddress, ProcessorMask;
            internal uint ProcessorCount, ProcessorType, Granularity;
            internal ushort Level, Revision;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Zone
        {
            internal int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string StandardName;
            internal SystemTime StandardDate;
            internal int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DaylightName;
            internal SystemTime DaylightDate;
            internal int DaylightBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string KeyName;
            internal byte Disabled;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct SystemTime { internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern int RtlGetVersion(ref VersionInfo info);
        [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(out NativeSystemInfo info);
        [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern uint GetDynamicTimeZoneInformation(out Zone zone);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetVolumePathName(string file, StringBuilder root, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetDiskFreeSpaceEx(string root, out long available, IntPtr total, IntPtr totalFree);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool GetVolumeInformation(string root, IntPtr label, int labelSize, IntPtr serial, IntPtr maxComponent, IntPtr flags, StringBuilder fs, int fsSize);
    }
}
