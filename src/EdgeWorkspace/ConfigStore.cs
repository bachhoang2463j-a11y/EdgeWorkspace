using System.Text.Json;

namespace EdgeWorkspace;

/// <summary>应用配置（exe 同目录 config.json）。P9 起接通；P12 扩展工作区路径/默认排序/开机自启。</summary>
public sealed class AppConfig
{
    public List<string> collapsedDrawers { get; set; } = new();   // 折叠中的抽屉路径（''=未分类）
    public string sortMode { get; set; } = "time";   // time | name | size | kind | frequent
    public List<string> drawerOrder { get; set; } = new();   // 抽屉手动排序（视图序，不动物理目录；未列者按名序补齐）
    public bool staleEnabled { get; set; } = true;   // P12 过期灰显与计数开关
    public int staleDays { get; set; } = 14;         // 过期天数
    public string workspacePath { get; set; } = "";  // 工作区路径（空=默认 D:\Workspace_Temp）
    public bool autostart { get; set; }             // 开机自启（启动时从注册表回读真实状态）
    public string theme { get; set; } = "white";    // P13 皮肤：white | acrylic
    public Dictionary<string, string> drawerIcons { get; set; } = new();   // 抽屉 emoji 标记（路径 -> emoji）
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
