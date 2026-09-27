<p align="center">
  <img src="CCPad/Assets/Square150x150Logo.scale-200.png" width="80" alt="CC Pad logo"/>
</p>

<h1 align="center">CC Pad</h1>

<p align="center">
  A multi-session AI CLI workbench — run Claude Code, Codex, and remote Codex sessions in one window.
</p>

<p align="center">
  <a href="README.zh-CN.md">中文文档</a> · <a href="LICENSE">License (GPL-3.0)</a>
</p>

<p align="center">
  <em>⚡ A community fork of <a href="https://github.com/nuomiaa/CCPad">nuomiaa/CCPad</a> — adds Codex CLI support, per-tab status lights with background "your turn" notifications, a 9-language switchable UI, Chrome-style session recovery, and a resilient terminal. Licensed under GPL-3.0; original copyright © the upstream authors. See <a href="#fork-changes">Fork Changes</a>.</em>
</p>

---

> Current release: **v1.10.12**
>
> Unreleased on `master`: Codex quota indicator.

## Features

- **Split Panes** — Vertical and horizontal splits with draggable dividers. Navigate between panes with `Alt+Arrow` keys.
- **Tabs** — Multiple tabs per pane, reorderable, with tab prewarming for instant creation.
- **Tab tags and sizing** *(fork)* — Give tabs a persistent label, resize the shared tab strip vertically, or drag an individual tab's right edge to set its width.
- **Workspaces** — Save and restore your entire layout (splits, tabs, working directories, window state) as `.ccpad-workspace` files. Auto-detects workspace files on startup.
- **Frozen tabs and templates** *(fork)* — Freeze idle tabs to release resources, thaw them on demand, drag live tabs between panes, and save or restore complete window layouts as `.ccpad-template` files.
- **Resource guard** *(fork)* — Tracks physical memory and system commit pressure and shows a non-modal warning when freezing idle tabs would help.
- **Codex quota display** *(unreleased; fork)* — The lower-left indicator shows the signed-in Codex plan, session/weekly usage, reset countdowns, and reset credits; it refreshes when opened and every five minutes.
- **Project Quick-Access** — Pin frequently-used directories for one-click new tabs.
- **Windows ConPTY** — Native pseudo-console integration. Runs any CLI tool — PowerShell, cmd, bash, python, node, git, etc.
- **xterm.js Rendering** — Full terminal emulation via xterm.js hosted in WebView2, with Cascadia Code font.
- **Mica Backdrop** — Native Windows 11 translucent material.
- **Web Remote Terminal** — Built-in HTTP/WebSocket server lets you view and control any session from a browser on the same LAN. Optional token authentication. Touch-friendly UI with on-screen keys for mobile devices.
- **Context Menu Integration** — Right-click any folder in Explorer to open it in CC Pad.
- **File Association** — Double-click `.ccpad-workspace` or `.ccpad-template` files to open them directly.
- **Local and remote CLI modes** *(fork)* — Run local Claude, local Codex, and remote Codex sessions through SSH and tmux. Pick a default mode or use it for a pinned project.
- **Multi-device remote Codex** *(fork)* — Manage multiple Linux SSH devices and external projects. Each remote tab uses its own `ccpad-*` tmux session, and stale detached sessions are cleaned up automatically.
- **Tab Status Lights** *(fork)* — Each local CLI tab shows a colored dot at a glance: **green** = the AI is working, **amber** = it's waiting for your input, **red** = the CLI has exited. Claude uses official hooks and local Codex uses its `notify` config. Codex@167 v1 has no remote-to-local hook bridge, so only its red SSH-exit state is authoritative.
- **Last-command bar** *(fork)* — Show the most recently submitted command at the top of every terminal; click it to copy the full command or press `Alt+L` to toggle it.
- **Background "Your Turn" Notifications** *(fork)* — When a session in a background tab starts waiting for you, CC Pad pops a Windows toast; click it to jump straight to that tab. Toggle it from the About menu.
- **Auto-reply** *(fork)* — Configure trigger phrases that send a response automatically, with typing protection, cooldowns, retry caps, and cross-window preference sync.
- **Auto-Enter** *(fork)* — Optionally press Enter automatically when the CLI shows a confirmation prompt.
- **Multi-Language UI** *(fork)* — Switch the interface language live (no restart) between **English, 简体中文, 繁體中文, Deutsch, 日本語, Français, 한국어, Español, Italiano**. First run follows your Windows display language. Covers the app UI, the Explorer right-click labels, and the remote web page.
- **One-Key Conversation Resume** *(fork)* — After Claude exits, press **↑** at the prompt to pre-fill the exact `claude --resume <id>` command (reviewed before you hit Enter), and the tab's red dot flips back to amber once the conversation is restored.
- **Session Recovery** *(fork)* — Chrome-style crash recovery (on by default): after an unexpected exit, restore your tabs and working directories.
- **Resilient Terminal** *(fork)* — When the CLI exits, the terminal drops into a shell in the same directory (scrollback preserved) instead of dying; press Enter to relaunch the CLI.
- **File Manager** *(fork)* — A right-docked panel to browse the active session's project directory, toggled from the bottom-right toolbar.
- **Command Staging** *(fork)* — Queue your next prompts while the AI is busy; CC Pad auto-sends them one at a time as soon as the session goes idle. Toggle it with the 寄存 button or ``Alt+` `` in a pane; `Alt+V` in the staging box saves a clipboard image to a PNG and queues its path (Claude attaches images by path).
- **Switchable Theme** *(fork)* — Dark (the default all-black skin), Light (translucent Mica), or System (follows Windows) — switched live from the About menu.

## Screenshots

Claude Code and OpenAI Codex running side by side in split panes — note the colored status dot on each tab:

![CC Pad — Claude and Codex sessions side by side in split panes](CCPad/Assets/Screenshot1.png)

Open any pinned project with either CLI from the Projects menu:

![CC Pad — Projects menu with "Open with Claude / Open with Codex"](CCPad/Assets/Screenshot2.png)

The About menu — session recovery, background "your turn" notifications, and the language switcher:

![CC Pad — About menu showing recovery, notifications, and the language switcher](CCPad/Assets/Screenshot3.png)

### Multi-pane, mixed CLI, and recovery controls

These examples show Codex and Claude running together in a four-pane layout, live language switching, the staging queue, and the session-recovery controls:

![CC Pad — two Codex and two Claude sessions in a four-pane layout](CCPad/Assets/Screenshot4-mixed-cli.png)

![CC Pad — four-pane mixed CLI layout in the English interface](CCPad/Assets/Screenshot5-mixed-cli-en.png)

![CC Pad — staging queue, session recovery, language switching, and version information](CCPad/Assets/Screenshot6-recovery-staging.png)

## Installation

### Installer (Recommended)

Download the latest `CCPad-Setup-x64.exe` from the [Releases](../../releases) page and run it.

### Build from Source

**Prerequisites:**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Windows App SDK 1.8+](https://learn.microsoft.com/windows/apps/windows-app-sdk/)
- Windows 10 (Build 17763) or later

```bash
# Clone (this fork)
git clone https://github.com/vv-l/CCPad.git
cd CCPad

