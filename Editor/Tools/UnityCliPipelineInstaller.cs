using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_0_OR_NEWER
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
#endif

namespace AIBridge.Editor
{
    // 纯决策不依赖安装器静态初始化，测试不会创建 UPM 请求。
    internal static class UnityCliPipelineInstallerRules
    {
        internal const string PackageName = "com.unity.pipeline";

        internal static bool SupportsUnity(string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            var separator = version.IndexOf('.');
            return separator > 0 && int.TryParse(version.Substring(0, separator), out var major) && major >= 6000;
        }

        internal static bool CanStart(bool busy, string pendingVersion)
        {
            return !busy && string.IsNullOrEmpty(pendingVersion);
        }

        internal static bool IsInstalled(string name)
        {
            return string.Equals(name, PackageName, StringComparison.Ordinal);
        }

        internal static string SelectVersion(string name, string latestCompatible, IEnumerable<string> compatible)
        {
            if (!IsInstalled(name) || string.IsNullOrWhiteSpace(latestCompatible) || compatible == null) return null;
            foreach (var version in compatible)
            {
                if (string.Equals(version, latestCompatible, StringComparison.Ordinal)) return latestCompatible;
            }
            return null;
        }

        internal static bool MatchesTarget(string name, string installedVersion, string targetVersion)
        {
            return IsInstalled(name) && !string.IsNullOrEmpty(targetVersion) &&
                string.Equals(installedVersion, targetVersion, StringComparison.Ordinal);
        }
    }

    [InitializeOnLoad]
    internal static class UnityCliPipelineInstaller
    {
        private const string PendingKey = "AIBridge.UnityCliPipelineInstaller.PendingVersion";
        private const string MenuPath = "AIBridge/Install Unity CLI Dependencies";
#if UNITY_6000_0_OR_NEWER
        private const double QueryTimeoutSeconds = 120;
        private const double WriteWarningSeconds = 180;
        private enum Phase { Idle, Listing, Searching, Adding, Verifying }
        private static Phase phase;
        private static ListRequest list;
        private static SearchRequest search;
        private static AddRequest add;
        private static double startedAt;
        private static bool writeTimeoutReported;
#endif
        static UnityCliPipelineInstaller()
        {
#if UNITY_6000_0_OR_NEWER
            EditorApplication.delayCall += Recover;
#endif
        }

        [MenuItem(MenuPath)]
        private static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                Report(AIBridgeEditorText.T("Exit Play Mode and wait for compilation before installing dependencies.", "请退出 Play Mode 并等待编译结束后安装依赖。"));
                return;
            }
            Report(AIBridgeEditorText.T(
                "This menu installs the optional Pipeline project package, not the standalone Unity CLI. Progress is shown in the Console.",
                "此菜单安装可选 Pipeline 项目包，独立 Unity CLI 需另行安装。进度显示在 Console 中。"));
            if (!UnityCliPipelineInstallerRules.SupportsUnity(Application.unityVersion))
            {
                Report(AIBridgeEditorText.T("Unity CLI dependencies require Unity 6000 or newer.", "Unity CLI 依赖要求 Unity 6000 或更高版本。"), true);
                return;
            }
#if UNITY_6000_0_OR_NEWER
            var pending = SessionState.GetString(PendingKey, string.Empty);
            if (!UnityCliPipelineInstallerRules.CanStart(phase != Phase.Idle, pending))
            {
                if (phase == Phase.Idle) Recover();
                else Report(AIBridgeEditorText.T("A dependency operation is already running. See the Console for progress.", "依赖操作正在进行，请查看 Console 进度。"));
                return;
            }
            Report(AIBridgeEditorText.T("Checking project dependencies for com.unity.pipeline...", "正在检查项目依赖 com.unity.pipeline……"));
            BeginList(false);
#endif
        }

