using System.Text.Json;

namespace IdleSword.Core;

/// <summary>
/// 应用设置（画面与音频）。与玩家存档分开保存：重置游戏进度不应该连带重置画面和音量偏好。
/// </summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
    public double MusicVolume { get; set; } = 70;
    public double SoundVolume { get; set; } = 80;
}

/// <summary>
/// 设置的读写。设置不是进度：文件缺失或损坏时回落到默认值继续启动，
/// 而不是像存档那样拒绝加载——让玩家因为音量配置写坏就打不开游戏是不合理的。
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            return Sanitize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings());
        }
        catch (Exception e) when (e is JsonException or IOException or ArgumentException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(Sanitize(settings), Options), new System.Text.UTF8Encoding(false));
        }
        // 设置写盘失败不应影响游戏进行；本次会话内的改动仍然生效。
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>夹取到合法范围：手改配置文件不应让窗口变成 0×0，或让音量越界。</summary>
    public static AppSettings Sanitize(AppSettings s)
    {
        s.Width = Math.Clamp(s.Width, 640, 7680);
        s.Height = Math.Clamp(s.Height, 360, 4320);
        s.MusicVolume = Math.Clamp(s.MusicVolume, 0, 100);
        s.SoundVolume = Math.Clamp(s.SoundVolume, 0, 100);
        return s;
    }
}
