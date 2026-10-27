using Microsoft.Win32;
using System.Windows.Forms;
using System.Windows.Threading;

namespace LingDongDao;

internal readonly record struct PowerSnapshot(bool HasBattery, bool IsPluggedIn, int? Percent)
{
    public static PowerSnapshot Read()
    {
        var status = SystemInformation.PowerStatus;
        bool hasBattery = !status.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        int? percent = status.BatteryLifePercent is >= 0 and <= 1
            ? (int)Math.Round(status.BatteryLifePercent * 100)
            : null;
        return new PowerSnapshot(hasBattery, status.PowerLineStatus == PowerLineStatus.Online, percent);
    }
}

internal sealed class PowerMonitor : IDisposable
{
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private PowerSnapshot _last;
    private bool _lowBatteryNotified;
    private readonly Func<AppSettings> _settings;

    public event Action<PowerSnapshot>? PluggedIn;
    public event Action? Unplugged;
    public event Action<PowerSnapshot>? LowBattery;

    public PowerSnapshot Current => _last;

    public PowerMonitor(Func<AppSettings> settings)
    {
        _settings = settings;
        _last = PowerSnapshot.Read();
        _pollTimer.Tick += OnPoll;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _pollTimer.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // SystemEvents may call from a non-UI thread.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(Check);
    }

    private void OnPoll(object? sender, EventArgs e) => Check();

    private void Check()
    {
        PowerSnapshot next = PowerSnapshot.Read();
        bool wasCharging = _last.HasBattery && _last.IsPluggedIn;
        bool nowCharging = next.HasBattery && next.IsPluggedIn;
        _last = next;

        if (!wasCharging && nowCharging)
            PluggedIn?.Invoke(next);
        else if (wasCharging && !nowCharging)
            Unplugged?.Invoke();

        AppSettings settings = _settings();
        bool low = settings.BatteryLowEnabled && next.HasBattery && !next.IsPluggedIn &&
                   next.Percent is int percent && percent <= settings.BatteryLowPercent;
        if (low && !_lowBatteryNotified)
        {
            _lowBatteryNotified = true;
            LowBattery?.Invoke(next);
        }
        else if (!settings.BatteryLowEnabled || next.IsPluggedIn ||
                 next.Percent is int current && current > settings.BatteryLowPercent + 5)
        {
            _lowBatteryNotified = false;
        }
    }

    public void RefreshSettings()
    {
        _lowBatteryNotified = false;
        Check();
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
