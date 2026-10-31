using System.Threading;
using System.Windows;

namespace LingDongDao;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        System.Windows.Forms.Application.SetHighDpiMode(
            System.Windows.Forms.HighDpiMode.PerMonitorV2);
        _singleInstance = new Mutex(true, "Local\\LingDongDao.App", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow(e.Args.Contains("--preview", StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
