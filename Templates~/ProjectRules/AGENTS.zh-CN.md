# AGENTS.md

## 基本原则
1. 尽量使用简体中文回复，禁止废话，言简意赅
2. 修改复杂业务逻辑时，必须用简体中文添加必要注释
3. 尊重用户已有改动，不擅自回滚无关文件

## 项目验证
- `$CLI` 指向项目本地 AIBridge CLI：`{{AIBRIDGE_CLI_PATH}}`。PowerShell 中可先设 `$CLI = "{{AIBRIDGE_CLI_PATH}}"`，再用 `& $CLI ...` 调用
- Unity 编译只能使用 `$CLI compile unity`
- `compile dotnet` 只能作为额外检查，不能作为 Unity 编译的替代或 fallback
