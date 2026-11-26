# Terminal web links — v1.10.49

Remote terminal output containing plain HTTP(S) URLs could not be clicked, or
could open a URL from the following row. The plain-text provider used the xterm
link API's 1-based row as a 0-based buffer index and returned 0-based columns.
OSC-8 links used a separate, working path, explaining why local output carrying
link metadata could work while remote output containing only styled text failed.
This was reproduced offline; no remote machine or live Jira page was accessed.

The provider now maps UTF-16 text offsets to actual terminal cells and emits
1-based ranges. Chinese, emoji, combining characters, soft wraps, row-end spaces
and scrollback retain their actual coordinates. Hard line breaks remain separate.
Surrounding prose brackets and punctuation are excluded; balanced URL brackets
and IPv6 addresses survive. The native click handler preserves the resolved URL
instead of trimming valid characters from explicit OSC-8 destinations.

Plain URLs and OSC-8 labels share the existing native browser launch path.
Right-click, middle scrolling, Shift selection and local text selection do not
launch links. A pre-existing selection under remote mouse tracking does not
disable a subsequent click. Only HTTP(S) destinations without embedded
credentials are accepted, matching the native host's existing validation.

## Verification

- `python tests/terminal_links.py --report <json>` uses the actual embedded HTML,
  bundled xterm, real Chromium mouse events and a fake WebView host.
- Pre-fix source: 2/15 checks pass. Fixed source: 15/15, no page errors. Cases
  include the screenshot's parenthesized Jira URL format with tmux-style mouse
  tracking, adjacent rows, Unicode, wrapping, scrollback, gestures and OSC-8.
- Existing staging suite: 28/28 checks pass, no page errors.
- Debug build and Release publish pass; existing nullable warnings remain at
  `MainWindow.xaml.cs:1977` and `MainWindow.xaml.cs:2302`.
- No CLI/PTY, SSH connection, external browser launch or reset credit was used
  for test verification. The first actual launch remains a user click.

The hover regression also checks that the underline and pointer are cleared when the mouse leaves the link.