#if UNITY_6000_0_OR_NEWER
        private static void Recover()
        {
            if (phase != Phase.Idle || string.IsNullOrEmpty(SessionState.GetString(PendingKey, string.Empty))) return;
            Report(AIBridgeEditorText.T("Recovering dependency verification after an interrupted operation. No Add request will be replayed.", "正在恢复中断操作的依赖核验，不会重放 Add 请求。"));
            BeginList(true);
        }

        private static void BeginList(bool verify)
        {
            Begin(verify ? Phase.Verifying : Phase.Listing);
            try { list = Client.List(true, true); }
            catch (Exception exception) { QueryFailed(exception.Message); }
        }

        private static void Begin(Phase next)
        {
            phase = next;
            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            try
            {
                if (phase == Phase.Adding)
                {
                    if (add != null && add.IsCompleted)
                    {
                        if (add.Status != StatusCode.Success)
                            Report(AIBridgeEditorText.T("UPM Add failed: ", "UPM Add 失败：") + ErrorMessage(add), true);
                        // Add 的返回结果不作为成功依据；必须重新列出项目中的准确版本。
                        BeginList(true);
                    }
                    else if (!writeTimeoutReported && EditorApplication.timeSinceStartup - startedAt >= WriteWarningSeconds)
                    {
                        writeTimeoutReported = true;
                        Report(UnknownResult() + AIBridgeEditorText.T(" Still waiting for the active Add request; new writes remain blocked.", " 仍在等待当前 Add 请求，新写入保持阻止。"), true);
                    }
                    return;
                }

                if ((phase == Phase.Listing || phase == Phase.Verifying) && list != null && list.IsCompleted)
                {
                    if (list.Status != StatusCode.Success) { QueryFailed(ErrorMessage(list)); return; }
                    if (phase == Phase.Verifying) { Verify(); return; }
                    foreach (var package in list.Result)
                    {
                        if (!UnityCliPipelineInstallerRules.IsInstalled(package.name)) continue;
                        Stop();
                        Report(AIBridgeEditorText.T("Already installed; keeping existing version: ", "已安装，保留现有版本：") + package.name + "@" + package.version);
                        return;
                    }
                    Begin(Phase.Searching);
                    search = Client.Search(UnityCliPipelineInstallerRules.PackageName, false);
                    return;
                }

                if (phase == Phase.Searching && search != null && search.IsCompleted)
                {
                    if (search.Status != StatusCode.Success) { QueryFailed(ErrorMessage(search)); return; }
                    string version = null;
                    foreach (var package in search.Result)
                    {
                        version = UnityCliPipelineInstallerRules.SelectVersion(package.name, package.versions.latestCompatible, package.versions.compatible);
                        if (version != null) break;
                    }
                    if (version == null)
                    {
                        QueryFailed(AIBridgeEditorText.T("The registry did not return a latestCompatible version included in compatible for com.unity.pipeline.", "注册表未返回 com.unity.pipeline 的有效兼容版本（latestCompatible 必须包含在 compatible 中）。"));
                        return;
                    }
                    // 先持久化目标再写入，域重载只能恢复核验，不能再次 Add。
                    SessionState.SetString(PendingKey, version);
                    Begin(Phase.Adding);
                    writeTimeoutReported = false;
                    Report(AIBridgeEditorText.T("Installing project dependency: ", "正在安装项目依赖：") + UnityCliPipelineInstallerRules.PackageName + "@" + version);
                    add = Client.Add(UnityCliPipelineInstallerRules.PackageName + "@" + version);
                    return;
                }

                if (EditorApplication.timeSinceStartup - startedAt >= QueryTimeoutSeconds)
                    QueryFailed(AIBridgeEditorText.T("UPM query timed out.", "UPM 查询超时。"));
            }
            catch (Exception exception)
            {
                // 活跃写句柄仍须继续观察；异常与超时都不能当成取消或释放写锁的依据。
                if (phase == Phase.Adding && add != null)
                {
                    if (!writeTimeoutReported)
                    {
                        writeTimeoutReported = true;
                        Report(UnknownResult() + "\n" + exception.Message, true);
                    }
                    return;
                }
                QueryFailed(exception.Message);
            }
        }

        private static void Verify()
        {
            var target = SessionState.GetString(PendingKey, string.Empty);
            var installed = string.Empty;
            foreach (var package in list.Result)
            {
                if (!UnityCliPipelineInstallerRules.IsInstalled(package.name)) continue;
                installed = package.version;
                if (!UnityCliPipelineInstallerRules.MatchesTarget(package.name, installed, target)) break;
                SessionState.EraseString(PendingKey);
                Stop();
                Report(AIBridgeEditorText.T("Verified installed dependency: ", "已确认依赖安装成功：") + package.name + "@" + installed);
                return;
            }
            // 丢失 Add 句柄时，List 不匹配不能证明后台写入已结束，保留锁供后续只读复查。
            var knownFailure = add != null && add.IsCompleted && add.Status == StatusCode.Failure;
            if (knownFailure) SessionState.EraseString(PendingKey);
            Stop();
            Report((knownFailure
                ? AIBridgeEditorText.T("Installation failed. You may retry the menu after fixing the UPM error.", "安装失败。修复 UPM 错误后可重新点击菜单。")
                : UnknownResult()) + AIBridgeEditorText.T(" Expected: ", " 目标版本：") + target +
                AIBridgeEditorText.T("; observed: ", "；检测版本：") +
                (string.IsNullOrEmpty(installed) ? AIBridgeEditorText.T("not installed", "未安装") : installed), true);
        }

        private static void QueryFailed(string detail)
        {
            var pending = !string.IsNullOrEmpty(SessionState.GetString(PendingKey, string.Empty));
            Stop();
            Report(AIBridgeEditorText.T("Dependency query failed: ", "依赖查询失败：") + detail + (pending ? "\n" + UnknownResult() : string.Empty), true);
        }

        private static string UnknownResult()
        {
            return AIBridgeEditorText.T(
                "Installation result is unknown. Pending state is retained to prevent duplicate writes. Use this menu again to verify only; if unresolved, check Package Manager and restart the Editor only after confirming UPM is idle.",
                "安装结果未知。已保留待核验状态以防止重复写入。再次点击此菜单只会复查；若仍未解决，请检查 Package Manager，确认 UPM 已空闲后再重启编辑器。");
        }

        private static string ErrorMessage(Request request)
        {
            return request.Error != null ? request.Error.message : AIBridgeEditorText.T("UPM returned no error details.", "UPM 未返回错误详情。");
        }

        private static void Stop()
        {
            EditorApplication.update -= Tick;
            phase = Phase.Idle;
            list = null;
            search = null;
            add = null;
        }
#endif
        private static void Report(string message, bool error = false)
        {
            var text = "[AIBridge] " + message;
            if (error) Debug.LogError(text);
            else Debug.Log(text);
        }
    }
}
