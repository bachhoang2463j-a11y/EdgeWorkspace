# EdgeWorkspace 技术规格书（SPEC）

> 轻量 Windows 桌面"临时工作区"应用：屏幕右缘唤出的文件收纳面板 + Markdown 白板便签墙。
> 本文档是开发与维护的唯一依据；改动先改 SPEC 再动代码。
> 阶段：P0 骨架 → P1 实时列表 → P2 分类 → P3 贴边唤出 → P4 拖放 → P5 便签墙 →
> P6 Markdown 渲染 + 独立便签窗口 → P7 缩略图 + 拖入/唤出修复。

## 1. 技术栈与运行环境

| 项 | 值 |
|---|---|
| 框架 | .NET 8 WinForms（SDK 8.0.412） |
| UI 层 | WebView2 加载本地 `wwwroot/`（HTML/CSS/JS），多窗口共享同一 Environment |
| 工作区 | `D:\Workspace_Temp`（`MainForm.WorkspacePath` 硬编码，尚未接 config.json） |
| 便签数据 | exe 同目录 `notes/`（每便签一个 txt + index.json 标题索引） |
| 目标系统 | Windows 10 19045+ / Windows 11 |

**架构原则**：C# 只做桌面能力（文件系统、窗口、热键、拖放、持久化），UI 全部在 HTML/JS。
两者通过 WebView2 消息桥通信（§4）。`wwwroot/` 改动无需重编译（构建时 CopyToOutputDirectory）。

## 2. 目录规划

```
EdgeWorkspace/
├── docs/SPEC.md
├── README.md
└── src/EdgeWorkspace/
    ├── EdgeWorkspace.csproj
    ├── Program.cs            # 入口：Application.Run(MainForm)
    ├── MainForm.cs           # 主面板：贴边/热键/收起/拖放/消息桥/便签窗口管理
    ├── NoteWindow.cs         # 独立便签窗口（每便签一个实例，WebView2 共享环境）
    ├── NoteStore.cs          # notes/ 目录：txt 正文 + index.json 标题，增删改查
    ├── FileMetaStore.cs      # meta.json 文件元数据仓（置顶/打开计数，v2 柱2）
    ├── FileDropTarget.cs     # 自定义 OLE 放置目标（拖入收纳关键，见 §5.5）
    ├── FileOps.cs            # 打开/定位/回收站删除/Shell 右键菜单/移入工作区
    ├── FileScanner.cs        # 目录扫描 → 条目列表
    ├── WorkspaceWatcher.cs   # FileSystemWatcher 封装，防抖后推送
    ├── Native.cs             # Win32 原语：光标/热键/桌面判定/OLE 拖放注册
    └── wwwroot/
        ├── index.html / app.js / style.css   # 面板页
        ├── note.html / note.js               # 便签窗口页（查看态/编辑态）
        ├── md.js                             # 共享 Markdown 渲染管道
        └── vendor/                           # 固定版本本地库，零运行时外部依赖
            ├── marked.umd.js                 # 18.0.11（GFM + breaks）
            └── purify.min.js                 # DOMPurify 3.4.14（消毒）
```

## 3. 数据模型

### 3.1 文件条目（C# → JS）

```json
{ "name": "策划案.docx", "isFolder": false, "ext": "docx", "kind": "doc",
  "drawer": null,               // null=根目录散文件（未分类）；"名"=在名为「名」的抽屉内
  "pinned": false, "openCount": 3,   // meta.json 合并（v2 柱2 骨架，P10 起有 UI）
  "size": 25090, "mtime": "2026-09-03 10:27" }
```

**两级扫描（v2 柱1，P8）**：工作区根目录的每个子文件夹 = 一个抽屉（`drawers` 随 files
消息推送，按名称序）；抽屉内直属文件归该抽屉组，抽屉内的子文件夹以文件夹卡片呈现，
更深不递归；根目录散文件 = 未分类组。所有指向文件的消息（`openPath/revealItem/
contextMenu/startDragOut`）都带 `drawer` 参数，路径 = `工作区/抽屉/文件名`。
缩略图 URL 为 `files.local/<抽屉>/<文件名>`（逐段 encodeURIComponent）。

`kind` 判定口径见 §6。

### 3.2 便签

