using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;

namespace AIBridge.Editor
{
    /// <summary>只存纯 DTO；单请求文件为事实来源，不依赖可能丢失的 latest 索引。</summary>
    internal sealed class PipelineTestStore : IPipelineTestJournal
    {
        internal const string DirectoryName = "pipeline-test-runs";
        private const int MaxRecords = 4096;
        private const int MaxRecordBytes = 2 * 1024 * 1024;
        private readonly string _root;
        private readonly string _directory;
        private readonly int _recordLimit;
        private static readonly string[] Fields =
        {
            "schema", "backend", "requestId", "status", "busy", "released", "submissionStarted", "executionEnded", "success",
            "projectPath", "editorPid", "unityVersion", "generation", "targetIdentity", "sessionGeneration", "queuedAt", "endedAt",
            "filter", "filterType", "timeoutMs", "httpStatus", "errorCode", "error", "data"
        };

        internal PipelineTestStore(string projectRoot, int recordLimit = MaxRecords)
        {
            if (recordLimit < 1 || recordLimit > MaxRecords) throw new ArgumentOutOfRangeException(nameof(recordLimit));
            _recordLimit = recordLimit;
            _root = Path.GetFullPath(projectRoot);
            _directory = Path.Combine(_root, ".aibridge", DirectoryName);
        }

        public PipelineTestRecord Load(string requestId, out bool unreadable)
        {
            unreadable = false;
            if (!IsRequestId(requestId)) { unreadable = true; return null; }
            try
            {
                var path = Path.Combine(_directory, requestId + ".json");
                RejectReparseAncestors(path);
                if (!Exists(path)) return null;
                var info = new FileInfo(path);
                if (info.Length > MaxRecordBytes) throw new FormatException();
                var json = ReadBounded(path);
                if (Encoding.UTF8.GetByteCount(json) > MaxRecordBytes) throw new FormatException();
                var record = Decode(json);
                if (record.requestId != requestId || !SamePath(record.projectPath, _root)) throw new FormatException();
                return record;
            }
            catch (Exception) { unreadable = true; return null; }
        }

        public PipelineTestRecord FindLatest(out bool unreadable) { return Scan(false, out unreadable); }
        public PipelineTestRecord FindOutstanding(out bool unreadable) { return Scan(true, out unreadable); }

