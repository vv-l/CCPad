using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CCPad.Settings
{
    /// <summary>
    /// System-pressure probes behind the "too many windows" warning. Every CC Pad
    /// window drags a chain of WebView2 renderer processes plus the CLI's node
    /// processes, so a handful of windows can exhaust physical memory and freeze
    /// every instance at once — including their close dialogs. These checks let a
    /// window warn before that point.
    /// </summary>
    public static class ResourceGuard
    {
        /// <summary>Instance count at/above which a newly launched window warns.</summary>
        public const int WarnInstanceCount = 6;

        /// <summary>Memory load (%) at/above which a newly launched window warns.</summary>
        public const int WarnLoadAtLaunch = 85;

        /// <summary>Memory load (%) at/above which a running window warns.</summary>
        public const int WarnLoadRuntime = 90;

        /// <summary>Runtime warning re-arms once load falls back below this.</summary>
        public const int RearmBelowLoad = 80;

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        /// <summary>System-wide physical memory load in percent (0–100), and GB still available. (0, 0) if the query fails.</summary>
        public static (int LoadPercent, double AvailGb) MemorySnapshot()
        {
            try
            {
                var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
                return ((int)status.dwMemoryLoad, status.ullAvailPhys / (1024.0 * 1024 * 1024));
            }
            catch { return (0, 0); }
        }

        /// <summary>Number of CC Pad main processes currently running (this one included).</summary>
        public static int CountInstances()
        {
            try
            {
                var procs = Process.GetProcessesByName("CCPad");
                int n = procs.Length;
                foreach (var p in procs) p.Dispose();
                return n;
            }
            catch { return 1; }
        }
    }
}
