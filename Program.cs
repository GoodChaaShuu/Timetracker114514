using System.Threading;

namespace TimeTracker;

internal static class Program
{
    private const string MutexName = @"Local\TimeTracker.SingleInstance";
    private const string ShowEventName = @"Local\TimeTracker.ShowPanel";

    [STAThread]
    private static void Main(string[] args)
    {
        // 双开会让两套采集器写同一个库，时长直接翻倍，所以只允许一份。
        using var mutex = new Mutex(true, MutexName, out bool isFirst);

        // 第二个实例不弹窗，只把「显示面板」的信号发给已经在跑的那个
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            try { showSignal.Set(); } catch { /* 主实例可能正在退出 */ }
            return;
        }

        ApplicationConfiguration.Initialize();

        Options opt;
        SessionStore store;

        try
        {
            opt = Options.Parse(args);
            store = new SessionStore(opt.DbPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "TimeTracker 启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        using (store)
        {
            // 主题要在建任何窗口之前定下来，托盘图标和面板都跟着它画
            var settings = AppSettings.Load(opt.DbPath);
            Theme.SetDark(settings.Dark);

            // --panel：启动就把面板摊开（做快捷方式、开机自启时想要直接看到界面）
            bool showPanel = Array.IndexOf(args, "--panel") >= 0;
            Application.Run(new TrayApp(store, opt, settings, showPanel, showSignal));
        }
    }
}
