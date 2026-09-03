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
let filterText = '';    // 搜索（P10：文件名或抽屉路径）
let sortMode = 'time';  // 排序模式 time|name|size|kind|frequent（P10，落 config）
let drawerOrder = [];   // 抽屉手动排序（视图序，拖 ⠿ 产生；未列者按名序补齐）

window.addEventListener('DOMContentLoaded', () => {
  bindTabs();
  bindButtons();
  bindSelectBar();
  bindSearch();
  bindLifecycle();
  bindSettings();
  post('ready');
});

// ---------- P12: 设置面板绑定 ----------
function bindSettings() {
  document.getElementById('setTheme').addEventListener('change', e =>
    post('setConfig', { key: 'theme', value: e.target.value }));
  document.getElementById('setStaleEnabled').addEventListener('change', e =>
    post('setConfig', { key: 'staleEnabled', value: e.target.checked }));
  document.getElementById('setStaleDays').addEventListener('change', e =>
    post('setConfig', { key: 'staleDays', value: Number(e.target.value) }));
  document.getElementById('setAutostart').addEventListener('change', e =>
    post('setConfig', { key: 'autostart', value: e.target.checked }));
  document.getElementById('btnBrowseWs').addEventListener('click', () => post('pickFolder'));
  document.getElementById('btnApplyWs').addEventListener('click', () =>
    post('setConfig', { key: 'workspacePath', value: document.getElementById('setWorkspacePath').value.trim() }));
}

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

// ---------- P11: 悬浮预览气泡 ----------
// 悬停卡片 800ms -> 卡片旁小气泡（约 410x520）；纯展示（pointer-events 穿透），
// 鼠标离开卡片 150ms 即隐，滑向相邻卡片 120ms 快速切换；选择模式下不预览。
// 图片/视频/PDF 直载 files.local；文本经 C# readText 读回（编码探测，防 GBK 乱码）。
const TEXT_EXTS = new Set(['txt','log','md','markdown','ini','cfg','conf','yml','yaml','nfo','tres','csv','json','xml','html','htm','css','scss','js','ts','jsx','tsx','py','lua','c','cpp','h','cs','java','go','rs','php','rb','sh','ps1','vbs','sql','toml']);

let hoverTimer = null;
let peekCloseTimer = null;
let peekItem = null;
const isPreviewOpen = () => peekItem !== null;

function showPreview(it, card) {
  peekItem = it;
  const box = document.getElementById('previewBox');
  const url = fileUrl(it);
  if (it.kind === 'image')
    box.innerHTML = '<img src="' + url + '" alt="">';
  else if (it.kind === 'video')
    box.innerHTML = '<video src="' + url + '" autoplay muted loop playsinline></video>';
  else if (it.ext === 'pdf')
    box.innerHTML = '<iframe src="' + url + '"></iframe>';
  else if (TEXT_EXTS.has(it.ext.toLowerCase())) {
    box.innerHTML = '<pre class="peek-text">…</pre>';
    post('readText', { name: it.name, drawer: it.drawer ?? null });
  } else return;   // 其余类型不预览
  document.getElementById('previewName').textContent = (it.drawer ? it.drawer + '/' : '') + it.name;
  document.getElementById('previewOverlay').hidden = false;
  positionPeek(card);
}

/// 气泡锚定：优先卡片左侧，空间不足靠右；垂直居中对齐卡片并夹在面板内。
function positionPeek(card) {
  const win = document.getElementById('previewWindow');
  const host = win.parentElement;                       // .preview-overlay（absolute 撑满 .drawer）
  const cr = card.getBoundingClientRect();
  const dr = host.getBoundingClientRect();
  const w = win.offsetWidth, h = win.offsetHeight;
  let left = cr.left - dr.left - w - 12;
  if (left < 8) left = Math.min(cr.right - dr.left + 12, dr.width - w - 8);
  left = Math.max(8, left);
  let top = cr.top - dr.top + cr.height / 2 - h / 2;
  top = Math.max(8, Math.min(top, dr.height - h - 40));
  win.style.left = left + 'px';
  win.style.top = top + 'px';
}

function closePeek() {
  clearTimeout(hoverTimer);
  clearTimeout(peekCloseTimer);
  peekItem = null;
  document.getElementById('previewOverlay').hidden = true;
  document.getElementById('previewBox').innerHTML = '';
}

