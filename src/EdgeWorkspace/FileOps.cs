using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

    // ---------- 移动（拖入收纳） ----------

    /// <summary>移动到工作区；重名自动追加 " (n)"。返回最终文件名。</summary>
    public static string MoveInto(string src, string workspace)
    {
        var dest = Path.Combine(workspace, Path.GetFileName(src));
        if (File.Exists(dest) || Directory.Exists(dest))
        {
            var stem = Path.GetFileNameWithoutExtension(src);
            var ext = Path.GetExtension(src);
            for (var n = 2; ; n++)
            {
                var candidate = Path.Combine(workspace, stem + " (" + n + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) { dest = candidate; break; }
            }
        }
        if (Directory.Exists(src)) Directory.Move(src, dest);
        else File.Move(src, dest);
        return Path.GetFileName(dest);
    }

    // ---------- Shell 原生右键菜单 ----------

    public static void ShowContextMenu(IntPtr owner, string path)
    {
        var menu = new ShellContextMenu(owner, path);
        menu.Show(Cursor.Position);
    }
}

/// <summary>
/// Shell 文件右键菜单：完整 IContextMenu COM 流程。
/// 注意：菜单以 Win32 弹出窗实现，期间必须让出消息循环。
/// </summary>
internal sealed class ShellContextMenu
{
    private readonly IntPtr _owner;
    private readonly string _path;

    public ShellContextMenu(IntPtr owner, string path)
    {
        _owner = owner;
        _path = path;
    }

    public void Show(Point screenPos)
    {
        if (SHParseDisplayName(_path, IntPtr.Zero, out var pidlFull, 0, out _) != 0) return;
        try
        {
            var parent = ILClone(pidlFull);
            if (ILRemoveLastID(parent) == IntPtr.Zero) { ILFree(parent); return; }
            var child = ILFindLastID(pidlFull);

            SHGetDesktopFolder(out var desktop);
            desktop.BindToObject(parent, out var folderObj);
            var folder = (IShellFolder)folderObj;

            folder.GetUIObjectOf(_owner, 1, new[] { child }, IID_IContextMenu, out var ctxObj);
            var ctx = (IContextMenu)ctxObj;

            var hMenu = CreatePopupMenu();
            try
            {
                ctx.QueryContextMenu(hMenu, 0, CMD_FIRST, 0x7FFF, CMF_NORMAL);
                SetForegroundWindow(_owner);
                var cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenPos.X, screenPos.Y, _owner, IntPtr.Zero);
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
                ILFree(parent);
                Marshal.ReleaseComObject(ctx);
                Marshal.ReleaseComObject(folder);
                Marshal.ReleaseComObject(desktop);
            }
        }
        finally
        {
            ILFree(pidlFull);
        }
    }

    // ---------- COM interop ----------

    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private const uint CMF_NORMAL = 0;
    private const uint TPM_RETURNCMD = 0x0100, TPM_RIGHTBUTTON = 0x0002;
    private const int CMD_FIRST = 1;

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

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr pchEaten, out IntPtr pidl, IntPtr attributes);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumList);
        void BindToObject(IntPtr pidl, out object folder);
        void BindToStorage(IntPtr pidl, ref Guid riid, out object ppv);
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out object ppv);
        int GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr[] apidl, [In] ref Guid riid, out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr reserved, IntPtr name, uint cchMax);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILRemoveLastID")]
    private static extern IntPtr ILRemoveLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll", EntryPoint = "ILClone")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr h);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);
}
