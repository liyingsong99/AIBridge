using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor.PackageManager;
using UnityEngine;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    /// <summary>
    /// Unity Pipeline 的薄适配层。核心 AIBridge 不引用本程序集，未安装 Pipeline 时不会参与编译。
    /// </summary>
    public static partial class UnityPipelineAdapter
    {
        private const string PipelinePackageName = "com.unity.pipeline";
        private const string PipelineDescriptorRelativePath = "Library/Pipeline/.unity-pipeline-port";
        private const int PipelineStatusProbeTimeoutMs = 1500;
        private const int PipelineStatusCacheMs = 3000;
        private static readonly object ProbeLock = new object();
        private static string s_ProbeKey;
        private static int s_ProbeGeneration;
        private static bool s_ProbeRunning;
        private static bool s_ProbeCompleted;
        private static bool s_ProbeReady;
        private static string s_ProbeErrorCode;
        private static string s_ProbeResponseBody;
        private static bool s_ProbeResponseValidated;
        private static DateTime s_ProbeCompletedUtc;

        public static bool IsCompiledIn
        {
            get
            {
#if AIBRIDGE_HAS_UNITY_PIPELINE
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsAvailable
        {
            get { return TryGetStatus(out var status) && status.IsAvailable; }
        }

        public static bool TryGetStatus(out UnityPipelineCapability status)
        {
            status = BuildStatus();
            return status != null;
        }

        public static UnityPipelineCapability GetStatus()
        {
            return BuildStatus();
        }

        /// <summary>
        /// 读取适配层约定的命令 schema。Schema 文件由 Pipeline 扩展包自行提供，适配层只负责读取 JSON 文本。
        /// </summary>
        public static bool TryReadCommandSchema(string path, out string schemaJson, out string error)
        {
            schemaJson = null;
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "descriptorPathEmpty";
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                {
                    error = "schemaNotFound";
                    return false;
                }

                schemaJson = File.ReadAllText(fullPath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static UnityPipelineCapability BuildStatus()
        {
            var status = new UnityPipelineCapability
            {
                compiled = IsCompiledIn,
                editorSupported = IsEditorSupported(),
                descriptorMatchesCurrentEditor = false,
                serverReady = false,
                errorCode = null,
                message = null
            };

            var package = FindPipelinePackage();
            if (package == null)
            {
                status.errorCode = "pipelinePackageMissing";
                status.message = "未检测到 Unity Pipeline 包。";
                return status;
            }

            status.packageVersion = package.Version;
            status.descriptorPath = GetPipelineDescriptorPath();
            var probeError = "pipelineDescriptorMismatch";
            PipelineServerDescriptor descriptor;
            status.descriptorMatchesCurrentEditor = TryReadPortDescriptor(status.descriptorPath, out descriptor);
            status.serverReady = status.descriptorMatchesCurrentEditor && TryGetProbeResult(descriptor, out probeError);
            if (!status.serverReady && string.IsNullOrEmpty(probeError))
            {
                probeError = "pipelineConnectivityNotTested";
            }
            if (!status.editorSupported)
            {
                status.errorCode = "editorVersionUnsupported";
                status.message = "当前 Unity Editor 版本不满足适配层要求。";
            }
            else if (!status.descriptorMatchesCurrentEditor)
            {
                status.errorCode = "pipelineDescriptorMismatch";
                status.message = "已检测到 Unity Pipeline 包，但 descriptor 未匹配当前 Editor。";
            }
            else if (!status.serverReady)
            {
                status.errorCode = probeError;
                status.message = "Pipeline descriptor 与当前 Editor 匹配，但 bearer-auth HTTP 状态探测未确认服务就绪。";
            }
            else
            {
                status.message = "Unity Pipeline 适配层已就绪。";
            }

            return status;
        }

        private static bool IsEditorSupported()
        {
            // Pipeline 适配层要求 Unity 6（6000.x）；只解析版本字符串，不引用高版本 API。
            try
            {
                var version = Application.unityVersion;
                if (string.IsNullOrEmpty(version))
                {
                    return false;
                }

                var separator = version.IndexOf('.', StringComparison.Ordinal);
                var majorText = separator >= 0 ? version.Substring(0, separator) : version;
                int major;
                return int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out major)
                    && major >= 6000;
            }
            catch
            {
                return false;
            }
        }

        private static PipelinePackage FindPipelinePackage()
        {
            // 仅使用当前 UPM 注册的解析结果；PackageCache 中的残留目录不得授权协议请求。
            var packages = PackageInfo.GetAllRegisteredPackages();
            if (packages == null) return null;
            foreach (var package in packages)
            {
                if (package != null && string.Equals(package.name, PipelinePackageName, StringComparison.Ordinal))
                    return new PipelinePackage(package.version, package.resolvedPath);
            }
            return null;
        }

        private static string GetPipelineDescriptorPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot == null)
            {
                return null;
            }

            // 返回约定路径本身，调用方可区分“路径已知但服务未启动”和“项目路径无法解析”。
            return Path.Combine(projectRoot.FullName, PipelineDescriptorRelativePath);
        }

        private static bool TryReadPortDescriptor(string descriptorPath, out PipelineServerDescriptor descriptor)
        {
            descriptor = null;
            if (string.IsNullOrEmpty(descriptorPath))
            {
                return false;
            }

            try
            {
                var json = File.ReadAllText(descriptorPath);
                if (string.IsNullOrEmpty(json))
                {
                    return false;
                }

                // 凭据只用于 loopback 状态探测，不向调用方返回或写入日志。
                descriptor = JsonUtility.FromJson<PipelineServerDescriptor>(json);
                var projectRoot = Directory.GetParent(Application.dataPath);
                return descriptor != null && descriptor.port > 0 && descriptor.port <= 65535
                    && descriptor.pid == Process.GetCurrentProcess().Id
                    && projectRoot != null
                    && IsSamePath(descriptor.projectPath, projectRoot.FullName)
                    && string.Equals(descriptor.unityVersion, Application.unityVersion, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(descriptor.evalToken);
            }
            catch
            {
                descriptor = null;
                return false;
            }
        }

        private static string GetProbeKey(PipelineServerDescriptor descriptor)
        {
            // 仅内存比较，禁止输出；token 轮换或服务重启必须使旧就绪结果失效。
            return descriptor.port.ToString(CultureInfo.InvariantCulture) + "|" + descriptor.pid + "|" + descriptor.projectPath
                + "|" + descriptor.unityVersion + "|" + descriptor.startedAt + "|" + descriptor.evalToken;
        }

        private static bool TryGetProbeResult(PipelineServerDescriptor descriptor, out string errorCode)
        {
            errorCode = null;
            var key = GetProbeKey(descriptor);
            lock (ProbeLock)
            {
                if (!string.Equals(s_ProbeKey, key, StringComparison.Ordinal))
                {
                    s_ProbeKey = key;
                    s_ProbeGeneration++;
                    s_ProbeRunning = false;
                    s_ProbeCompleted = false;
                    s_ProbeReady = false;
                    s_ProbeErrorCode = null;
                    s_ProbeResponseBody = null;
                    s_ProbeResponseValidated = false;
                }

                if (s_ProbeCompleted)
                {
                    if (!s_ProbeResponseValidated)
                    {
                        if (s_ProbeReady)
                        {
                            try
                            {
                                var response = JsonUtility.FromJson<PipelineStatusResponse>(s_ProbeResponseBody);
                                s_ProbeReady = response != null
                                    && string.Equals(response.status, "ready", StringComparison.OrdinalIgnoreCase);
                                s_ProbeErrorCode = s_ProbeReady ? null : "pipelineStatusMismatch";
                            }
                            catch
                            {
                                s_ProbeReady = false;
                                s_ProbeErrorCode = "pipelineStatusInvalid";
                            }
                        }

                        s_ProbeResponseValidated = true;
                    }

                    if ((DateTime.UtcNow - s_ProbeCompletedUtc).TotalMilliseconds < PipelineStatusCacheMs)
                    {
                        errorCode = s_ProbeErrorCode;
                        return s_ProbeReady;
                    }

                    s_ProbeCompleted = false;
                }

                if (!s_ProbeRunning)
                {
                    s_ProbeRunning = true;
                    var generation = s_ProbeGeneration;
                    ThreadPool.QueueUserWorkItem(_ => ProbePipelineStatus(descriptor, key, generation));
                }

                errorCode = "pipelineConnectivityProbePending";
                return false;
            }
        }

        private static void ProbePipelineStatus(PipelineServerDescriptor descriptor, string key, int generation)
        {
            var ready = false;
            var errorCode = "pipelineConnectivityFailed";
            string responseBody = null;
            try
            {
                var endpoint = "http://127.0.0.1:" + descriptor.port.ToString(CultureInfo.InvariantCulture) + "/api/status";
                var request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "GET";
                request.Timeout = PipelineStatusProbeTimeoutMs;
                request.ReadWriteTimeout = PipelineStatusProbeTimeoutMs;
                request.Proxy = null;
                request.AllowAutoRedirect = false;
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + descriptor.evalToken;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseBody = reader.ReadToEnd();
                    ready = response.StatusCode == HttpStatusCode.OK;
                    errorCode = ready ? null : "pipelineStatusMismatch";
                }
            }
            catch (WebException exception)
            {
                var response = exception.Response as HttpWebResponse;
                errorCode = response != null && response.StatusCode == HttpStatusCode.Unauthorized
                    ? "pipelineAuthenticationFailed"
                    : "pipelineConnectivityFailed";
                if (response != null) response.Dispose();
            }
            catch
            {
                errorCode = "pipelineStatusInvalid";
            }

            PublishProbeResult(key, generation, ready, errorCode, responseBody);
        }

        private static void PublishProbeResult(string key, int generation, bool ready, string errorCode, string responseBody)
        {
            lock (ProbeLock)
            {
                if (s_ProbeGeneration == generation && string.Equals(s_ProbeKey, key, StringComparison.Ordinal))
                {
                    s_ProbeReady = ready;
                    s_ProbeErrorCode = errorCode;
                    s_ProbeResponseBody = responseBody;
                    s_ProbeResponseValidated = false;
                    s_ProbeCompletedUtc = DateTime.UtcNow;
                    s_ProbeCompleted = true;
                    s_ProbeRunning = false;
                }
            }
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison);
            }
            catch
            {
                return false;
            }
        }

        [Serializable]
        private sealed class PipelineStatusResponse
        {
            public string status;
        }

        [Serializable]
        private sealed class PipelineServerDescriptor
        {
            public int pid;
            public int port;
            public string projectPath;
            public string unityVersion;
            public string startedAt;
            public string evalToken;
        }

        private sealed class PipelinePackage
        {
            public readonly string Version;

            public readonly string ResolvedPath;

            public PipelinePackage(string version, string resolvedPath)
            {
                Version = version;
                ResolvedPath = resolvedPath;
            }
        }
    }
}
