using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIBridge.Internal.Json;

namespace AIBridge.Runtime.Internal
{
    /// <summary>构建日志的严格、保守读取与原子发布；不负责推断构建是否停止。</summary>
    public static class AIBridgeBuildStore
    {
        private const int MaxFileBytes = 4 * 1024 * 1024;
        private const int MaxString = 8192;
        private const int MaxId = 256;
        private const int MaxScenes = 128;
        private const int MaxFiles = 256;
        private const int MaxSteps = 128;
        private const int MaxMessages = 32;
        private const string RecordFields = "schemaVersion requestId buildId projectPath unityVersion generation status target outputPath queuedAt startedAt endedAt errorCode error editorPid success busy released invocationStarted development scenes report";
        private const string ReportFields = "result target outputPath totalSizeBytes durationMs totalErrors totalWarnings fileCount stepCount truncated files steps";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly string[] UtcFormats =
        {
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
            "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
        };

        public static string RecordPath(string projectRoot, string requestId)
        {
            if (requestId != null && !ValidId(requestId)) throw new ArgumentException("Invalid requestId.", nameof(requestId));
            var root = FullRoot(projectRoot);
            var name = "latest";
            if (requestId != null)
            {
                using (var hash = SHA256.Create())
                {
                    var digest = hash.ComputeHash(Utf8.GetBytes(requestId));
                    name = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            var path = Path.Combine(root, ".aibridge", "build-operations", name + ".json");
            RequireSafePath(path);
            return path;
        }

        public static void Save(string projectRoot, AIBridgeBuildRecord record)
        {
            if (!Validate(record, projectRoot)) throw new ArgumentException("Invalid build record.", nameof(record));
            var json = AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(RecordData(record)));
            if (Utf8.GetByteCount(json) > MaxFileBytes) throw new InvalidDataException("Build record exceeds size limit.");
            // 校验实际发布的快照，避免可变 DTO 在复制期间产生不能读回的状态。
            var snapshot = ReadRecord(AIBridgeJson.DeserializeObjectStrict(json, 32));
            if (!Validate(snapshot, projectRoot)) throw new InvalidDataException("Invalid build snapshot.");
            var path = RecordPath(projectRoot, snapshot.requestId);
            var latest = RecordPath(projectRoot, null);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            RequireSafePath(path);
            RequireSafePath(latest);
            // 单 ID 先落盘；latest 失败也不能删掉任何已发布的版本，更不能自动重试构建。
            AIBridgeAtomicFile.WriteTextAtomic(path, json, Utf8, false, true);
            RequireSafePath(latest);
            AIBridgeAtomicFile.WriteTextAtomic(latest, json, Utf8, false, true);
        }

        public static AIBridgeBuildRecord Load(string projectRoot, string requestId, out bool unreadable)
        {
            unreadable = false;
            try
            {
                if (requestId == null) return LoadDefault(projectRoot);
                var path = RecordPath(projectRoot, requestId);
                string json;
                try { json = ReadBounded(path); }
                catch (FileNotFoundException) { RequireSafePath(path); return null; }
                catch (DirectoryNotFoundException) { RequireSafePath(path); return null; }
                RequireSafePath(path);
                var record = ReadRecord(AIBridgeJson.DeserializeObjectStrict(json, 32));
                if (!Validate(record, projectRoot) || (requestId != null && record.requestId != requestId))
                    throw new InvalidDataException("Build record identity or content is invalid.");
                return record;
            }
            catch (Exception)
            {
                // 损坏、权限、身份及路径异常均不能变成空闲/成功。读取本身不改写日志。
                unreadable = true;
                return null;
            }
        }

        private static AIBridgeBuildRecord LoadDefault(string projectRoot)
        {
            var path = RecordPath(projectRoot, null);
            AIBridgeBuildRecord index = null;
            try
            {
                RequireSafePath(path);
                index = ReadRecord(AIBridgeJson.DeserializeObjectStrict(ReadBounded(path), 32));
                if (!Validate(index, projectRoot)) throw new InvalidDataException("Invalid build index.");
                // 单ID先发布，latest仅为索引；partial latest写失败不能复活旧busy状态。
                var authoritative = Load(projectRoot, index.requestId, out var invalidSingle);
                if (invalidSingle) throw new InvalidDataException("Authoritative build record is unreadable.");
                if (authoritative != null)
                {
                    if (authoritative.buildId != index.buildId) throw new InvalidDataException("Build index identity conflict.");
                    index = authoritative;
                }
            }
            catch (FileNotFoundException) { RequireSafePath(path); }
            catch (DirectoryNotFoundException) { RequireSafePath(path); }
            var directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) return index;
            var files = Directory.GetFiles(directory, "*.json");
            if (files.Length > 4096) throw new InvalidDataException("Too many build records.");
            var busy = index != null && index.busy ? index : null;
            foreach (var file in files)
            {
                if (Path.GetFileName(file) == "latest.json") continue;
                RequireSafePath(file);
                var candidate = ReadRecord(AIBridgeJson.DeserializeObjectStrict(ReadBounded(file), 32));
                if (!Validate(candidate, projectRoot) || RecordPath(projectRoot, candidate.requestId) != file)
                    throw new InvalidDataException("Invalid registered build record.");
                if (candidate.busy)
                {
                    if (busy != null && busy.requestId != candidate.requestId) throw new InvalidDataException("Multiple unresolved builds.");
                    busy = candidate;
                }
                if (index == null || string.CompareOrdinal(candidate.queuedAt, index.queuedAt) > 0) index = candidate;
            }
            return busy ?? index;
        }

