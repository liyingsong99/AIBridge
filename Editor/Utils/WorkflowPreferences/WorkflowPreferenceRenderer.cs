using System;
using System.Security.Cryptography;
using System.Text;

namespace AIBridge.Editor
{
    internal static class WorkflowPreferenceRenderer
    {
        public const string DevelopmentWorkflowSkillName = "aibridge-development-workflow";
        public const string PreferencesRelativePath = "references/project-workflow-preferences.md";
        public const string BranchSelectionRelativePath = "references/branch-selection.md";

        private const string ImplementationId = "implementation";
        private const string DebugId = "debug";
        private const string ReviewId = "review";
        private const string ValidationId = "validation";
        private const string OrchestrationId = "orchestration";

        private static readonly BranchInfo[] Branches =
        {
            new BranchInfo(ImplementationId, "实施分支", "创建、修改、修复、重构、生成、迁移、提交", "改动当前工作树并验证", "references/branches/implementation.md", "aibridge、aibridge-code-index、aibridge-prefab-patch、unity-yaml-editing、aibridge-batch-script", "aibridge、aibridge-prefab-patch、unity-yaml-editing、aibridge-batch-script"),
            new BranchInfo(DebugId, "调试诊断分支", "排查、诊断、复现、为什么、追踪、日志、Runtime、Player、Play Mode、性能、UI 异常", "收集证据并给出根因判断", "references/branches/debug.md", "aibridge、aibridge-code-index、aibridge-workflow-orchestration、aibridge-batch-script", "aibridge、aibridge-workflow-orchestration、aibridge-batch-script"),
            new BranchInfo(ReviewId, "审查分支", "review、audit、检查风险、设计评审、只读分析", "输出 confirmed findings 和剩余风险", "references/branches/review.md", "aibridge-code-index、按需 aibridge-workflow-orchestration", "按需 aibridge-workflow-orchestration"),
            new BranchInfo(ValidationId, "验证分支", "编译、日志、截图、测试、Runtime/UI 验证、回归确认", "给出可重复验证结果", "references/branches/validation.md", "aibridge、现有 workflow recipe", "aibridge、现有 workflow recipe"),
            new BranchInfo(OrchestrationId, "编排分支", "workflow recipe、多 Agent、并行 sweep、对抗验证、结构化 artifact", "设计或执行结构化 workflow", "references/branches/orchestration.md", "aibridge-workflow-orchestration", "aibridge-workflow-orchestration")
        };

        public static string RenderPreferences(string projectRoot, AssistantIntegrationTarget target)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var workflowUi = settings.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine("# 项目 Workflow 偏好");
            builder.AppendLine();
            builder.AppendLine("> 本文件由 AIBridge 根据项目默认设置生成。不要手动编辑。");
            builder.AppendLine();
            builder.AppendLine("- Assistant: " + target.DisplayName + " (`" + target.Id + "`)");
            builder.AppendLine();

            builder.AppendLine("## 启用分支");
            builder.AppendLine();
            builder.AppendLine("| 分支 | 状态 | 分支文档 |");
            builder.AppendLine("|---|---|---|");
            for (var i = 0; i < Branches.Length; i++)
            {
                var branch = Branches[i];
                builder.AppendLine("| " + branch.DisplayName + " | " + GetEnabledText(IsBranchEnabled(workflowUi, branch.Id)) + " | `" + branch.DocumentPath + "` |");
            }

            builder.AppendLine();
            builder.AppendLine("## 默认验证策略");
            builder.AppendLine();
            builder.AppendLine("- 默认验证级别：" + GetValidationLevelText(workflowUi.DefaultValidationLevel) + " (`" + AIBridgeProjectSettings.NormalizeWorkflowValidationLevel(workflowUi.DefaultValidationLevel) + "`)");
            builder.AppendLine("- Runtime 证据偏好：" + (workflowUi.PreferRuntimeEvidence ? "优先收集可用 Runtime 证据" : "仅在任务明确需要时收集 Runtime 证据"));
            if (settings.CodeIndex.EnableCodeIndex)
            {
                builder.AppendLine("- Code Index：" + (workflowUi.PreferCodeIndexGuidance
                    ? "已启用；优先用于快速 C# 声明文件定位（加载 `aibridge-code-index`）"
                    : "已启用；只有明确需要快速定位 C# 声明文件时才加载 `aibridge-code-index`"));
            }
            else
            {
                builder.AppendLine("- Code Index：已关闭。禁止加载 `aibridge-code-index`，禁止调用 `code_index`");
            }
            builder.AppendLine();