- **无限量**：`notes/noteN.txt`，一个文件一张便签，内容为 **Markdown 源码**（UTF-8）
- 标题存 `notes/index.json`：`{ "note1": { "title": "标题" } }`（P8 起对象结构；旧字符串
  格式按"仅标题"兼容读取；P13 贴纸将扩展 `tile: { on, x, y, w, h }` 字段）
- 排序按文件 mtime 倒序；改名会 Touch（顶到最前）；删除删文件并清标题
- `index.json` 写入非原子（沿用裸 `Write.WriteAllText`，原子写为可选加固项）

### 3.3 config.json

csproj 中有 CopyToOutputDirectory，但当前工作区路径等并未接入（硬编码）；保留占位。

## 4. C# ↔ JS 桥协议

所有消息 `type` 字段必填；JSON 走 `JsonSerializerDefaults.Web`（camelCase）；未知 type 静默忽略。

### 4.1 面板页（index.html / app.js）

JS → C#（`window.chrome.webview.postMessage`）：

| type | payload | 语义 |
|---|---|---|
| `ready` / `refresh` | — | 就绪请求首推 / 手动刷新 |
| `openPath` / `revealItem` | `{name, drawer}` | 系统打开 / 资源管理器定位（drawer=null 为根目录；openPath 顺带累计 openCount） |
| `openFolder` | — | 资源管理器打开工作区 |
| `contextMenu` | `{name, drawer}` | Shell 原生右键菜单（抽屉横栏右键 = 该文件夹的菜单） |
| `startDragOut` | `{name, drawer}` | C# 发起 OLE 拖出（DoDragDrop） |
| `drawerCreate` | `{name}` | 新建抽屉 = 工作区新建同名文件夹 |
| `noteCreate` / `noteDelete` | `{id}` | 新建 / 删除便签（删时关窗防复活） |
| `noteRename` | `{id, title}` | 改名（写 index.json + Touch + 重推） |
| `noteOpen` | `{id}` | 打开/聚焦该便签的独立窗口 |
| `openLink` | `{url}` | 渲染出的链接交系统浏览器（仅放行 http/https） |
| `setPinned` | `{pinned}` | 钉住态 |

C# → JS（`PostWebMessageAsJson`）：

| type | payload | 时机 |
|---|---|---|
| `files` | `{items, total, drawers}` | 启动 / watcher / 拖入后（drawers=抽屉名清单，按名称序） |
| `notes` | `{notes: [{id, title, content, mtime}]}` | 启动 / 增删改名后 |
| `setTab` | `{tab}` | 唤出时切换视图（鼠标→whiteboard，拖放→all） |

### 4.2 便签窗口页（note.html / note.js）

| 方向 | type | payload | 语义 |
|---|---|---|---|
| JS → C# | `ready` | — | 请求便签数据（窗口身份由 C# 侧维护） |
| JS → C# | `noteSave` | `{content}` | 保存正文（无 id——窗口知道自己是哪张） |
| JS → C# | `noteRename` | `{title}` | 窗口内改名 + 更新窗口标题栏 |
| JS → C# | `openLink` | `{url}` | 同面板 |
| C# → JS | `note` | `{note}` | ready 后推送完整便签 |

## 5. 窗口与交互规格

### 5.1 主面板

无边框、TopMost、不进任务栏；宽 `max(420, WORKAREA/3)` × 高 WORKAREA；停靠右缘，
隐藏态完全滑出屏外（`Visible=false`）。收起三通道（MouseLeave 600ms 宽限 / 失焦 300ms 复核 /
100ms 看门狗轮询兜底），钉住/拖放悬停/菜单打开期间挂起。

### 5.2 唤出（100ms 轮询 `GetCursorPos`，两个分支）

- **鼠标贴边**：X ≥ 右缘-8px 且桌面暴露（`WindowFromPoint` 命中类名 ∈
  Progman/WorkerW/SHELLDLL_DefView/SysListView32/Shell_TrayWnd/RainmeterMeterWindow；
  应用窗口盖住右缘时**有意不唤出**）；前台全屏应用不唤出 → 唤出 + 按光标上下半屏分流视图
  （上半屏 → 全部文件，下半屏 → 白板便签）
- **拖拽贴边**（P7 修复）：按住左键（`GetAsyncKeyState(VK_LBUTTON)`，OLE 拖拽期间为真）
  贴右缘 → **绕过桌面/全屏检查**唤出 + 切全部 Tab。面板隐藏时右缘没有窗口可命中
  DragEnter，拖拽唤出必须走这条路
