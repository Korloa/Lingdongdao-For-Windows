# 桌面主动感知模块

本模块接入现有 LingDongDao WPF 程序的设置面板和灵动岛提醒。默认关闭。本地截图、Windows 事件、UI Automation、离线 OCR、可选 GUI 检测、状态记忆与规则评分均不访问付费 API。只有用户在面板中明确授权截图上传、配置多模态接口，并且本地门控通过后，才可能发出云端请求。用户也可以主动按快捷键触发分析。普通分析只返回建议；用户明确填写目标并启动 Agent 循环后，才会执行受限的鼠标工具。

## 已实现的数据流

`ScreenCapture` 每 2 秒默认采集所有显示器，JPEG 存于受时间及字节上限约束的 `FrameRingBuffer`，不写入磁盘。`WindowsActivity` 用前台窗口 API 和原生 hook 获取进程、标题、坐标、点击、滚轮、输入活动、窗口与焦点变化；键盘 hook 不读取或保存字符。`ChangeDetector` 把画面缩到 480 像素宽，做灰度分块差分，输出整屏差异、局部变化区域及窗口切换等原因。仅首次画面、窗口切换、点击、局部/全局变化或 30 秒兜底扫描会进入 UIA/OCR。单纯滚动且画面不变不会重复执行 OCR。

项目配置及启动代码启用 PerMonitorV2 DPI 感知，使 Win32 窗口、鼠标、UIA 和截图尽量处于同一物理像素坐标系；混合缩放多显示器仍需在目标机器实测。

`UiAutomationReader` 读取有界深度的控件树；密码控件只保留边界框。OCR 优先针对变化区域；窗口切换或周期扫描回退到前台窗口。`ObservationFusion` 关联 OCR、控件与点击，区分观测事实和推断事件。`StateMemory` 在内存里维护最近活动、任务类别、同一错误的持续次数和帮助冷却。`RuleClassifier` 输出活动、事件、重要性、介入分、置信度与理由；分数是**未训练、未校准的启发式调度分**，不是模型概率。`ILocalClassifier` 是后续替换为 fastText/ONNX 分类器的接口；目前没有标注样本，因此没有训练模型。

`CloudEscalationGate` 检查阈值（默认 0.8）、相似事件 15 分钟去重、冷却时间、每小时调用数、每日预估费用预算、敏感窗口及上传授权。门控先做不扣额度的检查；只有候选事件通过后，才对**整个活动窗口重新做 OCR**，检查画面其他位置的密钥文字，然后构建事件包并扣除预估额度。`PrivacyFilter` 只裁剪当前活动窗口，遮挡 UIA 密码字段；识别到密钥样式文字、敏感窗口或 UIA 信息缺失时拒绝上传。前一帧只有在窗口位置、句柄一致、当前没有密码字段，且前一帧也经过单独的完整 OCR 隐私检查时才纳入。云端包保留 JPEG 原始视觉信息，同时附 OCR/GUI 坐标、交互、记忆及评分。云端不可用时本地循环继续运行。

若云端对某个事件明确返回“不提醒”，同一事件在随后 2 小时内不会再次自动上传，减少重复调用；手动分析不受这项抑制影响。

云端调用额度记录持久化于 `%LOCALAPPDATA%\LingDongDao\cloud-usage.json`，只含调用时间、事件哈希和预估费用，不含截图、OCR 或密钥。重启后仍执行去重、每小时次数及每日预算限制；记录损坏或无法读取时会停止云端调用，避免意外突破预算。

### 主动分析与调用记录

默认在目标软件窗口按 **Ctrl+Alt+J** 主动分析当前窗口，可在“启动与快捷键”修改。手动请求无需等待本地分数达到阈值，也不受自动去重、冷却和每小时次数限制；仍须已授权上传、配置密钥，通过当前窗口完整 OCR 与 UIA 隐私检查，并受每日费用预算约束。感知暂停时也可手动分析。手动分析无论模型判断是否需要打断，都会在灵动岛显示简短结果；详细内容在设置面板的“查看云端调用记录与截图”中。

