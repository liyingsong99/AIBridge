# Workflows 面板功能规格说明

## 状态

- 状态：当前基线
- 面板入口：`AIBridge/Workflows`
- 文档目的：定义 `Workflows` 面板的产品定位，防止后续再次发生功能范围跑偏

## 背景

`Workflows` 面板最初的目标，是承接那些已经不适合继续放在 `AIBridge/Settings` 里的工作流相关用户功能。

它的初始重点不是 workflow 引擎检视，而是：

- `Skills`
- `Recommended Skill Library / 推荐 Skill 库`
- 后续可能增加的工作流用户选项，例如分支开关、提示词定制等

这个面板主要面向使用 Codex、Claude Code、Cursor 等外部 AI 工具进行开发的 AIBridge 用户。

## 目标用户

主要用户：

- 将 AIBridge 作为项目内 AI 开发 harness 使用的 Unity 开发者
- 需要将 AIBridge Skills 安装到所选 AI 工具中的用户
- 需要配置 AIBridge 工作流行为的用户

次要用户：

- 包维护者也可能使用这个面板，但默认信息架构不能以维护者或调试需求为中心

## 产品定位

`AIBridge/Workflows` 是一个面向用户的工作流配置面板。

它不是以下内容的默认承载位置：

- recipe 编写
- workflow run 检查
- artifact 浏览
- gate 调试
- cleanup 操作
- 原始 CLI 输出查看

这些能力即使存在，也应放到以下位置之一：

- 单独的 `Workflow Admin` 或 `Workflow Debug` 面板
- CLI

## 核心目标

这个面板必须满足以下目标：

1. 将工作流相关的用户功能从 `AIBridge/Settings` 中迁出，避免继续堆叠到设置面板。
2. 为 Codex、Claude Code、Cursor、DeepSeek Harness、ZCode、WorkBuddy 等 AI 工具提供统一、清晰的工作流配置入口。
3. 默认只暴露正常用户在日常使用中真正需要的工作流控制项。
4. 让用户无需理解 recipe、run、artifact、gate、step 等底层概念，也能理解这个面板。
5. 让 `AIBridge/Settings` 保持聚焦于引擎、runtime、code index 和系统级设置。

## 非目标

这个面板不应变成所有 workflow CLI 命令的图形化壳。

这个面板不应要求普通用户理解以下内部概念：

- workflow recipe
- run manifest
- artifact 目录
- gate 状态细节
- 外部结果 schema 名称
- CLI 内部使用的 export target 原名

这个面板不能为了方便维护者调试而牺牲普通用户的理解成本。

## 必要的信息架构

默认的 `Workflows` 面板应围绕用户任务组织，而不是围绕 workflow 引擎内部结构组织。

### 必要页签

#### 1. Skills

用途：

- 为所选 AI 工具安装或刷新 AIBridge Skills
- 用用户能理解的方式显示当前集成目标

典型操作：

- 安装选中的集成
- 在必要时跳转相关设置
- 通过“运行设置向导”重新打开 `AIBridge/Setup`，完成首次语言与工具选择

首次安装由 `AIBridge/Setup` 设置向导负责；本页签负责后续管理，两处共享同一套目标注册表与安装器。

#### 2. Recommended Library / 推荐 Skill 库

用途：

- 浏览或安装适合 AIBridge 工作流的推荐第三方 Skill

典型操作：

- 打开安装目录
- 刷新推荐库来源
- 将推荐 Skill 安装到所选工具目标

## 可选页签

### Export 或 Handoff

这是可选页签。

如果保留，应该围绕 assistant 交接来表达，例如：

- 为 Codex 准备
- 为 Claude 准备
- 为 Cursor 准备

除非某个底层术语本身就是用户概念，否则不应直接暴露实现导向的命名。

## 不应属于默认用户面板的页签

以下内容不应作为普通用户默认看到的 `Workflows` 页签：

- `Overview`
- `Recipes`
- `Runs`
- `Artifacts`
- `Cleanup`
- 原始 CLI 输出查看区

原因：

- 这些都是维护、检视或调试概念
- 它们不能直接帮助普通用户完成日常 AIBridge 辅助开发任务
- 它们会让面板偏向引擎管理，而不是工作流配置

## 职责边界

### `AIBridge/Workflows`

适合放在这里的内容：

- 面向用户的工作流配置
- 面向用户的工作流偏好设置
- 面向 assistant 的交接入口
- Skill 安装和推荐库管理

### `AIBridge/Settings`

适合放在这里的内容：

- runtime 传输与默认配置
- code index 开关与索引行为
- 执行安全或系统级总开关
- 不直接属于工作流配置的项目级或引擎级设置

### Admin 面板或 CLI

适合放在这里的内容：

- recipe 校验
- run 检查
- artifact 浏览
- gate 调试
- run 清理
- 原始 workflow 引擎排障

## 用户体验要求

这个面板应读起来像产品功能面板，而不是开发控制台。

必须满足以下体验要求：

- 用户不需要先学习 workflow 内部概念
- 主要操作必须对应真实用户目标
- 说明文案优先使用产品语言，而不是 schema 语言
- 高级操作和维护操作不能占据默认路径

## 后续扩展规则

以后新增任何 `Workflows` 功能前，都必须先回答以下问题：

1. 这是否是普通 AIBridge 用户在日常使用 Codex、Claude Code、Cursor 时真的需要的功能？
2. 这是否属于工作流配置，而不是引擎设置？
3. 默认展示它，是否会提升用户理解，而不是仅仅方便维护者？

如果以上任一问题答案是否定的，就不应把该功能加入默认 `Workflows` 面板。

## 规划中的演进方向

预期方向如下：

1. 持续保留工作流相关的用户配置在 `Workflows` 面板中
2. 将维护型、调试型页签从默认用户面板移除或迁出
3. 如果确有用户价值，再增加聚焦 assistant 交接的窄入口

## 与当前实现的对齐说明

在编写本文件时，当前实现里仍存在若干偏向 workflow 引擎检视的页签，已经超出了默认用户面板的目标范围。

这些实现应被视为过渡状态，而不是产品定义本身。

真正的产品定义以本文件为准。
