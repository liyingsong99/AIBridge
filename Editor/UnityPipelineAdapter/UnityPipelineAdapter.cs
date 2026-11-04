using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace AIBridge.Editor.UnityPipelineAdapter
{
    /// <summary>
    /// Unity Pipeline 的薄适配层。核心 AIBridge 不引用本程序集，未安装 Pipeline 时不会参与编译。
    /// </summary>
    public static class UnityPipelineAdapter
    {
        private const string PipelinePackageName = "com.unity.pipeline";
        private const string PackageManifestFileName = "package.json";
        private const string PipelineDescriptorRelativePath = "Library/Pipeline/.unity-pipeline-port";

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
            status.serverReady = TryReadPortDescriptor(status.descriptorPath);
            if (!status.editorSupported)
            {
                status.errorCode = "editorVersionUnsupported";
                status.message = "当前 Unity Editor 版本不满足适配层要求。";
            }
            else if (!status.serverReady)
            {
                status.errorCode = "pipelineServerNotDetected";
                status.message = "已检测到 Unity Pipeline 包，但未检测到运行中的 Pipeline 服务。";
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
            var packageRoots = GetPackageRoots();
            foreach (var packageRoot in packageRoots)
            {
                var directPath = Path.Combine(packageRoot, PipelinePackageName);
                var directManifest = Path.Combine(directPath, PackageManifestFileName);
                if (File.Exists(directManifest))
                {
                    return new PipelinePackage(ReadPackageVersion(directManifest));
                }

                if (!Directory.Exists(packageRoot))
                {
                    continue;
                }

                string[] candidates;
                try
                {
                    candidates = Directory.GetDirectories(packageRoot, PipelinePackageName + "@*");
                }
                catch
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    var manifestPath = Path.Combine(candidate, PackageManifestFileName);
                    if (File.Exists(manifestPath))
                    {
                        return new PipelinePackage(ReadPackageVersion(manifestPath));
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> GetPackageRoots()
        {
            var roots = new List<string>();
            var projectRoot = Directory.GetParent(Application.dataPath);
            if (projectRoot != null)
            {
                roots.Add(Path.Combine(projectRoot.FullName, "Packages"));
                roots.Add(Path.Combine(projectRoot.FullName, "Library", "PackageCache"));
            }

            return roots;
        }

        private static string ReadPackageVersion(string manifestPath)
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var marker = "\"version\"";
                var markerIndex = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    return "unknown";
                }

                var colonIndex = json.IndexOf(':', markerIndex + marker.Length);
                if (colonIndex < 0)
                {
                    return "unknown";
                }

                var quoteStart = json.IndexOf('\"', colonIndex + 1);
                var quoteEnd = json.IndexOf('\"', quoteStart + 1);
                return quoteStart >= 0 && quoteEnd > quoteStart
                    ? json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1)
                    : "unknown";
            }
            catch
            {
                return "unknown";
            }
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

        private static bool TryReadPortDescriptor(string descriptorPath)
        {
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

                // Pipeline 0.10 的凭据字段为 evalToken；不向调用方返回凭据或原始 descriptor。
                // 此处只确认本地服务描述文件，实际连通性仍须通过官方 CLI 验证。
                var descriptor = JsonUtility.FromJson<PipelineServerDescriptor>(json);
                return descriptor != null && descriptor.port > 0 && descriptor.port <= 65535
                    && !string.IsNullOrEmpty(descriptor.evalToken);
            }
            catch
            {
                return false;
            }
        }

        [Serializable]
        private sealed class PipelineServerDescriptor
        {
            public int port;
            public string evalToken;
        }

        private sealed class PipelinePackage
        {
            public readonly string Version;

            public PipelinePackage(string version)
            {
                Version = version;
            }
        }
    }
}
