# CCPad demo videos

[Back to the project](../../README.md) · [中文说明](README.zh-CN.md)

Two independent CCPad windows run real local Codex sessions at the same time. These short narrated demos show how to follow each task, recall its last instruction, and recognize when it needs your attention.

## Choose a language

<table>
  <thead>
    <tr>
      <th>English · 59 seconds</th>
      <th>简体中文 · 61 秒</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>
        <video controls preload="metadata" poster="ccpad-parallel-demo-en.jpg" width="100%" src="https://github.com/user-attachments/assets/aae4439c-ddb0-4666-ae2b-1210359cd976">
          <a href="https://github.com/user-attachments/assets/aae4439c-ddb0-4666-ae2b-1210359cd976">Play the English demo</a>
        </video>
      </td>
      <td>
        <video controls preload="metadata" poster="ccpad-parallel-demo-zh-CN.jpg" width="100%" src="https://github.com/user-attachments/assets/0049b6ca-a5b9-4272-adfb-1df13a599a81">
          <a href="https://github.com/user-attachments/assets/0049b6ca-a5b9-4272-adfb-1df13a599a81">观看中文演示</a>
        </video>
      </td>
    </tr>
    <tr>
      <td><a href="https://raw.githubusercontent.com/vv-l/CCPad/master/docs/demos/ccpad-parallel-demo-en.mp4">Download original MP4</a> · <a href="ccpad-parallel-demo-en.srt">English subtitles (SRT)</a></td>
      <td><a href="https://raw.githubusercontent.com/vv-l/CCPad/master/docs/demos/ccpad-parallel-demo-zh-CN.mp4">下载原版 MP4</a> · <a href="ccpad-parallel-demo-zh-CN.srt">中文字幕（SRT）</a></td>
    </tr>
    <tr>
      <td>English interface, task prompts, narration and subtitles</td>
      <td>Chinese interface, task prompts, narration and subtitles</td>
    </tr>
  </tbody>
</table>

Click either preview to play the video directly in your browser. Use the Download original MP4 link to save the original MP4. Both videos are 1080p at 30 fps, with narration and visible subtitles included.

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
| English | [Play MP4 in browser](https://cdn.jsdelivr.net/gh/vv-l/CCPad@master/docs/demos/ccpad-parallel-demo-en.mp4) | [SRT](ccpad-parallel-demo-en.srt) | [JPG](ccpad-parallel-demo-en.jpg) |
| 简体中文 | [Play MP4 in browser](https://cdn.jsdelivr.net/gh/vv-l/CCPad@master/docs/demos/ccpad-parallel-demo-zh-CN.mp4) | [SRT](ccpad-parallel-demo-zh-CN.srt) | [JPG](ccpad-parallel-demo-zh-CN.jpg) |

For installation and the full feature guide, return to the [project README](../../README.md).
