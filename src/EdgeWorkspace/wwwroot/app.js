// EdgeWorkspace 前端逻辑
// P1：接收 C# 推送的文件列表并渲染；分类 Tab 在 P2 接过滤。

const bridge = window.chrome?.webview ?? null;

// ---------- 桥：JS -> C# ----------
function post(type, payload = {}) {
  bridge?.postMessage(JSON.stringify({ type, ...payload }));
}

let allItems = [];      // 最新文件条目（C# 推送）
let currentTab = 'all'; // P2 起生效

window.addEventListener('DOMContentLoaded', () => {
  bindTabs();
  bindButtons();
  post('ready');
});

// ---------- Tabs（P0 视图切换；P2 接过滤） ----------
function bindTabs() {
  document.querySelectorAll('.tab-item').forEach(tab => {
    tab.addEventListener('click', () => setTab(tab.dataset.tab));
  });
}

function setTab(tab) {
  currentTab = tab;
  document.querySelectorAll('.tab-item').forEach(t => t.classList.toggle('active', t.dataset.tab === tab));
  const isBoard = tab === 'whiteboard';
  document.getElementById('fileGrid').style.display = isBoard ? 'none' : 'grid';
  document.getElementById('noteWall').style.display = isBoard ? 'grid' : 'none';
  if (!isBoard) renderGrid(); // 切回文件视图时按当前 Tab 重渲染
}

// ---------- Header buttons ----------
function bindButtons() {
  document.getElementById('btnOpenFolder').addEventListener('click', () => post('openFolder'));
  document.getElementById('btnRefresh').addEventListener('click', () => post('refresh'));
  document.getElementById('btnPin').addEventListener('click', () => {
    const btn = document.getElementById('btnPin');
    const pinned = btn.classList.toggle('pinned-active');
    post('setPinned', { pinned });
  });
}

// ---------- 文件渲染（P1） ----------
const KIND_ICON = {
  folder: '📁', doc: '📄', image: '🖼️', video: '🎬',
  audio: '🎵', archive: '🗜️', app: '⚙️', other: '📄',
};

function fmtSize(bytes) {
  if (bytes <= 0) return '';
  const units = ['B', 'kB', 'MB', 'GB'];
  let i = 0, v = bytes;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (i === 0 ? v : v.toFixed(1)) + ' ' + units[i];
}

function renderGrid() {
  const grid = document.getElementById('fileGrid');
  const items = currentTab === 'all' ? allItems : allItems.filter(it => kindOfTab(it) === currentTab);
  const frag = document.createDocumentFragment();

  for (const it of items) {
    const card = document.createElement('div');
    card.className = 'file-card';
    card.title = it.name + '\n' + it.mtime;

    const thumb = document.createElement('div');
    thumb.className = 'file-thumb-box';
    if (it.kind === 'image') {
      // 缩略图：虚拟主机无法访问任意盘符，先用 kind 图标；P4 接缩略图管道
      thumb.innerHTML = '<span class="kind-icon">' + (KIND_ICON[it.kind] || '📄') + '</span>';
    } else {
      thumb.innerHTML = '<span class="kind-icon">' + (KIND_ICON[it.kind] || '📄') + '</span>';
    }

    const title = document.createElement('div');
    title.className = 'file-title';
    title.textContent = it.name;

    const meta = document.createElement('div');
    meta.className = 'file-meta';
    meta.textContent = it.isFolder ? '文件夹' : fmtSize(it.size);

    card.append(thumb, title, meta);
    frag.append(card);
  }
  grid.replaceChildren(frag);

  document.getElementById('totalBadge').textContent = allItems.length + ' 项';
}

// Tab 名到条目 kind 的映射（P2 起由 setTab 驱动）
function kindOfTab(it) {
  if (currentTab === 'folder') return it.kind === 'folder';
  if (currentTab === 'doc') return it.kind === 'doc';
  if (currentTab === 'image') return it.kind === 'image';
  if (currentTab === 'video') return it.kind === 'video';
  return true;
}

// ---------- 桥：C# -> JS ----------
// WebView2 的宿主消息走 window.chrome.webview 的 message 事件（不是 window 的）
bridge?.addEventListener('message', e => {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'files':
      allItems = msg.items || [];
      document.getElementById('totalBadge').textContent = msg.total + ' 项';
      renderGrid();
      break;
  }
});
