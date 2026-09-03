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
{ "name": "策划案.docx", "isFolder": false, "ext": "docx",
  "kind": "doc", "size": 25090, "mtime": "2026-09-03 10:27" }
```

`kind` 判定口径见 §6。

### 3.2 便签

- **无限量**：`notes/noteN.txt`，一个文件一张便签，内容为 **Markdown 源码**（UTF-8）
- 标题存 `notes/index.json`（`{ "note1": "标题" }`，损坏时静默回退空标题）
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
| `openPath` / `revealItem` | `{name}` | 系统打开 / 资源管理器定位 |
| `openFolder` | — | 资源管理器打开工作区 |
| `contextMenu` | `{name}` | Shell 原生右键菜单（P4） |
| `startDragOut` | `{name}` | C# 发起 OLE 拖出（DoDragDrop） |
| `noteCreate` / `noteDelete` | `{id}` | 新建 / 删除便签（删时关窗防复活） |
| `noteRename` | `{id, title}` | 改名（写 index.json + Touch + 重推） |
| `noteOpen` | `{id}` | 打开/聚焦该便签的独立窗口 |
| `openLink` | `{url}` | 渲染出的链接交系统浏览器（仅放行 http/https） |
| `setPinned` | `{pinned}` | 钉住态 |

C# → JS（`PostWebMessageAsJson`）：

| type | payload | 时机 |
|---|---|---|
| `files` | `{items, total}` | 启动 / watcher / 拖入后 |
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
  应用窗口盖住右缘时**有意不唤出**）；前台全屏应用不唤出 → 唤出 + 切白板 Tab
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
`DOMPurify.sanitize(marked.parse(text))`。消毒是必须的：页面持有 openPath 等 bridge
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