            builder.AppendLine("## 附加提示词");
            builder.AppendLine();
            AppendPrompt(builder, "通用提示词前缀", workflowUi.SharedPromptPrefix);
            settings.TryGetWorkflowAssistantPromptPrefix(target.Id, out var assistantPromptPrefix);
            AppendPrompt(builder, target.DisplayName + " 专属提示词前缀", assistantPromptPrefix);
            builder.AppendLine();

            builder.AppendLine("## 执行规则");
            builder.AppendLine();
            builder.AppendLine("1. Preflight / Skill 路由前必须先读取本文件。");
            builder.AppendLine("2. 只在启用分支中选择默认主分支；禁用分支不能自动进入。");
            builder.AppendLine("3. 如果用户明确要求进入禁用分支，先说明该分支已关闭并请求确认；不要自动进入。");
            builder.AppendLine("4. 选择主分支后，只读取该分支对应的 `references/branches/<branch>.md`，不要预加载其它分支文档。");
            builder.AppendLine("5. 验证和证据收集默认遵守本文件中的验证策略。");
            builder.AppendLine();
            builder.AppendLine("## 元数据");
            builder.AppendLine();
            builder.AppendLine("- Settings Hash: `" + ComputeSettingsHash(projectRoot, target) + "`");
            return AiRuleMarkdownNormalizer.TrimLineEndPunctuation(builder.ToString());
        }

        public static string RenderBranchSelection(string projectRoot, AssistantIntegrationTarget target)
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine("# 任务分流规则");
            builder.AppendLine();
            builder.AppendLine("> 本文件由 AIBridge 生成。进入分支判定前先读取 `project-workflow-preferences.md`。");
            builder.AppendLine();
            builder.AppendLine("## 工作流生命周期");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine("Preflight / Skill Routing");
            builder.AppendLine("  -> Mode Enter");
            builder.AppendLine("  -> Mode Execute");
            builder.AppendLine("  -> Mode Exit / SkillHandoff / Release");
            builder.AppendLine("  -> Transition Preflight");
            builder.AppendLine("```");
            builder.AppendLine();
            builder.AppendLine("- Preflight / Skill Routing 是入口步骤，不是业务模式；它只选择主分支并计算 Skill 状态。");
            builder.AppendLine("- 项目侧能力以 Root Rule / preferences 为准；勿用 harness status 做 enablement 预检。");
            builder.AppendLine("- 只有运行时任务命令失败、降级、阻塞，或能力状态改变工具选择时，才在当前业务分支输出中简短补充工具策略。");
            builder.AppendLine("- 如果需求边界、验收标准或方案方向不清晰，先进入需求讨论分支，确认后再继续正式分支选择。");
            builder.AppendLine("- Mode Enter 只激活当前分支真正需要的 Skill，并读取该分支文档。");
            builder.AppendLine("- Mode Exit 生成 `SkillHandoff`，并释放下一模式不需要的模式专用 Skill。");
            builder.AppendLine();

            builder.AppendLine("## 需求讨论分支");
            builder.AppendLine();
            builder.AppendLine("需求讨论分支是 Preflight 的前置分支，不是可选主分支。需求不清晰、边界待定、方案分歧，或用户要求先分析/先确认时触发。");
            builder.AppendLine();
            builder.AppendLine("- 目标是收敛目标、边界、非目标、约束、方案选项和确认结论。");
            builder.AppendLine("- 方案写入规则见 `references/branches/requirements.md`（默认 `.aibridge/plan/<slug>.md`）。");
            builder.AppendLine();

