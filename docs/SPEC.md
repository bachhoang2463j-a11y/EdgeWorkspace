# EdgeWorkspace 技术规格书（SPEC）

> 轻量 Windows 桌面"临时工作区"应用：屏幕右缘唤出的文件收纳面板 + 白板便签墙。
> 本文档是开发与维护的唯一依据；改动先改 SPEC 再动代码。

## 1. 技术栈与运行环境

| 项 | 值 |
|---|---|
| 框架 | .NET 8 WinForms（本机 SDK 8.0.412） |
| UI 层 | WebView2 控件加载本地 `wwwroot/`（HTML/CSS/JS），运行时 150.x 已安装 |
| 工作区 | `F:\Workspace_Temp`（可在 `config.json` 改） |
| 目标系统 | Windows 10 19045+ / Windows 11 |

**架构原则**：C# 只做桌面能力（文件系统、窗口、热键、拖放、持久化），UI 全部在 HTML/JS。
两者通过 WebView2 消息桥通信，桥协议见 §4。UI 文件可随时改而无需重编译。

## 2. 目录规划

```
EdgeWorkspace/
├── docs/SPEC.md, README.md
├── src/EdgeWorkspace/
│   ├── EdgeWorkspace.csproj
│   ├── Program.cs            # 入口：单实例、托盘（后期）
│   ├── MainForm.cs           # 窗口、置顶、贴边、热键、拖放、消息桥
│   ├── WorkspaceWatcher.cs   # FileSystemWatcher 封装，防抖后推送文件列表
│   ├── FileScanner.cs        # 目录扫描 → 条目 JSON（与 watcher 复用）
│   └── wwwroot/
│       ├── index.html        # 布局骨架（改造自视觉 Demo）
│       ├── app.js            # 渲染、Tab 过滤、便签编辑、桥调用
│       └── style.css
├── notes/note1.txt ...       # 便签内容（随仓库走，内容即持久化）
├── config.json               # 工作区路径、钉住态等用户设置
└── README.md
```

## 3. 数据模型

### 3.1 文件条目（C# → JS，JSON 数组）

```json
{
  "name": "策划案初稿_0902.docx",
  "isFolder": false,
  "ext": "docx",
  "kind": "doc",          // folder | doc | image | video | audio | archive | app | code | other
  "size": 25090,          // 字节；文件夹为 0
  "mtime": "2026-09-03T04:27:11"
}
```

`kind` 判定口径（与 Rainmeter 版 SubMap 一致，见 §6）。

### 3.2 便签
- 固定槽位起步：`notes/note1.txt ~ note6.txt`；空文件 = 空便签
- 每张便签独立文件（UTF-8），前端 textarea 防抖 500ms 自动保存（JS→C# `saveNote`）
- 删除 = 清空文件内容（保留槽位）；新增 = 点空槽开始编辑
- 后续可扩展：槽位元数据（标题、颜色、顺序）存 `notes/index.json`

### 3.3 config.json
```json
{ "workspacePath": "F:\\Workspace_Temp", "pinned": false, "autoStart": false }
```
应用启动读取，退出/修改时写回。

## 4. C# ↔ JS 桥协议

### 4.1 JS → C#（`window.chrome.webview.postMessage`）

| type | payload | 语义 |
|---|---|---|
| `ready` | — | 前端加载完成，请求首份文件列表 |
| `openPath` | `{path}` | 用系统默认方式打开文件/文件夹 |
| `openFolder` | — | 资源管理器打开工作区 |
| `revealItem` | `{name}` | 资源管理器中定位该文件 |
| `saveNote` | `{slot, content}` | 写 `notes/noteN.txt`（UTF-8） |
| `clearNote` | `{slot}` | 清空对应 txt |
| `setPinned` | `{pinned}` | 通知 C# 改钉住态（影响收起逻辑） |
| `openNoteFile` | `{slot}` | （备用）外部编辑器打开便签文件 |

### 4.2 C# → JS（`PostWebMessageAsJson`）

| type | payload | 时机 |
|---|---|---|
| `files` | `{items: [条目...], total}` | watcher 触发 / 启动 / drop 后（防抖 300ms） |
| `noteSaved` | `{slot, ok}` | saveNote 写盘后确认 |
| `config` | `{workspacePath, pinned}` | 启动时 |