- 动画 180ms 缓出；唤出后 1.2s 内看门狗不收

### 5.3 热键

`Ctrl+Shift+Z`（RegisterHotKey 全局，无视焦点）：可见→收起；隐藏→唤出 + 切白板 +
`Activate()` 抢前台置顶（P7）。

### 5.4 拖放（P7 彻底修复）

**关键事实**：OLE 的放置目标解析**不沿父链上溯**——光标命中的最深层窗口
（WebView2 的 `Chrome_RenderWidgetHostHWND`）没有注册目标就直接判拒绝；
`AllowExternalDrop=false` 只是不让网页收，**不会透传给宿主窗体**。因此窗体
`AllowDrop=true` 在 WebView2 铺满窗体后永远收不到事件（P4 时代拖入从未真正工作过）。

**方案**：`FileDropTarget` 实现 COM `IDropTarget`（DragQueryFile 读 CF_HDROP），
由 `ApplyFileDropTargets()` 逐窗口注册到**窗体 + WebView2 全部子窗口**
（`RegisterDragDrop`，先 `RevokeDragDrop` 防重复）；`NavigationCompleted` 后重跑
（渲染器可能重建子窗口）。拖入悬停挂起收起并切【全部】；Drop 后逐文件
`FileOps.MoveInto`（重名追加 " (n)"）+ 立即推送。拖离（DragLeave）解除挂起。

拖出（面板 → 资源管理器）：前端 dragstart 手势检测 → C# `DoDragDrop` OLE 源（不变）。

### 5.5 独立便签窗口（P6）

- 每张便签一个 `NoteWindow`（`Dictionary<noteId, NoteWindow>` 复用/聚焦），
  WebView2 复用主面板 Environment（共享浏览器进程），独立 app.local 映射
- 可拖动 / 可最大化 / 任务栏可见 / 760×640 起（最小 420×320）；关窗不退出应用
- **查看态**（默认）：`renderMarkdown`（marked + DOMPurify）渲染，宽屏内容列限宽居中；
  **编辑态**：标题输入 + Markdown 源码 textarea；Ctrl+S 保存 / Esc 取消
- 保存后本地渲染刷新 + 通知主面板 `PushNotes`/`PushFiles`；主面板删除便签时关其窗口

### 5.6 Markdown 渲染管道（md.js，两页共用）

`marked.use({ gfm: true, breaks: true })`（单换行→`<br>`，符合便签短行习惯）→
**渲染前转义 `<`**（伪 HTML 块如酒馆 `<Status_block>` 会被 marked 当原生 HTML 透传，
块内换行按 HTML 语义折叠丢失；转义后走段落路径保住换行，marked 实测 T3/T4 验证）→
`DOMPurify.sanitize(marked.parse(src))`。消毒是必须的：页面持有 openPath 等 bridge
权限，粘贴进便签的恶意 HTML 不消毒会在 app.local 上下文执行。卡片与窗口共用 `.note-md`
排版（便签黄主题）。**富文本路线已定案弃用**（Vditor 525 文件 + WYSIWYG 稳定性教训，
参照花笺 floral-notepaper 的"纯文本编辑 + 只读渲染"模型）。

## 6. 分类口径（kind 映射）与缩略图

| kind | 扩展名 |
|---|---|
| folder | 目录 |
| doc | txt log md … pdf js ts py json xml html css c cs 等文本/文档 |
| image | jpg jpeg png gif bmp webp tif tiff ico svg psd heic |
| video | mp4 mkv avi mov wmv flv webm m4v rmvb |
| audio | mp3 wav flac ogg aac m4a wma |
| archive | zip rar 7z tar gz bz2 xz iso |
| app | exe msi bat cmd lnk dll appx |
| other | 其余 |

缩略图（P7）：C# 把工作区映射为 `files.local` 虚拟主机；图片用 `<img loading="lazy">`
直载；视频用 `<video preload="metadata" src="...#t=1">` 显示第 1 秒帧 + ▶ 徽标；
解码失败（heic/psd/部分编码）onerror 回退 emoji 图标。文件名经 encodeURIComponent。

## 7. 构建与运行

```
cd src/EdgeWorkspace
dotnet build -c Release
dotnet run -c Release
```