            builder.AppendLine("## 可选主分支");
            builder.AppendLine();
            builder.AppendLine("| 主分支 | 触发信号 | 默认目标 | 进入后读取 | 常用 Skills / 工具 |");
            builder.AppendLine("|---|---|---|---|---|");
            var enabledCount = 0;
            for (var i = 0; i < Branches.Length; i++)
            {
                var branch = Branches[i];
                if (!IsBranchEnabled(workflowUi, branch.Id))
                {
                    continue;
                }

                enabledCount++;
                builder.AppendLine("| " + branch.DisplayName + " | " + branch.TriggerSignals + " | " + branch.DefaultGoal + " | `" + branch.DocumentPath + "` | `" + GetCommonSkills(branch) + "` |");
            }

            if (enabledCount == 0)
            {
                builder.AppendLine("| 验证分支 | 编译、日志、截图、测试、Runtime/UI 验证、回归确认 | 给出可重复验证结果 | `references/branches/validation.md` | `aibridge` |");
            }

            builder.AppendLine();
            AppendDisabledBranches(builder, workflowUi);
            builder.AppendLine("## 交接规则");
            builder.AppendLine();
            builder.AppendLine("- 调试诊断分支发现 confirmed 根因且用户要求修复时，交接到实施分支；如果实施分支被禁用，先请求用户确认。");
            builder.AppendLine("- 实施分支完成改动后，按风险选择验证分支补充 Runtime、截图、UI 或多目标证据；如果验证分支被禁用，说明剩余风险。");
            builder.AppendLine("- 审查分支发现问题后，未得到修复授权前不直接改文件。");
            builder.AppendLine("- 编排分支只定义流程、角色、artifact 和 gate；具体 Unity 对象修改仍由实施分支串行完成。");
            builder.AppendLine("- Mode Exit 或分支交接时同步交接上下文：面向用户只列关键产出、必要 artifact refs、gate 状态、未关闭风险和下一步动作。");
            builder.AppendLine();

            builder.AppendLine("## Skill 列出策略");
            builder.AppendLine();
            builder.AppendLine("- `Preflight / Skill 路由` 只做内部选路，不要求对用户显式输出 `baselineSkills`、`activeSkills`、`deferredSkills` 或 `guardedSkills`。");
            builder.AppendLine("- `【模式：...】` 进入时列当前 `Skills`，仅在 active Skills 变化时重新列。");
            builder.AppendLine("- 执行进度、检查清单、Mode Exit 和面向用户的最终回复不列 `使用 Skills`、已释放 Skills 或下一步建议 Skills。");
            builder.AppendLine("- 只有跨模式续跑、外部 agent 交接或 `workflow import` 需要结构化结果时，才在 `SkillHandoff` 数据中记录 releasedSkills / nextRecommendedSkills。");
            builder.AppendLine();

            builder.AppendLine("## 输出格式");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine("【模式：需求讨论分支】");
            builder.AppendLine("Skills：aibridge-development-workflow");
            builder.AppendLine("已加载规范：requirements.md、risk-gates.md");
            builder.AppendLine("输出目标：收敛需求边界并输出 `.aibridge/plan` 工作底稿。");
            builder.AppendLine();
            builder.AppendLine("【模式：<启用分支之一>】");
            builder.AppendLine("Skills：<当前分支 Skills>");
            builder.AppendLine("已加载规范：<当前分支文档>");
            builder.AppendLine("输出目标：<本模式的验收目标>");
            builder.AppendLine("```");
            builder.AppendLine();

