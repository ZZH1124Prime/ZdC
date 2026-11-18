using System.Threading;
using System.Windows;
namespace ZdC.App;
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\ZdC.ConfigEditor", out var firstInstance);
        if (!firstInstance)
        {
            PaperDialog.ShowMessage(null, "ZdC", "ZdC 已经运行，请切换到已有窗口。");
            _instance.Dispose(); _instance = null;
            Shutdown(); return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
