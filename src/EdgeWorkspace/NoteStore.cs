using System.IO;
using System.Text.Json;

namespace EdgeWorkspace;

/// <summary>
/// 白板便签后端：notes/ 目录下每个 .txt 即一张便签（无上限）。
/// 文件名 = 便签 id（note1.txt、note2.txt...新建顺延）；内容 = 便签文本。
/// 标题存 notes/index.json（id -> title），正文落盘仍是纯文本 txt。
/// 前端负责展示与编辑，本类只做目录枚举与增删改。
/// </summary>
public static class NoteStore
{
    public sealed record NoteInfo(string Id, string Title, string Content, string Mtime);

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "notes");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    private static string PathFor(string id) => Path.Combine(Dir, id + ".txt");

    private static string IndexPath => Path.Combine(Dir, "index.json");

    /// <summary>便签元数据（P13 贴纸将扩展 tile: { on, x, y, w, h } 字段）。</summary>
    public sealed class NoteMeta
    {
        public string title { get; set; } = "";
    }

    /// <summary>元数据索引（id -> 元数据）。旧格式（id -> 字符串）按仅标题兼容；损坏返回空表。</summary>
    private static Dictionary<string, NoteMeta> LoadMeta()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            using var doc = JsonDocument.Parse(File.ReadAllText(IndexPath));
            var map = new Dictionary<string, NoteMeta>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                map[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? new NoteMeta { title = p.Value.GetString() ?? "" }
                    : JsonSerializer.Deserialize<NoteMeta>(p.Value.GetRawText()) ?? new NoteMeta();
            }
            return map;
        }
        catch { return new(); }
    }

    private static void SaveMeta(Dictionary<string, NoteMeta> meta) =>
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(meta));

    /// <summary>全部便签，按修改时间倒序（最新在前）。</summary>
    public static List<NoteInfo> LoadAll()
    {
        EnsureDir();
        var meta = LoadMeta();
        var list = new List<NoteInfo>();
        foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("*.txt"))
        {
            var id = Path.GetFileNameWithoutExtension(f.Name);
            list.Add(new NoteInfo(
                id,
                meta.TryGetValue(id, out var m) ? m.title : "",
                File.ReadAllText(f.FullName),
                f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Mtime, a.Mtime));
        return list;
    }

    /// <summary>取单张便签（独立窗口用）；不存在返回 null。</summary>
    public static NoteInfo? Get(string id)
    {
        var p = PathFor(id);
        if (!File.Exists(p)) return null;
        var meta = LoadMeta();
        return new NoteInfo(
            id,
            meta.TryGetValue(id, out var m) ? m.title : "",
            File.ReadAllText(p),
            File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>新建便签，返回 id。</summary>
    public static string Create()
    {
        EnsureDir();
        for (var n = 1; ; n++)
        {
            var id = "note" + n;
            if (!File.Exists(PathFor(id)))
            {
                File.WriteAllText(PathFor(id), "");
                return id;
            }
        }
    }

    public static void Save(string id, string content)
    {
        EnsureDir();
        File.WriteAllText(PathFor(id), content);
    }

    /// <summary>改名：写标题索引并刷新 mtime（顶到最前）。</summary>
    public static void Rename(string id, string title)
    {
        var meta = LoadMeta();
        if (!meta.TryGetValue(id, out var m)) meta[id] = m = new NoteMeta();
        m.title = title;
        SaveMeta(meta);
        var p = PathFor(id);
        if (File.Exists(p)) File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
    }

    public static void Delete(string id)
    {
        var p = PathFor(id);
        if (File.Exists(p)) File.Delete(p);
        var meta = LoadMeta();
        if (meta.Remove(id)) SaveMeta(meta);
    }
}
