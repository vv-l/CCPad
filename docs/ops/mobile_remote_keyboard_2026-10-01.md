# 手机远程键盘：终端输入法与文本注入修复（1.10.30）

用户报告：手机软键盘经远程桌面输入 CCPad 时文字不正确；电脑键盘输入正常，同一手机输入普通 txt 正常。尚未取得用户手机的实际事件轨迹，因此不能断言覆盖了其远程软件的全部输入方式。

用户随后确认手机使用 **网易 UU 远程**；本机 `GameViewer.exe` 文件版本核验为 **4.40.1.2090**（电脑端版本，手机端版本未知）。因此现场路径是 UU 远程控制 Windows 桌面 → CCPad WebView2 → xterm → PTY，而不是 CCPad 自带网页远程入口。已有自动用例覆盖桌面前端的 Unicode packet、直接提交文字和整字段替换输入法。公开资料和本机可用日志未提供此次手机输入的事件轨迹；不能据此认定 UU 一定使用整字段替换，也不能把修复后的模拟用例当作 UU 实机验收。源码本轮不追加推测性的 UU 专用按键改写。

## 已复现的机制

当前发布版与源码的 xterm.js 字节相同，对应 5.3.0。隔离 Chromium 中加载实际 TerminalHtml、相同资源及模拟宿主，不连接 CLI 或 PTY：

1. 通过 CDP 输入法提交 `abc`，终端 onData 得到 `abc`，隐藏 textarea 也残留 `abc`。
2. 后续输入法以整个输入框为替换范围，提交 `中文`。
3. xterm CompositionHelper 沿用旧 composition start=3，从新值 `中文` 的第 3 位截取，结果为空。终端仅收到 `abc`。

原版 xterm、桌面前端、网页前端均复现。普通文本框保留完整 `中文`。Unicode packet（VK_PACKET=231）测试正常，不能把所有远程 Unicode 注入都判为错误。

