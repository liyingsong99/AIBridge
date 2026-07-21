using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AIBridge.Runtime.Internal
{
    /// <summary>
    /// Editor 实例心跳路径约定：高频状态写在项目外用户状态目录，避免触发 Git 工作区监听。
    /// Editor 与 CLI 共用同一套解析规则，保证 projectRoot → 路径一致。
    /// </summary>
    public static class AIBridgeEditorInstancePaths
    {
        public const string StateDirEnvironment = "AIBRIDGE_STATE_DIR";
        public const string MetadataFileName = "editor-instance.json";
        public const string ProductDirectoryName = "AIBridge";
        public const string InstancesDirectoryName = "instances";
        private const int ProjectHashHexLength = 12;

        public static string GetStateRoot()
        {
            var envPath = Environment.GetEnvironmentVariable(StateDirEnvironment);
            if (!string.IsNullOrWhiteSpace(envPath))
            {
                return Path.GetFullPath(envPath.Trim());
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? string.Empty,
                    ".local",
                    "share");
            }

            return Path.Combine(localAppData, ProductDirectoryName);
        }

        public static string BuildProjectKey(string projectRoot)
        {
            var normalized = NormalizeProjectRoot(projectRoot);
            if (string.IsNullOrEmpty(normalized))
            {
                return "unknown";
            }

            var projectName = SanitizePathSegment(Path.GetFileName(normalized));
            if (string.IsNullOrEmpty(projectName))
            {
                projectName = "project";
            }

            return projectName + "_" + ComputeStableHashHex(normalized, ProjectHashHexLength);
        }

        public static string GetInstanceDirectory(string projectRoot)
        {
            return Path.Combine(GetStateRoot(), InstancesDirectoryName, BuildProjectKey(projectRoot));
        }

        public static string GetMetadataPath(string projectRoot)
        {
            return Path.Combine(GetInstanceDirectory(projectRoot), MetadataFileName);
        }

        public static string GetLegacyMetadataPath(string projectRoot)
        {
            var normalized = NormalizeProjectRoot(projectRoot);
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }

            return Path.Combine(normalized, ".aibridge", MetadataFileName);
        }

        public static string GetLegacyMetadataPathFromBridgeDirectory(string bridgeDirectory)
        {
            if (string.IsNullOrWhiteSpace(bridgeDirectory))
            {
                return null;
            }

            return Path.Combine(Path.GetFullPath(bridgeDirectory), MetadataFileName);
        }

        /// <summary>
        /// CLI 读取顺序：外部状态目录优先，再回退项目内遗留路径。
        /// </summary>
        public static IList<string> GetMetadataCandidatePaths(string projectRoot)
        {
            var paths = new List<string>(2);
            var external = GetMetadataPath(projectRoot);
            if (!string.IsNullOrEmpty(external))
            {
                paths.Add(external);
            }

            var legacy = GetLegacyMetadataPath(projectRoot);
            if (!string.IsNullOrEmpty(legacy)
                && !paths.Exists(path => PathsEqual(path, legacy)))
            {
                paths.Add(legacy);
            }

            return paths;
        }

        public static string NormalizeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return null;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(projectRoot.Trim());
            }
            catch
            {
                return null;
            }

            fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsWindows())
            {
                // Windows 路径大小写不敏感；统一小写保证 Editor/CLI 哈希一致。
                fullPath = fullPath.ToLowerInvariant();
            }

            return fullPath;
        }

        private static string SanitizePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Trim().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0
                    || chars[i] == Path.DirectorySeparatorChar
                    || chars[i] == Path.AltDirectorySeparatorChar)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string ComputeStableHashHex(string value, int hexLength)
        {
            var byteCount = Math.Max(1, (hexLength + 1) / 2);
            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(hexLength);
                for (var i = 0; i < byteCount && i < hash.Length; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                if (builder.Length > hexLength)
                {
                    return builder.ToString(0, hexLength);
                }

                return builder.ToString();
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        private static bool IsWindows()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                || Environment.OSVersion.Platform == PlatformID.Win32Windows
                || Environment.OSVersion.Platform == PlatformID.Win32S
                || Environment.OSVersion.Platform == PlatformID.WinCE;
        }
    }
}
