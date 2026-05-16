# AGENTS.md

## 基本原则
1. 尽量使用简体中文回复，禁止废话，言简意赅
2. 修改复杂业务逻辑时，必须用简体中文添加必要注释
3. 尊重用户已有改动，不擅自回滚无关文件

## AIBridge 入口
- `$CLI` 表示：`./AIBridgeCache/CLI/AIBridgeCLI.exe`
- Unity 编译：`$CLI compile unity`
- Error 日志：`$CLI get_logs --logType Error`
- 写日志示例：`$CLI editor log --message "Hello" --logType Warning`

## 工作流路由
- 快速任务：纯问答、代码解释、查找、显示、无代码或资源修改，直接回答或执行，不加载标准开发工作流
- 开发任务：创建、修改、修复、重构 C# 代码、Unity 资源、Prefab、Editor 工具、包结构、测试、AGENTS.md 或 Skills，必须优先加载 `aibridge-development-workflow`
- 进入标准开发工作流后，由 `aibridge-development-workflow` 在 `【Skills 匹配模式】` 决定是否继续加载其它 Skill

## Skill 索引
| Skill | 匹配关键词 |
|---|---|
| `aibridge-development-workflow` | 开发、修改、修复、重构、验证、测试、AGENTS、Skill、Editor 工具、包结构、Unity 资源 |
| `aibridge` | CLI、编译、日志、Console、asset、scene、gameobject、inspector、selection、transform、screenshot、test、focus |
| `aibridge-prefab-patch` | 复杂 Prefab、prefab patch、dryRun、批量 SerializedProperty、ensure_child、ensure_component、数组、引用写入 |
| `aibridge-batch-script` | batch、multi、批处理、脚本自动化、stdin、delay、call、menu、长脚本 |

## 项目验证
- Unity 编译只能使用 `$CLI compile unity`
- `compile dotnet` 只能作为额外检查，不能作为 Unity 编译的替代或 fallback