上游同类记录：[残留与字段替换](https://github.com/xtermjs/xterm.js/issues/6012)、[整字段替换截断](https://github.com/xtermjs/xterm.js/issues/6049)。这些记录支持排查方向，修复判断以本项目 5.3.0 的实际测试为准。

## 改动

- `Assets/xterm/ccpad-input-compat.js`：通过公开 addon API，在已提交输入的 DOM 事件与 xterm 延迟处理完成后清空隐藏输入框；新按键或 compositionstart 取消旧清理任务；输入法组词过程中保留候选文本。
- 桌面和网页前端共用此 addon，不修改 xterm 第三方打包文件，不改 UTF-8/PTY 通道。
- screenReaderMode 下保留 textarea 值，避免破坏朗读。
- 寄存输入框在 `isComposing` 或 keyCode 229 时不响应提交快捷键，候选确认回车不会入队。
- 原有尚未提交的 Auto-Enter/远程审批匹配和 README 改动保留。

## 验证与限制

运行 `python tests/terminal_input_compat.py --report <输出.json>`；`--baseline` 加载 Git HEAD 的嵌入前端和未挂载 addon 的 xterm。

42 项覆盖三个前端的英文/中文、Unicode packet、emoji、字段替换、候选保留/取消、快速连续提交、229 延迟 diff、退格、粘贴、控制/方向键、screen reader，以及寄存输入法与普通回车。

修复前 37/42，修复后 42/42，页面脚本错误 0。测试使用实际浏览器输入/输入法 API，也包含明确标注的合成 229/寄存事件；不是用户手机实机远程操作。若其远程客户端还有其他事件形态，需要继续扩充输入兼容性用例。

本机发布必须使用外层 publish-live.ps1，保留所有旧版本（Keep=2147483647），运行中的窗口不退出、不重启、不覆盖。新版在原进程自然退出后自动激活，之后启动才使用修复。发布状态及证据保存在外层 research/mobile-input-diagnosis-20261001。

## 用户补充“两个字符变首字符”后的第二轮

用户确认出错区域为终端，并描述输入两个字符后变成首字母。`ab→a` 与 `ab→aa` 的精确区别尚待澄清，自动用例覆盖两类；依然没有获取 UU 手机实机事件。

本项目实际 xterm 5.3.0 中复现另一条路径：

- 浏览器字符包提交 `ab`，keypress 的 charCode=97，仅表示 `a`；后续原生 input.data=`ab`。
- xterm `_keyPress` 先发送 `a` 并设置 `_keyPressHandled=true`；`_inputEvent` 丢弃完整提交，终端只收到 `a`。提交 `中文` 同样只收到 `中`。
- VK_PACKET=231 的 keydown 先发送首字符并 preventDefault 时，还可能使后续原生字符事件不再触发。
- 合成折叠键用例中，先提交 `a`，第二次 keypress.key=`ab`、charCode=97，但 input.data=`b`，旧版会发送 `aa`。这是事件模型验证，未确认 UU 使用了这一形态。

1.10.28 只清理已发送文字的残留，不能修复这些路径；因此准备新的不可变发布 1.10.29，旧发布目录保留。

1.10.29 的 addon 在 xterm 事件处理前识别 Unicode 字符包；多字符 keypress 等待真实 input.data。若按键路径已发送完整提交的前缀，只补发未发送的后缀。补发经桌面原有 sendTerminalInput 或网页 sendKey 进入同一宿主通道，不写剪贴板、不使用 paste API，不引入 bracketed paste 包装；disableStdin 继续生效。组合输入、控制键和物理按键仍交给原 xterm 路径；读屏模式保留输入框内容与原生 input 事件。

扩展至 78 项，新增多字符/中文/emoji 字符包、首字符/多字符/Unidentified 键描述、Unicode keydown、折叠键首字重复、bracketed paste 模式、disableStdin 和读屏事件验证。修复后 78/78，页面错误 0。证据为 `multichar-regression-results.json`；旧版对照为 `multichar-regression-baseline.json`。78 项里折叠键与原有 229/寄存检查是合成 DOM 事件，其余字符注入/输入法用例走 CDP；都不接入真实 CLI/PTY。

同一套 78 项旧版对照为 40/78，新版为 78/78。1.10.29 已编译至 `D:\CC Pad\app\v1.10.29_20261001-062809`，发布资源逐文件与源码核验一致。原 PID 15628 存活，current 仍指向 1.10.27；pending 与下次登录激活记录均指向 1.10.29，Keep=2147483647。1.10.28 等旧目录全部保留，未执行删除或重启。第二轮发布收据为 `publish-receipt-1.10.29.json`。

## 两张手机截图后的第三轮

按用户给图的原始顺序检查：图 1 的寄存输入框可见“你好”，图 2 的手机候选词为“你好”，终端输入行则出现两个相同的错误汉字。截图较模糊，不把该错误字识别为特定汉字。图 2 还显示拼音组词 `ni'h`，因此截图本身并不是候选确认后的完整事件轨迹，也不是待激活修复版的实机验收。

新增模型用明确的测试字 `甲` 表示错误按键字符：keypress.charCode=30002，但 input.data=`你好`，或连续两次分别为 `你`/`好`。1.10.29 不会处理错误按键字与实际文字不匹配的情况，分别得到 `甲` 或 `甲甲`。这解释了前一轮用例的覆盖缺口；不是断言 UU 实际发送了这些事件参数。

1.10.30 对没有物理 code（空或 Unidentified）的可打印 keypress 等待真实 input.data，再通过同一宿主输入通道发送。VK_PACKET 不再信任可能不一致的扫描键描述；keyCode 229 的非组合字符继续由 xterm 延迟 textarea diff 唯一发送，避免额外 keypress 重复。正常物理按键、组合候选预览、控制键和读屏行为继续回归。

同一套 96 项测试，用已发布的 1.10.29 资源对照为 78/96，用修复源码资源为 96/96，页面错误均为 0。新增的错误字符与 input.data 不一致用例是合成 DOM 事件模型，另有实际 CDP Unicode 字符包用例；不把它们称为 UU 实机复现。测试命令可加 `--assets <不可变发布目录>/Assets/xterm`，用于保留旧资源作对照。证据：`image-regression-before-1.10.29.json`、`image-regression-results.json`。截图源文件与放大检查图的路径、SHA256 只记在外层 research，不将用户截图复制到 Git 源码仓。

1.10.30 已通过 `publish-live.ps1 -DisableReadyToRun -Keep 2147483647` 编译至 `D:\CC Pad\app\v1.10.30_20261001-063458`。发布资源逐文件与源码一致，文件版本为 1.10.30.0；构建只有已有的两条可空性警告。发布时原 PID 15628 仍存活，current 仍指向 1.10.27；pending 与下次登录激活记录已改为 1.10.30。所有旧版本目录保留，没有关闭或重启现有窗口。第三轮发布收据为外层 research 中的 `publish-receipt-1.10.30.json`；修复等待现有进程自然退出后切换，后续启动使用新版，UU 手机实机验证仍未完成。

## 用户确认具体错字后的补充验证

用户明确确认：手机输入的是 `你好`，寄存框显示 `你好`，终端命令行显示 `原原`。这里的具体错字来源于用户文字确认，不是从模糊截图猜测。再次只读核对本机，原 PID 15628 仍在运行 1.10.27.0，current 仍指向 `v1.10.27_20260929-064741`；待切换目录仍为 `v1.10.30_20261001-063458`。这次用户反馈不能当作 1.10.30 已生效后的失败。

保留原有 `甲` 事件模型，新增 `原` 按键字符与 `你好` 完整提交、`你`/`好` 分次提交的对照，并补充寄存框保留 `你好` 的浏览器提交测试。同一套 103 项用已发布的 1.10.29 资源运行，结果为 79/103，分次提交模型明确输出 `原原`；用待切换的 1.10.30 发布资源运行，结果为 103/103，三条终端前端均完整输出 `你好`，寄存框也保留 `你好`，页面错误均为 0。证据为外层 research 中的 `exact-glyph-regression-before-1.10.29.json`、`exact-glyph-regression-1.10.30.json`。

本次只补充测试与记录，没有修改已发布的 1.10.30 应用资源，也没有重启现有窗口。测试中的错字事件依然是受控合成模型；UU 手机实际事件轨迹尚未采集，实机修复效果尚未验证。
