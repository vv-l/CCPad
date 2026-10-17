using System.Diagnostics;
using System.Text.Json;

// GUI launcher: launched by the user's desktop, so the WinUI process inherits its interactive session.
// It starts only --quota-overview, never a terminal or a reset/login action.
try
{
    const string root = @"D:\CC Pad";
    var current = Path.Combine(root, "current", "CCPad.exe");
    var currentVersion = File.Exists(current) ? FileVersionInfo.GetVersionInfo(current).FileVersion : null;
    string target;
    if (Version.TryParse(currentVersion, out var version) && version >= new Version(1, 10, 34, 0))
        target = current;
    else
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "target.json")));
        target = Path.GetFullPath(config.RootElement.GetProperty("executable").GetString()!);
        var appRoot = Path.GetFullPath(Path.Combine(root, "app")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(target), "CCPad.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
    }
    Process.Start(new ProcessStartInfo
    {
        FileName = target, Arguments = "--quota-overview", WorkingDirectory = Path.GetDirectoryName(target)!,
        UseShellExecute = false,
    });
    return 0;
}
catch (Exception error)
{
    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"), error.GetType().Name); }
    catch { }
    return 1;
}
