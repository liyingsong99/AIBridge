using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;

namespace AIBridge.Editor
{
    [Serializable]
    internal sealed class NativeMemoryRecord
    {
        public string requestId, operationId, projectPath, generation, targetName, outputPath, tempPath, status;
        public string queuedAt, startedAt, completedAt, error;
        public int editorPid, targetGuid, timeoutMs;
        public bool busy, released, nativeMayStillRun;
        public bool? result;
        public long? sizeBytes;
    }

    /// <summary>纯文件日志：严格读入、原子意图、全部历史占位扫描，不恢复原生 delegate。</summary>
    internal sealed class NativeMemoryJournal
    {
        internal const int MaxBytes = 64 * 1024;
        internal const int MaxRecords = 4096;
        internal const string OutputDirectory = ".aibridge/memory";
        internal const string JournalDirectory = ".aibridge/memory-operations";
        private readonly string _project;
        internal readonly int RecordLimit;
        internal Action<string, string> Write = (path, json) =>
            AIBridgeAtomicFile.WriteTextAtomic(path, json, new UTF8Encoding(false), false, true);
        internal static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly string[] Fields =
        {
            "requestId", "operationId", "projectPath", "generation", "editorPid", "targetGuid", "targetName",
            "outputPath", "tempPath", "status", "queuedAt", "startedAt", "completedAt", "busy", "released",
            "result", "sizeBytes", "error", "nativeMayStillRun", "timeoutMs"
        };

        internal NativeMemoryJournal(string project, int recordLimit = MaxRecords)
        {
            if (recordLimit < 2 || recordLimit > MaxRecords) throw new ArgumentOutOfRangeException(nameof(recordLimit));
            RecordLimit = recordLimit;
            _project = Path.GetFullPath(project);
            RejectReparse(_project);
        }

        internal string RecordPath(string id)
        {
            var name = id == null ? "latest" : Hash(id);
            return Path.Combine(_project, JournalDirectory, name + ".json");
        }

        internal void Save(NativeMemoryRecord record)
        {
            if (!Validate(record)) throw new InvalidDataException("Invalid memory operation record.");
            var path = RecordPath(record.requestId);
            RejectReparse(path); RejectReparse(RecordPath(null));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            RejectReparse(path); RejectReparse(RecordPath(null));
            var json = AIBridgeJson.Serialize(ToData(record));
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Memory journal exceeds limit.");
            // 覆盖另一ID的索引前，先把它唯一/冲突中的意图归入单条日志，不能丢掉旧busy。
            PreservePreviousLatest(record.requestId);
            // 索引先写：第二次写失败仍留下保守意图，不允许当成从未提交。
            Write(RecordPath(null), json);
            Write(path, json);
        }

        private void PreservePreviousLatest(string requestId)
        {
            var latest = ReadFile(RecordPath(null), null);
            if (latest == null || latest.requestId == requestId) return;
            var path = RecordPath(latest.requestId);
            var single = ReadFile(path, latest.requestId);
            if (single != null && AIBridgeJson.Serialize(ToData(single)) == AIBridgeJson.Serialize(ToData(latest))) return;
            var merged = Merge(single, latest);
            var json = AIBridgeJson.Serialize(ToData(merged));
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException("Memory journal exceeds limit.");
            Write(path, json);
        }

        internal NativeMemoryRecord Load(string id, out bool invalid)
        {
            invalid = false;
            try
            {
                var latest = ReadFile(RecordPath(null), null);
                if (id == null) return latest;
                var single = ReadFile(RecordPath(id), id);
                return latest != null && latest.requestId == id ? Merge(single, latest) : single;
            }
            catch (Exception) { invalid = true; return null; }
        }

        internal List<NativeMemoryRecord> ReadAll(out bool invalid)
        {
            invalid = false;
            var result = new Dictionary<string, NativeMemoryRecord>(StringComparer.Ordinal);
            try
            {
                var directory = Path.GetDirectoryName(RecordPath(null));
                RejectReparse(directory);
                if (!Directory.Exists(directory)) return new List<NativeMemoryRecord>();
                var files = Directory.GetFiles(directory, "*.json");
                if (files.Length > RecordLimit) throw new InvalidDataException("Too many memory operation records.");
                NativeMemoryRecord latest = null;
                foreach (var file in files)
                {
                    var record = ReadFile(file, null);
                    if (record == null) throw new InvalidDataException("Missing memory journal during scan.");
                    if (Path.GetFileName(file) == "latest.json") { latest = record; continue; }
                    if (Path.GetFileName(file) != Hash(record.requestId) + ".json") throw new InvalidDataException("Memory journal filename mismatch.");
                    result.Add(record.requestId, record);
                }
                if (latest != null)
                {
                    result.TryGetValue(latest.requestId, out var single);
                    result[latest.requestId] = Merge(single, latest);
                }
                return new List<NativeMemoryRecord>(result.Values);
            }
            catch (Exception) { invalid = true; return new List<NativeMemoryRecord>(); }
        }

