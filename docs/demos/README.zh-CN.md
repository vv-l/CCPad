# CCPad 演示视频

[返回项目首页](../../README.zh-CN.md) · [English](README.md)

两个独立的 CCPad 窗口同时运行真实的本地 Codex 会话。用一分钟了解怎样查看各任务的进度、找回上一条指令，以及判断什么时候需要继续操作。

## 选择语言

| 简体中文 · 61 秒 | English · 59 seconds |
| --- | --- |
| [![观看中文演示](ccpad-parallel-demo-zh-CN.jpg)](ccpad-parallel-demo-zh-CN.mp4) | [![Watch the English demo](ccpad-parallel-demo-en.jpg)](ccpad-parallel-demo-en.mp4) |
| [观看 / 下载 MP4](ccpad-parallel-demo-zh-CN.mp4) · [中文字幕（SRT）](ccpad-parallel-demo-zh-CN.srt) | [Watch / download MP4](ccpad-parallel-demo-en.mp4) · [English subtitles (SRT)](ccpad-parallel-demo-en.srt) |
| 中文界面、任务指令、配音和字幕 | 英文界面、任务指令、配音和字幕 |

点击封面可打开视频，也可以下载 MP4 在本地播放。两个版本均为 1080p、30 fps，视频已包含配音和画面字幕。

## 重点看什么

| 演示环节 | 画面中的变化 | 实际用途 |
| --- | --- | --- |
| 提交两条任务 | 两个独立 Codex 会话分别接收写作任务和数据任务 | 让不同目标及其产出各自保留在对应会话中 |
| 双绿灯并行 | 两个会话同时工作 | 一眼识别正在执行的任务 |
| 复制上一条指令 | 终端顶部保留完整指令，点击后复制全文 | 回到一个会话时，快速回忆或复用刚才的要求 |
| 完成后变黄灯 | 两条任务结束，会话等待下一次输入 | 提醒你检查产出或继续发指令 |
| 正常退出变红灯 | 右侧 Codex CLI 退出，左侧会话继续保留 | 区分已退出的 CLI 与仍可继续输入的会话 |

状态灯的一般含义是：**绿色表示工作中，黄色表示等待输入，红色表示 CLI 已退出**。黄灯本身不代表产出已通过验收，继续之前仍需检查实际结果。

## 两个示例任务

- **左侧：写游记。** 写一篇公园游记并保存为 Markdown 文件。中文指令要求约 500 字，英文指令要求 500 词。
- **右侧：汇总销售数据。** 读取虚构的 `sales.csv`，汇总各渠道销售额、列出销售额最高的三天，并保存 Markdown 小结。

英文版已切换到英文 CCPad 界面，用英文任务重新录制。两个版本分别配有对应语言的解说和字幕。演示使用虚构内容，剪去了等待片段并加入局部放大；状态灯与复制提示均来自实际应用画面。

## 文件清单

| 版本 | 视频 | 字幕 | 封面 |
| --- | --- | --- | --- |
| 简体中文 | [MP4](ccpad-parallel-demo-zh-CN.mp4) | [SRT](ccpad-parallel-demo-zh-CN.srt) | [JPG](ccpad-parallel-demo-zh-CN.jpg) |
| English | [MP4](ccpad-parallel-demo-en.mp4) | [SRT](ccpad-parallel-demo-en.srt) | [JPG](ccpad-parallel-demo-en.jpg) |

安装方法与完整功能说明见[项目 README](../../README.zh-CN.md)。
