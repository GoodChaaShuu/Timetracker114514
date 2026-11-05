using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TimeTracker;

/// <summary>极简风格的全部配色与绘制小工具。</summary>
internal static class Theme
{
    public static bool Dark { get; private set; } = true;

    public static Color Background { get; private set; } = Color.FromArgb(14, 16, 19);
    public static Color Surface { get; private set; } = Color.FromArgb(24, 27, 33);
    public static Color Border { get; private set; } = Color.FromArgb(34, 38, 46);
    public static Color Text { get; private set; } = Color.FromArgb(231, 234, 240);
    public static Color Muted { get; private set; } = Color.FromArgb(120, 128, 142);
    public static Color Accent { get; private set; } = Color.FromArgb(52, 211, 153);
    public static Color Idle { get; private set; } = Color.FromArgb(94, 102, 116);
    public static Color MenuHover { get; private set; } = Color.FromArgb(38, 44, 52);
    public static Color MenuLine { get; private set; } = Color.FromArgb(38, 43, 51);

    /// <summary>悬停高亮往哪个方向偏：深色面板提亮，浅色面板压暗。</summary>
    public static Color HoverToward => Dark ? Color.White : Color.Black;

    /// <summary>
    /// 切换白天/夜间。所有绘制都实时读上面这些属性，改完重绘一次就生效，
    /// 不用重建窗口，也不用重启。
    /// </summary>
    public static void SetDark(bool dark)
    {
        Dark = dark;

        if (dark)
        {
            Background = Color.FromArgb(14, 16, 19);
            Surface = Color.FromArgb(24, 27, 33);
            Border = Color.FromArgb(34, 38, 46);
            Text = Color.FromArgb(231, 234, 240);
            Muted = Color.FromArgb(120, 128, 142);
            Accent = Color.FromArgb(52, 211, 153);
            Idle = Color.FromArgb(94, 102, 116);
            MenuHover = Color.FromArgb(38, 44, 52);
            MenuLine = Color.FromArgb(38, 43, 51);
        }
        else
        {
            Background = Color.FromArgb(247, 248, 250);
            Surface = Color.FromArgb(232, 235, 240);
            Border = Color.FromArgb(215, 220, 227);
            Text = Color.FromArgb(18, 21, 26);
            Muted = Color.FromArgb(107, 115, 129);
            Accent = Color.FromArgb(5, 150, 105);
            Idle = Color.FromArgb(163, 169, 180);
            MenuHover = Color.FromArgb(226, 230, 236);
            MenuLine = Color.FromArgb(220, 224, 231);
        }
    }

    public const string Family = "Microsoft YaHei UI";

    public static Color A(Color c, float alpha)
    {
        int a = (int)(Math.Clamp(alpha, 0f, 1f) * 255f + 0.5f);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180f, 90f);
        path.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        path.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>0→1 的缓出曲线，让电源键的移动像“滑过去”而不是“跳过去”。</summary>
    public static float Ease(float t) => 1f - MathF.Pow(1f - Math.Clamp(t, 0f, 1f), 3f);

    /// <summary>把颜色朝目标色混合，用来做悬停高亮。</summary>
    public static Color Mix(Color a, Color b, float t)
    {
        float k = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Lerp(a.A, b.A, k),
            (int)Lerp(a.R, b.R, k),
            (int)Lerp(a.G, b.G, k),
            (int)Lerp(a.B, b.B, k));
    }
}

/// <summary>
/// 主面板：无边框、自绘。
/// 未开始 → 屏幕中央一个大电源键；
/// 开始后 → 电源键平移缩小到右上角，腾出位置列应用。
/// </summary>
public sealed class MainForm : Form
{
    private const int BaseWidth = 420;
    private const int BaseHeight = 600;

    private readonly SessionStore _store;
    private readonly TrackingService _tracking;
    private readonly AppSettings _settings;

    private readonly System.Windows.Forms.Timer _animTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _dataTimer = new() { Interval = 1500 };

    private readonly Font _fHint = new(Theme.Family, 10f);
    private readonly Font _fName = new(Theme.Family, 10.5f);
    private readonly Font _fTime = new(Theme.Family, 10f);
    private readonly Font _fLabel = new(Theme.Family, 9f);
    private readonly Font _fTotal = new(Theme.Family, 17f, FontStyle.Bold);
    private readonly Font _fTile = new(Theme.Family, 8f, FontStyle.Bold);

