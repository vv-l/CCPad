<p align="center">
  <img src="CCPad/Assets/claude.ico" width="80" alt="CC Pad logo"/>
</p>

<h1 align="center">CC Pad</h1>

<p align="center">
  Claude Code 多会话工作台 — 在单窗口中并行运行多个 Claude Code 会话。
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="LICENSE">许可证 (GPL-3.0)</a>
</p>

<p align="center">
  <em>⚡ 这是 <a href="https://github.com/nuomiaa/CCPad">nuomiaa/CCPad</a> 的社区 fork —— 增加了 Codex CLI 支持、每标签状态指示灯与后台"该你了"通知、9 种语言可切换界面、Chrome 式会话恢复、不死终端。基于 GPL-3.0 许可,原始版权归上游作者所有。详见<a href="#衍生版改动">衍生版改动</a>。</em>
</p>

---

## 功能特性

- **分屏** — 支持纵向和横向分屏，可拖拽调整比例。使用 `Alt+方向键` 在面板间快速切换。
- **标签页** — 每个面板支持多标签页，可拖拽排序，预热机制确保新建标签页即开即用。
- **工作区** — 将完整布局（分屏、标签页、工作目录、窗口状态）保存为 `.ccpad-workspace` 文件。启动时自动检测并恢复工作区。
- **项目快速访问** — 固定常用目录，一键在新标签页中打开。
- **Windows ConPTY** — 原生伪控制台集成，可运行任何命令行工具 — PowerShell、cmd、bash、python、node、git 等。
- **xterm.js 渲染** — 通过 WebView2 承载 xterm.js，使用 Cascadia Code 字体进行完整终端模拟。
- **Mica 背景** — 原生 Windows 11 半透明云母材质效果。
- **网页远程终端** — 内置 HTTP/WebSocket 服务器，可在局域网内通过浏览器实时查看和控制任意终端会话。支持可选的令牌认证。移动端友好，提供触屏虚拟按键。
- **右键菜单集成** — 在资源管理器中右键任意文件夹即可在 CC Pad 中打开。
- **文件关联** — 双击 `.ccpad-workspace` 文件直接打开。
- **双 CLI(Claude + Codex)** *(fork)* — 同一窗口并排开 Claude 和 Codex 标签;可设默认 CLI,固定项目可用任一 CLI 打开。
- **标签状态指示灯** *(fork)* — 每个标签上有一个彩色小圆点,一眼可知会话状态:**绿色** = AI 正在工作,**琥珀色** = 正在等你输入,**红色** = CLI 已退出。Claude(通过官方 hooks)和 Codex(通过其 `notify` 配置)都支持,不用来回切标签就知道哪个会话在等你。
- **后台"该你了"通知** *(fork)* — 当后台标签里的会话开始等待你输入时,CC Pad 会弹出 Windows 通知;点击即可直接跳到那个标签。可在"关于"菜单中开关。
- **多语言界面** *(fork)* — 界面语言可即时切换(无需重启):**English、简体中文、繁體中文、Deutsch、日本語、Français、한국어、Español、Italiano**。首次运行跟随 Windows 显示语言。覆盖应用界面、资源管理器右键菜单文案以及远程网页。
- **一键恢复对话** *(fork)* — Claude 退出后,在提示符按 **↑** 即可预填精确的 `claude --resume <id>` 命令(确认后再回车),对话恢复后标签的红灯会变回琥珀色。
- **会话恢复** *(fork)* — Chrome 式崩溃恢复(默认开启):异常退出后恢复标签和工作目录。
- **不死终端** *(fork)* — CLI 退出后终端不再变死,自动落到同目录的 shell(保留滚动历史);按回车可重新启动 CLI。
- **文件管理器** *(fork)* — 右侧停靠面板,浏览当前会话的项目目录,可用右下角工具栏的「文件」按钮开关。
- **命令寄存** *(fork)* — AI 还在忙时,先把接下来要发的命令排进队列;会话一空闲,CC Pad 就自动逐条发送。用「寄存」按钮或在面板内按 ``Alt+` `` 切换;在寄存输入框里按 ``Alt+V`` 会把剪贴板里的图片存成 PNG 并把路径排进队列(Claude 通过路径附带图片)。
- **可切换主题** *(fork)* — 深色(默认纯黑皮肤)、浅色(半透明云母)或跟随系统,可在「关于」菜单中即时切换。

## 截图

Claude Code 与 OpenAI Codex 在分屏中并排运行 —— 注意每个标签上的彩色状态指示灯:

![CC Pad — Claude 与 Codex 会话在分屏中并排运行](CCPad/Assets/Screenshot1.png)

从「项目」菜单可用任一 CLI 打开固定的项目:

![CC Pad — 项目菜单中的「用 Claude 打开 / 用 Codex 打开」](CCPad/Assets/Screenshot2.png)

「关于」菜单 —— 会话恢复、后台「该你了」通知,以及语言切换:

![CC Pad — 关于菜单:会话恢复、通知与语言切换](CCPad/Assets/Screenshot3.png)

## 安装

### 安装程序（推荐）

从 [Releases](../../releases) 页面下载最新的 `CCPad-Setup-x64.exe` 并运行。

### 从源码构建

**前置条件：**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Windows App SDK 1.8+](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- Windows 10（Build 17763）或更高版本

```bash
# 克隆仓库（本 fork）
git clone https://github.com/vv-l/CCPad.git
cd CCPad