// ---------- P12: 过期提醒与归档 ----------
const ARCHIVE = '归档';   // 特殊抽屉：不进主列表，底部面板呈现（data-drawer=归档 可作拖放目标）

function isStale(it) {
  if (!appConfig.staleEnabled || it.drawer === ARCHIVE) return false;
  const days = appConfig.staleDays || 14;
  const t = Date.parse((it.mtime || '').replace(' ', 'T'));
  return !isNaN(t) && (Date.now() - t) > days * 86400000;
}

function staleFiles() { return allItems.filter(isStale); }

function updateStaleInfo() {
  const n = staleFiles().length;
  const on = appConfig.staleEnabled;
  document.getElementById('staleInfo').hidden = !on || n === 0;
  document.getElementById('btnArchiveAll').hidden = !on || n === 0;
  document.getElementById('staleCount').textContent = n;
  document.getElementById('staleDaysText').textContent = appConfig.staleDays || 14;
}

function bindLifecycle() {
  document.getElementById('btnArchiveAll').addEventListener('click', () => {
    const files = staleFiles().map(it => ({ name: it.name, drawer: it.drawer ?? null }));
    if (files.length) post('moveFiles', { files, drawer: ARCHIVE });   // 可逆：归档面板可移回
  });
  document.getElementById('btnArchivePanel').addEventListener('click', () => toggleArchive());
  document.getElementById('archiveClose').addEventListener('click', () => toggleArchive(false));
  document.getElementById('archiveEmpty').addEventListener('click', () => {
    const files = allItems.filter(it => it.drawer === ARCHIVE).map(it => ({ name: it.name, drawer: ARCHIVE }));
    if (files.length && confirm('清空归档？共 ' + files.length + ' 项，将移入系统回收站。'))
      post('deleteFiles', { files });
  });
}

function toggleArchive(force) {
  const panel = document.getElementById('archivePanel');
  panel.hidden = force !== undefined ? !force : !panel.hidden;
  document.getElementById('btnArchivePanel').textContent = panel.hidden ? '归档 ▲' : '归档 ▼';
  if (!panel.hidden) renderArchive();
}

function renderArchive() {
  const items = allItems.filter(it => it.drawer === ARCHIVE);
  document.getElementById('archiveCount').textContent = items.length;
  const list = document.getElementById('archiveList');
  list.replaceChildren();
  for (const it of items) {
    const row = document.createElement('div');
    row.className = 'archive-row';
    const name = document.createElement('span');
    name.className = 'archive-row-name';
    name.textContent = it.name;
    name.title = it.name + ' · ' + it.mtime;
    const restore = document.createElement('button');
    restore.className = 'archive-btn';
    restore.textContent = '移回';
    restore.title = '移回未分类';
    restore.addEventListener('click', () => post('moveFiles', { files: [{ name: it.name, drawer: ARCHIVE }], drawer: null }));
    const del = document.createElement('button');
    del.className = 'archive-btn archive-del';
    del.textContent = '删除';
    del.addEventListener('click', () => post('deleteFiles', { files: [{ name: it.name, drawer: ARCHIVE }] }));
    row.append(name, restore, del);
    list.append(row);
  }
  if (!items.length) {
    const empty = document.createElement('div');
    empty.className = 'archive-empty';
    empty.textContent = '归档为空（一键归档收进来的文件会出现在这里）';
    list.append(empty);
  }
}

// ---------- 文件渲染（v2 柱1：抽屉分组） ----------
// 抽屉 = 工作区根目录子文件夹；未分类 = 根目录散文件。
// 折叠状态记忆走 config.json（C# 落盘，跨重启可靠；appConfig.collapsedDrawers）。
let collapsedDrawers = new Set();

