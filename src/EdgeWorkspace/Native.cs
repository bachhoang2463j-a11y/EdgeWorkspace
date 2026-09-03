using System.Runtime.InteropServices;

namespace EdgeWorkspace;

/// <summary>
/// Win32 交互原语：光标位置、前台窗口查询、全局热键。
/// </summary>
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr h, int id);

    // ---------- OLE 拖放（P7：拖入收纳） ----------

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int X, Y; }

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr h, IDropTarget target);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr h);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr hdrop, uint iFile, System.Text.StringBuilder? name, uint max);

    private delegate bool EnumChildProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr h, EnumChildProc cb, IntPtr l);

    /// <summary>注册/替换 hwnd 上的 OLE 放置目标（同窗口重复注册会失败，先撤销再注册）。</summary>
    public static void SetDropTarget(IntPtr hwnd, IDropTarget target)
    {
        RevokeDragDrop(hwnd);
        RegisterDragDrop(hwnd, target);
    }

    /// <summary>收集 hwnd 的全部子孙窗口。</summary>
    public static List<IntPtr> CollectDescendants(IntPtr hwnd)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(hwnd, (child, _) => { list.Add(child); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>从 CF_HDROP 句柄读出全部文件路径。</summary>
    public static string[] DragQueryFiles(IntPtr hdrop)
    {
        var count = (int)DragQueryFile(hdrop, 0xFFFFFFFF, null, 0);
        var files = new string[count];
        for (var i = 0; i < count; i++)
        {
            var len = (int)DragQueryFile(hdrop, (uint)i, null, 0);
            var sb = new System.Text.StringBuilder(len + 1);
            DragQueryFile(hdrop, (uint)i, sb, (uint)sb.Capacity);
            files[i] = sb.ToString();
        }
        return files;
    }

    public const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4;
    public const uint VK_Z = 0x5A;
    private const int HOTKEY_ID = 0xBEEF;

    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var pt);
        return (pt.X, pt.Y);
    }

    /// <summary>左键当前是否按住（OLE 拖拽期间保持为真）。</summary>
    public static bool IsMouseLeftDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    /// <summary>指定虚拟键当前是否按住（面板可见期的无焦点 Ctrl+V 检测）。</summary>
    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 点下方是否可视为"桌面暴露"：桌面本身（Progman/WorkerW）、桌面图标列表，
    /// 或非交互覆盖层（任务栏/Rainmeter 等贴边小部件）都放行；
    /// 应用窗口（浏览器/编辑器等）盖住右缘时才拒绝。
    /// </summary>
    public static bool IsDesktopAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, 64);
        var cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "SHELLDLL_DefView"
               or "SysListView32"          // 桌面图标列表
               or "Shell_TrayWnd"          // 任务栏
               or "RainmeterMeterWindow";  // 桌面小部件覆盖层
    }

    /// <summary>前台窗口是否为全屏（覆盖整块屏幕，排除任务栏与本应用）。</summary>
    public static bool IsForegroundFullScreen(IntPtr exclude)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == exclude) return false;
        GetWindowRect(fg, out var r);
        var vs = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return r.L <= vs.Left && r.T <= vs.Top && r.R >= vs.Right && r.B >= vs.Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

    public static bool RegisterAppHotKey(IntPtr h) =>
        RegisterHotKey(h, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_Z);

    public static bool UnregisterAppHotKey(IntPtr h) =>
        UnregisterHotKey(h, HOTKEY_ID);
}