# 构建（Debug）
dotnet build CCPad/CCPad.csproj

# 构建（Release，x64）
dotnet publish CCPad/CCPad.csproj -c Release -r win-x64

```

支持平台：`win-x64`、`win-x86`、`win-arm64`。

## 使用

### 启动

```bash
# 在当前目录打开
CCPad.exe

# 打开指定文件夹
CCPad.exe "C:\Projects\my-app"

# 打开工作区文件
CCPad.exe my-project.ccpad-workspace
```

无参数启动时，CC Pad 会自动检测当前目录下的 `.ccpad-workspace` 文件并进入工作区模式。

### 快捷键

| 操作 | 快捷键 |
|------|--------|
| 新建标签页 | `Ctrl+T` |
| 关闭标签页 | `Ctrl+W` |
| 复制选中内容 | `Ctrl+C` |
| 向右分屏 | `Alt+Shift+=` |
| 向下分屏 | `Alt+Shift+-` |
| 面板导航 | `Alt+方向键` |
| 关闭面板 | `Ctrl+Shift+W` |

右键点击终端或标签页标题可查看更多选项。

### 网页远程终端

在同一局域网内的任意浏览器中访问你的终端会话：

1. 点击工具栏中的远程终端按钮
2. 选择局域网地址，可选择是否启用令牌认证
3. 在其他设备（手机、平板、另一台电脑）上打开显示的 URL
4. 从侧边栏选择一个会话，即可实时查看和控制

功能特性：
- **实时镜像** — 与桌面终端显示完全同步
- **完整键盘输入** — 远程输入命令
- **触屏控制** — 移动端提供虚拟方向键、退格和回车
- **会话回放** — 缓存近期终端输出，连接时即时显示
- **安全** — 可选 16 字节令牌认证

### 工作区

工作区以 JSON 格式保存完整布局：

- **分屏布局** — 面板树结构，包含方向和比例
- **标签页状态** — 每个标签页的名称和工作目录
- **窗口状态** — 大小、位置和最大化状态

使用工作区按钮（右上角，工作区模式下可见）或右键菜单来保存/加载工作区。默认文件名为当前目录名。

### 项目管理

点击任意标签栏右侧的 **项目** 按钮来管理固定目录。添加项目后即可在所有面板中快速创建对应目录的新标签页。

## 架构

```
CCPad/
├── App.xaml.cs              # 入口，启动逻辑，右键菜单注册
├── MainWindow.xaml.cs       # 窗口管理，工作区模式，更新 UI
├── SplitHost.xaml.cs        # 二叉分屏树布局引擎
├── TabPanel.xaml.cs         # 标签页生命周期，项目菜单，状态指示灯
├── TerminalPane.xaml.cs     # WebView2 + xterm.js 宿主，会话注册
├── UpdateChecker.cs         # GitHub 发布检查器，自动更新
├── Terminal/
│   ├── ConPtySession.cs     # Windows ConPTY 进程管理
│   └── PseudoConsoleApi.cs  # ConPTY Win32 API P/Invoke 绑定
├── Web/
│   ├── WebTerminalServer.cs # ASP.NET Core Kestrel HTTP/WebSocket 服务器
│   ├── WebTerminalSession.cs# 远程镜像 WebSocket 处理器
│   ├── TerminalSessionRegistry.cs # 会话追踪 + 输出环形缓冲区
│   ├── CliNotify.cs         # 接收 Claude/Codex 回合信号的本地回环端点   [fork]
│   └── WebTerminalHtml.cs   # 内嵌网页 UI（含 xterm.js）
├── Notify/
│   └── ToastService.cs      # 后台"该你了"Windows 通知                  [fork]
├── Localization/
│   └── Loc.cs               # 9 语言字符串表 + 即时切换                 [fork]
├── Files/
│   └── FileManagerPanel.xaml.cs # 右侧停靠的项目文件浏览器              [fork]
├── Controls/
│   └── GridSplitter.cs      # 可拖拽分屏比例控件
├── Settings/
│   ├── WorkspaceConfig.cs   # .ccpad-workspace 文件读写
│   ├── ProjectConfig.cs     # 项目列表持久化
│   ├── AppConfig.cs         # 应用偏好(默认 CLI、语言、开关)  [fork]
│   ├── CliMode.cs           # Claude/Codex 解析 + cmd /c 包裹  [fork]
│   ├── AppPaths.cs          # 数据根目录解析(CCPAD_DATA_DIR 覆盖) [fork]
│   ├── ThemeManager.cs      # 深色/浅色/系统 主题状态 + 事件    [fork]
│   └── SessionRecovery.cs   # 崩溃恢复快照                     [fork]
└── Assets/
    └── xterm/               # xterm.js 终端模拟器
