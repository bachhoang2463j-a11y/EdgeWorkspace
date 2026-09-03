# Agents 工作约定

## 前提：用户对该技术栈无开发经验：关键决策须解释理由与备选项，不跳步；涉及专业术语需简要说明。用户描述的可能只是现象而非方案——判断其建议是否真能解决问题，存疑即提出，而不是照做。

## 一、项目三件套（README / SPEC / LOG）
每个项目根目录维护三个核心文件，职责分明，不可混用：

- **README.md -- 现状**：项目现在是什么、如何运行、目录结构、主要能力。
- **SPEC.md -- 目标**：项目应该成为什么，设计原则、边界、验收标准。除非当前技术栈走不通需要更换，否则不轻易改动。
- **LOG.md -- 历史**：项目如何一步步变成现在这样，每轮施工记录与决策原因。

## 二、施工日志规范（可溯源）
- 每轮施工结束前必须在 LOG.md 追加一条记录：日期、变更行为、涉及文件、决策原因。
- 同步维护 **LOG-INDEX.md** 作为目录，仅四列精简信息：`日期 | 行为 | LOG.md 行号 | HASH`。
- 回溯历史时，先读 LOG-INDEX.md，按 HASH 精准定位到 LOG.md 对应段落，**禁止一次性全量读取 LOG.md**（避免幻觉与上下文浪费）。

## 三、README 更新时机
- 仅在我确认"本轮工作有效无误"后，才更新 README.md。未确认前不要擅自改写项目现状描述。

## 四、Git 提交流程与 Hash 回填规范（严防自指死循环）

> **⚠️ 核心死循环陷阱**：Git Commit Hash 是对整棵提交树（Tree）内容和元数据的 SHA-1 计算。修改 `LOG.md` 或 `LOG-INDEX.md` 会改变文件内容，从而必然产生新的 Commit Hash。**绝对禁止使用 `git commit --amend` 试图强行将包含该 Hash 的 Commit 与文件内记录的 Hash 保持一致**，否则会导致 `修改 Hash → amend 导致新 Hash → 再次修改 Hash → 再次 amend` 的无限死循环。

### 标准单向回填流（两步提交法）：
1. **第一步（业务/代码提交）**：
   - 业务代码修改并测试通过后，先提交业务 Commit：
     ```bash
     git add <修改的代码/业务文件>
     git commit -m "feat/fix: <功能简述>"
     ```
   - 此时得到本次业务变更的稳定 Git Hash（例如 `f92b874`）。
2. **第二步（日志回填与文档提交）**：
   - 将上述生成的 Git Hash（`f92b874`）回填写入 `LOG.md` 与 `LOG-INDEX.md` 的对应新行中；
   - 提交日志与文档改动（**单向提交，禁止 amend 回退**）：
     ```bash
     git add LOG.md LOG-INDEX.md [README.md]
     git commit -m "docs(log): record <f92b874> in LOG and LOG-INDEX"
     ```
3. **第三步（推送到远程）**：
   - 直接推送到远程仓库：`git push origin <branch>`。