function renderGrid() {
  const grid = document.getElementById('fileGrid');
  const frag = document.createDocumentFragment();

  // Tab kind 过滤 + 搜索（P10：文件名或抽屉路径；抽屉命中则其全部直属文件随行可见，
  // 后代路径天然包含祖先路径 -> 整个子树命中）
  const q = filterText.trim().toLowerCase();
  const items = allItems
    .filter(it => matchesTab(it, currentTab))
    .filter(it => !q
      || it.name.toLowerCase().includes(q)
      || (it.drawer && it.drawer.toLowerCase().includes(q)));
  const groups = new Map();   // 抽屉路径 | null(未分类) -> 条目[]
  for (const it of items) {
    const key = it.drawer ?? null;
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(it);
  }

  // 抽屉树（v2：递归嵌套）：allDrawers 是路径清单（"父/子"），按父路径归拢
  const children = new Map();   // '' | 父路径 -> [子路径]
  for (const p of allDrawers) {
    const i = p.lastIndexOf('/');
    const parent = i < 0 ? '' : p.slice(0, i);
    if (!children.has(parent)) children.set(parent, []);
    children.get(parent).push(p);
  }
  // 可见抽屉：有可见内容的路径及其全部祖先；搜索时抽屉路径命中的也可见（含祖先链）
  const active = new Set();
  const markChain = p => {
    while (p) {
      active.add(p);
      const i = p.lastIndexOf('/');
      p = i < 0 ? '' : p.slice(0, i);
    }
  };
  for (const key of groups.keys()) markChain(key);
  if (q) for (const p of allDrawers)
    if (p.toLowerCase().includes(q)) markChain(p);
  const showAll = currentTab === 'all' && !q;   // 全部视图（未搜索）恒显示所有抽屉

  // 抽屉排序：手动序优先（config），未列者按名序补齐（P10：拖 ⠿ 排序）
  const orderIdx = new Map(drawerOrder.map((p, i) => [p, i]));
  const sortDrawers = list => [...list].sort((a, b) =>
    (orderIdx.get(a) ?? 1e9) - (orderIdx.get(b) ?? 1e9) || a.localeCompare(b, 'zh'));

  const buildLevel = parent => {
    const out = [];
    for (const p of sortDrawers(children.get(parent) || [])) {
      if (p === ARCHIVE) continue;   // 归档走底部面板，不进主列表（P12）
      if (!showAll && !active.has(p)) continue;
      out.push(buildSection(p, sortItems(groups.get(p) || []), buildLevel(p)));
    }
    return out;
  };
  frag.append(...buildLevel(''));

  // 未分类殿后
  if (groups.has(null)) frag.append(buildSection(null, sortItems(groups.get(null)), []));

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
  updateStaleInfo();
  if (!document.getElementById('archivePanel').hidden) renderArchive();
}

