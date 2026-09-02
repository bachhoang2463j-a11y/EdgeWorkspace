using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace EdgeWorkspace;

/// <summary>
/// 主面板窗口：右缘停靠、置顶、WebView2 UI 宿主。
/// P1：文件扫描 + FSW 即时推送 + 轮询兜底（F: 卷不推送通知，见 WorkspaceWatcher 注释）。
/// </summary>
public class MainForm : Form
{
    private Microsoft.Web.WebView2.WinForms.WebView2 _web = null!;
    private CoreWebView2? _core;
    private WorkspaceWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };
    private bool _bridgeReady;
    private string _lastSignature = "";

    private static readonly string WorkspacePath = "D:\\Workspace_Temp";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bridge.log");

    private static void Log(string line) =>
        File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);

    public MainForm()
    {
        Text = "EdgeWorkspace";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;

        Deactivate += (_, _) => { /* P3: 收起判定 */ };

        Load += async (_, _) =>
        {
            DockRight();
            _web = new Microsoft.Web.WebView2.WinForms.WebView2
            {
                Dock = DockStyle.Fill,
                AllowExternalDrop = false, // 拖放由窗体 OLE 处理（P4）
            };
            Controls.Add(_web);

            var dataDir = Path.Combine(AppContext.BaseDirectory, "WebView2Data");
            Directory.CreateDirectory(dataDir);
            await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dataDir));
            _core = _web.CoreWebView2;

            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _core.SetVirtualHostNameToFolderMapping(
                "app.local", root, CoreWebView2HostResourceAccessKind.Allow);

            _core.WebMessageReceived += OnWebMessage;
            _core.Navigate("https://app.local/index.html");

            StartWatcher();
            _poll.Tick += (_, _) => PushFilesIfChanged();
            _poll.Start();
        };
    }

    // ---------- P1: 文件列表 ----------

    private void StartWatcher()
    {
        Directory.CreateDirectory(WorkspacePath);
        _watcher = new WorkspaceWatcher(WorkspacePath, PushFiles);
        _watcher.Start();
    }

    /// <summary>轮询兜底：目录签名变化才推送。</summary>
    private void PushFilesIfChanged()
    {
        var sig = ComputeSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        PushFiles();
    }

    private string ComputeSignature()
    {
        try
        {
            var dir = new DirectoryInfo(WorkspacePath);
            if (!dir.Exists) return "missing";
            long acc = 0;
            foreach (var f in dir.EnumerateFileSystemInfos())
                acc ^= f.Name.GetHashCode() ^ f.LastWriteTimeUtc.Ticks ^ (long)(f is FileInfo fi ? fi.Length : -1);
            return acc.ToString("X");
        }
        catch { return "error"; }
    }

    /// <summary>扫描工作区并把文件列表推给前端。</summary>
    private void PushFiles()
    {
        var items = FileScanner.Scan(WorkspacePath);
        if (_core is null || !_bridgeReady) return;
        var json = JsonSerializer.Serialize(new { type = "files", total = items.Count, items }, JsonOpts);
        BeginInvoke(() => _core.PostWebMessageAsJson(json));
        Log("PushFiles: posted " + items.Count + " items");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // WebMessageAsJson：字符串消息会是 "\"...\"" 的 JSON 字符串包装
            var raw = e.WebMessageAsJson;
            if (raw.StartsWith('"'))
                raw = JsonSerializer.Deserialize<string>(raw) ?? "";

            var msg = JsonDocument.Parse(raw);
            var type = msg.RootElement.GetProperty("type").GetString();
            switch (type)
            {
                case "ready":
                    _bridgeReady = true;
                    _lastSignature = ComputeSignature();
                    PushFiles();
                    break;
                case "refresh":
                    _lastSignature = ComputeSignature();
                    PushFiles();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log("WebMessage error: " + ex.Message);
        }
    }

    /// <summary>停靠到工作区右缘（可见状态）。</summary>
    public void DockRight()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int width = Math.Max(420, wa.Width / 3);
        Location = new Point(wa.Right - width, wa.Top);
        Size = new Size(width, wa.Height);
    }
}