约定：所有消息 `type` 字段必填；前端对未知 type 静默忽略（向后兼容）。

## 5. 窗口与交互规格

### 5.1 窗口
- 无边框（`FormBorderStyle=None`），尺寸 = 宽 `WORKAREA/3`（≥420 逻辑像素）× 高 `WORKAREA 高度`
- TopMost = true；不显示在任务栏
- 位置：右侧贴边（`X = WORKAREA右缘 - 窗宽`），隐藏态 `X += 窗宽`（完全滑出屏幕）

### 5.2 唤出
- 100ms `System.Windows.Forms.Timer` 轮询 `GetCursorPos`：
  - 光标 X ≥ `WORKAREA 右缘 - 8px` 且 Y 在工作区内
  - 且 `WindowFromPoint` 命中的是桌面（`GetShellWindow`）或本应用窗口 → 判定可唤出
  - 全屏应用（前台窗口覆盖整个屏幕）时不触发
- 满足 → 动画滑入：Timer 60fps 插值 X，时长 180ms，缓出曲线
- 触发后本轮禁用，直到收起完成

### 5.3 收起（未钉住时，满足其一）
- `MouseLeave` 事件后 600ms 内未回到窗口（拖放悬停除外：`DragOver` 期间挂起收起）
- 窗口失焦（`Deactivate`）后 300ms（拖放/菜单打开期间挂起）
- 收起 = 反向滑出动画，完成后 `Visible=false`（或停在屏外）

### 5.4 热键与钉住
- `RegisterHotKey(CTRL+SHIFT+Z)`：开 ↔ 关；打开时若当前是白板 Tab 保持白板
- 钉住：头部 📌 按钮，钉住时忽略 §5.3 全部收起条件；窗口失焦不收
- 拖动文件到窗口 = 自动唤出并切到【全部】Tab；贴边唤出 = 切【白板】Tab（用户习惯分流，与 Rainmeter 版一致）

### 5.5 拖放
- 窗口级 `AllowDrop`；`DragEnter` 校验 `FileDrop` 格式并置 effect=Move
- `DragDrop`：逐个 `File.Move` 到工作区（重名追加 " (n)"）；完成后推 `files` 刷新
- 拖放悬停期间窗口保持展开（挂起收起计时器）

## 6. 分类口径（kind 映射）

| kind | 扩展名 |
|---|---|
| folder | 目录（IsFolder） |
| doc | txt log md markdown ini cfg conf yml yaml nfo tres doc docx rtf odt wps xls xlsx csv ods ppt pptx odp pdf js ts jsx tsx py lua json xml html htm css scss c cpp h cs java go rs php rb sh ps1 vbs sql toml |
| image | jpg jpeg png gif bmp webp tif tiff ico svg psd heic |
| video | mp4 mkv avi mov wmv flv webm m4v rmvb |
| audio | mp3 wav flac ogg aac m4a wma |
| archive | zip rar 7z tar gz bz2 xz iso |
| app | exe msi bat cmd lnk dll appx |
| code | （并入 doc，前端 Tab"文档"含 code） |
| other | 其余 |

前端 Tab 过滤：全部=全部；文件夹=kind==folder；文档=doc；图片=image；视频=video。
图片/视频在网格中直接渲染内容缩略图（`file://` 或虚拟主机映射），失败回落彩色图标。

## 7. 构建与运行

```
cd src/EdgeWorkspace
dotnet build -c Release
dotnet run -c Release
```
WebView2 运行时缺失时：安装 "Evergreen WebView2 Runtime"（机器已有，pv=150.x）。

## 8. 已知取舍 / 第二期路线
- 第一期不做：面板内自定义分类抽屉、文件在抽屉间拖动（OLE 源 + HTML5 拖放组合，架构已预留）、便签富文本、多工作区
- Rainmeter 皮肤 v2.3 保留为新应用成熟前的过渡；`doc` 类含代码扩展名（沿用 Rainmeter 口径）
- 中文输入：textarea 原生，无风险；WebView2 的 DevTools 可在调试构建打开（环境变量 `EDGEWORKSPACE_DEV=1`）