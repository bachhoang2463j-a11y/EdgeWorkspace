using System.IO;

namespace EdgeWorkspace;

/// <summary>
/// 白板便签后端：notes/ 目录下每个 .txt 即一张便签（无上限）。
/// 文件名 = 便签 id（note1.txt、note2.txt...新建顺延）；内容 = 便签文本。
/// 前端负责展示与编辑，本类只做目录枚举与增删。
/// </summary>
public static class NoteStore
{
    public sealed record NoteInfo(string Id, string Content, string Mtime);

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "notes");

    public static void EnsureDir() => Directory.CreateDirectory(Dir);

    private static string PathFor(string id) => Path.Combine(Dir, id + ".txt");

    /// <summary>全部便签，按修改时间倒序（最新在前）。</summary>
    public static List<NoteInfo> LoadAll()
    {
        EnsureDir();
        var list = new List<NoteInfo>();
        foreach (var f in new DirectoryInfo(Dir).EnumerateFiles("*.txt"))
        {
            var content = File.ReadAllText(f.FullName);
            list.Add(new NoteInfo(
                Path.GetFileNameWithoutExtension(f.Name),
                content,
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

    public static void Delete(string id)
    {
        var p = PathFor(id);
        if (File.Exists(p)) File.Delete(p);
    }
}
