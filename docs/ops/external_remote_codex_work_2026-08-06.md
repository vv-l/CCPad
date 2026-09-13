# CC Pad 外部设备与远程 Codex 改造归档

- 日期：2026-08-06
- 范围：外部项目、多 Linux SSH 设备、远程 Codex/tmux、AI 接入 Skill
- 当前发布：`D:\CC Pad\current` → `app\v1.10.0_20260806-113918`
- SSH 目标记录：内置设备 ID `codex-167`（主机与密钥信息不在本文展开）

## 最终结果

CC Pad 已将“本地项目”和“外部项目”拆分为独立入口。外部功能的数据关系为：

```text
Linux SSH 外部设备
└── 绑定到该设备的外部项目目录
    └── 每个标签独立的远程 Codex/tmux 会话
```

外部项目名称只用于菜单和标签标题；实际工作目录由项目的远程绝对路径决定。

## 用户流程

### AI 快速接入

1. 在“外部项目”菜单点击“复制 AI 外部设备接入指令”。
2. 将剪贴板内容粘贴给 AI，补充设备 IP、SSH 用户及项目路径。
3. AI 使用项目级 `$ccpad-onboard-linux-device` Skill 只读探测设备。
4. AI 准备缺失环境并验证 Linux、SSH、工作目录、tmux、Codex CLI 与 Codex 登录。
5. AI 调用随 Skill 提供的配置脚本，合并 CC Pad 设备和项目配置。

复制入口带复制图标、悬浮说明及复制成功反馈。复制文本同时给出 Skill 的本机绝对路径，避免 AI 不在 CC Pad 工作目录时无法发现 Skill。

### 半手动接入

1. AI 返回设备名称、主机、端口、用户、私钥路径或 ssh-agent、默认目录、Codex 命令、启动模板和检查结果。
2. 用户选择“添加外部设备”，填写并测试设备。
3. 用户选择“添加外部项目”，绑定设备并登记远程绝对路径。
4. 添加操作只保存项目，不自动启动标签。
5. 用户点击项目或右键“用远程 Codex 打开”。

## 外部设备功能

- 支持多台 Linux SSH 设备。
- 支持添加、编辑、删除、切换和测试当前设备。
- 设备字段包括名称、主机、SSH 端口、Linux 用户、私钥路径、默认工作目录、Codex 命令、远程启动模板、tmux 前缀和清理期限。
- 私钥字段只保存路径；不保存密码、私钥内容、OpenAI API 密钥、访问令牌或 Codex 登录文件。
- 测试项包括：
  - SSH 是否连接成功。
  - 目标是否为 Linux。
  - 默认目录是否存在并可进入。
  - `tmux` 是否可用。
  - Codex CLI 是否可用。
  - `codex login status` 是否成功。
- 删除仍被项目引用的设备会被阻止，必须先编辑或删除关联项目。
- 首次读取时，旧版单一 `RemoteCodex` 配置自动迁移为 `codex-167` 设备。

## 外部项目功能

- 项目记录绑定具体设备 ID 和 Linux 绝对路径。
- 支持添加、编辑、测试远程目录、删除和打开。
- 项目菜单悬浮提示完整远程路径。
- 如果绑定设备已不存在，项目项禁用并在测试时返回明确错误。
- “新建远程 Codex 标签”和“恢复远程 Codex 对话”使用当前设备的默认目录。
- 内置默认目录为 `/zettos/pool/1/agents`；已保存项目始终使用自己的远程目录。

## 远程会话生命周期

- 每个远程标签创建独立的 `ccpad-*` tmux 会话。
- 会话名写入标签状态，支持冻结、工作区恢复、崩溃恢复和重新附加。
- 明确关闭标签会尽力结束它拥有的远程 tmux 会话。
- 冻结、跨面板移动、可恢复窗口关闭和网络中断只 detach，不主动结束会话。
- 后台清扫器回收超过保留期限的已断开 CC Pad 会话。
- 会话列表、重新附加、显式结束和清扫状态都按当前设备执行。

## 167 最高权限默认值

- 内置 `codex-167` 设备的新会话默认使用 `codex --yolo`。
- `--yolo` 跳过审批并关闭 Codex 沙箱，属于最高权限模式。
- 新建标签、外部项目打开和恢复选择器均继承该参数。
- 旧式字面 `codex` 启动模板与新版 `{codex}` 模板都做了兼容迁移。
- 已经运行的 tmux 进程不会被强制重启；关闭并重新打开后才应用新参数。

