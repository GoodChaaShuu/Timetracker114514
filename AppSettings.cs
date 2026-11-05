using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeTracker;

/// <summary>
/// 用户偏好，存成和数据库同目录的 settings.json。
/// 读失败一律退回默认值：配置坏了不该让程序起不来。
/// </summary>
public sealed class AppSettings
{
    /// <summary>夜间模式。true = 深色。默认深色，和第一版观感一致。</summary>
    public bool Dark { get; set; } = true;

    /// <summary>列表里最多显示几个应用，0 表示不限。超出的合并成「其他」。</summary>
    public int VisibleApps { get; set; } = 5;

    /// <summary>面板是否置顶（左上角那颗钉子）。</summary>
    public bool TopMost { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    public static AppSettings Load(string dbPath)
    {
        var settings = new AppSettings();

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dir)) settings.FilePath = Path.Combine(dir, "settings.json");
        }
        catch
        {
            return settings;
        }

        try
        {
            if (settings.FilePath.Length > 0 && File.Exists(settings.FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settings.FilePath));
                if (loaded is not null)
                {
                    settings.Dark = loaded.Dark;
                    settings.VisibleApps = Math.Clamp(loaded.VisibleApps, 0, 30);
                    settings.TopMost = loaded.TopMost;
                }
            }
        }
        catch
        {
            // 配置损坏就用默认值，下次保存会覆盖掉
        }

        return settings;
    }

    public void Save()
    {
        if (FilePath.Length == 0) return;

        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // 先写临时文件再替换，避免写到一半断电留下半个 json
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 配置存不下不影响使用
        }
    }
}