function buildSection(drawer, items, subs) {
  const key = drawer ?? '';
  const group = document.createElement('div');
  group.className = 'section-group' + (collapsedDrawers.has(key) ? ' collapsed' : '');
  group.dataset.drawer = key;   // 落点命中（hitTest）依据；'' = 未分类
  if (drawer !== null) group.style.marginLeft = ((drawer.split('/').length - 1) * 14) + 'px';   // 嵌套缩进

  const header = document.createElement('div');
  header.className = 'section-header';
  const titleBox = document.createElement('div');
  titleBox.className = 'section-title-box';
  titleBox.innerHTML =
    '<svg class="toggle-arrow" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5"><polyline points="6 9 12 15 18 9"></polyline></svg>';
  // P10：拖动排序把手（箭头之前，负边距藏进横栏左内边距，悬停浮现）。
  // 不能用 HTML5 DnD：我们注册在 Chromium 窗口上的 OLE 放置目标会拦截并拒绝
  // Chromium 内部拖放（非 CF_HDROP -> 禁止光标），故走 pointer 手动拖拽。
  if (drawer !== null) {
    const grip = document.createElement('span');
    grip.className = 'section-grip';
    grip.textContent = '⠿';
    grip.title = '拖动排序（同级）';
    titleBox.prepend(grip);
    grip.addEventListener('pointerdown', e => {
      if (e.button !== 0) return;
      e.preventDefault();   // 阻止原生拖拽/选择
      grip.setPointerCapture(e.pointerId);
      startDrawerDrag(drawer, group, grip);
    });
    grip.addEventListener('click', e => e.stopPropagation());   // 纯点击不折叠
  }
  const label = document.createElement('span');
  label.textContent = drawer === null ? '未分类'
    : drawer.slice(drawer.lastIndexOf('/') + 1);   // 只显示末级名（路径全名在 title）
  titleBox.append(label);
  if (drawer !== null) {
    label.className = 'section-title-label';
    label.title = '点击改名（路径：' + drawer + '）';
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
  // 抽屉横栏右键 = 该文件夹的 Shell 菜单（改名/删除走系统）
  if (drawer !== null) {
    header.addEventListener('contextmenu', e => {
      e.preventDefault();
      post('contextMenu', { name: drawer, drawer: null });
    });
  }

  // 体：次级抽屉在前（资源管理器习惯），随后本组文件网格；折叠时整体隐藏
  const body = document.createElement('div');
  body.className = 'section-body';
  body.append(...subs);
  const inner = document.createElement('div');
  inner.className = 'file-grid';
  for (const it of items) inner.append(buildFileCard(it));
  body.append(inner);

  group.append(header, body);
  return group;
}

// 抽屉手动排序：pointer 拖拽（把手 capture），同级横栏上/下半插前/插后。
// 重排全量序（未列者保持名序尾随），落 config 即时生效。
function startDrawerDrag(drawer, groupEl, grip) {
  const parentOf = p => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '');
  let moved = false;

  const clearMarks = () => document.querySelectorAll('.drag-over-before, .drag-over-after')
    .forEach(el => el.classList.remove('drag-over-before', 'drag-over-after'));

  const onMove = ev => {
    if (!moved) {
      moved = true;
      groupEl.classList.add('dragging');
      document.body.style.cursor = 'grabbing';
    }
    clearMarks();
    const el = document.elementFromPoint(ev.clientX, ev.clientY);
    const sec = el instanceof Element ? el.closest('.section-group') : null;
    const target = sec ? sec.dataset.drawer : '';
    if (!target || target === drawer || parentOf(target) !== parentOf(drawer)) return;
    const h = sec.querySelector('.section-header');
    const r = h.getBoundingClientRect();
    h.classList.add(ev.clientY < r.top + r.height / 2 ? 'drag-over-before' : 'drag-over-after');
  };

  const finish = () => {
    grip.removeEventListener('pointermove', onMove);
    grip.removeEventListener('pointerup', finish);
    grip.removeEventListener('pointercancel', finish);
    const marked = document.querySelector('.drag-over-before, .drag-over-after');
    const before = marked ? marked.classList.contains('drag-over-before') : false;
    const target = marked ? marked.closest('.section-group').dataset.drawer : null;
    clearMarks();
    groupEl.classList.remove('dragging');
    document.body.style.cursor = '';
    if (moved && target) reorderDrawer(drawer, target, before);
  };

  grip.addEventListener('pointermove', onMove);
  grip.addEventListener('pointerup', finish);
  grip.addEventListener('pointercancel', finish);
}

function reorderDrawer(src, target, before) {
  const idx = new Map(drawerOrder.map((p, i) => [p, i]));
  const current = [...allDrawers].sort((a, b) =>
    (idx.get(a) ?? 1e9) - (idx.get(b) ?? 1e9) || a.localeCompare(b, 'zh'));
  const order = current.filter(p => p !== src);
  const t = order.indexOf(target);
  if (t < 0) return;
  order.splice(before ? t : t + 1, 0, src);
  drawerOrder = order;
  post('setConfig', { key: 'drawerOrder', value: order });
  if (currentTab !== 'whiteboard') renderGrid();
}

// 抽屉改名（原地输入框；Enter/blur 提交，Esc 取消）。编辑末级名，路径父级保持；实际重命名与数据迁移在 C# 侧。
function renameDrawer(labelEl, drawerPath) {
  const last = drawerPath.slice(drawerPath.lastIndexOf('/') + 1);
  const input = document.createElement('input');
  input.className = 'section-title-input';
  input.value = last;
  input.maxLength = 60;
  labelEl.replaceWith(input);
  input.focus();
  input.select();
  const commit = () => {
    const nm = input.value.trim();
    input.replaceWith(labelEl);
    if (nm && nm !== last) {
      const parent = drawerPath.includes('/') ? drawerPath.slice(0, drawerPath.lastIndexOf('/') + 1) : '';
      post('drawerRename', { from: drawerPath, to: parent + nm });
    }
  };
  // 编辑中点击输入框不触发折叠（事件截在输入框上）
  input.addEventListener('mousedown', e => e.stopPropagation());
  input.addEventListener('click', e => e.stopPropagation());
  input.addEventListener('blur', commit);
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') input.blur();
    if (e.key === 'Escape') { input.value = last; input.blur(); }
  });
}

function fileUrl(it) {
  const segs = it.drawer ? it.drawer.split('/').map(encodeURIComponent).join('/') + '/' : '';
  return 'https://files.local/' + segs + encodeURIComponent(it.name);
}