# Build (Debug)
dotnet build CCPad/CCPad.csproj

# Build (Release, x64)
dotnet publish CCPad/CCPad.csproj -c Release -r win-x64

```

Supported targets: `win-x64`, `win-x86`, `win-arm64`.

### Updates

CC Pad checks the fork's GitHub Releases API a few seconds after startup. When a newer release is found, the update button appears in the lower-right toolbar; the About menu also has **Check for updates**. The updater downloads the installer matching the current architecture when one is available, then exits and launches it. The release workflow currently publishes the installer and portable ZIP for **x64**; x86 and ARM64 are supported for source builds but do not currently have published release assets.

## Usage

### Launch

```bash
# Open in current directory
CCPad.exe

# Open a specific folder
CCPad.exe "C:\Projects\my-app"

# Open a workspace file
CCPad.exe my-project.ccpad-workspace

# Open a frozen template in a separate window
CCPad.exe my-layout.ccpad-template
```

When launched without arguments, CC Pad auto-detects `.ccpad-workspace` files in the current directory and enters workspace mode.

To run a development or demo instance without sharing the production profile, set `CCPAD_DATA_DIR` before launching. The override moves preferences, projects, recovery snapshots, templates, hooks, logs, and lock files under that directory:

```powershell
$env:CCPAD_DATA_DIR = "$PWD\.ccpad-demo-data"
.\CCPad.exe
```

### Keyboard Shortcuts

**Tabs & panes**

| Action | Shortcut |
|--------|----------|
| New tab | `Ctrl+T` |
| Close tab | `Ctrl+W` |
| Split right (vertical) | `Alt+Shift+=` |
| Split down (horizontal) | `Alt+Shift+-` |
| Navigate between panes | `Alt+Arrow Keys` |
| Close current pane | `Ctrl+Shift+W` |

**Font size**

| Action | Shortcut |
|--------|----------|
| Zoom in | `Ctrl++` · `Ctrl+=` · `Ctrl+Wheel Up` |
| Zoom out | `Ctrl+-` · `Ctrl+Wheel Down` |
| Reset font size | `Ctrl+0` |

**Terminal selection & copy**

| Action | Shortcut |
|--------|----------|
| Copy selection | `Ctrl+C` *(passes through to the CLI when nothing is selected)* |
| Clear the current input line | `Alt+A` |
| Select the whole screen (to copy) | `Alt+Shift+A` |
| Toggle the last-command bar | `Alt+L` |

**Command staging** *(fork)*

| Action | Shortcut |
|--------|----------|
| Toggle staging mode | ``Alt+` `` |
| Queue the current entry | `Enter` *(`Shift+Enter` for a newline)* |
| Select all / deselect in the staging box | `Alt+A` |
| Paste a clipboard image (saved as PNG, path queued) | `Alt+V` |

