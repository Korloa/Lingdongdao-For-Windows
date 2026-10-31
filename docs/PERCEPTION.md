# 桌面主动感知模块

本模块接入现有 LingDongDao WPF 程序的设置面板和灵动岛提醒。默认关闭。本地截图、Windows 事件、UI Automation、离线 OCR、可选 GUI 检测、状态记忆与规则评分均不访问付费 API。只有用户在面板中明确授权截图上传、配置多模态接口，并且本地门控通过后，才可能发出云端请求。云端只返回建议，不执行电脑操作。

## 已实现的数据流

`ScreenCapture` 每 2 秒默认采集所有显示器，JPEG 存于受时间及字节上限约束的 `FrameRingBuffer`，不写入磁盘。`WindowsActivity` 用前台窗口 API 和原生 hook 获取进程、标题、坐标、点击、滚轮、输入活动、窗口与焦点变化；键盘 hook 不读取或保存字符。`ChangeDetector` 把画面缩到 480 像素宽，做灰度分块差分，输出整屏差异、局部变化区域及窗口切换等原因。仅首次画面、窗口切换、点击、局部/全局变化或 30 秒兜底扫描会进入 UIA/OCR。单纯滚动且画面不变不会重复执行 OCR。

项目配置及启动代码启用 PerMonitorV2 DPI 感知，使 Win32 窗口、鼠标、UIA 和截图尽量处于同一物理像素坐标系；混合缩放多显示器仍需在目标机器实测。

`UiAutomationReader` 读取有界深度的控件树；密码控件只保留边界框。OCR 优先针对变化区域；窗口切换或周期扫描回退到前台窗口。`ObservationFusion` 关联 OCR、控件与点击，区分观测事实和推断事件。`StateMemory` 在内存里维护最近活动、任务类别、同一错误的持续次数和帮助冷却。`RuleClassifier` 输出活动、事件、重要性、介入分、置信度与理由；分数是**未训练、未校准的启发式调度分**，不是模型概率。`ILocalClassifier` 是后续替换为 fastText/ONNX 分类器的接口；目前没有标注样本，因此没有训练模型。

`CloudEscalationGate` 检查阈值（默认 0.8）、相似事件 15 分钟去重、冷却时间、每小时调用数、每日预估费用预算、敏感窗口及上传授权。`PrivacyFilter` 只裁剪当前活动窗口，遮挡 UIA 密码字段；识别到密钥样式文字、敏感窗口或 UIA 信息缺失时拒绝上传。前一帧仅在窗口句柄相同且当前没有密码字段时纳入。云端包保留 JPEG 原始视觉信息，同时附 OCR/GUI 坐标、交互、记忆及评分。`ConfigurableCloudAgent` 使用可配置的兼容 Chat Completions 多模态接口；密钥只从环境变量读取，不写入日志。云端不可用时本地循环继续运行。

## 运行与依赖

需要 Windows 10/11、.NET 9 SDK；离线 OCR 需要 Python 3.13（目前验证版本）。从仓库根目录运行：

```powershell
.\scripts\setup-perception.ps1
dotnet run --project .\LingDongDao\LingDongDao.csproj -c Release
```

若 Python 不在 `PATH`，运行 `.\scripts\setup-perception.ps1 -Python 'C:\path\python.exe'`。脚本使用官方 PyPI 创建项目内 `.venv`，安装 `perception_py/requirements.txt`。在设置面板“桌面感知”中开启感知、暂停/恢复、关闭云端上传、切换资源模式、调整采样间隔和阈值；面板展示当前软件、事件、评分、调用次数、进程合计内存、CPU 与黑底结构化日志。虚拟环境、模型、报告、截图均不加入 Git。

默认 **低负载** 模式为窗口 API、UIA、按变化/兜底运行 OCR、规则评分；**标准** 模式更频繁运行 OCR；**增强** 模式增加按需 GUI 检测。GUI 检测须额外执行：

```powershell
.\scripts\setup-perception.ps1 -WithGui
```

该步骤下载官方权重、核对 SHA-256，再使用 Ultralytics 开发依赖导出 320×320 ONNX。模型与导出依赖体积较大，因此不属于默认安装。已有 ONNX 可通过环境变量 `LINGDONGDAO_GUI_MODEL` 指定。若从发布版 EXE 运行，仍须将 Python 环境和 `perception_py` 脚本准备在程序目录旁，或设置 `LINGDONGDAO_PYTHON` 为 Python 解释器绝对路径；仓库源码运行会自动找到项目 `.venv`。本版安装包的 OCR/GUI Python 运行时**尚未打包**，不能把它当成免配置的完整感知安装包。

