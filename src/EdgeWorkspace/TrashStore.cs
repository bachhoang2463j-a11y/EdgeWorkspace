using System.Text.Json;

namespace EdgeWorkspace;

/// <summary>
/// 工作区回收站（P9，SPEC §9.2）：删除的文件移入隐藏文件夹 .ews-trash/（存储名 id_原名），
/// 清单 trash.json（id/原名/原抽屉/时间）。恢复 = 移回原抽屉（抽屉已不存在则回根目录，
/// 重名自动编号）。Scanner 跳过隐藏目录，回收站不进文件网格。
/// </summary>
public static class TrashStore
{
    public sealed class TrashItem
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string drawer { get; set; } = "";   // ""=根目录（未分类）
        public string deletedAt { get; set; } = "";
    }

    private static string Dir => Path.Combine(MainForm.WorkspacePath, ".ews-trash");
    private static string ManifestPath => Path.Combine(Dir, "trash.json");

    private static List<TrashItem> Load()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new();
            return JsonSerializer.Deserialize<List<TrashItem>>(File.ReadAllText(ManifestPath)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(List<TrashItem> items)
    {
        Directory.CreateDirectory(Dir);
        File.SetAttributes(Dir, FileAttributes.Hidden);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(items));
    }

    public static List<TrashItem> List() => Load();

    /// <summary>移入回收站（fullPath 必须在工作区内）。</summary>
    public static void Add(string fullPath, string? drawer)
    {
        Directory.CreateDirectory(Dir);
        File.SetAttributes(Dir, FileAttributes.Hidden);
        var items = Load();
        var item = new TrashItem
        {
            id = DateTime.Now.ToString("yyyyMMddHHmmssfff"),
            name = Path.GetFileName(fullPath),
            drawer = drawer ?? "",
            deletedAt = DateTime.Now.ToString("MM-dd HH:mm"),
        };
        var stored = Path.Combine(Dir, item.id + "_" + item.name);
        if (Directory.Exists(fullPath)) Directory.Move(fullPath, stored);
        else File.Move(fullPath, stored);
        items.Add(item);
        Save(items);
    }

    public static void Restore(string id)
    {
        var items = Load();
        var item = items.FirstOrDefault(x => x.id == id);
        if (item is null) return;
        var stored = Path.Combine(Dir, item.id + "_" + item.name);
        if (File.Exists(stored) || Directory.Exists(stored))
        {
            var drawerDir = item.drawer == "" ? MainForm.WorkspacePath
                                              : Path.Combine(MainForm.WorkspacePath, item.drawer);
            Directory.CreateDirectory(drawerDir);
            var dest = Path.Combine(drawerDir, item.name);
            if (File.Exists(dest) || Directory.Exists(dest)) dest = FileOps.UniqueName(drawerDir, item.name);
            if (Directory.Exists(stored)) Directory.Move(stored, dest);
            else File.Move(stored, dest);
        }
        items.Remove(item);
        Save(items);
    }

    public static void Empty()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(Dir))
        {
            if (File.Exists(f)) File.Delete(f);
            else if (Directory.Exists(f)) Directory.Delete(f, true);
        }
        Save(new());
    }
}
