// EdgeWorkspace 前端逻辑
// P2：分类 Tab 过滤 + 计数徽章联动。P5：白板便签。
// P6：卡片只读渲染 Markdown（见 md.js），单击打开独立窗口查看/编辑。

const bridge = window.chrome?.webview ?? null;

// ---------- 桥：JS -> C# ----------
function post(type, payload = {}) {
  bridge?.postMessage(JSON.stringify({ type, ...payload }));
}

let allItems = [];      // 最新文件条目（C# 推送）
let allDrawers = [];    // 抽屉清单（根目录子文件夹，C# 推送，名称序）
let currentTab = 'all';
let appConfig = {};     // 设置项（C# config 消息推送）
let filterText = '';    // 名称过滤（P10）
let sortMode = 'time';  // 排序模式 time|name|size|kind|frequent（P10，落 config）

window.addEventListener('DOMContentLoaded', () => {
  bindTabs();
  bindButtons();
  bindSelectBar();
  bindSearch();
  // Ctrl+V 收纳走 C# 键态检测（pasteDetected 消息）：贴边唤出不抢焦点，
  // WebView 收不到键盘事件，必须由 C# 侧检测后回传分流。
  post('ready');
});

// ---------- P10: 名称过滤 + 排序 ----------
function bindSearch() {
  const box = document.getElementById('filterBox');
  box.addEventListener('input', () => {
    filterText = box.value;
    if (currentTab !== 'whiteboard') renderGrid();
  });
  box.addEventListener('keydown', e => {
    if (e.key === 'Escape') { box.value = ''; filterText = ''; box.blur(); renderGrid(); }
  });
  document.getElementById('sortBox').addEventListener('change', e => {
    sortMode = e.target.value;
    post('setConfig', { key: 'sortMode', value: sortMode });
    if (currentTab !== 'whiteboard') renderGrid();
  });
}

// 组内排序：置顶恒优先，其次按模式（time=修改时间倒序，name=zh 词典序，size/kind/frequent）
const KIND_RANK = { folder: 0, doc: 1, image: 2, video: 3, audio: 4, archive: 5, app: 6, other: 7 };
function sortItems(list) {
  const pin = (a, b) => (b.pinned ? 1 : 0) - (a.pinned ? 1 : 0);
  const time = (a, b) => (b.mtime || '').localeCompare(a.mtime || '');
  if (sortMode === 'name') return [...list].sort((a, b) => pin(a, b) || a.name.localeCompare(b.name, 'zh'));
  if (sortMode === 'size') return [...list].sort((a, b) => pin(a, b) || b.size - a.size);
  if (sortMode === 'kind') return [...list].sort((a, b) => pin(a, b) || (KIND_RANK[a.kind] ?? 9) - (KIND_RANK[b.kind] ?? 9) || time(a, b));
  if (sortMode === 'frequent') return [...list].sort((a, b) => pin(a, b) || (b.openCount || 0) - (a.openCount || 0) || time(a, b));
  return [...list].sort((a, b) => pin(a, b) || time(a, b));
}

// ---------- Tabs（P2：过滤 + 计数） ----------
function bindTabs() {
  document.querySelectorAll('.tab-item').forEach(tab => {
    tab.addEventListener('click', () => setTab(tab.dataset.tab));
  });
}

function setTab(tab) {
  if (tab === currentTab) return;   // C# 可能在拖拽悬停期间重复推送，幂等防闪烁
  currentTab = tab;
  document.querySelectorAll('.tab-item').forEach(t => t.classList.toggle('active', t.dataset.tab === tab));
  const isBoard = tab === 'whiteboard';
  document.getElementById('fileGrid').style.display = isBoard ? 'none' : 'flex';
  document.getElementById('noteWall').style.display = isBoard ? 'grid' : 'none';
  updateBadges();
  if (!isBoard) renderGrid();
}

// Tab -> kind 匹配（口径见 SPEC §6；v2 抽屉即文件夹，文件夹 Tab 已移除）
const TAB_KINDS = {
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
  document.getElementById('btnSelect').addEventListener('click', () => setSelectMode(!selectMode));
  document.getElementById('btnSettings').addEventListener('click', () => {
    document.getElementById('settingsPanel').hidden = false;
  });
  document.getElementById('btnSettingsClose').addEventListener('click', () => {
    document.getElementById('settingsPanel').hidden = true;
  });
}

