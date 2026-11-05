namespace TimeTracker;

/// <summary>
/// 采集循环。核心不变量：时长一律由墙钟时间差得出，绝不累加 tick 计数。
/// </summary>
public sealed class Tracker
{
    /// <summary>两次采样间隔超过这个值，就认为是睡眠/休眠/系统时间跳变，整段丢弃。</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    private readonly SessionStore _store;
    private readonly Options _opt;

    private long _openId;
    private string _openKey = "";
    private DateTime _openStartUtc;
    private DateTime _lastSampleUtc;
    private DateTime _lastTouchUtc;
    private bool _away;

    /// <summary>正在进行的挂机时段起点。界面要实时显示挂机时长时读它。</summary>
    private DateTime _idleStartUtc = DateTime.MinValue;

    /// <summary>当前正在计时的应用名；空闲/锁屏/切换间隙为 null。界面实时显示用。</summary>
    public string? CurrentExe { get; private set; }

    /// <summary>当前应用的 exe 全路径，界面用来取图标。</summary>
    public string? CurrentExePath { get; private set; }

    /// <summary>当前这段会话的开始时间（UTC）。</summary>
    public DateTime CurrentSinceUtc { get; private set; }

    /// <summary>当前挂机时段从何时开始（UTC）；没在挂机时为 <see cref="DateTime.MinValue"/>。</summary>
    public DateTime IdleSinceUtc => _idleStartUtc;

    /// <summary>当前这段会话在库里的 id。统计时排除它，避免和实时补算重复。</summary>
    public long CurrentSessionId => _openId;

    public Tracker(SessionStore store, Options opt)
    {
        _store = store;
        _opt = opt;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _lastSampleUtc = DateTime.UtcNow;
        _lastTouchUtc = _lastSampleUtc;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opt.SampleMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                Sample();
                if (DateTime.UtcNow - _lastTouchUtc >= TouchInterval) Touch();
            }
            catch (Exception ex)
            {
                Log("采样出错：" + ex.Message);
            }
        }

        Close(DateTime.UtcNow);
        FlushIdle(DateTime.UtcNow);
    }

    private void Sample()
    {
        var now = DateTime.UtcNow;
        var delta = now - _lastSampleUtc;

        // 时间倒退（改系统时间）或跳变（睡眠唤醒）：不留记录，直接重设基线。
        if (delta <= TimeSpan.Zero || delta > MaxGap)
        {
            if (delta > MaxGap)
                Log($"计时中断 {(int)delta.TotalSeconds} 秒（睡眠/休眠/改时间），这段不计入。");

            Close(_lastSampleUtc);
            FlushIdle(_lastSampleUtc);
            _lastSampleUtc = now;
            return;
        }

        bool away = Foreground.IdleTime() > _opt.IdleThreshold || Foreground.IsLocked();
        if (away)
        {
            Close(now);

            if (_idleStartUtc == DateTime.MinValue)
            {
                // 空闲阈值是 60 秒，真正的离开时刻要比“发现时”早，往回补那一段。
                // 但不能早于上一次采样，否则会和已经计入的应用时长重叠。
                long idleMs = (long)Foreground.IdleTime().TotalMilliseconds;
                var start = now.AddMilliseconds(-idleMs);
                if (idleMs <= 0 || start < _lastSampleUtc) start = _lastSampleUtc;
                _idleStartUtc = start;
            }

            if (!_away)
            {
                _away = true;
                Log("已离开（空闲或锁屏），暂停计时。");
            }

            _lastSampleUtc = now;
            return;
        }

        if (_away)
        {
            _away = false;
            FlushIdle(now);
            Log("恢复计时。");
        }

        var app = Foreground.Current();
        if (app is null)
        {
            Close(now);
            _lastSampleUtc = now;
            return;
        }

        if (_openId == 0 || _openKey != app.Key)
        {
            Close(now);

            _openId = _store.Begin(app.ExeName, app.ExePath, app.Title, now);
            _openKey = app.Key;
            _openStartUtc = now;
            _lastTouchUtc = now;
            CurrentExe = app.ExeName;
            CurrentExePath = app.ExePath;
            CurrentSinceUtc = now;

            if (!_opt.Quiet)
                Log($"▶ {Format.Trim(app.ExeName, 24),-24} {Format.Trim(app.Title, 55)}");
        }

        _lastSampleUtc = now;
    }

    /// <summary>把当前未结束的会话滚动刷新到数据库，防止崩溃时丢失整段。</summary>
    private void Touch()
    {
        _lastTouchUtc = DateTime.UtcNow;
        if (_openId != 0) _store.End(_openId, _lastTouchUtc);
    }

    private void Close(DateTime endUtc)
    {
        if (_openId == 0) return;

        if (endUtc < _openStartUtc) endUtc = _openStartUtc;

        _store.End(_openId, endUtc);

        // 切换太快产生的零长度会话没有统计价值，删掉免得把库撑脏。
        if (endUtc - _openStartUtc < TimeSpan.FromSeconds(1)) _store.Delete(_openId);

        _openId = 0;
        _openKey = "";
        CurrentExe = null;
        CurrentExePath = null;
    }

    /// <summary>把进行中的挂机时段落库并清零。end 之前没挂机就什么也不做。</summary>
    private void FlushIdle(DateTime endUtc)
    {
        if (_idleStartUtc == DateTime.MinValue) return;

        var start = _idleStartUtc;
        _idleStartUtc = DateTime.MinValue;

        if (endUtc <= start) return;

        try
        {
            _store.AddIdle(start, endUtc);
        }
        catch (Exception ex)
        {
            Log("写挂机记录失败：" + ex.Message);
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {message}");
}