    private IReadOnlyList<AppTotal> _rows = Array.Empty<AppTotal>();
    private long _totalMs;
    private long _idleMs;
    private DateTime _dayStart = DateTime.Today;
    private int _scroll;

    private float _anim;       // 0 = 大按钮，1 = 右上角小按钮
    private float _target;

    private bool _powerHover;
    private bool _powerDown;
    private bool _pinHover;
    private bool _busy;
    private bool _dragArmed;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;

    // 边框拖拽改尺寸
    private enum Edge { None, N, S, E, W, NE, NW, SE, SW }

    private Edge _resizeEdge;
    private Edge _hoverEdge;
    private Point _resizeCursor;
    private Rectangle _resizeRect;
    private bool _sizeLocked;   // 用户手动拖过大小之后，就不要再自动改高度了

    public MainForm(SessionStore store, TrackingService tracking, AppSettings settings)
    {
        _store = store;
        _tracking = tracking;
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Theme.Background;
        Text = "TimeTracker";
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(BaseWidth, BaseHeight);
        MinimumSize = new Size(320, 300);
        TopMost = settings.TopMost;

        _animTimer.Tick += (_, _) => OnAnimTick();
        _pulseTimer.Tick += (_, _) => { if (_tracking.IsRunning) Invalidate(); };
        _dataTimer.Tick += (_, _) => RefreshData();

        _tracking.StateChanged += OnTrackingStateChanged;

        SyncState();
        RefreshData();
    }

    /// <summary>当前 DPI 相对 96 的缩放，布局全部乘它，字体交给 GDI+ 自动处理。</summary>
    private float S => DeviceDpi / 96f;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    // ── 状态 ────────────────────────────────────────────────

