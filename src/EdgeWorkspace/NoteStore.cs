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

    /// <summary>标题索引（id -> title）。缺失或损坏时返回空表，不影响正文。</summary>
    private static Dictionary<string, string> LoadTitles()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch { return new(); }
    }

    private static void SaveTitles(Dictionary<string, string> titles) =>
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(titles));

    /// <summary>全部便签，按修改时间倒序（最新在前）。</summary>
    public static List<NoteInfo> LoadAll()
    {
        EnsureDir();
        var titles = LoadTitles();
        var list = new List<NoteInfo>();
        foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("*.txt"))
        {
            var id = Path.GetFileNameWithoutExtension(f.Name);
            list.Add(new NoteInfo(
                id,
                titles.GetValueOrDefault(id, ""),
                File.ReadAllText(f.FullName),
                f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Mtime, a.Mtime));
        return list;
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
        var titles = LoadTitles();
        titles[id] = title;
        SaveTitles(titles);
        var p = PathFor(id);
        if (File.Exists(p)) File.SetLastWriteTimeUtc(p, DateTime.UtcNow);
    }

    public static void Delete(string id)
    {
        var p = PathFor(id);
        if (File.Exists(p)) File.Delete(p);
        var titles = LoadTitles();
        if (titles.Remove(id)) SaveTitles(titles);
    }
}
