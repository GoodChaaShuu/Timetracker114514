namespace TimeTracker;

internal static class Format
{
    public static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (s.Length <= max) return s;
        return s[..Math.Max(0, max - 1)] + "…";
    }

    public static string Duration(long milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }
}