        public static bool Validate(AIBridgeBuildRecord record, string projectRoot)
        {
            try { return ValidateCore(record, FullRoot(projectRoot)); }
            catch (Exception) { return false; }
        }

        private static bool ValidateCore(AIBridgeBuildRecord r, string root)
        {
            if (r == null || r.schemaVersion != 1 || !ValidId(r.requestId) || !HexId(r.buildId) || !HexId(r.generation)
                || r.editorPid <= 0 || !Text(r.unityVersion, true) || !Text(r.target, true)
                || !Text(r.status, true) || !Text(r.errorCode) || !Text(r.error)) return false;
            if (!CanonicalPath(r.projectPath) || !string.Equals(TrimPath(r.projectPath), root, PathComparison)) return false;
            RequireSafePath(r.projectPath);
            var leaf = Path.Combine(root, ".aibridge", "builds", r.buildId);
            if (!CanonicalPath(r.outputPath) || !r.outputPath.StartsWith(leaf + Path.DirectorySeparatorChar, PathComparison)) return false;
            RequireSafePath(r.outputPath);
            if (!ValidScenes(r.scenes) || !Utc(r.queuedAt, out var queued)) return false;
            if (r.startedAt != null && (!Utc(r.startedAt, out var started) || started < queued)) return false;
            if (r.endedAt != null && (!Utc(r.endedAt, out var ended) || ended < queued)) return false;
            if (r.startedAt != null && r.endedAt != null && ParseUtc(r.endedAt) < ParseUtc(r.startedAt)) return false;
            switch (r.status)
            {
                case "queued":
                    return !r.success.HasValue && r.busy && !r.released && !r.invocationStarted
                        && r.report == null && r.startedAt == null && r.endedAt == null;
                case "building":
                    return !r.success.HasValue && r.busy && !r.released && r.invocationStarted
                        && r.report == null && r.startedAt != null && r.endedAt == null;
                case "completed":
                    return !r.busy && !r.released && r.invocationStarted && r.startedAt != null && r.endedAt != null
                        && r.success.HasValue && ValidReport(r.report, r)
                        && r.success.Value == (r.report.result == "Succeeded")
                        && (r.success.Value || Text(r.error, true));
                case "unknown":
                    return !r.success.HasValue && r.report == null && r.busy == !r.released
                        && Text(r.errorCode, true) && Text(r.error, true)
                        && (r.startedAt == null || r.invocationStarted);
                default: return false;
            }
        }