每次真正向云端发出的请求从新版开始保存实际发送的当前窗口 JPEG、可选事件前 JPEG、结构化判断、建议、完整 JSON 回复或失败状态。记录由 Windows 当前用户 DPAPI 加密保存在 `%LOCALAPPDATA%\LingDongDao\cloud-history`，最多保留最近 100 次，可在记录窗口清空；云端额度账本会继续保留。旧版的调用只保存了时间与预估费用，无法补回截图或模型回复，记录窗口会明确标记。内存截图缓存与调用记录是两份独立数据；“清空内存截图”不会删除已保存的调用记录。

### Agent 操作循环

在“桌面感知”的“Agent 操作循环”输入 3～300 字的任务目标并保存，切换到目标软件后按 **Ctrl+Alt+Shift+L** 启动；按 **Ctrl+Alt+Shift+X** 或面板“停止当前 Agent”随时中止，两组快捷键可修改。任务目标只保留在当前程序进程内，重启需重新填写。每轮流程是：完整窗口隐私检查与截图 → DeepSeek 工具调用选择 `click`、`right_click` 或 `finish` → 本地校验前台窗口、屏幕坐标和密码控件 → 执行一次鼠标动作 → 暂时显示点击文字和 `X/Y` 物理像素坐标 → 隐藏标记后重新截图 → 把工具执行结果及新截图送回云端。云端仅在看到新画面并选择 `finish(success=true)` 时宣布完成。

Agent 仅操作启动时的进程；切换到其他软件、隐私检查失败、云端异常或用户中止都会停止。默认最多执行 8 次鼠标动作、最长运行 5 分钟；最后一次动作后仍可让模型查看新截图。控制面板可调步数和每日预估云端预算；每轮云端判断都占用一次预算额度。步数或预算耗尽时停止并显示未完成，避免无限点击或无限扣费。Agent 不提供键盘输入、文件修改或命令执行工具。每轮截图和工具回复会作为加密云端调用记录保存。当前实现只支持左键单击和右键单击，不能完成需要输入文字、拖拽或跨进程切换的任务。

### 快捷指令 Agent

按 **Ctrl+Alt+K** 弹出全桌面任务输入框，写自然语言任务并按 Enter。快捷 Agent 每轮将所有显示器拼成一张保留物理屏幕坐标的截图，并附上前台进程、可见窗口清单、OCR 与前台 UI Automation 控件。允许在步骤间切换软件，不再锁定启动时的进程。云端每次只选一个工具：`click`、`right_click`、`double_click`、`drag`、`scroll`、`type_text`、`press_keys`、`wait`、`open_app`、`activate_window`、`open_browser`、`navigate` 或 `finish`。`open_app` 只打开已安装程序的开始菜单快捷方式及少数 Windows 内置程序，不接受可执行文件路径；`activate_window` 只允许选择上一帧可见窗口清单中的句柄。浏览器网址工具仅接受 HTTP(S)，拒绝本机回环地址、私有 IP 字面地址及带账号密码的网址。常用快捷键经过白名单校验；不能调用任意命令或绕过 UAC 安全桌面。

蓝色光标是置顶、点击穿透的独立视觉光标，约 620 ms 平滑移动到目标并停住；真实鼠标在发送点击时短暂定位，若用户没有移动它会恢复原位。截图时蓝色光标会隐藏，防止影响云端识别；每一步重新截图并等待云端确认。Esc 关闭输入框，默认 **Ctrl+Alt+Shift+X** 停止运行。快捷键可在“启动与快捷键”修改；截图授权、密钥、预算与旧 Agent 相同。上传前全桌面 OCR 检查凭据，前台密码控件与敏感后台窗口会在截图中遮挡，锁屏和登录界面会阻止上传。对于 UI Automation 不提供控件信息的软件，密码区域无法保证识别，使用时应避免把敏感内容显示在桌面。任务未明确要求发送时，QQ/微信等常见聊天软件中的发送按钮及输入文字后的回车会被本地拦截；自绘发送控件可能无法识别。QQ/微信的实际发送流程尚未在本机验证。

