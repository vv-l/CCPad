# Hard-wrapped paragraph URLs — v1.10.54

Plain paragraph links could open only their first physical row, including an
incomplete domain such as `https://example-global.fe` when `ishu.cn/sheets/...`
was on the following row. The existing hard-wrap recovery covered only styled,
parenthesized table links.

Paragraph recovery now joins fragments that reach the terminal's right margin
(up to two padding cells), retain the paragraph indentation, and share foreground,
background, bold, italic and underline attributes. Scanning stays bounded to 16
rows. Each fragment gets its own click range and the complete destination.
Chinese punctuation, indentation and following prose are excluded. Short lines,
changed indentation/style, new URL schemes and separators are rejected. Existing
table, soft-wrap, OSC-8 and selection behavior is retained.

Offline verification uses the real embedded HTML, bundled xterm, Chromium mouse
events and a fake WebView host. Four new regression cases fail against the
pre-fix source; the final suite passes **30/30** with no page errors. Tests cover
both fragments, zero to two margin cells, normal/bold text, three-row URLs,
queries, scrolling and rejected continuations. No live document or browser was
opened. The published DLL's complete HTML matches the tested source.
