using System.IO;

namespace EdgeWorkspace;

/// <summary>
/// FileSystemWatcher 封装：工作区任何变更 → 防抖 300ms → PushFiles 回调。
/// 注意：部分卷（如本机 F: 新加卷）不推送文件系统通知（PowerShell 原生 FSW
/// 同样收不到事件），因此 Main 另有 1.5s 轮询兜底；本类在支持的卷上提供即时性。
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private readonly Action _onChanged;

    public WorkspaceWatcher(string path, Action onChanged)
    {
        _onChanged = onChanged;
        _debounce.Tick += (_, _) => { _debounce.Stop(); _onChanged(); };

        _fsw.Path = path;
        _fsw.IncludeSubdirectories = false;
        _fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                          | NotifyFilters.LastWrite | NotifyFilters.Size;
        _fsw.Created += (_, _) => Debounce();
        _fsw.Deleted += (_, _) => Debounce();
        _fsw.Renamed += (_, _) => Debounce();
        _fsw.Changed += (_, _) => Debounce();
        _fsw.Error += (_, _) => Debounce(); // 缓冲溢出时全量重扫
    }

    public void Start() => _fsw.EnableRaisingEvents = true;
    public void Stop() => _fsw.EnableRaisingEvents = false;
    private void Debounce() { _debounce.Stop(); _debounce.Start(); }
    public void Dispose() { _fsw.Dispose(); _debounce.Dispose(); }
}
