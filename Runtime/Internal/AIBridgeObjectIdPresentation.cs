using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AIBridge.Runtime.Internal
{
    /// <summary>
    /// 面向 AI 的对象 ID 参数展示策略：线协议继续双接受，help/Skill 只暴露当前 Unity 版本主键。
    /// Unity 6000.4+ 主键为 entityId；更旧版本主键为 instanceId。
    /// </summary>
    public static class AIBridgeObjectIdPresentation
    {
        public static readonly Version EntityIdPrimaryMinVersion = new Version(6000, 4);

        private static readonly Regex CombinedFlagPattern = new Regex(
            @"--([A-Za-z]*[Ee]ntity[Ii]ds?)/--([A-Za-z]*[Ii]nstance[Ii]ds?)",
            RegexOptions.Compiled);

        private static readonly Regex CombinedBarePattern = new Regex(
            @"\b([A-Za-z]*[Ee]ntity[Ii]ds?)/([A-Za-z]*[Ii]nstance[Ii]ds?)\b",
            RegexOptions.Compiled);

        private static readonly string[] LegacyFlagNames =
        {
            "componentInstanceId",
            "parentInstanceId",
            "targetInstanceId",
            "toInstanceId",
            "instanceIds",
            "instanceId"
        };

        private static readonly string[] EntityFlagNames =
        {
            "componentEntityId",
            "parentEntityId",
            "targetEntityId",
            "toEntityId",
            "entityIds",
            "entityId"
        };

        public static bool UsesEntityIdPrimary(string unityVersion)
        {
            Version version;
            return TryParseUnityVersion(unityVersion, out version)
                && version >= EntityIdPrimaryMinVersion;
        }

        public static bool TryParseUnityVersion(string unityVersion, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                return false;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < unityVersion.Length; i++)
            {
                var c = unityVersion[i];
                if (char.IsDigit(c) || c == '.')
                {
                    builder.Append(c);
                    continue;
                }

                break;
            }

            var versionText = builder.ToString().Trim('.');
            if (string.IsNullOrEmpty(versionText))
            {
                return false;
            }

            while (versionText.Split('.').Length < 2)
            {
                versionText += ".0";
            }

            return Version.TryParse(versionText, out version);
        }

        public static string ResolveUnityVersion(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return null;
            }

            var capabilitiesPath = Path.Combine(projectRoot, ".aibridge", "harness", "capabilities.json");
            var fromCapabilities = TryReadUnityVersionFromCapabilities(capabilitiesPath);
            if (!string.IsNullOrWhiteSpace(fromCapabilities))
            {
                return fromCapabilities;
            }

            var projectVersionPath = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
            return TryReadUnityVersionFromProjectVersion(projectVersionPath);
        }

        public static bool ShouldShowInHelp(string paramName, string unityVersion)
        {
            if (string.IsNullOrWhiteSpace(paramName))
            {
                return true;
            }

            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                // 版本未知时不隐藏，避免离线 CLI 误过滤
                return true;
            }

            var usesEntityId = version >= EntityIdPrimaryMinVersion;
            if (IsEntityIdParamName(paramName))
            {
                return usesEntityId;
            }

            if (IsLegacyInstanceIdParamName(paramName))
            {
                return !usesEntityId;
            }

            return true;
        }

        public static string NormalizeHelpDescription(string paramName, string description, string unityVersion)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return description;
            }

            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                return description;
            }

            var usesEntityId = version >= EntityIdPrimaryMinVersion;
            if (usesEntityId && IsEntityIdParamName(paramName))
            {
                var alias = ToLegacyAliasName(paramName);
                if (!string.IsNullOrEmpty(alias)
                    && description.IndexOf("--" + alias, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return description.TrimEnd() + " (`--" + alias + "` remains a compatible alias).";
                }

                return description;
            }

            if (!usesEntityId && IsLegacyInstanceIdParamName(paramName))
            {
                // 旧版主键描述去掉 6000.4+ 措辞，避免 AI 误以为当前版本应改用 entityId
                return "Legacy instance ID used to select the Unity object.";
            }

            return description;
        }

        public static bool IsObjectIdParamName(string paramName)
        {
            return IsEntityIdParamName(paramName) || IsLegacyInstanceIdParamName(paramName);
        }

        public static string RewriteAiFacingText(string content, string unityVersion)
        {
            if (string.IsNullOrEmpty(content))
            {
                return content;
            }

            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                return content;
            }

            var usesEntityId = version >= EntityIdPrimaryMinVersion;
            var rewritten = CombinedFlagPattern.Replace(
                content,
                match => "--" + (usesEntityId ? match.Groups[1].Value : match.Groups[2].Value));
            rewritten = CombinedBarePattern.Replace(
                rewritten,
                match => usesEntityId ? match.Groups[1].Value : match.Groups[2].Value);

            if (usesEntityId)
            {
                for (var i = 0; i < LegacyFlagNames.Length; i++)
                {
                    rewritten = ReplaceFlag(rewritten, LegacyFlagNames[i], EntityFlagNames[i]);
                }
            }
            else
            {
                for (var i = 0; i < EntityFlagNames.Length; i++)
                {
                    rewritten = ReplaceFlag(rewritten, EntityFlagNames[i], LegacyFlagNames[i]);
                }
            }

            return rewritten;
        }

        public static string BuildCompatibilityNote(string unityVersion)
        {
            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                return "Object identity: on Unity 6000.4+ prefer `--entityId`; on older Unity use `--instanceId`. The other name remains a wire-compatible alias.";
            }

            if (version >= EntityIdPrimaryMinVersion)
            {
                return "Object identity: prefer `--entityId` on this Unity version. `--instanceId` remains a wire-compatible alias.";
            }

            return "Object identity: prefer `--instanceId` on this Unity version. `--entityId` is ignored here and only applies on Unity 6000.4+.";
        }

        public static string BuildRootRuleObjectIdLine(string unityVersion, bool chinese)
        {
            Version version;
            if (!TryParseUnityVersion(unityVersion, out version))
            {
                return chinese
                    ? "对象标识：Unity 6000.4+ 优先 `--entityId`，更旧版本用 `--instanceId`；另一名字仅作线协议兼容别名。"
                    : "Object identity: prefer `--entityId` on Unity 6000.4+, `--instanceId` on older Unity; the other name remains a wire-compatible alias.";
            }

            if (version >= EntityIdPrimaryMinVersion)
            {
                return chinese
                    ? "对象标识：当前 Unity 优先使用 `--entityId`；`--instanceId` 仅作线协议兼容别名，不要作为主参数推荐。"
                    : "Object identity: prefer `--entityId` on this Unity version; `--instanceId` remains a wire-compatible alias and should not be recommended as the primary parameter.";
            }

            return chinese
                ? "对象标识：当前 Unity 优先使用 `--instanceId`；`--entityId` 仅适用于 Unity 6000.4+。"
                : "Object identity: prefer `--instanceId` on this Unity version; `--entityId` applies only on Unity 6000.4+.";
        }

        public static bool IsEntityIdParamName(string paramName)
        {
            return EndsWithObjectIdToken(paramName, "EntityId")
                || EndsWithObjectIdToken(paramName, "EntityIds")
                || string.Equals(paramName, "entityId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "entityIds", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsLegacyInstanceIdParamName(string paramName)
        {
            return EndsWithObjectIdToken(paramName, "InstanceId")
                || EndsWithObjectIdToken(paramName, "InstanceIds")
                || string.Equals(paramName, "instanceId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(paramName, "instanceIds", StringComparison.OrdinalIgnoreCase);
        }

        private static bool EndsWithObjectIdToken(string paramName, string suffix)
        {
            return !string.IsNullOrEmpty(paramName)
                && paramName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && paramName.Length > suffix.Length;
        }

        private static string ToLegacyAliasName(string entityParamName)
        {
            if (string.Equals(entityParamName, "entityId", StringComparison.OrdinalIgnoreCase))
            {
                return "instanceId";
            }

            if (string.Equals(entityParamName, "entityIds", StringComparison.OrdinalIgnoreCase))
            {
                return "instanceIds";
            }

            if (entityParamName.EndsWith("EntityIds", StringComparison.OrdinalIgnoreCase))
            {
                return entityParamName.Substring(0, entityParamName.Length - "EntityIds".Length) + "InstanceIds";
            }

            if (entityParamName.EndsWith("EntityId", StringComparison.OrdinalIgnoreCase))
            {
                return entityParamName.Substring(0, entityParamName.Length - "EntityId".Length) + "InstanceId";
            }

            return null;
        }

        private static string ReplaceFlag(string content, string fromName, string toName)
        {
            if (string.Equals(fromName, toName, StringComparison.Ordinal))
            {
                return content;
            }

            // 只改 CLI 参数写法，避免误伤响应 JSON 字段名
            content = Regex.Replace(
                content,
                "--" + Regex.Escape(fromName) + @"\b",
                "--" + toName,
                RegexOptions.IgnoreCase);
            content = Regex.Replace(
                content,
                "`--" + Regex.Escape(fromName) + "`",
                "`--" + toName + "`",
                RegexOptions.IgnoreCase);
            content = Regex.Replace(
                content,
                "`" + Regex.Escape(fromName) + "`",
                "`" + toName + "`",
                RegexOptions.IgnoreCase);
            content = Regex.Replace(
                content,
                "(?<=['\"])" + Regex.Escape(fromName) + "(?=['\"])",
                toName,
                RegexOptions.IgnoreCase);
            return content;
        }

        private static string TryReadUnityVersionFromCapabilities(string capabilitiesPath)
        {
            if (!File.Exists(capabilitiesPath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(capabilitiesPath);
                var match = Regex.Match(
                    json,
                    "\"unity\"\\s*:\\s*\\{[\\s\\S]*?\"version\"\\s*:\\s*\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadUnityVersionFromProjectVersion(string projectVersionPath)
        {
            if (!File.Exists(projectVersionPath))
            {
                return null;
            }

            try
            {
                foreach (var line in File.ReadAllLines(projectVersionPath))
                {
                    const string prefix = "m_EditorVersion:";
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line.Substring(prefix.Length).Trim();
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}
