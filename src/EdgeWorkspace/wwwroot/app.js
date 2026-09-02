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
  if (currentTab === 'whiteboard' && tab !== 'whiteboard') flushNotes();
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

    // P4 交互
    card.addEventListener('click', () => post('openPath', { name: it.name }));
    card.addEventListener('contextmenu', e => {
      e.preventDefault();
      post('contextMenu', { name: it.name });
    });
    // 拖出：HTML5 拖拽只作手势检测，实际 OLE 拖放由 C# DoDragDrop 执行
    card.draggable = true;
    card.addEventListener('dragstart', e => {
      e.preventDefault();
      post('startDragOut', { name: it.name });
    });

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
    case 'setTab':
      setTab(msg.tab);
      break;
    case 'notes':
      allNotes = msg.notes || [];
      renderNoteWall();
      break;
  }
});

// ---------- P5: 白板便签墙 ----------
// 无上限：每张便签 = notes/ 下一个 txt（C# NoteStore 管理）。
// 原生 textarea 直接编辑（中文输入零风险），输入停 600ms 自动保存。

let allNotes = [];
const saveTimers = new Map();   // id -> timer
const dirtyNotes = new Set();

function renderNoteWall() {
  const wall = document.getElementById('noteWall');
  const frag = document.createDocumentFragment();

  for (const n of allNotes) {
    frag.append(buildNoteCard(n));
  }

  // 新建卡（始终在最后）
  const addCard = document.createElement('div');
  addCard.className = 'whiteboard-box whiteboard-add';
  addCard.innerHTML =
    '<div class="whiteboard-header"><span>＋ 新建便签</span></div>' +
    '<div class="whiteboard-add-hint">点击添加一张新便签</div>';
  addCard.addEventListener('click', () => post('noteCreate'));
  frag.append(addCard);

  wall.replaceChildren(frag);
}

function buildNoteCard(n) {
  const box = document.createElement('div');
  box.className = 'whiteboard-box';

  const header = document.createElement('div');
  header.className = 'whiteboard-header';
  const title = document.createElement('span');
  title.textContent = '便签 · ' + (n.mtime || '');
  const del = document.createElement('button');
  del.className = 'whiteboard-del';
  del.textContent = '删除';
  del.title = '删除这张便签';
  del.addEventListener('click', e => {
    e.stopPropagation();
    if (confirm('删除这张便签？内容不可恢复。')) {
      saveTimers.delete(n.id);
      dirtyNotes.delete(n.id);
      post('noteDelete', { id: n.id });
    }
  });
  header.append(title, del);

  const ta = document.createElement('textarea');
  ta.className = 'whiteboard-content';
  ta.value = n.content;
  ta.placeholder = '在此随手记录：临时想法、代码片段、待办…';
  ta.dataset.noteId = n.id;
  // 防抖自动保存：停止输入 600ms 落盘
  ta.addEventListener('input', () => {
    dirtyNotes.add(n.id);
    clearTimeout(saveTimers.get(n.id));
    saveTimers.set(n.id, setTimeout(() => {
      saveTimers.delete(n.id);
      post('noteSave', { id: n.id, content: ta.value });
    }, 600));
  });
  // 失焦时把未保存的立即落盘
  ta.addEventListener('blur', () => {
    if (saveTimers.has(n.id)) {
      clearTimeout(saveTimers.get(n.id));
      saveTimers.delete(n.id);
      post('noteSave', { id: n.id, content: ta.value });
    }
  });

  box.append(header, ta);
  return box;
}

// 离开白板视图时把所有待保存的定时器立即落盘
function flushNotes() {
  document.querySelectorAll('.whiteboard-content').forEach(ta => {
    const id = ta.dataset.noteId;
    if (saveTimers.has(id)) {
      clearTimeout(saveTimers.get(id));
      saveTimers.delete(id);
      post('noteSave', { id, content: ta.value });
    }
  });
}