// ---------- P9: 批量选择模式 ----------
let selectMode = false;
const selectedKeys = new Set();   // "抽屉/文件名"（未分类为 "/文件名"）

function keyOf(it) { return (it.drawer ?? '') + '/' + it.name; }

function setSelectMode(on) {
  selectMode = on;
  if (!on) selectedKeys.clear();
  document.getElementById('selectBar').hidden = !on;
  document.getElementById('btnSelect').classList.toggle('pinned-active', on);
  updateSelectBar();
  if (currentTab !== 'whiteboard') renderGrid();
}

function selectedFiles() {
  return allItems.filter(it => selectedKeys.has(keyOf(it)))
    .map(it => ({ name: it.name, drawer: it.drawer ?? null }));
}

function updateSelectBar() {
  const n = selectedKeys.size;
  document.getElementById('selDelete').textContent = '删除' + (n ? ' (' + n + ')' : '');
  const all = allItems.length > 0 && n >= allItems.length;
  document.getElementById('selAll').textContent = all ? '取消全选' : '全选';
  // 移动目标下拉：未分类 + 全部抽屉
  const sel = document.getElementById('selTarget');
  sel.replaceChildren(...['', ...allDrawers].map(d => {
    const opt = document.createElement('option');
    opt.value = d;
    opt.textContent = d === '' ? '未分类' : d;
    return opt;
  }));
}