            return AiRuleMarkdownNormalizer.TrimLineEndPunctuation(builder.ToString());
        }

        public static string ComputeSettingsHash(string projectRoot, AssistantIntegrationTarget target)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var workflowUi = settings.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine(target == null ? string.Empty : target.Id);
            builder.AppendLine(settings.CodeIndex.EnableCodeIndex.ToString());
            builder.AppendLine(workflowUi.EnableImplementationBranch.ToString());
            builder.AppendLine(workflowUi.EnableDebugBranch.ToString());
            builder.AppendLine(workflowUi.EnableReviewBranch.ToString());
            builder.AppendLine(workflowUi.EnableValidationBranch.ToString());
            builder.AppendLine(workflowUi.EnableOrchestrationBranch.ToString());
            builder.AppendLine(AIBridgeProjectSettings.NormalizeWorkflowValidationLevel(workflowUi.DefaultValidationLevel));
            builder.AppendLine(workflowUi.PreferRuntimeEvidence.ToString());
            builder.AppendLine(workflowUi.PreferCodeIndexGuidance.ToString());
            builder.AppendLine(workflowUi.SharedPromptPrefix ?? string.Empty);
            if (target != null)
            {
                AIBridgeProjectSettings.Instance.TryGetWorkflowAssistantPromptPrefix(target.Id, out var assistantPromptPrefix);
                builder.AppendLine(assistantPromptPrefix ?? string.Empty);
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static bool IsBranchEnabled(AIBridgeProjectSettings.WorkflowUiSettingsData workflowUi, string branchId)
        {
            switch (branchId)
            {
                case ImplementationId:
                    return workflowUi.EnableImplementationBranch;
                case DebugId:
                    return workflowUi.EnableDebugBranch;
                case ReviewId:
                    return workflowUi.EnableReviewBranch;
                case ValidationId:
                    return workflowUi.EnableValidationBranch;
                case OrchestrationId:
                    return workflowUi.EnableOrchestrationBranch;
                default:
                    return false;
            }
        }

        private static string GetEnabledText(bool enabled)
        {
            return enabled ? "启用" : "禁用";
        }

        private static string GetValidationLevelText(string validationLevel)
        {
            switch (AIBridgeProjectSettings.NormalizeWorkflowValidationLevel(validationLevel))
            {
                case "compileOnly":
                    return "仅 Unity 编译";
                case "compileLogsAndRuntime":
                    return "Unity 编译 + Error 日志 + 可用时 Runtime 证据";
                default:
                    return "Unity 编译 + Error 日志";
            }
        }

        private static void AppendPrompt(StringBuilder builder, string title, string promptPrefix)
        {
            builder.AppendLine("### " + title);
            builder.AppendLine();
            if (string.IsNullOrWhiteSpace(promptPrefix))
            {
                builder.AppendLine("未设置。");
                builder.AppendLine();
                return;
            }

            builder.AppendLine("```text");
            builder.AppendLine(promptPrefix.Trim());
            builder.AppendLine("```");
            builder.AppendLine();
        }

        private static void AppendDisabledBranches(StringBuilder builder, AIBridgeProjectSettings.WorkflowUiSettingsData workflowUi)
        {
            var wroteHeader = false;
            for (var i = 0; i < Branches.Length; i++)
            {
                var branch = Branches[i];
                if (IsBranchEnabled(workflowUi, branch.Id))
                {
                    continue;
                }

                if (!wroteHeader)
                {
                    builder.AppendLine("## 禁用分支");
                    builder.AppendLine();
                    wroteHeader = true;
                }

                builder.AppendLine("- " + branch.DisplayName + "：禁用。不要自动进入；如果用户明确要求，先请求确认。");
            }

            if (wroteHeader)
            {
                builder.AppendLine();
            }
        }

        private static string GetCommonSkills(BranchInfo branch)
        {
            return AIBridgeProjectSettings.Instance.CodeIndex.EnableCodeIndex
                ? branch.CommonSkillsWithCodeIndex
                : branch.CommonSkillsWithoutCodeIndex;
        }

        private sealed class BranchInfo
        {
            public BranchInfo(
                string id,
                string displayName,
                string triggerSignals,
                string defaultGoal,
                string documentPath,
                string commonSkillsWithCodeIndex,
                string commonSkillsWithoutCodeIndex)
            {
                Id = id;
                DisplayName = displayName;
                TriggerSignals = triggerSignals;
                DefaultGoal = defaultGoal;
                DocumentPath = documentPath;
                CommonSkillsWithCodeIndex = commonSkillsWithCodeIndex;
                CommonSkillsWithoutCodeIndex = commonSkillsWithoutCodeIndex;
            }

            public string Id { get; private set; }
            public string DisplayName { get; private set; }
            public string TriggerSignals { get; private set; }
            public string DefaultGoal { get; private set; }
            public string DocumentPath { get; private set; }
            public string CommonSkillsWithCodeIndex { get; private set; }
            public string CommonSkillsWithoutCodeIndex { get; private set; }
        }
    }
}
