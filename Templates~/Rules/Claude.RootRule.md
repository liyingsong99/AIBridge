---
templateId: unity-integration
assistant: claude
version: 5
target: root-rule
---
## AIBridge Bootstrap

**CLI Alias**: `$CLI = {{CLI_PATH}}`

**常用命令**:
```bash
$CLI compile unity
$CLI get_logs --logType Error
$CLI editor log --message "Hello" --logType Warning
```

**路由原则**:
- 快速任务：纯问答、代码解释、查找、显示、无代码或资源修改，直接回答或执行。
- 开发任务：创建、修改、修复、重构 C# 代码、Unity 资源、Prefab、Editor 工具、包结构、测试、AGENTS.md 或 Skills，必须优先加载 `aibridge-development-workflow`。
- 进入标准开发工作流后，由 `aibridge-development-workflow` 在 `【Skills 匹配模式】` 决定是否继续加载其它 Skill。

**Skill 索引**:
{{SKILL_INDEX}}
