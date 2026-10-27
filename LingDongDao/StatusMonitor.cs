using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace LingDongDao;

internal readonly record struct WifiSnapshot(string Ssid, int? LinkMbps);
internal readonly record struct TemperatureSample(string Component, double Celsius, double Limit);

internal sealed class StatusMonitor : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Func<AppSettings> _settings;
    private readonly TemperatureReader _temperature = new();
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(4)
    };
    private bool _busy;
    private bool _disposed;
    private Task<TemperatureSample?>? _temperatureTask;
    private bool _hasWifiBaseline;
    private string? _lastSsid;
    private int _memoryStreak;
    private int _heatStreak;
    private int _slowWifiStreak;
    private int _internetFailureStreak;
    private bool _memoryAlerted;
    private bool _heatAlerted;
    private bool _slowWifiAlerted;
    private bool _internetAlerted;
    private DateTime _lastInternetCheck = DateTime.MinValue;

    public event Action<IslandNotice>? Notice;
    public WifiSnapshot? CurrentWifi { get; private set; }
    public uint? CurrentMemoryPercent { get; private set; }
    public TemperatureSample? CurrentTemperature { get; private set; }
    public bool? InternetAvailable { get; private set; }
    public string? WifiError { get; private set; }

    public StatusMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => _ = CheckAsync();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _timer.Start();
        _ = CheckAsync();
    }

    public void RefreshSettings() => _ = CheckAsync(forceInternet: true);

    private void OnNetworkChanged(object? sender, EventArgs e) => RequestCheck();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RequestCheck();

    private void RequestCheck()
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => _ = CheckAsync(forceInternet: true)));
    }

    private async Task CheckAsync(bool forceInternet = false)
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            uint? memory = ReadMemoryPercent();
            CurrentMemoryPercent = memory;
            CheckMemory(memory);
            AppSettings settings = _settings();
            if (_temperatureTask is null)
                _temperatureTask = Task.Run(() => _temperature.Read(settings.CpuTemperatureC, settings.GpuTemperatureC));
            else if (_temperatureTask.IsCompleted)
            {
                CurrentTemperature = await _temperatureTask;
                CheckTemperature(CurrentTemperature);
                _temperatureTask = Task.Run(() => _temperature.Read(settings.CpuTemperatureC, settings.GpuTemperatureC));
            }

            WifiSnapshot? wifi = await ReadWifiAsync();
            if (_disposed) return;

            CurrentWifi = wifi;
            CheckWifi(wifi);

            if (wifi is not null &&
                (forceInternet || DateTime.UtcNow - _lastInternetCheck >= TimeSpan.FromSeconds(30)))
            {
                _lastInternetCheck = DateTime.UtcNow;
                bool available = await CheckInternetAsync();
                if (!_disposed) CheckInternet(available);
            }
            else if (wifi is null)
            {
                InternetAvailable = null;
                _internetFailureStreak = 0;
                _internetAlerted = false;
            }
        }
        catch
        {
            // A failed sensor or OS query must not close the island.
        }
        finally
        {
            _busy = false;
        }
    }

    private void CheckWifi(WifiSnapshot? wifi)
    {
        AppSettings settings = _settings();
        bool changed = wifi?.Ssid != _lastSsid;
        if (changed)
        {
            _slowWifiStreak = 0;
            _slowWifiAlerted = false;
            _internetFailureStreak = 0;
            _internetAlerted = false;
        }
        if (settings.WifiConnectedEnabled && _hasWifiBaseline && wifi is { } connected && changed)
            Notice?.Invoke(new IslandNotice(NoticeKind.WifiConnected, "Wi‑Fi 已连接", connected.Ssid));
        _hasWifiBaseline = true;
        _lastSsid = wifi?.Ssid;

        bool slow = settings.WifiSlowEnabled && wifi?.LinkMbps is int rate && rate < settings.WifiSlowMbps;
        _slowWifiStreak = slow ? _slowWifiStreak + 1 : 0;
        if (_slowWifiStreak >= 2 && !_slowWifiAlerted && wifi is { } current)
        {
            _slowWifiAlerted = true;
            Notice?.Invoke(new IslandNotice(NoticeKind.WifiSlow, "Wi‑Fi 速率偏低",
                $"{current.Ssid} · 链路 {current.LinkMbps} Mbps"));
        }
        if (!settings.WifiSlowEnabled || wifi is null ||
            wifi?.LinkMbps is int recovered && recovered >= settings.WifiSlowMbps + 15)
            _slowWifiAlerted = false;
    }

    private void CheckMemory(uint? percent)
    {
        AppSettings settings = _settings();
        bool high = settings.MemoryEnabled && percent is uint load && load >= settings.MemoryPercent;
        _memoryStreak = high ? _memoryStreak + 1 : 0;
        if (_memoryStreak >= 3 && !_memoryAlerted && percent is { } value)
        {
            _memoryAlerted = true;
            Notice?.Invoke(new IslandNotice(NoticeKind.MemoryHigh, "内存占用过高", $"当前已使用 {value}%"));
        }
        if (!settings.MemoryEnabled || percent is uint current && current < settings.MemoryPercent - 5)
            _memoryAlerted = false;
    }

    private void CheckTemperature(TemperatureSample? sample)
    {
        AppSettings settings = _settings();
        bool hot = settings.TemperatureEnabled && sample is { } current && current.Celsius >= current.Limit;
        _heatStreak = hot ? _heatStreak + 1 : 0;
        if (_heatStreak >= 2 && !_heatAlerted && sample is { } value)
        {
            _heatAlerted = true;
            Notice?.Invoke(new IslandNotice(NoticeKind.TemperatureHigh, "电脑温度过高",
                $"{value.Component} {value.Celsius:0}°C"));
        }
        if (!settings.TemperatureEnabled || sample is { } cool && cool.Celsius < cool.Limit - 7)
            _heatAlerted = false;
    }

    private void CheckInternet(bool available)
    {
        InternetAvailable = available;
        _internetFailureStreak = available ? 0 : _internetFailureStreak + 1;
        if (_settings().NoInternetEnabled && _internetFailureStreak >= 2 &&
            !_internetAlerted && CurrentWifi is { } wifi)
        {
            _internetAlerted = true;
            Notice?.Invoke(new IslandNotice(NoticeKind.NoInternet, "无法访问网络",
                $"已连接 {wifi.Ssid}，请检查网络"));
        }
        if (available || !_settings().NoInternetEnabled) _internetAlerted = false;
    }

    private async Task<WifiSnapshot?> ReadWifiAsync()
    {
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await process.WaitForExitAsync(timeout.Token);
            string output = await outputTask;
            if (process.ExitCode != 0)
            {
                WifiError = $"netsh 退出码 {process.ExitCode}";
                return null;
            }
            Match ssid = Regex.Match(output, @"(?m)^\s*SSID\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!ssid.Success)
            {
                WifiError = "未找到已连接的 SSID";
                return null;
            }
            Match rate = Regex.Match(output,
                @"(?mi)^\s*(?:Receive rate|接收速率).*?:\s*(\d+)");
            int? mbps = rate.Success && int.TryParse(rate.Groups[1].Value, out int parsed) ? parsed : null;
            WifiError = null;
            return new WifiSnapshot(ssid.Groups[1].Value.Trim(), mbps);
        }
        catch (Exception error)
        {
            WifiError = $"{error.GetType().Name}: {error.Message}";
            try { if (process is { HasExited: false }) process.Kill(); } catch { }
            return null;
        }
        finally { process?.Dispose(); }
    }

    private async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var response = await _http.GetAsync("http://www.msftconnecttest.com/connecttest.txt");
            if (response.IsSuccessStatusCode &&
                (await response.Content.ReadAsStringAsync()).Trim() == "Microsoft Connect Test")
                return true;
        }
        catch { }

        foreach (string url in new[] { "https://www.baidu.com/favicon.ico", "https://www.qq.com/favicon.ico" })
        {
            try
            {
                using var response = await _http.GetAsync(url);
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true)
                    return true;
            }
            catch { }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private static uint? ReadMemoryPercent()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad : null;
    }

    public string GetStatusSummary()
    {
        string wifi = CurrentWifi is { } connection
            ? $"Wi‑Fi：{connection.Ssid}，链路 {connection.LinkMbps?.ToString() ?? "未知"} Mbps"
            : $"Wi‑Fi：未连接{(WifiError is null ? "" : $"（{WifiError}）")}";
        string internet = InternetAvailable switch { true => "可访问", false => "不可访问", _ => "未检测" };
        string memory = CurrentMemoryPercent is { } load ? $"{load}%" : "不可用";
        string temperature = CurrentTemperature is { } sample
            ? $"{sample.Component} {sample.Celsius:0}°C"
            : "无可用传感器";
        return $"{wifi}\n网络：{internet}\n内存：{memory}\n温度：{temperature}";
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        if (_temperatureTask is null || _temperatureTask.IsCompleted)
            _temperature.Dispose();
        _http.Dispose();
    }
}

