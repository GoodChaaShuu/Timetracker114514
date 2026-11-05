namespace TimeTracker;

public sealed class Options
{
    public string DbPath { get; private set; } = "";
    public string? DbPathWarning { get; private set; }
    public int SampleMs { get; private set; } = 1000;
    public TimeSpan IdleThreshold { get; private set; } = TimeSpan.FromSeconds(60);
    public bool ReportMode { get; private set; }
    public int Days { get; private set; } = 1;
    public bool Quiet { get; private set; }
    public bool ShowHelp { get; private set; }

    public static string PrimaryDbPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TimeTracker",
        "timetracker.db");

    public static Options Parse(string[] args)
    {
        var o = new Options();
        bool dbGiven = false;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--report":
                case "-r":
                    o.ReportMode = true;
                    break;

                case "--days":
                case "-d":
                    o.Days = Math.Max(1, NextInt(args, ref i, 1));
                    o.ReportMode = true;
                    break;

                case "--idle":
                    o.IdleThreshold = TimeSpan.FromSeconds(Math.Max(10, NextInt(args, ref i, 60)));
                    break;

                case "--sample":
                    o.SampleMs = Math.Max(200, NextInt(args, ref i, 1000));
                    break;

                case "--db":
                    o.DbPath = NextString(args, ref i, o.DbPath);
                    dbGiven = true;
                    break;

                case "--quiet":
                case "-q":
                    o.Quiet = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    o.ShowHelp = true;
                    break;
            }
        }

        // 没显式指定 --db 时，按可写性挑一个位置。
        // 为什么要挑：%LOCALAPPDATA% 在部分机器上会被安全软件、组策略、
        // 或一个同名的残留文件挡住，直接 CreateDirectory 会抛 UnauthorizedAccessException。
        if (!dbGiven || string.IsNullOrWhiteSpace(o.DbPath))
        {
            o.DbPath = ResolveDefaultPath(out string? warning);
            o.DbPathWarning = warning;
        }

        return o;
    }

    private static string ResolveDefaultPath(out string? warning)
    {
        warning = null;

        var candidates = new List<string>();

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "TimeTracker"));

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming)) candidates.Add(Path.Combine(roaming, "TimeTracker"));

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "data"));

        foreach (string dir in candidates)
        {
            if (CanWrite(dir, out string reason)) return Path.Combine(dir, "timetracker.db");

            warning ??= $"默认目录 {dir} 不可写（{reason}），已自动换到其它位置。";
        }

        // 全都不行：仍然返回首选路径，让 SessionStore 抛出带修复建议的异常。
        return Path.Combine(candidates[0], "timetracker.db");
    }

    private static bool CanWrite(string dir, out string reason)
    {
        reason = "";
        try
        {
            Directory.CreateDirectory(dir);

            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static int NextInt(string[] args, ref int i, int fallback)
    {
        if (i + 1 >= args.Length) return fallback;
        if (!int.TryParse(args[i + 1], out int v)) return fallback;
        i++;
        return v;
    }

    private static string NextString(string[] args, ref int i, string fallback)
    {
        if (i + 1 >= args.Length) return fallback;
        i++;
        return args[i];
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            TimeTracker —— 统计你在各个应用上花了多少时间

            用法：
              TimeTracker                 开始记录（Ctrl+C 停止）
              TimeTracker --report        查看今天的统计
              TimeTracker --days 7        查看最近 7 天
              TimeTracker --idle 180      空闲超过 180 秒就停止计时（默认 60）
              TimeTracker --sample 500    采样间隔毫秒（默认 1000）
              TimeTracker --db <path>     指定数据库位置
              TimeTracker --quiet         不打印切换日志

            数据库默认位置：
            """ + PrimaryDbPath());
    }
}
