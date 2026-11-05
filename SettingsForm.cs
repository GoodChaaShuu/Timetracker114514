using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TimeTracker;

/// <summary>
/// 设置面板。和主面板一样是自绘无边框，只有三件事可调：
/// 主题、列表里显示几个应用、以及「不限」。
/// </summary>
public sealed class SettingsForm : Form
{
    private const int BaseW = 360;
    private const int BaseH = 268;

    private readonly AppSettings _settings;
    private readonly Action _changed;

    private readonly Font _fTitle = new(Theme.Family, 13f, FontStyle.Bold);
    private readonly Font _fLabel = new(Theme.Family, 10f);
    private readonly Font _fSmall = new(Theme.Family, 8.5f);
    private readonly Font _fValue = new(Theme.Family, 10.5f, FontStyle.Bold);
    private readonly Font _fSeg = new(Theme.Family, 9.5f);
    private readonly Font _fGlyph = new(Theme.Family, 12f);

    private int _hot = -1;
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragCursor;
    private Point _dragOrigin;

    public SettingsForm(AppSettings settings, Action changed)
    {
        _settings = settings;
        _changed = changed;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        Text = "设置";
        Size = new Size(BaseW, BaseH);
    }

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

    // ── 布局 ────────────────────────────────────────────────

    private RectangleF SegRect(float s) => new(152f * s, 66f * s, 184f * s, 34f * s);

    private RectangleF MinusRect(float s) => new(152f * s, 116f * s, 40f * s, 34f * s);

    private RectangleF PlusRect(float s) => new(296f * s, 116f * s, 40f * s, 34f * s);

    private RectangleF DoneRect(float s) => new(262f * s, 210f * s, 74f * s, 32f * s);

    /// <summary>返回鼠标落在哪个可点区域：0/1 主题格，2 减，3 加，4 完成，-1 无。</summary>
    private int HitTest(Point p)
    {
        float s = S;

        var seg = SegRect(s);
        if (seg.Contains(p))
        {
            float half = seg.Width / 2f;
            return p.X < seg.X + half ? 0 : 1;
        }

        if (MinusRect(s).Contains(p)) return 2;
        if (PlusRect(s).Contains(p)) return 3;
        if (DoneRect(s).Contains(p)) return 4;

        return -1;
    }

    // ── 绘制 ────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float s = S;

        using (var bg = new SolidBrush(Theme.Background))
            g.FillRectangle(bg, ClientRectangle);

