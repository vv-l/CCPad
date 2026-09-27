using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CCPad.Terminal
{
    internal static class PseudoConsoleApi
    {
        internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        internal static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        internal const uint TOKEN_DUPLICATE = 0x0002;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        internal const uint TOKEN_ADJUST_SESSIONID = 0x0100;
        internal const uint TOKEN_REQUIRED_FOR_CHILD = TOKEN_ASSIGN_PRIMARY |
            TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
        internal const uint DISABLE_MAX_PRIVILEGE = 0x0001;
        internal const uint LUA_TOKEN = 0x0004;

        internal const int SecurityImpersonation = 2;
        internal const int TokenPrimary = 1;
        internal const int TokenElevation = 20;
        internal const int TokenLinkedToken = 19;

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

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(
            IntPtr existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr duplicateToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateRestrictedToken(
            IntPtr existingToken,
            uint flags,
            uint disabledSidCount,
            IntPtr sidsToDisable,
            uint deletedPrivilegeCount,
            IntPtr privilegesToDelete,
            uint restrictedSidCount,
            IntPtr sidsToRestrict,
            out IntPtr newToken);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUser(
            IntPtr token,
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref STARTUPINFOEX startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

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