**Resume & file panel** *(fork)*

| Action | Shortcut |
|--------|----------|
| Resume the last Claude conversation (at the prompt after the CLI exits) | `↑` |
| File panel — open selection (enter folder / open file) | `Enter` |
| File panel — go up one directory | `Backspace` |

Right-click the terminal or a tab header for additional options.

### Web Remote Terminal

Access your terminal sessions from any browser on the same network:

1. Click the remote terminal button in the toolbar
2. Select your LAN address, choose a port (default `9220`), and decide whether to auto-increment when that port is busy
3. Optionally enable token authentication
4. Open the displayed URL on another device (phone, tablet, another PC)
5. Select a session from the sidebar to view and control it in real-time

Features:
- **Live mirroring** — See exactly what's on the desktop terminal
- **Full keyboard input** — Type commands remotely
- **Touch controls** — On-screen arrow keys, backspace, and enter for mobile devices
- **Session replay** — Recent terminal output is buffered for instant display when connecting
- **Security** — Optional 16-byte token authentication. Without a token, anyone who can reach the listening address can control the listed sessions.

### Workspaces

Workspaces save your complete layout as a JSON file:

- **Split layout** — Pane tree structure with orientations and ratios
- **Tab states** — Name and working directory for each tab
- **Window state** — Size, position, and maximized state

Use the workspace button (top-right, visible in workspace mode) or the context menu to save/load workspaces. The default filename is the current directory name. The same menu manages the frozen-template library: save a new `.ccpad-template`, open one in a new window or restore it into the current window, and import/export template files.

### Session and resource controls