function bindSelectBar() {
  document.getElementById('selAll').addEventListener('click', () => {
    if (selectedKeys.size >= allItems.length) selectedKeys.clear();
    else allItems.forEach(it => selectedKeys.add(keyOf(it)));
    updateSelectBar();
    renderGrid();
  });
  document.getElementById('selDelete').addEventListener('click', () => {
    const files = selectedFiles();
    if (!files.length) return;
    post('deleteFiles', { files });   // 进回收站，可恢复，无需确认
    selectedKeys.clear();
    updateSelectBar();
  });
  document.getElementById('selMove').addEventListener('click', () => {
    const files = selectedFiles();
    if (!files.length) return;
    post('moveFiles', { files, drawer: document.getElementById('selTarget').value || null });
    selectedKeys.clear();
    updateSelectBar();
  });
  document.getElementById('selDone').addEventListener('click', () => setSelectMode(false));
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

// ---------- 文件渲染（v2 柱1：抽屉分组） ----------
// 抽屉 = 工作区根目录子文件夹；未分类 = 根目录散文件。
// 折叠状态记忆走 config.json（C# 落盘，跨重启可靠；appConfig.collapsedDrawers）。
let collapsedDrawers = new Set();

function renderGrid() {
  const grid = document.getElementById('fileGrid');
  const frag = document.createDocumentFragment();

  // Tab kind 过滤 + 名称过滤（P10：跨抽屉子串、忽略大小写）
  const q = filterText.trim().toLowerCase();
  const items = allItems
    .filter(it => matchesTab(it, currentTab))
    .filter(it => !q || it.name.toLowerCase().includes(q));
  const groups = new Map();   // 抽屉名 | null(未分类) -> 条目[]
  for (const it of items) {
    const key = it.drawer ?? null;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(it);
  }

  // 抽屉分组：全部视图下空抽屉也显示（拖放目标 + 状态提示）；分类视图/过滤中只显示有匹配项的组
  for (const d of allDrawers) {
    const g = groups.get(d) || [];
    if (currentTab !== 'all' && !groups.has(d)) continue;
    if (q && g.length === 0) continue;
    frag.append(buildSection(d, sortItems(g)));
  }
  // 未分类殿后
  if (groups.has(null)) frag.append(buildSection(null, sortItems(groups.get(null))));

  const add = document.createElement('div');
  add.className = 'section-add';
  add.textContent = '＋ 新建抽屉';
  add.title = '在工作区新建同名文件夹';
  add.addEventListener('click', () => {
    const name = (prompt('新抽屉名称（= 工作区文件夹名）：') || '').trim();
    if (name) post('drawerCreate', { name });
  });
  frag.append(add);

  grid.replaceChildren(frag);
  updateBadges();
}

function buildSection(drawer, items) {
  const key = drawer ?? '';
  const group = document.createElement('div');
  group.className = 'section-group' + (collapsedDrawers.has(key) ? ' collapsed' : '');
  group.dataset.drawer = key;   // 落点命中（hitTest）依据；'' = 未分类

  const header = document.createElement('div');
  header.className = 'section-header';
  const titleBox = document.createElement('div');
  titleBox.className = 'section-title-box';
  titleBox.innerHTML =
    '<svg class="toggle-arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="6 9 12 15 18 9"></polyline></svg>';
  const label = document.createElement('span');
  label.textContent = drawer ?? '未分类';
  titleBox.append(label);
  // P10：点击抽屉标题原地改名（= 文件夹重命名）；未分类不可改
  if (drawer !== null) {
    label.className = 'section-title-label';
    label.title = '点击改名';
    label.addEventListener('click', e => {
      e.stopPropagation();   // 别触发折叠
      renameDrawer(label, drawer);
    });
  }
  const divider = document.createElement('div');
  divider.className = 'section-divider-line';
  const count = document.createElement('span');
  count.className = 'section-count';
  count.textContent = items.length + ' 项';
  header.append(titleBox, divider, count);

  header.addEventListener('click', () => {
    const collapsed = group.classList.toggle('collapsed');
    if (collapsed) collapsedDrawers.add(key); else collapsedDrawers.delete(key);
    post('setConfig', { key: 'collapsedDrawers', value: [...collapsedDrawers] });
  });
  // 抽屉横栏右键 = 该文件夹的 Shell 菜单（改名/删除走系统原生）
  if (drawer !== null) {
    header.addEventListener('contextmenu', e => {
      e.preventDefault();
      post('contextMenu', { name: drawer, drawer: null });
    });
  }

  const inner = document.createElement('div');
  inner.className = 'file-grid';
  for (const it of items) inner.append(buildFileCard(it));

  group.append(header, inner);
  return group;
}

// 抽屉改名（原地输入框；Enter/blur 提交，Esc 取消）。实际重命名与数据迁移在 C# 侧。
function renameDrawer(labelEl, drawer) {
  const input = document.createElement('input');
  input.className = 'section-title-input';
  input.value = drawer;
  input.maxLength = 60;
  labelEl.replaceWith(input);
  input.focus();
  input.select();
  const commit = () => {
    const to = input.value.trim();
    input.replaceWith(labelEl);
    if (to && to !== drawer) post('drawerRename', { from: drawer, to });
  };
  // 编辑中点击输入框不触发折叠（事件截在输入框上）
  input.addEventListener('mousedown', e => e.stopPropagation());
  input.addEventListener('click', e => e.stopPropagation());
  input.addEventListener('blur', commit);
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') input.blur();
    if (e.key === 'Escape') { input.value = drawer; input.blur(); }
  });
}

