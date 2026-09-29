using System;
using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static CCPad.Terminal.PseudoConsoleApi;

namespace CCPad.Terminal
{
    internal class ConPtySession : IDisposable
    {
        private IntPtr _hPC = IntPtr.Zero;
        private IntPtr _hProcess = IntPtr.Zero;
        private IntPtr _hThread = IntPtr.Zero;
        private IntPtr _hPipeInWrite = IntPtr.Zero;
        private IntPtr _hPipeOutRead = IntPtr.Zero;
        private IntPtr _attributeList = IntPtr.Zero;
        private volatile bool _disposed;
        private volatile bool _consoleReady;
        private readonly object _procLock = new();

        public event Action<byte[]>? OutputReceived;

        /// <summary>
        /// Fires when the CURRENT child process exits. The pseudoconsole stays
        /// alive, so the consumer can spawn a follow-up process (e.g. a shell)
        /// on the same console without losing scrollback. Invoked on a
        /// background thread.
        /// </summary>
        public event Action? ProcessExited;

        /// <summary>
        /// Fires when the output pipe hits EOF — i.e. the pseudoconsole itself
        /// is being torn down (typically during Dispose).
        /// </summary>
        public event Action? Exited;

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        public static ConPtySession Start(string command, int cols, int rows, string? workingDir = null)
        {
            var session = new ConPtySession { Cols = cols, Rows = rows };
            session.InitConsole(cols, rows);
            session.SpawnProcess(command, workingDir);
            return session;
        }

