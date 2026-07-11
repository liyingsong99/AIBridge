# 验证分支

## 适用场景

用户要求编译、日志、测试、截图、Runtime/UI 验证、回归确认，或实施分支完成后需要验证时，进入验证分支

## 进入规则

1. 先确认 preferences 中验证分支已启用
2. 读取 `harness-readiness.md`（能力以 Root Rule / preferences 为准）
3. 按 preferences 默认验证级别选择命令

## 默认验证级别

以 `project-workflow-preferences.md` 为准，不在此复述三档定义

## 输出规则

- 只报告实际执行过的验证；不把静态检查或 `dotnet build` 冒充 Unity 编译
- Unity/Runtime/测试/日志不可用时说明原因与剩余风险
- Runtime 证据偏好启用时，优先尝试可用 target
- 先确认前置，再执行验证，最后判定通过/失败/阻塞；未执行的检查不能写成通过
