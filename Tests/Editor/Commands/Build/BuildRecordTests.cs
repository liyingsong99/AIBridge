using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AIBridge.Internal.Json;
using AIBridge.Runtime.Internal;
using NUnit.Framework;

namespace AIBridge.Editor.Tests
{
    public sealed class BuildRecordTests
    {
        private const string BuildId = "0123456789abcdef0123456789abcdef";
        private const string Time = "2026-11-05T01:02:03.1234567Z";
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "aibridge-build-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void RecordPathUsesSha256AndDoesNotCreateDirectories()
        {
            var path = AIBridgeBuildStore.RecordPath(_root, "abc");
            Assert.That(Path.GetFileName(path), Is.EqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad.json"));
            Assert.That(Path.GetFileName(AIBridgeBuildStore.RecordPath(_root, null)), Is.EqualTo("latest.json"));
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(Path.Combine(_root, ".aibridge", "build-operations")));
            Assert.That(Directory.Exists(Path.Combine(_root, ".aibridge")), Is.False);
        }

        [Test]
        public void MissingRecordIsNotUnreadableAndDoesNotWrite()
        {
            Assert.That(AIBridgeBuildStore.Load(_root, "missing", out var bad), Is.Null);
            Assert.That(bad, Is.False);
            Assert.That(AIBridgeBuildStore.Load(_root, null, out bad), Is.Null);
            Assert.That(bad, Is.False);
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [TestCase("queued")]
        [TestCase("building")]
        [TestCase("completed")]
        [TestCase("unknown")]
        public void SaveLoadRoundTripsEachStateAndLatest(string status)
        {
            var record = Record(status);
            Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.True);
            AIBridgeBuildStore.Save(_root, record);
            var loaded = AIBridgeBuildStore.Load(_root, record.requestId, out var bad);
            Assert.That(bad, Is.False); Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded.status, Is.EqualTo(status)); Assert.That(loaded.success, Is.EqualTo(record.success));
            var latest = AIBridgeBuildStore.Load(_root, null, out bad);
            Assert.That(bad, Is.False); Assert.That(latest.requestId, Is.EqualTo(record.requestId));
            Assert.That(Directory.Exists(Path.Combine(_root, ".aibridge", "builds")), Is.False);
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(PathFor(record.requestId))), Has.Length.EqualTo(2));
        }

        [Test]
        public void MissingLatestStillFindsBusySingleRecord()
        {
            var record = Record("queued"); AIBridgeBuildStore.Save(_root, record);
            File.Delete(AIBridgeBuildStore.RecordPath(_root, null));
            var found = AIBridgeBuildStore.Load(_root, null, out var bad);
            Assert.That(bad, Is.False); Assert.That(found, Is.Not.Null); Assert.That(found.requestId, Is.EqualTo(record.requestId));
            Assert.That(found.busy, Is.True);
        }

        [Test]
        public void AuthoritativeReleasedSingleDoesNotReviveStaleBusyIndex()
        {
            var record = Record("unknown"); AIBridgeBuildStore.Save(_root, record);
            var old = File.ReadAllBytes(AIBridgeBuildStore.RecordPath(_root, null));
            record.released = true; record.busy = false; AIBridgeBuildStore.Save(_root, record);
            File.WriteAllBytes(AIBridgeBuildStore.RecordPath(_root, null), old);
            var found = AIBridgeBuildStore.Load(_root, null, out var bad);
            Assert.That(bad, Is.False); Assert.That(found.released, Is.True); Assert.That(found.busy, Is.False);
        }

        [Test]
        public void MultipleBusyRecordsNeverBecomeIdleOrChooseOne()
        {
            var record = Record("queued"); AIBridgeBuildStore.Save(_root, record);
            record.requestId = "second"; AIBridgeBuildStore.Save(_root, record);
            Assert.That(AIBridgeBuildStore.Load(_root, null, out var bad), Is.Null); Assert.That(bad, Is.True);
        }

        [Test]
        public void UnknownReleaseOnlyChangesLocalBusyAndNeverInfersResult()
        {
            var record = Record("unknown");
            record.startedAt = null; record.invocationStarted = false;
            AIBridgeBuildStore.Save(_root, record);
            record.released = true; record.busy = false;
            AIBridgeBuildStore.Save(_root, record);
            var loaded = AIBridgeBuildStore.Load(_root, record.requestId, out var bad);
            Assert.That(bad, Is.False); Assert.That(loaded.released, Is.True);
            Assert.That(loaded.success, Is.Null); Assert.That(loaded.report, Is.Null);
            Assert.That(loaded.endedAt, Is.Null); Assert.That(loaded.invocationStarted, Is.False);
            record.busy = true; Invalid(record);
            record.busy = false; record.success = false; Invalid(record);
        }

        [Test]
        public void QueuedAndBuildingRejectContradictoryFacts()
        {
            foreach (var status in new[] { "queued", "building" })
            {
                foreach (var change in new Action<AIBridgeBuildRecord>[]
                {
                    r => r.success = false, r => r.busy = false, r => r.released = true,
                    r => r.report = Report(r), r => r.endedAt = Time
                })
                {
                    var record = Record(status); change(record); Invalid(record);
                }
            }
            var queued = Record(); queued.invocationStarted = true; Invalid(queued);
            queued = Record(); queued.startedAt = Time; Invalid(queued);
            var building = Record("building"); building.startedAt = null; Invalid(building);
            building = Record("building"); building.invocationStarted = false; Invalid(building);
        }

        [Test]
        public void CompletedRequiresFullTerminalFacts()
        {
            foreach (var change in new Action<AIBridgeBuildRecord>[]
            {
                r => r.busy = true, r => r.released = true, r => r.invocationStarted = false,
                r => r.startedAt = null, r => r.endedAt = null, r => r.success = null,
                r => r.report = null, r => r.success = false, r => r.report.result = "validated",
                r => r.report.totalErrors = 1, r => r.endedAt = "2026-11-05T01:01:00Z"
            })
            {
                var record = Record("completed"); change(record); Invalid(record);
            }
        }

        [TestCase("Failed")]
        [TestCase("Cancelled")]
        [TestCase("Unknown")]
        public void CompletedNonSuccessRequiresFalseAndError(string result)
        {
            var record = Record("completed"); record.report.result = result;
            Invalid(record); record.success = false; Invalid(record);
            record.error = "Build did not succeed.";
            Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.True);
            AIBridgeBuildStore.Save(_root, record);
            Assert.That(AIBridgeBuildStore.Load(_root, record.requestId, out var bad).success, Is.False);
            Assert.That(bad, Is.False);
        }

        [Test]
        public void UnknownRequiresDiagnosticsAndExplicitInvocationFacts()
        {
            foreach (var change in new Action<AIBridgeBuildRecord>[]
            {
                r => r.error = null, r => r.errorCode = "", r => r.busy = false,
                r => r.success = false, r => r.report = Report(r), r => r.invocationStarted = false
            })
            {
                var record = Record("unknown"); change(record); Invalid(record);
            }
            var unknown = Record("unknown"); unknown.endedAt = Time;
            Assert.That(AIBridgeBuildStore.Validate(unknown, _root), Is.True);
        }

        [TestCase("2026-11-05T01:02:03Z", true)]
        [TestCase("2026-11-05T01:02:03+00:00", true)]
        [TestCase("2026-11-05T09:02:03+08:00", false)]
        [TestCase("2026-11-05T01:02:03", false)]
        [TestCase("2026-11-05", false)]
        [TestCase("invalid", false)]
        public void TimestampsMustBeIsoUtc(string time, bool expected)
        {
            var record = Record(); record.queuedAt = time;
            Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.EqualTo(expected));
        }

        [Test]
        public void IdentityAndSceneBoundsFailClosed()
        {
            foreach (var change in new Action<AIBridgeBuildRecord>[]
            {
                r => r.schemaVersion = 2, r => r.requestId = "", r => r.requestId = "x\n",
                r => r.requestId = new string('x', 257), r => r.buildId = BuildId.ToUpperInvariant(),
                r => r.generation = "bad", r => r.editorPid = 0, r => r.unityVersion = null,
                r => r.target = "", r => r.error = new string('x', 8193), r => r.scenes = null,
                r => r.scenes.Clear(), r => r.scenes = new List<string>(new string[129]),
                r => r.scenes[0] = "Packages/Test.unity", r => r.scenes[0] = "Assets/../Test.unity",
                r => r.scenes[0] = "Assets/./Test.unity", r => r.scenes[0] = "Assets//Test.unity",
                r => r.scenes[0] = "Assets\\Test.unity", r => r.scenes[0] = "Assets/Test.prefab",
                r => r.scenes[0] = "Assets/Test\n.unity"
            })
            {
                var record = Record(); change(record); Invalid(record);
            }
            Assert.That(AIBridgeBuildStore.Validate(null, _root), Is.False);
        }

        [Test]
        public void OutputAndProjectIdentityStayInsideTheirExactRoot()
        {
            foreach (var path in new[]
            {
                Path.Combine(_root, ".aibridge", "builds", BuildId),
                Path.Combine(_root, ".aibridge", "builds", BuildId + "0", "x.exe"),
                Path.Combine(_root, ".aibridge", "builds", BuildId, "..", "x.exe"),
                Path.Combine(_root, "other.exe"), "relative.exe"
            })
            {
                var record = Record(); record.outputPath = path; Invalid(record);
            }
            var crossRoot = Record(); crossRoot.projectPath = _root + "-other"; Invalid(crossRoot);
            Assert.That(AIBridgeBuildStore.Validate(Record(), _root + "-other"), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_root, ".aibridge")), Is.False);
            var valid = Record(); valid.outputPath = Path.Combine(Path.GetDirectoryName(valid.outputPath), "app", "contents");
            Assert.That(AIBridgeBuildStore.Validate(valid, _root), Is.True);
        }

        [Test]
        public void WindowsDeviceAliasesAndTrailingDotsAreRejected()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path aliases fixture.");
            foreach (var name in new[] { "NUL", "CON.exe", "COM1", "LPT9.txt", "Player.exe.", "Player.exe ", "Player.exe:stream" })
            {
                var record = Record(); record.outputPath = Path.Combine(_root, ".aibridge", "builds", BuildId, name);
                Invalid(record);
            }
        }

        [Test]
        public void RootComparisonUsesPlatformCaseRules()
        {
            var record = Record(); record.projectPath = _root.ToUpperInvariant();
            Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.EqualTo(Path.DirectorySeparatorChar == '\\'));
        }

        [Test]
        public void ReportCountsIdentityAndBoundsAreValidated()
        {
            foreach (var change in new Action<AIBridgeBuildReport>[]
            {
                r => r.target = "Other", r => r.outputPath += "other", r => r.totalSizeBytes = -1,
                r => r.durationMs = -1, r => r.totalErrors = -1, r => r.totalWarnings = -1,
                r => r.fileCount = 0, r => r.stepCount = 0, r => r.fileCount = 2,
                r => r.steps = null, r => r.files = null, r => r.files[0].sizeBytes = -1,
                r => r.files[0].role = null, r => r.steps[0].depth = -1, r => r.steps[0].durationMs = -1,
                r => r.steps[0].messages = null, r => r.steps[0].messages[0].message = null,
                r => r.files = new List<AIBridgeBuildFile>(new AIBridgeBuildFile[257]),
                r => r.steps = new List<AIBridgeBuildStep>(new AIBridgeBuildStep[129]),
                r => r.steps[0].messages = new List<AIBridgeBuildMessage>(new AIBridgeBuildMessage[33])
            })
            {
                var record = Record("completed"); change(record.report); Invalid(record);
            }
            var truncated = Record("completed"); truncated.report.truncated = true;
            truncated.report.fileCount = 1000; truncated.report.stepCount = 200;
            Assert.That(AIBridgeBuildStore.Validate(truncated, _root), Is.True);
        }

        [Test]
        public void ExplicitNullsAreWrittenAndEveryRootFieldIsRequired()
        {
            var data = Data();
            foreach (var name in new[] { "success", "report", "startedAt", "endedAt", "errorCode", "error" })
            {
                Assert.That(data.ContainsKey(name), Is.True, name); Assert.That(data[name], Is.Null, name);
            }
            foreach (var field in new List<string>(data.Keys))
            {
                var copy = new Dictionary<string, object>(data); copy.Remove(field); WriteData(copy); Unreadable();
            }
            data["unexpected"] = true; WriteData(data); Unreadable();
        }

        [Test]
        public void ReportAndNestedFieldsAreRequiredAndUnknownFieldsRejected()
        {
            var data = Data("completed"); var report = (Dictionary<string, object>)data["report"];
            var file = (Dictionary<string, object>)((List<object>)report["files"])[0];
            var step = (Dictionary<string, object>)((List<object>)report["steps"])[0];
            var message = (Dictionary<string, object>)((List<object>)step["messages"])[0];
            foreach (var dto in new[] { report, file, step, message })
            {
                foreach (var field in new List<string>(dto.Keys))
                {
                    var value = dto[field]; dto.Remove(field); WriteData(data); Unreadable(); dto[field] = value;
                }
                dto["future"] = null; WriteData(data); Unreadable(); dto.Remove("future");
            }
        }

        [Test]
        public void FieldTypesCannotBeCoercedOrDefaulted()
        {
            var data = Data("completed"); var report = (Dictionary<string, object>)data["report"];
            foreach (var key in new[] { "schemaVersion", "editorPid", "success", "busy", "development", "scenes" })
            {
                var original = data[key]; data[key] = "0"; WriteData(data); Unreadable(); data[key] = original;
            }
            foreach (var key in new[] { "totalSizeBytes", "durationMs", "totalErrors", "totalWarnings", "fileCount", "stepCount", "truncated", "files", "steps" })
            {
                var original = report[key];
                foreach (var invalid in new object[] { null, "0", 0.5 })
                {
                    report[key] = invalid; WriteData(data); Unreadable();
                }
                report[key] = original;
            }
            var pid = data["editorPid"]; data["editorPid"] = long.MaxValue; WriteData(data); Unreadable(); data["editorPid"] = pid;
            data["target"] = 1; WriteData(data); Unreadable();
            data["target"] = null; WriteData(data); Unreadable();
        }

        [Test]
        public void OversizeDecodedListsAndStringsAreUnreadable()
        {
            for (var index = 0; index < 5; index++)
            {
                var data = Data("completed"); var report = (Dictionary<string, object>)data["report"];
                var step = (Dictionary<string, object>)((List<object>)report["steps"])[0];
                switch (index)
                {
                    case 0: data["scenes"] = new List<object>(new object[129]); break;
                    case 1: report["files"] = new List<object>(new object[257]); break;
                    case 2: report["steps"] = new List<object>(new object[129]); break;
                    case 3: step["messages"] = new List<object>(new object[33]); break;
                    default: data["error"] = new string('x', 8193); break;
                }
                WriteData(data); Unreadable();
            }
        }

        [Test]
        public void DuplicateKeysMalformedJsonDepthAndEncodingAreUnreadable()
        {
            var data = Data(); var json = Json(data);
            var path = PathFor("request");
            foreach (var invalid in new[]
            {
                "{", json + " true", json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0"),
                json.Replace("\"success\":null", "\"success\":false"),
                json.Replace("\"error\":null", "\"error\":\"\\ud800\""),
                "{\"future\":" + new string('[', 40) + "0" + new string(']', 40) + "}"
            })
            {
                File.WriteAllText(path, invalid); Unreadable();
            }
            File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0xff }); Unreadable();
        }

        [Test]
        public void OversizeFilesAndLockedIoRemainUnreadable()
        {
            Data(); var path = PathFor("request");
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength(4 * 1024 * 1024 + 1);
            Unreadable();
            if (Path.DirectorySeparatorChar != '\\') return;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) Unreadable();
        }

        [Test]
        public void WrongRequestedIdAndCrossRootContentAreUnreadable()
        {
            var data = Data(); data["requestId"] = "another"; WriteData(data); Unreadable();
            data = Data(); data["projectPath"] = _root + "-other"; WriteData(data); Unreadable();
            data = Data(); data["outputPath"] = Path.Combine(_root, "outside.exe"); WriteData(data); Unreadable();
            Assert.That(AIBridgeBuildStore.Load(_root, "bad\n", out var bad), Is.Null); Assert.That(bad, Is.True);
        }

        [Test]
        public void InvalidSaveLeavesBothPriorFilesUnchanged()
        {
            AIBridgeBuildStore.Save(_root, Record());
            var prior = File.ReadAllBytes(PathFor("request")); var latest = File.ReadAllBytes(PathFor(null));
            var invalid = Record(); invalid.success = true;
            Assert.Throws<ArgumentException>(() => AIBridgeBuildStore.Save(_root, invalid));
            Assert.That(File.ReadAllBytes(PathFor("request")), Is.EqualTo(prior));
            Assert.That(File.ReadAllBytes(PathFor(null)), Is.EqualTo(latest));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AtomicWriteFailurePreservesPriorAndNeverDeletesLogs(bool blockLatest)
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows sharing semantics fixture.");
            AIBridgeBuildStore.Save(_root, Record());
            var priorId = File.ReadAllBytes(PathFor("request")); var priorLatest = File.ReadAllBytes(PathFor(null));
            using (var held = new FileStream(PathFor(blockLatest ? null : "request"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() => AIBridgeBuildStore.Save(_root, Record("building")));
            }
            Assert.That(File.ReadAllBytes(PathFor(null)), Is.EqualTo(priorLatest));
            if (blockLatest)
                Assert.That(AIBridgeBuildStore.Load(_root, "request", out _).status, Is.EqualTo("building"));
            else Assert.That(File.ReadAllBytes(PathFor("request")), Is.EqualTo(priorId));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(PathFor(null)), "*.tmp.*"), Is.Empty);
        }

        [Test]
        public void OversizeSaveThrowsBeforePublishing()
        {
            var record = Record("completed"); record.report.files.Clear();
            for (var i = 0; i < 256; i++)
                record.report.files.Add(new AIBridgeBuildFile { path = new string('p', 8192), role = new string('r', 8192), sizeBytes = 0 });
            record.report.fileCount = 256;
            Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.True);
            Assert.Throws<InvalidDataException>(() => AIBridgeBuildStore.Save(_root, record));
            Assert.That(Directory.Exists(Path.Combine(_root, ".aibridge")), Is.False);
        }

        [Test]
        public void MaximumBoundedCollectionsRoundTripWithoutInventingCounts()
        {
            var record = Record("completed"); record.scenes.Clear(); record.report.files.Clear(); record.report.steps.Clear();
            for (var i = 0; i < 128; i++) record.scenes.Add("Assets/Scene" + i + ".unity");
            for (var i = 0; i < 256; i++)
                record.report.files.Add(new AIBridgeBuildFile { path = "file" + i, role = "", sizeBytes = 0 });
            for (var i = 0; i < 128; i++)
            {
                var step = new AIBridgeBuildStep { name = "step" + i, depth = i, durationMs = 0, messages = new List<AIBridgeBuildMessage>() };
                for (var j = 0; j < 32; j++) step.messages.Add(new AIBridgeBuildMessage { type = "Log", message = "" });
                record.report.steps.Add(step);
            }
            record.report.fileCount = 256; record.report.stepCount = 128; record.error = new string('e', 8192);
            AIBridgeBuildStore.Save(_root, record);
            var loaded = AIBridgeBuildStore.Load(_root, record.requestId, out var bad);
            Assert.That(bad, Is.False); Assert.That(loaded.scenes, Has.Count.EqualTo(128));
            Assert.That(loaded.report.files, Has.Count.EqualTo(256)); Assert.That(loaded.report.steps, Has.Count.EqualTo(128));
            Assert.That(loaded.report.steps[0].messages, Has.Count.EqualTo(32));
            Assert.That(loaded.report.truncated, Is.False); Assert.That(loaded.error.Length, Is.EqualTo(8192));
        }

        [Test]
        public void FileAncestorAndRecordDirectoryAreNotTreatedAsMissing()
        {
            var bridge = Path.Combine(_root, ".aibridge"); File.WriteAllText(bridge, "block");
            Assert.That(AIBridgeBuildStore.Load(_root, "request", out var bad), Is.Null); Assert.That(bad, Is.True);
            File.Delete(bridge);
            Directory.CreateDirectory(PathFor("request")); Unreadable();
        }

        [Test]
        public void ReparseOutputAndStoreAncestorsAreRejectedWhenSupported()
        {
            // 反射保持 Unity 2019.4 API 兼容；宿主没有链接 API/权限时明确跳过。
            var createLink = typeof(Directory).GetMethod("CreateSymbolicLink", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string), typeof(string) }, null);
            if (createLink == null) Assert.Ignore("Host does not expose managed symbolic-link creation.");
            var target = Path.Combine(_root, "link-target"); Directory.CreateDirectory(target);
            var bridge = Path.Combine(_root, ".aibridge");
            try { createLink.Invoke(null, new object[] { bridge, target }); }
            catch (TargetInvocationException exception) { Assert.Ignore("Link unavailable: " + exception.InnerException?.GetType().Name); }
            try
            {
                if ((File.GetAttributes(bridge) & FileAttributes.ReparsePoint) == 0) Assert.Ignore("Host does not report reparse attributes.");
                Invalid(Record());
                Assert.That(AIBridgeBuildStore.Load(_root, "request", out var bad), Is.Null); Assert.That(bad, Is.True);
                Assert.Throws<ArgumentException>(() => AIBridgeBuildStore.Save(_root, Record()));
                Assert.That(Directory.GetFileSystemEntries(target), Is.Empty);
            }
            finally { Directory.Delete(bridge); }
        }

        private AIBridgeBuildRecord Record(string status = "queued")
        {
            var record = new AIBridgeBuildRecord
            {
                requestId = "request", buildId = BuildId, projectPath = _root, unityVersion = "6000.0.51f1",
                generation = new string('a', 32), status = status, target = "StandaloneWindows64",
                outputPath = Path.Combine(_root, ".aibridge", "builds", BuildId, "Player.exe"),
                queuedAt = Time, editorPid = 123, busy = true, scenes = new List<string> { "Assets/Test.unity" }
            };
            if (status != "queued") { record.startedAt = Time; record.invocationStarted = true; }
            if (status == "completed")
            {
                record.endedAt = "2026-11-05T01:03:00Z"; record.busy = false; record.success = true; record.report = Report(record);
            }
            if (status == "unknown") { record.errorCode = "BUILD_RESULT_UNKNOWN"; record.error = "Build state lost."; }
            return record;
        }

        private static AIBridgeBuildReport Report(AIBridgeBuildRecord record)
        {
            return new AIBridgeBuildReport
            {
                result = "Succeeded", target = record.target, outputPath = record.outputPath, totalSizeBytes = 42,
                durationMs = 1234, totalErrors = 0, totalWarnings = 1, fileCount = 1, stepCount = 1,
                files = new List<AIBridgeBuildFile> { new AIBridgeBuildFile { path = record.outputPath, role = "Executable", sizeBytes = 42 } },
                steps = new List<AIBridgeBuildStep>
                {
                    new AIBridgeBuildStep
                    {
                        name = "Build", depth = 0, durationMs = 1234,
                        messages = new List<AIBridgeBuildMessage> { new AIBridgeBuildMessage { type = "Warning", message = "Test warning" } }
                    }
                }
            };
        }

        private Dictionary<string, object> Data(string status = "queued")
        {
            AIBridgeBuildStore.Save(_root, Record(status));
            return AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(PathFor("request")), 32);
        }

        private string PathFor(string id) => AIBridgeBuildStore.RecordPath(_root, id);
        private static string Json(Dictionary<string, object> data) => AIBridgeJson.Serialize(AIBridgeJson.PreserveObjectNulls(data));
        private void WriteData(Dictionary<string, object> data) => File.WriteAllText(PathFor("request"), Json(data), new UTF8Encoding(false));
        private void Invalid(AIBridgeBuildRecord record) => Assert.That(AIBridgeBuildStore.Validate(record, _root), Is.False);

        private void Unreadable()
        {
            Assert.That(AIBridgeBuildStore.Load(_root, "request", out var bad), Is.Null); Assert.That(bad, Is.True);
        }
    }
}
