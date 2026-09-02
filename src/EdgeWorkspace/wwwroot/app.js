// EdgeWorkspace 前端逻辑
// P0：静态骨架 + 桥就绪信号；数据/交互随 P1-P5 逐阶段接通。

const bridge = window.chrome?.webview ?? null;

// ---------- 桥：JS -> C# ----------
function post(type, payload = {}) {
  bridge?.postMessage(JSON.stringify({ type, ...payload }));
}

window.addEventListener('DOMContentLoaded', () => {
  bindTabs();
  bindButtons();
  post('ready');
});

// ---------- Tabs ----------
let currentTab = 'all';

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

// ---------- 桥：C# -> JS ----------
// P1 接入：window.addEventListener('message', e => { const msg = e.data; ... })
