using System.Text.Json;

namespace EdgeWorkspace;

/// <summary>应用配置（exe 同目录 config.json）。P9 起接通；P12 扩展工作区路径/默认排序/开机自启。</summary>
public sealed class AppConfig
{
    public int trashAutoClearDays { get; set; }   // 回收站自动清空：超过 N 天的项启动时清除；0=关闭
}

/// <summary>配置读写。损坏时回退默认值（不抛）。</summary>
public static class ConfigStore
{
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Current { get; private set; } = Load();

    private static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { /* 损坏 -> 默认 */ }
        return new AppConfig();
    }

    public static void Save() =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));

    public static void Update(Action<AppConfig> mutate)
    {
        mutate(Current);
        Save();
    }
}