    private void OnTrackingStateChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(new Action(SyncState));
            else SyncState();
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁，忽略
        }
    }

    private void SyncState()
    {
        float target = _tracking.IsRunning ? 1f : 0f;
        _target = target;
        _animTimer.Start();
        _pulseTimer.Enabled = _tracking.IsRunning;
        RefreshData();
        Invalidate();
    }

    private void OnAnimTick()
    {
        float diff = _target - _anim;
        _anim += diff * 0.22f;

        bool animDone = Math.Abs(diff) < 0.003f;
        if (animDone) _anim = _target;

        // 面板高度跟着内容走：应用少的时候不留一大片空白，应用多了最多长到屏幕内。
        float wantH = DesiredHeight(S);
        float nextH = Height + (wantH - Height) * 0.22f;
        bool sizeDone = Math.Abs(wantH - nextH) < 0.8f;
        if (sizeDone) nextH = wantH;

        int rounded = (int)Math.Round(nextH);
        if (rounded != Height && rounded > 0) Height = rounded;

        if (animDone && sizeDone) _animTimer.Stop();

        Invalidate();
    }

    private float DesiredHeight(float s)
    {
        // 用户手动拖过边角就尊重他的选择，不再自动收放
        if (_sizeLocked) return Height;

        if (!_tracking.IsRunning) return BaseHeight * s;

        int rows = Math.Clamp(_rows.Count, 1, 14);
        float h = 112f * s + rows * 52f * s + 22f * s;
        return Math.Clamp(h, 260f * s, 780f * s);
    }

    /// <summary>设置面板改完之后由外部调一下：切主题、改应用数、改置顶都走这里。</summary>
    public void ApplySettings()
    {
        Theme.SetDark(_settings.Dark);
        BackColor = Theme.Background;
        TopMost = _settings.TopMost;

        RefreshData();
        Invalidate();
    }

    private async void TogglePower()
    {
        if (_busy) return;
        _busy = true;

        Dbg($"toggle enter running={_tracking.IsRunning}");
        try
        {
            if (_tracking.IsRunning) await _tracking.StopAsync();
            else _tracking.Start();
        }
        catch
        {
            // 起停失败不该让界面崩掉
        }
        finally
        {
            _busy = false;
        }

        SyncState();
    }

    private void RefreshData()
    {
        try
        {
            var now = DateTime.Now;
            if (now.Date != _dayStart.Date)
            {
                _dayStart = now.Date;
                _scroll = 0;
            }

            var from = _dayStart.ToUniversalTime();
            var to = now.ToUniversalTime();

            var tracker = _tracking.Current;
            long excludeId = tracker?.CurrentSessionId ?? 0;

            var list = new List<AppTotal>(_store.Totals(from, to, excludeId));

            // 正在进行的这一段还没写进库里（或只写了上一次 Touch 的旧值），单独补上。
            if (tracker?.CurrentExe is string exe)
            {
                var start = tracker.CurrentSinceUtc < from ? from : tracker.CurrentSinceUtc;
                long ms = (long)Math.Max(0, (to - start).TotalMilliseconds);

                int idx = list.FindIndex(r => r.ExeName == exe);
                if (idx >= 0) list[idx] = list[idx] with { Milliseconds = list[idx].Milliseconds + ms };
                else list.Add(new AppTotal(exe, ms, 1, tracker.CurrentExePath));
            }

            var rows = list.Where(r => r.Milliseconds > 0)
                           .OrderByDescending(r => r.Milliseconds)
                           .ToList();

            _totalMs = rows.Sum(r => r.Milliseconds);

            // 超过「显示应用数」的尾巴合并成「其他」，免得列表无限长
            _rows = ApplyLimit(rows);

            // 挂机时长：库里的 + 当前正在进行的那一段
            long idle = _store.IdleTotal(from, to);
            if (tracker is not null && tracker.IdleSinceUtc != DateTime.MinValue)
            {
                var idleStart = tracker.IdleSinceUtc < from ? from : tracker.IdleSinceUtc;
                idle += (long)Math.Max(0, (to - idleStart).TotalMilliseconds);
            }

            _idleMs = idle;

            // 行数变了就让它滑到新高度
            if (Visible && Math.Abs(DesiredHeight(S) - Height) > 1f) _animTimer.Start();

            Invalidate();
        }
        catch
        {
            // 统计查询失败不影响采集，界面保留上一帧
        }
    }

    private const string OtherName = "其他";

    private IReadOnlyList<AppTotal> ApplyLimit(List<AppTotal> rows)
    {
        int max = _settings.VisibleApps;
        if (max <= 0 || rows.Count <= max) return rows;

        long restMs = 0;
        long restCount = 0;

        for (int i = max; i < rows.Count; i++)
        {
            restMs += rows[i].Milliseconds;
            restCount += rows[i].SessionCount;
        }

        var trimmed = rows.Take(max).ToList();

        // 只有一条尾巴时没必要单独弄个「其他」行，读起来反而别扭
        if (rows.Count - max == 1) trimmed.Add(rows[max]);
        else trimmed.Add(new AppTotal(OtherName, restMs, restCount));

        return trimmed;
    }

    // ── 布局 ────────────────────────────────────────────────

    private RectangleF PowerRect(float t)
    {
        float s = S;
        float d = Theme.Lerp(132f * s, 54f * s, t);
        float cx = Theme.Lerp(Width / 2f, Width - 28f * s - 27f * s, t);

        // 停止态：把「大按钮 + 提示文字」当成一组，居中在标题下方，上下留白对称
        float groupH = d + 44f * s;
        float top = 104f * s;
        float bottom = Height - 32f * s;
        float stopCy = top + Math.Max(0f, (bottom - top - groupH) / 2f) + d / 2f;

        float runCy = 26f * s + 27f * s;
        float cy = Theme.Lerp(stopCy, runCy, t);

        return new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
    }

    // ── 绘制 ────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = S;
        float t = Theme.Ease(_anim);

        using (var bg = new SolidBrush(Theme.Background))
            g.FillRectangle(bg, ClientRectangle);

        using (var frame = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 16f * s))
        using (var pen = new Pen(Theme.Border))
            g.DrawPath(pen, frame);

        DrawHeader(g, t, s);
        DrawPin(g, s);
        DrawList(g, t, s);
        DrawPower(g, t, s);
        DrawHint(g, t, s);
    }

    private void DrawHeader(Graphics g, float t, float s)
    {
        // 没开始记录时也保留今日总时长，只是压暗，让大电源键仍然是视觉主角
        float a = 0.6f + 0.4f * t;

        using var label = new SolidBrush(Theme.A(Theme.Muted, a));
        using var value = new SolidBrush(Theme.A(Theme.Text, a));

        g.DrawString("今天", _fLabel, label, 28f * s, 40f * s);
        g.DrawString(Format.Duration(_totalMs), _fTotal, value, 27f * s, 54f * s);

        // 挂机时长单独一行，和「真正在用电脑」的时间区分开
        if (_idleMs > 0)
            g.DrawString("挂机 " + Format.Duration(_idleMs), _fLabel, label, 28f * s, 88f * s);
    }

    private RectangleF PinRect(float s) => new(22f * s, 12f * s, 18f * s, 18f * s);

    private void DrawPin(Graphics g, float s)
    {
        var r = PinRect(s);

        Color color = _settings.TopMost ? Theme.Accent : Theme.A(Theme.Muted, 0.9f);
        if (_pinHover) color = Theme.Mix(color, Theme.HoverToward, 0.25f);

        float cx = r.X + r.Width / 2f;

        using var brush = new SolidBrush(color);

        // 图钉：帽 + 上宽下窄的身子 + 一根针
        using (var cap = Theme.RoundRect(new RectangleF(cx - 6.4f * s, r.Y + 1f * s, 12.8f * s, 3.4f * s), 1.7f * s))
            g.FillPath(brush, cap);

        using (var body = new GraphicsPath())
        {
            body.AddPolygon(new[]
            {
                new PointF(cx - 4.6f * s, r.Y + 4.4f * s),
                new PointF(cx + 4.6f * s, r.Y + 4.4f * s),
                new PointF(cx + 2.1f * s, r.Y + 11.2f * s),
                new PointF(cx - 2.1f * s, r.Y + 11.2f * s)
            });
            g.FillPath(brush, body);
        }

        using var pen = new Pen(color, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx, r.Y + 10.8f * s, cx, r.Y + 16.4f * s);
    }

    private void DrawList(Graphics g, float t, float s)
    {
        if (t < 0.04f) return;

        float top = 112f * s;
        float rowH = 52f * s;
        float left = 28f * s;
        float right = Width - 28f * s;
        float bottom = Height - 16f * s;

        int visible = Math.Max(1, (int)((bottom - top) / rowH));
        int maxScroll = Math.Max(0, _rows.Count - visible);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        if (_rows.Count == 0)
        {
            using var empty = new SolidBrush(Theme.A(Theme.Muted, t * 0.9f));
            g.DrawString("还没有记录", _fName, empty, left, top + 4f * s);
            return;
        }

        long max = _rows.Max(r => r.Milliseconds);
        float iconSize = 20f * s;

        for (int i = 0; i < visible && _scroll + i < _rows.Count; i++)
        {
            var row = _rows[_scroll + i];
            float y = top + i * rowH;

            // 图标：真实 exe 图标取不到（被删了/路径是空的）就用首字母色块顶上
            var iconRect = new RectangleF(left, y + 1f * s, iconSize, iconSize);
            var icon = row.ExeName == OtherName ? null : AppIcons.For(row.ExeName, row.ExePath);

            if (icon is not null)
            {
                AppIcons.Draw(g, icon, iconRect);
            }
            else
            {
                DrawTile(g, iconRect, row.ExeName == OtherName ? "…" : Letter(row.ExeName), t, s);
            }

            float nameX = left + iconSize + 9f * s;

            using var nameBrush = new SolidBrush(Theme.A(Theme.Text, t));
            using var timeBrush = new SolidBrush(Theme.A(Theme.Muted, t));

            string name = Format.Trim(row.ExeName, 30);
            g.DrawString(name, _fName, nameBrush, nameX, y);

            string time = Format.Duration(row.Milliseconds);
            var size = g.MeasureString(time, _fTime);
            g.DrawString(time, _fTime, timeBrush, right - size.Width, y + 1f * s);

            // 一条发丝般的进度条，替代百分比数字，省掉视觉噪音
            float barY = y + 30f * s;
            float barH = 3f * s;
            float barW = right - left;

            using (var track = new SolidBrush(Theme.A(Theme.Surface, t)))
                g.FillRectangle(track, left, barY, barW, barH);

            float ratio = max <= 0 ? 0f : (float)row.Milliseconds / max;
            float fillW = Math.Max(barH, barW * ratio);

            using (var fill = new SolidBrush(Theme.A(Theme.Accent, t * 0.8f)))
                g.FillRectangle(fill, left, barY, fillW, barH);
        }
    }

    private static string Letter(string exe)
    {
        var name = exe;
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    }

    private void DrawTile(Graphics g, RectangleF r, string label, float t, float s)
    {
        using (var path = Theme.RoundRect(r, 5f * s))
        using (var fill = new SolidBrush(Theme.A(Theme.Surface, t)))
            g.FillPath(fill, path);

        using var brush = new SolidBrush(Theme.A(Theme.Muted, t));

        if (label == "…")
        {
            float cy = r.Y + r.Height / 2f;
            float cx = r.X + r.Width / 2f;

            for (int k = -1; k <= 1; k++)
                g.FillEllipse(brush, cx + k * 5f * s - 1.2f * s, cy - 1.2f * s, 2.4f * s, 2.4f * s);

            return;
        }

        var size = g.MeasureString(label, _fTile);
        g.DrawString(label, _fTile, brush, r.X + (r.Width - size.Width) / 2f, r.Y + (r.Height - size.Height) / 2f);
    }

    private void DrawPower(Graphics g, float t, float s)
    {
        var r = PowerRect(t);
        bool running = _tracking.IsRunning;

        Color color;
        if (running) color = Theme.Accent;
        else if (_powerHover || _powerDown) color = Theme.Accent;
        else color = Theme.Idle;

        // 悬停时提亮（浅色主题下是压暗）一点，运行态也能看出“这里可以点”
        if (_powerHover || _powerDown) color = Theme.Mix(color, Theme.HoverToward, 0.22f);

        // 运行时让光晕缓慢呼吸，表示“正在计时”
        float pulse = 1f;
        if (running)
        {
            double phase = Environment.TickCount64 / 1000.0 * 1.6;
            pulse = 0.72f + 0.28f * (float)((Math.Sin(phase) + 1) / 2);
        }

        float stroke = Theme.Lerp(6f * s, 3.4f * s, t);
        float inset = stroke / 2f + 2f * s;
        var ring = new RectangleF(r.X + inset, r.Y + inset, r.Width - inset * 2f, r.Height - inset * 2f);

        using (var pen = new Pen(Theme.A(color, pulse), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(pen, ring, -55f, 290f);

            float cx = ring.X + ring.Width / 2f;
            g.DrawLine(pen, cx, ring.Y + stroke * 0.2f, cx, ring.Y + ring.Height * 0.42f);
        }
    }

    private void DrawHint(Graphics g, float t, float s)
    {
        if (t > 0.9f) return;

        float alpha = 1f - t;
        var r = PowerRect(t);
        string hint = "点击开始记录";

        using var brush = new SolidBrush(Theme.A(Theme.Muted, alpha));
        var size = g.MeasureString(hint, _fHint);
        g.DrawString(hint, _fHint, brush, Width / 2f - size.Width / 2f, r.Bottom + 24f * s);
    }

    // ── 交互 ────────────────────────────────────────────────

    internal static void Dbg(string msg)
    {
        // 只在拿 TT_DEBUG=1 启动时写日志，平时不产生任何文件
        if (Environment.GetEnvironmentVariable("TT_DEBUG") != "1") return;

        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tt-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_resizeEdge != Edge.None)
        {
            ResizeFrom(Cursor.Position);
            return;
        }

        if (_dragArmed)
        {
            // 用屏幕绝对坐标算位移，比 PointToScreen 更稳，也不会让窗口自己追自己
            var p = Cursor.Position;
            int dx = p.X - _dragCursorOrigin.X;
            int dy = p.Y - _dragCursorOrigin.Y;

            if (Math.Abs(dx) + Math.Abs(dy) > 3)
                Location = new Point(_dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy);

            return;
        }

        // 边框 5px 内是改尺寸的热区，比电源键优先级高，但不会碰到它（键在右上角内侧）
        var edge = HitEdge(e.Location);
        if (edge != _hoverEdge)
        {
            _hoverEdge = edge;
            Invalidate();
        }

        if (edge != Edge.None)
        {
            _powerHover = false;
            _pinHover = false;
            Cursor = CursorFor(edge);
            return;
        }

        bool power = PowerRect(Theme.Ease(_anim)).Contains(e.Location);
        bool pin = PinRect(S).Contains(e.Location);

        if (power != _powerHover || pin != _pinHover)
        {
            _powerHover = power;
            _pinHover = pin;
            Invalidate();
        }

        Cursor = power || pin ? Cursors.Hand : Cursors.Default;
    }

    private Edge HitEdge(Point p)
    {
        float s = S;
        int grip = Math.Max(4, (int)Math.Round(5f * s));

        bool left = p.X <= grip;
        bool right = p.X >= Width - grip;
        bool top = p.Y <= grip;
        bool bottom = p.Y >= Height - grip;

        if (top && left) return Edge.NW;
        if (top && right) return Edge.NE;
        if (bottom && left) return Edge.SW;
        if (bottom && right) return Edge.SE;
        if (left) return Edge.W;
        if (right) return Edge.E;
        if (top) return Edge.N;
        if (bottom) return Edge.S;

        return Edge.None;
    }

    private static Cursor CursorFor(Edge edge) => edge switch
    {
        Edge.N or Edge.S => Cursors.SizeNS,
        Edge.E or Edge.W => Cursors.SizeWE,
        Edge.NW or Edge.SE => Cursors.SizeNWSE,
        Edge.NE or Edge.SW => Cursors.SizeNESW,
        _ => Cursors.Default
    };

    /// <summary>按鼠标的屏幕位移改窗口大小；拖左/上边时固定右/下边。</summary>
    private void ResizeFrom(Point cursor)
    {
        var r = _resizeRect;
        int right = r.Right;
        int bottom = r.Bottom;

        int dx = cursor.X - _resizeCursor.X;
        int dy = cursor.Y - _resizeCursor.Y;

        int minW = (int)Math.Round(320f * S);
        int minH = (int)Math.Round(240f * S);

        if (_resizeEdge is Edge.W or Edge.NW or Edge.SW)
        {
            int x = Math.Min(r.X + dx, right - minW);
            r.X = x;
            r.Width = right - x;
        }

        if (_resizeEdge is Edge.E or Edge.NE or Edge.SE)
            r.Width = Math.Max(minW, r.Width + dx);

        if (_resizeEdge is Edge.N or Edge.NW or Edge.NE)
        {
            int y = Math.Min(r.Y + dy, bottom - minH);
            r.Y = y;
            r.Height = bottom - y;
        }

        if (_resizeEdge is Edge.S or Edge.SW or Edge.SE)
            r.Height = Math.Max(minH, r.Height + dy);

        _sizeLocked = true;
        Bounds = r;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        var edge = HitEdge(e.Location);
        if (edge != Edge.None)
        {
            _resizeEdge = edge;
            _resizeCursor = Cursor.Position;
            _resizeRect = Bounds;
            return;
        }

        // 左上角的钉子 = 置顶开关，点一下就记住
        if (PinRect(S).Contains(e.Location))
        {
            _settings.TopMost = !_settings.TopMost;
            TopMost = _settings.TopMost;
            _settings.Save();
            Invalidate();
            return;
        }

        var pr = PowerRect(Theme.Ease(_anim));
        Dbg($"down loc={e.Location} anim={_anim:F3} power={pr} hit={pr.Contains(e.Location)} size={Width}x{Height} cursor={Cursor.Position}");

        if (pr.Contains(e.Location))
        {
            _powerDown = true;
            Invalidate();
            return;
        }

        // 拖动窗口：先记住起点，真的移动了才开始拖，避免一次点击就把窗口弹走
        _dragArmed = true;
        _dragCursorOrigin = Cursor.Position;
        _dragWindowOrigin = Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_resizeEdge != Edge.None)
        {
            _resizeEdge = Edge.None;
            _hoverEdge = Edge.None;
            Cursor = Cursors.Default;
            return;
        }

        if (_powerDown)
        {
            _powerDown = false;
            Invalidate();
            bool hit = PowerRect(Theme.Ease(_anim)).Contains(e.Location);
            Dbg($"up loc={e.Location} hit={hit} running={_tracking.IsRunning}");
            if (hit) TogglePower();
        }

        _dragArmed = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= Math.Sign(e.Delta) * 2;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 关窗口 = 缩回托盘，只有托盘菜单里的“退出”才真的退出
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        if (Visible)
        {
            RefreshData();
            _dataTimer.Start();
            _pulseTimer.Enabled = _tracking.IsRunning;
        }
        else
        {
            _dataTimer.Stop();
            _pulseTimer.Stop();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float s = S;

        // 面板可能是在采集器已经跑起来之后才第一次打开的，
        // 这时直接落到正确状态，不要从“停止态”滑一遍。
        _anim = _tracking.IsRunning ? 1f : 0f;
        _target = _anim;

        Size = new Size((int)(BaseWidth * s), (int)(BaseHeight * s));

        // 第一次打开时直接落到内容高度，避免先闪一下 600 高再缩回去
        if (_tracking.IsRunning) Height = (int)Math.Round(DesiredHeight(s));

        ApplyRegion();

        RefreshData();
        _pulseTimer.Enabled = _tracking.IsRunning;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;

        using var path = Theme.RoundRect(new RectangleF(0f, 0f, Width, Height), 16f * S);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tracking.StateChanged -= OnTrackingStateChanged;
            _animTimer.Dispose();
            _pulseTimer.Dispose();
            _dataTimer.Dispose();
            _fHint.Dispose();
            _fName.Dispose();
            _fTime.Dispose();
            _fLabel.Dispose();
            _fTotal.Dispose();
            _fTile.Dispose();
        }

        base.Dispose(disposing);
    }
}
