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
    private readonly Action _notesChanged; // 保存/改名后让主面板刷新白板
    private CoreWebView2? _core;

    public NoteWindow(string id, CoreWebView2Environment env, Action notesChanged)
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
                    SendNote();
                    break;
                case "noteSave":
                    NoteStore.Save(_id, msg.RootElement.GetProperty("content").GetString() ?? "");
                    _notesChanged();
                    break;
                case "noteRename":
                    {
                        var title = msg.RootElement.GetProperty("title").GetString() ?? "";
                        NoteStore.Rename(_id, title);
                        Text = title != "" ? title : "便签";
                        _notesChanged();
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
}
