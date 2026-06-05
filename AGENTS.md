# AGENTS.md

## 基本原则
1. 尽量使用简体中文回复，禁止废话，言简意赅
2. 修改复杂业务逻辑时，必须用简体中文添加必要注释
3. 尊重用户已有改动，不擅自回滚无关文件

## 本文件职责
本文件只用于开发 `cn.lys.aibridge` 包自身。安装到 Unity 项目根目录的示例模板位于：

`Templates~/ProjectRules/AGENTS.zh-CN.md` 和 `Templates~/ProjectRules/AGENTS.en-US.md`

不要把 AIBridge 包内部设计规则写入项目模板，避免污染使用者项目。

## 开发任务工作流
开发、修改、修复、重构 C# 代码、Unity 资源、Prefab、Editor 工具、包结构、测试、AGENTS 模板或 Skills 时，必须优先使用：

- `aibridge-development-workflow`
- 涉及 CLI / Unity 编译 / 日志 / 资源 / Inspector 时，再使用 `aibridge`
- 涉及复杂 Prefab 资源修改时，再使用 `aibridge-prefab-patch`
- 涉及直接修改 Unity YAML 序列化文件或 AIBridge 不支持的 Prefab / Scene / `.asset` 结构修改时，再使用 `unity-yaml-editing`
- 涉及 batch / multi 脚本自动化时，再使用 `aibridge-batch-script`

## 项目验证
- 编译.\Tools~\AIBridgeCLI\AIBridgeCLI.csproj 项目确认无报错
- 检查所有Runtime和Editor下的代码文件都有生成相应的meta文件
- 检查以 `~` 结尾的目录或文件是否保留了 `.meta`；Unity 默认忽略这类路径，原则上不要提交它们的 `.meta`，清理时连同对应目录下的 `.meta` 一并删除
- 检查编辑器面板新增或修改的用户可见文本已适配英文和简体中文
- 涉及 Unity API 或编译兼容性时，必须确保 `package.json` 声明的最低 Unity 2019.4 到 Unity 6000.x 兼容；能访问对应编辑器时至少验证 Unity 2019.4 和 Unity 6000.x 编译，无法验证的版本必须明确说明

## 代码硬约束
1. C# 代码必须兼容 C# 9.0，禁止 C# 10.0+ 语法
2. Unity 对象判空必须显式使用 `!= null`，禁止对 Unity 对象使用 `?.`
3. 业务配置、路径、魔法数字不得散落硬编码；必要时定义常量或读取配置
4. 避免重复代码；同一业务规则重复出现时应提取公共方法或工具类
5. 新增或修改 Unity Editor/Runtime API 调用时，禁止直接依赖高版本 API 破坏低版本编译；版本差异必须用 Unity 版本宏、反射或集中封装处理

## SkillDoc 生成规则
1. 主 `Skill~/SKILL.md` 保持轻量，只放 CLI 调用入口、核心规则和 reference 索引
2. 命令文档默认生成到目标 Skill 的 `references/command-reference.md`
3. 命令需要写入其它 reference 文件时，实现 `ICommandSkillDocProvider`
4. `CommandSkillDoc.TargetSkillName` 默认是 `aibridge`
5. `CommandSkillDoc.TargetReferenceFileName` 默认是 `command-reference.md`
6. 大模块按职责拆分 reference，例如：
   - batch / multi：`aibridge-batch-script/references/batch-script-reference.md`
   - prefab：`aibridge-prefab-patch/references/prefab-reference.md`
   - inspector：`aibridge/references/inspector-property-reference.md`
   - unity yaml：`unity-yaml-editing/references/unity-yaml-reference.md`
7. 未实现 `ICommandSkillDocProvider` 的旧命令继续使用 `ICommand.SkillDescription`，并写入默认 reference
8. SkillInstaller 自动安装 `Skill~/SKILL.md` 和 `Skill~/*/SKILL.md`；新增 Skill 不要修改 RootRule 索引或安装器路由
9. 需要受功能开关控制的可选 Skill，在 Skill 目录放 `aibridge-skill.json` 并声明 `requiredFeature`，不要把功能说明硬塞进主 Skill
10. 面向 AI 的 Skill / SkillDescription / command reference 必须精简凝练，只保留调用方式、关键约束和必要决策规则，避免冗余解释性说明
11. 面向用户的编辑器面板、README、HelpBox、Tooltip 可以解释功能影响、默认行为和使用说明；不要把这类用户说明混入面向 AI 的提示词

## 模板维护规则
1. `Templates~/ProjectRules/AGENTS.zh-CN.md` 和 `Templates~/ProjectRules/AGENTS.en-US.md` 是安装到 Unity 项目的示例文件
2. `Templates~/Rules/AIBridge.RootRule.md` 是注入到已有规则文件的通用最小引导块
3. RootRule 只放 CLI 路径、常用命令、“快速任务不进工作流 / 工作流任务先加载工作流”的路由规则、Skill 根目录提示、项目版本和 compact 能力摘要
4. 完整行为规范必须放在 `aibridge-development-workflow`
5. CLI 详细命令说明必须放在对应 Skill 的 `references/*.md`