        private static NativeMemoryRecord Merge(NativeMemoryRecord single, NativeMemoryRecord latest)
        {
            if (single != null && AIBridgeJson.Serialize(ToData(single)) == AIBridgeJson.Serialize(ToData(latest))) return single;
            if (single != null && !SameOperation(single, latest))
                throw new InvalidDataException("Memory journal identity conflict.");
            // latest 先写只证明意图已记录；单条缺失/落后时，连终态或release也不能当成双写已完成。
            var record = single ?? latest;
            record.startedAt = record.startedAt ?? latest.startedAt;
            NormalizeUnknown(record, "Memory journal versions disagree or request journal is missing; release the exact requestId locally.");
            return record;
        }

        internal static bool SameOperation(NativeMemoryRecord a, NativeMemoryRecord b)
        {
            return a.requestId == b.requestId && a.operationId == b.operationId && a.generation == b.generation
                && a.editorPid == b.editorPid && a.targetGuid == b.targetGuid && a.targetName == b.targetName
                && a.timeoutMs == b.timeoutMs && a.queuedAt == b.queuedAt
                && string.Equals(a.projectPath, b.projectPath, PathComparison)
                && string.Equals(a.outputPath, b.outputPath, PathComparison)
                && string.Equals(a.tempPath, b.tempPath, PathComparison)
                && (a.startedAt == null || b.startedAt == null || a.startedAt == b.startedAt);
        }

        internal static void NormalizeUnknown(NativeMemoryRecord record, string reason)
        {
            record.status = "unknown"; record.busy = true; record.released = false; record.nativeMayStillRun = true;
            record.result = null; record.sizeBytes = null; record.completedAt = null; record.error = reason;
        }

