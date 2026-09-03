// 独立便签窗口（P6）：默认查看态渲染 Markdown，编辑态改源码。
// 窗口身份由 C# 侧维护（按便签 id 开窗），本页不需要知道 id；
// ready 后 C# 推送 { note }，保存/改名发消息回 C# 落盘并刷新白板。

const bridge = window.chrome?.webview ?? null;

let note = null;      // { id, title, content, mtime }
let editing = false;

function post(type, payload = {}) {
  bridge?.postMessage(JSON.stringify({ type, ...payload }));
}

window.addEventListener('DOMContentLoaded', () => {
  document.getElementById('btnEdit').addEventListener('click', enterEdit);
  document.getElementById('btnSave').addEventListener('click', save);
  document.getElementById('btnCancel').addEventListener('click', exitEdit);
  document.getElementById('btnCopy').addEventListener('click', () => {
    if (!note) return;
    post('noteCopy', { content: note.content || '' });
    const b = document.getElementById('btnCopy');
    b.textContent = '已复制 ✓';
    setTimeout(() => { b.textContent = '复制'; }, 1200);
  });
  window.addEventListener('keydown', e => {
    if (!editing) return;
    if (e.key === 'Escape') exitEdit();
    if (e.ctrlKey && e.key === 's') { e.preventDefault(); save(); }
  });
  // 渲染内容里的链接交给系统浏览器，别把本窗口 WebView 导航走
  document.getElementById('noteView').addEventListener('click', e => {
    const a = e.target.closest('a[href]');
    if (a) { e.preventDefault(); post('openLink', { url: a.href }); }
  });
  post('ready');
});

bridge?.addEventListener('message', e => {
  const msg = e.data;
  if (msg?.type === 'theme') {
    // P13 皮肤（与面板同步）
    document.documentElement.dataset.theme = msg.theme || 'white';
    return;
  }
  if (msg?.type === 'note' && msg.note) {
    note = msg.note;
    if (!editing) renderView();
  }
});

function renderView() {
  document.getElementById('viewTitle').textContent = note.title || '便签';
  document.getElementById('viewMtime').textContent = note.mtime || '';
  document.getElementById('noteView').innerHTML = renderMarkdown(note.content);
}

function enterEdit() {
  if (!note) return;
  editing = true;
  document.getElementById('viewTitle').hidden = true;
  document.getElementById('viewMtime').hidden = true;
  document.getElementById('viewActions').hidden = true;
  document.getElementById('noteView').hidden = true;
  const title = document.getElementById('editTitle');
  title.hidden = false;
  title.value = note.title || '';
  document.getElementById('editActions').hidden = false;
  const editor = document.getElementById('noteEditor');
  editor.hidden = false;
  editor.value = note.content || '';
  editor.focus();
}

function exitEdit() {
  editing = false;
  document.getElementById('editTitle').hidden = true;
  document.getElementById('editActions').hidden = true;
  document.getElementById('noteEditor').hidden = true;
  document.getElementById('viewTitle').hidden = false;
  document.getElementById('viewMtime').hidden = false;
  document.getElementById('viewActions').hidden = false;
  document.getElementById('noteView').hidden = false;
}

function save() {
  if (!note) return;
  const content = document.getElementById('noteEditor').value;
  const title = document.getElementById('editTitle').value.trim();
  if (content !== (note.content || '')) post('noteSave', { content });
  if (title !== (note.title || '')) post('noteRename', { title });
  note.content = content;
  note.title = title;
  note.mtime = fmtNow();
  exitEdit();
  renderView();
}

function fmtNow() {
  const d = new Date();
  const p = n => String(n).padStart(2, '0');
  return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate()) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes());
}
