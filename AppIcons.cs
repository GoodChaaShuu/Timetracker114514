using System.Drawing.Drawing2D;

namespace TimeTracker;

/// <summary>
/// 从 exe 里抠应用图标。按进程名缓存，同一个应用只读一次磁盘。
/// 抠不到（文件被删、路径为空、路径是 UWP 的伪路径）就返回 null，由界面画首字母色块。
/// </summary>
internal static class AppIcons
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? For(string exeName, string? exePath)
    {
        if (Cache.TryGetValue(exeName, out var cached)) return cached;

        Bitmap? bitmap = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null) bitmap = icon.ToBitmap();
            }
        }
        catch
        {
            // 图标坏了不影响记录，退回色块
        }

        Cache[exeName] = bitmap;
        return bitmap;
    }

    /// <summary>把 <see cref="Cache"/> 里的位图按新尺寸重采样，避免 32px 图标直接缩小发糊。</summary>
    public static void Draw(Graphics g, Bitmap bitmap, RectangleF target)
    {
        var mode = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(bitmap, target);
        g.InterpolationMode = mode;
    }
}