- The **Freeze** toolbar menu can freeze idle tabs, freeze all tabs, or thaw every frozen tab. Freezing releases the tab's CLI and WebView2 resources and leaves a click-to-thaw placeholder; remote sessions detach and can be reattached later. Optional auto-freeze is off by default and can be set to 30 minutes, 1 hour, 2 hours, or 4 hours of CLI idle time.
- **About → Restore closed session** keeps up to 12 recent layouts. Restoring replaces the current window after first archiving it, so the operation can be undone from the same menu. Session recovery can be disabled there, and **Clear recovery data** removes the saved snapshots and history.
- Right-click a tab to set or clear a user tag. Tags, custom tab widths, remote device IDs, and frozen state are included in workspaces, templates, and recovery snapshots.

### Safety and preferences

- **Auto-Enter** and **Auto-reply** are separate toolbar controls. Auto-Enter presses Enter when a confirmation prompt is detected. Auto-reply matches configured phrases in pane output; left-click toggles it and right-click edits rules. Rules have a 30-second per-phrase cooldown and a retry cap.
- **Codex quota display** reads the local `%CODEX_HOME%\auth.json` file (or `%USERPROFILE%\.codex\auth.json`) and requests usage over HTTPS. The access token is kept in memory for the request and is not written to CC Pad logs; if Codex is not signed in, the indicator remains unavailable.
- **About → Bypass permission prompts** controls newly launched local Claude tabs and is enabled by default in this fork (`--permission-mode bypassPermissions`). Turn it off when you want Claude's normal approval prompts. Existing sessions keep their original launch mode.
- **About → Confirm before closing** controls the close dialog and its restore-on-next-launch choice.

### Projects

Click the **Local** button in any tab strip footer to manage pinned Windows directories. Adding a project makes it available as a quick-launch option across all panes.

The adjacent **External** button manages Linux SSH devices and project directories on those devices. Add a device with its name, host, port, Linux user, private-key path, and default working directory, then use the built-in test for SSH, Linux, directory, tmux, Codex CLI, and login readiness. Devices are stored in `%LOCALAPPDATA%\CCPad\remote-devices.json`; the legacy Codex@167 preferences migrate automatically.

After a device is ready, add an external project, bind it to that device, and enter an absolute path such as `/zettos/pool/1/agents/myproject`. Adding only registers the directory; click the resulting item or use **Open with remote Codex** from its context menu. Projects remain in `remote-projects.json`, and device IDs/remote directories survive freeze, workspace restore, and crash recovery.

**New remote Codex tab** and **Recover remote Codex conversation** use the selected device's default directory. Each tab owns a private `ccpad-*` tmux session. Explicit close ends it; freeze, pane migration, recoverable window close, and SSH loss only detach it, with the remote sweeper handling stale detached sessions.

The built-in `codex-167` device starts new sessions with `codex --yolo`, bypassing approvals and sandboxing; the resume picker inherits the same permission flag. Legacy `codex` and `-s/--sandbox danger-full-access` forms are normalized to `codex --yolo` when the device profile is read. Existing tmux processes keep their original launch arguments until closed and reopened.

In a remote Codex pane, text paste works normally. Pasting an image with `Ctrl+V` or `Alt+V` uploads it to the device under `/tmp/ccpad-images` and pastes the remote path into the CLI, so the SSH account needs write access there.

### AI-assisted onboarding

The **External** menu can copy an onboarding prompt for an AI. The development workspace also has an optional `ccpad-onboard-linux-device` skill, but that skill is maintained outside this Git repository and is not included in release packages. It can probe Linux requirements and either write the CC Pad device/project configuration or return the exact checks and fields for semi-manual UI entry. It never stores passwords, private-key contents, OpenAI keys, or Codex login credentials.

## Architecture

