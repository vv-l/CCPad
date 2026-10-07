# CCPad demo videos

[Back to the project](../../README.md) · [中文说明](README.zh-CN.md)

Two independent CCPad windows run real local Codex sessions at the same time. These short narrated demos show how to follow each task, recall its last instruction, and recognize when it needs your attention.

## Choose a language

| English · 59 seconds | 简体中文 · 61 秒 |
| --- | --- |
| [![Watch the English demo](ccpad-parallel-demo-en.jpg)](ccpad-parallel-demo-en.mp4) | [![观看中文演示](ccpad-parallel-demo-zh-CN.jpg)](ccpad-parallel-demo-zh-CN.mp4) |
| [Watch / download MP4](ccpad-parallel-demo-en.mp4) · [English subtitles (SRT)](ccpad-parallel-demo-en.srt) | [观看 / 下载 MP4](ccpad-parallel-demo-zh-CN.mp4) · [中文字幕（SRT）](ccpad-parallel-demo-zh-CN.srt) |
| English interface, task prompts, narration and subtitles | Chinese interface, task prompts, narration and subtitles |

Click a preview to open the video, or download the MP4 for local playback. Both videos are 1080p at 30 fps, with narration and visible subtitles included.

## What the demo shows

| Moment | What to look for | Why it helps |
| --- | --- | --- |
| Two tasks start | Separate Codex sessions receive a writing task and a data task | Keep different goals and their outputs in separate sessions |
| Both lights turn green | The two sessions work at the same time | See active work at a glance |
| The last instruction is copied | The bar above the terminal retains the full submitted instruction; clicking it copies the text | Recall or reuse a prompt when returning to a session |
| The lights turn amber | The tasks have finished and the sessions are waiting for input | Know when to inspect the results or send the next instruction |
| The right light turns red | The right Codex CLI exits normally while the left session stays available | Distinguish a closed CLI from a session waiting for input |

In general, **green means working**, **amber means waiting for input**, and **red means the CLI has exited**. Amber by itself does not certify that an output is correct; inspect the saved result before continuing.

## The example tasks

- **Left — travel writing:** write a short park travel story and save it as a Markdown file. The Chinese prompt asks for about 500 Chinese characters; the English prompt asks for 500 words.
- **Right — sales analysis:** read a fictional `sales.csv`, total sales by channel, list the three highest-sales days, and save a Markdown summary.

The English edition was recorded again with the English CCPad interface and English tasks. Each edition includes narration and subtitles in its own language. The recordings use fictional sample content; pauses are trimmed and close-ups highlight the controls. Status lights and copy feedback are captured from the app.

## Files

| Edition | Video | Subtitles | Preview |
| --- | --- | --- | --- |
| English | [MP4](ccpad-parallel-demo-en.mp4) | [SRT](ccpad-parallel-demo-en.srt) | [JPG](ccpad-parallel-demo-en.jpg) |
| 简体中文 | [MP4](ccpad-parallel-demo-zh-CN.mp4) | [SRT](ccpad-parallel-demo-zh-CN.srt) | [JPG](ccpad-parallel-demo-zh-CN.jpg) |

For installation and the full feature guide, return to the [project README](../../README.md).