```

**渲染管线：** xterm.js (JavaScript) → WebView2 (Chromium) → WinUI 3 窗口

**布局模型：** `SplitNode` 二叉树 — 叶节点为包含 `TabPanel` 的 `PaneNode`，内部节点为带方向和比例的 `SplitContainerNode`。

## 系统要求

- Windows 10 版本 1809（Build 17763）或更高版本
- WebView2 运行时（Windows 11 已内置，Windows 10 会自动安装）

## 衍生版改动

本项目是 [nuomiaa/CCPad](https://github.com/nuomiaa/CCPad) 的社区 fork(基于上游 **v1.0.2**)。本 fork 的改动:

### v1.4.0

- **文件管理器面板** — 右侧停靠的文件浏览器,展示当前会话的项目目录,由右下角新增的「文件」按钮开关(`Files/FileManagerPanel.xaml`)。
- **命令寄存模式** — AI 还在干活时,先把接下来要发的命令排进队列;会话一空闲,CC Pad 就自动逐条发送。用「寄存」按钮或在面板内按 ``Alt+` `` 切换。在寄存输入框里按 ``Alt+V`` 会读取剪贴板图片、存成临时目录下的 PNG 并把路径排进队列(Claude Code 通过路径附带图片)——因为寄存输入框拦截了按键,CLI 自己读不到这次剪贴板。
- **更聪明的状态指示灯** — 琥珀色「该你了」灯现在能自我纠正过期的 hook 信号:hook 可能在一个回合中途把面板翻成「等待」,所以 CC Pad 只有在输出真正安静下来后才让灯保持琥珀(还在干活的 CLI 会持续重绘转圈动画,这会把灯保持为绿色)。检测到致命 API 错误横幅(如 `API Error: 529 Overloaded`、402 计费、403 鉴权)时,即使 CLI 仍停在提示符存活,也会强制亮**红灯**并跨过回合结束的 hook 一直保持到你重试。
- **可切换主题(深色 / 浅色 / 跟随系统)** — 纯黑皮肤现在是「关于」菜单中的一个选项;浅色恢复半透明云母外观,跟随系统则跟随 Windows。界面外壳通过 XAML `ThemeDictionaries`(`App.xaml`)切换,终端面板则通过 `Settings/ThemeManager.cs` 实时重新设置 xterm 前端样式。
- **隔离的数据目录** — 设置 `CCPAD_DATA_DIR` 环境变量,可让第二个实例(与正式安装并行启动的开发/演示版)拥有完全独立的配置档 —— 偏好、项目、崩溃恢复快照、锁文件、hooks 和日志 —— 从而绝不污染正式实例的会话状态。所有数据根路径现在统一经过 `Settings/AppPaths.cs`。
- **右下角工具栏重做** — 统一的 **文件 / 回车 / 寄存 / 关于** 按钮组(自动回车现在是一个一等的开关按钮,而非隐藏的开关)。版本号升到 1.4.0。

