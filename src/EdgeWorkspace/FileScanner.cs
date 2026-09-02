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
    public long size { get; init; }
    public string mtime { get; init; } = "";
}

/// <summary>
/// 扫描工作区目录并产出条目列表；kind 判定口径见 SPEC §6。
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

    public static List<FileEntry> Scan(string path)
    {
        var items = new List<FileEntry>();
        if (!Directory.Exists(path)) return items;

        var dir = new DirectoryInfo(path);
        foreach (var d in dir.EnumerateDirectories())
        {
            if (d.Attributes.HasFlag(FileAttributes.Hidden)) continue;
            items.Add(new FileEntry
            {
                name = d.Name,
                isFolder = true,
                ext = "",
                kind = "folder",
                size = 0,
                mtime = d.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
            });
        }
        foreach (var f in dir.EnumerateFiles())
        {
            if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;
            var ext = f.Extension.TrimStart('.');
            items.Add(new FileEntry
            {
                name = f.Name,
                isFolder = false,
                ext = ext,
                kind = KindFor(ext),
                size = f.Length,
                mtime = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
            });
        }
        // Rainmeter 版口径：最近修改在前
        items.Sort((a, b) => string.CompareOrdinal(b.mtime, a.mtime));
        return items;
    }
}
