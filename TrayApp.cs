using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TimeTracker;

/// <summary>
/// 采集器的启停包装。界面上那颗电源键就是调它的 Start / StopAsync。
    /// </summary>
public sealed class TrackingService
{
    private readonly SessionStore _store;
    private readonly Options _opt;

    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private Tracker? _tracker;

    public TrackingService(SessionStore store, Options opt)
    {
        _store = store;
        _opt = opt;
    }

    /// <summary>每次启停都会触发，可能在后台线程。</summary>
    public event Action? StateChanged;

    public bool IsRunning => _cts is not null;

    /// <summary>正在跑的采集器；停止后为 null。界面用它拿“当前应用 / 开始时间”。</summary>
    public Tracker? Current => _tracker;

    public void Start()
    {
        if (_cts is not null) return;

        var cts = new CancellationTokenSource();
        var tracker = new Tracker(_store, _opt);

        _cts = cts;
        _tracker = tracker;
        _loop = Task.Run(() => tracker.RunAsync(cts.Token));

        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var tracker = _tracker;
        if (cts is null) return;

        var loop = _loop;
        _cts = null;

        cts.Cancel();

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch
        {
            // 取消导致的异常无需上报
        }

        cts.Dispose();

        // 等待期间可能又被 Start 了，别把新采集器一起清掉。
        if (ReferenceEquals(_tracker, tracker)) _tracker = null;

        StateChanged?.Invoke();
    }

    public void StopAndWait()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // 退出路径上不抛
        }
    }
}

/// <summary>托盘图标、右键菜单、开机自启。</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly SessionStore _store;
    private readonly Options _opt;
    private readonly AppSettings _settings;
    private readonly TrackingService _tracking;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly MainForm _form;
    private readonly Control _marshal = new();

    private ToolStripMenuItem? _toggleItem;
    private ToolStripMenuItem? _autoItem;
    private SettingsForm? _settingsForm;
    private bool _exiting;

    public TrayApp(SessionStore store, Options opt, AppSettings settings, bool showPanel = false, EventWaitHandle? showSignal = null)
    {
        _store = store;
        _opt = opt;
        _settings = settings;
        _tracking = new TrackingService(store, opt);

        // 面板只创建一次，之后只是隐藏/显示，重新打开时不会丢滚动位置
        _form = new MainForm(store, _tracking, settings);
        _ = _marshal.Handle;   // 先建好句柄，后台线程才能用 BeginInvoke 回到 UI 线程

        _menu = new RoundedMenu
        {
            Renderer = new MenuRenderer(),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Padding = new Padding(6),
            Font = new Font(Theme.Family, 9.5f)
        };
        _menu.Opening += (_, _) => SyncMenu();

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "TimeTracker",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowPanel();
        };
        _tray.DoubleClick += (_, _) => ShowPanel();

        BuildMenu();

        // 开机就开录：托盘程序没人会每天手动点一下。
        _tracking.Start();

        if (showPanel) ShowPanel();

        if (showSignal is not null) StartSignalWatcher(showSignal);

#if DEBUG
        StartDemoHooks();
#endif
    }

#if DEBUG
    /// <summary>开发时用来截图验证菜单和设置窗口，Release 构建里不存在。</summary>
    private void StartDemoHooks()
    {
        var argv = Environment.GetCommandLineArgs();
        bool menu = Array.IndexOf(argv, "--demo-menu") >= 0;
        bool settings = Array.IndexOf(argv, "--demo-settings") >= 0;
        if (!menu && !settings) return;

        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            if (menu) _menu.Show(Cursor.Position);
            else OpenSettings();
        };
        timer.Start();
    }
