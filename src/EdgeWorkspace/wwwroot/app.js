// EdgeWorkspace 前端逻辑
// P2：分类 Tab 过滤 + 计数徽章联动。

const bridge = window.chrome?.webview ?? null;

// ---------- 桥：JS -> C# ----------
function post(type, payload = {}) {
  bridge?.postMessage(JSON.stringify({ type, ...payload }));
}

let allItems = [];      // 最新文件条目（C# 推送）
let currentTab = 'all';

window.addEventListener('DOMContentLoaded', () => {
  bindTabs();
  bindButtons();
  post('ready');
});

// ---------- Tabs（P2：过滤 + 计数） ----------
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
  updateBadges();
  if (!isBoard) renderGrid();
}

// Tab -> kind 匹配（口径见 SPEC §6）
const TAB_KINDS = {
  folder: ['folder'],
  doc: ['doc'],
  image: ['image'],
  video: ['video'],
};
function matchesTab(it, tab) {
  const kinds = TAB_KINDS[tab];
  return kinds ? kinds.includes(it.kind) : true; // all
}

function updateBadges() {
  // 头部徽章显示当前视图数量；白板视图显示工作区总数
  const n = currentTab === 'whiteboard' || currentTab === 'all'
    ? allItems.length
    : allItems.filter(it => matchesTab(it, currentTab)).length;
  document.getElementById('totalBadge').textContent = n + ' 项';
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

// ---------- 文件渲染 ----------
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
  const items = currentTab === 'all' ? allItems : allItems.filter(it => matchesTab(it, currentTab));
  const frag = document.createDocumentFragment();

  for (const it of items) {
    const card = document.createElement('div');
    card.className = 'file-card';
    card.title = it.name + '\n' + it.mtime;

    const thumb = document.createElement('div');
    thumb.className = 'file-thumb-box';
    thumb.innerHTML = '<span class="kind-icon">' + (KIND_ICON[it.kind] || '📄') + '</span>';

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
  updateBadges();
}

// ---------- 桥：C# -> JS ----------
// WebView2 的宿主消息走 window.chrome.webview 的 message 事件（不是 window 的）
bridge?.addEventListener('message', e => {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'files':
      allItems = msg.items || [];
      if (currentTab !== 'whiteboard') renderGrid(); else updateBadges();
      break;
  }
});
