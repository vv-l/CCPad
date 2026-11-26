# Terminal table URL continuations — v1.10.50

The table in the reported screenshot renders a URL ending in `.html?` on one
physical row and `case=AINAS-14294` on the next. These are explicit TUI line
breaks inside a table column, so xterm does not mark the continuation as a soft
wrap. The previous provider opened the first fragment without the query and
provided no click target for the continuation.

The provider now recovers complete parenthesized, underlined URLs across up to
16 physical rows when the foreground color and table-cell indentation agree.
It requires the closing parenthesis, rejects new URLs and intervening content,
and retains balanced parentheses, Unicode, query separators and fragments.
Each physical fragment has its own click range with the same complete target;
padding and other columns are not included. Ordinary hard line breaks still
separate unrelated content. OSC-8 destinations retain priority, and existing
soft-wrap handling and click/selection safeguards remain in place.

This is deliberately bounded recovery for styled parenthesized table links,
not a general inference that arbitrary adjacent terminal lines form a URL.

## Verification

- Offline Chromium tests use the actual embedded frontend and bundled xterm,
  real mouse events and a fake WebView host. A documentation-only IP address
  stands in for the screenshot's LAN service; no external request is made.
- Before the fix, all four initial table-continuation regressions fail: the
  first fragment loses the query, and the continuation is not clickable.
- Final link suite: **24/24 passed**, no page errors. This includes three-row
  URLs, later table columns, wide characters, scrolling, separate closing
  brackets, exact clickable boundaries and rejection of unrelated content.
- Existing staging suite: **28/28 passed**, no page errors.
- Release publish succeeded. Existing nullable warnings remain at
  `MainWindow.xaml.cs:1977` and `MainWindow.xaml.cs:2302`.
- The published DLL's complete embedded HTML matches the tested source after
  normalizing source newlines and C# raw-string indentation.
