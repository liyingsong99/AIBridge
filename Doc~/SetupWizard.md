# AIBridge 设置向导

## 这个功能是做什么的

`AIBridge/Setup` 是首次使用 AIBridge 时的单页设置向导。它在同一个界面里完成：

1. 选择编辑器界面语言；
2. 选择正在使用的 AI 工具（可多选）；
3. 预览将写入的规则文件与 Skill 目录；
4. 一键安装并回显实际写入结果。

它面向第一次在 Unity 项目里安装 AIBridge 的用户，也用于后续更换 AI 工具或修复集成。

## 首次启动行为

- 新项目第一次导入 AIBridge 后，`SkillInstaller` 会在编译/导入结束后自动打开向导。
- 同一 Editor 会话只自动打开一次，域重载不会反复打扰。
- `-batchmode`、Play Mode、资源导入未完成时不打开。
- 关闭窗口或点击“稍后配置”视为完成引导：先记录完成标记，再按默认选择（通常是 Codex）安装一次，保证开箱可用。
- 已有项目升级时，如果 `editorLanguageInitialized` 已经为 true，会直接视为已完成引导，不重复弹出。

## 工具与写入路径

| 工具 | 项目规则文件 | Skill 目录 |
|---|---|---|
| Codex | `AGENTS.md` | `.agents/skills/aibridge` |
| DeepSeek Harness | `AGENTS.md` | `.agents/skills/aibridge` |
| ZCode | `AGENTS.md` | `.agents/skills/aibridge` |
| Claude Code | `CLAUDE.md` | `.claude/skills/aibridge` |
| Cursor | `.cursor/rules/aibridge.mdc` | `.cursor/skills/aibridge` |
| WorkBuddy | `CODEBUDDY.md` | `.codebuddy/skills/aibridge` |
| Cline | `.clinerules/aibridge.md` | `.clinerules/skills/aibridge` |

说明：

- Codex、DeepSeek Harness、ZCode 都读取项目根 `AGENTS.md`，并且都支持项目级 `.agents/skills`，因此共用同一个注入块与同一个 Skill 目录；多选不会重复写入。
- Codex 的项目级官方 Skill 目录是 `.agents/skills`。旧版本 AIBridge 写入的 `.codex/skills` 只是用户级 `~/.codex/skills` 的遗留对应路径，升级后不再更新；旧目录不会自动删除。
- Claude Code 存在 `CLAUDE.md` 时只读该文件，所以 Claude 必须继续写 `CLAUDE.md`。
- WorkBuddy 为腾讯 CodeBuddy 团队产品；办公模式的 Skill 可能在云端，本地 `.codebuddy/skills` 主要服务项目代码开发模式。

## 界面行为

- “常用工具”默认展示 Codex、Claude Code、Cursor、DeepSeek Harness、ZCode、WorkBuddy；Cline 在“更多工具”中。
- 预选规则：
  1. 有工具专属目录（如 `.codex`、`.claude`、`.dsh`、`.zcode`、`.codebuddy`、`.cursor`、`.clinerules`）的工具优先；
  2. 没有专属目录时，选择独享规则文件的工具（Claude、WorkBuddy、Cursor、Cline）；
  3. 只有共享 `AGENTS.md` 时，只预选组内第一个工具（Codex）；
  4. 全无信号时回落 Codex。
- 选中 AGENTS.md 系工具且项目根没有 `AGENTS.md` 时，可勾选“安装 Unity 项目 AGENTS.md 模板”。
- 已存在 `AGENTS.md` 时只追加 AIBridge 区块，不覆盖正文。
- “高级”中可以设置全局自定义 Skills 目录。自定义目录会应用到所有已选工具，且可能无法被 AI 工具自动发现。
- 安装结果按工具显示：规则文件新建/更新/已最新/跳过/失败，以及 Skill 目录动作和实际路径。

## 与 Workflows 面板的关系

- `AIBridge/Setup` 负责首次安装与工具选择。
- `AIBridge/Workflows > Skills` 负责后续管理：修改选择、重新安装、自定义目录、推荐 Skill 库。
- `AIBridge/Workflows > Skills` 顶部提供“运行设置向导”按钮，可随时重新打开。
- 菜单项也可通过 AIBridge CLI 的 `menu_item` 命令触发，便于自动化验证。

## 明确不做什么

- 不检测工具是否安装在用户机器上，只使用项目内信号（规则文件、工具目录）。
- 不自动删除旧版本生成的 `.codex/skills`；如需清理请手动确认。
- 不修改 CLI 协议、Runtime、Workflow recipe 或 Skill 正文。
- 不在 batch 模式下显示任何 UI。
