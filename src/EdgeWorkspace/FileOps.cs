using System.IO;
using System.Runtime.InteropServices;

namespace EdgeWorkspace;

/// <summary>
/// 文件操作：打开、定位、移动（重名追加序号）、Shell 原生右键菜单。
/// </summary>
public static class FileOps
{
    // ---------- 打开 / 定位 ----------

    public static void Open(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });

    public static void OpenFolder(string folder) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });

    public static void Reveal(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
        {
            Arguments = "/select,\"" + path + "\"",
            UseShellExecute = true,
        });

    // ---------- 移动 / 复制（拖入收纳、抽屉间移动、Ctrl+C/V 粘贴通道，P9） ----------

    /// <summary>
    /// 解析目标路径：无冲突用原名；同名同大小 → owner 弹窗（是=替换 / 否=保留两者自动编号 /
    /// 取消=跳过返回 null）；同名不同大小 → 自动追加 " (n)"。
    /// </summary>
    private static string? ResolveDest(IWin32Window? owner, string src, string targetDir)
    {
        var name = Path.GetFileName(src);
        var dest = Path.Combine(targetDir, name);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;

        var ask = owner is not null && TryGetSize(src) >= 0 && TryGetSize(src) == TryGetSize(dest);
        if (ask)
        {
            var r = MessageBox.Show(owner,
                "「" + name + "」在目标位置已存在（大小相同，疑似重复）。\n\n" +
                "是 = 替换\n否 = 保留两者（自动编号）\n取消 = 跳过",
                "收纳去重", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return null;
            if (r == DialogResult.Yes)
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                else File.Delete(dest);
                return dest;
            }
        }
        return UniqueName(targetDir, name);
    }

    /// <summary>移动到目标目录；已在目标位置则跳过。返回最终文件名；跳过返回 null。</summary>
    public static string? MoveInto(IWin32Window? owner, string src, string targetDir)
    {
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(src)!),
                          Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(src);   // 已在目标位置（自落回原抽屉等）

        var dest = ResolveDest(owner, src, targetDir);
        if (dest is null) return null;
        Move(src, dest);
        return Path.GetFileName(dest);
    }

    /// <summary>
    /// 复制到目标目录（Ctrl+V 粘贴通道：资源管理器 → 工作区、工作区内部复制）。
    /// 粘贴到原位 = 自动编号建副本。返回最终文件名；跳过返回 null。
    /// </summary>
    public static string? CopyInto(IWin32Window? owner, string src, string targetDir)
    {
        var name = Path.GetFileName(src);
        var direct = Path.Combine(targetDir, name);
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(direct), StringComparison.OrdinalIgnoreCase))
        {
            var dup = UniqueName(targetDir, name);   // 原位粘贴 = 建副本
            if (Directory.Exists(src)) CopyDirectory(src, dup);
            else File.Copy(src, dup);
            return Path.GetFileName(dup);
        }

        var dest = ResolveDest(owner, src, targetDir);
        if (dest is null) return null;
        if (Directory.Exists(src)) CopyDirectory(src, dest);
        else File.Copy(src, dest);
        return Path.GetFileName(dest);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// <summary>目标目录内的可用名称：原名追加 " (n)"。</summary>
    internal static string UniqueName(string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, stem + " (" + n + ")" + ext);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static long TryGetSize(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : -1;

    /// <summary>移动；跨卷的文件退化为 复制+删除（目录跨卷仍抛异常，走上层日志）。</summary>
    private static void Move(string src, string dest)
    {
        try
        {
            if (Directory.Exists(src)) Directory.Move(src, dest);
            else File.Move(src, dest);
        }
        catch (IOException) when (File.Exists(src) &&
            !string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(src, dest, true);
            File.Delete(src);
        }
    }

    // ---------- Shell 原生右键菜单 ----------

    public static event Action<string>? LogLine;

    public static void ShowContextMenu(IntPtr owner, string path)
    {
        try
        {
            new ShellContextMenu(owner, path).Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            LogLine?.Invoke("ShowContextMenu failed: " + ex.GetType().Name + " " + ex.Message);
        }
    }
}

/// <summary>
/// Shell 文件右键菜单：SHCreateItemFromParsingName -> IShellItem ->
/// BindToHandler(BHID_SFUIObject) -> IContextMenu。
/// 之前的实现手写 IShellFolder 完整接口导致 vtable 槽位错（AccessViolation 闪退），
/// 本实现只声明实际调用的方法，且全部经 GUID 验证的官方路径。
/// </summary>
internal sealed class ShellContextMenu
{
    private readonly IntPtr _owner;
    private readonly string _path;

    // 官方 GUID（shlguid.h / shobjidl_core）
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid BHID_SFUIObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");

    private const uint CMF_NORMAL = 0;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int CMD_FIRST = 1;

    public ShellContextMenu(IntPtr owner, string path)
    {
        _owner = owner;
        _path = path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
    }

    // 只声明实际用到的方法，且都在接口前部（vtable 前几槽，签名错也难崩）
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr reserved, IntPtr name, uint cchMax);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr h);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IShellItem ppv);

    public void Show(Point screenPos)
    {
        var iidShellItem = IID_IShellItem;
        if (SHCreateItemFromParsingName(_path, IntPtr.Zero, ref iidShellItem, out var item) != 0)
            return;
        try
        {
            var iid = IID_IContextMenu;
            var bhid = BHID_SFUIObject;
            item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var ctxObj);
            if (ctxObj is not IContextMenu ctx) return;

            var hMenu = CreatePopupMenu();
            try
            {
                if (ctx.QueryContextMenu(hMenu, 0, CMD_FIRST, 0x7FFF, CMF_NORMAL) < 0) return;
                SetForegroundWindow(_owner);
                var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    screenPos.X, screenPos.Y, _owner, IntPtr.Zero);
                if (cmd != 0)
                {
                    var info = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                        hwnd = _owner,
                        lpVerb = (IntPtr)(cmd - CMD_FIRST),
                        nShow = 5, // SW_SHOW
                    };
                    ctx.InvokeCommand(ref info);
                }
            }
            finally
            {
                DestroyMenu(hMenu);
                Marshal.ReleaseComObject(ctx);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }
}
