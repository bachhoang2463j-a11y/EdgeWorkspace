using Microsoft.Web.WebView2.Core;

namespace EdgeWorkspace;

/// <summary>
/// 主面板窗口：右缘停靠、置顶、WebView2 UI 宿主。
/// P0 仅负责窗口与页面加载；唤出/收起/热键在 P3，数据桥在 P1 接通。
/// </summary>
public class MainForm : Form
{
    private Microsoft.Web.WebView2.WinForms.WebView2 _web = null!;

    public MainForm()
    {
        Text = "EdgeWorkspace";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;

        SizeChanged += (_, _) => LayoutWebView();

        Load += async (_, _) =>
        {
            DockRight();
            _web = new Microsoft.Web.WebView2.WinForms.WebView2
            {
                Dock = DockStyle.Fill,
                AllowExternalDrop = false, // 拖放由窗体 OLE 处理（P4）
            };
            Controls.Add(_web);

            // 用户数据目录放在应用旁，避免写 Program Files
            var dataDir = Path.Combine(AppContext.BaseDirectory, "WebView2Data");
            Directory.CreateDirectory(dataDir);
            await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, dataDir));

            // 虚拟主机映射：页面里用 https://app.local/ 引用 wwwroot
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local", root, CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.Navigate("https://app.local/index.html");

            LayoutWebView();
        };
    }

    /// <summary>停靠到工作区右缘（可见状态）。</summary>
    public void DockRight()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        int width = Math.Max(420, wa.Width / 3);
        Location = new Point(wa.Right - width, wa.Top);
        Size = new Size(width, wa.Height);
    }

    private void LayoutWebView()
    {
        // WebView2 Dock=Fill 自适应，无需额外处理；保留钩子供后续使用
    }
}
