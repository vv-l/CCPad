# 167 远程 Codex 默认权限修复记录

- 本文是 2026-09-21 的内部修复归档，不代表当前运行版本或发布状态。

- 日期：2026-09-21
- 设备 ID：`codex-167`
- 范围：CC Pad 远程 Codex 启动命令

## 问题

167 的本地设备配置曾使用：

```text
/zettos/pool/1/agents/opt/npm-global/bin/codex -s danger-full-access
```

这没有按约定显式使用 `--yolo`，导致启动命令和文档约定不一致。

## 修复

- `%LOCALAPPDATA%\CCPad\remote-devices.json` 中的 `codex-167` 已改为：

  ```text
  /zettos/pool/1/agents/opt/npm-global/bin/codex --yolo
  ```

- `CCPad/Settings/RemoteDeviceConfig.cs` 现在会为内置 167 配置统一补齐 `--yolo`。
- 旧的 `codex`、`-s danger-full-access`、`--sandbox danger-full-access` 形式会在读取时兼容归一化。
- 旧式启动模板末尾直接写 `codex` 时，也会自动补上 `--yolo`。

## 验证

- Release x64 编译通过：0 个错误；保留 4 个既有的 `MainWindow.xaml.cs` 可空引用警告。
- 生成的远程命令已确认包含：

  ```text
  tmux -u new -A -s <session> /zettos/pool/1/agents/opt/npm-global/bin/codex --yolo
  ```

- 原配置备份：`%LOCALAPPDATA%\CCPad\remote-devices.json.bak_before_yolo_20260921-233630`

## 发布状态

- 已按非中断发布流程暂存：`D:\CC Pad\app\v1.10.10_20260921-233832`
- 当前 `CCPad.exe` 进程仍在运行，`D:\CC Pad\current` 暂未切换。
- 进程自然退出后，下一次启动会使用该版本；没有自动停止或重启 CC Pad。
- 已经运行的远程 tmux 会话会保留原启动参数，关闭并重新打开后才会使用新参数。
