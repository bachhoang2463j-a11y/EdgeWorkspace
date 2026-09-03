using System.Diagnostics;
using System.Text;
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
    private CoreWebView2Environment? _env;   // P6: 独立便签窗口复用
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

    /// <summary>工作区路径（P12 起可经设置热切换：重映射 files.local + 重建 watcher）。</summary>
    internal static string WorkspacePath { get; private set; } = "D:\\Workspace_Temp";

    /// <summary>文件完整路径：drawer=null 为工作区根目录，否则 工作区/抽屉路径/文件名。
    /// drawer 是 '/' 分隔的相对路径；校验不越出工作区。</summary>
    private static string FullPath(string? drawer, string name)
    {
        var rel = string.IsNullOrEmpty(drawer) ? name : drawer + "/" + name;
        var full = Path.GetFullPath(Path.Combine(WorkspacePath, rel));
        if (!full.StartsWith(WorkspacePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径越界: " + rel);
        return full;
    }

    /// <summary>读取消息里的 drawer 字段（缺省/为 null 时返回 null = 根目录）。</summary>
    private static string? GetDrawer(JsonElement root) =>
        root.TryGetProperty("drawer", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bridge.log");

    private const int SlideMs = 180;
    private const int EdgeZone = 8;

    internal static void Log(string line) =>
        File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);

    static MainForm() => FileOps.LogLine += Log;

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
            // P12：配置先行（工作区路径在映射/监视器建立前生效；自启回读注册表真实态）
            if (!string.IsNullOrEmpty(ConfigStore.Current.workspacePath))
                WorkspacePath = ConfigStore.Current.workspacePath;
            ConfigStore.Current.autostart = AutoStartEnabled();

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
            _env = await CoreWebView2Environment.CreateAsync(null, dataDir); // 独立便签窗口共用（P6）
            await _web.EnsureCoreWebView2Async(_env);
            _core = _web.CoreWebView2;
            // OLE 放置目标不沿父链上溯：Chromium 子窗口必须逐个注册我们的目标（FileDropTarget）。
            // 导航完成后渲染器可能重建子窗口，重跑一遍。
            ApplyFileDropTargets();
            _core.NavigationCompleted += (_, _) => ApplyFileDropTargets();

            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _core.SetVirtualHostNameToFolderMapping(
                "app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            // 工作区文件直读：图片/视频缩略图（P7）
            _core.SetVirtualHostNameToFolderMapping(
                "files.local", WorkspacePath, CoreWebView2HostResourceAccessKind.Allow);
            _core.WebMessageReceived += OnWebMessage;
            _core.Navigate("https://app.local/index.html");

            ApplyTheme(ConfigStore.Current.theme);   // P13：初始化主题（webview 透底 + 亚克力）

            StartWatcher();
            _poll.Tick += (_, _) => PushFilesIfChanged();
            _poll.Start();

            // P3: 唤出/收起
            _edgeWatch.Tick += (_, _) => EdgeWatchTick();
            _closeDelay.Tick += (_, _) => CloseDelayTick();
            _anim.Tick += (_, _) => AnimTick();
            _dropTimeout.Tick += (_, _) => CompletePendingDrop(null);   // P9: 命中回执超时 -> 未分类
            _pasteWatch.Tick += (_, _) => PasteWatchTick();             // P9: 面板可见期 Ctrl+V 检测
            _edgeWatch.Start();
            _pasteWatch.Start();   // 启动即停靠可见
            ApplyHotkeyFromConfig();

            // P4/P7: 拖入收纳走自定义 OLE 放置目标（见 ApplyFileDropTargets），
            // 不用窗体 AllowDrop——OLE 不沿父链上溯，WebView2 盖住窗体后它收不到事件。
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
    private DateTime _lastExpandDone = DateTime.MinValue;

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
        // 唤出后前 1.2s 不收（给用户把鼠标从屏幕边缘挪进面板的时间）；
        // 按住左键（拖拽进行中）也不收——用户可能正把文件拖向面板。
        if (Visible && !_pinned && !_dragOver && !_menuOpen && !_anim.Enabled
            && !Native.IsMouseLeftDown()
            && (DateTime.UtcNow - _lastExpandDone).TotalMilliseconds > 1200)
        {
            var inPanel = IsSelfAt(x, y);
            var atTrigger = x >= Screen.PrimaryScreen!.WorkingArea.Right - EdgeZone;
            if (!inPanel && !atTrigger)
            {
                Log("watchdog collapse: cursor=" + x + "," + y + " rect=" + Left + ".." + Right);
                BeginCollapse();
                return;
            }
        }

        if (_opening || Visible) return;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        var atEdge = x >= wa.Right - EdgeZone && y > wa.Top && y < wa.Bottom;
        if (!atEdge) return;

        // 拖拽中贴右缘：面板隐藏时收不到 DragEnter（右缘没有窗口可命中），
        // 必须由这里唤出。此时右缘下方是拖拽源窗口，绕过桌面/全屏检查。
        if (Native.IsMouseLeftDown())
        {
            Log("drag summon: cursor=" + x + "," + y + " btn=1");
            BeginExpand();
            SetFrontTab("all");   // 拖放视图：全部
            return;
        }

        if (Native.IsForegroundFullScreen(Handle)) return;   // 全屏应用不打扰
        if (!Native.IsDesktopAt(x, y) && !IsSelfAt(x, y)) return; // 窗口盖住右缘时不唤出（有意设计）

        Log("edge summon: cursor=" + x + "," + y);
        BeginExpand();
        // 贴边唤出（鼠标）：上半屏 -> 文件【全部】，下半屏 -> 白板便签；拖放唤出固定【全部】
        SetFrontTab(y < (wa.Top + wa.Bottom) / 2 ? "all" : "whiteboard");
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
        _pasteWatch.Start();   // 面板可见期开启 Ctrl+V 检测（P9）
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
        UpdateAcrylic();   // P13：滑动期间关亚克力（Win10 移动冻结问题）
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
            UpdateAcrylic();   // P13：动画落定，恢复亚克力
            if (!_animOpening)
            {
                Visible = false;   // 滑出后完全隐藏，恢复边缘监视
                _opening = false;
                _pasteWatch.Stop();
                // 收起时告诉前端重置为白板，下次贴边唤出所见即所得
            }
            else
            {
                _opening = false;
                _lastExpandDone = DateTime.UtcNow;
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
            else
            {
                BeginExpand();
                SetFrontTab("all");   // 热键固定唤出【全部】；鼠标贴边才按上下半屏分流
                Activate();   // 无视当前焦点：抢到前台并置顶（TopMost）
            }
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

    /// <summary>P4/P9: 从面板拖文件出去（OLE 源，支持多文件）。DoDragDrop 自带消息循环，会阻塞到松手。
    /// 落回自己面板时由 FileDropTarget 命中抽屉完成组内移动（见 CompletePendingDrop）。</summary>
    private void StartDragOut(string[] paths)
    {
        var valid = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (valid.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, valid);
        _dragOver = true;
        try { _web.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move); }
        finally
        {
            _dragOver = false;
            Task.Delay(600).ContinueWith(_ => BeginInvoke(CheckShouldCollapse));
        }
    }

    // ---------- P4/P7/P9: 拖入收纳（自定义 OLE 放置目标，逐窗口注册） ----------

    private FileDropTarget? _fileDrop;

    /// <summary>把文件放置目标注册到窗体与 WebView2 全部子窗口（导航后子窗口重建需重跑）。</summary>
    private void ApplyFileDropTargets()
    {
        _fileDrop ??= new FileDropTarget(
            files =>
            {
                _dragOver = files;   // 拖放期间挂起收起
                if (files)
                {
                    if (!Visible) BeginExpand();
                    SetFrontTab("all");   // 拖放视图：全部
                }
            },
            () => { _dragOver = false; PostToJs(new { type = "dragHover", x = -1, y = -1 }); },
            pt => OnOleDragOver(pt),
            (files, pt) => OnOleDrop(files, pt));
        Native.SetDropTarget(Handle, _fileDrop);
        Native.SetDropTarget(_web.Handle, _fileDrop);
        foreach (var h in Native.CollectDescendants(_web.Handle))
            Native.SetDropTarget(h, _fileDrop);
    }

    // ---------- P10: 抽屉改名（文件夹重命名 + 数据迁移） ----------

    /// <summary>抽屉改名（路径语义）：目录重命名；meta.json 键前缀与折叠状态（含后代）跟随，config 重推。</summary>
    private void RenameDrawer(string from, string to)
    {
        if (from == "" || to == "" || from == to) return;
        // 路径分段校验（'/' 分隔，逐段按文件名合法性）
        if (!to.Split('/').All(s => s != "" && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0))
        {
            Log("drawerRename: 非法名称「" + to + "」");
            return;
        }
        var src = Path.Combine(WorkspacePath, from.Replace('/', Path.DirectorySeparatorChar));
        var dest = Path.Combine(WorkspacePath, to.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(src) || File.Exists(dest) || Directory.Exists(dest))
        {
            Log("drawerRename: 源不存在或目标已存在「" + to + "」");
            return;
        }
        try
        {
            Directory.Move(src, dest);
            FileMetaStore.MigrateDrawer(from, to);   // 置顶/常用统计跟随（含后代键前缀）
            ConfigStore.Update(c =>
            {
                void MigrateList(List<string> list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var k = list[i];
                        if (k == from) list[i] = to;
                        else if (k.StartsWith(from + "/", StringComparison.Ordinal))
                            list[i] = to + "/" + k[(from.Length + 1)..];   // 后代跟随
                    }
                }
                MigrateList(c.collapsedDrawers);
                MigrateList(c.drawerOrder);   // 手动排序里的路径同样跟随
                // emoji 标记：字典键同样迁移
                var oldIcons = c.drawerIcons.Where(k => k.Key == from || k.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList();
                foreach (var kv in oldIcons) c.drawerIcons.Remove(kv.Key);
                foreach (var kv in oldIcons)
                    c.drawerIcons[kv.Key == from ? to : to + "/" + kv.Key[(from.Length + 1)..]] = kv.Value;
            });
            _lastSignature = ComputeSignature();
            PushFiles();
            PostToJs(new { type = "config", config = ConfigStore.Current });
        }
        catch (Exception ex)
        {
            Log("drawerRename failed: " + ex.Message);
        }
    }

    // ---------- P11: 悬浮预览文本读取 ----------

    private static Encoding? _gb;
    private static Encoding Gb18030()
    {
        if (_gb is null)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gb = Encoding.GetEncoding("GB18030");
        }
        return _gb;
    }

    /// <summary>文本解码：BOM 优先（UTF-8/UTF-16 LE/BE），否则严格 UTF-8，失败回退 GB18030
    /// （中文 Windows 记事本默认 ANSI/GBK）。</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Gb18030().GetString(bytes); }
    }

    // ---------- P13: 主题（亮色 / 毛玻璃） ----------

    private string _theme = "white";
    private bool _acrylicOn;
    internal string CurrentTheme => _theme;

    /// <summary>应用主题：webview 透底 + Win10 亚克力 + 广播前端/便签窗口。</summary>
    private void ApplyTheme(string theme)
    {
        _theme = theme is "white" or "acrylic" or "eye" ? theme : "white";
        var glass = _theme == "acrylic";
        _web.DefaultBackgroundColor = glass ? Color.Transparent : Color.White;
        UpdateAcrylic();
        PostToJs(new { type = "theme", theme = _theme });
        foreach (var w in _noteWindows.Values)
            w.SetTheme(_theme);
    }

    /// <summary>亚克力实际生效条件：毛玻璃主题 + 面板可见 + 不在滑动动画中
    ///（Win10 亚克力在窗口移动时会冻结，动画期间先关，落定再开）。</summary>
    private void UpdateAcrylic()
    {
        var on = _theme == "acrylic" && Visible && !_anim.Enabled;
        if (on == _acrylicOn) return;
        _acrylicOn = on;
        Native.SetAcrylic(Handle, on);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_acrylicOn) return;   // 亚克力激活时不铺底色，让 DWM 模糊透出
        base.OnPaintBackground(e);
    }

    // ---------- P13: 呼出快捷键自定义 ----------

    private uint _hkMods = Native.MOD_CONTROL | Native.MOD_SHIFT;
    private int _hkVk = 0x5A;   // VK_Z

    private static uint ModFlags(IEnumerable<string> mods)
    {
        uint f = 0;
        foreach (var m in mods)
            f |= m switch { "alt" => 1u, "ctrl" => 2u, "shift" => 4u, "win" => 8u, _ => 0u };
        return f;
    }

    /// <summary>按 config 注册呼出热键（空配置 = 默认 Ctrl+Shift+Z）。</summary>
    private void ApplyHotkeyFromConfig()
    {
        var c = ConfigStore.Current;
        _hkVk = c.hotkeyKey > 0 ? c.hotkeyKey : 0x5A;
        _hkMods = c.hotkeyMods.Count > 0 ? ModFlags(c.hotkeyMods) : Native.MOD_CONTROL | Native.MOD_SHIFT;
        Native.RegisterAppHotKey(Handle, _hkMods, (uint)_hkVk);
    }

    // ---------- P12: 工作区热切换 / 开机自启 ----------

    /// <summary>热切换工作区：重映射 files.local 虚拟主机 + 重建 watcher + 全量重推。</summary>
    private void ApplyWorkspacePath(string newPath)
    {
        WorkspacePath = newPath;
        try
        {
            _core?.SetVirtualHostNameToFolderMapping("files.local", WorkspacePath,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex) { Log("remap files.local failed: " + ex.Message); }
        _watcher?.Dispose();
        _watcher = new WorkspaceWatcher(WorkspacePath, PushFiles);
        _watcher.Start();
        _lastSignature = ComputeSignature();
        PushFiles();
        PostToJs(new { type = "config", config = ConfigStore.Current });
    }

    private static bool AutoStartEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("EdgeWorkspace") is not null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool on)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (on) key.SetValue("EdgeWorkspace", "\"" + Application.ExecutablePath + "\"");
            else key.DeleteValue("EdgeWorkspace", throwOnMissingValue: false);
        }
        catch (Exception ex) { Log("SetAutoStart failed: " + ex.Message); }
    }

    // ---------- P9: 落点命中（OLE 屏幕坐标 -> CSS 坐标 -> JS 抽屉命中 -> 回执移动） ----------

    private string[]? _pendingDrop;
    private readonly System.Windows.Forms.Timer _dropTimeout = new() { Interval = 1500 };
    private DateTime _lastHoverFwd = DateTime.MinValue;

    private void PostToJs(object payload)
    {
        if (_core is null || !_bridgeReady) return;
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    /// <summary>OLE 屏幕坐标 -> WebView CSS 像素（本机 200% 缩放下 x/2）。</summary>
    private (int X, int Y) ToCssPoint(Native.POINTL pt)
    {
        var scale = DeviceDpi / 96.0;
        return ((int)((pt.X - Left) / scale), (int)((pt.Y - Top) / scale));
    }

    private void OnOleDragOver(Native.POINTL pt)
    {
        if ((DateTime.UtcNow - _lastHoverFwd).TotalMilliseconds < 80) return;   // 节流
        _lastHoverFwd = DateTime.UtcNow;
        var (x, y) = ToCssPoint(pt);
        PostToJs(new { type = "dragHover", x, y });
    }

    private void OnOleDrop(string[] files, Native.POINTL pt)
    {
        _dragOver = false;
        var (x, y) = ToCssPoint(pt);
        _pendingDrop = files;
        _dropTimeout.Stop();
        _dropTimeout.Start();   // JS 无回执时按未分类兜底
        PostToJs(new { type = "hitTest", x, y });
    }

    /// <summary>落点命中完成：外部拖入 = 收纳到目标抽屉；自落 = 抽屉间移动（MoveInto 统一处理）。</summary>
    private void CompletePendingDrop(string? drawer)
    {
        _dropTimeout.Stop();
        if (_pendingDrop is not { } files) return;
        _pendingDrop = null;
        Log("Drop: " + files.Length + " 个文件 -> " + (drawer ?? "未分类"));
        var targetDir = string.IsNullOrEmpty(drawer) ? WorkspacePath : Path.Combine(WorkspacePath, drawer);
        foreach (var f in files)
        {
            try { FileOps.MoveInto(this, f, targetDir); }
            catch (Exception ex) { Log("drop failed: " + f + " - " + ex.Message); }
        }
        _lastSignature = ComputeSignature();
        PushFiles();
    }

    /// <summary>P9: Ctrl+V 粘贴收纳（drawer = 光标下的抽屉分组）。优先文件（FileDrop，
    /// 来自资源管理器或面板自身的复制），其次截图/文本。</summary>
    private void SaveClipboard(string? drawer)
    {
        try
        {
            var targetDir = string.IsNullOrEmpty(drawer) ? WorkspacePath : Path.Combine(WorkspacePath, drawer);
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var f in Clipboard.GetFileDropList().Cast<string>())
                {
                    try { FileOps.CopyInto(this, f, targetDir); }
                    catch (Exception ex) { Log("paste file failed: " + f + " - " + ex.Message); }
                }
                PushFiles();
            }
            else if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                img?.Save(Path.Combine(targetDir, "截图 " + DateTime.Now.ToString("MMdd HHmmss") + ".png"),
                    System.Drawing.Imaging.ImageFormat.Png);
                PushFiles();
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
            {
                File.WriteAllText(Path.Combine(targetDir, "文本 " + DateTime.Now.ToString("MMdd HHmmss") + ".txt"), text);
                PushFiles();
            }
        }
        catch (Exception ex) { Log("clipboardSave failed: " + ex.Message); }
    }

    /// <summary>P9: 白板页 Ctrl+V —— 剪贴板直接变便签：文本即正文；截图存文件后以图片链接入便签；
    /// 复制的是文件时退回文件收纳。</summary>
    private void SaveClipboardAsNote()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) { SaveClipboard(null); return; }
            string content;
            if (Clipboard.ContainsImage())
            {
                var name = "截图 " + DateTime.Now.ToString("MMdd HHmmss") + ".png";
                using var img = Clipboard.GetImage();
                img?.Save(Path.Combine(WorkspacePath, name), System.Drawing.Imaging.ImageFormat.Png);
                content = "![截图](https://files.local/" + Uri.EscapeDataString(name) + ")";
                PushFiles();
            }
            else if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
            {
                content = text;
            }
            else return;

            var id = NoteStore.Create();
            NoteStore.Save(id, content);
            PushNotes();
        }
        catch (Exception ex) { Log("clipboardToNote failed: " + ex.Message); }
    }

    // P9: 面板可见期的 Ctrl+C / Ctrl+V / Del 检测（30ms 键态边沿；贴边唤出不抢焦点，键盘事件到不了
    // WebView，只能由 C# 侧检测后经 JS 分流）。光标在面板内才触发，避免用户在别的窗口操作时误伤。
    // Ctrl+C = 复制光标下的文件到剪贴板（FileDrop，可粘贴到资源管理器/面板）；
    // Ctrl+V = 粘贴收纳，JS 按当前视图与光标下的抽屉分组分流；
    // Del = 删除（选中组或光标下的文件）到系统回收站。
    private readonly System.Windows.Forms.Timer _pasteWatch = new() { Interval = 30 };
    private bool _pasteWasDown, _copyWasDown, _delWasDown;

    private void PasteWatchTick()
    {
        const int VK_CONTROL = 0x11, VK_V = 0x56, VK_C = 0x43, VK_DELETE = 0x2E;
        var ctrl = Native.IsKeyDown(VK_CONTROL);
        var v = ctrl && Native.IsKeyDown(VK_V);
        var c = ctrl && Native.IsKeyDown(VK_C);
        var del = Native.IsKeyDown(VK_DELETE);
        if ((v && !_pasteWasDown) || (c && !_copyWasDown) || (del && !_delWasDown))
        {
            var (cx, cy) = Native.Cursor();
            if (IsSelfAt(cx, cy))
            {
                var (x, y) = ToCssPoint(new Native.POINTL { X = cx, Y = cy });
                var type = v ? "pasteDetected" : c ? "copyDetected" : "delDetected";
                PostToJs(new { type, x, y });
            }
        }
        _pasteWasDown = v;
        _copyWasDown = c;
        _delWasDown = del;
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
        if (sig != _lastSignature)
        {
            _lastSignature = sig;
            PushFiles();
        }
        var nsig = ComputeNoteSignature();
        if (nsig != _lastNoteSignature)
        {
            _lastNoteSignature = nsig;
            // 编辑中的保存会写文件 -> 签名变化 -> 重推 -> 前端 textarea 重建会丢焦点。
            // 仅当便签"集合"变化（数量/大小）时才推；纯内容编辑由前端自行维持。
            PushNotesIfCountChanged();
        }
    }

    private string _lastNoteSignature = "";
    private int _lastNoteCount = -1;

    private string ComputeNoteSignature()
    {
        try
        {
            long acc = 0;
            foreach (var f in new DirectoryInfo(NoteStore.Dir).EnumerateFiles("*.txt"))
                acc ^= f.Name.GetHashCode() ^ f.LastWriteTimeUtc.Ticks ^ f.Length;
            return acc.ToString("X");
        }
        catch { return "error"; }
    }

    private void PushNotesIfCountChanged()
    {
        var notes = NoteStore.LoadAll();
        if (notes.Count == _lastNoteCount) return;
        _lastNoteCount = notes.Count;
        var json = JsonSerializer.Serialize(new { type = "notes", notes }, JsonOpts);
        if (_core is null || !_bridgeReady) return;
        BeginInvoke(() => _core.PostWebMessageAsJson(json));
        Log("PushNotes(count-based): posted " + notes.Count);
    }

    private string ComputeSignature()
    {
        try
        {
            var dir = new DirectoryInfo(WorkspacePath);
            if (!dir.Exists) return "missing";
            long acc = 0;
            WalkSig(dir);
            return acc.ToString("X");

            // 递归签名（与 FileScanner 同口径：跳过隐藏与重解析点，v2 柱1 嵌套抽屉）
            void WalkSig(DirectoryInfo d)
            {
                foreach (var f in d.EnumerateFileSystemInfos())
                {
                    acc ^= f.Name.GetHashCode() ^ f.LastWriteTimeUtc.Ticks ^ (long)(f is FileInfo fi ? fi.Length : -1);
                    if (f is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.Hidden)
                        && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        WalkSig(sub);
                }
            }
        }
        catch { return "error"; }
    }

    /// <summary>把便签列表推给前端（P5）。</summary>
    private void PushNotes()
    {
        if (_core is null || !_bridgeReady) return;
        var notes = NoteStore.LoadAll();
        var json = JsonSerializer.Serialize(new { type = "notes", notes }, JsonOpts);
        BeginInvoke(() => _core.PostWebMessageAsJson(json));
        Log("PushNotes: posted " + notes.Count);
    }

    private void PushFiles()
    {
        var result = FileScanner.Scan(WorkspacePath);
        foreach (var it in result.Items) FileMetaStore.Apply(it);
        if (_core is null || !_bridgeReady) return;
        var json = JsonSerializer.Serialize(
            new { type = "files", total = result.Items.Count, items = result.Items, drawers = result.Drawers }, JsonOpts);
        BeginInvoke(() => _core.PostWebMessageAsJson(json));
        Log("PushFiles: posted " + result.Items.Count + " items, " + result.Drawers.Count + " drawers");
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
                    PostToJs(new { type = "theme", theme = _theme });   // 主题先行（初始化时的推送被桥丢弃）
                    PushFiles();
                    PushNotes();
                    PostToJs(new { type = "config", config = ConfigStore.Current });
                    break;
                case "refresh":
                    _lastSignature = ComputeSignature();
                    PushFiles();
                    PushNotes();
                    PostToJs(new { type = "config", config = ConfigStore.Current });
                    break;
                case "setPinned":
                    {
                        _pinned = msg.RootElement.GetProperty("pinned").GetBoolean();
                        Log("setPinned: " + _pinned);
                        // 取消钉住：光标不在面板内则立即收起（无需等复核宽限）
                        if (!_pinned) BeginInvoke(CheckShouldCollapse);
                        break;
                    }
                case "openPath":
                    {
                        var name = msg.RootElement.GetProperty("name").GetString()!;
                        var drawer = GetDrawer(msg.RootElement);
                        FileOps.Open(FullPath(drawer, name));
                        FileMetaStore.RecordOpen(drawer, name);   // 常用优先的数据积累（v2 柱2）
                        break;
                    }
                case "revealItem":
                    {
                        var name = msg.RootElement.GetProperty("name").GetString()!;
                        FileOps.Reveal(FullPath(GetDrawer(msg.RootElement), name));
                        break;
                    }
                case "openFolder":
                    FileOps.OpenFolder(WorkspacePath);
                    break;
                case "drawerCreate":
                    {
                        // 新建抽屉 = 工作区新建同名文件夹（v2 柱1）
                        var name = (msg.RootElement.GetProperty("name").GetString() ?? "").Trim();
                        if (name != "" && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                        {
                            Directory.CreateDirectory(Path.Combine(WorkspacePath, name));
                            PushFiles();
                        }
                        break;
                    }
                case "drawerRename":
                    {
                        // 抽屉改名 = 文件夹重命名（P10）；meta.json 键前缀与折叠状态跟随
                        var from = (msg.RootElement.GetProperty("from").GetString() ?? "").Trim();
                        var to = (msg.RootElement.GetProperty("to").GetString() ?? "").Trim();
                        RenameDrawer(from, to);
                        break;
                    }
                case "contextMenu":
                    {
                        var name = msg.RootElement.GetProperty("name").GetString()!;
                        var path = FullPath(GetDrawer(msg.RootElement), name);
                        _menuOpen = true;
                        BeginInvoke(() =>
                        {
                            FileOps.ShowContextMenu(Handle, path);
                            // 菜单关闭后稍等再解除（菜单命令可能还在跑）
                            Task.Delay(800).ContinueWith(_ => BeginInvoke(() => _menuOpen = false));
                        });
                        break;
                    }
                case "startDragOut":
                    {
                        // 前端拖动手势：批量选择时拖整组，否则拖单文件（P9）
                        var paths = msg.RootElement.GetProperty("files").EnumerateArray()
                            .Select(el => FullPath(
                                el.TryGetProperty("drawer", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null,
                                el.GetProperty("name").GetString()!))
                            .ToArray();
                        BeginInvoke(() => StartDragOut(paths));
                        break;
                    }
                // ---------- P9: 批量操作 / 回收站 / 剪贴板 ----------
                case "hitResult":
                    CompletePendingDrop(GetDrawer(msg.RootElement));
                    break;
                case "moveFiles":
                    {
                        var target = GetDrawer(msg.RootElement);
                        var targetDir = string.IsNullOrEmpty(target) ? WorkspacePath : Path.Combine(WorkspacePath, target);
                        Directory.CreateDirectory(targetDir);   // 目标抽屉可能已被删（如归档），重建
                        foreach (var el in msg.RootElement.GetProperty("files").EnumerateArray())
                        {
                            var name = el.GetProperty("name").GetString()!;
                            var drawer = el.TryGetProperty("drawer", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
                            try { FileOps.MoveInto(this, FullPath(drawer, name), targetDir); }
                            catch (Exception ex) { Log("moveFiles failed: " + name + " - " + ex.Message); }
                        }
                        _lastSignature = ComputeSignature();
                        PushFiles();
                        break;
                    }
                case "deleteFiles":
                    {
                        // 删除 = 系统回收站（选择模式「删除」/ Del 键）
                        foreach (var el in msg.RootElement.GetProperty("files").EnumerateArray())
                        {
                            var name = el.GetProperty("name").GetString()!;
                            var drawer = el.TryGetProperty("drawer", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
                            try { FileOps.SendToRecycleBin(FullPath(drawer, name)); }
                            catch (Exception ex) { Log("deleteFiles failed: " + name + " - " + ex.Message); }
                        }
                        _lastSignature = ComputeSignature();
                        PushFiles();
                        break;
                    }
                case "clipboardSave":
                    SaveClipboard(GetDrawer(msg.RootElement));
                    break;
                case "clipboardToNote":
                    SaveClipboardAsNote();
                    break;
                case "copyFiles":
                    {
                        // Ctrl+C：文件写入剪贴板 FileDrop（可粘贴到资源管理器/面板）
                        var paths = msg.RootElement.GetProperty("files").EnumerateArray()
                            .Select(el => FullPath(
                                el.TryGetProperty("drawer", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null,
                                el.GetProperty("name").GetString()!))
                            .Where(p => File.Exists(p) || Directory.Exists(p))
                            .ToArray();
                        if (paths.Length == 0) break;
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange(paths);
                        Clipboard.SetFileDropList(list);
                        break;
                    }
                case "setConfig":
                    {
                        var key = msg.RootElement.GetProperty("key").GetString()!;
                        ConfigStore.Update(c =>
                        {
                            if (key == "collapsedDrawers")   // 抽屉折叠状态记忆（跨重启；''=未分类）
                                c.collapsedDrawers = msg.RootElement.GetProperty("value").EnumerateArray()
                                    .Select(x => x.GetString() ?? "").ToList();
                            else if (key == "sortMode")      // 排序模式（P10）
                                c.sortMode = msg.RootElement.GetProperty("value").GetString() ?? "time";
                            else if (key == "drawerOrder")   // 抽屉手动排序（视图序）
                                c.drawerOrder = msg.RootElement.GetProperty("value").EnumerateArray()
                                    .Select(x => x.GetString() ?? "").ToList();
                            else if (key == "staleEnabled")  // P12 过期灰显与计数开关
                                c.staleEnabled = msg.RootElement.GetProperty("value").GetBoolean();
                            else if (key == "staleDays")     // P12 过期天数
                                c.staleDays = msg.RootElement.GetProperty("value").GetInt32();
                            else if (key == "autostart")     // P12 开机自启（写注册表）
                            {
                                c.autostart = msg.RootElement.GetProperty("value").GetBoolean();
                                SetAutoStart(c.autostart);
                            }
                            else if (key == "theme")        // P13 皮肤切换（亮色/毛玻璃）
                            {
                                c.theme = msg.RootElement.GetProperty("value").GetString() ?? "white";
                                ApplyTheme(c.theme);
                            }
                            else if (key == "drawerIcons")   // P13: 抽屉 emoji 标记（路径 -> emoji）
                            {
                                c.drawerIcons.Clear();
                                foreach (var p in msg.RootElement.GetProperty("value").EnumerateObject())
                                    c.drawerIcons[p.Name] = p.Value.GetString() ?? "";
                            }
                            else if (key == "workspacePath") // P12 工作区路径热切换
                            {
                                var p = msg.RootElement.GetProperty("value").GetString() ?? "";
                                if (Directory.Exists(p))
                                {
                                    c.workspacePath = p;
                                    ApplyWorkspacePath(p);
                                }
                            }
                        });
                        break;
                    }
                case "setHotkey":
                    {
                        // P13: 呼出快捷键自定义——试注册新组合，失败回滚旧组合
                        var mods = msg.RootElement.GetProperty("mods").EnumerateArray()
                            .Select(x => x.GetString() ?? "").ToList();
                        var vk = msg.RootElement.GetProperty("vk").GetInt32();
                        var text = msg.RootElement.GetProperty("text").GetString() ?? "";
                        var newMods = ModFlags(mods);
                        if (vk <= 0 || (newMods & 0x0Bu) == 0)
                        {
                            PostToJs(new { type = "hotkeyResult", ok = false, text });   // 必须含 Ctrl/Alt/Win
                            break;
                        }
                        Native.UnregisterAppHotKey(Handle);
                        if (Native.RegisterAppHotKey(Handle, newMods, (uint)vk))
                        {
                            _hkMods = newMods;
                            _hkVk = vk;
                            ConfigStore.Update(c =>
                            {
                                c.hotkeyMods = mods;
                                c.hotkeyKey = vk;
                            });
                            PostToJs(new { type = "hotkeyResult", ok = true, text });
                            Log("hotkey -> " + text);
                        }
                        else
                        {
                            Native.RegisterAppHotKey(Handle, _hkMods, (uint)_hkVk);   // 回滚
                            PostToJs(new { type = "hotkeyResult", ok = false, text });
                            Log("hotkey register FAILED: " + text);
                        }
                        break;
                    }
                case "pickFolder":
                    {
                        // P12：设置面板「浏览」-> 系统选目录对话框 -> 回填前端
                        using var dlg = new FolderBrowserDialog { Description = "选择工作区文件夹" };
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            PostToJs(new { type = "folderPicked", path = dlg.SelectedPath });
                        break;
                    }
                case "pinFile":
                    {
                        // P10：文件置顶（meta.json 落盘 + 重推刷新星标与置顶排序）
                        var name = msg.RootElement.GetProperty("name").GetString()!;
                        var drawer = GetDrawer(msg.RootElement);
                        FileMetaStore.SetPinned(drawer, name, msg.RootElement.GetProperty("pinned").GetBoolean());
                        PushFiles();
                        break;
                    }
                case "copyText":
                    {
                        // P11: 卡片「复制 Markdown 链接」等通用剪贴板文本写入
                        Clipboard.SetText(msg.RootElement.GetProperty("text").GetString() ?? "");
                        break;
                    }
                case "readText":
                    {
                        // P11: 悬浮预览读文本（编码探测：BOM -> 严格 UTF-8 -> GB18030，
                        // 中文记事本 ANSI 文件不再乱码）；截断 128KB 防大文件
                        var name = msg.RootElement.GetProperty("name").GetString()!;
                        var drawer = GetDrawer(msg.RootElement);
                        try
                        {
                            var bytes = File.ReadAllBytes(FullPath(drawer, name));
                            if (bytes.Length > 128 * 1024) Array.Resize(ref bytes, 128 * 1024);
                            PostToJs(new { type = "textPreview", name, drawer, text = DecodeText(bytes) });
                        }
                        catch (Exception ex) { Log("readText failed: " + ex.Message); }
                        break;
                    }
                // ---------- P5: 白板便签 ----------
                case "noteCreate":
                    {
                        NoteStore.Create();
                        PushNotes();
                        break;
                    }
                case "noteSave":
                    {
                        var id = msg.RootElement.GetProperty("id").GetString()!;
                        var content = msg.RootElement.GetProperty("content").GetString() ?? "";
                        NoteStore.Save(id, content);
                        break; // 不推送：编辑中频繁保存，列表由前端本地状态维持
                    }
                case "noteDelete":
                    {
                        var id = msg.RootElement.GetProperty("id").GetString()!;
                        NoteStore.Delete(id);
                        if (_noteWindows.TryGetValue(id, out var nw) && !nw.IsDisposed) nw.Close(); // 防删后窗口再保存复活
                        PushNotes();
                        break;
                    }
                case "noteRename":
                    {
                        var id = msg.RootElement.GetProperty("id").GetString()!;
                        var title = msg.RootElement.GetProperty("title").GetString() ?? "";
                        NoteStore.Rename(id, title);
                        PushNotes(); // 改名要刷新墙上的标题
                        break;
                    }
                case "noteOpen":
                    {
                        var id = msg.RootElement.GetProperty("id").GetString()!;
                        BeginInvoke(() => OpenNoteWindow(id));
                        break;
                    }
                case "openLink":
                    {
                        // 渲染出的链接交给系统浏览器；只放行 http(s)
                        var url = msg.RootElement.GetProperty("url").GetString() ?? "";
                        if (url.StartsWith("http://") || url.StartsWith("https://"))
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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

    // ---------- P6: 独立便签窗口 ----------

    private readonly Dictionary<string, NoteWindow> _noteWindows = new();

    /// <summary>打开（或聚焦既有）某便签的独立窗口。</summary>
    private void OpenNoteWindow(string id)
    {
        if (_noteWindows.TryGetValue(id, out var w))
        {
            if (w.IsDisposed) _noteWindows.Remove(id);
            else { w.Show(); w.Activate(); return; }
        }
        w = new NoteWindow(id, _env!, NotifyNoteChanged);
        w.FormClosed += (_, _) => _noteWindows.Remove(id);
        _noteWindows[id] = w;
        w.Show();
    }

    /// <summary>P11: 便签变更广播——刷新面板白板 + 重推所有打开的便签窗口（跨窗口同步）。</summary>
    private void NotifyNoteChanged(string id)
    {
        PushNotes();
        foreach (var w in _noteWindows.Values)
            w.RefreshNote(id);
    }
}
