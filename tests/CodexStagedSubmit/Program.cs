using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CCPad.Terminal;

// Runs only disposable Codex instances against a LOOPBACK failure endpoint.
// They get empty, separate homes and no credentials; no real model is contacted.
// Suspend/resume targets the exact process handle created by this fixture only.
var report = Console.Out;
_ = Console.Error;
Native.FreeConsole();
foreach (int handle in new[] { -10, -11, -12 }) Native.SetStdHandle(handle, IntPtr.Zero);
string root = args.Length > 0 ? Path.GetFullPath(args[0]) :
    Path.Combine(Path.GetTempPath(), "CCPad-tests", "staged-submit-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
string binary = Directory.GetFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64"), "codex.exe", SearchOption.AllDirectories).Single();
var results = new List<object>();
var cases = new[] {
    (Name: "short", Prompt: "CCPad offline input fixture: 第一张单据与第二张单据，应当作为一条消息完整提交。"),
    (Name: "multiline", Prompt: "CCPad offline input fixture: 两张单据\n/tmp/fixture-receipt-a.png\n/tmp/fixture-receipt-b.png\n备注：保留换行🙂"),
    (Name: "long", Prompt: "CCPad offline input fixture: " + string.Concat(Enumerable.Repeat("完整保留多行单据备注🙂\n", 160)))
};
foreach (var test in cases)
foreach (bool repaired in new[] { false, true })
{
    string label = test.Name + (repaired ? "-fixed" : "-baseline");
    string fixture = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N"));
    string home = Path.Combine(fixture, "home"), work = Path.Combine(fixture, "work");
    Directory.CreateDirectory(home); Directory.CreateDirectory(work);
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
    int submissions = 0;
    int backgroundRequests = 0;
    var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    string prompt = test.Prompt.TrimEnd();
    bool correctPrompt = false;
    string? actualPrompt = null;
    var server = Task.Run(async () => {
        try {
            while (listener.IsListening) {
                var context = await listener.GetContextAsync();
                string body = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
                if (context.Request.HttpMethod == "POST" && context.Request.Url!.AbsolutePath.EndsWith("/responses")) {
                    using var json = JsonDocument.Parse(body);
                    actualPrompt = json.RootElement.GetProperty("input").EnumerateArray()
                        .Where(item => item.TryGetProperty("role", out var role) && role.GetString() == "user" && item.TryGetProperty("content", out _))
                        .SelectMany(item => item.GetProperty("content").EnumerateArray())
                        .Where(part => part.TryGetProperty("text", out _))
                        .Select(part => part.GetProperty("text").GetString())
                        .LastOrDefault(text => text?.Contains("CCPad offline") == true);
                    if (actualPrompt?.TrimEnd() == prompt) {
                        Interlocked.Increment(ref submissions);
                        correctPrompt = true;
                        submitted.TrySetResult();
                    } else {
                        Interlocked.Increment(ref backgroundRequests); // e.g. automatic task-title generation
                    }
                }
                byte[] response = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Offline input fixture completed\"}}");
                context.Response.StatusCode = 400; context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
            }
        } catch (HttpListenerException) { } catch (ObjectDisposedException) { }
    });
    File.WriteAllText(Path.Combine(home, "config.toml"), """
        model = "gpt-5"
        model_provider = "fixture"
        check_for_update_on_startup = false
        cli_auth_credentials_store = "file"
        [model_providers.fixture]
        name = "CCPad offline input fixture"
        wire_api = "responses"
        requires_openai_auth = false
        request_max_retries = 0
        stream_max_retries = 0
        """ + $"\nbase_url = \"http://127.0.0.1:{port}/v1\"\n[projects.{JsonSerializer.Serialize(work)}]\ntrust_level = \"trusted\"\n");
    using var terminal = ConPtySession.Start($"\"{binary}\" --no-alt-screen --sandbox read-only --ask-for-approval never", 110, 32, work,
        new Dictionary<string, string> { ["CODEX_HOME"] = home, ["OPENAI_API_KEY"] = "", ["CODEX_API_KEY"] = "" });
    var output = new StringBuilder();
    terminal.OutputReceived += chunk => {
        string text = Encoding.UTF8.GetString(chunk);
        lock (output) output.Append(text);
        if (text.Contains("\x1b[6n")) terminal.TryWriteInput("\x1b[1;1R");
    };
    bool ready = false;
    for (int attempt = 0; attempt < 100; attempt++) {
        string text; lock(output) text = Regex.Replace(output.ToString(), "\\x1b\\[[0-?]*[ -/]*[@-~]", "");
        if (text.Contains("Ask Codex to do anything") && (text.Contains("Tip:") || text.Contains("f2 to view"))) {
            ready = true; break;
        }
        await Task.Delay(100);
    }
    if (!ready) {
        lock(output) File.WriteAllText(Path.Combine(root, label + "-startup.ansi"), output.ToString());
        report.WriteLine($"FAIL {label}: fixture composer did not become ready; see {label}-startup.ansi");
        results.Add(new { label, passed = false, error = "fixture_startup" });
        Environment.ExitCode = 1; listener.Stop(); await server; continue;
    }
    var process = (IntPtr)typeof(ConPtySession).GetField("_hProcess", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    if (Native.NtSuspendProcess(process) != 0) throw new Exception("Cannot suspend the fixture consumer");
    try {
        terminal.TryWriteInput(StagedInputProtocol.Paste(prompt, true));
        await Task.Delay(180); // same previous production delay, while the consumer is busy
        terminal.TryWriteInput(repaired ? StagedInputProtocol.Submit(true) : "\r");
    } finally { Native.NtResumeProcess(process); }
    await Task.WhenAny(submitted.Task, Task.Delay(repaired ? 8000 : 1500));
    await Task.Delay(200);
    lock(output) File.WriteAllText(Path.Combine(root, label + ".ansi"), output.ToString());
    int count = Volatile.Read(ref submissions);
    bool passed = repaired ? count == 1 && correctPrompt : count == 0;
    results.Add(new { label, passed, submissions = count, correctPrompt, backgroundRequests });
    report.WriteLine($"{(passed ? "PASS" : "FAIL")} {label}: loopback submissions={count}, exact prompt={correctPrompt}");
    if (!passed) Environment.ExitCode = 1;
    listener.Stop(); await server;
}
File.WriteAllText(Path.Combine(root, "codex-native-regression.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

static class Native
{
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
    [DllImport("kernel32.dll")] public static extern bool SetStdHandle(int n, IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr process);
}
