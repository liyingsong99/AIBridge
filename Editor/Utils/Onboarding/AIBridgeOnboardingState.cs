using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// 首次设置向导的状态与默认值计算。保持无 UI、可单元测试。
    /// </summary>
    internal static class AIBridgeOnboardingState
    {
        public const int CurrentVersion = 1;

        private const string AutoOpenedSessionKey = "AIBridge.SetupWizard.AutoOpened";

        public static bool NeedsWizard()
        {
            return !AIBridgeProjectSettings.Instance.OnboardingCompleted;
        }

        public static void MarkCompleted()
        {
            var settings = AIBridgeProjectSettings.Instance;
            settings.EditorLanguageInitialized = true;
            settings.OnboardingCompleted = true;
            settings.OnboardingVersion = CurrentVersion;
            settings.SaveSettings();
        }

        public static bool WasAutoOpenedThisSession()
        {
            return SessionState.GetBool(AutoOpenedSessionKey, false);
        }

        public static void MarkAutoOpenedThisSession()
        {
            SessionState.SetBool(AutoOpenedSessionKey, true);
        }

        /// <summary>
        /// 首次进入向导时预选语言：跟随系统语言，用户仍可在向导里修改。
        /// </summary>
        public static AIBridgeEditorLanguage ResolveInitialLanguage()
        {
            return Application.systemLanguage == SystemLanguage.ChineseSimplified
                   || Application.systemLanguage == SystemLanguage.ChineseTraditional
                ? AIBridgeEditorLanguage.SimplifiedChinese
                : AIBridgeEditorLanguage.English;
        }

        /// <summary>
        /// 预选工具：
        /// 1. 有工具专属目录信号的优先；
        /// 2. 没有专属信号时，选独享 RootRule 文件的工具（Claude / WorkBuddy / Cursor / Cline）；
        /// 3. 只有共享 RootRule（AGENTS.md）时，只选组内第一个（Codex）；
        /// 4. 全无信号时回落 Codex，保持旧版首次体验。
        /// </summary>
        public static List<AssistantIntegrationTarget> ResolveInitialSelection(string projectRoot)
        {
            var targets = AssistantIntegrationRegistry.GetTargets()
                .Where(target => target != null)
                .OrderBy(target => target.SortOrder)
                .ToList();

            var selected = targets
                .Where(target => !string.IsNullOrEmpty(AssistantIntegrationDetector.GetFirstDetectedDirectory(projectRoot, target)))
                .ToList();
            if (selected.Count > 0)
            {
                return selected;
            }

            var groupsByRootRule = targets
                .Where(target => !string.IsNullOrEmpty(target.RootRuleFileName))
                .GroupBy(target => AssistantIntegrationGroupBuilder.NormalizeRelativePath(target.RootRuleFileName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                var rootRule = AssistantIntegrationGroupBuilder.NormalizeRelativePath(target.RootRuleFileName);
                List<AssistantIntegrationTarget> sharedTargets;
                if (!groupsByRootRule.TryGetValue(rootRule, out sharedTargets) || sharedTargets.Count != 1)
                {
                    continue;
                }

                if (File.Exists(Path.Combine(projectRoot, target.RootRuleFileName.Replace('/', Path.DirectorySeparatorChar))))
                {
                    selected.Add(target);
                }
            }

            if (selected.Count > 0)
            {
                return selected;
            }

            foreach (var pair in groupsByRootRule.OrderBy(pair => pair.Value.Min(target => target.SortOrder)))
            {
                var rootRulePath = Path.Combine(projectRoot, pair.Value[0].RootRuleFileName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(rootRulePath))
                {
                    selected.Add(pair.Value.OrderBy(target => target.SortOrder).First());
                }
            }

            if (selected.Count > 0)
            {
                return selected;
            }

            var codex = targets.FirstOrDefault(target => string.Equals(target.Id, "codex", StringComparison.OrdinalIgnoreCase));
            if (codex != null)
            {
                selected.Add(codex);
            }

            return selected;
        }
    }
}