全桌面模式默认最多 24 步、上限 40 步，固定窗口模式默认 8 步；两者可在“桌面感知”分别设置。每轮云端判断都计入预算。模型可能选错控件或绕路，达到上限会停止并显示未完成，用户可修改任务后重试。

DeepSeek 的 `deepseek-flash` 支持图片和函数工具调用：[视觉输入](https://api-docs.deepseek.com/guides/vision/)、[工具调用](https://api-docs.deepseek.com/guides/tool_calls/)。由于该 Chat Completions 接口的图片放在 `user` 消息里，程序先回传工具结果，再发送包含新截图的下一条 `user` 消息。工具参数仍由本地校验，云端返回的坐标不直接信任。

本地评分覆盖终端、编辑器、浏览器和 UIA 错误弹窗，以及重复点击。编辑器仍在输入时降低不确定事件分数；同一事件持续出现或重复操作时提高分数；提供过相似建议后降低分数。控制面板同时显示**当前判断**与最近 2 分钟的**待关注事件**，避免正常画面覆盖刚发现的高分问题。分数仍是规则启发式值，正常活动会保持低分。

## 运行与依赖

需要 Windows 10/11、.NET 9 SDK；离线 OCR 需要 Python 3.13（目前验证版本）。从仓库根目录运行：

```powershell
.\scripts\setup-perception.ps1
dotnet run --project .\LingDongDao\LingDongDao.csproj -c Release
```

若 Python 不在 `PATH`，运行 `.\scripts\setup-perception.ps1 -Python 'C:\path\python.exe'`。脚本使用官方 PyPI 创建项目内 `.venv`，安装 `perception_py/requirements.txt`。在设置面板“桌面感知”中开启感知、暂停/恢复、关闭云端上传、切换资源模式、调整采样间隔和阈值；面板展示当前软件、事件、评分、调用次数、进程合计内存、CPU 与黑底结构化日志。虚拟环境、模型、报告、截图均不加入 Git。

设置侧栏每项是独立页面。“桌面感知”页在 OCR/GUI 状态下有“屏幕调试标注”开关，保存后会以穿透式、不会抢焦点且排除系统截图的窗口，在各识别框上标注 OCR 文字、GUI 类型和物理像素坐标。该开关默认关闭；关闭感知或切换敏感窗口时隐藏，密码控件及与其重叠的 OCR 文本也不绘制。GUI 视觉检测框只有在增强模式、启用检测器且模型可用时才会出现；UI Automation 控件仍可作为 GUI 标注。“提醒与天气”页显示 Open-Meteo 当前温度、WMO 天气描述、数据更新时间和未来雷雨查询状态，可手动刷新。[当前天气字段说明](https://open-meteo.com/en/docs)。

OCR 和 GUI Python worker 使用请求 ID 对齐 JSON 行协议。开机后首次推理的回复等待上限为 60 秒，随后为 25 秒；超时、协议错位或 worker 异常会终止旧进程，按 10 秒起的指数退避重新启动（最高 120 秒），成功后重置退避。这样未完成的旧回复不会进入下一次请求，短暂启动故障也不会永久关闭 OCR/GUI。离线回归测试用模拟 worker 覆盖超时与 ID 错位后的自动恢复。
低负载与标准模式不会启动 GUI 视觉 worker；设置页会把这点写明，仅增强模式且 GUI 检测器已启用时显示它的运行状态。

默认 **低负载** 模式为窗口 API、UIA、按变化/兜底运行 OCR、规则评分；**标准** 模式更频繁运行 OCR；**增强** 模式增加按需 GUI 检测。GUI 检测须额外执行：

```powershell
.\scripts\setup-perception.ps1 -WithGui
```

该步骤下载官方权重、核对 SHA-256，再使用 Ultralytics 开发依赖导出 320×320 ONNX。模型与导出依赖体积较大，因此不属于默认安装。已有 ONNX 可通过环境变量 `LINGDONGDAO_GUI_MODEL` 指定。若从发布版 EXE 运行，仍须将 Python 环境和 `perception_py` 脚本准备在程序目录旁，或设置 `LINGDONGDAO_PYTHON` 为 Python 解释器绝对路径；仓库源码及测试运行会向上寻找项目 `.venv` 和 `models/gpa/model.onnx`。本版安装包的 OCR/GUI Python 运行时**尚未打包**，不能把它当成免配置的完整感知安装包。

### 云端配置

默认使用 DeepSeek 的 `https://api.deepseek.com/chat/completions` 和模型 `deepseek-flash`。官方文档确认它是 V4.1 Flash 的当前 API 名称，支持图片加文字输入及 JSON 输出：[模型说明](https://api-docs.deepseek.com/updates/)、[图片输入格式](https://api-docs.deepseek.com/guides/vision/)。在设置面板的“桌面感知”区域输入密钥并点“保存密钥”，密钥会以 Windows 当前用户 DPAPI 加密保存在 `%LOCALAPPDATA%\LingDongDao\agent-key.dpapi`，不会写入 `settings.json`、源码或日志。也可以在本地交互式运行：

```powershell
.\scripts\set-deepseek-key.ps1
```

面板里的“移除”或脚本的 `-Remove` 可撤销本机密钥。DPAPI 文件只供官方 `api.deepseek.com` HTTPS 端点读取；如改用其他兼容服务，需显式设置该服务自己的密钥和完整端点：

```powershell
$env:LINGDONGDAO_AGENT_BASE_URL = 'https://your-provider.example/v1/chat/completions'
$env:LINGDONGDAO_AGENT_MODEL = 'your-vision-model'
$env:LINGDONGDAO_AGENT_API_KEY = 'set-in-your-shell-or-secure-user-env'
```

`BASE_URL` 是完整请求端点。不要把真实密钥写入源码、提交记录或截图。环境变量在程序启动时读取，修改后重启程序。DeepSeek 默认启用非思考模式做简短决策，避免默认长思考消耗输出预算；仍保留图片直接输入和 JSON 响应。默认每小时最多 3 次、每日预估费用上限 0.25 美元、每次预估 0.02 美元、冷却 300 秒；这些是保守的本地估算，**不是供应商真实账单**。当前设置面板暴露阈值、采样和开关，更多限额可编辑 `%LOCALAPPDATA%\LingDongDao\settings.json` 的 `Perception` 对象。

云端端点只接受 HTTPS，或本机回环地址的 HTTP Mock 服务。首次允许截图上传仍须在面板单独确认。

## 模型选型与限制

| 候选 | 核查结果 | 结论 |
| --- | --- | --- |
| [RapidOCR](https://rapidai.github.io/RapidOCRDocs/main/quickstart/) + ONNX Runtime | 本机离线中英文 OCR 及四点坐标可运行；[模型许可证说明](https://rapidai.github.io/RapidOCRDocs/main/en/model_licenses/) | 默认 OCR |
| [Salesforce GPA-GUI-Detector](https://huggingface.co/Salesforce/GPA-GUI-Detector) | MIT；官方 `model.pt` SHA-256 `dd404f25b7f329998c7ed97a67827a174dd626dc2683f6844ab33a5219c05f71`；只有单一 `icon` 类，不区分按钮/输入框/菜单 | 可选增强模型，仅输出 `interactive_region`；控件类型以 UIA 为准 |
| [Microsoft OmniParser v2](https://huggingface.co/microsoft/OmniParser-v2.0) | 组件许可证需按具体版本区分；较新的 MIT 检测权重约 281 MB；本机尚未验证 CPU 推理 | 未接入，避免增大默认内存 |
| fastText / 量化 ONNX 分类器 | 项目没有可信的桌面事件标注集和校准集 | 仅保留接口，不伪称已训练 |

GUI 检测输出的 `interactive_region` 只是模型认为可交互的区域，不等于按钮或代码/终端/报错语义区域。ONNX 的坐标经缩放及边距逆变换回传到原截图，再叠加屏幕偏移。窗口程序名称来自 Windows API，不由视觉猜测。权重 `.pt` 为 pickle 格式，仅从已核对哈希的官方文件导出；运行时只加载 ONNX。

## 测试与资源报告

```powershell
dotnet run --project .\PerceptionTests\PerceptionTests.csproj -c Release
dotnet run --project .\PerceptionTests\PerceptionTests.csproj -c Release -- --live-smoke
dotnet run --project .\PerceptionTests\PerceptionTests.csproj -c Release -- --deepseek-smoke
dotnet run --project .\PerceptionTests\PerceptionTests.csproj -c Release -- --agent-tool-smoke
dotnet run --project .\PerceptionTests\PerceptionTests.csproj -c Release -- --settings-smoke
.\scripts\measure-perception.ps1
.\scripts\measure-perception.ps1 -Enhanced
```

离线回放覆盖正常浏览、Chrome→VS Code、单行局部变化、终端/编辑器/网页/弹窗报错、输入抑制、重复操作、GUI/OCR 点击关联、截图云端包、未授权/敏感窗口/密钥/预算拦截、跨重启额度、手动请求绕过自动频控但保留每日预算、加密调用记录、旧版记录占位、云端失效、DPAPI 加密及 HTTP Mock 响应。`--settings-smoke` 在独立测试进程加载 WPF 设置面板和调用记录窗口，并验证独立页面切换、当前天气格式及 OCR/GUI 调试覆盖层，不关闭正在运行的灵动岛。可附加 `--settings-snapshot` 在系统临时目录生成设置界面 PNG 供视觉检查。`--live-smoke` 读取当前屏幕；资源脚本会真实采样桌面并在 `reports/` 写 JSON，**不会写截图**。请在不含敏感内容的桌面运行实测。`--deepseek-smoke` 使用合成截图发起一次真实 API 请求，会消耗少量额度，但**不会上传当前桌面画面**。2026-11-01 已用合成截图验证 DeepSeek 返回结构化 JSON；随后实际按主动分析快捷键验证了调用、完整回复和截图写入加密记录。该次测试窗口未抢到系统焦点，实际上传的是当时的 `thunder` 活动窗口；对自动事件的灵动岛提醒仍需按模型真正返回建议的场景进一步观察。

Agent 离线回放覆盖点击、右键参数解析、窗口和坐标校验、工具结果加新截图回传、成功结束、步数耗尽停止；`--agent-tool-smoke` 用合成 JPEG 请求一次真实 DeepSeek 函数工具调用，不执行鼠标动作。本机已验证模型返回可解析的 `finish` 调用。WPF 烟测验证坐标浮层能显示文字和物理坐标、不会激活抢焦点、使用穿透式窗口样式。`--live-service-enhanced --live-debug` 已实测产生 84 个 OCR 区域和 58 个融合 GUI 控件区域，调试覆盖层在 WPF 烟测中验证了点击穿透及 Windows 截图排除。尚未在用户指定的真实任务软件里验证自动点击结果，因此不能把合成测试视为真实任务完成率证明。

v0.9.0 新增测试覆盖 `open_browser` → `navigate` → 新截图 → `finish` 的 Mock API 完整协议，以及 URL、锁屏隐私、键盘输入结构和蓝色光标点击穿透。`--quick-agent-tool-smoke` 已向真实 DeepSeek 验证五种工具定义可被接受。`--live-quick-agent` 已在 Chrome 前台实际点击地址栏、访问 baidu.com，并在第二张截图后由模型确认百度首页；从资源管理器启动浏览器的实测途中 Windows 会话进入锁屏，浏览器无法到前台，Agent 停止。该情况下已补充锁屏截图排除与新浏览器窗口激活，尚需在未锁屏的桌面再次验证完整的跨进程路径。

v0.9.1 将快捷 Agent 改为整桌面循环。离线测试覆盖负坐标显示器、跨窗口点击校验、全桌面敏感窗口遮挡、应用与窗口工具解析、禁止路径启动，以及 13 种工具定义；`--live-smoke` 在本机测得整桌面 JPEG 为 1536×864、约 0.05 MB。真实云端任务 `--live-desktop-agent` 使用 Win+E 从 Chrome 切到资源管理器并经新截图确认；`--live-text-agent` 从 Chrome 切到记事本、输入“灵动岛测试”并经新截图确认（5 步、6 次云端判断）。未实测 QQ、微信消息发送、UAC 窗口与多显示器实机交互。

v0.10.0 增加光标造型、发光、移动曲线、操作与输入节奏的设置，以及点击粒子效果；召唤窗口按当前屏幕 DPI 定位到任务栏上方并从底部中央展开。Agent 成功结束后，`AgentSkillStore` 使用当前用户 DPAPI 保存任务目标和安全的动作摘要，不保存截图、点击坐标和输入文字。再次执行同一目标时把经验作为上下文交给模型；模型仍要检查当前截图，所以这不是未经验证的坐标脚本回放。`AgentWakeScheduler` 将最多 5 个定时唤醒加密保存，用单次计时器等待最近到期项，不持续截图或调用云端。云端可调用 `schedule_wake(seconds, context, label)`，限制 10 秒至 24 小时；到期后用当前截图和原任务重启全桌面 Agent。唤醒失败会提示原因，未授权、密钥缺失或预算不足时不会上传截图。设置面板可查看并清空经验和待唤醒任务。离线测试覆盖工具解析、循环休眠、加密存储、跨重启唤醒、WPF 光标火花及召唤框定位；真实 DeepSeek 合成图调用接受更新后的 14 种工具定义。尚未对真实 15 分钟视频任务进行全程等待实测。

v0.10.1 将定时器启动延迟到主窗口 `Loaded` 后，确保重启时已经到期的任务先完成 UI 事件绑定再唤醒；离线回放新增对这一时序的检查。

v0.11.0 增加 `create_folder`、`create_text_file` 和 `open_created_file` 三个全桌面 Agent 工具。创建文件只接受绝对本地路径和 HTML/CSS/JS/JSON/TXT/Markdown 文本，不覆盖现有内容；打开工具只接受本轮新建的路径。任务中明确写出的 HTML 文件名会被保留。VS Code、资源管理器和 Chrome 的新文件流程都经过真实桌面及云端循环测试，截图会在每步之后重新采集。浏览器预览任务只有本轮新建的指定文件已打开且浏览器位于前台时才能成功结束；模型错误工具参数会作为可恢复反馈进入下一轮。复杂任务的实际成功率仍取决于屏幕状态、云端模型和 Windows 焦点状态，UAC 交互尚未实测。

v0.12.0 增加按需加载的 `show_visualization` 与 `close_visualization` 工具。云端可提交 HTML 片段、CSS、JS、标题和面板大小；`AgentVisualizationDocument` 校验大小、禁止嵌套页面和外链标签，并拼装带 CSP 的内存文档。WPF 中央无边框面板用 WebView2 渲染，可用右上角或 Esc 关闭；Agent 的下一轮整桌面截图包含面板，供模型核对。WebView2 禁用宿主对象、网页消息、权限、下载、弹窗和外部导航，并拒绝网络资源请求；本机实测外部 `fetch` 被阻止。面板默认保持显示，也可设置 1～300 秒自动关闭。设置中可独立关闭 Agent 动画面板。`--visualization-smoke` 验证本地 HTML/JS 渲染与外部请求拦截；`--visualization-tool-smoke` 用合成截图验证真实云端函数调用；`--live-visualization-agent` 已完成实际云端调用、面板渲染、新截图回传与成功判断。此功能依赖单独安装的 Microsoft Edge WebView2 Runtime，未安装时面板会提示原因。

2026-10-31 本机短测：RapidOCR 热推理约 0.32～0.35 秒、进程峰值约 145～146 MB；320×320 GUI ONNX 热推理约 0.09～0.13 秒、进程峰值约 225 MB。12 秒真实增强模式采样中主进程约 82 MB、两个 Python 模型进程合计约 335～368 MB，总计约 **417～450 MB**。这不是长期峰值保证，CPU、内存随分辨率、显示器数、窗口内容和模型而变化。640×640 GUI ONNX 曾测约 441 MB，故默认选择 320×320。资源脚本输出单核相对 CPU 百分比和工作集；面板的 CPU 百分比按整机逻辑处理器数归一化。

独立 WPF 界面烟测已验证新版 EXE 能启动，并能通过 Ctrl+Alt+D 打开感知设置面板；测试后已恢复原安装版进程。该界面进程本身曾观察到约 214 MB 工作集（当时感知关闭），因此启用增强模式后整款应用可能超过 500 MB；上面的 417～450 MB 是感知测试宿主与模型进程合计值，不代表完整灵动岛的内存上限。

## 接口与代码位置

| 位置 | 职责 |
| --- | --- |
| `Perception/Contracts.cs` | `DesktopObservation`、截图/窗口/GUI/OCR/事件/评分结构，`IOcrEngine`、`IGuiDetector`、`ILocalClassifier`、`ICloudAgent` |
| `Perception/ScreenCapture.cs`, `ChangeDetector.cs`, `WindowsActivity.cs`, `UiAutomationReader.cs` | 采集和初筛 |
| `Perception/ObservationFusion.cs`, `StateMemory.cs`, `RuleClassifier.cs` | 空间关联、短期记忆和可解释规则 |
| `Perception/CloudCallHistory.cs`, `LingDongDao/CloudHistoryWindow.xaml` | 本机加密调用记录及截图、完整回复查看 |
| `Perception/AgentLoop.cs`, `AgentToolClient.cs`, `AgentFileTool.cs`, `AgentVisualization.cs`, `AgentMouseTool.cs`, `AgentBrowserTool.cs`, `AgentKeyboardTool.cs`, `DesktopAppTool.cs`, `DesktopWindowTool.cs`, `DesktopWindowInventory.cs`, `PerceptionService.Agent.cs` | 全桌面 Agent 循环、文件创建、HTML 动画文档与工具校验、输入工具、软件与窗口切换、受限网址输入和运行控制 |
| `LingDongDao/AgentVisualizationWindow.cs` | WebView2 中央动画面板、关闭与页面资源隔离 |
| `Perception/AgentSkillStore.cs`, `AgentWakeScheduler.cs` | 加密的成功步骤经验和单次计时唤醒 |
| `LingDongDao/AgentActionOverlay.cs`, `AgentGhostCursorOverlay.cs`, `AgentCommandWindow.cs` | 点击坐标浮层、蓝色光标和快捷指令输入框 |
| `LingDongDao/PerceptionDebugOverlay.cs` | OCR/GUI 识别框与坐标调试浮层，Windows 截图排除 |
| `LingDongDao/CurrentWeather.cs` | 当前温度、WMO 天气代码及更新时间解析 |
| `Perception/PythonRuntime.cs` | 源码及测试目录向上寻找本地 Python 和 GUI ONNX 运行时 |
| `Perception/CloudEscalationGate.cs`, `PrivacyAndCloud.cs` | 预算门控、隐私过滤、多模态 JSON 请求及 Mock |
| `Perception/AgentCredentialStore.cs` | Windows 当前用户 DPAPI 密钥存储 |
| `Perception/PerceptionService.cs` | 后台调度、日志、UI 事件、暂停和清空缓存 |
| `perception_py/ocr_worker.py`, `gui_worker.py` | JSON Lines 内存 IPC；运行时不保存截图 |
| `PerceptionTests/Program.cs` | 离线回放、Mock HTTP、真实采样及资源检查 |

## 当前边界

UIA 不支持的应用不会得到真实控件类型；增强模型也只能给出可交互区域。当前只识别有终端上下文和变化/交互证据的错误，其他事件仍需标注样本扩展规则或训练模型。任务级记忆只有粗略的活动类别，没有长期语义计划。隐私过滤是保守的工程防线，不能保证识别所有屏幕秘密；首次开启云端必须手动授权，建议在可信窗口和可信供应商下使用。文件工具只创建新文件，不修改已有文件；没有任意命令执行能力，消息发送仍需用户在任务中明确提出。