调试日志：exe 同目录 `bridge.log`（推送、唤出、拖放、异常全量落盘，排障第一现场）。

## 8. 已知取舍 / 待办

- 第一期不做：面板内自定义分类抽屉、文件抽屉间拖动、多工作区
- 便签数据在 `bin/` 下，`dotnet clean`/删 bin 会连数据一起删（可选迁移到 %APPDATA%）
- `NoteStore` 写入非原子；config.json 未接入（工作区路径硬编码）
- 桌面偶发唤不出（疑似壁纸引擎类全屏覆盖层拦截 `IsDesktopAt` 白名单）——待复现
- `dirtyNotes`（app.js）为 P5 遗留死代码，待清理

## 9. v2 路线图：19 项已确认功能的架构决策（P8–P13）

> 需求确认（2026-09）：PM 提案全选——收纳移动 4（抽屉间拖拽、去重检测、剪贴板收纳、
> 批量多选）+ 查找排序 4（名称过滤、置顶、多种排序、常用优先）+ 预览联动 4（txt/md 便签
> 窗口打开、图片灯箱、视频悬停预览、文件进便签）+ 生命周期 3（过期提醒、设置面板、回收站
> 恢复），另加开机自启、性能与动效（架构预留、最后统一优化）。上下半屏贴边分流与 MD 换行
> 修复已随本节先行实施（§5.2 / §5.6）。

### 9.1 四根架构柱（后续功能全部"便宜"的前提）

**柱 1 · 抽屉 = 根目录子文件夹（零配置数据模型）**
不设 drawers.json：工作区根目录每个子文件夹天然即一个抽屉，根目录散文件 = 未分类；
资源管理器里手动建/删/改名文件夹，面板经 watcher 自动跟进。扫描两级：根文件 +
每个子文件夹的直属文件与子文件夹卡片（更深不递归）。抽屉间移动 = `File.Move`，
与外部资源管理器双向同构。现存的「The Sims 4 模组」等文件夹将自动成为抽屉。

**柱 2 · 文件元数据仓 `meta.json`（exe 同目录）**
`{ "工作区相对路径": { pinned, openCount, lastOpened } }`——置顶聚合、常用优先、
未来的标签/备注都只是往 map 加字段。打开文件时 C# 计数；推送时合并进 files 消息
（items 带 pinned/openCount），前端零额外请求。落盘带防抖。

**柱 3 · 单向推送 + 前端派生视图（纯数据流）**
C# 只推**带 drawer 字段的扁平列表**；分组、过滤、排序、过期标记、置顶优先全部由前端
从 `allItems` 派生计算。新增消息仅动作类：`moveFile / setPinned / clipboardSave /
deleteFiles / restoreTrash / setConfig / openTextFile`。设置接通闲置的 config.json：
工作区路径、默认排序、过期天数、开机自启（HKCU Run 键）。

**柱 4 · 拖放统一 OLE 单管线（一条管线三种语义）**
面板内拖文件卡仍走现有 `startDragOut`（C# `DoDragDrop`）——**落回自己面板上**时
FileDropTarget 会收到（带屏幕坐标）：命中抽屉横栏 = 抽屉间移动；落未分类区 = 移出抽屉；
拖出面板 = 交给资源管理器。无需 HTML5 内部拖放。去重检测同点做：同名同大小 →
C# 原生对话框「替换 / 保留两者(自动编号) / 跳过」，不绕 JS。

**其余地基**：`NoteWindow` 泛化（目标从便签 id 泛化为 `note:noteN | file:相对路径`，
txt/md 文件直接用便签窗口打开+编辑+保存，保存后 PushFiles 刷新）；回收站 = 工作区隐藏
`.ews-trash/` 文件夹 + trash.json 清单（恢复 = 移回，比 Shell 回收站 COM 可控）；
性能预留——渲染事件委托（现每卡 5 监听器）、`content-visibility: auto` 屏外分区、
推送重渲染保持滚动位置；动画只走 transform/opacity 合成器路径。

### 9.2 分期（每期一个提交，可随时插入调整）