        private static bool ValidReport(AIBridgeBuildReport report, AIBridgeBuildRecord record)
        {
            if (report == null || (report.result != "Succeeded" && report.result != "Failed"
                && report.result != "Cancelled" && report.result != "Unknown")) return false;
            if (report.target != record.target || report.outputPath != record.outputPath || report.totalSizeBytes < 0
                || report.durationMs < 0 || report.totalErrors < 0 || report.totalWarnings < 0
                || (report.result == "Succeeded" && report.totalErrors != 0)) return false;
            if (report.files == null || report.steps == null || report.files.Count > MaxFiles || report.steps.Count > MaxSteps
                || report.fileCount < report.files.Count || report.stepCount < report.steps.Count) return false;
            if (!report.truncated && (report.fileCount != report.files.Count || report.stepCount != report.steps.Count)) return false;
            foreach (var file in report.files)
                if (file == null || !Text(file.path, true) || !Text(file.role) || file.role == null || file.sizeBytes < 0) return false;
            foreach (var step in report.steps)
            {
                if (step == null || !Text(step.name, true) || step.depth < 0 || step.durationMs < 0
                    || step.messages == null || step.messages.Count > MaxMessages) return false;
                foreach (var message in step.messages)
                    if (message == null || !Text(message.type, true) || !Text(message.message) || message.message == null) return false;
            }
            return true;
        }

        private static bool ValidScenes(List<string> scenes)
        {
            if (scenes == null || scenes.Count == 0 || scenes.Count > MaxScenes) return false;
            foreach (var scene in scenes)
            {
                if (!Text(scene, true) || !scene.StartsWith("Assets/", StringComparison.Ordinal)
                    || !scene.EndsWith(".unity", StringComparison.Ordinal) || scene.IndexOf('\\') >= 0) return false;
                foreach (var part in scene.Split('/'))
                    if (part.Length == 0 || part == "." || part == ".." || InvalidSegment(part)) return false;
            }
            return true;
        }

        private static bool Text(string value, bool required = false)
        {
            if (value == null) return !required;
            if (value.Length > MaxString || (required && string.IsNullOrWhiteSpace(value))) return false;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsLowSurrogate(value[i])) return false;
                if (!char.IsHighSurrogate(value[i])) continue;
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            return true;
        }

        private static bool ValidId(string value)
        {
            if (!Text(value, true) || value.Length > MaxId) return false;
            foreach (var c in value) if (char.IsControl(c)) return false;
            return true;
        }