### 云端配置

先配置**支持图片+文字输入**且兼容 Chat Completions 的端点，再在面板里单独确认上传授权：

```powershell
$env:LINGDONGDAO_AGENT_BASE_URL = 'https://example.invalid/v1/chat/completions'
$env:LINGDONGDAO_AGENT_MODEL = 'your-vision-model'
$env:LINGDONGDAO_AGENT_API_KEY = 'set-in-your-shell-or-secure-user-env'
```

`BASE_URL` 是完整请求端点。请在私有系统环境或安全配置里设置真实密钥，不要写入源码、提交记录或截图。环境变量在程序启动时读取，修改后重启程序。默认每小时最多 3 次、每日预估费用上限 0.25 美元、每次预估 0.02 美元、冷却 300 秒；这些是保守的本地估算，**不是供应商真实账单**。当前设置面板仅暴露阈值、采样和开关，更多限额可编辑 `%LOCALAPPDATA%\LingDongDao\settings.json` 的 `Perception` 对象。

云端端点只接受 HTTPS，或本机回环地址的 HTTP Mock 服务。真实供应商仍需使用其支持图片输入的兼容接口，并在目标供应商上单独验证。

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
.\scripts\measure-perception.ps1
.\scripts\measure-perception.ps1 -Enhanced
```

离线回放覆盖正常浏览、Chrome→VS Code、单行局部变化、首次报错和重复去重、滚动抑制、GUI/OCR 点击关联、截图云端包、未授权/敏感窗口/密钥/预算拦截、云端失效及真实 HTTP 序列化的 Mock 响应。`--live-smoke` 读取当前屏幕；资源脚本会真实采样桌面并在 `reports/` 写 JSON，**不会写截图**。请在不含敏感内容的桌面运行实测。Mock 接口只验证请求结构和图片传输；没有真实 API 凭据，因此真实供应商请求尚未验证。

2026-10-31 本机短测：RapidOCR 热推理约 0.32～0.35 秒、进程峰值约 145～146 MB；320×320 GUI ONNX 热推理约 0.09～0.13 秒、进程峰值约 225 MB。12 秒真实增强模式采样中主进程约 82 MB、两个 Python 模型进程合计约 335～368 MB，总计约 **417～450 MB**。这不是长期峰值保证，CPU、内存随分辨率、显示器数、窗口内容和模型而变化。640×640 GUI ONNX 曾测约 441 MB，故默认选择 320×320。资源脚本输出单核相对 CPU 百分比和工作集；面板的 CPU 百分比按整机逻辑处理器数归一化。

独立 WPF 界面烟测已验证新版 EXE 能启动，并能通过 Ctrl+Alt+D 打开感知设置面板；测试后已恢复原安装版进程。该界面进程本身曾观察到约 214 MB 工作集（当时感知关闭），因此启用增强模式后整款应用可能超过 500 MB；上面的 417～450 MB 是感知测试宿主与模型进程合计值，不代表完整灵动岛的内存上限。

## 接口与代码位置

| 位置 | 职责 |
| --- | --- |
| `Perception/Contracts.cs` | `DesktopObservation`、截图/窗口/GUI/OCR/事件/评分结构，`IOcrEngine`、`IGuiDetector`、`ILocalClassifier`、`ICloudAgent` |
| `Perception/ScreenCapture.cs`, `ChangeDetector.cs`, `WindowsActivity.cs`, `UiAutomationReader.cs` | 采集和初筛 |
| `Perception/ObservationFusion.cs`, `StateMemory.cs`, `RuleClassifier.cs` | 空间关联、短期记忆和可解释规则 |
| `Perception/CloudEscalationGate.cs`, `PrivacyAndCloud.cs` | 预算门控、隐私过滤、多模态 JSON 请求及 Mock |
| `Perception/PerceptionService.cs` | 后台调度、日志、UI 事件、暂停和清空缓存 |
| `perception_py/ocr_worker.py`, `gui_worker.py` | JSON Lines 内存 IPC；运行时不保存截图 |
| `PerceptionTests/Program.cs` | 离线回放、Mock HTTP、真实采样及资源检查 |

## 当前边界

UIA 不支持的应用不会得到真实控件类型；增强模型也只能给出可交互区域。当前只识别有终端上下文和变化/交互证据的错误，其他事件仍需标注样本扩展规则或训练模型。任务级记忆只有粗略的活动类别，没有长期语义计划。隐私过滤是保守的工程防线，不能保证识别所有屏幕秘密；首次开启云端必须手动授权，建议在可信窗口和可信供应商下使用。没有自动文件修改、命令执行或消息发送能力。
