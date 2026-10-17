using System.Diagnostics;
using System.Text.Json;

// Interactive-session launcher for the real footer, with terminal creation disabled.
try
{
    const string root = @"D:\CC Pad";
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "target.json")));
    var target = Path.GetFullPath(config.RootElement.GetProperty("executable").GetString()!);
    var dataDirectory = Path.GetFullPath(config.RootElement.GetProperty("dataDirectory").GetString()!);
    if (!target.StartsWith(Path.Combine(root, "app") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(target) != "CCPad.exe" ||
        !dataDirectory.StartsWith(Path.Combine(root, "research") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException();
    var start = new ProcessStartInfo
    {
        FileName = target, Arguments = "--quota-toolbar-preview", WorkingDirectory = Path.GetDirectoryName(target)!,
        UseShellExecute = false,
    };
    start.Environment["CCPAD_DATA_DIR"] = dataDirectory;
    start.Environment["CODEX_HOME"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    start.Environment["CCPAD_CODEX_RESET_DRY_RUN"] = "1"; // UI checks cannot consume a credit.
    Process.Start(start);
    return 0;
}
catch (Exception error)
{
    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launch-error.log"), error.GetType().Name); }
    catch { }
    return 1;
}
