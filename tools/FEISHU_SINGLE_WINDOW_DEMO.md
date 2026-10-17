# CCPad 单窗口 × 飞书智能助手 Demo

这个 Demo 是一个本机 sidecar：CCPad 继续拥有终端进程，Demo 只通过 CCPad 已有的 loopback WebTerminal 接口转发消息。它不会启动模型，也不会把飞书消息直接交给另一个独立的 Codex 会话。

## 硬隔离规则

Demo 必须使用独立的 Debug 可执行文件、`CCPAD_DATA_DIR`、恢复快照、hooks、WebView2 用户数据目录、sidecar 状态目录和本机端口。禁止把 Demo 指向 `D:\CC Pad\current`，禁止复用生产 CCPad 的 profile、session、凭据或状态文件。

Demo 启动参数 `--feishu-demo` 只允许开启 `127.0.0.1` loopback WebTerminal 和随机 token；Demo 不注册生产右键菜单、不注册生产通知入口、不切换 `current` junction，也不停止、重启或覆盖任何生产 CCPad 进程。飞书联调只允许使用已指定的“林葳的智能助手”。

启动前必须记录生产 CCPad 的 PID、可执行文件路径和 `current` 指向；停止 Demo 时只允许停止 Demo 进程及其专属子进程，并在结束后复核生产 PID、路径和 junction 未变化。

## 运行前提

1. 启动 CCPad，并在 CCPad 的 Web Terminal 页面打开本机服务。建议启用 token，并记录端口和 token。
2. 确认服务只监听本机地址，例如 `http://127.0.0.1:9220`。不要把未鉴权的端口暴露到局域网。
3. 确认飞书 App 凭据文件位于受控目录，例如：
   `D:\Desktop\NAS_DEVICE_CODE\secrets\feishu-ccpad-pilot.md`。

## 启动

在本目录执行：

```powershell
python .\feishu_single_window_demo.py `
  --credentials 'D:\Desktop\NAS_DEVICE_CODE\secrets\feishu-ccpad-pilot.md' `
  --web-descriptor 'D:\Desktop\NAS_DEVICE_CODE\_scratch\ccpad-feishu-pilot-20261008\ccpad-demo-profile\feishu-demo-webterminal.json' `
  --state-dir '.\.ccpad-feishu-demo' `
  --allowed-chat-id '<目标智能助手会话 ID>' `
  --listen-seconds 3600 `
  --allow-input
```

`--allow-input` 是显式开启普通消息转发的开关。没有它时，状态和绑定命令仍可用，但普通文本只返回只读提示。

## 飞书命令

在这个智能助手的私聊中发送：

| 命令 | 作用 |
|---|---|
| `/bridge status` | 查看长连接、入站事件数、当前绑定和是否允许转发 |
| `/bridge whoami` | 查看当前绑定的 Pane、CLI Session、引擎和工作目录 |
| `/bridge sessions` | 列出 CCPad 当前可绑定的终端及其 session id |
| `/bridge bind <session-id>` | 明确绑定一个窗口；绑定结果写入 sidecar 状态文件 |
| `/bridge resume <cli-session-id>` | 通过稳定 CLI Session ID 唯一恢复窗口；无法唯一匹配时拒绝绑定 |
| `/bridge unbind` | 解除绑定并删除 sidecar 状态文件 |
| 普通文本 | 追加回车写入绑定窗口；终端输出每约 1.5 秒合并回传 |

图片和文件消息会先下载到 Demo 专属 `state-dir\attachments`，再把本地路径写入已绑定的原窗口。下载上限 100 MB；只读模式不会下载附件。`--allowed-chat-id` 应设置为“林葳的智能助手”对应会话，其他聊天事件会被忽略并记录为 `ignored_chat`。

Demo 重新启动时会尝试用状态文件恢复绑定：同一 WebTerminal PID 可按 PaneId 恢复；PID 改变后只按唯一的 CLI `SessionId`、引擎和工作目录匹配。无法唯一匹配时显示 `orphaned`，要求用户执行 `/bridge sessions` 后明确选择，不会静默绑定新窗口。

## 当前边界

- 飞书长连接和入站事件由官方 SDK 负责 ACK；状态、绑定和文本转发均在本地 sidecar 内完成。
- Demo 可以通过 `--chat-id-file` 开启单聊天轮询补偿。它按消息 ID 去重，只处理用户消息，适合长连接短暂漏事件的恢复；轮询同样不调用模型。
- 这个 Demo 不调用模型。用户把普通文本发到绑定窗口后，是否调用 Codex/Claude 由该窗口原本运行的终端决定。
- 只有一条绑定和一条写入锁，避免飞书和本地桥接同时写入同一个 ConPTY。
- 输出通知是低频合并消息，不把每个终端 delta 变成飞书消息。
- Demo 不启动或重启 CCPad，也不替换 `D:\CC Pad\current`；发布仍须走 `publish-live.ps1` 的非中断流程。
