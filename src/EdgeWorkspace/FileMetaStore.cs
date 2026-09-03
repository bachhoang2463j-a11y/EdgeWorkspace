using System.Text.Json;

namespace EdgeWorkspace;

/// <summary>
/// 文件元数据仓（v2 柱2，SPEC §9.1）：工作区相对路径（"抽屉/文件名"，未分类为 "文件名"）
/// -> { pinned, openCount, lastOpened }。置顶聚合、常用优先、未来的标签/备注都只是加字段。
/// 打开计数在 openPath 时累加（RecordOpen，低频人工操作、立即落盘）；
/// 推送时合并进 files 消息（Apply），前端零额外请求。
/// </summary>
public static class FileMetaStore
{
    public sealed class FileMeta
    {
        public bool pinned { get; set; }
        public int openCount { get; set; }
        public string lastOpened { get; set; } = "";
    }

    private static string MetaPath => Path.Combine(AppContext.BaseDirectory, "meta.json");

    private static Dictionary<string, FileMeta>? _cache;
    private static Dictionary<string, FileMeta> Meta => _cache ??= Load();

    private static Dictionary<string, FileMeta> Load()
    {
        try
        {
            if (!File.Exists(MetaPath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, FileMeta>>(File.ReadAllText(MetaPath)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save() => File.WriteAllText(MetaPath, JsonSerializer.Serialize(Meta));

    private static string Key(string? drawer, string name) =>
        string.IsNullOrEmpty(drawer) ? name : drawer + "/" + name;

    /// <summary>推送时把 pinned/openCount 合并进条目。</summary>
    public static void Apply(FileEntry e)
    {
        if (Meta.TryGetValue(Key(e.drawer, e.name), out var m))
        {
            e.pinned = m.pinned;
            e.openCount = m.openCount;
        }
    }

    /// <summary>打开一次：计数 + 时间戳，立即落盘。</summary>
    public static void RecordOpen(string? drawer, string name)
    {
        var key = Key(drawer, name);
        if (!Meta.TryGetValue(key, out var m)) Meta[key] = m = new FileMeta();
        m.openCount++;
        m.lastOpened = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        Save();
    }

    /// <summary>置顶标记（P10），立即落盘。</summary>
    public static void SetPinned(string? drawer, string name, bool pinned)
    {
        var key = Key(drawer, name);
        if (!Meta.TryGetValue(key, out var m)) Meta[key] = m = new FileMeta();
        m.pinned = pinned;
        Save();
    }

    /// <summary>抽屉改名：迁移该抽屉下全部条目的键前缀（置顶/常用统计跟随，P10）。</summary>
    public static void MigrateDrawer(string oldName, string newName)
    {
        var prefix = oldName + "/";
        var oldKeys = Meta.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (oldKeys.Count == 0) return;
        foreach (var old in oldKeys)
        {
            var value = Meta[old];
            Meta.Remove(old);
            Meta[newName + "/" + old[prefix.Length..]] = value;
        }
        Save();
    }
}
