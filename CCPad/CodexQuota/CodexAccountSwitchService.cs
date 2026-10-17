using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using CCPad.Settings;

namespace CCPad.CodexQuota;

public sealed record CodexAccountSwitchRequest(string ProfileId, string AccountKey, string Home);

[JsonSerializable(typeof(CodexAccountSwitchRequest))]
internal partial class CodexAccountSwitchJsonContext : JsonSerializerContext { }

public enum CodexAccountSwitchResult { None, Applied, Busy, InvalidLogin, UnsupportedStore, StorageError }

/// <summary>Switches only authentication, after all prior local writers have exited.</summary>
public sealed class CodexAccountSwitchService
{
    private readonly CodexQuotaProfileStore _profiles;
    private readonly string _dataDirectory;
    private readonly string _requestFile;
    private readonly string _mutexName;

    public CodexAccountSwitchService(string? dataDirectory = null, string? codexHome = null)
    {
        _dataDirectory = dataDirectory ?? AppPaths.Root;
        _profiles = new CodexQuotaProfileStore(_dataDirectory, codexHome);
        _requestFile = Path.Combine(_dataDirectory, "codex-account-switch.json");
        _mutexName = "CCPad.AccountSwitch." + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(_profiles.DefaultHome.ToUpperInvariant())));
    }

    public CodexAccountSwitchRequest? ReadPending()
        => !File.Exists(_requestFile) ? null : JsonSerializer.Deserialize(
            File.ReadAllText(_requestFile), CodexAccountSwitchJsonContext.Default.CodexAccountSwitchRequest);

    // Selecting the current/native entry cancels a queued switch. This operation
    // never writes auth.json, so it is safe while terminals are running.
    public bool Request(string profileId)
    {
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            if (!Acquire(mutex)) return false;
            try
            {
                if (profileId == CodexQuotaProfileStore.DefaultId)
                {
                    WriteRequest(null);
                    return true;
                }
                if (!_profiles.TryResolveLaunchProfile(profileId, out var profile)) return false;
                var (_, identity) = ReadLogin(profile.Home);
                WriteRequest(new(profileId, identity.Key, _profiles.DefaultHome));
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch { return false; }
    }

    public string? FailureCode { get; private set; }

    public CodexAccountSwitchResult ApplyPending(Func<bool>? hasOtherWriters = null)
    {
        string phase = "read-request";
        FailureCode = null;
        try
        {
            using var mutex = new Mutex(false, _mutexName);
            if (!Acquire(mutex)) return CodexAccountSwitchResult.Busy;
            try
            {
                var request = ReadPending();
                if (request == null) return CodexAccountSwitchResult.None;
                var writers = hasOtherWriters ?? HasOtherWriters;
                if (writers()) return CodexAccountSwitchResult.Busy;
                if (!string.Equals(request.Home, _profiles.DefaultHome, StringComparison.OrdinalIgnoreCase) ||
                    !_profiles.TryResolveLaunchProfile(request.ProfileId, out var target))
                    return CodexAccountSwitchResult.InvalidLogin;
                if (!UsesFileCredentials()) return CodexAccountSwitchResult.UnsupportedStore;

                phase = "read-target";
                var (targetBytes, targetIdentity) = ReadLogin(target.Home);
                if (targetIdentity.Key != request.AccountKey) return CodexAccountSwitchResult.InvalidLogin;
                phase = "read-current";
                var (currentBytes, currentIdentity) = ReadLogin(_profiles.DefaultHome);

                // A crash after the atomic replacement may leave the request in
                // place. Never overwrite the target's newer refreshed login then.
                if (currentIdentity.Key != targetIdentity.Key)
                {
                    phase = "save-current";
                    SaveCurrentLogin(currentBytes, currentIdentity);
                    if (writers()) return CodexAccountSwitchResult.Busy;
                    // An external login/refresh between the snapshot and replacement
                    // must not be silently overwritten.
                    if (!File.ReadAllBytes(AuthFile(_profiles.DefaultHome)).AsSpan().SequenceEqual(currentBytes) ||
                        !File.ReadAllBytes(AuthFile(target.Home)).AsSpan().SequenceEqual(targetBytes))
                        return CodexAccountSwitchResult.Busy;
                    phase = "activate-target";
                    WritePrivateFile(AuthFile(_profiles.DefaultHome), targetBytes);
                }
                phase = "clear-request";
                WriteRequest(null);
                return CodexAccountSwitchResult.Applied;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (InvalidDataException)
        {
            FailureCode = phase + ":invalid-login";
            return CodexAccountSwitchResult.InvalidLogin;
        }
        catch (Exception ex)
        {
            FailureCode = phase + ":" + ex.GetType().Name;
            return CodexAccountSwitchResult.StorageError;
        }
    }

    private void SaveCurrentLogin(byte[] bytes, CodexAccountIdentity identity)
    {
        var saved = _profiles.Load().FirstOrDefault(p => CodexAccountIdentity.Read(p.Home)?.Key == identity.Key);
        var directory = saved?.Home ?? Path.Combine(_dataDirectory, "codex-accounts", "saved-" + identity.Key);
        var original = new DirectoryInfo(_profiles.DefaultHome);
        var backup = new DirectoryInfo(directory);
        if (string.Equals(original.ResolveLinkTarget(true)?.FullName ?? original.FullName,
            (backup.Exists ? backup.ResolveLinkTarget(true)?.FullName : null) ?? backup.FullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Saved account must have its own credential directory");
        if (saved == null)
        {
            CreatePrivateDirectory(directory);
            var existing = CodexAccountIdentity.Read(directory);
            if (File.Exists(AuthFile(directory)) && existing?.Key != identity.Key)
                throw new InvalidDataException("Saved login identity changed");
        }
        WritePrivateFile(AuthFile(directory), bytes);
        if (!File.ReadAllBytes(AuthFile(directory)).AsSpan().SequenceEqual(bytes))
            throw new IOException("Saved login could not be verified");
        if (saved == null && _profiles.TryRememberCurrentAccount(
            identity.Email ?? "Account " + identity.Key[..8], directory, out _) != CodexQuotaProfileError.None)
            throw new IOException("Saved account could not be registered");
    }

    private bool UsesFileCredentials()
    {
        var config = Path.Combine(_profiles.DefaultHome, "config.toml");
        if (!File.Exists(config)) return true;
        foreach (var line in File.ReadLines(config))
        {
            var value = line.Trim();
            if (value.StartsWith("[", StringComparison.Ordinal)) break;
            if (value.StartsWith("#", StringComparison.Ordinal)) continue;
            if (value.Contains("cli_auth_credentials_store", StringComparison.Ordinal) &&
                !Regex.IsMatch(value, "^cli_auth_credentials_store\\s*=\\s*['\"]file['\"]\\s*(#.*)?$"))
                return false;
        }
        return true;
    }

    private static (byte[] Bytes, CodexAccountIdentity Identity) ReadLogin(string directory)
    {
        if (!File.Exists(AuthFile(directory))) throw new InvalidDataException("Saved login is missing");
        var bytes = File.ReadAllBytes(AuthFile(directory));
        var identity = CodexAccountIdentity.FromJson(bytes) ?? throw new InvalidDataException("ChatGPT login required");
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.GetProperty("tokens").TryGetProperty("refresh_token", out var refresh) ||
            refresh.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(refresh.GetString()))
            throw new InvalidDataException("Reusable ChatGPT login required");
        return (bytes, identity);
    }

    private static string AuthFile(string directory) => Path.Combine(directory, "auth.json");

    private void WriteRequest(CodexAccountSwitchRequest? request)
    {
        Directory.CreateDirectory(_dataDirectory);
        var temporary = _requestFile + ".tmp";
        File.WriteAllText(temporary, request == null ? "null" : JsonSerializer.Serialize(request,
            CodexAccountSwitchJsonContext.Default.CodexAccountSwitchRequest));
        File.Move(temporary, _requestFile, overwrite: true);
    }

    private static void CreatePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WritePrivateFile(string destination, byte[] bytes)
    {
        // Replacing a credential symlink would detach it from its owning provider.
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked authentication is not supported");
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".ccpad-auth-" + Guid.NewGuid().ToString("N"));
        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write,
                FileShare.None, 4096, FileOptions.None, security);
        }
        else stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        using (stream)
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }

    private static bool Acquire(Mutex mutex)
    {
        try { return mutex.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool HasOtherWriters()
    {
        foreach (var name in new[] { "CCPad", "codex" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (process.Id != Environment.ProcessId && !process.HasExited) return true;
        return false;
    }
}