### v1.1.0

- **标签状态指示灯** — 每个标签带一个彩色圆点反映会话状态:绿色(AI 工作中)、琥珀色(等你输入)、红色(CLI 已退出)。由 Claude 官方 hooks 和 Codex 的 `notify` 配置通过一个本地回环端点(`Web/CliNotify.cs`)驱动,是事件触发而非抓屏识别。
- **后台"该你了"通知** — 当后台会话切换到"等待输入"时,CC Pad 弹出 Windows 通知(`Notify/ToastService.cs`);点击通知会聚焦到对应来源标签。可在"关于"菜单开关,状态存入应用偏好。
- **一键恢复对话** — Claude 退出后,在提示符按 ↑ 即可预填解析好的 `claude --resume <id>` 命令供确认后提交;对话恢复后标签灯变回琥珀色。
- **9 语言可切换界面** — English、简体中文、繁體中文、Deutsch、日本語、Français、한국어、Español、Italiano,通过自定义字符串表(`Localization/Loc.cs`)和 `LanguageChanged` 事件即时切换、无需重启。首次运行跟随 Windows 显示语言;选择覆盖应用界面、资源管理器右键菜单文案和远程网页,并存入应用偏好。
- **右上角自适应布局** — 工作区/项目按钮以及标签栏预留区现在根据测量出的文案宽度自适应,不再用固定边距,使较长的本地化文案(如 "Espacio de trabajo"、"Projekte")不再溢出或被裁剪。版本号升到 1.1.0。

### v1.0.x

- **双 CLI(Claude + Codex)** — 同窗口混开 Claude/Codex 标签;按 PATH/PATHEXT 解析真实可执行文件,`.cmd`/`.bat` 用 `cmd /c` 包裹(修复 `codex.cmd` 的 "CreateProcess failed: 2");默认 CLI 偏好和每个标签的 CLI 类型在重启后保留。
- **会话恢复** — Chrome 式崩溃恢复(默认开启);状态存于 `%LOCALAPPDATA%\CCPad\sessions\`;恢复标签和工作目录。
- **不死终端** — 伪控制台生命周期独立于子进程;CLI 退出后落到 `cmd.exe`(保留滚动历史)并提示按回车重启;用 `WaitForSingleObject` 可靠检测进程退出。Claude 异常退出时还会打印精确的 `claude --resume <id>` 命令(从磁盘上的会话记录解析得到),便于恢复对话。
- **Ctrl+滚轮缩放修复** — 关闭 WebView2 内置页面缩放(其持久化的 `ZoomFactor` 会累计逼近 ~5× 上限,导致面板用久后"能缩小却不能放大");现在 Ctrl+滚轮改为调整终端字号(钳制 8–40),并支持 Ctrl `+` / `-` / `0` 键盘缩放。版本号升到 1.0.6。
- **构建修复** — 关闭 trim(它曾裁掉 `WinRT.Runtime` 方法导致启动崩溃 `MissingMethodException`);版本号升到 1.0.4。

遵循 GPL-3.0,保留原始版权与许可证,改动在此处及提交历史中记录。

## 许可证

本项目基于 [GNU 通用公共许可证 v3.0](LICENSE) 授权,与上游项目相同。原始版权归上游 [nuomiaa/CCPad](https://github.com/nuomiaa/CCPad) 作者所有;fork 改动版权归各自贡献者所有。

## 参与贡献

欢迎贡献代码！请先创建 Issue 讨论你想要更改的内容。

1. Fork 本仓库
2. 创建你的功能分支（`git checkout -b feature/my-feature`）
3. 提交更改
4. 推送分支并创建 Pull Request
