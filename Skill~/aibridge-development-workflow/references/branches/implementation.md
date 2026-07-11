# 实施分支

## 适用场景

用户要求创建、修改、修复、重构、迁移、生成代码或资源，且目标和验收标准足够明确时，进入实施分支

## 进入规则

1. 先确认 preferences 中实施分支已启用
2. 需求未锁定时先进入需求讨论分支（方案写入见 `requirements.md`），不直接实施
3. 修改前读取 `risk-gates.md` 和 `coding-rules.md`
4. 复杂一次性 Editor 侧 C# 任务时读取 `editor-generation.md`
5. 仅当 Root Rule / preferences 声明 Code Index 已启用且任务需要时，才加载 `aibridge-code-index`；其它修改 Skill 按需加载

## 执行规则

- 先定位真实代码路径，再修改
- 改动范围贴合用户目标，不做无关重构
- 每步具备前置条件、动作、完成标准与回退；前置不成立则回需求讨论或调试分支
- Unity 对象/Prefab/资源/Console 优先结合 AIBridge/Unity API 验证
- 完成后按 preferences 默认验证级别执行 `checklist.md`

## 默认验证

验证级别与命令以 `project-workflow-preferences.md` 为准。无法执行的验证必须说明原因，不能标记为通过