        private NativeMemoryRecord ReadFile(string path, string expectedId)
        {
            RejectReparse(path);
            FileAttributes attributes;
            if (!TryAttributes(path, out attributes)) return null;
            if ((attributes & FileAttributes.Directory) != 0) throw new InvalidDataException();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaxBytes) throw new InvalidDataException();
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), false))
                {
                    var buffer = new char[MaxBytes + 1];
                    var count = 0;
                    while (count < buffer.Length)
                    {
                        var read = reader.Read(buffer, count, buffer.Length - count);
                        if (read == 0) break;
                        count += read;
                    }
                    var json = new string(buffer, 0, count);
                    if (count > MaxBytes || Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new InvalidDataException();
                    var data = AIBridgeJson.DeserializeObjectStrict(json, 8);
                    if (data == null || data.Count != Fields.Length) throw new InvalidDataException();
                    foreach (var field in Fields) if (!data.ContainsKey(field)) throw new InvalidDataException();
                    var record = new NativeMemoryRecord
                    {
                        requestId = Text(data, "requestId"), operationId = Text(data, "operationId"),
                        projectPath = Text(data, "projectPath"), generation = Text(data, "generation"),
                        targetName = Text(data, "targetName"), outputPath = Text(data, "outputPath"), tempPath = Text(data, "tempPath"),
                        status = Text(data, "status"), queuedAt = Text(data, "queuedAt"), startedAt = Text(data, "startedAt"),
                        completedAt = Text(data, "completedAt"), error = Text(data, "error"),
                        editorPid = Number(data, "editorPid"), targetGuid = Number(data, "targetGuid"), timeoutMs = Number(data, "timeoutMs"),
                        busy = Flag(data, "busy"), released = Flag(data, "released"), nativeMayStillRun = Flag(data, "nativeMayStillRun"),
                        result = data["result"] == null ? (bool?)null : Flag(data, "result"),
                        sizeBytes = data["sizeBytes"] == null ? (long?)null : Long(data, "sizeBytes")
                    };
                    if ((expectedId != null && record.requestId != expectedId) || !Validate(record)) throw new InvalidDataException();
                    return record;
                }
            }
        }

        internal bool Validate(NativeMemoryRecord r)
        {
            try
            {
                if (r == null || !ValidId(r.requestId) || !Hex(r.operationId, 32) || !Hex(r.generation, 32)
                    || r.editorPid <= 0 || r.targetGuid != -1 || !ValidId(r.targetName)
                    || r.timeoutMs < 1000 || r.timeoutMs > 60000 || !string.Equals(r.projectPath, _project, PathComparison)
                    || !string.Equals(Path.GetFullPath(r.projectPath), r.projectPath, PathComparison)) return false;
                var output = ResolveSnapshotPath(_project, r.outputPath);
                var temp = ResolveSnapshotPath(_project, r.tempPath);
                if (output != r.outputPath || temp != r.tempPath || string.Equals(output, temp, PathComparison)) return false;
                var queued = Time(r.queuedAt);
                var started = r.startedAt == null ? (DateTimeOffset?)null : Time(r.startedAt);
                var ended = r.completedAt == null ? (DateTimeOffset?)null : Time(r.completedAt);
                if ((started.HasValue && started.Value < queued) || (ended.HasValue && ended.Value < (started ?? queued))
                    || (r.error != null && r.error.Length > 4096)) return false;
                switch (r.status)
                {
                    case "queued":
                        return r.busy && !r.released && !r.nativeMayStillRun && !started.HasValue && !ended.HasValue
                            && r.result == null && r.sizeBytes == null && r.error == null;
                    case "capturing":
                        return r.busy && !r.released && r.nativeMayStillRun && started.HasValue && !ended.HasValue
                            && r.result == null && r.sizeBytes == null && r.error == null;
                    case "unknown":
                        return r.busy == !r.released && !ended.HasValue && r.result == null && r.sizeBytes == null
                            && !string.IsNullOrWhiteSpace(r.error);
                    case "completed":
                        return !r.busy && !r.released && !r.nativeMayStillRun && started.HasValue && ended.HasValue
                            && r.result == true && r.sizeBytes > 0 && r.error == null;
                    case "failed":
                        return !r.busy && !r.released && !r.nativeMayStillRun && ended.HasValue && r.result == false
                            && r.sizeBytes == null && !string.IsNullOrWhiteSpace(r.error);
                    default: return false;
                }
            }
            catch (Exception) { return false; }
        }

        internal static string ResolveSnapshotPath(string project, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested) || requested.StartsWith("\\\\", StringComparison.Ordinal)
                || requested.StartsWith("//", StringComparison.Ordinal) || requested.IndexOf('\0') >= 0)
                throw new ArgumentException("UNC/device or empty snapshot path is forbidden.");
            foreach (var segment in requested.Replace('\\', '/').Split('/'))
                if (segment == ".." || segment == ".") throw new ArgumentException("Snapshot path traversal is forbidden.");
            var root = Path.GetFullPath(Path.Combine(project, OutputDirectory));
            var path = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(project, requested));
            if (!string.Equals(Path.GetDirectoryName(path), root, PathComparison)
                || Path.GetExtension(path) != ".snap" || !Hex(Path.GetFileNameWithoutExtension(path), 32))
                throw new ArgumentException("Snapshot must be a direct lowercase GUID.snap under " + OutputDirectory + ".");
            RejectReparse(path);
            FileAttributes attributes;
            if (TryAttributes(path, out attributes) && (attributes & FileAttributes.Directory) != 0) throw new IOException("Snapshot path is a directory.");
            return path;
        }

        internal static void AssertAbsent(string path)
        {
            RejectReparse(path);
            FileAttributes attributes;
            if (TryAttributes(path, out attributes)) throw new IOException("Snapshot target exists; overwrite is not supported.");
        }

        internal static void RejectReparse(string path)
        {
            var current = Path.GetFullPath(path);
            while (current != null)
            {
                FileAttributes attributes;
                if (TryAttributes(current, out attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse/symlink paths are forbidden.");
                current = Path.GetDirectoryName(current);
            }
        }

        private static bool TryAttributes(string path, out FileAttributes attributes)
        {
            try { attributes = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { attributes = default; return false; }
            catch (DirectoryNotFoundException) { attributes = default; return false; }
        }

        internal static Dictionary<string, object> ToData(NativeMemoryRecord r)
        {
            return (Dictionary<string, object>)AIBridgeJson.PreserveObjectNulls(new Dictionary<string, object>
            {
                { "requestId", r.requestId }, { "operationId", r.operationId }, { "projectPath", r.projectPath },
                { "generation", r.generation }, { "editorPid", r.editorPid }, { "targetGuid", r.targetGuid }, { "targetName", r.targetName },
                { "outputPath", r.outputPath }, { "tempPath", r.tempPath }, { "status", r.status }, { "queuedAt", r.queuedAt },
                { "startedAt", r.startedAt }, { "completedAt", r.completedAt }, { "busy", r.busy }, { "released", r.released },
                { "result", r.result }, { "sizeBytes", r.sizeBytes }, { "error", r.error },
                { "nativeMayStillRun", r.nativeMayStillRun }, { "timeoutMs", r.timeoutMs }
            });
        }

        internal static bool ValidId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256) return false;
            foreach (var c in id) if (c < 32) return false;
            return true;
        }

        internal static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }

        private static DateTimeOffset Time(string value)
        {
            if (value == null || (!value.EndsWith("Z", StringComparison.Ordinal) && !value.EndsWith("+00:00", StringComparison.Ordinal)))
                throw new InvalidDataException();
            DateTimeOffset time;
            var formats = new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" };
            if (!DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time)
                || time.Offset != TimeSpan.Zero) throw new InvalidDataException();
            return time;
        }

        private static string Text(Dictionary<string, object> data, string key)
        {
            if (data[key] == null || data[key] is string) return (string)data[key];
            throw new InvalidDataException();
        }
        private static bool Flag(Dictionary<string, object> data, string key)
        {
            if (data[key] is bool flag) return flag;
            throw new InvalidDataException();
        }
        private static long Long(Dictionary<string, object> data, string key)
        {
            if (data[key] is long number) return number;
            throw new InvalidDataException();
        }
        private static int Number(Dictionary<string, object> data, string key)
        {
            return checked((int)Long(data, key));
        }
    }
}
