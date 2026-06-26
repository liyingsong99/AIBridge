# Contributing

English | [中文](#贡献指南)

## English

Thanks for contributing to AIBridge. This repository contains the `cn.lys.aibridge` Unity package itself, not a generated end-user Unity project.

### Scope

- Package-internal contributor rules live in [AGENTS.md](./AGENTS.md).
- End-user project templates live in `Templates~/ProjectRules/AGENTS.zh-CN.md` and `Templates~/ProjectRules/AGENTS.en-US.md`.
- Do not put package-internal design or maintenance rules into end-user project templates.
- Keep the product direction aligned with the README design principles: simple, easy to use, stable.

### Branches

- Use `dev` for normal development pull requests.
- Keep `main` for stable release-ready changes.
- Keep each change narrow. Do not mix feature work, generated binary refreshes, docs cleanup, and unrelated formatting in one pull request.
- Do not rewrite shared history just to hide generated CLI binary commits.

### Local Setup

- Install Unity 2019.4 or later. Changes that touch Unity APIs must remain compatible from Unity 2019.4 through Unity 6000.x.
- Install the .NET 8 SDK when working on `Tools~/AIBridgeCLI` or `Tools~/AIBridgeCodeIndex`.
- Open or embed the package in a Unity project when Unity compilation, Editor commands, assets, prefabs, scenes, or tests need validation.

### Change Rules

- C# must stay compatible with C# 9.0.
- Check Unity objects with explicit `!= null`; do not use null-conditional access on Unity objects.
- Guard Unity API version differences with Unity version defines, reflection, or a centralized compatibility wrapper.
- Avoid scattered magic paths, numbers, or business rules. Put shared rules in constants, settings, or helpers.
- Add concise Simplified Chinese comments for complex business logic changes.
- Update both English and Simplified Chinese user-visible text when changing Editor panels, HelpBox text, tooltips, README content, or templates.
- Preserve `.meta` files for Unity-imported package assets and scripts. Do not add new `.meta` files under paths ending with `~`, such as `Doc~`; when a change intentionally touches a Unity-ignored `~` path, clean the related `.meta` files in the same narrow change.

### Skill, Template, And Command Docs

- Keep the root `Skill~/SKILL.md` lightweight: CLI entry point, core rules, and reference index only.
- Put generated command documentation under the target Skill's `references/command-reference.md` by default.
- Implement `ICommandSkillDocProvider` when a command belongs in another reference file.
- Do not edit the Skill installer or RootRule routing just to add a new sibling Skill; `SkillInstaller` discovers `Skill~/SKILL.md` and `Skill~/*/SKILL.md`.
- Put optional Skill feature gates in `aibridge-skill.json` with `requiredFeature`.
- Keep AI-facing Skill docs compact. Put user-facing explanation in README, Editor UI, HelpBox, or tooltips instead.
- Keep `Templates~/Rules/AIBridge.RootRule.md` minimal: explicit project-root-relative `$CLI` binding, common commands, host-tool `exec` routing, quick-task/workflow routing, Skill root hint, package version, and compact capability summary.

### Validation

Run the checks that match the change. Do not report a check as passed if it was not executed.

- For every pull request: run `git diff --check`.
- For CLI or Code Index changes: build `Tools~/AIBridgeCLI/AIBridgeCLI.csproj` and any touched .NET project.
- For Unity-facing C# or asset changes: run AIBridge `compile unity`, then read `get_logs --logType Error`.
- For tests: run the focused Unity EditMode or .NET tests that cover the changed behavior.
- For Unity API compatibility changes: validate at least Unity 2019.4 and Unity 6000.x when those Editors are available. If a version is unavailable, state that explicitly in the pull request.
- For docs-only changes: at minimum check links, headings, Markdown formatting, and whether README badges still match `package.json`.
- For root package files and Unity-imported docs: verify required `.meta` files are present. For `~`-suffixed folders touched by the pull request, verify no new `.meta` files are introduced; if the pull request is a metadata cleanup, verify the targeted `.meta` files are removed.

### CLI Binaries And CI

The GitHub workflow builds platform CLI binaries under `Tools~/CLI`. Prefer to include necessary binary refreshes with the corresponding CLI source change. Standalone `chore: update CLI binaries for all platforms` commits are acceptable for release, cross-platform refresh, or explicit maintainer request. Avoid mixing generated binaries with unrelated source or docs changes.

### Pull Request Checklist

- The PR explains the problem, the chosen fix, and the validation performed.
- User-facing behavior changes include English and Simplified Chinese text updates.
- CLI examples, Skill references, and validation notes are updated when Unity-facing behavior changes.
- `package.json` remains the package version source of truth; README badges and version text stay synchronized when the package version changes.
- No unrelated files, local caches, `.aibridge/`, Unity `Library/`, or ignored generated output are included.

## 贡献指南

感谢参与 AIBridge。本仓库维护的是 `cn.lys.aibridge` Unity 包本身，不是安装后生成的用户 Unity 工程。

### 范围

- 包内部贡献规则写在 [AGENTS.md](./AGENTS.md)。
- 安装到用户 Unity 工程的模板位于 `Templates~/ProjectRules/AGENTS.zh-CN.md` 和 `Templates~/ProjectRules/AGENTS.en-US.md`。
- 不要把包内部设计、维护和发布规则写进用户项目模板。
- 产品方向保持 README 顶部的设计准则：简单、易用、稳定。

### 分支

- 常规开发 PR 合入 `dev`。
- `main` 保持稳定和可发布状态。
- 改动保持窄范围，不要把功能、生成二进制、文档清理和无关格式化混在一个 PR。
- 不要为了隐藏 CLI 生成二进制提交而重写共享历史。

### 本地环境

- 安装 Unity 2019.4 或更高版本。涉及 Unity API 的改动必须兼容 Unity 2019.4 到 Unity 6000.x。
- 修改 `Tools~/AIBridgeCLI` 或 `Tools~/AIBridgeCodeIndex` 时安装 .NET 8 SDK。
- 涉及 Unity 编译、Editor 命令、资源、Prefab、Scene 或测试时，把包放入 Unity 项目中验证。

### 修改规则

- C# 代码必须兼容 C# 9.0。
- Unity 对象判空必须显式使用 `!= null`，不要对 Unity 对象使用空条件访问。
- Unity API 版本差异必须用 Unity 版本宏、反射或集中兼容封装处理。
- 不要散落魔法路径、数字或业务规则；共享规则放入常量、设置或 helper。
- 修改复杂业务逻辑时添加简洁的简体中文注释。
- 修改 Editor 面板、HelpBox、Tooltip、README 或模板中的用户可见文本时，同步更新英文和简体中文。
- Unity 会导入的包内资源和脚本要保留 `.meta`。不要在 `Doc~` 这类以 `~` 结尾的路径下新增 `.meta`；如果本次改动明确触碰 Unity 忽略的 `~` 路径，应按窄范围一并清理对应 `.meta`。

### Skill、模板和命令文档

- 主 `Skill~/SKILL.md` 保持轻量，只放 CLI 入口、核心规则和 reference 索引。
- 命令文档默认生成到目标 Skill 的 `references/command-reference.md`。
- 命令需要写入其它 reference 文件时，实现 `ICommandSkillDocProvider`。
- 新增 sibling Skill 不要为了索引去改 Skill 安装器或 RootRule 路由；`SkillInstaller` 会发现 `Skill~/SKILL.md` 和 `Skill~/*/SKILL.md`。
- 受功能开关控制的可选 Skill，在目录内放 `aibridge-skill.json` 并声明 `requiredFeature`。
- 面向 AI 的 Skill 文档保持精简；用户说明放入 README、Editor UI、HelpBox 或 Tooltip。
- `Templates~/Rules/AIBridge.RootRule.md` 只保留明确的项目根目录相对 `$CLI` 绑定、常用命令、host 工具 `exec` 路由、快速任务/工作流任务路由、Skill 根目录提示、包版本和 compact 能力摘要。

### 验证

按改动类型执行对应检查。没有实际执行的检查不能写成已通过。

- 每个 PR 至少执行 `git diff --check`。
- 修改 CLI 或 Code Index 时，编译 `Tools~/AIBridgeCLI/AIBridgeCLI.csproj` 和被改动的 .NET 项目。
- 修改 Unity 侧 C# 或资源时，执行 AIBridge `compile unity`，再读取 `get_logs --logType Error`。
- 涉及测试时，运行覆盖该行为的 Unity EditMode 或 .NET focused tests。
- 涉及 Unity API 兼容性时，有对应 Editor 的情况下至少验证 Unity 2019.4 和 Unity 6000.x；无法验证的版本在 PR 中明确说明。
- 仅修改文档时，至少检查链接、标题、Markdown 格式，以及 README badge 是否仍与 `package.json` 一致。
- 修改包根文件或 Unity 会导入的文档时，检查需要的 `.meta` 是否存在；对本 PR 触碰的 `~` 结尾目录，检查是否没有新增 `.meta`；如果是元数据清理 PR，确认目标 `.meta` 已删除。

### CLI 二进制和 CI

GitHub workflow 会把多平台 CLI 二进制生成到 `Tools~/CLI`。必要的二进制刷新优先随对应 CLI 源码改动一起提交。独立的 `chore: update CLI binaries for all platforms` 只适合发布、跨平台刷新或维护者明确要求。不要把生成二进制和无关源码、文档改动混在一起。

### PR 清单

- PR 说明问题、方案和已执行验证。
- 用户可见行为改动同步更新英文和简体中文。
- 修改 Unity 侧行为时，同步更新 CLI 示例、Skill reference 和验证说明。
- `package.json` 是包版本来源；版本变化时同步 README badge 和版本文本。
- 不提交无关文件、本地缓存、`.aibridge/`、Unity `Library/` 或被忽略的生成输出。