function buildFileCard(it) {
  const card = document.createElement('div');
  card.className = 'file-card';
  card.title = (it.drawer ? it.drawer + '/' : '') + it.name + '\n' + it.mtime;
  card.dataset.name = it.name;          // copyDetected（Ctrl+C）命中依据
  card.dataset.drawer = it.drawer ?? '';

  // P7：图片/视频直接出缩略图，读不了的格式回退图标
  const thumb = document.createElement('div');
  thumb.className = 'file-thumb-box';
  const fileUrl = (it.drawer ? 'https://files.local/' + encodeURIComponent(it.drawer) + '/' : 'https://files.local/')
    + encodeURIComponent(it.name);
  if (it.kind === 'image') {
    const img = document.createElement('img');
    img.src = fileUrl;
    img.alt = it.name;
    img.loading = 'lazy';
    img.addEventListener('error', () => {
      thumb.innerHTML = '<span class="kind-icon">🖼️</span>'; // heic/psd 等浏览器不认的
    });
    thumb.append(img);
  } else if (it.kind === 'video') {
    const v = document.createElement('video');
    v.src = fileUrl + '#t=1';   // 媒体片段：直接显示第 1 秒画面
    v.preload = 'metadata';
    v.muted = true;
    v.addEventListener('error', () => {
      thumb.innerHTML = '<span class="kind-icon">🎬</span>'; // 解码不了的容器/编码
    });
    const play = document.createElement('span');
    play.className = 'video-badge';
    play.textContent = '▶';
    thumb.append(v, play);
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

  // P10：置顶星标（已置顶恒显 ★，未置顶悬停显 ☆；点击切换，不触发打开/勾选）
  const star = document.createElement('button');
  star.className = 'pin-star' + (it.pinned ? ' pinned' : '');
  star.textContent = it.pinned ? '★' : '☆';
  star.title = it.pinned ? '取消置顶' : '置顶（恒排组内最前）';
  star.addEventListener('click', e => {
    e.stopPropagation();
    post('pinFile', { name: it.name, drawer: it.drawer ?? null, pinned: !it.pinned });
  });
  card.append(star);

  // P4 交互（v2：全部带 drawer 定位）；P9 选择模式下点击 = 勾选而非打开
  const key = keyOf(it);
  if (selectMode && selectedKeys.has(key)) card.classList.add('selected');
  card.addEventListener('click', () => {
    if (selectMode) {
      if (selectedKeys.has(key)) selectedKeys.delete(key); else selectedKeys.add(key);
      card.classList.toggle('selected');
      updateSelectBar();
      return;
    }
    post('openPath', { name: it.name, drawer: it.drawer ?? null });
  });
  card.addEventListener('contextmenu', e => {
    e.preventDefault();
    post('contextMenu', { name: it.name, drawer: it.drawer ?? null });
  });
  // 拖出：HTML5 拖拽只作手势检测，实际 OLE 拖放由 C# DoDragDrop 执行；
  // 落回自己面板 = 抽屉间移动（P9）。选择模式下拖整组。
  card.draggable = true;
  card.addEventListener('dragstart', e => {
    e.preventDefault();
    const files = (selectMode && selectedKeys.has(key))
      ? selectedFiles()
      : [{ name: it.name, drawer: it.drawer ?? null }];
    post('startDragOut', { files });
  });

  return card;
}

// ---------- 桥：C# -> JS ----------
// WebView2 的宿主消息走 window.chrome.webview 的 message 事件（不是 window 的）
bridge?.addEventListener('message', e => {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'files':
      allItems = msg.items || [];
      allDrawers = msg.drawers || [];
      if (currentTab !== 'whiteboard') renderGrid(); else updateBadges();
      break;
    case 'setTab':
      setTab(msg.tab);
      break;
    case 'notes':
      allNotes = msg.notes || [];
      renderNoteWall();
      break;
    // ---------- P9: 拖放落点 ----------
    case 'dragHover': {
      // 悬停高亮光标下的抽屉横栏（x<0 = 拖离，清除）
      document.querySelectorAll('.drop-target').forEach(el => el.classList.remove('drop-target'));
      if (msg.x >= 0) {
        const el = document.elementFromPoint(msg.x, msg.y);
        const sec = el instanceof Element ? el.closest('.section-group') : null;
        sec?.querySelector('.section-header')?.classList.add('drop-target');
      }
      break;
    }
    case 'hitTest': {
      // 落点命中哪个分组：抽屉名 / null(未分类或空白)
      const el = document.elementFromPoint(msg.x, msg.y);
      const sec = el instanceof Element ? el.closest('.section-group') : null;
      post('hitResult', { drawer: sec ? (sec.dataset.drawer || null) : null });
      break;
    }
    case 'delDetected': {
      // Del 键：选中组优先，否则光标下的文件 -> 系统回收站。输入框聚焦时不拦截。
      const focus = document.activeElement;
      if (focus instanceof Element && focus.closest('input, textarea, select')) break;
      let files = null;
      if (selectMode && selectedKeys.size) {
        files = selectedFiles();
      } else {
        const el = document.elementFromPoint(msg.x, msg.y);
        const card = el instanceof Element ? el.closest('.file-card') : null;
        if (card && card.dataset.name)
          files = [{ name: card.dataset.name, drawer: card.dataset.drawer || null }];
      }
      if (files && files.length) post('deleteFiles', { files });
      break;
    }
    case 'pasteDetected': {
      // 光标在面板时的 Ctrl+V（C# 键态检测，无需焦点）。输入框聚焦时不拦截正常粘贴；
      // 文件视图 = 收纳到光标下的抽屉分组（无分组=未分类），白板页 = 直接建便签。
      const focus = document.activeElement;
      if (focus instanceof Element && focus.closest('input, textarea, select')) break;
      if (currentTab === 'whiteboard') { post('clipboardToNote'); break; }
      const el = document.elementFromPoint(msg.x, msg.y);
      const sec = el instanceof Element ? el.closest('.section-group') : null;
      post('clipboardSave', { drawer: sec ? (sec.dataset.drawer || null) : null });
      break;
    }
    case 'copyDetected': {
      // Ctrl+C 复制光标下的文件（FileDrop，可粘贴到资源管理器/面板）。
      // 输入框聚焦时正常复制文本；选择模式且该卡在选区内 = 整组。
      const focus = document.activeElement;
      if (focus instanceof Element && focus.closest('input, textarea, select')) break;
      const el = document.elementFromPoint(msg.x, msg.y);
      const card = el instanceof Element ? el.closest('.file-card') : null;
      if (!card || !card.dataset.name) break;
      const it = allItems.find(i => i.name === card.dataset.name
        && (i.drawer ?? '') === (card.dataset.drawer || ''));
      if (!it) break;
      const files = (selectMode && selectedKeys.has(keyOf(it)))
        ? selectedFiles()
        : [{ name: it.name, drawer: it.drawer ?? null }];
      post('copyFiles', { files });
      break;
    }
    case 'config': {
      // 设置项（C# ready/refresh 推送）；含抽屉折叠状态与排序模式 -> 应用
      appConfig = msg.config || {};
      collapsedDrawers = new Set(appConfig.collapsedDrawers || []);
      sortMode = appConfig.sortMode || 'time';
      document.getElementById('sortBox').value = sortMode;
      if (currentTab !== 'whiteboard') renderGrid();
      break;
    }
  }
});

