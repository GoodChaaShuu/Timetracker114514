namespace TimeTracker;

public static class Report
{
    private static readonly string[] Weekdays = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    private const int BarWidth = 32;

    public static void Print(SessionStore store, Options opt)
    {
        int days = Math.Max(1, opt.Days);

        for (int i = days - 1; i >= 0; i--)
        {
            var day = DateTime.Today.AddDays(-i);

            // 本地自然日 → UTC 区间。注意用 ToUniversalTime 而不是手工加 8 小时。
            var startUtc = day.ToUniversalTime();
            var endUtc = day.AddDays(1).ToUniversalTime();

            var rows = store.Totals(startUtc, endUtc);
            PrintDay(day, rows);

            if (i > 0) Console.WriteLine();
        }
    }

    private static void PrintDay(DateTime day, IReadOnlyList<AppTotal> rows)
    {
        Console.WriteLine($"{day:yyyy-MM-dd}  {Weekdays[(int)day.DayOfWeek]}");
        Console.WriteLine(new string('─', 68));

        if (rows.Count == 0)
        {
            Console.WriteLine("  这一天没有记录。");
            return;
        }

        long total = rows.Sum(r => r.Milliseconds);
        long max = rows.Max(r => r.Milliseconds);

        foreach (var r in rows)
        {
            int bar = Math.Max(1, (int)Math.Round(r.Milliseconds * (double)BarWidth / max));
            double percent = total == 0 ? 0 : r.Milliseconds * 100.0 / total;

            Console.WriteLine(
                $"  {Format.Trim(r.ExeName, 24),-24} {Format.Duration(r.Milliseconds),9}  " +
                $"{new string('█', bar),-32} {percent,4:F0}%");
        }

        Console.WriteLine(new string('─', 68));

        string label = "合计";
        Console.WriteLine($"  {label,-24} {Format.Duration(total),9}   {rows.Count} 个应用");
    }
}