#endif

    /// <summary>再双击一次 exe（或快捷方式）时，把已经开着的面板拉到前面来。</summary>
    private void StartSignalWatcher(EventWaitHandle showSignal)
    {
        var thread = new Thread(() =>
        {
            while (!_exiting)
            {
                if (!showSignal.WaitOne(400)) continue;
                if (_exiting) return;

                try { _marshal.BeginInvoke(new Action(ShowPanel)); }
                catch (InvalidOperationException) { return; }   // 句柄没了 = 正在退出。
            }
        })
        {
            IsBackground = true,
            Name = "TimeTracker.ShowPanelWatcher"
        };

        thread.Start();
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        _menu.Items.Add(Item("打开面板", ShowPanel));
        _menu.Items.Add(Separator());

        _toggleItem = Item("开始记录", () =>
        {
            if (_tracking.IsRunning) _ = _tracking.StopAsync();
            else _tracking.Start();
        });
        _menu.Items.Add(_toggleItem);

        _autoItem = Item("开机自动启动", null);
        _autoItem.Click += (_, _) =>
        {
            AutoStart.Enabled = !AutoStart.Enabled;
            SyncMenu();
        };
        _menu.Items.Add(_autoItem);

        _menu.Items.Add(Item("设置", OpenSettings));

        _menu.Items.Add(Separator());
        _menu.Items.Add(Item("退出", ExitApp));

        SyncMenu();
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, ApplySettings)
        {
            TopMost = _form.TopMost
        };

        _settingsForm.Show();
    }

    /// <summary>设置里改了主题/应用数/置顶之后，把主面板和托盘菜单一起刷一遍。</summary>
    private void ApplySettings()
    {
        _form.ApplySettings();
        ApplyMenuTheme();

        if (_settingsForm is { IsDisposed: false }) _settingsForm.TopMost = _form.TopMost;
    }

    private void ApplyMenuTheme()
    {
        _menu.BackColor = Theme.Surface;
        _menu.ForeColor = Theme.Text;

        foreach (ToolStripItem item in _menu.Items)
            item.ForeColor = Theme.Text;
    }

    /// <summary>菜单每次弹出前把勾选状态和启停文案刷新成最新的。</summary>
    private void SyncMenu()
    {
        if (_toggleItem is not null) _toggleItem.Text = _tracking.IsRunning ? "停止记录" : "开始记录";
        if (_autoItem is not null) _autoItem.Checked = AutoStart.Enabled;
    }

    private static ToolStripMenuItem Item(string text, Action? onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = Theme.Text,
            Padding = new Padding(2, 4, 2, 4)
        };

        if (onClick is not null) item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>菜单里的分组线：留点上下间距，线本身由 MenuRenderer 画。</summary>
    private static ToolStripSeparator Separator() => new()
    {
        Margin = new Padding(0, 5, 0, 5)
    };

    private void ShowPanel()
    {
        if (_form.IsDisposed) return;

        if (!_form.Visible) _form.Show();

        if (_form.WindowState == FormWindowState.Minimized)
            _form.WindowState = FormWindowState.Normal;

        _form.Activate();
        _form.BringToFront();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        _tracking.StopAndWait();

        if (_settingsForm is { IsDisposed: false }) _settingsForm.Dispose();
        if (!_form.IsDisposed) _form.Dispose();

        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        _marshal.Dispose();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exiting = true;
            _tray.Dispose();
            _menu.Dispose();
            _marshal.Dispose();
            if (!_form.IsDisposed) _form.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>自绘菜单：圆角悬停块、跟随主题的文字与分隔线。</summary>
    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors())
        {
            RoundedEdges = true;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Theme.Surface);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.ToolStrip.Size));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed) return;

            var r = new Rectangle(3, 1, Math.Max(0, e.Item.Width - 6), Math.Max(0, e.Item.Height - 2));

            using var path = Theme.RoundRect(r, 7f);
            using var brush = new SolidBrush(Theme.MenuHover);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // 系统自带的勾是浅蓝方块，放在深色菜单里很扎眼，自己画一个细细的对勾
            var r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;

            using var pen = new Pen(Theme.Accent, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.30f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.45f, r.Top + r.Height * 0.67f),
                new PointF(r.Left + r.Width * 0.72f, r.Top + r.Height * 0.34f)
            });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var b = e.Item.Bounds;
            int y = Math.Max(1, b.Height / 2);

            using var pen = new Pen(Theme.A(Theme.Muted, 0.5f));
            // 这里的 Graphics 已经平移到条目原点，用局部坐标才画得出来
            e.Graphics.DrawLine(pen, 4, y, Math.Max(6, b.Width - 4), y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // 不画左边那道灰条，极简一点。
        }

        private sealed class MenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Surface;
            public override Color ImageMarginGradientBegin => Theme.Surface;
            public override Color ImageMarginGradientMiddle => Theme.Surface;
            public override Color ImageMarginGradientEnd => Theme.Surface;
            public override Color MenuBorder => Theme.Border;
            public override Color MenuItemBorder => Theme.MenuHover;
            public override Color MenuItemSelected => Theme.MenuHover;
            public override Color MenuItemSelectedGradientBegin => Theme.MenuHover;
            public override Color MenuItemSelectedGradientEnd => Theme.MenuHover;
            public override Color SeparatorDark => Theme.Border;
            public override Color SeparatorLight => Theme.Border;
        }
    }
}

/// <summary>圆角的右键菜单。原生 ToolStripDropDown 是直角的，只能自己裁一下。</summary>
internal sealed class RoundedMenu : ContextMenuStrip
{
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;

        using var path = Theme.RoundRect(new RectangleF(0f, 0f, Width, Height), 10f);

        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }
}

/// <summary>运行时画出来的托盘图标，免得往仓库里塞 .ico 二进制。</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var bg = new SolidBrush(Theme.Accent))
                g.FillEllipse(bg, 1f, 1f, 30f, 30f);

            using var pen = new Pen(Theme.Background, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 9f, 9f, 14f, 14f, -55f, 290f);
            g.DrawLine(pen, 16f, 8f, 16f, 15f);
        }

        IntPtr handle = bmp.GetHicon();

        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>写入 HKCU 的 Run 键实现开机自启，不需要管理员权限。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TimeTracker";

    public static bool Enabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (key is null) return;

                if (value) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(ValueName, false);
            }
            catch
            {
                // 组策略禁用时写不进去，忽略即可。
            }
        }
    }
}