| 期 | 内容 |
|---|---|
| P8 地基 | 两级扫描 + 抽屉分组渲染（section-group 折叠/localStorage 记忆）+ meta.json 骨架 + 上下半屏分流 ✅ + MD 换行修复 ✅ |
| P9 收纳 | OLE 自落命中抽屉间移动 · 去重对话框 · 批量选择模式（头部「选择」开关 + 操作条：移入抽屉/删除/全选）· Ctrl+V 剪贴板收纳（C# Clipboard 读图/文本落盘）· 回收站与恢复 |
| P10 查找 | 名称过滤框（跨抽屉）· 排序切换（名称/时间/大小/类型/常用，置顶恒优先，选择落 config）· 置顶 ⭐ 交互 |
| P11 预览 | txt/md 便签窗口打开（NoteWindow 泛化）· 图片灯箱（面板内遮罩大图）· 视频悬停静音播放 · 文件进便签（V1 简化：右键「复制 Markdown 链接」；跨 WebView 直拖受 Chromium 无文件路径限制，后续评估） |
| P12 生命周期 | 过期灰显 + 计数 + 一键归档 · 设置面板（工作区路径 FolderBrowserDialog / 默认排序 / 过期天数 / 开机自启） |
| P13 皮肤与桌面贴纸 | 毛玻璃皮肤首发（主题机制保留扩展性，见 §9.4）· 桌面置顶小便签（纯文本渲染，见 §9.4） |
| P14 性能动效 | 事件委托、滚动保持、content-visibility、渲染节流、动画统一走合成器 |

### 9.3 皮肤与桌面贴纸的架构决策（P13，已确认）

**皮肤**（确认：首发仅毛玻璃，机制留扩展）：
- 主题 = 跨 C#/JS 的**描述符** `{ css类名, backdrop(none/acrylic/mica), 窗体透明 }`，
  C# 侧集中 ThemeApplier 对全部窗口（面板/便签窗/贴纸）统一应用，config.json 存主题名
- 毛玻璃平台分支：Win11 `DWMWA_SYSTEMBACKDROP_TYPE`（官方）；Win10
  `SetWindowCompositionAttribute`+`ACCENT_ENABLE_ACRYLICBLURBEHIND`（未公开 API，
  Rainmeter 同款；失败降级纯半透明）。WebView2 侧 `DefaultBackgroundColor=Transparent`
  + CSS 半透明背景
- Win10 亚克力拖动迟滞：滑入/滑出动画期间临时关模糊，动画结束再开（与 P14 联动）
- 跨窗口换肤同步：app.local 同源 localStorage 的 `storage` 事件零成本广播
  （需实测 WebView2 跨窗口行为，兜底 C# 广播消息）
- **CSS 变量化纪律从 P8 起执行**：新样式一律用 `--*` 变量，禁止硬编码颜色

**桌面贴纸**（确认：默认纯文本渲染）：
- TileWindow：无边框、置顶、不进任务栏的小窗口，每便签一个；纯文本渲染
  （页面不加载 marked/DOMPurify，渲染进程轻量）；双击打开 NoteWindow 大编辑窗
- 位置/尺寸存 `notes/index.json` 的 `tile` 字段（schema 已在 P8 预留）
- 内容实时同步依赖 **C# → 全窗口广播**（`NotifyNoteChanged`），与主题变更广播同一基础设施
- 窗口家族（面板/NoteWindow/TileWindow）共享 WebView2 底座（环境/映射/消息/主题）

### 9.4 提前动作清单（不在 P13 才做）

| 动作 | 归属 | 说明 |
|---|---|---|
| CSS 颜色全面变量化 + `data-theme` 骨架 | **P8 起持续** | 每期新增样式一律走变量 |
| `notes/index.json` 升级对象 schema（兼容旧字符串） | **P8** | 贴纸 tile 字段的落点 |
| C# → 全窗口广播机制（主题/内容变更共用） | P11 | NoteWindow 泛化时建 |
| WebView2 窗口公共底座（env/映射/消息/主题） | P11 | TileWindow 直接继承 |

### 9.5 风险登记

- 屏幕坐标 → CSS 坐标换算依赖 DPI 缩放与滚动位置，P9 实测校准（本机 200% 缩放）
- meta.json 与 watcher 推送的相互作用（meta 写入不得触发文件扫描风暴——meta 在 exe 目录，天然隔离）
- 批量选择模式的交互与"点开文件"的手势冲突——用显式「选择」模式开关隔离
- 大目录（数百文件）全量重渲染的性能——P13 事件委托 + content-visibility 兜底
