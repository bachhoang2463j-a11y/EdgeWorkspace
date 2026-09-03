// Markdown 渲染管道（P6）：卡片与便签窗口共用。
// marked 负责 GFM 语法 + 单换行换行（便签短行习惯）；DOMPurify 消毒，
// 防止粘贴进便签的 HTML 借 bridge 权限（openPath 等）在 app.local 上下文执行。
marked.use({ gfm: true, breaks: true });

function renderMarkdown(text) {
  // 先转义 "<"：伪 HTML 块（如酒馆 <Status_block>）会被 marked 当原生 HTML 透传，
  // 块内换行在 HTML 语义下折叠丢失；转义后走段落路径，breaks 保住每一行。
  const src = String(text || '').replace(/</g, '&lt;');
  return DOMPurify.sanitize(marked.parse(src));
}