// ---------- P5: 白板便签墙 ----------
// 无上限：每张便签 = notes/ 下一个 txt（C# NoteStore 管理）。
// P6：卡片只读渲染 Markdown（marked + DOMPurify，见 md.js），单击打开独立窗口。

let allNotes = [];

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
  box.title = '点击打开大窗口查看';

  const header = document.createElement('div');
  header.className = 'whiteboard-header';
  const title = document.createElement('span');
  title.className = 'whiteboard-title';
  title.textContent = n.title || ('便签 · ' + (n.mtime || ''));
  title.title = '点击改名';
  title.addEventListener('click', e => {
    e.stopPropagation(); // 别触发卡片的开窗
    renameNote(n, title);
  });
  const del = document.createElement('button');
  del.className = 'whiteboard-del';
  del.textContent = '删除';
  del.title = '删除这张便签';
  del.addEventListener('click', e => {
    e.stopPropagation();
    if (confirm('删除这张便签？内容不可恢复。')) post('noteDelete', { id: n.id });
  });
  header.append(title, del);

  // 只读渲染视图；编辑在独立窗口里完成
  const body = document.createElement('div');
  body.className = 'note-md';
  if ((n.content || '').trim()) body.innerHTML = renderMarkdown(n.content);
  else body.classList.add('note-md-empty');
  // 链接交给系统浏览器，防止把面板 WebView 导航走
  body.addEventListener('click', e => {
    const a = e.target.closest('a[href]');
    if (a) { e.preventDefault(); e.stopPropagation(); post('openLink', { url: a.href }); }
  });

  box.addEventListener('click', () => post('noteOpen', { id: n.id }));
  box.append(header, body);
  return box;
}

// ---------- P6: 便签卡片交互 ----------

function renameNote(n, titleEl) {
  const input = document.createElement('input');
  input.className = 'whiteboard-title-input';
  input.value = n.title || '';
  input.maxLength = 40;
  titleEl.replaceWith(input);
  input.focus();
  input.select();
  const commit = () => {
    const t = input.value.trim();
    if (t !== (n.title || '')) post('noteRename', { id: n.id, title: t }); // C# PushNotes 重渲染刷新标题
    input.replaceWith(titleEl);
  };
  input.addEventListener('blur', commit);
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') input.blur();
    if (e.key === 'Escape') { input.value = n.title || ''; input.blur(); }
  });
}
