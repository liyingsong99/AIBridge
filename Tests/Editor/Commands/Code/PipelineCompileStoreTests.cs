using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineCompileStoreTests
    {
        private string _root;
        private string _directory;
        private PipelineCompileStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AIBridgePipelineCompileStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _directory = Path.Combine(_root, ".aibridge", PipelineCompileStore.DirectoryName);
            _store = new PipelineCompileStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            // 只清理本 fixture 独有随机根目录，不触及项目实际日志。
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void MissingIsDistinctFromCorruptAndSingleIdFileNeedsNoLatestIndex()
        {
            Assert.That(_store.Load("missing", out var invalid), Is.Null); Assert.That(invalid, Is.False);
            Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            _store.Save(Record("one"));
            Assert.That(_store.FindLatest(out invalid).requestId, Is.EqualTo("one")); Assert.That(invalid, Is.False);
            Assert.That(_store.FindOutstanding(out invalid).requestId, Is.EqualTo("one")); Assert.That(invalid, Is.False);
            Assert.That(File.Exists(FileFor("latest")), Is.False);
            File.WriteAllText(FileFor("one"), "{");
            Assert.That(_store.Load("one", out invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.That(_store.FindLatest(out invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.Throws<IOException>(() => _store.RequireCapacityForNewRequest());
            Assert.Throws<IOException>(() => _store.Save(Record("one")));
        }

        [TestCase("up_to_date", true)] [TestCase("failed", true)] [TestCase("compiling", true)]
        public void AcknowledgementRoundTripsOnlyAsRefreshEvidence(string acknowledgement, bool busy)
        {
            var record = Acknowledged("one", acknowledgement); _store.Save(record);
            var loaded = _store.Load("one", out var invalid);
            Assert.That(invalid, Is.False); Assert.That(loaded.status, Is.EqualTo("acknowledged"));
            Assert.That(loaded.acknowledgement, Is.EqualTo(acknowledgement)); Assert.That(loaded.busy, Is.EqualTo(busy));
            var data = (Dictionary<string, object>)loaded.data;
            foreach (var field in new[] { "compilationSuccess", "errorCount", "warningCount", "duration", "warnings" })
            { Assert.That(data.ContainsKey(field), Is.True); Assert.That(data[field], Is.Null); }
            Assert.That(data["operationCorrelation"], Is.False);
            var outstanding = _store.FindOutstanding(out invalid); Assert.That(invalid, Is.False);
            Assert.That(outstanding != null, Is.EqualTo(busy));
        }

        [Test]
        public void CompilingAcknowledgementMayBeLocallyReleasedWithoutClaimingCompletion()
        {
            var record = Acknowledged("one", "compiling"); _store.Save(record);
            record.released = true; record.busy = false;
            Assert.Throws<FormatException>(() => _store.Save(record));
            record.status = "unknown"; record.acknowledgement = null; record.data = null;
            record.errorCode = "localRelease"; record.error = "Only locally released."; _store.Save(record);
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.False);
            var loaded = _store.Load("one", out invalid);
            Assert.That(loaded.status, Is.EqualTo("unknown")); Assert.That(loaded.acknowledgement, Is.Null);
            foreach (var acknowledgement in new[] { "up_to_date", "failed" })
            { record = Acknowledged("one", acknowledgement); record.released = true; Assert.Throws<FormatException>(() => _store.Save(record)); }
        }

        [Test]
        public void SubmittingRequiresCompleteBoundIdentityAndIntentRemainsUnbound()
        {
            var record = Record("one"); record.status = "submitting"; record.submissionStarted = true;
            Assert.Throws<FormatException>(() => _store.Save(record));
            Bind(record); _store.Save(record);
            Assert.That(_store.Load("one", out var invalid).submissionStarted, Is.True); Assert.That(invalid, Is.False);
            var bad = Record("two"); Bind(bad); Assert.Throws<FormatException>(() => _store.Save(bad));
            bad = Record("two"); bad.targetIdentity = new string('b', 64); Assert.Throws<FormatException>(() => _store.Save(bad));
        }

        [Test]
        public void UnknownKeepsBusyUntilExactReleaseAndMayPreserveOriginalCompilingReceipt()
        {
            var record = Unknown("one"); _store.Save(record);
            Assert.That(_store.FindOutstanding(out var invalid).requestId, Is.EqualTo("one")); Assert.That(invalid, Is.False);
            record.released = true; record.busy = false; _store.Save(record);
            Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            record = Acknowledged("two", "compiling"); record.status = "unknown";
            record.errorCode = "pipelineCompileStateLost"; record.error = "Unknown after reload."; _store.Save(record);
            var loaded = _store.Load("two", out invalid); Assert.That(invalid, Is.False);
            Assert.That(loaded.acknowledgement, Is.EqualTo("compiling")); Assert.That(loaded.busy, Is.True);
            foreach (var acknowledgement in new[] { "up_to_date", "failed" })
            { record = Acknowledged("two", acknowledgement); record.status = "unknown"; record.errorCode = "unknown"; record.error = "unknown";
                record.busy = true; Assert.Throws<FormatException>(() => _store.Save(record)); }
        }

        [Test]
        public void UnknownCannotKeepUnboundDataOrContradictReleaseFlags()
        {
            var record = Unknown("one"); record.data = new Dictionary<string, object>();
            Assert.Throws<FormatException>(() => _store.Save(record));
            record.data = null; record.released = true; Assert.Throws<FormatException>(() => _store.Save(record));
            record.released = false; record.busy = false; Assert.Throws<FormatException>(() => _store.Save(record));
            record.busy = true; record.errorCode = null; Assert.Throws<FormatException>(() => _store.Save(record));
            record.errorCode = "unknown"; record.error = null; Assert.Throws<FormatException>(() => _store.Save(record));
        }

        [Test]
        public void NotSubmittedRequiresEndAndCannotHaveStartedSubmission()
        {
            var record = Record("one"); record.status = "not_submitted"; record.busy = false;
            Assert.Throws<FormatException>(() => _store.Save(record));
            record.endedAt = "2026-11-05T06:00:01Z"; _store.Save(record);
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.False);
            Bind(record); record.submissionStarted = true; Assert.Throws<FormatException>(() => _store.Save(record));
        }

        [Test]
        public void ExactRecordSchemaRejectsMissingUnknownDuplicateAndWrongTypedFields()
        {
            _store.Save(Record("one")); var original = File.ReadAllText(FileFor("one"));
            var fields = AIBridgeJson.DeserializeObjectStrict(original);
            Assert.That(fields.Count, Is.EqualTo(21));
            foreach (var field in new List<string>(fields.Keys))
            { var data = AIBridgeJson.DeserializeObjectStrict(original); data.Remove(field); Write(data); Invalid(); }
            foreach (var field in new[] { "success", "executionEnded", "compilationSuccess", "token" })
            { var data = AIBridgeJson.DeserializeObjectStrict(original); data[field] = true; Write(data); Invalid(); }
            foreach (var field in new[] { "busy", "released", "submissionStarted", "schema", "timeoutMs", "httpStatus", "editorPid" })
            { var data = AIBridgeJson.DeserializeObjectStrict(original); data[field] = "1"; Write(data); Invalid(); }
            File.WriteAllText(FileFor("one"), original.Replace("\"busy\":true", "\"busy\":true,\"b\\u0075sy\":true")); Invalid();
        }

        [Test]
        public void TimeOrderAndUtcNullPolicyAreStrict()
        {
            foreach (var ended in new[] { "2026-11-05T05:59:59Z", "2026-11-05T06:00:01", "2026-11-05T06:00:01+08:00" })
            { var record = Acknowledged("one", "up_to_date"); record.endedAt = ended; Assert.Throws<FormatException>(() => _store.Save(record)); }
            var beforeEnd = Acknowledged("one", "up_to_date"); beforeEnd.endedAt = null;
            Assert.Throws<FormatException>(() => _store.Save(beforeEnd));
            var nullTime = Record("one"); nullTime.queuedAt = null; Assert.Throws<FormatException>(() => _store.Save(nullTime));
            var intent = Record("one"); var data = AIBridgeJson.DeserializeObjectStrict(PipelineCompileProtocolTests.Json(PipelineCompileStore.ToData(intent)));
            foreach (var field in new[] { "acknowledgement", "targetIdentity", "sessionGeneration", "endedAt", "error", "errorCode", "data" })
            { Assert.That(data.ContainsKey(field), Is.True); Assert.That(data[field], Is.Null); }
            Assert.That(data["queuedAt"], Is.EqualTo("2026-11-05T06:00:00Z"));
        }

        [Test]
        public void AcknowledgedIdentityHttpAndMetricsCannotBeForged()
        {
            foreach (var field in new[] { "targetIdentity", "sessionGeneration", "endedAt" })
            { var record = Acknowledged("one", "up_to_date"); var data = Projection(record); data[field] = null; Write(data); Invalid(); }
            foreach (var http in new[] { 0, 201, 500 })
            { var record = Acknowledged("one", "up_to_date"); record.httpStatus = http; Assert.Throws<FormatException>(() => _store.Save(record)); }
            var forged = Acknowledged("one", "up_to_date"); ((Dictionary<string, object>)forged.data)["compilationSuccess"] = true;
            Assert.Throws<FormatException>(() => _store.Save(forged));
            var invalidAck = Acknowledged("one", "up_to_date"); invalidAck.acknowledgement = "completed";
            Assert.Throws<FormatException>(() => _store.Save(invalidAck));
        }

        [Test]
        public void FileIdAndProjectPathMustMatchAndCorruptionBlocksBothScans()
        {
            _store.Save(Record("one")); var original = File.ReadAllText(FileFor("one"));
            var data = AIBridgeJson.DeserializeObjectStrict(original); data["requestId"] = "two"; Write(data); Invalid();
            data = AIBridgeJson.DeserializeObjectStrict(original); data["projectPath"] = Path.Combine(_root, "other"); Write(data); Invalid();
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.That(_store.FindLatest(out invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [Test]
        public void MultipleBusyOrUnrecognizedJsonFilenameFailsClosed()
        {
            _store.Save(Record("one")); _store.Save(Record("two"));
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.That(_store.FindLatest(out invalid), Is.Null); Assert.That(invalid, Is.True);
            File.WriteAllText(FileFor("bad.name"), "{}");
            Assert.That(_store.FindLatest(out invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [Test]
        public void CapacityPreflightRejectsNewIdButExistingReceiptAndReleaseRemainWritable()
        {
            var store = new PipelineCompileStore(_root, 2);
            var first = Unknown("one"); first.busy = false; first.released = true; store.Save(first);
            var second = Record("two"); store.RequireCapacityForNewRequest(); store.Save(second);
            Assert.Throws<IOException>(() => store.RequireCapacityForNewRequest());
            Assert.Throws<IOException>(() => store.Save(Record("three")));
            second = Acknowledged("two", "compiling"); store.Save(second);
            Assert.That(store.FindOutstanding(out var invalid).requestId, Is.EqualTo("two")); Assert.That(invalid, Is.False);
            second = Unknown("two"); second.released = true; second.busy = false; store.Save(second);
            Assert.That(store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            Assert.That(File.Exists(FileFor("three")), Is.False);
        }

        [Test]
        public void OversizedDeepInvalidUtf8AndDirectoryRecordsAreUnreadable()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FileFor("one"), new string('x', 2 * 1024 * 1024 + 1)); Invalid();
            File.WriteAllText(FileFor("one"), "{\"x\":" + new string('[', 25) + "0" + new string(']', 25) + "}"); Invalid();
            File.WriteAllBytes(FileFor("one"), new byte[] { 0xff, 0xfe, 0, 0 }); Invalid();
            Directory.CreateDirectory(FileFor("directory"));
            Assert.That(_store.Load("directory", out var invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [TestCase("../escape")] [TestCase("x.json")] [TestCase("a/b")] [TestCase(@"a\b")] [TestCase("")] [TestCase(null)]
        public void UnsafeRequestIdsNeverAddressFilesystem(string id)
        { Assert.That(_store.Load(id, out var invalid), Is.Null); Assert.That(invalid, Is.True); }

        [Test]
        public void ReparseAncestorIsRejectedWhenHostCanCreateIt()
        {
            var api = typeof(Directory).GetMethod("CreateSymbolicLink", new[] { typeof(string), typeof(string) });
            if (api == null) Assert.Ignore("Host profile has no symbolic-link creation API.");
            var target = Path.Combine(_root, "fixture-target"); Directory.CreateDirectory(target);
            var bridge = Path.Combine(_root, ".aibridge");
            try { api.Invoke(null, new object[] { bridge, target }); }
            catch (Exception) { Assert.Ignore("Host does not permit symbolic-link creation."); }
            try
            {
                Assert.That(_store.Load("one", out var invalid), Is.Null); Assert.That(invalid, Is.True);
                Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.True);
                Assert.Throws<IOException>(() => _store.Save(Record("one")));
                Assert.Throws<IOException>(() => _store.RequireCapacityForNewRequest());
                Assert.That(Directory.GetFileSystemEntries(target), Is.Empty);
            }
            finally { Directory.Delete(bridge); }
        }

        [Test]
        public void ScanStopsAtConfiguredLimitAndInvalidLimitsAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineCompileStore(_root, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineCompileStore(_root, 4097));
            foreach (var id in new[] { "one", "two", "three" })
            { var record = Unknown(id); record.busy = false; record.released = true; _store.Save(record); }
            var bounded = new PipelineCompileStore(_root, 2);
            Assert.That(bounded.FindLatest(out var invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [Test]
        public void CloneDoesNotShareMutableReceiptAndLatestUsesUtcTimeThenOrdinalId()
        {
            var original = Acknowledged("one", "up_to_date"); var clone = PipelineCompileStore.Clone(original);
            ((Dictionary<string, object>)clone.data)["compilationSuccess"] = true;
            Assert.That(((Dictionary<string, object>)original.data)["compilationSuccess"], Is.Null);
            original = Unknown("one"); original.busy = false; original.released = true;
            _store.Save(original); var later = Unknown("two"); later.busy = false; later.released = true; _store.Save(later);
            Assert.That(_store.FindLatest(out var invalid).requestId, Is.EqualTo("two")); Assert.That(invalid, Is.False);
        }

        private PipelineCompileRecord Record(string id)
        {
            return new PipelineCompileRecord { requestId = id, projectPath = _root, editorPid = 1234, unityVersion = "6000.0.51f1",
                generation = new string('a', 32), queuedAt = "2026-11-05T06:00:00Z", timeoutMs = 5000 };
        }
        private PipelineCompileRecord Acknowledged(string id, string acknowledgement)
        {
            var result = PipelineCompileProtocolTests.Parse(PipelineCompileProtocolTests.Outer(acknowledgement));
            var record = Record(id); Bind(record); record.submissionStarted = true; record.status = "acknowledged";
            record.acknowledgement = acknowledgement; record.busy = true; record.httpStatus = 200;
            record.endedAt = "2026-11-05T06:00:01Z"; record.data = result.data; record.errorCode = result.errorCode; record.error = result.error;
            return record;
        }
        private PipelineCompileRecord Unknown(string id)
        {
            var record = Record(id); record.status = "unknown"; record.errorCode = "pipelineCompileStateLost";
            record.error = "Unknown after reload; do not replay."; return record;
        }
        private static void Bind(PipelineCompileRecord record)
        { record.targetIdentity = new string('b', 64); record.sessionGeneration = new string('c', 32); }
        private static Dictionary<string, object> Projection(PipelineCompileRecord record)
        { return AIBridgeJson.DeserializeObjectStrict(PipelineCompileProtocolTests.Json(PipelineCompileStore.ToData(record))); }
        private string FileFor(string id) { return Path.Combine(_directory, id + ".json"); }
        private void Write(Dictionary<string, object> data)
        { Directory.CreateDirectory(_directory); File.WriteAllText(FileFor("one"), PipelineCompileProtocolTests.Json(data)); }
        private void Invalid()
        { Assert.That(_store.Load("one", out var invalid), Is.Null); Assert.That(invalid, Is.True); }
    }
}
