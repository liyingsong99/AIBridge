using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;

namespace AIBridge.Editor
{
    [Serializable]
    internal sealed class PipelineCompileRecord
    {
        public int schema = 1;
        public string backend = "pipeline";
        public string requestId;
        public string status = "intent";
        public bool busy = true;
        public bool released;
        public bool submissionStarted;
        public string acknowledgement;
        public string projectPath;
        public int editorPid;
        public string unityVersion;
        public string generation;
        public string targetIdentity;
        public string sessionGeneration;
        public string queuedAt;
        public string endedAt;
        public int timeoutMs;
        public int httpStatus;
        public string errorCode;
        public string error;
        public object data;
    }

    /// <summary>单请求文件是唯一事实来源；读取损坏时失败关闭，绝不借此重放提交。</summary>
    internal sealed class PipelineCompileStore : IPipelineCompileJournal
    {
        internal const string DirectoryName = "pipeline-compile-requests";
        private const int MaxRecords = 4096;
        private const int MaxRecordBytes = 2 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly string[] Fields =
        {
            "schema", "backend", "requestId", "status", "busy", "released", "submissionStarted", "acknowledgement",
            "projectPath", "editorPid", "unityVersion", "generation", "targetIdentity", "sessionGeneration", "queuedAt", "endedAt",
            "timeoutMs", "httpStatus", "errorCode", "error", "data"
        };
        private readonly string _root;
        private readonly string _directory;
        private readonly int _recordLimit;

        internal PipelineCompileStore(string projectRoot, int recordLimit = MaxRecords)
        {
            if (recordLimit < 1 || recordLimit > MaxRecords) throw new ArgumentOutOfRangeException(nameof(recordLimit));
            _root = Path.GetFullPath(projectRoot);
            _directory = Path.Combine(_root, ".aibridge", DirectoryName);
            _recordLimit = recordLimit;
        }

        public PipelineCompileRecord Load(string requestId, out bool invalid)
        {
            invalid = false;
            if (!IsRequestId(requestId)) { invalid = true; return null; }
            try
            {
                var path = Path.Combine(_directory, requestId + ".json");
                RejectReparseAncestors(path);
                if (!Exists(path)) return null;
                var record = Decode(ReadBounded(path));
                if (record.requestId != requestId || !SamePath(record.projectPath, _root)) throw new FormatException();
                return record;
            }
            catch (Exception) { invalid = true; return null; }
        }

        public PipelineCompileRecord FindOutstanding(out bool invalid) { return Scan(true, out invalid); }
        public PipelineCompileRecord FindLatest(out bool invalid) { return Scan(false, out invalid); }

