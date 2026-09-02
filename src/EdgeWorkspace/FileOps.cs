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
