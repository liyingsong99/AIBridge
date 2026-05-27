using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIBridge.Runtime
{
    /// <summary>
    /// Automatically creates AIBridgeRuntime for built Players when the project opts in.
    /// </summary>
    public static class AIBridgeRuntimeBootstrap
    {
        private const string BootstrapObjectName = "AIBridgeRuntime (Bootstrap)";
        private const string TransportName = "http";
        private const string DefaultHttpBindAddress = "0.0.0.0";
        private const int DefaultHttpPort = 27182;
        private const int DefaultDiscoveryUdpPort = 27183;

        private static bool _initialized;
        private static bool _releaseBuildAllowed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeSceneLoad()
        {
#if UNITY_EDITOR
            return;
#else
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            if (!ShouldAutoInject(out _releaseBuildAllowed))
            {
                return;
            }

            // BeforeSceneLoad 阶段还看不到首场景内的组件，等 sceneLoaded 后再判重创建。
            SceneManager.sceneLoaded += HandleFirstSceneLoaded;
#endif
        }

#if !UNITY_EDITOR
        private static void HandleFirstSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= HandleFirstSceneLoaded;
            CreateRuntimeIfNeeded(scene.name);
        }

        private static bool ShouldAutoInject(out bool releaseBuildAllowed)
        {
            releaseBuildAllowed = false;

#if AIBRIDGE_RUNTIME_AUTO_INJECT_DISABLED
            Debug.Log("[AIBridgeRuntimeBootstrap] Runtime Bridge auto injection is disabled. / Runtime Bridge 自动注入已关闭。");
            return false;
#else
            if (Debug.isDebugBuild)
            {
                Debug.Log("[AIBridgeRuntimeBootstrap] Development Build auto injection is enabled. / Development Build 自动注入已启用。");
                return true;
            }

#if AIBRIDGE_RUNTIME_ALLOW_RELEASE_BUILD
            releaseBuildAllowed = true;
            Debug.LogWarning("[AIBridgeRuntimeBootstrap] Release Build Runtime Bridge was explicitly allowed. Restrict token and allowed actions before shipping. / Release Build Runtime Bridge 已显式允许，发布前请限制 Token 和 action 白名单。");
            return true;
#else
            Debug.Log("[AIBridgeRuntimeBootstrap] Release Build Runtime Bridge is disabled by default. / Release Build Runtime Bridge 默认关闭。");
            return false;
#endif
#endif
        }

        private static void CreateRuntimeIfNeeded(string sceneName)
        {
            var existingRuntime = FindExistingRuntime();
            if (existingRuntime != null)
            {
                Debug.Log("[AIBridgeRuntimeBootstrap] Existing AIBridgeRuntime found in scene '" + sceneName + "'; bootstrap creation skipped. / 场景 '" + sceneName + "' 已存在 AIBridgeRuntime，跳过自动创建。");
                return;
            }

            var gameObject = new GameObject(BootstrapObjectName);
            gameObject.SetActive(false);
            gameObject.hideFlags = HideFlags.HideInHierarchy;

            var runtime = gameObject.AddComponent<AIBridgeRuntime>();
            if (runtime.runtimeSettings == null)
            {
                runtime.runtimeSettings = new AIBridgeRuntimeSettings();
            }

            runtime.runtimeSettings.enableRuntimeBridge = true;
            runtime.runtimeSettings.allowInReleaseBuild = _releaseBuildAllowed;
            runtime.runtimeSettings.enableHttpTransport = true;
            runtime.runtimeSettings.httpBindAddress = DefaultHttpBindAddress;
            runtime.runtimeSettings.httpPort = DefaultHttpPort;
            runtime.runtimeSettings.enableLanDiscovery = true;
            runtime.runtimeSettings.discoveryUdpPort = DefaultDiscoveryUdpPort;
            gameObject.SetActive(true);

            Debug.Log("[AIBridgeRuntimeBootstrap] AIBridgeRuntime created by bootstrap. transport=" + TransportName + ". Default HTTP port=" + DefaultHttpPort + " (auto-increments if occupied). LAN: AIBridgeCLI runtime discover --udpPort " + DefaultDiscoveryUdpPort + " / 已通过 bootstrap 创建 AIBridgeRuntime。");
        }

        private static AIBridgeRuntime FindExistingRuntime()
        {
            if (AIBridgeRuntime.Instance != null)
            {
                return AIBridgeRuntime.Instance;
            }

            var runtimes = Resources.FindObjectsOfTypeAll<AIBridgeRuntime>();
            for (var i = 0; i < runtimes.Length; i++)
            {
                var runtime = runtimes[i];
                if (runtime != null
                    && runtime.gameObject != null
                    && runtime.gameObject.scene.IsValid())
                {
                    return runtime;
                }
            }

            return null;
        }
#endif
    }
}
