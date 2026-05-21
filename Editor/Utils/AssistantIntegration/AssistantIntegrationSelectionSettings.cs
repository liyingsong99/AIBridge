using System.Collections.Generic;
using UnityEditor;

namespace AIBridge.Editor
{
    internal static class AssistantIntegrationSelectionSettings
    {
        private const string KeyPrefix = "AIBridge_AssistantIntegration_";
        private const string DefaultTargetId = "codex";

        public static bool GetSelected(string targetId, bool defaultValue = false)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (settings.TryGetAssistantSelection(targetId, out var selected))
            {
                return selected;
            }

            return defaultValue;
        }

        public static void SetSelected(string targetId, bool value)
        {
            var settings = AIBridgeProjectSettings.Instance;
            if (!settings.SetAssistantSelection(targetId, value))
            {
                return;
            }

            settings.SaveSettings();
        }

        public static void EnsureDefaults(string projectRoot, IReadOnlyList<AssistantIntegrationTarget> targets)
        {
            var settings = AIBridgeProjectSettings.Instance;
            var changed = false;
            var hasProjectSelection = HasProjectSelection(settings, targets);
            var hasLegacySelection = HasLegacyEditorPrefsSelection(targets);

            if (!hasProjectSelection && !hasLegacySelection)
            {
                var defaultTargetId = ResolveDefaultTargetId(projectRoot, targets);
                foreach (var target in targets)
                {
                    if (settings.SetAssistantSelection(target.Id, string.Equals(target.Id, defaultTargetId, System.StringComparison.OrdinalIgnoreCase)))
                    {
                        changed = true;
                    }
                }

                if (changed)
                {
                    settings.SaveSettings();
                }

                return;
            }

            foreach (var target in targets)
            {
                if (settings.TryGetAssistantSelection(target.Id, out _))
                {
                    continue;
                }

                var key = GetSelectionKey(target.Id);
                if (EditorPrefs.HasKey(key))
                {
                    if (settings.SetAssistantSelection(target.Id, EditorPrefs.GetBool(key, false)))
                    {
                        changed = true;
                    }
                    EditorPrefs.DeleteKey(key);
                    continue;
                }

                if (settings.SetAssistantSelection(target.Id, false))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                settings.SaveSettings();
            }
        }

        public static Dictionary<string, bool> LoadSelections(string projectRoot, IReadOnlyList<AssistantIntegrationTarget> targets)
        {
            EnsureDefaults(projectRoot, targets);

            var selections = new Dictionary<string, bool>(targets.Count);
            foreach (var target in targets)
            {
                selections[target.Id] = GetSelected(target.Id);
            }

            return selections;
        }

        private static string GetSelectionKey(string targetId)
        {
            return KeyPrefix + targetId;
        }

        private static bool HasProjectSelection(AIBridgeProjectSettings settings, IReadOnlyList<AssistantIntegrationTarget> targets)
        {
            foreach (var target in targets)
            {
                if (settings.TryGetAssistantSelection(target.Id, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLegacyEditorPrefsSelection(IReadOnlyList<AssistantIntegrationTarget> targets)
        {
            foreach (var target in targets)
            {
                if (EditorPrefs.HasKey(GetSelectionKey(target.Id)))
                {
                    return true;
                }
            }

            return false;
        }

        public static string ResolveDefaultTargetId(string projectRoot, IReadOnlyList<AssistantIntegrationTarget> targets)
        {
            AssistantIntegrationTarget bestTarget = null;
            AssistantIntegrationDetection bestDetection = null;
            var bestOrder = int.MaxValue;

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var detection = AssistantIntegrationDetector.Detect(projectRoot, target);
                if (!detection.IsDetected)
                {
                    continue;
                }

                var order = GetTargetTieBreakOrder(target.Id);
                if (bestDetection == null
                    || detection.Priority > bestDetection.Priority
                    || (detection.Priority == bestDetection.Priority && order < bestOrder))
                {
                    bestTarget = target;
                    bestDetection = detection;
                    bestOrder = order;
                }
            }

            if (bestTarget != null)
            {
                return bestTarget.Id;
            }

            foreach (var target in targets)
            {
                if (string.Equals(target.Id, DefaultTargetId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return target.Id;
                }
            }

            return targets.Count > 0 ? targets[0].Id : null;
        }

        private static int GetTargetTieBreakOrder(string targetId)
        {
            if (string.Equals(targetId, "codex", System.StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(targetId, "claude", System.StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (string.Equals(targetId, "cursor", System.StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(targetId, "cline", System.StringComparison.OrdinalIgnoreCase))
            {
                return 3;
            }

            return 100;
        }
    }
}