function buildFileCard(it) {
  const card = document.createElement('div');
  card.className = 'file-card';
  card.title = (it.drawer ? it.drawer + '/' : '') + it.name + '\n' + it.mtime;
  card.dataset.name = it.name;          // copyDetected（Ctrl+C）命中依据
  card.dataset.drawer = it.drawer ?? '';

  // P7：图片/视频直接出缩略图，读不了的格式回退图标；抽屉路径按段编码（'/' 不转义）
  const thumb = document.createElement('div');
  thumb.className = 'file-thumb-box';
  if (it.kind === 'image') {
    const img = document.createElement('img');
    img.src = fileUrl(it);
    img.alt = it.name;
    img.loading = 'lazy';
    img.addEventListener('error', () => {
      thumb.innerHTML = '<span class="kind-icon">🖼️</span>'; // heic/psd 等浏览器不认的
    });
    thumb.append(img);
  } else if (it.kind === 'video') {
    const v = document.createElement('video');
    v.src = fileUrl(it) + '#t=1';   // 媒体片段：直接显示第 1 秒画面
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

  // P11：复制 Markdown 链接（文件进便签 V1；图片为内嵌语法）
  const link = document.createElement('button');
  link.className = 'card-link';
  link.textContent = '🔗';
  link.title = '复制 Markdown 链接';
  link.addEventListener('click', e => {
    e.stopPropagation();
    const url = fileUrl(it);
    post('copyText', {
      text: it.kind === 'image' ? '![' + it.name + '](' + url + ')' : '[' + it.name + '](' + url + ')',
    });
    link.textContent = '✓';
    setTimeout(() => { link.textContent = '🔗'; }, 900);
  });
  card.append(link);

  // P11：悬停 800ms -> 卡片旁悬浮气泡（选择模式下不预览）
  card.addEventListener('mouseenter', () => {
    if (selectMode) return;
    clearTimeout(hoverTimer);
    clearTimeout(peekCloseTimer);   // 从别的卡片滑过来，别让气泡消失
    hoverTimer = setTimeout(() => showPreview(it, card), isPreviewOpen() ? 120 : 800);
  });
  card.addEventListener('mouseleave', () => {
    clearTimeout(hoverTimer);
    if (isPreviewOpen()) {
      clearTimeout(peekCloseTimer);
      peekCloseTimer = setTimeout(closePeek, 150);   // 150ms 宽限给相邻卡片接管
    }
  });

  // P12：过期灰显（仅视觉标记，功能不受影响）
  if (isStale(it)) card.classList.add('stale');

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
      // 落点命中哪个分组：抽屉名 / null(未分类或空白)。[data-drawer] 同时覆盖
      // section-group 与底部归档面板（拖到归档面板上 = 移入归档，P12）
      const el = document.elementFromPoint(msg.x, msg.y);
      const sec = el instanceof Element ? el.closest('[data-drawer]') : null;
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
    case 'textPreview': {
      // readText 回执：校验仍是当前预览的文件再填充（防快速切换串台）
      if (peekItem && peekItem.name === msg.name && (peekItem.drawer ?? '') === (msg.drawer || '')) {
        const pre = document.querySelector('#previewBox .peek-text');
        if (pre) pre.textContent = msg.text;
      }
      break;
    }
    case 'folderPicked':
      document.getElementById('setWorkspacePath').value = msg.path || '';
      break;
    case 'theme':
      // P13 皮肤切换（C# 推送；CSS 变量组按 html[data-theme] 切换）
      document.documentElement.dataset.theme = msg.theme || 'white';
      document.getElementById('setTheme').value = msg.theme || 'white';
      break;
    case 'config': {
      // 设置项（C# ready/refresh 推送）；含折叠状态/排序模式/手动抽屉序/生命周期 -> 应用
      appConfig = msg.config || {};
      collapsedDrawers = new Set(appConfig.collapsedDrawers || []);
      sortMode = appConfig.sortMode || 'time';
      drawerOrder = appConfig.drawerOrder || [];
      document.getElementById('sortBox').value = sortMode;
      document.getElementById('setStaleEnabled').checked = appConfig.staleEnabled !== false;
      document.getElementById('setStaleDays').value = String(appConfig.staleDays || 14);
      document.getElementById('setAutostart').checked = !!appConfig.autostart;
      document.getElementById('setWorkspacePath').value = appConfig.workspacePath || '';
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