        private static bool HexId(string value)
        {
            if (value == null || value.Length != 32) return false;
            foreach (var c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        private static bool Utc(string value, out DateTimeOffset time)
        {
            time = default;
            return value != null && (value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal))
                && DateTimeOffset.TryParseExact(value, UtcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
                && time.Offset == TimeSpan.Zero;
        }

        private static DateTimeOffset ParseUtc(string value)
        {
            if (!Utc(value, out var time)) throw new FormatException("UTC timestamp required.");
            return time;
        }

        private static string FullRoot(string root)
        {
            if (!Text(root, true)) throw new ArgumentException("Project root required.", nameof(root));
            var full = TrimPath(Path.GetFullPath(root));
            if (!CanonicalPath(full)) throw new ArgumentException("Invalid project root.", nameof(root));
            RequireSafePath(full);
            return full;
        }

        private static string TrimPath(string path)
        {
            var volume = Path.GetPathRoot(path);
            return path.Length > volume.Length ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
        }

        private static bool CanonicalPath(string path)
        {
            if (!Text(path, true) || !Path.IsPathRooted(path)) return false;
            if (Path.DirectorySeparatorChar == '\\' && (path.StartsWith(@"\\?\", StringComparison.Ordinal)
                || path.StartsWith(@"\\.\", StringComparison.Ordinal))) return false;
            foreach (var c in path) if (char.IsControl(c)) return false;
            // 持久路径必须已归一；不接受通过 ..、替代分隔符或尾随点掩盖的同一路径。
            if (!string.Equals(TrimPath(path), TrimPath(Path.GetFullPath(path)), PathComparison)) return false;
            var tail = path.Substring(Path.GetPathRoot(path).Length);
            if (tail.Length == 0) return true;
            foreach (var segment in tail.Split(Path.DirectorySeparatorChar))
                if (segment.Length == 0 || segment == "." || segment == ".." || InvalidSegment(segment)) return false;
            return true;
        }

        private static bool InvalidSegment(string segment)
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return true;
            foreach (var c in segment) if (char.IsControl(c)) return true;
            if (Path.DirectorySeparatorChar != '\\') return false;
            if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) return true;
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;
            return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && stem[3] >= '1' && stem[3] <= '9';
        }

        private static void RequireSafePath(string path)
        {
            var current = Path.GetFullPath(path);
            var leaf = true;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reparse paths are not accepted.");
                    if (!leaf && (attributes & FileAttributes.Directory) == 0)
                        throw new IOException("Path ancestor is not a directory.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
                leaf = false;
            }
        }

        private static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxFileBytes) throw new InvalidDataException("Build record exceeds size limit.");
                var bytes = new byte[MaxFileBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = stream.Read(bytes, count, bytes.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaxFileBytes) throw new InvalidDataException("Build record exceeds size limit.");
                var offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                return Utf8.GetString(bytes, offset, count - offset);
            }
        }

        private static Dictionary<string, object> Object(object value, string fields)
        {
            var data = value as Dictionary<string, object>;
            var names = fields.Split(' ');
            if (data == null || data.Count != names.Length) throw new FormatException("Unexpected build schema.");
            foreach (var name in names) if (!data.ContainsKey(name)) throw new FormatException("Missing field: " + name);
            return data;
        }

        private static string String(object value, bool nullable = false)
        {
            if (nullable && value == null) return null;
            if (!(value is string text) || text.Length > MaxString) throw new FormatException("String required.");
            return text;
        }

        private static bool Boolean(object value)
        {
            if (!(value is bool flag)) throw new FormatException("Boolean required.");
            return flag;
        }

        private static long Integer(object value)
        {
            if (!(value is long number)) throw new FormatException("Integer required.");
            return number;
        }

        private static int Int32(object value)
        {
            var number = Integer(value);
            if (number < int.MinValue || number > int.MaxValue) throw new FormatException("Integer out of range.");
            return (int)number;
        }

        private static List<T> Items<T>(object value, int maximum, Func<object, T> read)
        {
            if (!(value is List<object> items) || items.Count > maximum) throw new FormatException("Invalid bounded list.");
            var result = new List<T>(items.Count);
            foreach (var item in items) result.Add(read(item));
            return result;
        }

        private static AIBridgeBuildRecord ReadRecord(object value)
        {
            var d = Object(value, RecordFields);
            return new AIBridgeBuildRecord
            {
                schemaVersion = Int32(d["schemaVersion"]), requestId = String(d["requestId"]), buildId = String(d["buildId"]),
                projectPath = String(d["projectPath"]), unityVersion = String(d["unityVersion"]), generation = String(d["generation"]),
                status = String(d["status"]), target = String(d["target"]), outputPath = String(d["outputPath"]),
                queuedAt = String(d["queuedAt"]), startedAt = String(d["startedAt"], true), endedAt = String(d["endedAt"], true),
                errorCode = String(d["errorCode"], true), error = String(d["error"], true), editorPid = Int32(d["editorPid"]),
                success = d["success"] == null ? (bool?)null : Boolean(d["success"]), busy = Boolean(d["busy"]),
                released = Boolean(d["released"]), invocationStarted = Boolean(d["invocationStarted"]),
                development = Boolean(d["development"]), scenes = Items(d["scenes"], MaxScenes, item => String(item)),
                report = d["report"] == null ? null : ReadReport(d["report"])
            };
        }

        private static AIBridgeBuildReport ReadReport(object value)
        {
            var d = Object(value, ReportFields);
            return new AIBridgeBuildReport
            {
                result = String(d["result"]), target = String(d["target"]), outputPath = String(d["outputPath"]),
                totalSizeBytes = Integer(d["totalSizeBytes"]), durationMs = Integer(d["durationMs"]),
                totalErrors = Int32(d["totalErrors"]), totalWarnings = Int32(d["totalWarnings"]),
                fileCount = Int32(d["fileCount"]), stepCount = Int32(d["stepCount"]), truncated = Boolean(d["truncated"]),
                files = Items(d["files"], MaxFiles, ReadFile), steps = Items(d["steps"], MaxSteps, ReadStep)
            };
        }

        private static AIBridgeBuildFile ReadFile(object value)
        {
            var d = Object(value, "path role sizeBytes");
            return new AIBridgeBuildFile { path = String(d["path"]), role = String(d["role"]), sizeBytes = Integer(d["sizeBytes"]) };
        }

        private static AIBridgeBuildStep ReadStep(object value)
        {
            var d = Object(value, "name depth durationMs messages");
            return new AIBridgeBuildStep
            {
                name = String(d["name"]), depth = Int32(d["depth"]), durationMs = Integer(d["durationMs"]),
                messages = Items(d["messages"], MaxMessages, ReadMessage)
            };
        }

        private static AIBridgeBuildMessage ReadMessage(object value)
        {
            var d = Object(value, "type message");
            return new AIBridgeBuildMessage { type = String(d["type"]), message = String(d["message"]) };
        }

        private static Dictionary<string, object> RecordData(AIBridgeBuildRecord r)
        {
            return new Dictionary<string, object>
            {
                { "schemaVersion", r.schemaVersion }, { "requestId", r.requestId }, { "buildId", r.buildId },
                { "projectPath", r.projectPath }, { "unityVersion", r.unityVersion }, { "generation", r.generation },
                { "status", r.status }, { "target", r.target }, { "outputPath", r.outputPath }, { "queuedAt", r.queuedAt },
                { "startedAt", r.startedAt }, { "endedAt", r.endedAt }, { "errorCode", r.errorCode }, { "error", r.error },
                { "editorPid", r.editorPid }, { "success", r.success }, { "busy", r.busy }, { "released", r.released },
                { "invocationStarted", r.invocationStarted }, { "development", r.development }, { "scenes", r.scenes },
                { "report", r.report == null ? null : ReportData(r.report) }
            };
        }

        private static Dictionary<string, object> ReportData(AIBridgeBuildReport r)
        {
            return new Dictionary<string, object>
            {
                { "result", r.result }, { "target", r.target }, { "outputPath", r.outputPath },
                { "totalSizeBytes", r.totalSizeBytes }, { "durationMs", r.durationMs }, { "totalErrors", r.totalErrors },
                { "totalWarnings", r.totalWarnings }, { "fileCount", r.fileCount }, { "stepCount", r.stepCount },
                { "truncated", r.truncated }, { "files", r.files.ConvertAll(FileData) }, { "steps", r.steps.ConvertAll(StepData) }
            };
        }

        private static Dictionary<string, object> FileData(AIBridgeBuildFile f)
        {
            return new Dictionary<string, object> { { "path", f.path }, { "role", f.role }, { "sizeBytes", f.sizeBytes } };
        }

        private static Dictionary<string, object> StepData(AIBridgeBuildStep s)
        {
            return new Dictionary<string, object>
            {
                { "name", s.name }, { "depth", s.depth }, { "durationMs", s.durationMs },
                { "messages", s.messages.ConvertAll(m => new Dictionary<string, object> { { "type", m.type }, { "message", m.message } }) }
            };
        }
    }
}