        /// <summary>
        /// One-time setup: create the pipes + pseudoconsole and start pumping
        /// output. The console outlives individual child processes.
        /// </summary>
        private void InitConsole(int cols, int rows)
        {
            if (!CreatePipe(out var hPipeInRead, out _hPipeInWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"CreatePipe(stdin) failed: {Marshal.GetLastWin32Error()}");

            if (!CreatePipe(out _hPipeOutRead, out var hPipeOutWrite, IntPtr.Zero, 0))
                throw new InvalidOperationException($"CreatePipe(stdout) failed: {Marshal.GetLastWin32Error()}");

            int hr = CreatePseudoConsole(
                new COORD { X = (short)cols, Y = (short)rows },
                hPipeInRead, hPipeOutWrite, 0, out _hPC);

            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");

            // These ends are now owned by the PTY
            CloseHandle(hPipeInRead);
            CloseHandle(hPipeOutWrite);

            BuildAttributeList();
            _consoleReady = true;

            Task.Factory.StartNew(ReadOutput, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Launch a process attached to this console. Can be called repeatedly:
        /// after one child exits, spawn the next (e.g. a fallback shell) on the
        /// same console so previous output (scrollback) is preserved.
        /// </summary>
        public void SpawnProcess(string command, string? workingDir = null)
        {
            if (_disposed || !_consoleReady) return;

            var siEx = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                lpAttributeList = _attributeList
            };

            string dir = workingDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            IntPtr envBlock = BuildUtf8EnvironmentBlock();
            IntPtr childToken = IntPtr.Zero;
            try
            {
                bool ok;
                PROCESS_INFORMATION pi;

                // An elevated CC Pad would otherwise pass its administrator token
                // into cmd.exe and every CLI it launches. Codex deliberately rejects
                // that shape because its shared Windows daemon must be started by a
                // non-elevated client. Reuse the user's linked (medium-integrity)
                // token whenever the host process is elevated; the normal path stays
                // on CreateProcess and keeps the existing behaviour unchanged.
                if (TryGetUnelevatedToken(out childToken))
                {
                    // The token gives the child the right identity and integrity.
                    // Keep the explicit UTF-8 variables from the normal environment
                    // block, since they are part of CC Pad's terminal contract.
                    ok = CreateProcessAsUser(
                        childToken, null, new StringBuilder(command),
                        IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envBlock, dir,
                        ref siEx, out pi);
                }
                else
                {
                    ok = CreateProcess(
                        null, command,
                        IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                        envBlock, dir,
                        ref siEx, out pi);
                }

                if (!ok)
                    throw new InvalidOperationException($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

                lock (_procLock)
                {
                    _hProcess = pi.hProcess;
                    _hThread = pi.hThread;
                }
            }
            finally
            {
                if (childToken != IntPtr.Zero)
                    CloseHandle(childToken);
                Marshal.FreeHGlobal(envBlock);
            }

            Task.Factory.StartNew(() => WaitForProcessExit(_hProcess), TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Returns a primary token for the current interactive user at medium
        /// integrity when this process is elevated. Windows keeps that token as
        /// TokenLinkedToken for UAC-split administrators. If no linked token is
        /// available, returning false preserves the old CreateProcess path.
        /// </summary>
        private static bool TryGetUnelevatedToken(out IntPtr token)
        {
            token = IntPtr.Zero;
            IntPtr currentToken = IntPtr.Zero;
            IntPtr linkedToken = IntPtr.Zero;
            IntPtr restrictedToken = IntPtr.Zero;
            IntPtr info = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out currentToken))
                    return false;

                int size = 0;
                GetTokenInformation(currentToken, TokenElevation, IntPtr.Zero, 0, out size);
                if (size <= 0) return false;
                info = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(currentToken, TokenElevation, info, size, out _))
                    return false;

                // TOKEN_ELEVATION.TokenIsElevated is the first DWORD.
                if (Marshal.ReadInt32(info) == 0)
                    return false;

                // UAC-split administrators expose the desired medium-integrity
                // token here. Some machines run with UAC disabled (or use a full
                // administrator token), in which case TokenLinkedToken is absent;
                // fall through to CreateRestrictedToken below.
                Marshal.FreeHGlobal(info);
                info = IntPtr.Zero;
                size = 0;
                GetTokenInformation(currentToken, TokenLinkedToken, IntPtr.Zero, 0, out size);
                if (size > 0)
                {
                    info = Marshal.AllocHGlobal(size);
                    if (GetTokenInformation(currentToken, TokenLinkedToken, info, size, out _))
                    {
                        linkedToken = Marshal.ReadIntPtr(info);
                        if (linkedToken != IntPtr.Zero && DuplicateTokenEx(
                                linkedToken, TOKEN_REQUIRED_FOR_CHILD,
                                IntPtr.Zero, SecurityImpersonation, TokenPrimary,
                                out token))
                            return true;
                    }
                }

                // There is no linked token on a full administrator session. Make a
                // LUA-style restricted token instead; this removes administrator
                // privileges and reports TokenElevation=0 to child applications.
                if (!CreateRestrictedToken(
                        currentToken, DISABLE_MAX_PRIVILEGE | LUA_TOKEN,
                        0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero,
                        out restrictedToken))
                    return false;

                return DuplicateTokenEx(
                    restrictedToken, TOKEN_REQUIRED_FOR_CHILD,
                    IntPtr.Zero, SecurityImpersonation, TokenPrimary,
                    out token);
            }
            catch
            {
                if (token != IntPtr.Zero)
                {
                    CloseHandle(token);
                    token = IntPtr.Zero;
                }
                return false;
            }
            finally
            {
                if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                if (linkedToken != IntPtr.Zero) CloseHandle(linkedToken);
                if (restrictedToken != IntPtr.Zero) CloseHandle(restrictedToken);
                if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
            }
        }

        /// <summary>
        /// Block on the child's process handle. Unlike the output pipe, this is
        /// reliable: the pseudoconsole host keeps the pipe's write end open after
        /// the child dies, so the pipe never hits EOF on its own.
        /// </summary>
        private void WaitForProcessExit(IntPtr hProcess)
        {
            if (hProcess == IntPtr.Zero) return;
            WaitForSingleObject(hProcess, INFINITE);
            CloseProcessHandles();
            if (!_disposed)
                ProcessExited?.Invoke();
        }

        private void CloseProcessHandles()
        {
            lock (_procLock)
            {
                if (_hProcess != IntPtr.Zero) { CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
                if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
            }
        }

        private void BuildAttributeList()
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

            _attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size))
                throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            // Pass the HPCON handle DIRECTLY — not a pointer to it.
            // This matches the official Microsoft MiniTerm sample.
            if (!UpdateProcThreadAttribute(
                    _attributeList, 0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
        }

        private void ReadOutput()
        {
            var buffer = new byte[4096];
            try
            {
                while (!_disposed)
                {
                    bool ok = ReadFile(_hPipeOutRead, buffer, buffer.Length, out int n, IntPtr.Zero);
                    if (!ok || n == 0) break;

                    var chunk = new byte[n];
                    Array.Copy(buffer, chunk, n);
                    OutputReceived?.Invoke(chunk);
                }
            }
            catch { }
            finally
            {
                if (!_disposed)
                    Exited?.Invoke();
            }
        }

        public void WriteInput(string text)
        {
            if (_hPipeInWrite == IntPtr.Zero || _disposed) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            WriteFile(_hPipeInWrite, bytes, bytes.Length, out _, IntPtr.Zero);
        }

        public void Resize(int cols, int rows)
        {
            if (_hPC == IntPtr.Zero || _disposed) return;
            Cols = cols;
            Rows = rows;
            ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
        }

        /// <summary>
        /// Build a Unicode environment block that inherits the current process's
        /// environment and declares this terminal's capabilities and UTF-8 locale.
        /// </summary>
        private static IntPtr BuildUtf8EnvironmentBlock()
        {
            var env = Environment.GetEnvironmentVariables();
            // Every child is attached to ConPTY rendered by xterm.js. A launcher
            // can supply TERM=dumb (or another terminal's type); forwarding that
            // makes local Codex warn and remote tmux reject the SSH terminal.
            // Set it for every spawn, including fallback shells and reconnects.
            env["TERM"] = "xterm-256color";
            // Force UTF-8 locale for child processes (git, node, bash, etc.)
            env["LANG"] = "en_US.UTF-8";
            env["LC_ALL"] = "en_US.UTF-8";
            env["PYTHONUTF8"] = "1";
            env["PYTHONIOENCODING"] = "utf-8";

            // Environment block: sorted KEY=VALUE\0 pairs, terminated by extra \0
            var keys = new string[env.Count];
            env.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            foreach (var key in keys)
            {
                sb.Append(key).Append('=').Append(env[key]).Append('\0');
            }
            sb.Append('\0'); // double-null terminator

            var bytes = Encoding.Unicode.GetBytes(sb.ToString());
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            return ptr;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Closing the pseudoconsole signals the attached child to exit and
            // unblocks the ReadOutput pump. The WaitForProcessExit thread closes
            // the process/thread handles once the child actually goes away.
            if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
            CloseProcessHandles();
            if (_hPipeInWrite != IntPtr.Zero) { CloseHandle(_hPipeInWrite); _hPipeInWrite = IntPtr.Zero; }
            if (_hPipeOutRead != IntPtr.Zero) { CloseHandle(_hPipeOutRead); _hPipeOutRead = IntPtr.Zero; }
            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }
        }
    }
}
