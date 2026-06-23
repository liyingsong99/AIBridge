# AGENTS.md

## 基本原则
1. 尽量使用简体中文回复，禁止废话，言简意赅
2. 修改复杂业务逻辑时，必须用简体中文添加必要注释
3. 尊重用户已有改动，不擅自回滚无关文件

## 知识库规则
- 开始任何 AIBridge 功能调整、命令/菜单/Workflow/Skill/Runtime/模板/文档变更前，先查看 `Packages/cn.lys.aibridge/Doc~/KnowledgeBaseIndex.md`，必要时联读 `Packages/cn.lys.aibridge/Doc~/README.md` 和对应专题文档。
- 凡新增、修改或删除用户可见能力、CLI 命令、Editor 菜单、Runtime 能力、workflow recipe、Skill、模板、生成产物路径或公开文档，必须在同一次改动中自动更新 `KnowledgeBaseIndex.md` 和相关 README / `Doc~` / `Skill~` / `Templates~` 文档，不需要用户额外提醒。
- 若实现与知识库不一致，先以当前代码、CLI 输出或 `workflow list` 核实事实，再修正实现或知识库；不得把漂移留给后续。
- 方案型或跨模块调整先写 `.aibridge/plan/<slug>.md` 工作底稿，再同步正式知识库文档。

## 项目验证
- Unity 编译只能使用 `$CLI compile unity`
- `compile dotnet` 只能作为额外检查，不能作为 Unity 编译的替代或 fallback
