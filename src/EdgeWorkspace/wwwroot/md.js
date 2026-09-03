// Markdown 渲染管道（P6）：卡片与便签窗口共用。
// marked 负责 GFM 语法 + 单换行换行（便签短行习惯）；DOMPurify 消毒，
// 防止粘贴进便签的 HTML 借 bridge 权限（openPath 等）在 app.local 上下文执行。
marked.use({ gfm: true, breaks: true });

function renderMarkdown(text) {
  return DOMPurify.sanitize(marked.parse(text || ''));
}