internal sealed class TemperatureReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true
    };
    private bool _opened;
    private bool _failed;

    public TemperatureSample? Read(int cpuLimit, int gpuLimit)
    {
        if (_failed) return null;
        try
        {
            if (!_opened)
            {
                _computer.Open();
                _opened = true;
            }

            TemperatureSample? hottest = null;
            foreach (IHardware hardware in _computer.Hardware)
            {
                bool cpu = hardware.HardwareType == HardwareType.Cpu;
                bool gpu = hardware.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;
                if (!cpu && !gpu) continue;
                hardware.Update();
                IEnumerable<ISensor> sensors = hardware.Sensors.Concat(hardware.SubHardware.SelectMany(sub =>
                {
                    sub.Update();
                    return sub.Sensors;
                }));
                foreach (ISensor sensor in sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || sensor.Value is not float value ||
                        value is < 20 or > 125) continue;
                    if (gpu && (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                                sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))) continue;
                    var sample = new TemperatureSample(cpu ? "CPU" : "GPU", value, cpu ? cpuLimit : gpuLimit);
                    if (hottest is null || sample.Celsius / sample.Limit > hottest.Value.Celsius / hottest.Value.Limit)
                        hottest = sample;
                }
            }
            return hottest;
        }
        catch
        {
            _failed = true;
            return null;
        }
    }

    public void Dispose()
    {
        if (_opened) _computer.Close();
    }
}
