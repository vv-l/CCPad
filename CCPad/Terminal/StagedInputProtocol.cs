using System;
using System.Text.RegularExpressions;

namespace CCPad.Terminal
{
    internal static class StagedInputProtocol
    {
        // Match xterm's paste newline convention. Bracketed paste makes embedded
        // newlines part of ONE composer entry and bypasses CLI paste heuristics.
        // The submitting Enter is always a later, separate write.
        internal static string Paste(string text, bool bracketedPaste)
        {
            string normalized = text.Replace("\r\n", "\r").Replace('\n', '\r');
            if (!bracketedPaste) return normalized;
            // A literal escape in pasted content must not terminate the frame.
            return "\x1b[200~" + normalized.Replace("\x1b", "") + "\x1b[201~";
        }

        internal static int SettleMilliseconds(int requested, bool remote)
            => Math.Clamp(requested, remote ? 500 : 180, 3000);

        // Native Windows console input loses the bracketed-paste boundaries.
        // Codex then buffers character events as a paste burst. End flushes that
        // buffer and clears Enter suppression without changing its text. Keeping
        // End before the ONE submitting Enter also works if the CLI drains both
        // writes late in the same event batch; a wall-clock delay cannot do that.
        internal static string Submit(bool nativeCodex)
            => nativeCodex ? "\x1b[F\r" : "\r";

        // Replies/focus reports are emitted by xterm, not by the person typing.
        // Only accept whole known reports; a report mixed with text still counts
        // as an edit. A physical key wins because e.g. Shift+F3 can look like CPR.
        private static readonly Regex PassiveInput = new(
            @"\A(?:\x1b\[[IO]|\x1b\[[?>=]?[0-9;]*c|\x1b\[\??\d+;\d+R|" +
            @"\x1b\[\d+n|\x1b\[\??\d+;\d+\$y|\x1b\[\d+(?:;\d+)*t|" +
            @"\x1b\](?:[0-9]+;)+(?:rgb:[0-9a-fA-F/]+)(?:\x07|\x1b\\))+\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal static bool InterruptsSubmission(string data, string? source)
        {
            if (data.Length == 0 || source is "middle-scroll" or "wheel-scroll") return false;
            return source == "keyboard" || !PassiveInput.IsMatch(data);
        }
    }
}
