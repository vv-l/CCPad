using System;
using System.IO;

namespace CCPad.Settings;

/// <summary>Prevents two CCPad windows from resuming the same local thread.</summary>
public static class CodexSessionLease
{
    public static IDisposable? TryAcquire(string codexHome, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id)) return null;
        try
        {
            var directory = Path.Combine(codexHome, ".ccpad-session-locks");
            Directory.CreateDirectory(directory);
            // Keep the file after closing the handle: unlinking a lock file can
            // let competing processes lock different files at the same path.
            return new FileStream(Path.Combine(directory, id.ToString("D") + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
