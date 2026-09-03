using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EdgeWorkspace;

/// <summary>
/// 独立便签窗口（P6）：真窗口（标题栏拖动/最大化），默认查看态（渲染 Markdown），编辑态改源码。
/// 每张便签一个实例，由 MainForm 按 id 复用/聚焦；WebView2 复用主面板环境（共享浏览器进程）。
/// </summary>
public sealed class NoteWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly CoreWebView2Environment _env;
    private readonly string _id;
    private readonly Action<string> _notesChanged;   // 保存/改名后广播（带 id，跨窗口同步）
    private CoreWebView2? _core;

    public NoteWindow(string id, CoreWebView2Environment env, Action<string> notesChanged)
    {
        _id = id;
        _env = env;
        _notesChanged = notesChanged;
        Text = "便签";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(420, 320);
        Size = new Size(760, 640);
        ShowInTaskbar = true;
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async(_env);
            _core = _web.CoreWebView2;
            _core.SetVirtualHostNameToFolderMapping(
                "app.local", Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                CoreWebView2HostResourceAccessKind.Allow);
            _core.WebMessageReceived += OnMessage;
            _core.Navigate("https://app.local/note.html");
        };
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.WebMessageAsJson;
            if (raw.StartsWith('"'))
                raw = JsonSerializer.Deserialize<string>(raw) ?? "";
            var msg = JsonDocument.Parse(raw);
            switch (msg.RootElement.GetProperty("type").GetString())
            {
                case "ready":
                    SetTheme(ConfigStore.Current.theme);   // P13：先于内容应用皮肤
                    SendNote();
                    break;
                case "noteSave":
                    NoteStore.Save(_id, msg.RootElement.GetProperty("content").GetString() ?? "");
                    _notesChanged(_id);
                    break;
                case "noteRename":
                    {
                        var title = msg.RootElement.GetProperty("title").GetString() ?? "";
                        NoteStore.Rename(_id, title);
                        Text = title != "" ? title : "便签";
                        _notesChanged(_id);
                        break;
                    }
                case "openLink":
                    {
                        // 只放行 http(s)，防 file:// 等协议绕过
                        var url = msg.RootElement.GetProperty("url").GetString() ?? "";
                        if (url.StartsWith("http://") || url.StartsWith("https://"))
                            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        break;
                    }
                case "noteCopy":
                    Clipboard.SetText(msg.RootElement.GetProperty("content").GetString() ?? "");
                    break;
            }
        }
        catch (Exception ex)
        {
            MainForm.Log("NoteWindow msg: " + ex.Message);
        }
    }

    private void SendNote()
    {
        var n = NoteStore.Get(_id);
        if (n is null) { Close(); return; } // 便签已不存在（外部被删）
        Text = n.Title != "" ? n.Title : "便签";
        var json = JsonSerializer.Serialize(new { type = "note", note = n }, MainForm.JsonOpts);
        _core!.PostWebMessageAsJson(json);
    }

    /// <summary>P11: 别的窗口改了这张便签 -> 重推数据刷新本窗
    /// （note.js 在编辑态会忽略推送，不会覆盖正在输入的内容）。</summary>
    public void RefreshNote(string id)
    {
        if (id == _id && _core is not null) SendNote();
    }

    // ---------- P13: 主题 ----------

    private string _theme = "white";
    private bool _acrylicOn;

    /// <summary>应用皮肤：webview 透底 + 本窗口亚克力 + 通知前端换 CSS 变量组。</summary>
    public void SetTheme(string theme)
    {
        _theme = theme == "acrylic" ? "acrylic" : "white";
        if (_core is null) return;
        _web.DefaultBackgroundColor = _theme == "acrylic" ? Color.Transparent : Color.White;
        var on = _theme == "acrylic";
        if (on != _acrylicOn)
        {
            _acrylicOn = on;
            Native.SetAcrylic(Handle, on);
        }
        var json = JsonSerializer.Serialize(new { type = "theme", theme = _theme }, MainForm.JsonOpts);
        _core.PostWebMessageAsJson(json);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_acrylicOn) return;   // 亚克力激活时不铺底色，让 DWM 模糊透出
        base.OnPaintBackground(e);
    }
}
