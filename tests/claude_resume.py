"""Run the real exit-banner and hook handlers against isolated Claude sessions.

python tests/claude_resume.py [--source path/to/TerminalPane.xaml.cs]
No CLI, network, WinUI window, or real Claude profile is used. --source lets the
same regression scenarios run against a saved pre-fix version.
"""
import argparse
from pathlib import Path
import re
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=Path(__file__).resolve().parents[1]
                    / 'CCPad/TerminalPane.xaml.cs')
args = parser.parse_args()
source = args.source.read_text(encoding='utf-8-sig')


def method(name, optional=False):
    # Members use eight-space indentation; nested closing braces are deeper.
    match = re.search(r'^        private (?:static )?[^\n]+\b' + name
                      + r'\([^\n]*\)\s*\n        \{.*?^        \}', source, re.M | re.S)
    if match is None and not optional:
        raise RuntimeError(f'Missing production method: {name}')
    return match.group(0) if match else ''


handlers = '\n'.join(method(name, optional) for name, optional in [
    ('BuildExitBanner', False), ('OnCliNotify', False),
    ('FindLatestClaudeSessionId', True), ('EncodeProjectDir', True),
])
program = r'''
using System.Text;
using System.Text.RegularExpressions;

var root = args[0];
var cwd = Path.Combine(root, "shared-project");
var slug = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
var transcripts = Path.Combine(root, ".claude", "projects", slug);
Directory.CreateDirectory(transcripts);
var own = "11111111-1111-4111-8111-111111111111";
var sibling = "22222222-2222-4222-8222-222222222222";
var changed = "33333333-3333-4333-8333-333333333333";
foreach (var (id, age) in new[] { (own, -5), (sibling, 0), (changed, -10) })
{
    var path = Path.Combine(transcripts, id + ".jsonl");
    File.WriteAllText(path, "{}\n");
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(age));
}
int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
void Expect(Pane pane, string? id)
{
    var banner = pane.Build();
    var command = "claude" + (CliNotify.Settings.Length > 0 ? " " + CliNotify.Settings : "")
        + " --resume" + (id == null ? "" : " " + id);
    Require(pane.Command == command, $"expected {command}; got {pane.Command}");
    Require(!banner.Contains("--continue"), "banner still recommends an unrelated latest conversation");
    Require(id == null ? banner.Contains("Choose a Claude conversation")
                      : banner.Contains("claude --resume " + id), "banner target differs from command");
}
Check("newer same-directory sibling cannot replace this pane", () =>
    Expect(new Pane(root, cwd, own), own));
Check("SessionStart after /clear or /resume replaces the launch ID", () =>
{
    var pane = new Pane(root, cwd, own);
    pane.Notify("waiting", changed);
    Expect(pane, changed);
});
Check("turn hook keeps the current conversation despite newer sibling writes", () =>
{
    var pane = new Pane(root, cwd, own);
    pane.Notify("working", changed);
    pane.Notify("waiting", changed);
    Expect(pane, changed);
});
Check("known ID is retained even before its transcript exists", () =>
{
    var missing = "44444444-4444-4444-8444-444444444444";
    Expect(new Pane(root, cwd, missing), missing);
});
Check("unknown ID opens picker despite existing same-directory transcripts", () =>
    Expect(new Pane(root, cwd, null), null));
Check("invalid IDs cannot become commands or select latest transcript", () =>
{
    foreach (var invalid in new[] { "", "not-a-uuid", own + " & echo invalid" })
        Expect(new Pane(root, cwd, invalid), null);
});
Check("unknown ID and absent project open picker", () =>
    Expect(new Pane(root, Path.Combine(root, "absent"), null), null));
Check("resume still works when notification settings are unavailable", () =>
{
    CliNotify.Settings = "";
    Expect(new Pane(root, cwd, own), own);
    Expect(new Pane(root, cwd, null), null);
});
Console.WriteLine($"{passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;

class Pane
{
    private string? _resumeCommand;
    private bool _remoteResumeOffer;
    private bool _completionHooksActive, _apiErrored, _explicitWaitingSignal;
    private string _workingDir;
    public string? SessionId;
    public string CliMode = Settings.CliMode.Claude;
    public string PaneId = "offline-pane";
    public string? CodexHome;
    public string? ClaudeGitBash;
    public string? Command => _resumeCommand;
    public Pane(string root, string cwd, string? id)
    { Environment.Root = root; _workingDir = cwd; SessionId = id; }
    public string Build() => BuildExitBanner();
    public void Notify(string evt, string? id) => OnCliNotify(evt, id);
    private void ConfirmObservationSession(string? id) { }
    private void ConfirmCodexSession(string id, string provenance) => SessionId = id;
    private void SetStatus(PaneStatus status, bool explicitTransition = false) { }
    private enum PaneStatus { Waiting, Working }
    // Only profile lookup is redirected; the pre-fix disk scan is otherwise real.
    private static class Environment
    {
        public static string Root = "";
        public enum SpecialFolder { UserProfile }
        public static string GetFolderPath(SpecialFolder _) => Root;
    }
    /* PRODUCTION_HANDLERS */
}
static class CliNotify
{
    public static string Settings = "--settings \"C:\\offline hooks\\pane.json\"";
    public static string ClaudeSettingsArg(string pane) => Settings;
    public static string PrepareCodexNotify(string pane) => "";
}
namespace Settings
{
    static class ClaudeGrepShell { public const string LogSearchArgs = "--append-system-prompt fixture"; }
    static class CliMode
    {
        public const string Claude = "claude", Codex = "codex", CodexRemote = "codex-remote";
        public static string BuildCodexResumeShellCommand(string? id, string extra) => "codex resume " + id;
    }
    static class CliSessions
    {
        public static string? ResolveCodexResumeSessionId(string? id, string? home) => id;
    }
}
'''.replace('/* PRODUCTION_HANDLERS */', handlers)

with tempfile.TemporaryDirectory(prefix='ccpad-claude-resume-') as temporary:
    work = Path(temporary).resolve()
    # All generated files and automatic cleanup stay inside this isolated fixture.
    assert work.parent == Path(tempfile.gettempdir()).resolve()
    (work / 'Probe.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <NoWarn>0169;0414;0649</NoWarn>
  </PropertyGroup>
</Project>''', encoding='utf-8')
    (work / 'Program.cs').write_text(program, encoding='utf-8')
    result = subprocess.run(['dotnet', 'run', '--project', str(work / 'Probe.csproj'),
                             '--', str(work / 'profile')], timeout=120)
    raise SystemExit(result.returncode)
