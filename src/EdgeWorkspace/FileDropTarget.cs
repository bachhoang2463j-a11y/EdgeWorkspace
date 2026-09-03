using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace EdgeWorkspace;

/// <summary>
/// 自定义 OLE 放置目标（P7 拖入收纳）。
/// OLE 的放置目标解析不沿父链上溯：光标命中的最深层窗口（WebView2 的 Chromium 子窗口）
/// 没有注册目标就直接判拒绝，窗体的 AllowDrop 收不到事件——所以把本目标逐窗口注册到
/// 窗体与 WebView2 全部子窗口上（见 MainForm.ApplyFileDropTargets）。
/// 回调在 UI 线程（注册所在线程）触发。
/// </summary>
internal sealed class FileDropTarget : Native.IDropTarget
{
    private const short CF_HDROP = 15;
    private const uint AllowedEffects = 3; // DROPEFFECT_COPY | DROPEFFECT_MOVE

    private readonly Action<bool> _onDragEnter;   // 拖入悬停（是否文件拖放）
    private readonly Action _onDragLeave;
    private readonly Action<string[]> _onDrop;
    private bool _files;

    public FileDropTarget(Action<bool> onDragEnter, Action onDragLeave, Action<string[]> onDrop)
    {
        _onDragEnter = onDragEnter;
        _onDragLeave = onDragLeave;
        _onDrop = onDrop;
    }

    private static bool HasFiles(ComTypes.IDataObject obj)
    {
        var fmt = new FORMATETC
        {
            cfFormat = CF_HDROP,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        return obj.QueryGetData(ref fmt) == 0;
    }

    public int DragEnter(ComTypes.IDataObject? pDataObj, uint grfKeyState, Native.POINTL pt, ref uint pdwEffect)
    {
        _files = pDataObj is not null && HasFiles(pDataObj);
        _onDragEnter(_files);
        pdwEffect = _files ? pdwEffect & AllowedEffects : 0;
        return 0;
    }

    public int DragOver(uint grfKeyState, Native.POINTL pt, ref uint pdwEffect)
    {
        pdwEffect = _files ? pdwEffect & AllowedEffects : 0;
        return 0;
    }

    public int DragLeave()
    {
        _onDragLeave();
        return 0;
    }

    public int Drop(ComTypes.IDataObject? pDataObj, uint grfKeyState, Native.POINTL pt, ref uint pdwEffect)
    {
        _onDragLeave();
        _files = false;
        if (pDataObj is null || !HasFiles(pDataObj)) { pdwEffect = 0; return 0; }

        var fmt = new FORMATETC
        {
            cfFormat = CF_HDROP,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
        };
        pDataObj.GetData(ref fmt, out var medium);
        try
        {
            var files = Native.DragQueryFiles(medium.unionmember);
            pdwEffect &= AllowedEffects;
            _onDrop(files);
        }
        finally
        {
            Native.ReleaseStgMedium(ref medium);
        }
        return 0;
    }
}
