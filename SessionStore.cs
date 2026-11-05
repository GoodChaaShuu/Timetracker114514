using Microsoft.Data.Sqlite;

namespace TimeTracker;

public sealed record AppTotal(string ExeName, long Milliseconds, long SessionCount, string? ExePath = null);

/// <summary>
/// 会话存储。一行 = 一段连续使用某个应用的时间，不是“每秒一条采样”。
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>
    /// UI 线程每隔一两秒查一次统计，采集线程每秒写一次，而它们共用一个 SqliteConnection。
    /// 所以所有访问必须串行化，否则会出现 “connection is busy” / 事务交叉。
    /// </summary>
    private readonly object _gate = new();

    public string DbPath { get; }

    public SessionStore(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);

        SqliteConnection conn;

        try
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());

            conn.Open();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法创建或打开数据库：{DbPath}{Environment.NewLine}" +
                $"原因：{ex.Message}{Environment.NewLine}" +
                $"换一个位置重试即可，例如：--db \"{Path.Combine(Path.GetTempPath(), "timetracker.db")}\"",
                ex);
        }

        _conn = conn;

        Exec("PRAGMA journal_mode=WAL;");   // 崩溃/并发读更安全
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("""
             CREATE TABLE IF NOT EXISTS session (
                 id           INTEGER PRIMARY KEY AUTOINCREMENT,
                 started_at   INTEGER NOT NULL,   -- unix 毫秒，UTC
                 ended_at     INTEGER NOT NULL,   -- unix 毫秒，UTC
                 exe_name     TEXT    NOT NULL,
                 exe_path     TEXT,
                 window_title TEXT
             );
             CREATE INDEX IF NOT EXISTS idx_session_started ON session(started_at);

             -- 挂机（空闲/锁屏）时段，和 session 分开存：它不是某个应用的功劳
             CREATE TABLE IF NOT EXISTS idle (
                 id         INTEGER PRIMARY KEY AUTOINCREMENT,
                 started_at INTEGER NOT NULL,
                 ended_at   INTEGER NOT NULL
             );
             CREATE INDEX IF NOT EXISTS idx_idle_started ON idle(started_at);
             """);
    }

    /// <summary>
    /// 开启一个会话，立刻落库（先写 end == start 的占位行），
    /// 这样进程被强杀最多丢 30 秒，而不是丢掉整段。
    /// </summary>
    public long Begin(string exeName, string exePath, string title, DateTime startUtc)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session (started_at, ended_at, exe_name, exe_path, window_title)
                VALUES ($start, $start, $exe, $path, $title)
                RETURNING id;
                """;
            long ms = ToMs(startUtc);
            cmd.Parameters.AddWithValue("$start", ms);
            cmd.Parameters.AddWithValue("$exe", exeName);
            cmd.Parameters.AddWithValue("$path", string.IsNullOrEmpty(exePath) ? (object)DBNull.Value : exePath);
            cmd.Parameters.AddWithValue("$title", string.IsNullOrEmpty(title) ? (object)DBNull.Value : title);

            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void End(long id, DateTime endUtc)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE session SET ended_at = $end WHERE id = $id;";
            cmd.Parameters.AddWithValue("$end", ToMs(endUtc));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 统计 [fromUtc, toUtc) 内各应用的使用时长。
    /// 关键点：会话可能跨天，所以用“区间求交”而不是 where started_at &gt;= …，
    /// 否则 23:50 用到 0:30 的那一段会被整天丢掉或整天重复计入。
    /// </summary>
    /// <param name="excludeId">要排除的会话 id（通常是当前正在进行的那段，它的时长由界面实时补算）。</param>
    public IReadOnlyList<AppTotal> Totals(DateTime fromUtc, DateTime toUtc, long excludeId = 0)
    {
        var result = new List<AppTotal>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT exe_name,
                       SUM(MIN(ended_at, $end) - MAX(started_at, $start)) AS ms,
                       COUNT(*) AS n,
                       MAX(exe_path) AS path
                FROM session
                WHERE ended_at > $start AND started_at < $end
                  AND ($exclude = 0 OR id <> $exclude)
                GROUP BY exe_name
                HAVING ms > 0
                ORDER BY ms DESC;
                """;
            cmd.Parameters.AddWithValue("$start", ToMs(fromUtc));
            cmd.Parameters.AddWithValue("$end", ToMs(toUtc));
            cmd.Parameters.AddWithValue("$exclude", excludeId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new AppTotal(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return result;
    }

    /// <summary>记一段挂机时间。比会话短得多的碎片（几秒）不写，免得把库撑脏。</summary>
    public void AddIdle(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return;
        if (endUtc - startUtc < TimeSpan.FromSeconds(20)) return;

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO idle (started_at, ended_at) VALUES ($start, $end);";
            cmd.Parameters.AddWithValue("$start", ToMs(startUtc));
            cmd.Parameters.AddWithValue("$end", ToMs(endUtc));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>[fromUtc, toUtc) 内累计挂机毫秒数，同样按区间求交，跨天不会重复。</summary>
    public long IdleTotal(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(MIN(ended_at, $end) - MAX(started_at, $start)), 0)
                FROM idle
                WHERE ended_at > $start AND started_at < $end;
                """;
            cmd.Parameters.AddWithValue("$start", ToMs(fromUtc));
            cmd.Parameters.AddWithValue("$end", ToMs(toUtc));

            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ToMs(DateTime utc) => new DateTimeOffset(
        DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
