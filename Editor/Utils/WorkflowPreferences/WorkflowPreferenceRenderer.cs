using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AIBridge.Editor
{
    internal static class WorkflowPreferenceRenderer
    {
        public const string DevelopmentWorkflowSkillName = "aibridge-development-workflow";
        public const string PreferencesRelativePath = "references/project-workflow-preferences.md";
        public const string BranchSelectionRelativePath = "references/branch-selection.md";
        public const string GraphManifestRelativePath = "references/workflow-graph.manifest.json";
        public const string ImplementationBranchManifestRelativePath = "references/implementation-branch.manifest.json";

        private const string ImplementationId = "implementation";
        private const string DebugId = "debug";
        private const string ReviewId = "review";
        private const string ValidationId = "validation";
        private const string OrchestrationId = "orchestration";

        private static readonly BranchInfo[] Branches =
        {
            new BranchInfo(ImplementationId, "实施分支", "创建、修改、修复、重构、生成、迁移、提交", "改动当前工作树并验证", "references/branches/implementation.md", "aibridge、aibridge-code-index、aibridge-prefab-patch、unity-yaml-editing、aibridge-batch-script"),
            new BranchInfo(DebugId, "调试诊断分支", "排查、诊断、复现、为什么、追踪、日志、Runtime、Player、Play Mode、性能、UI 异常", "收集证据并给出根因判断", "references/branches/debug.md", "aibridge、aibridge-code-index、aibridge-workflow-orchestration、aibridge-batch-script"),
            new BranchInfo(ReviewId, "审查分支", "review、audit、检查风险、设计评审、只读分析", "输出 confirmed findings 和剩余风险", "references/branches/review.md", "aibridge-code-index、宿主搜索/读取工具、按需 aibridge-workflow-orchestration"),
            new BranchInfo(ValidationId, "验证分支", "编译、日志、截图、测试、Runtime/UI 验证、回归确认", "给出可重复验证结果", "references/branches/validation.md", "aibridge、现有 workflow recipe"),
            new BranchInfo(OrchestrationId, "编排分支", "workflow recipe、多 Agent、并行 sweep、对抗验证、结构化 artifact", "设计或执行结构化 workflow", "references/branches/orchestration.md", "aibridge-workflow-orchestration")
        };

        public static string RenderPreferences(string projectRoot, AssistantIntegrationTarget target)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var workflowUi = settings.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine("# 项目 Workflow 偏好");
            builder.AppendLine();
            builder.AppendLine("> 本文件由 AIBridge/Workflows 根据项目设置生成。不要手动编辑；修改请回到 Unity 的 `AIBridge/Workflows > Workflow Options`。");
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
            builder.AppendLine("- Code Index 偏好：" + (workflowUi.PreferCodeIndexGuidance ? "Code Index 可用时优先用于快速 C# 声明文件定位" : "默认使用宿主自带的搜索/文件读取工具；只有明确需要快速定位 C# 声明文件时才使用 Code Index"));
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
            builder.AppendLine("3. 如果用户明确要求进入禁用分支，先说明该分支已关闭，并请求用户确认是否临时继续或回到 Workflows 面板启用。");
            builder.AppendLine("4. 选择主分支后，只读取该分支对应的 `references/branches/<branch>.md`，不要预加载其它分支文档。");
            builder.AppendLine("5. 验证和证据收集默认遵守本文件中的验证策略。");
            builder.AppendLine();
            builder.AppendLine("## 元数据");
            builder.AppendLine();
            builder.AppendLine("- Settings Hash: `" + ComputeSettingsHash(projectRoot, target) + "`");
            return builder.ToString();
        }

        public static string RenderBranchSelection(string projectRoot, AssistantIntegrationTarget target)
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine("# 任务分流规则");
            builder.AppendLine();
            builder.AppendLine("> 本文件由 AIBridge/Workflows 生成。进入分支判定前先读取 `project-workflow-preferences.md`。");
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
            builder.AppendLine("- Harness 判定是 Preflight gate，不是业务分支固定步骤；fresh 且不影响工具选择时不单独输出。");
            builder.AppendLine("- 只有缺失、过期、降级、阻塞、用户要求说明，或能力状态改变工具选择时，才在入口块简短展开 Harness 状态。");
            builder.AppendLine("- 如果需求边界、验收标准或方案方向不清晰，先进入需求讨论分支，确认后再继续正式分支选择。");
            builder.AppendLine("- Mode Enter 只激活当前分支真正需要的 Skill，并读取该分支文档。");
            builder.AppendLine("- Mode Exit 生成 `SkillHandoff`，并释放下一模式不需要的模式专用 Skill。");
            builder.AppendLine();

            builder.AppendLine("## 需求讨论分支");
            builder.AppendLine();
            builder.AppendLine("需求讨论分支是 Preflight 的前置分支，不是可选主分支。它只在需求不清晰、边界待定、方案方向分歧，或用户要求先分析/先确认时触发。");
            builder.AppendLine();
            builder.AppendLine("- 目标是收敛目标、边界、非目标、约束、方案选项和确认结论。");
            builder.AppendLine("- 若用户要求，或项目存在相应功能文档归类，确认后的方案必须先写入 `.aibridge/plan` 工作底稿，再按需同步到正式文档位置。");
            builder.AppendLine("- 默认以 Markdown 工作底稿作为方案源文件；当方案包含流程图、决策树、对比表，或更适合开发者浏览时，再在每个落点目录内同步生成 HTML 展示页。");
            builder.AppendLine("- Markdown 和 HTML 应保持同目录、同 basename；`.aibridge/plan` 负责 AI 续跑和多 agent 协作，正式文档负责 Git review 和对外呈现。");
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
                builder.AppendLine("| " + branch.DisplayName + " | " + branch.TriggerSignals + " | " + branch.DefaultGoal + " | `" + branch.DocumentPath + "` | `" + branch.CommonSkills + "` |");
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
            builder.AppendLine("- `【入口：Preflight / Skill 路由】` 列 `baselineSkills`、`activeSkills`，必要时列 `deferredSkills` / `guardedSkills`。");
            builder.AppendLine("- `【模式：...】` 进入时列当前 `Skills`，仅在 active Skills 变化时重新列。");
            builder.AppendLine("- 执行进度、检查清单、Mode Exit 和面向用户的最终回复不列 `使用 Skills`、已释放 Skills 或下一步建议 Skills。");
            builder.AppendLine("- 只有跨模式续跑、外部 agent 交接或 `workflow import` 需要结构化结果时，才在 `SkillHandoff` 数据中记录 releasedSkills / nextRecommendedSkills。");
            builder.AppendLine();

            builder.AppendLine("## 输出格式");
            builder.AppendLine();
            builder.AppendLine("```text");
            builder.AppendLine("【入口：Preflight / Skill 路由】");
            builder.AppendLine("baselineSkills：aibridge-development-workflow");
            builder.AppendLine("activeSkills：<当前分支 Skills>");
            builder.AppendLine("主分支：<启用分支之一>");
            builder.AppendLine("理由：<进入该分支的依据>");
            builder.AppendLine();
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

            return builder.ToString();
        }

        public static string RenderGraphManifest(string projectRoot, AssistantIntegrationTarget target)
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            var manifest = new WorkflowGraphManifestData
            {
                schemaVersion = 1,
                kind = "aibridge-workflow-routing-graph",
                generatedFrom = new WorkflowGraphManifestGeneratedFromData
                {
                    assistant = target == null ? string.Empty : target.Id,
                    settingsHash = ComputeSettingsHash(projectRoot, target)
                },
                nodes = new[]
                {
                    CreateGraphNode("root", "Root", "Root", "Task Entry", null, false, true),
                    CreateGraphNode("preflight", "Preflight", "Preflight / Skill Routing", "Project preferences and Skill routing", null, false, true),
                    CreateGraphNode("requirements", "Condition", "Requirements Discussion", "When scope or risk is unclear", "references/branches/requirements.md", false, true),
                    CreateGraphNode("selector", "Selector", "Branch Selector", "Enabled workflow branches", null, false, true),
                    CreateGraphNode("branch-implementation", "Action", "Implementation Branch", "Change-oriented", "references/branches/implementation.md", true, workflowUi.EnableImplementationBranch),
                    CreateGraphNode("branch-debug", "Action", "Debug Branch", "Diagnosis-oriented", "references/branches/debug.md", true, workflowUi.EnableDebugBranch),
                    CreateGraphNode("branch-review", "Action", "Review Branch", "Read-only review", "references/branches/review.md", true, workflowUi.EnableReviewBranch),
                    CreateGraphNode("branch-validation", "Action", "Validation Branch", "Compile / logs / runtime", "references/branches/validation.md", true, workflowUi.EnableValidationBranch),
                    CreateGraphNode("branch-orchestration", "Action", "Orchestration Branch", "Multi-step workflow", "references/branches/orchestration.md", true, workflowUi.EnableOrchestrationBranch),
                    CreateGraphNode("handoff", "Handoff", "Mode Exit / SkillHandoff", "Artifact refs, gates, and open risks", null, false, true)
                },
                edges = new[]
                {
                    CreateGraphEdge("root", "preflight", null),
                    CreateGraphEdge("preflight", "requirements", "on demand"),
                    CreateGraphEdge("requirements", "selector", "confirmed"),
                    CreateGraphEdge("preflight", "selector", "ready"),
                    CreateGraphEdge("selector", "branch-implementation", "change"),
                    CreateGraphEdge("selector", "branch-debug", "diagnose"),
                    CreateGraphEdge("selector", "branch-review", "review"),
                    CreateGraphEdge("selector", "branch-validation", "validate"),
                    CreateGraphEdge("selector", "branch-orchestration", "recipe"),
                    CreateGraphEdge("branch-implementation", "handoff", null),
                    CreateGraphEdge("branch-debug", "handoff", null),
                    CreateGraphEdge("branch-review", "handoff", null),
                    CreateGraphEdge("branch-validation", "handoff", null),
                    CreateGraphEdge("branch-orchestration", "handoff", null)
                }
            };

            return JsonUtility.ToJson(manifest, true);
        }

        public static string RenderImplementationBranchManifest()
        {
            var manifest = new WorkflowBranchManifestData
            {
                schemaVersion = 1,
                kind = "aibridge-workflow-branch-graph",
                branchId = "implementation",
                title = "Implementation Branch",
                subtitle = "Branch Detail",
                summary = "Editor-only branch detail graph for implementation tasks.",
                nodes = new[]
                {
                    CreateBranchNode("implementation-root", "Root", "Implementation Branch", "Change Task Entry", "Ready", "Entry node for implementation tasks.", "references/branches/implementation.md", null, "branch-root", false, 0, 0, 0, CreateDetails(("Goal", "Change current worktree and verify"), ("Source", "implementation.md"))),
                    CreateBranchNode("implementation-locate", "Action", "Locate Real Path", "Find actual code or asset entry", "Ready", "Locate the real implementation path before changing anything.", "references/branches/implementation.md", "implementation-root", "mandatory-step", false, 1, 0, 0, CreateDetails(("Rule", "Locate real code path before editing"), ("Why", "Avoid changing guessed or mirrored paths"))),
                    CreateBranchNode("implementation-risk", "Condition", "Risk Gate", "Boundary / compatibility / acceptance", "Ready", "Confirm the change is still within the agreed scope.", "references/risk-gates.md", "implementation-root", "mandatory-gate", false, 2, 0, 0, CreateDetails(("Check", "scope / compatibility / acceptance"), ("Fallback", "Return to requirements or debug when preconditions are not met"))),
                    CreateBranchNode("implementation-modify", "Action", "Modify Worktree", "Scoped implementation", "Ready", "Change only the real control point and keep the diff narrow.", "references/branches/implementation.md", "implementation-root", "mandatory-step", false, 3, 0, 0, CreateDetails(("Rule", "Keep diff narrow"), ("Tooling", "aibridge / code-index / prefab-patch / yaml-editing on demand"))),
                    CreateBranchNode("implementation-verify", "Gate", "Verify Result", "Compile and required evidence", "Ready", "Run the default verification path for the project after the change.", "references/checklist.md", "implementation-root", "mandatory-gate", false, 4, 0, 0, CreateDetails(("Default", "compileAndLogs"), ("Command", "$CLI compile unity -> $CLI get_logs --logType Error"))),
                    CreateBranchNode("implementation-handoff", "Handoff", "Handoff", "Result / risk / next step", "NotStarted", "Summarize what changed, what was verified, and what remains.", "references/checklist.md", "implementation-root", "handoff", false, 5, 0, 0, CreateDetails(("Output", "changed files / verification / residual risk"), ("Next", "validation branch when extra proof is needed"))),
                    CreateBranchNode("implementation-editor", "Condition", "Editor Generation", "Only for complex one-off editor tasks", "NotStarted", "Optional branch for complex editor-side generation work.", "references/editor-generation.md", "implementation-root", "optional-branch", true, 3, 1, 1, CreateDetails(("Condition", "complex one-off editor C# task"), ("Optional", "yes"))),
                    CreateBranchNode("implementation-runtime", "Condition", "Runtime Evidence", "Only when task explicitly needs runtime proof", "NotStarted", "Optional verification branch for runtime or UI evidence.", "references/branches/validation.md", "implementation-root", "optional-branch", true, 4, 1, 1, CreateDetails(("Condition", "runtime or UI evidence explicitly required"), ("Optional", "yes")))
                },
                edges = new[]
                {
                    CreateBranchEdge("implementation-root", "implementation-locate", null, "Flow"),
                    CreateBranchEdge("implementation-locate", "implementation-risk", null, "Flow"),
                    CreateBranchEdge("implementation-risk", "implementation-modify", null, "Flow"),
                    CreateBranchEdge("implementation-modify", "implementation-verify", null, "Flow"),
                    CreateBranchEdge("implementation-verify", "implementation-handoff", null, "Flow"),
                    CreateBranchEdge("implementation-risk", "implementation-editor", "complex editor task", "OptionalFlow"),
                    CreateBranchEdge("implementation-editor", "implementation-modify", "generated", "OptionalFlow"),
                    CreateBranchEdge("implementation-verify", "implementation-runtime", "runtime needed", "OptionalFlow"),
                    CreateBranchEdge("implementation-runtime", "implementation-handoff", "evidence ready", "OptionalFlow")
                }
            };

            return JsonUtility.ToJson(manifest, true);
        }

        public static string ComputeSettingsHash(string projectRoot, AssistantIntegrationTarget target)
        {
            var workflowUi = AIBridgeProjectSettings.Instance.WorkflowUi;
            var builder = new StringBuilder();
            builder.AppendLine(target == null ? string.Empty : target.Id);
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

        public static int CountEnabledBranches(AIBridgeProjectSettings.WorkflowUiSettingsData workflowUi)
        {
            var count = 0;
            for (var i = 0; i < Branches.Length; i++)
            {
                if (IsBranchEnabled(workflowUi, Branches[i].Id))
                {
                    count++;
                }
            }

            return count;
        }

        private static WorkflowGraphManifestNodeData CreateGraphNode(
            string id,
            string graphKind,
            string title,
            string subtitle,
            string source,
            bool editable,
            bool enabled)
        {
            return new WorkflowGraphManifestNodeData
            {
                id = id,
                kind = graphKind,
                title = title,
                subtitle = subtitle,
                source = source,
                editable = editable ? "setting" : "false",
                enabled = enabled
            };
        }

        private static WorkflowGraphManifestEdgeData CreateGraphEdge(string from, string to, string condition)
        {
            return new WorkflowGraphManifestEdgeData
            {
                from = from,
                to = to,
                condition = condition
            };
        }

        private static WorkflowBranchManifestNodeData CreateBranchNode(
            string id,
            string graphKind,
            string title,
            string subtitle,
            string state,
            string description,
            string source,
            string parent,
            string semanticRole,
            bool optional,
            int column,
            int order,
            int row,
            WorkflowBranchManifestDetailData[] details)
        {
            return new WorkflowBranchManifestNodeData
            {
                id = id,
                kind = graphKind,
                title = title,
                subtitle = subtitle,
                state = state,
                description = description,
                source = source,
                parent = parent,
                semanticRole = semanticRole,
                optional = optional,
                column = column,
                order = order,
                row = row,
                details = details
            };
        }

        private static WorkflowBranchManifestEdgeData CreateBranchEdge(string from, string to, string label, string kind)
        {
            return new WorkflowBranchManifestEdgeData
            {
                from = from,
                to = to,
                label = label,
                kind = kind
            };
        }

        private static WorkflowBranchManifestDetailData[] CreateDetails(params (string label, string value)[] values)
        {
            if (values == null || values.Length == 0)
            {
                return Array.Empty<WorkflowBranchManifestDetailData>();
            }

            var details = new WorkflowBranchManifestDetailData[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                details[i] = new WorkflowBranchManifestDetailData
                {
                    label = values[i].label,
                    value = values[i].value
                };
            }

            return details;
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

        private sealed class BranchInfo
        {
            public BranchInfo(string id, string displayName, string triggerSignals, string defaultGoal, string documentPath, string commonSkills)
            {
                Id = id;
                DisplayName = displayName;
                TriggerSignals = triggerSignals;
                DefaultGoal = defaultGoal;
                DocumentPath = documentPath;
                CommonSkills = commonSkills;
            }

            public string Id { get; private set; }
            public string DisplayName { get; private set; }
            public string TriggerSignals { get; private set; }
            public string DefaultGoal { get; private set; }
            public string DocumentPath { get; private set; }
            public string CommonSkills { get; private set; }
        }

        [Serializable]
        private sealed class WorkflowGraphManifestData
        {
            public int schemaVersion;
            public string kind;
            public WorkflowGraphManifestGeneratedFromData generatedFrom;
            public WorkflowGraphManifestNodeData[] nodes;
            public WorkflowGraphManifestEdgeData[] edges;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestGeneratedFromData
        {
            public string assistant;
            public string settingsHash;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestNodeData
        {
            public string id;
            public string kind;
            public string title;
            public string subtitle;
            public string source;
            public string editable;
            public bool enabled;
        }

        [Serializable]
        private sealed class WorkflowGraphManifestEdgeData
        {
            public string from;
            public string to;
            public string condition;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestData
        {
            public int schemaVersion;
            public string kind;
            public string branchId;
            public string title;
            public string subtitle;
            public string summary;
            public WorkflowBranchManifestNodeData[] nodes;
            public WorkflowBranchManifestEdgeData[] edges;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestNodeData
        {
            public string id;
            public string kind;
            public string title;
            public string subtitle;
            public string state;
            public string description;
            public string source;
            public string parent;
            public string semanticRole;
            public bool optional;
            public int column;
            public int order;
            public int row;
            public WorkflowBranchManifestDetailData[] details;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestEdgeData
        {
            public string from;
            public string to;
            public string label;
            public string kind;
        }

        [Serializable]
        private sealed class WorkflowBranchManifestDetailData
        {
            public string label;
            public string value;
        }
    }
}