```
CCPad/
├── App.xaml.cs              # Entry point, startup, context menu registration
├── MainWindow.xaml.cs       # Window management, workspace mode, update UI
├── SplitHost.xaml.cs        # Binary split-tree layout engine
├── TabPanel.xaml.cs         # Tab lifecycle, project menu, status lights
├── TerminalPane.xaml.cs     # WebView2 + xterm.js host, session registration
├── UpdateChecker.cs         # GitHub release checker, auto-update
├── Terminal/
│   ├── ConPtySession.cs     # Windows ConPTY process management
│   └── PseudoConsoleApi.cs  # P/Invoke bindings for ConPTY
├── Web/
│   ├── WebTerminalServer.cs # ASP.NET Core Kestrel HTTP/WebSocket server
│   ├── WebTerminalSession.cs# WebSocket handler for remote mirroring
│   ├── TerminalSessionRegistry.cs # Session tracking + output ring buffer
│   ├── CliNotify.cs         # Loopback endpoint for Claude/Codex turn signals [fork]
│   └── WebTerminalHtml.cs   # Embedded web UI with xterm.js
├── Notify/
│   └── ToastService.cs      # Background "your turn" Windows toasts        [fork]
├── CodexQuota/
│   └── CodexQuotaService.cs # Local Codex auth + usage endpoint             [fork]
├── Localization/
│   └── Loc.cs               # 9-language string table + live switching     [fork]
├── Files/
│   └── FileManagerPanel.xaml.cs # Right-docked project file browser        [fork]
├── Controls/
│   └── GridSplitter.cs      # Draggable split ratio control
├── Settings/
│   ├── WorkspaceConfig.cs   # .ccpad-workspace file I/O
│   ├── ProjectConfig.cs     # Project list persistence
│   ├── AppConfig.cs         # App prefs (default CLI, language, toggles)  [fork]
│   ├── CliMode.cs           # Claude/Codex resolution + cmd /c wrapping   [fork]
│   ├── CliSessions.cs        # Conversation/session ID discovery           [fork]
│   ├── FrozenTemplateStore.cs # .ccpad-template library                   [fork]
│   ├── ResourceGuard.cs      # Physical memory + system commit warnings    [fork]
│   ├── AppPaths.cs          # Data-root resolver (CCPAD_DATA_DIR override) [fork]
│   ├── ThemeManager.cs      # Dark/Light/System theme state + events      [fork]
│   ├── SessionRecovery.cs   # Crash-recovery snapshots + closed history    [fork]
│   ├── RemoteDeviceConfig.cs # SSH device definitions + migration          [fork]
│   ├── RemoteDeviceConnection.cs # SSH readiness checks                    [fork]
│   ├── RemoteProjectConfig.cs # External project persistence               [fork]
│   └── RemoteSessions.cs    # Per-tab tmux sessions + stale sweeper        [fork]
└── Assets/
    └── xterm/               # xterm.js terminal emulator
```

**Rendering pipeline:** xterm.js (JavaScript) → WebView2 (Chromium) → WinUI 3 window

**Layout model:** Binary tree of `SplitNode` — each leaf is a `PaneNode` containing a `TabPanel`, each internal node is a `SplitContainerNode` with orientation and ratio.

## System Requirements

- Windows 10 version 1809 (Build 17763) or later
- WebView2 Runtime (bundled with Windows 11, auto-installed on Windows 10)

## Fork Changes