        private PipelineTestRecord Scan(bool outstanding, out bool unreadable)
        {
            unreadable = false;
            try
            {
                RejectReparseAncestors(_directory);
                if (!Exists(_directory)) return null;
                if (!Directory.Exists(_directory)) throw new IOException();
                PipelineTestRecord latest = null;
                PipelineTestRecord busy = null;
                var count = 0;
                foreach (var file in Directory.EnumerateFileSystemEntries(_directory, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (++count > _recordLimit) throw new IOException();
                    var record = Load(Path.GetFileNameWithoutExtension(file), out var invalid);
                    if (invalid || record == null) throw new IOException();
                    if (record.busy)
                    {
                        if (busy != null) throw new IOException();
                        busy = record;
                    }
                    if (latest == null || IsLater(record, latest)) latest = record;
                }
                return outstanding ? busy : busy ?? latest;
            }
            catch (Exception) { unreadable = true; return null; }
        }

        public void RequireCapacityForNewRequest()
        {
            RejectReparseAncestors(_directory);
            if (!Exists(_directory)) return;
            if (!Directory.Exists(_directory)) throw new IOException("Pipeline test journal directory is unavailable.");
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
                if (++count >= _recordLimit) throw new IOException("Pipeline test journal capacity reached; no intent or POST was started.");
        }

        public void Save(PipelineTestRecord record)
        {
            var json = Encode(record);
            var verified = Decode(json);
            if (!SamePath(verified.projectPath, _root) || Encoding.UTF8.GetByteCount(json) > MaxRecordBytes) throw new FormatException();
            var path = Path.Combine(_directory, verified.requestId + ".json");
            RejectReparseAncestors(path);
            Directory.CreateDirectory(_directory);
            RejectReparseAncestors(path);
            // 已有ID终态/释放不受新建容量门影响，禁止新增导致自身扫描超限。
            if (!Exists(path)) RequireCapacityForNewRequest();
            AIBridgeAtomicFile.WriteTextAtomic(path, json, new UTF8Encoding(false), false, true);
        }

        internal static PipelineTestRecord Clone(PipelineTestRecord record) { return Decode(Encode(record)); }

        private static string Encode(PipelineTestRecord r) { return AIBridgeJson.Serialize(ToData(r)); }

        internal static object ToData(PipelineTestRecord r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            var data = new Dictionary<string, object>
            {
                { "schema", r.schema }, { "backend", r.backend }, { "requestId", r.requestId }, { "status", r.status },
                { "busy", r.busy }, { "released", r.released }, { "submissionStarted", r.submissionStarted },
                { "executionEnded", r.executionEnded }, { "success", r.success }, { "projectPath", r.projectPath },
                { "editorPid", r.editorPid }, { "unityVersion", r.unityVersion }, { "generation", r.generation },
                { "targetIdentity", r.targetIdentity }, { "sessionGeneration", r.sessionGeneration }, { "queuedAt", r.queuedAt },
                { "endedAt", r.endedAt }, { "filter", r.filter }, { "filterType", r.filterType }, { "timeoutMs", r.timeoutMs },
                { "httpStatus", r.httpStatus }, { "errorCode", r.errorCode }, { "error", r.error }, { "data", r.data }
            };
            return AIBridgeJson.PreserveObjectNulls(data);
        }

        private static PipelineTestRecord Decode(string json)
        {
            var d = AIBridgeJson.DeserializeObjectStrict(json, 24);
            if (d == null || d.Count != Fields.Length) throw new FormatException();
            foreach (var key in Fields) if (!d.ContainsKey(key)) throw new FormatException();
            if (!Equals(d["schema"], 1L) || !Equals(d["backend"], "pipeline")) throw new FormatException();
            var r = new PipelineTestRecord
            {
                requestId = Text(d, "requestId"), status = Text(d, "status"), busy = Flag(d, "busy"), released = Flag(d, "released"),
                submissionStarted = Flag(d, "submissionStarted"), executionEnded = Flag(d, "executionEnded"),
                projectPath = Text(d, "projectPath"), editorPid = Integer(d, "editorPid"), unityVersion = Text(d, "unityVersion"),
                generation = Text(d, "generation"), targetIdentity = Text(d, "targetIdentity", true),
                sessionGeneration = Text(d, "sessionGeneration", true), queuedAt = Text(d, "queuedAt"), endedAt = Text(d, "endedAt", true),
                filter = Text(d, "filter"), filterType = Text(d, "filterType"), timeoutMs = Integer(d, "timeoutMs"),
                httpStatus = Integer(d, "httpStatus"), errorCode = Text(d, "errorCode", true), error = Text(d, "error", true),
                data = AIBridgeJson.PreserveObjectNulls(d["data"])
            };
            if (d["success"] != null && !(d["success"] is bool)) throw new FormatException();
            r.success = d["success"] as bool?;
            Validate(r);
            return r;
        }

        private static void Validate(PipelineTestRecord r)
        {
            if (!IsRequestId(r.requestId) || !IsGeneration(r.generation) || r.editorPid <= 0 || r.unityVersion.Length == 0
                || r.unityVersion.Length > 256 || !Path.IsPathRooted(r.projectPath) || r.projectPath.Length > 32768
                || !PipelineTestProtocol.ValidUtc(r.queuedAt, out var queued) || r.httpStatus < 0 || r.httpStatus > 599) throw new FormatException();
            var options = new PipelineTestOptions(r.filter, r.filterType, r.timeoutMs);
            if (r.endedAt != null && (!PipelineTestProtocol.ValidUtc(r.endedAt, out var ended) || ended < queued)) throw new FormatException();
            if (r.targetIdentity != null && !IsIdentity(r.targetIdentity)) throw new FormatException();
            if (r.sessionGeneration != null && !IsGeneration(r.sessionGeneration)) throw new FormatException();
            if (r.submissionStarted && (r.targetIdentity == null || r.sessionGeneration == null)) throw new FormatException();
            if (r.released && (r.status != "unknown" || r.busy)) throw new FormatException();
            if (PipelineTestEngine.IsTerminal(r.status))
            {
                if (r.busy || r.released || !r.submissionStarted || !r.executionEnded || r.success != (r.status == "passed")
                    || r.endedAt == null || r.httpStatus != 200 || !PipelineTestProtocol.ValidCompletedData(r.data, options, r.status)) throw new FormatException();
                return;
            }
            if (r.success != null || r.executionEnded || r.data != null) throw new FormatException();
            switch (r.status)
            {
                case "intent":
                    if (!r.busy || r.released || r.submissionStarted || r.targetIdentity != null || r.sessionGeneration != null || r.endedAt != null) throw new FormatException();
                    break;
                case "running":
                    if (!r.busy || r.released || !r.submissionStarted || r.endedAt != null) throw new FormatException();
                    break;
                case "unknown":
                    if (r.busy == r.released || string.IsNullOrWhiteSpace(r.errorCode) || string.IsNullOrWhiteSpace(r.error)) throw new FormatException();
                    break;
                case "not_submitted":
                    if (r.busy || r.released || r.submissionStarted || r.endedAt == null) throw new FormatException();
                    break;
                default: throw new FormatException();
            }
        }

        private static bool IsLater(PipelineTestRecord left, PipelineTestRecord right)
        {
            PipelineTestProtocol.ValidUtc(left.queuedAt, out var leftTime);
            PipelineTestProtocol.ValidUtc(right.queuedAt, out var rightTime);
            return leftTime > rightTime || (leftTime == rightTime && string.CompareOrdinal(left.requestId, right.requestId) > 0);
        }

        internal static bool IsRequestId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (var c in value) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }
        internal static bool IsGeneration(string value) { return Hex(value, 32); }
        internal static bool IsIdentity(string value) { return Hex(value, 64); }
        private static bool Hex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (var c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
        private static bool SamePath(string left, string right)
        { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        private static string Text(Dictionary<string, object> d, string key, bool nullable = false)
        {
            if (d[key] == null && nullable) return null;
            if (!(d[key] is string value) || value.Length > 32768) throw new FormatException();
            return value;
        }
        private static bool Flag(Dictionary<string, object> d, string key)
        { if (!(d[key] is bool value)) throw new FormatException(); return value; }
        private static int Integer(Dictionary<string, object> d, string key)
        { if (!(d[key] is long value) || value < int.MinValue || value > int.MaxValue) throw new FormatException(); return checked((int)value); }
        private static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
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
                return new string(buffer, 0, count);
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
