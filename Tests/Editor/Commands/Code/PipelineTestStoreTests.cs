using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public class PipelineTestStoreTests
    {
        private string _root;
        private string _directory;
        private PipelineTestStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "AIBridgePipelineTestStore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _directory = Path.Combine(_root, ".aibridge", PipelineTestStore.DirectoryName);
            _store = new PipelineTestStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            // 仅删除本fixture创建的随机独立根，不触及真实项目journal。
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void MissingIsDistinctFromMalformedAndNoLatestIndexIsRequired()
        {
            Assert.That(_store.Load("missing", out var invalid), Is.Null); Assert.That(invalid, Is.False);
            Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            var record = Record("request_1"); _store.Save(record);
            Assert.That(_store.FindOutstanding(out invalid).requestId, Is.EqualTo("request_1")); Assert.That(invalid, Is.False);
            Assert.That(File.Exists(Path.Combine(_directory, "latest.json")), Is.False);
            File.WriteAllText(FileFor("request_1"), "{", Encoding.UTF8);
            Assert.That(_store.Load("request_1", out invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [Test]
        public void CompletedResultAndExplicitNullMembersRoundTrip()
        {
            var record = Record("request_1"); var outcome = PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner());
            record.status = outcome.status; record.success = outcome.success; record.busy = false;
            record.submissionStarted = true; record.executionEnded = true; record.targetIdentity = new string('b', 64);
            record.sessionGeneration = new string('c', 32); record.httpStatus = 200; record.data = outcome.data;
            record.endedAt = "2026-11-05T06:00:01Z";
            _store.Save(record);
            var loaded = _store.Load(record.requestId, out var invalid);
            Assert.That(invalid, Is.False); Assert.That(loaded.success, Is.True); Assert.That(loaded.busy, Is.False);
            Assert.That(_store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            var data = (Dictionary<string, object>)loaded.data; Assert.That(data["executionTimeMs"], Is.EqualTo(1000L));
            Assert.That(data.ContainsKey("innerWarnings") && data["innerWarnings"] == null, Is.True);
            var result = (Dictionary<string, object>)((List<object>)data["results"])[0];
            Assert.That(result.ContainsKey("StackTrace") && result["StackTrace"] == null, Is.True);
        }

        [Test]
        public void FailedAndZeroTestTerminalsArePersistedWithoutClaimingPass()
        {
            foreach (var outcome in new[] { PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner(0, 1)),
                PipelineTestProtocolTests.Parse(PipelineTestProtocolTests.Inner(0, 0)) })
            {
                var record = Record("request_" + outcome.status);
                record.status = outcome.status; record.success = false; record.busy = false; record.executionEnded = true;
                record.submissionStarted = true; record.targetIdentity = new string('b', 64); record.sessionGeneration = new string('c', 32);
                record.httpStatus = 200; record.endedAt = "2026-11-05T06:00:01Z"; record.data = outcome.data;
                _store.Save(record);
                var loaded = _store.Load(record.requestId, out var invalid);
                Assert.That(invalid, Is.False); Assert.That(loaded.success, Is.False);
            }
        }

        [Test]
        public void MissingFlagsWrongTypesDuplicateAndUnknownFieldsFailClosed()
        {
            var record = Record("request_1"); _store.Save(record);
            var original = File.ReadAllText(FileFor(record.requestId));
            foreach (var flag in new[] { "busy", "released", "submissionStarted", "executionEnded", "success" })
            {
                var data = AIBridgeJson.DeserializeObjectStrict(original); data.Remove(flag); Write(data); Invalid();
                data = AIBridgeJson.DeserializeObjectStrict(original); data[flag] = "false"; Write(data); Invalid();
            }
            var extra = AIBridgeJson.DeserializeObjectStrict(original); extra["token"] = "fixture"; Write(extra); Invalid();
            File.WriteAllText(FileFor("request_1"), original.Replace("\"busy\":true", "\"busy\":true,\"busy\":false")); Invalid();
        }

        [Test]
        public void ContradictoryFlagsCannotReleaseOrPromoteOperation()
        {
            var record = Record("request_1"); _store.Save(record);
            var original = File.ReadAllText(FileFor(record.requestId));
            foreach (var key in new[] { "released", "submissionStarted", "executionEnded", "success" })
            { var data = AIBridgeJson.DeserializeObjectStrict(original); data[key] = true; Write(data); Invalid(); }
            foreach (var status in new[] { "passed", "idle", "queued", "canceled" })
            { var data = AIBridgeJson.DeserializeObjectStrict(original); data["status"] = status; Write(data); Invalid(); }
            var unbusy = AIBridgeJson.DeserializeObjectStrict(original); unbusy["busy"] = false; Write(unbusy); Invalid();
        }

        [Test]
        public void FileIdentityAndProjectRootMustMatch()
        {
            _store.Save(Record("request_1")); var original = File.ReadAllText(FileFor("request_1"));
            var data = AIBridgeJson.DeserializeObjectStrict(original); data["requestId"] = "request_2"; Write(data); Invalid();
            data = AIBridgeJson.DeserializeObjectStrict(original); data["projectPath"] = Path.Combine(_root, "other"); Write(data); Invalid();
            foreach (var field in new[] { "generation", "queuedAt", "filterType", "unityVersion" })
            { data = AIBridgeJson.DeserializeObjectStrict(original); data[field] = null; Write(data); Invalid(); }
        }

        [Test]
        public void MultipleOutstandingOrUnrecognizedJsonFilenameBlocksScan()
        {
            _store.Save(Record("request_1")); _store.Save(Record("request_2"));
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.True);
            Assert.That(_store.FindLatest(out invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [Test]
        public void ReleasedUnknownRemainsRegisteredAndIsNotOutstanding()
        {
            var record = Record("request_1"); record.status = "unknown"; record.released = true; record.busy = false;
            record.errorCode = "pipelineTestStateLost"; record.error = "unknown"; _store.Save(record);
            Assert.That(_store.FindOutstanding(out var invalid), Is.Null); Assert.That(invalid, Is.False);
            var loaded = _store.Load(record.requestId, out invalid);
            Assert.That(loaded.released, Is.True); Assert.That(loaded.status, Is.EqualTo("unknown"));
            Assert.That(loaded.success, Is.Null); Assert.That(loaded.executionEnded, Is.False);
        }

        [Test]
        public void OversizedOrDirectoryInPlaceOfFileIsUnreadable()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FileFor("request_1"), new string('x', 2 * 1024 * 1024 + 1)); Invalid();
            Directory.CreateDirectory(FileFor("request_2"));
            Assert.That(_store.Load("request_2", out var invalid), Is.Null); Assert.That(invalid, Is.True);
        }

        [TestCase("../escape")] [TestCase("x.json")] [TestCase("a/b")] [TestCase(@"a\b")] [TestCase("")] [TestCase(null)]
        public void InvalidRequestIdCannotAddressFilesystem(string id)
        { Assert.That(_store.Load(id, out var invalid), Is.Null); Assert.That(invalid, Is.True); }

        [Test]
        public void RecordCapacityRejectsNewIdAndAllowsExistingExactRelease()
        {
            var store = new PipelineTestStore(_root, 2);
            foreach (var id in new[] { "one", "two" })
            {
                store.RequireCapacityForNewRequest();
                var record = Record(id); record.status = "unknown"; record.errorCode = "unknown"; record.error = "unknown";
                record.released = true; record.busy = false; store.Save(record);
            }
            Assert.Throws<IOException>(() => store.RequireCapacityForNewRequest());
            Assert.Throws<IOException>(() => store.Save(Record("three")));
            var existing = store.Load("two", out var invalid); existing.released = false; existing.busy = true; store.Save(existing);
            Assert.That(store.FindOutstanding(out invalid).requestId, Is.EqualTo("two")); Assert.That(invalid, Is.False);
            existing.released = true; existing.busy = false; store.Save(existing);
            Assert.That(store.FindOutstanding(out invalid), Is.Null); Assert.That(invalid, Is.False);
            Assert.That(File.Exists(FileFor("three")), Is.False);
        }

        [Test]
        public void WireProjectionKeepsUnknownNullsAndUtcText()
        {
            var record = Record("request_wire");
            var data = AIBridgeJson.DeserializeObjectStrict(AIBridgeJson.Serialize(PipelineTestStore.ToData(record)));
            foreach (var key in new[] { "success", "targetIdentity", "sessionGeneration", "endedAt", "error", "errorCode", "data" })
            { Assert.That(data.ContainsKey(key), Is.True, key); Assert.That(data[key], Is.Null, key); }
            Assert.That(data["queuedAt"], Is.EqualTo("2026-11-05T06:00:00Z"));
        }

        private PipelineTestRecord Record(string id)
        {
            return new PipelineTestRecord { requestId = id, projectPath = _root, editorPid = 1234, unityVersion = "6000.0.51f1",
                generation = new string('a', 32), queuedAt = "2026-11-05T06:00:00Z", filter = PipelineTestProtocolTests.Options.Filter,
                filterType = PipelineTestProtocolTests.Options.FilterType, timeoutMs = PipelineTestProtocolTests.Options.TimeoutMs };
        }
        private string FileFor(string id) { return Path.Combine(_directory, id + ".json"); }
        private void Write(Dictionary<string, object> data) { File.WriteAllText(FileFor("request_1"), PipelineTestProtocolTests.Json(data)); }
        private void Invalid() { Assert.That(_store.Load("request_1", out var invalid), Is.Null); Assert.That(invalid, Is.True); }
    }
}
