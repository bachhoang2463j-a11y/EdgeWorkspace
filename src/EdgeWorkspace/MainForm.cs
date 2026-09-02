using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace EdgeWorkspace;

/// <summary>
/// 主面板窗口：右缘停靠、置顶、WebView2 UI 宿主。
/// P3：贴边唤出 / 移开收起 / Ctrl+Shift+Z / 钉住 / 滑入滑出动画。
/// </summary>
public class MainForm : Form
{
    private Microsoft.Web.WebView2.WinForms.WebView2 _web = null!;
    private CoreWebView2? _core;
    private WorkspaceWatcher? _watcher;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer _edgeWatch = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _closeDelay = new() { Interval = 600 };
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private bool _bridgeReady;
    private bool _pinned;
    private bool _dragOver;      // P4 拖放期间挂起收起
    private bool _menuOpen;      // 右键菜单打开期间挂起收起（P4）
    private string _lastSignature = "";
    private DateTime _animStart;
    private int _animFrom, _animTo;
    private bool _animOpening;
    private int _parkedX;        // 完全滑出屏幕的 X

    private static readonly string WorkspacePath = "D:\\Workspace_Temp";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bridge.log");

    private const int SlideMs = 180;
    private const int EdgeZone = 8;

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

        Load += async (_, _) =>
        {
            DockRight();
            _parkedX = Right; // 初始即停靠（P3 后启动隐藏，先保持可见便于联调）
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

            // P3: 唤出/收起
            _edgeWatch.Tick += (_, _) => EdgeWatchTick();
            _closeDelay.Tick += (_, _) => CloseDelayTick();
            _anim.Tick += (_, _) => AnimTick();
            _edgeWatch.Start();
            Native.RegisterAppHotKey(Handle);
        };

        // 移开判定（600ms 宽限）
        MouseLeave += (_, _) => _closeDelay.Start();
        MouseEnter += (_, _) => _closeDelay.Stop();

        // 失焦判定（300ms 后复核：动画进行中则等动画结束再判）
        Deactivate += (_, _) => BeginInvoke(async () =>
        {
            await Task.Delay(300);
            if (_anim.Enabled) { _pendingCloseCheck = true; return; }
            CheckShouldCollapse();
        });

        FormClosed += (_, _) => Native.UnregisterAppHotKey(Handle);
    }

    // ---------- P3: 唤出与收起 ----------

    private bool _opening;
    private bool _pendingCloseCheck;

    private void CheckShouldCollapse()
    {
        if (_pinned || _dragOver || _menuOpen || !Visible || _opening) return;
        var (x, y) = Native.Cursor();
        if (!IsSelfAt(x, y)) BeginCollapse();
    }

    private void EdgeWatchTick()
    {
        var (x, y) = Native.Cursor();

        // 面板可见时的兜底：光标既不在面板内、也不在右缘触发带 -> 收起。
        // 这是 MouseLeave 之外的保险（合成鼠标事件可能不触发 WinForms 路径）。
        if (Visible && !_pinned && !_dragOver && !_menuOpen && !_anim.Enabled)
        {
            var inPanel = IsSelfAt(x, y);
            var atTrigger = x >= Screen.PrimaryScreen!.WorkingArea.Right - EdgeZone;
            if (!inPanel && !atTrigger)
            {
                BeginCollapse();
                return;
            }
        }

        if (_opening || Visible) return;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var atEdge = x >= wa.Right - EdgeZone && y > wa.Top && y < wa.Bottom;
        if (!atEdge) return;
        if (Native.IsForegroundFullScreen(Handle)) return;   // 全屏应用不打扰
        if (!Native.IsDesktopAt(x, y) && !IsSelfAt(x, y)) return; // 窗口盖住右缘时不唤出

        BeginExpand();
        // 贴边唤出（鼠标）默认展示白板；拖放唤出时 P4 会切回全部
        SetFrontTab("whiteboard");
    }

    private bool IsSelfAt(int x, int y) => Visible && x >= Left && x < Right && y >= Top && y < Bottom;

    private void CloseDelayTick()
    {
        _closeDelay.Stop();
        if (_pinned || _dragOver || _menuOpen || !Visible) return;
        // 光标离开窗口且未回来
        var (x, y) = Native.Cursor();
        if (!IsSelfAt(x, y)) BeginCollapse();
    }

    private void BeginExpand()
    {
        _opening = true;
        _closeDelay.Stop();
        if (!Visible)
        {
            Visible = true;
            Left = _parkedX;
        }
        StartAnim(_parkedX, _openX, opening: true);
    }

    private void BeginCollapse()
    {
        StartAnim(Left, _parkedX, opening: false);
    }

    private int _openX;

    private void StartAnim(int from, int to, bool opening)
    {
        // 动画进行中反转方向：从当前位置取齐，不丢弃指令
        _anim.Stop();
        _animFrom = Left; _animTo = to; _animOpening = opening;
        _animStart = DateTime.UtcNow;
        _anim.Start();
    }

    private void AnimTick()
    {
        var t = (DateTime.UtcNow - _animStart).TotalMilliseconds / SlideMs;
        if (t >= 1.0)
        {
            _anim.Stop();
            Left = _animTo;
            if (!_animOpening)
            {
                Visible = false;   // 滑出后完全隐藏，恢复边缘监视
                _opening = false;
                // 收起时告诉前端重置为白板，下次贴边唤出所见即所得
            }
            else
            {
                _opening = false;
                if (_pendingCloseCheck) { _pendingCloseCheck = false; CheckShouldCollapse(); }
            }
            return;
        }
        // 缓出（ease-out quadratic）
        var e = 1 - (1 - t) * (1 - t);
        Left = _animFrom + (int)((_animTo - _animFrom) * e);
    }

    // WndProc: 热键
    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt64() == 0xBEEF)
        {
            if (Visible) BeginCollapse();
            else { BeginExpand(); SetFrontTab("whiteboard"); }
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>通知前端切换 Tab（贴边唤出 -> 白板；P4 拖放 -> all）。</summary>
    private void SetFrontTab(string tab)
    {
        if (_core is null || !_bridgeReady) return;
        var json = JsonSerializer.Serialize(new { type = "setTab", tab }, JsonOpts);
        _core.PostWebMessageAsJson(json);
    }

    // ---------- P1: 文件列表 ----------

    private void StartWatcher()
    {
        Directory.CreateDirectory(WorkspacePath);
        _watcher = new WorkspaceWatcher(WorkspacePath, PushFiles);
        _watcher.Start();
    }

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
                case "setPinned":
                    {
                        _pinned = msg.RootElement.GetProperty("pinned").GetBoolean();
                        Log("setPinned: " + _pinned);
                        // 取消钉住：光标不在面板内则立即收起（无需等复核宽限）
                        if (!_pinned) BeginInvoke(CheckShouldCollapse);
                        break;
                    }
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
        _openX = wa.Right - width;
        Location = new Point(_openX, wa.Top);
        Size = new Size(width, wa.Height);
    }
}