## 配置与 Skill

### CC Pad 配置

- `%LOCALAPPDATA%\CCPad\remote-devices.json`
  - 文档版本、当前设备 ID、设备列表。
- `%LOCALAPPDATA%\CCPad\remote-projects.json`
  - 项目名称、设备 ID、远程路径。
- `CCPAD_DATA_DIR`
  - 开发和隔离测试时覆盖配置根目录。

### 项目级 Skill

- 位置：`D:\CC Pad\.agents\skills\ccpad-onboard-linux-device\SKILL.md`
- 自动配置脚本：`scripts\configure_ccpad.ps1`
- 只读探测脚本：`scripts\probe_linux_device.sh`
- 环境要求：`references\device-requirements.md`

Skill 提供快速自动配置和半手动清单两条路线。密码登录不能满足 CC Pad 的无人值守重连与清理要求；设备应先具备公钥或 ssh-agent 认证。Codex 登录需要使用官方交互流程，凭据不得写入仓库或归档。

## 主要代码位置

- `CCPad/Settings/RemoteDeviceConfig.cs`
  - 多设备数据模型、旧配置迁移、167 `--yolo` 默认值。
- `CCPad/Settings/RemoteDeviceConnection.cs`
  - 非交互 SSH 探测、目录检查和错误分类。
- `CCPad/Settings/RemoteProjectConfig.cs`
  - 外部项目持久化。
- `CCPad/Settings/RemoteSessions.cs`
  - 按设备管理 tmux、清理器和会话列表。
- `CCPad/Settings/CliMode.cs`
  - 生成按设备、目录和会话组合的 SSH 启动命令。
- `CCPad/TabPanel.xaml.cs`
  - 外部设备/项目菜单、对话框、测试、AI 指令复制及标签启动。
- `CCPad/MainWindow.xaml.cs`
  - 当前设备的远程会话管理界面。
- `CCPad/SplitHost.xaml.cs`
  - 将设备 ID 和远程目录传入重新附加流程。
- `CCPad/Settings/WorkspaceConfig.cs`、`CCPad/TerminalPane.xaml.cs`
  - 远程设备 ID、工作目录和会话状态持久化。
- `CCPad/Localization/Loc.cs`
  - 新增设备、项目、测试、复制入口和会话管理文案。

## 验证记录

- Debug 与 Release 构建均通过，0 个错误。
- 当前仍有 4 个 `MainWindow.xaml.cs` 的可空引用编译警告；未阻塞发布。
- `git diff --check` 通过，无空白错误。
- Skill 通过 `quick_validate.py` 验证。
- 自动配置脚本在隔离临时目录验证：
  - 创建设备。
  - 合并同一设备。
  - 写入多个项目。
  - 始终输出 JSON 数组格式。
- 使用隔离 `CCPAD_DATA_DIR` 启动应用验证：
  - 旧单设备配置可生成 `remote-devices.json`。
  - 迁移设备 ID 为 `codex-167`。
  - 默认目录迁移正确。
  - `CodexCommand` 和旧启动模板均包含 `--yolo`。
- 已多次通过原子 `current` junction 发布；运行中的旧窗口不受影响，下次启动使用新版本。

## 安全边界与未决事项

- 本轮没有使用真实设备凭据执行端到端 SSH 接入，因此实际目标上的网络、主机指纹、认证、包安装和 Codex 登录仍需在首次接入时验证。
- 主机密钥不匹配必须停止；不得自动删除或覆盖 `known_hosts` 记录。
- 通用非 root 设备可能无法安装 `/usr/local/bin` 清扫脚本或写入 `/etc/cron.d`；当前用户场景以 root 接入为主。
- 167 默认 `--yolo` 具有高风险，只应在用户确认可信的专用设备和项目中使用。
- 新增文案以简体中文和英文为主，其他语言回退英文；如需完整多语言可单独补齐。
- 发布脚本保留最近版本目录；被占用的旧目录可能暂时无法清理。

## 后续建议

1. 用一台测试 Linux 设备执行完整 AI 快速接入，记录真实首连体验。
2. 验证密码转公钥的用户引导是否足够清晰。
3. 为非 root 设备增加用户级 tmux 清扫器方案。
4. 为设备和项目数据模型补充独立单元测试。
5. 在 UI 中进一步区分“检查通过”和“Codex 尚待登录”的状态。
