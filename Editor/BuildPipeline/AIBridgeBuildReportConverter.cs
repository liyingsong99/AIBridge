using System;
using AIBridge.Runtime.Internal;
using UnityEditor.Build.Reporting;

namespace AIBridge.Editor
{
    /// <summary>在主线程读BuildReport一次，持久化只保留有界纯DTO；不保留Unity对象引用。</summary>
    internal static class AIBridgeBuildReportConverter
    {
        private const int MaxFiles = 256;
        private const int MaxSteps = 128;
        private const int MaxMessages = 32;
        private const int MaxText = 8192;
        private const int MaxRetainedTextChars = 256 * 1024;

        public static AIBridgeBuildReport Capture(BuildReport report)
        {
            if (report == null) throw new InvalidOperationException("BuildReport unavailable.");
            var summary = report.summary;
            var result = new AIBridgeBuildReport
            {
                result = summary.result.ToString(), target = summary.platform.ToString(), outputPath = System.IO.Path.GetFullPath(summary.outputPath),
                totalSizeBytes = checked((long)summary.totalSize), durationMs = Duration(summary.totalTime),
                totalErrors = checked((int)summary.totalErrors), totalWarnings = checked((int)summary.totalWarnings),
                files = new System.Collections.Generic.List<AIBridgeBuildFile>(),
                steps = new System.Collections.Generic.List<AIBridgeBuildStep>()
            };
            // GetFiles替代files的Unity版本边界集中在此，2019.4仍使用原始属性。
#if UNITY_2022_1_OR_NEWER
            var files = report.GetFiles();
#else
            var files = report.files;
#endif
            if (files == null) throw new InvalidOperationException("Build file list unavailable.");
            result.fileCount = files.Length;
            var retainedChars = 0;
            for (var i = 0; i < files.Length && i < MaxFiles; i++)
            {
                if (retainedChars + (files[i].path?.Length ?? 0) + (files[i].role?.Length ?? 0) > MaxRetainedTextChars)
                { result.truncated = true; break; }
                var file = new AIBridgeBuildFile { path = Text(files[i].path, result), role = Text(files[i].role, result),
                    sizeBytes = checked((long)files[i].size) };
                retainedChars += file.path.Length + file.role.Length; result.files.Add(file);
            }
            var steps = report.steps;
            if (steps == null) throw new InvalidOperationException("Build steps unavailable.");
            result.stepCount = steps.Length;
            for (var i = 0; i < steps.Length && i < MaxSteps; i++)
            {
                var source = steps[i];
                if (retainedChars + (source.name?.Length ?? 0) > MaxRetainedTextChars) { result.truncated = true; break; }
                var step = new AIBridgeBuildStep { name = Text(source.name, result), depth = source.depth,
                    durationMs = Duration(source.duration), messages = new System.Collections.Generic.List<AIBridgeBuildMessage>() };
                var messages = source.messages;
                if (messages == null) throw new InvalidOperationException("Build messages unavailable.");
                retainedChars += step.name.Length;
                for (var j = 0; j < messages.Length && j < MaxMessages; j++)
                {
                    if (retainedChars + (messages[j].content?.Length ?? 0) + 32 > MaxRetainedTextChars)
                    { result.truncated = true; break; }
                    var message = new AIBridgeBuildMessage { type = messages[j].type.ToString(), message = Text(messages[j].content, result) };
                    retainedChars += message.message.Length + message.type.Length; step.messages.Add(message);
                }
                if (messages.Length > MaxMessages) result.truncated = true;
                result.steps.Add(step);
            }
            result.truncated |= files.Length > MaxFiles || steps.Length > MaxSteps;
            return result;
        }

        private static long Duration(TimeSpan span)
        {
            if (span < TimeSpan.Zero || double.IsNaN(span.TotalMilliseconds) || double.IsInfinity(span.TotalMilliseconds)
                || span.TotalMilliseconds > long.MaxValue) throw new InvalidOperationException("Invalid build duration.");
            return (long)span.TotalMilliseconds;
        }

        private static string Text(string value, AIBridgeBuildReport report)
        {
            if (value == null) throw new InvalidOperationException("Required BuildReport text is unavailable.");
            if (value.Length <= MaxText) return value;
            report.truncated = true;
            // 不切断UTF16代理对，保证严格JSON可以再次读回。
            var length = char.IsHighSurrogate(value[MaxText - 1]) ? MaxText - 1 : MaxText;
            return value.Substring(0, length);
        }
    }
}
