using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AIBridge.Editor
{
    [InitializeOnLoad]
    internal sealed class AIBridgeRuntimeBuildProcessor : IPreprocessBuildWithReport
    {
        private const string AutoInjectDisabledDefine = "AIBRIDGE_RUNTIME_AUTO_INJECT_DISABLED";
        private const string ReleaseBuildAllowedDefine = "AIBRIDGE_RUNTIME_ALLOW_RELEASE_BUILD";

        static AIBridgeRuntimeBuildProcessor()
        {
            EditorApplication.delayCall += SyncRuntimeBootstrapDefinesForActiveTarget;
        }

        public int callbackOrder
        {
            get { return 0; }
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = AIBridgeProjectSettings.Instance.RuntimeBridge;
            var buildTargetGroup = report != null
                ? BuildPipeline.GetBuildTargetGroup(report.summary.platform)
                : BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);

            var symbolsChanged = SyncRuntimeBootstrapDefines(buildTargetGroup, settings, true);
            if (symbolsChanged)
            {
                throw new BuildFailedException(AIBridgeEditorText.T(
                    "AIBridge Runtime Bridge build symbols changed. Unity must recompile scripts before this build can be trusted; please build again.",
                    "AIBridge Runtime Bridge 构建宏已变化。Unity 需要先重新编译脚本，请重新执行构建。"));
            }

            var isDevelopmentBuild = report != null
                && (report.summary.options & BuildOptions.Development) == BuildOptions.Development;
            LogBuildInjectionState(settings, isDevelopmentBuild);
        }

        internal static void SyncRuntimeBootstrapDefinesForActiveTarget()
        {
            SyncRuntimeBootstrapDefines(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget),
                AIBridgeProjectSettings.Instance.RuntimeBridge,
                false);
        }

        private static bool SyncRuntimeBootstrapDefines(
            BuildTargetGroup buildTargetGroup,
            AIBridgeProjectSettings.RuntimeBridgeSettingsData settings,
            bool logChanges)
        {
            if (buildTargetGroup == BuildTargetGroup.Unknown || settings == null)
            {
                return false;
            }

            var symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup);
            var defines = ParseDefines(symbols);

            var changed = false;
            changed |= SetDefine(
                defines,
                AutoInjectDisabledDefine,
                !settings.EnableRuntimeBridge || !settings.AutoInjectRuntimeBridgeInDevelopmentBuild);
            changed |= SetDefine(
                defines,
                ReleaseBuildAllowedDefine,
                settings.EnableRuntimeBridge && settings.AllowRuntimeBridgeInReleaseBuild);
            changed |= SetDefine(
                defines,
                AIBridgeHybridClrUtility.HybridClrAvailableDefine,
                AIBridgeHybridClrUtility.IsHybridClrInstalled());

            if (!changed)
            {
                return false;
            }

            PlayerSettings.SetScriptingDefineSymbolsForGroup(buildTargetGroup, string.Join(";", defines.ToArray()));
            if (logChanges)
            {
                Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Runtime Bridge bootstrap scripting symbols synchronized.",
                    "[AIBridge] Runtime Bridge bootstrap 脚本宏已同步。"));
            }

            return true;
        }

        private static List<string> ParseDefines(string symbols)
        {
            var defines = new List<string>();
            if (string.IsNullOrEmpty(symbols))
            {
                return defines;
            }

            var parts = symbols.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var define = parts[i].Trim();
                if (!string.IsNullOrEmpty(define) && !defines.Contains(define))
                {
                    defines.Add(define);
                }
            }

            return defines;
        }

        private static bool SetDefine(List<string> defines, string define, bool enabled)
        {
            var index = defines.IndexOf(define);
            if (enabled)
            {
                if (index >= 0)
                {
                    return false;
                }

                defines.Add(define);
                return true;
            }

            if (index < 0)
            {
                return false;
            }

            defines.RemoveAt(index);
            return true;
        }

        private static void LogBuildInjectionState(
            AIBridgeProjectSettings.RuntimeBridgeSettingsData settings,
            bool isDevelopmentBuild)
        {
            if (settings == null || !settings.EnableRuntimeBridge)
            {
                Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Runtime Bridge is disabled; bootstrap auto injection will not run.",
                    "[AIBridge] Runtime Bridge 已关闭，bootstrap 自动注入不会运行。"));
                return;
            }

            if (!settings.AutoInjectRuntimeBridgeInDevelopmentBuild)
            {
                Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Runtime Bridge bootstrap auto injection is disabled in settings.",
                    "[AIBridge] Runtime Bridge bootstrap 自动注入已在设置中关闭。"));
                return;
            }

            if (isDevelopmentBuild)
            {
                Debug.Log(AIBridgeEditorText.T(
                    "[AIBridge] Development Build will auto inject AIBridgeRuntime bootstrap.",
                    "[AIBridge] Development Build 将自动注入 AIBridgeRuntime bootstrap。"));
                return;
            }

            if (settings.AllowRuntimeBridgeInReleaseBuild)
            {
                Debug.LogWarning(AIBridgeEditorText.T(
                    "[AIBridge] Release Build Runtime Bridge is explicitly allowed; bootstrap can create AIBridgeRuntime.",
                    "[AIBridge] Release Build Runtime Bridge 已显式允许，bootstrap 可创建 AIBridgeRuntime。"));
                return;
            }

            Debug.Log(AIBridgeEditorText.T(
                "[AIBridge] Release Build Runtime Bridge remains disabled by default.",
                "[AIBridge] Release Build Runtime Bridge 保持默认关闭。"));
        }
    }
}
