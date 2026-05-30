using System;
using System.Runtime.InteropServices;

namespace CCPad.Terminal
{
    internal static class PseudoConsoleApi
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            IntPtr lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(
            COORD size,
            IntPtr hInput,
            IntPtr hOutput,
            uint dwFlags,
            out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(
            IntPtr hFile,
            byte[] lpBuffer,
            int nNumberOfBytesToRead,
            out int lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            int nNumberOfBytesToWrite,
            out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        internal const uint INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }
    }
}
