namespace EdgeWorkspace;

/// <summary>
/// 工作区文件条目（SPEC §3.1）。
/// </summary>
public sealed class FileEntry
{
    public string name { get; init; } = "";
    public bool isFolder { get; init; }
    public string ext { get; init; } = "";
    public string kind { get; init; } = "other";
    public string? drawer { get; init; }   // null=根目录散文件（未分类）；否则=所在抽屉的相对路径（"父/子"，'/' 分隔）
    public bool pinned { get; set; }       // meta.json 合并（FileMetaStore.Apply）
    public int openCount { get; set; }
    public long size { get; init; }
    public string mtime { get; init; } = "";
}

/// <summary>扫描结果：条目 + 抽屉路径清单（含所有层级）。</summary>
public sealed record ScanResult(List<FileEntry> Items, List<string> Drawers);

/// <summary>
/// 扫描工作区目录并产出条目列表；kind 判定口径见 SPEC §6。
/// 递归模型（v2 柱1）：任何层级的文件夹都是抽屉（分组），直属文件归该分组；
/// 隐藏与重解析点（符号链接/junction，防循环）跳过。文件夹不再有条目级卡片。
/// </summary>
public static class FileScanner
{
    private static readonly Dictionary<string, string> ExtToKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["doc"] = "txt,log,md,markdown,ini,cfg,conf,yml,yaml,nfo,tres,doc,docx,rtf,odt,wps,xls,xlsx,csv,ods,ppt,pptx,odp,pdf,js,ts,jsx,tsx,py,lua,json,xml,html,htm,css,scss,c,cpp,h,cs,java,go,rs,php,rb,sh,ps1,vbs,sql,toml",
        ["image"] = "jpg,jpeg,png,gif,bmp,webp,tif,tiff,ico,svg,psd,heic",
        ["video"] = "mp4,mkv,avi,mov,wmv,flv,webm,m4v,rmvb",
        ["audio"] = "mp3,wav,flac,ogg,aac,m4a,wma",
        ["archive"] = "zip,rar,7z,tar,gz,bz2,xz,iso",
        ["app"] = "exe,msi,bat,cmd,lnk,dll,appx",
    };

    static FileScanner()
    {
        // 反转成 ext -> kind 查找表
        foreach (var (kind, exts) in ExtToKind.ToArray())
        {
            ExtToKind.Remove(kind);
            foreach (var ext in exts.Split(','))
                ExtToKind[ext.Trim()] = kind;
        }
    }

    public static string KindFor(string ext) =>
        ExtToKind.TryGetValue(ext.TrimStart('.'), out var kind) ? kind : "other";

    public static ScanResult Scan(string path)
    {
        var items = new List<FileEntry>();
        var drawers = new List<string>();
        if (Directory.Exists(path))
            Walk(new DirectoryInfo(path), "");

        drawers.Sort(StringComparer.Ordinal);
        // 最近修改在前（组内序 = 全局序，Rainmeter 口径）
        items.Sort((a, b) => string.CompareOrdinal(b.mtime, a.mtime));
        return new(items, drawers);

        // 递归：文件夹 -> 抽屉路径；直属文件 -> 归当前分组
        void Walk(DirectoryInfo dir, string rel)
        {
            foreach (var d in dir.EnumerateDirectories())
            {
                if (d.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // 符号链接/junction：不跟随，防循环
                var dRel = rel == "" ? d.Name : rel + "/" + d.Name;
                drawers.Add(dRel);
                Walk(d, dRel);
            }
            foreach (var f in dir.EnumerateFiles())
            {
                if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;
                var ext = f.Extension.TrimStart('.');
                items.Add(new FileEntry
                {
                    name = f.Name,
                    ext = ext,
                    kind = KindFor(ext),
                    drawer = rel == "" ? null : rel,
                    size = f.Length,
                    mtime = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                });
            }
        }
    }
}