        using (var frame = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 16f * s))
        using (var pen = new Pen(Theme.Border))
            g.DrawPath(pen, frame);

        using (var text = new SolidBrush(Theme.Text))
        using (var muted = new SolidBrush(Theme.Muted))
        {
            g.DrawString("设置", _fTitle, text, 24f * s, 20f * s);

            g.DrawString("外观", _fLabel, text, 24f * s, 75f * s);
            g.DrawString("显示应用数", _fLabel, text, 24f * s, 125f * s);

            g.DrawString("超出的应用会合并成「其他」，数据不会丢", _fSmall, muted, 24f * s, 164f * s);
        }

        DrawSegmented(g, s);
        DrawStepper(g, s);
        DrawDone(g, s);
    }

    private void DrawSegmented(Graphics g, float s)
    {
        var r = SegRect(s);

        using (var path = Theme.RoundRect(r, 9f * s))
        using (var fill = new SolidBrush(Theme.Surface))
            g.FillPath(fill, path);

        float half = r.Width / 2f;

        for (int i = 0; i < 2; i++)
        {
            var cell = new RectangleF(r.X + i * half, r.Y, half, r.Height);
            bool selected = (i == 0) == Theme.Dark;

            if (selected)
            {
                var inner = RectangleF.Inflate(cell, -3f * s, -3f * s);
                using var path = Theme.RoundRect(inner, 7f * s);
                using var fill = new SolidBrush(Theme.Background);
                using var pen = new Pen(Theme.Border);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            string label = i == 0 ? "夜间" : "白天";
            var color = selected ? Theme.Text : (i == _hot ? Theme.Text : Theme.Muted);

            using var brush = new SolidBrush(color);
            var size = g.MeasureString(label, _fSeg);
            g.DrawString(label, _fSeg, brush, cell.X + (cell.Width - size.Width) / 2f, cell.Y + (cell.Height - size.Height) / 2f);
        }
    }

    private void DrawStepper(Graphics g, float s)
    {
        var box = new RectangleF(152f * s, 116f * s, 184f * s, 34f * s);

        using (var path = Theme.RoundRect(box, 9f * s))
        using (var fill = new SolidBrush(Theme.Surface))
            g.FillPath(fill, path);

        DrawStepperButton(g, s, MinusRect(s), "−", 2);
        DrawStepperButton(g, s, PlusRect(s), "+", 3);

        string value = _settings.VisibleApps == 0 ? "不限" : _settings.VisibleApps + " 个";

        using var brush = new SolidBrush(Theme.Text);
        var size = g.MeasureString(value, _fValue);
        g.DrawString(value, _fValue, brush,
            box.X + (box.Width - size.Width) / 2f,
            box.Y + (box.Height - size.Height) / 2f);
    }

    private void DrawStepperButton(Graphics g, float s, RectangleF r, string glyph, int id)
    {
        if (_hot == id)
        {
            using var path = Theme.RoundRect(RectangleF.Inflate(r, -2f * s, -2f * s), 7f * s);
            using var fill = new SolidBrush(Theme.A(Theme.Text, 0.08f));
            g.FillPath(fill, path);
        }

        using var brush = new SolidBrush(_hot == id ? Theme.Text : Theme.Muted);
        var size = g.MeasureString(glyph, _fGlyph);
        g.DrawString(glyph, _fGlyph, brush, r.X + (r.Width - size.Width) / 2f, r.Y + (r.Height - size.Height) / 2f + 1f * s);
    }

    private void DrawDone(Graphics g, float s)
    {
        var r = DoneRect(s);
        bool hot = _hot == 4;

        using (var path = Theme.RoundRect(r, 9f * s))
        using (var fill = new SolidBrush(hot ? Theme.Mix(Theme.Accent, Theme.Dark ? Color.White : Color.Black, 0.12f) : Theme.Accent))
            g.FillPath(fill, path);

        using var brush = new SolidBrush(Theme.Dark ? Theme.Background : Color.White);
        var size = g.MeasureString("完成", _fSeg);
        g.DrawString("完成", _fSeg, brush, r.X + (r.Width - size.Width) / 2f, r.Y + (r.Height - size.Height) / 2f);
    }

    // ── 交互 ────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging)
        {
            var p = Cursor.Position;
            Location = new Point(_dragOrigin.X + p.X - _dragCursor.X, _dragOrigin.Y + p.Y - _dragCursor.Y);
            return;
        }

        if (_dragArmed)
        {
            var p = Cursor.Position;
            if (Math.Abs(p.X - _dragCursor.X) + Math.Abs(p.Y - _dragCursor.Y) > 3)
            {
                _dragging = true;
                _dragOrigin = Location;
            }

            return;
        }

        int hot = HitTest(e.Location);
        if (hot != _hot)
        {
            _hot = hot;
            Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int hot = HitTest(e.Location);
        if (hot >= 0)
        {
            Apply(hot);
            return;
        }

        // 空白处按下 = 拖窗口
        _dragArmed = true;
        _dragging = false;
        _dragCursor = Cursor.Position;
        _dragOrigin = Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragArmed = false;
        _dragging = false;
    }

    private void Apply(int hot)
    {
        switch (hot)
        {
            case 0:
            case 1:
                _settings.Dark = hot == 0;
                Theme.SetDark(_settings.Dark);
                break;

            case 2:
                _settings.VisibleApps = _settings.VisibleApps switch
                {
                    0 => 30,               // 不限 → 退回最大的一档
                    1 => 1,
                    _ => _settings.VisibleApps - 1
                };
                break;

            case 3:
                _settings.VisibleApps = _settings.VisibleApps switch
                {
                    0 => 0,                // 已经是不限，没得再加
                    30 => 0,               // 到顶了，再点就是不限
                    _ => _settings.VisibleApps + 1
                };
                break;

            case 4:
                Close();
                return;
        }

        _settings.Save();
        _changed();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyRegion();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        float s = S;
        Size = new Size((int)Math.Round(BaseW * s), (int)Math.Round(BaseH * s));

        // 贴着主面板右下角弹出来，别挡住它
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(
            Math.Clamp((screen.Width - Width) / 2, screen.Left, Math.Max(screen.Left, screen.Right - Width)),
            Math.Clamp((screen.Height - Height) / 2, screen.Top, Math.Max(screen.Top, screen.Bottom - Height)));

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
            _fTitle.Dispose();
            _fLabel.Dispose();
            _fSmall.Dispose();
            _fValue.Dispose();
            _fSeg.Dispose();
            _fGlyph.Dispose();
        }

        base.Dispose(disposing);
    }
}
