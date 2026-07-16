using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CCPad.Settings
{
    /// <summary>
    /// System-pressure probes behind the "too many windows" warning. Every CC Pad
    /// window brings WebView2 renderer processes plus CLI processes, so a handful
    /// of windows can exhaust the system commit limit and freeze every instance.
    /// </summary>
    public static class ResourceGuard
    {
        /// <summary>Instance count at/above which a newly launched window warns.</summary>
        public const int WarnInstanceCount = 6;

        /// <summary>Physical-memory load (%) at/above which a newly launched window warns.</summary>
        public const int WarnPhysicalLoadAtLaunch = 85;

        /// <summary>Physical-memory load (%) at/above which a running window warns.</summary>
        public const int WarnPhysicalLoadRuntime = 90;

        /// <summary>Commit load (%) at/above which a newly launched window warns.</summary>
        public const double WarnCommitLoadAtLaunch = 85;

        /// <summary>Commit load (%) at/above which a running window warns.</summary>
        public const double WarnCommitLoadRuntime = 92;

        /// <summary>Warn whenever less than this much commit headroom remains.</summary>
        public const double WarnCommitAvailableGb = 2;

        /// <summary>Runtime warning re-arms once both load metrics fall below this.</summary>
        public const double RearmBelowLoad = 80;

        /// <summary>Commit headroom required before a warning episode re-arms.</summary>
        public const double RearmCommitAvailableGb = 3;

        public readonly struct ResourceSnapshot
        {
            public ResourceSnapshot(
                int physicalLoadPercent,
                double physicalAvailableGb,
                double commitLoadPercent,
                double commitAvailableGb)
            {
                PhysicalLoadPercent = physicalLoadPercent;
                PhysicalAvailableGb = physicalAvailableGb;
                CommitLoadPercent = commitLoadPercent;
                CommitAvailableGb = commitAvailableGb;
            }

            public int PhysicalLoadPercent { get; }
            public double PhysicalAvailableGb { get; }
            public double CommitLoadPercent { get; }
            public double CommitAvailableGb { get; }
            public bool IsValid => PhysicalLoadPercent > 0 || CommitLoadPercent > 0;
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetPerformanceInfo(
            out PERFORMANCE_INFORMATION performanceInformation,
            uint size);

        /// <summary>
        /// Captures both physical-memory pressure and system commit pressure.
        /// Commit can be exhausted even while physical RAM still appears available.
        /// The probes fail independently so either useful metric can still be used.
        /// </summary>
        public static ResourceSnapshot CaptureSnapshot()
        {
            int physicalLoad = 0;
            double physicalAvailableGb = 0;
            double commitLoad = 0;
            double commitAvailableGb = 0;

            try
            {
                var status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
                };
                if (GlobalMemoryStatusEx(ref status))
                {
                    physicalLoad = (int)status.dwMemoryLoad;
                    physicalAvailableGb = BytesToGb(status.ullAvailPhys);
                }
            }
            catch { }

            try
            {
                uint size = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>();
                if (GetPerformanceInfo(out var performance, size))
                {
                    ulong totalPages = performance.CommitTotal.ToUInt64();
                    ulong limitPages = performance.CommitLimit.ToUInt64();
                    ulong pageSize = performance.PageSize.ToUInt64();

                    if (limitPages > 0 && pageSize > 0)
                    {
                        commitLoad = totalPages * 100.0 / limitPages;
                        ulong availablePages = limitPages > totalPages
                            ? limitPages - totalPages
                            : 0;
                        commitAvailableGb = BytesToGb(availablePages * (double)pageSize);
                    }
                }
            }
            catch { }

            return new ResourceSnapshot(
                physicalLoad,
                physicalAvailableGb,
                commitLoad,
                commitAvailableGb);
        }

        private static double BytesToGb(double bytes)
            => bytes / (1024.0 * 1024 * 1024);

        /// <summary>Number of CC Pad main processes currently running (this one included).</summary>
        public static int CountInstances()
        {
            try
            {
                var processes = Process.GetProcessesByName("CCPad");
                int count = processes.Length;
                foreach (var process in processes) process.Dispose();
                return count;
            }
            catch { return 1; }
        }
    }
}