        private PipelineCompileRecord Scan(bool outstanding, out bool invalid)
        {
            invalid = false;
            try
            {
                RejectReparseAncestors(_directory);
                if (!Exists(_directory)) return null;
                if (!Directory.Exists(_directory)) throw new IOException();
                PipelineCompileRecord latest = null;
                PipelineCompileRecord busy = null;
                var count = 0;
                foreach (var file in Directory.EnumerateFileSystemEntries(_directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (++count > _recordLimit) throw new IOException();
                    var record = Load(Path.GetFileNameWithoutExtension(file), out var unreadable);
                    if (unreadable || record == null) throw new IOException();
                    if (record.busy)
                    {
                        if (busy != null) throw new IOException();
                        busy = record;
                    }
                    if (latest == null || IsLater(record, latest)) latest = record;
                }
                return outstanding ? busy : busy ?? latest;
            }
            catch (Exception) { invalid = true; return null; }
        }

        public void RequireCapacityForNewRequest()
        {
            RejectReparseAncestors(_directory);
            if (!Exists(_directory)) return;
            // 容量预检同时确认已有日志可读；损坏记录不能被新意图掩盖。
            Scan(false, out var invalid);
            if (invalid) throw new IOException("Pipeline compile journal is corrupt or unavailable.");
            var count = 0;
            foreach (var file in Directory.EnumerateFileSystemEntries(_directory, "*.json", SearchOption.TopDirectoryOnly))
                if (++count >= _recordLimit) throw new IOException("Pipeline compile journal capacity reached; no intent or POST was started.");
        }

        public void Save(PipelineCompileRecord record)
        {
            var json = Encode(record);
            if (json.Length > MaxRecordBytes || StrictUtf8.GetByteCount(json) > MaxRecordBytes) throw new FormatException();
            var verified = Decode(json);
            if (!SamePath(verified.projectPath, _root)) throw new FormatException();
            var path = Path.Combine(_directory, verified.requestId + ".json");
            RejectReparseAncestors(path);
            if (Exists(path))
            {
                if (Load(verified.requestId, out var invalid) == null || invalid) throw new IOException("Existing compile record is unreadable.");
            }
            else RequireCapacityForNewRequest();
            Directory.CreateDirectory(_directory);
            RejectReparseAncestors(path);
            // 满容量后仍允许原ID应答/释放原子落盘，不删除旧状态腾空间。
            AIBridgeAtomicFile.WriteTextAtomic(path, json, StrictUtf8, false, true);
        }

        internal static PipelineCompileRecord Clone(PipelineCompileRecord record) { return Decode(Encode(record)); }
        private static string Encode(PipelineCompileRecord record) { return AIBridgeJson.Serialize(ToData(record)); }

        internal static object ToData(PipelineCompileRecord r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            return AIBridgeJson.PreserveObjectNulls(new Dictionary<string, object>
            {
                { "schema", r.schema }, { "backend", r.backend }, { "requestId", r.requestId }, { "status", r.status },
                { "busy", r.busy }, { "released", r.released }, { "submissionStarted", r.submissionStarted },
                { "acknowledgement", r.acknowledgement }, { "projectPath", r.projectPath }, { "editorPid", r.editorPid },
                { "unityVersion", r.unityVersion }, { "generation", r.generation }, { "targetIdentity", r.targetIdentity },
                { "sessionGeneration", r.sessionGeneration }, { "queuedAt", r.queuedAt }, { "endedAt", r.endedAt },
                { "timeoutMs", r.timeoutMs }, { "httpStatus", r.httpStatus }, { "errorCode", r.errorCode }, { "error", r.error }, { "data", r.data }
            });
        }

        private static PipelineCompileRecord Decode(string json)
        {
            var d = AIBridgeJson.DeserializeObjectStrict(json, 24);
            if (d == null || d.Count != Fields.Length) throw new FormatException();
            foreach (var field in Fields) if (!d.ContainsKey(field)) throw new FormatException();
            if (!Equals(d["schema"], 1L) || !Equals(d["backend"], "pipeline")) throw new FormatException();
            var r = new PipelineCompileRecord
            {
                requestId = Text(d, "requestId"), status = Text(d, "status"), busy = Flag(d, "busy"), released = Flag(d, "released"),
                submissionStarted = Flag(d, "submissionStarted"), acknowledgement = Text(d, "acknowledgement", true),
                projectPath = Text(d, "projectPath"), editorPid = Integer(d, "editorPid"), unityVersion = Text(d, "unityVersion"),
                generation = Text(d, "generation"), targetIdentity = Text(d, "targetIdentity", true), sessionGeneration = Text(d, "sessionGeneration", true),
                queuedAt = Text(d, "queuedAt"), endedAt = Text(d, "endedAt", true), timeoutMs = Integer(d, "timeoutMs"),
                httpStatus = Integer(d, "httpStatus"), errorCode = Text(d, "errorCode", true), error = Text(d, "error", true),
                data = AIBridgeJson.PreserveObjectNulls(d["data"])
            };
            Validate(r);
            return r;
        }

        private static void Validate(PipelineCompileRecord r)
        {
            if (!IsRequestId(r.requestId) || !IsGeneration(r.generation) || r.editorPid <= 0 || string.IsNullOrWhiteSpace(r.unityVersion)
                || r.unityVersion.Length > 256 || !Path.IsPathRooted(r.projectPath) || r.projectPath.Length > 32768
                || !PipelineCompileProtocol.ValidUtc(r.queuedAt, out var queued) || r.httpStatus < 0 || r.httpStatus > 599
                || r.timeoutMs < PipelineCompileProtocol.MinTimeoutMs || r.timeoutMs > PipelineCompileProtocol.MaxTimeoutMs) throw new FormatException();
            if (r.endedAt != null && (!PipelineCompileProtocol.ValidUtc(r.endedAt, out var ended) || ended < queued)) throw new FormatException();
            if (r.targetIdentity != null && !IsIdentity(r.targetIdentity)) throw new FormatException();
            if (r.sessionGeneration != null && !IsGeneration(r.sessionGeneration)) throw new FormatException();
            if ((r.targetIdentity == null) != (r.sessionGeneration == null)) throw new FormatException();
            if (r.submissionStarted && r.targetIdentity == null) throw new FormatException();
            switch (r.status)
            {
                case "acknowledged": ValidateAcknowledged(r); break;
                case "unknown": ValidateUnknown(r); break;
                case "intent":
                    if (!r.busy || r.released || r.submissionStarted || r.targetIdentity != null || r.endedAt != null) throw new FormatException();
                    ValidateUnacknowledged(r); break;
                case "submitting":
                    if (!r.busy || r.released || !r.submissionStarted || r.endedAt != null) throw new FormatException();
                    ValidateUnacknowledged(r); break;
                case "not_submitted":
                    if (r.busy || r.released || r.submissionStarted || r.endedAt == null) throw new FormatException();
                    ValidateUnacknowledged(r); break;
                default: throw new FormatException();
            }
        }

        private static void ValidateAcknowledged(PipelineCompileRecord r)
        {
            if (!r.submissionStarted || r.endedAt == null || r.httpStatus != 200
                || !PipelineCompileProtocol.ValidAcknowledgedData(r.data, r.acknowledgement)) throw new FormatException();
            // 所有应答仅确认Refresh调用，不能证明原请求生命周期完成，必须保持本地门。
            if (!r.busy || r.released) throw new FormatException();
        }

        private static void ValidateUnknown(PipelineCompileRecord r)
        {
            if (r.busy == r.released || string.IsNullOrWhiteSpace(r.errorCode) || string.IsNullOrWhiteSpace(r.error)) throw new FormatException();
            if (r.acknowledgement == null)
            {
                if (r.data != null) throw new FormatException();
                return;
            }
            // 重载可保留已收到的 compiling 应答，但不能升级成完成或编译成功。
            if (r.acknowledgement != "compiling" || !r.submissionStarted || r.httpStatus != 200 || r.endedAt == null
                || !PipelineCompileProtocol.ValidAcknowledgedData(r.data, "compiling")) throw new FormatException();
        }

        private static void ValidateUnacknowledged(PipelineCompileRecord r)
        {
            if (r.acknowledgement != null || r.data != null || r.httpStatus != 0) throw new FormatException();
            if ((r.status == "intent" || r.status == "submitting") && (r.errorCode != null || r.error != null)) throw new FormatException();
        }

        private static bool IsLater(PipelineCompileRecord left, PipelineCompileRecord right)
        {
            PipelineCompileProtocol.ValidUtc(left.queuedAt, out var leftTime);
            PipelineCompileProtocol.ValidUtc(right.queuedAt, out var rightTime);
            return leftTime > rightTime || (leftTime == rightTime && string.CompareOrdinal(left.requestId, right.requestId) > 0);
        }

        internal static bool IsRequestId(string value) { return PipelineTestStore.IsRequestId(value); }
        internal static bool IsIdentity(string value) { return PipelineTestStore.IsIdentity(value); }
        internal static bool IsGeneration(string value) { return PipelineTestStore.IsGeneration(value); }
        private static bool SamePath(string left, string right)
        { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        private static string Text(Dictionary<string, object> data, string field, bool nullable = false)
        {
            if (nullable && data[field] == null) return null;
            if (!(data[field] is string text) || text.Length > 32768) throw new FormatException();
            StrictUtf8.GetByteCount(text);
            return text;
        }
        private static bool Flag(Dictionary<string, object> data, string field)
        { if (!(data[field] is bool value)) throw new FormatException(); return value; }
        private static int Integer(Dictionary<string, object> data, string field)
        { if (!(data[field] is long value) || value < int.MinValue || value > int.MaxValue) throw new FormatException(); return checked((int)value); }

        private static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, StrictUtf8, false))
            {
                if (stream.Length > MaxRecordBytes) throw new FormatException();
                var buffer = new char[MaxRecordBytes + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = reader.Read(buffer, count, buffer.Length - count);
                    if (read == 0) break;
                    count += read;
                }
                if (count > MaxRecordBytes) throw new FormatException();
                var json = new string(buffer, 0, count);
                if (StrictUtf8.GetByteCount(json) > MaxRecordBytes) throw new FormatException();
                return json;
            }
        }

        private static bool Exists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        private static void RejectReparseAncestors(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse journals are unsupported."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
        }
    }
}