This is a community fork of [nuomiaa/CCPad](https://github.com/nuomiaa/CCPad) (based on upstream **v1.0.2**). Changes made in this fork:

### Unreleased

- **Codex quota display** — Show the signed-in account's plan, session/weekly usage, reset times, and reset credits in the lower-left toolbar indicator.
- **Codex / Claude resume entries** — The local-project menu now offers **Resume Codex** and **Resume Claude**, opening the corresponding CLI resume picker.
- **Exact resume after exit** — Codex and Claude keep the detected session ID after exit, show a runnable resume command, and let **↑** put it back into the shell input line.
- **Multi-process layout recovery** — When several CC Pad windows start together, each process consumes only its own recovery snapshot instead of clearing layouts still waiting for another window.
- **CLI launch from elevated hosts** — When CC Pad is started from an administrator terminal, child CLIs use a standard-user token so a Codex daemon cannot inherit administrator privileges.

### v1.10.12

- **Codex daemon isolation** — Local Codex launch, resume, picker, fork, and the post-exit resume shortcut now pass `--no-daemon`, so sessions do not inherit an elevated Windows daemon and can resume after restarting CC Pad.

### v1.10.11

- **Split-pane localization refresh** — Rebuilt split layouts keep their language subscriptions, so project and external-project labels update together across all panes.
- **Live UI refresh** — Existing tab menus, frozen placeholders, terminal error overlays, the file panel, and the staging page now use the newly selected language immediately.

### v1.10.10

- **Multi-device remote Codex** — Add, edit, test, and select multiple Linux SSH devices and external projects. Each remote tab gets its own `ccpad-*` tmux session; stale detached sessions are cleaned up automatically. The legacy Codex@167 profile migrates to the built-in `codex-167` device and launches new sessions with `codex --yolo`.
- **Remote project persistence** — External device IDs and Linux project directories survive workspaces, tab freezing, and crash recovery. Remote Codex tabs can create new sessions or recover existing conversations.
- **Auto-reply and status recovery** — Triggered replies have cooldowns, retry limits, and typing guards. Capacity stalls and incremental output update status lights without waiting for a stale hook signal.
- **Tab and staging quality of life** — Drag the right edge of a tab to resize it (72–640 px), double-click to reset, and drag staged commands to reorder them. Widths and other tab settings persist.
- **Localization and onboarding** — Remote-device flows and the AI onboarding copy are localized across the supported UI languages; the optional device-preparation skill remains an external workspace tool.

### v1.9.0 (milestone commit; no tag)

- **Frozen-template library** — Save, restore, overwrite, rename, import, and export complete window layouts as `.ccpad-template` files from the workspace menu.
- **Commit-aware resource warnings** — Memory-pressure warnings now track system commit charge as well as physical RAM and suggest freezing idle tabs.
- **Localization sweep** — Bottom toolbar controls, the file manager, terminal error overlays, and in-page staging UI now follow the selected language.

### v1.8.0 (milestone commit; no tag)

- **Session recovery rework** — Per-process snapshots, closed-session history, stronger session attribution, and clear notices for missing or fast-exiting sessions.
- **Freeze/thaw lifecycle** — Click-to-thaw placeholders, optional automatic freezing, warm-renderer reuse, and safe recovery when thawing fails.
- **Cross-panel tab drag** — Move live tabs between split panes without closing their sessions; cold thaw can start the CLI in parallel with renderer setup.

### v1.5.0–v1.7.x (continuous iterations; no individual tags)

- **v1.5.0 last-command bar** — Show the most recently submitted command at the top of every terminal, click to copy it, and toggle it globally with `Alt+L`; the command comes from the real PTY input stream and works for Claude, Codex, and shells.
- **v1.6–v1.7 stability iterations** — Continued work on command staging, status lights, terminal rendering, and theme behavior. These commits did not receive individual Git tags and are recorded here as one continuous iteration range.
- **Version record** — The repository has formal tags for v1.4.0 and v1.10.10; v1.5.0–v1.9.0 are milestone commits or local release records and are now listed together in this history.

### v1.4.0

- **File manager panel** — a right-docked file browser for the active session's project directory, toggled by the new **文件** button in the bottom-right cluster (`Files/FileManagerPanel.xaml`).
- **Command staging mode** — queue your next prompts while the AI is still working; CC Pad auto-sends them one at a time as soon as the session goes idle. Toggle it with the **寄存** button or ``Alt+` `` inside a pane. ``Alt+V`` in the staging box reads a clipboard image, saves it as a PNG under the temp folder, and queues its path (Claude Code attaches images referenced by path), since the staging input can't see the CLI's own clipboard read.
- **Smarter status lights** — the amber "your turn" light now self-corrects against stale hook signals: a hook can flip a pane to *waiting* mid-turn, so CC Pad only keeps it amber once output has gone quiet (a still-working CLI redraws its spinner continuously, which holds the light green). A fatal API-error banner (e.g. `API Error: 529 Overloaded`, 402 billing, 403 auth) now forces a **red** light and holds it across the turn-ending hook, even though the CLI stays alive at its prompt, until you retry.
- **Switchable theme (Dark / Light / System)** — the all-black skin is now a choice in the About menu; Light restores the translucent Mica look and System follows Windows. The chrome switches through XAML `ThemeDictionaries` (`App.xaml`) while the terminal panes re-style their xterm front-end live via `Settings/ThemeManager.cs`.
- **Isolated data directory** — set the `CCPAD_DATA_DIR` environment variable to give a second instance (a dev/demo build launched alongside the real install) a fully separate profile — prefs, projects, crash-recovery snapshot, lock files, hooks and logs — so it never cross-contaminates production session state. All data-root paths now flow through `Settings/AppPaths.cs`.
- **Bottom-right toolbar redesign** — a uniform **文件 / 回车 / 寄存 / 关于** button cluster (auto-confirm is now a first-class toggle button rather than a hidden switch). Version bumped to 1.4.0.

### v1.1.0

- **Tab status lights** — each tab carries a colored dot reflecting its session state: green (AI working), amber (waiting for your input), red (CLI exited). Driven by Claude's official hooks and Codex's `notify` config via a small loopback endpoint (`Web/CliNotify.cs`), so the signal is event-based rather than screen-scraped.
- **Background "your turn" notifications** — when a backgrounded session transitions to "waiting for input", CC Pad raises a Windows toast (`Notify/ToastService.cs`); activating it focuses the originating tab. Toggleable from the About menu and persisted in app prefs.
- **One-key conversation resume** — after Claude exits, pressing ↑ at the prompt pre-fills the resolved `claude --resume <id>` command for review before submission; the tab light returns to amber once the conversation resumes.
- **9-language switchable UI** — English, 简体中文, 繁體中文, Deutsch, 日本語, Français, 한국어, Español, Italiano, switched live without restart through a custom string table (`Localization/Loc.cs`) and a `LanguageChanged` event. First run follows the Windows display language; the selection covers the app chrome, the Explorer context-menu labels, and the remote web page, and is persisted in app prefs.
- **Adaptive top-right layout** — the Workspace/Projects buttons and the tab-strip reserve now size themselves from measured label widths instead of fixed margins, so long localized labels (e.g. "Espacio de trabajo", "Projekte") no longer overflow or clip. Version bumped to 1.1.0.

### v1.0.x

- **Dual-CLI (Claude + Codex)** — mixed Claude/Codex tabs in one window; PATH/PATHEXT executable resolution with `cmd /c` wrapping for `.cmd`/`.bat` (fixes `codex.cmd` "CreateProcess failed: 2"); default-CLI preference and per-tab CLI persisted across restarts.
- **Session recovery** — Chrome-style crash recovery (default on); state under `%LOCALAPPDATA%\CCPad\sessions\`; restores tabs and working directories.
- **Resilient terminal** — the pseudoconsole outlives its child process; on CLI exit it drops into `cmd.exe` (scrollback preserved) and offers an Enter-to-relaunch; reliable exit detection via `WaitForSingleObject`. On abnormal Claude exits it also prints the exact `claude --resume <id>` command (resolved from the on-disk session transcript) so the conversation can be recovered.
- **Ctrl+wheel zoom fix** — disabled WebView2's built-in page zoom, whose persistent `ZoomFactor` accumulated toward its ~5× ceiling so a long-lived pane could "zoom out but not in"; Ctrl+wheel now adjusts the terminal font size (clamped 8–40), with Ctrl `+` / `-` / `0` keyboard parity. Version bumped to 1.0.6.
- **Build fix** — disabled trimming, which had stripped `WinRT.Runtime` methods and crashed startup (`MissingMethodException`); version bumped to 1.0.4.

In keeping with GPL-3.0, the original copyright and license are preserved, and these modifications are documented here and in the commit history.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE), the same license as the upstream project. Original copyright © the upstream [nuomiaa/CCPad](https://github.com/nuomiaa/CCPad) authors; fork modifications © their respective contributors.

## Contributing

Contributions are welcome! Please open an issue first to discuss what you'd like to change.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes
4. Push to the branch and open a Pull Request
