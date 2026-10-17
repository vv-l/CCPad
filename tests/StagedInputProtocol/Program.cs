using CCPad.Terminal;

// Exercise wire framing using a tiny offline terminal receiver, not a real CLI.
var composer = "";
var messages = new List<string>();
void Receive(string frame)
{
    if (frame.StartsWith("\x1b[200~"))
    {
        if (!frame.EndsWith("\x1b[201~")) throw new Exception("Unclosed paste");
        var body = frame[6..^6];
        if (body.Contains('\x1b')) throw new Exception("Paste escaped its frame");
        composer += body;
    }
    else if (frame == "\r") { messages.Add(composer); composer = ""; }
    else composer += frame;
}
var prompts = new[]
{
    "\u975e\u6cd5\u540d\u79f0?",
    "/tmp/fictional.png \u91cd\u590d\u76ee\u6807\n\u540c\u4e00\u6761\u7684\u7b2c\u4e8c\u884c?",
    new string('\u4e2d', 20000)
};
foreach (var prompt in prompts)
{
    int before = messages.Count;
    Receive(StagedInputProtocol.Paste(prompt, bracketedPaste: true));
    if (messages.Count != before) throw new Exception("Paste prematurely submitted a line");
    Receive("\r");
}
if (!messages.SequenceEqual(prompts.Select(p => p.Replace('\n', '\r'))))
    throw new Exception("Prompt boundaries or Unicode changed");
Console.WriteLine("PASS Unicode, long text, and embedded newlines arrive as three separate messages");
Receive(StagedInputProtocol.Paste("before\x1b[201~\rafter", bracketedPaste: true));
if (messages.Count != 3) throw new Exception("Content escaped the paste frame");
Receive("\r");
Console.WriteLine("PASS pasted escape cannot end the frame and submit early");
Receive(StagedInputProtocol.Paste("ordinary shell command", bracketedPaste: false));
if (messages.Count != 4) throw new Exception("Plain paste submitted early");
Receive(StagedInputProtocol.Submit(nativeCodex: false));
if (messages[^1] != "ordinary shell command") throw new Exception("Plain paste changed");
Console.WriteLine("PASS non-bracketed single-line input still uses a separate Enter");

var passive = new[] { "\x1b[I", "\x1b[O", "\x1b[1;1R", "\x1b[?12;80R", "\x1b[?1;2c",
    "\x1b[>0;276;0c", "\x1b[0n", "\x1b[?2004;1$y", "\x1b[8;40;120t",
    "\x1b]10;rgb:ffff/ffff/ffff\x1b\\", "\x1b[O\x1b[1;1R" };
foreach (var data in passive)
    if (StagedInputProtocol.InterruptsSubmission(data, "user"))
        throw new Exception("Terminal report canceled a staged Enter");
Console.WriteLine("PASS focus and protocol reports do not take ownership of a staged submit");

var interactive = new[] { "hello", "涓枃", "\r", "\x03", "\x1b", "\x7f", "\x1b[A", "\x1b[F",
    "\x1b[Oedited", "edited\x1b[I", "\x1b[200~pasted text\x1b[201~" };
foreach (var data in interactive)
    if (!StagedInputProtocol.InterruptsSubmission(data, "user"))
        throw new Exception("Actual editing no longer cancels an automatic submit");
if (!StagedInputProtocol.InterruptsSubmission("\x1b[1;2R", "keyboard"))
    throw new Exception("Physical Shift+F3 was confused with a cursor report");
if (StagedInputProtocol.InterruptsSubmission("\x1b[A", "wheel-scroll"))
    throw new Exception("Scrolling canceled a staged Enter");
Console.WriteLine("PASS actual edits, paste, Escape and physical keys still interrupt; scrolling does not");

// Optionally consume the events emitted by the REAL bundled xterm in the
// browser regression, to check both sides of the input-origin contract.
if (args.Length == 1)
{
    using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[0]));
    var inputs = report.RootElement.GetProperty("input_cases").EnumerateArray().ToArray();
    if (inputs.Length < 5) throw new Exception("Missing real xterm focus/query fixtures");
    foreach (var input in inputs)
        if (StagedInputProtocol.InterruptsSubmission(input.GetProperty("data").GetString()!,
                input.GetProperty("source").GetString()) != input.GetProperty("interrupt").GetBoolean())
            throw new Exception("Host misclassified a real xterm input event");
    Console.WriteLine("PASS real xterm reports keep the send gate; physical keys take over");
}
