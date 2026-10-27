# 灵动岛

Windows 11 屏幕顶边的小型灵动岛。空闲时显示一条短线；有提醒时从短线展开，默认 3.5 秒后收起。

## 使用

从 [Releases 下载 Windows 独立版](https://github.com/Korloa/Lingdongdao-For-Windows/releases/latest)，运行 `LingDongDao.exe`，无需额外安装 .NET。按 **Ctrl+Alt+D** 打开控制面板，也可以右键点击任务栏通知区的灵动岛图标，选择“打开控制面板”。如快捷键被其他程序占用，可从托盘菜单打开面板并更换快捷键。点击顶边短线可预览充电提示。

控制面板提供三种显示模式：**自动切换**（默认，收到提醒时展开）、**保持展开**（持续显示充电样式预览）、**完全隐藏**（顶边短线也隐藏，仍可用快捷键或托盘打开面板）。外观可分别调节短线长宽、展开宽高、顶部距离、圆角、内容留白、图标和文字大小、背景透明度、动画速度与停留时间。每项都能拖动滑块，或在右侧输入精确数值并按回车；调整时即时预览，取消或关闭面板会恢复已保存的外观。默认展开高度为 **68 px**，允许调至 60–110 px；宽度允许调至 220–420 px。提醒开关、触发阈值、开机自动启动和全局快捷键也可在面板中设置。开机自动启动默认关闭，开启后在当前用户登录 Windows 时运行；移动程序文件后需要重新开启。点击“保存并应用”后重启仍会保留；“恢复默认”需再点击保存。配置文件位于 `%LOCALAPPDATA%\LingDongDao\settings.json`。

## 提醒

- 电源：接入电源时显示充电状态和电量；未接电且电量低于阈值时提醒，默认阈值为 20%。
- 剪贴板：复制文本时显示开头，复制图片或图片文件时显示圆形缩略图。内容只用于当前提示，不写入磁盘。
- Wi‑Fi：连接新网络时显示网络名称；链路速率连续两次低于阈值时提醒，默认 30 Mbps；已连接但连续两次无法访问网络时提醒。链路速率是网卡协商值，不是下载测速结果。
- 内存：占用率连续三次达到阈值时提醒，默认 85%；低于阈值 5 个百分点后可以再次提醒。
- 温度：CPU 或 GPU 温度连续两次达到阈值时提醒，默认分别为 90°C 和 88°C。温度来自 LibreHardwareMonitor；没有可用传感器时跳过。
- 郫都区雷雨：默认每 30 分钟查询一次成都市郫都区附近的未来 24 小时逐小时预报，出现雷雨天气代码时弹出提醒。同一时段运行期间不重复提醒；可在面板中关闭或调整查看范围（6–48 小时）。数据来自 Open-Meteo 天气预报，仅供参考，并非官方气象预警；查询失败时可在托盘“查看检测状态”中查看原因。

Wi‑Fi、内存、温度约每 10 秒检查一次，网络连通性约每 30 秒检查一次。托盘菜单可预览提醒、查看检测状态或退出程序。

## 源码运行与构建

需要 Windows 和 .NET 9 SDK：

```powershell
dotnet run --project .\LingDongDao\LingDongDao.csproj
```

生成独立可执行文件：

```powershell
dotnet publish .\LingDongDao\LingDongDao.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o .\dist
```

检测本机可用的 Wi‑Fi、网络、内存和温度数据：

```powershell
dotnet run --project .\Diagnostics\Diagnostics.csproj -c Release
```
