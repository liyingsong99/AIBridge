using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AIBridge.Internal.Json;
using NUnit.Framework;
using UnityEditor;

namespace AIBridge.Editor.Tests
{
    public sealed class CodePipelineOperationTests
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public;
        private const string SourcePath = ".aibridge/code/pipeline-operation-test.csx";
        private readonly Dictionary<string, byte[]> _fileBackups = new Dictionary<string, byte[]>();
        private FieldInfo _activeField;
        private FieldInfo _updateField;
        private object _savedActive;
        private Delegate _savedUpdate;
        private EditorApplication.CallbackFunction _asyncUpdate;
        private bool _isolated;
        private string _requestId;
        private string _resultPath;

        [SetUp]
        public void SetUp()
        {
            _fileBackups.Clear();
            _isolated = false;
            // 先取已初始化的 Bridge 路径，再快照回调，避免静态初始化订阅被误当成测试改动。
            var bridgeDirectory = AIBridge.BridgeDirectory;
            Assert.That(bridgeDirectory, Is.Not.Null.And.Not.Empty);
            _activeField = typeof(CodeCommand).GetField("_activeOperation", StaticPrivate);
            // update 在支持的 Editor 中是静态委托字段；若接口变化，先失败，绝不破坏原回调。
            _updateField = typeof(EditorApplication).GetField("update", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(_activeField, Is.Not.Null);
            Assert.That(_updateField, Is.Not.Null, "Cannot safely preserve EditorApplication.update on this Unity version.");
            _asyncUpdate = (EditorApplication.CallbackFunction)Delegate.CreateDelegate(
                typeof(EditorApplication.CallbackFunction), Method("OnAsyncUpdate"));
            _savedActive = _activeField.GetValue(null);
            _savedUpdate = (Delegate)_updateField.GetValue(null);
            _isolated = true;
            RemoveAsyncUpdate();
            _activeField.SetValue(null, null);

            _requestId = "pipeline-operation-test-" + Guid.NewGuid().ToString("N");
            _resultPath = Path.Combine(bridgeDirectory, "results", _requestId + ".json");
            BackupFile(RecordPath(null));
            BackupFile(RecordPath(_requestId));
            BackupFile(_resultPath);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                foreach (var backup in _fileBackups)
                {
                    if (backup.Value != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup.Key));
                        File.WriteAllBytes(backup.Key, backup.Value);
                    }
                    else if (File.Exists(backup.Key))
                    {
                        // 只删除已登记且原先不存在的本用例文件，不枚举或清空记录目录。
                        File.Delete(backup.Key);
                    }
                }
            }
            finally
            {
                if (_isolated)
                {
                    RemoveAsyncUpdate();
                    _activeField.SetValue(null, _savedActive);
                    _updateField.SetValue(null, _savedUpdate);
                    _isolated = false;
                }
            }
        }

        [Test]
        public void CompletedSuccess_ReleasesAndReturnsValidatedRecord()
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            completion.SetResult(new PipelineCodeResult { success = true, status = "validated", httpStatus = 200 });

            Assert.That(Step(operation, out var result, out var release), Is.True);
            Assert.That(release, Is.True);
            Assert.That(result.success, Is.True);
            Assert.That(result.id, Is.EqualTo(_requestId));
            AssertRecord(result.data, "validated", false, false, false);
            Assert.That(Value<bool>(result.data, "resultWritten"), Is.True);
            Assert.That(Value<int>(result.data, "httpStatus"), Is.EqualTo(200));
            AssertRecord(LoadRecord(), "validated", false, false, false);
            Assert.That(Step(operation, out result, out release), Is.True);
            Assert.That(result, Is.Null, "A completed operation must not emit its original result twice.");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ExecutedResult_AndLateResult_RoundTripsThroughPersistedStatus(bool timedOut, bool run)
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, timedOut ? 0 : int.MaxValue);
            var record = operation.GetType().GetField("_record", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(operation);
            Set(record, "dryRun", false);
            Set(record, "confirmed", true);
            Set(record, "operationKind", "execute");
            Set(record, "pipelineCommand", run ? "script run" : "script eval");
            Set(record, "entry", run ? "Fixture.Run" : null);
            Set(record, "argumentsSha256", run ? "fixture-args-hash" : null);
            byte[] timeoutResult = null;
            if (timedOut)
            {
                Method("OnAsyncUpdate").Invoke(null, null);
                timeoutResult = File.ReadAllBytes(_resultPath);
                Assert.That(completion.Task.IsCompleted, Is.False);
            }
            var expected = new Dictionary<string, object>
            {
                { "count", 42L }, { "items", new List<object> { "text", null, false, new Dictionary<string, object> { { "ratio", 1.25d } } } }
            };
            completion.SetResult(new PipelineCodeResult
            {
                success = true, status = "executed", httpStatus = 200, resultValue = expected,
                output = "captured output", message = "done", executionTimeMs = 23L
            });
            Method("OnAsyncUpdate").Invoke(null, null);
            Assert.That(_activeField.GetValue(null), Is.Null);
            if (timedOut) CollectionAssert.AreEqual(timeoutResult, File.ReadAllBytes(_resultPath));
            var status = Status();
            AssertRecord(status, "executed", false, false, false);
            Assert.That(AIBridgeJson.Serialize(Value<object>(status, "resultValue")), Is.EqualTo(AIBridgeJson.Serialize(expected)));
            Assert.That(Value<long?>(status, "executionTimeMs"), Is.EqualTo(23L));
            Assert.That(Value<string>(status, "output"), Is.EqualTo("captured output"));
            Assert.That(Value<bool>(status, "dryRun"), Is.False);
            Assert.That(Value<string>(status, "pipelineCommand"), Is.EqualTo(run ? "script run" : "script eval"));
            Assert.That(Value<string>(status, "entry"), Is.EqualTo(run ? "Fixture.Run" : null));
            Assert.That(Value<string>(status, "argumentsSha256"), Is.EqualTo(run ? "fixture-args-hash" : null));
            // 模拟 reload 后只剩磁盘记录；不得新建任务或丢失结构化返回值。
            Method("RecoverPipelineOperation").Invoke(null, null);
            Assert.That(_activeField.GetValue(null), Is.Null);
            Assert.That(AIBridgeJson.Serialize(Value<object>(Status(), "resultValue")), Is.EqualTo(AIBridgeJson.Serialize(expected)));
        }

        [TestCase(504, "pipelineResponseInvalid")]
        [TestCase(200, "pipelineRunTimeout")]
        public void CompletedUnknownTimeout_RetainsBusyAndMarksTimedOut(int httpStatus, string errorCode)
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            completion.SetResult(new PipelineCodeResult
            {
                status = "unknown", httpStatus = httpStatus, errorCode = errorCode, mayStillBeRunning = true
            });
            Step(operation, out var result, out var release);
            Assert.That(release, Is.False);
            var record = operation.GetType().GetField("_record", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(operation);
            Assert.That(Value<bool>(record, "timedOut"), Is.True);
            Assert.That(Value<bool>(record, "busy"), Is.True);
            Assert.That(Value<bool>(record, "mayStillBeRunning"), Is.True);
        }

        [TestCase("null")]
        [TestCase("0")]
        [TestCase("false")]
        [TestCase("[]")]
        public void PersistedExecutionValue_PreservesNullAndPrimitiveKinds(string resultJson)
        {
            var record = CreateRecord(5000);
            Set(record, "resultValue", AIBridgeJson.Deserialize(resultJson));
            Set(record, "executionTimeMs", null);
            Method("SavePipelineRecord").Invoke(null, new[] { record });
            var loaded = LoadRecord();
            Assert.That(AIBridgeJson.Serialize(Value<object>(loaded, "resultValue")), Is.EqualTo(resultJson));
            Assert.That(Value<long?>(loaded, "executionTimeMs"), Is.Null);
        }

        [Test]
        public void MissingLatestIndex_RecoversBusyPerRequestRecordWithoutReplay()
        {
            var record = CreateRecord(5000);
            var directory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RecordPath(null)), "..", "tmp", _requestId));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(RecordPath(_requestId)));
            BackupFile(path);
            File.WriteAllText(path, AIBridgeJson.Serialize(record));
            // 独立目录没有 latest；真实工程的损坏/历史日志不能污染本用例。
            var arguments = new object[] { directory, false };
            var loaded = Method("FindUnindexedPipelineRecord").Invoke(null, arguments);
            Assert.That(arguments[1], Is.EqualTo(false));
            Assert.That(loaded, Is.Not.Null);
            Assert.That(Value<string>(loaded, "requestId"), Is.EqualTo(_requestId));
            Method("ApplyRecoveredPipelineRecord").Invoke(null, new[] { loaded, arguments[1] });
            Assert.That(_activeField.GetValue(null), Is.Not.Null);
            Assert.That(_activeField.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            var active = _activeField.GetValue(null);
            var recovered = Invoke(active, "BuildStatusData");
            AssertRecord(recovered, "unknown", false, true, true);
            // 恢复可向原调用方写失败回执；文件存在不代表重放任务。
            if (File.Exists(_resultPath))
            {
                var failure = AIBridgeJson.DeserializeObject(File.ReadAllText(_resultPath));
                Assert.That(failure["success"], Is.EqualTo(false));
                Assert.That(failure["id"], Is.EqualTo(_requestId));
            }
            Assert.That(Value<string>(recovered, "errorCode"), Is.EqualTo("pipelineStateLost"));
            Assert.That(Step(active, out var replayResult, out var release), Is.False);
            Assert.That(replayResult, Is.Null);
            Assert.That(release, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UnindexedScan_UsesOnlyValidatedPerRequestRecords(bool busy)
        {
            var directory = Path.Combine(Path.GetDirectoryName(RecordPath(null)), "..", "tmp", _requestId);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(RecordPath(_requestId)));
            BackupFile(path);
            var record = CreateRecord(5000);
            Set(record, "active", busy);
            Set(record, "busy", busy);
            Set(record, "status", busy ? "running" : "validated");
            File.WriteAllText(path, AIBridgeJson.Serialize(record));
            var arguments = new object[] { directory, false };
            var loaded = Method("FindUnindexedPipelineRecord").Invoke(null, arguments);
            Assert.That(arguments[1], Is.False);
            if (busy) Assert.That(Value<string>(loaded, "requestId"), Is.EqualTo(_requestId));
            else Assert.That(loaded, Is.Null);
        }

        [Test]
        public void ContradictoryUnknownState_CannotReleaseBusyWithoutExplicitCancel()
        {
            var record = CreateRecord(5000);
            Set(record, "active", false);
            Set(record, "busy", false);
            Set(record, "mayStillBeRunning", true);
            Set(record, "status", "unknown");
            Method("SavePipelineRecord").Invoke(null, new[] { record });
            Method("RecoverPipelineOperation").Invoke(null, null);
            Assert.That(_activeField.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            var arguments = new object[] { _requestId, false };
            Assert.That(Method("LoadPipelineRecordWithStatus").Invoke(null, arguments), Is.Null);
            Assert.That(arguments[1], Is.True);
        }

        [Test]
        public void ExplicitNullObjectMembers_SurviveExecutionResultPersistence()
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            CreateOperation(completion, int.MaxValue);
            const string expected = "{\"a\":null,\"nested\":{\"b\":null},\"list\":[null,{\"c\":null}]}";
            completion.SetResult(new PipelineCodeResult
            {
                success = true, status = "validated", resultValue = AIBridgeJson.Deserialize(expected)
            });
            Method("OnAsyncUpdate").Invoke(null, null);
            var loaded = Value<object>(Status(), "resultValue");
            Assert.That(AIBridgeJson.Serialize(loaded), Is.EqualTo(expected));
            var data = AIBridgeJson.DeserializeObject(File.ReadAllText(RecordPath(_requestId)));
            Assert.That(((Dictionary<string, object>)data["resultValue"]).ContainsKey("a"), Is.True);
        }

        [TestCase("active")]
        [TestCase("busy")]
        [TestCase("mayStillBeRunning")]
        [TestCase("dryRun")]
        [TestCase("resultWritten")]
        [TestCase("status")]
        public void IncompletePersistedState_FailsClosed(string missingField)
        {
            var data = AIBridgeJson.DeserializeObject(AIBridgeJson.Serialize(CreateRecord(5000)));
            data.Remove(missingField);
            var path = RecordPath(_requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, AIBridgeJson.Serialize(data));
            var arguments = new object[] { _requestId, false };
            Assert.That(Method("LoadPipelineRecordWithStatus").Invoke(null, arguments), Is.Null);
            Assert.That(arguments[1], Is.True);
        }

        [TestCase(-1)]
        [TestCase("invalid")]
        public void InvalidPersistedExecutionTime_FailsClosed(object invalidTime)
        {
            var record = CreateRecord(5000);
            var data = AIBridgeJson.DeserializeObject(AIBridgeJson.Serialize(record));
            data["executionTimeMs"] = invalidTime;
            var path = RecordPath(_requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, AIBridgeJson.Serialize(data));
            var arguments = new object[] { _requestId, false };
            Assert.That(Method("LoadPipelineRecordWithStatus").Invoke(null, arguments), Is.Null);
            Assert.That(arguments[1], Is.True);
        }

        [Test]
        public void CompletedCompileFailure_PreservesDiagnosticsAndFillsMissingSourcePath()
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            completion.SetResult(new PipelineCodeResult
            {
                status = "compile_failed", httpStatus = 200, errorCode = "compilationFailed", error = "Compilation failed.",
                diagnostics = new List<PipelineCodeDiagnostic>
                {
                    new PipelineCodeDiagnostic { severity = "ERROR", code = "CS1002", message = "Expected semicolon", line = 3, column = 7 },
                    new PipelineCodeDiagnostic { severity = "warning", code = "CS0168", message = "Unused variable", file = "explicit.cs" }
                }
            });

            Assert.That(Step(operation, out var result, out var release), Is.True);
            Assert.That(release, Is.True);
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Is.EqualTo("Compilation failed."));
            AssertRecord(result.data, "compile_failed", false, false, false);
            Assert.That(Value<string>(result.data, "errorCode"), Is.EqualTo("compilationFailed"));
            CollectionAssert.AreEqual(new[] { "Expected semicolon" }, Value<List<string>>(result.data, "compileErrors"));
            var diagnostics = Value<List<PipelineCodeDiagnostic>>(LoadRecord(), "diagnostics");
            Assert.That(diagnostics.Count, Is.EqualTo(2));
            Assert.That(diagnostics[0].file, Is.EqualTo(SourcePath));
            Assert.That(diagnostics[0].line, Is.EqualTo(3));
            Assert.That(diagnostics[0].column, Is.EqualTo(7));
            Assert.That(diagnostics[1].file, Is.EqualTo("explicit.cs"));
            Assert.That(diagnostics[1].line, Is.Null);
        }

        [Test]
        public void PendingStep_DoesNotReleaseEmitOrPersistResult()
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            Assert.That(Step(operation, out var result, out var release), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(release, Is.False);
            Assert.That(_activeField.GetValue(null), Is.SameAs(operation));
            Assert.That(completion.Task.IsCompleted, Is.False);
            AssertRecord(Status(), "running", true, true, false);
            Assert.That(Value<bool>(Status(), "resultWritten"), Is.False);
            Assert.That(File.Exists(RecordPath(_requestId)), Is.False);
            Assert.That(File.Exists(_resultPath), Is.False);
            Assert.That(HasAsyncUpdate(), Is.True);
        }

        [Test]
        public void CancelForOriginalRequest_CancelsOnlyWaitingAndEmitsOnceUntilRemoteCompletes()
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            var result = (CommandResult)Invoke(operation, "CancelForOriginalRequest");
            Assert.That(result, Is.Not.Null);
            Assert.That(result.success, Is.False);
            Assert.That(result.id, Is.EqualTo(_requestId));
            Assert.That(completion.Task.IsCanceled, Is.False);
            Assert.That(completion.Task.IsCompleted, Is.False);
            AssertRecord(Status(), "cancel_requested", true, true, true);
            Assert.That(Value<bool>(Status(), "waitCanceled"), Is.True);
            Assert.That(Invoke(operation, "CancelForOriginalRequest"), Is.Null);
            Assert.That(Step(operation, out result, out var release), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(release, Is.False);
            Assert.That(_activeField.GetValue(null), Is.SameAs(operation));
            Assert.That(HasAsyncUpdate(), Is.True);

            completion.SetResult(new PipelineCodeResult { success = true, status = "validated" });
            Assert.That(Step(operation, out result, out release), Is.True);
            Assert.That(result, Is.Null, "Remote completion must not replace the canceled wait result.");
            Assert.That(release, Is.True);
            AssertRecord(Status(), "validated", false, false, false);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Timeout_WritesOriginalResultOnceAndLateCompletionRemainsQueryable(bool remoteSuccess)
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, 0);
            // 零超时只作用于人工构造的记录，不睡眠，也不修改产品的超时边界。
            Method("OnAsyncUpdate").Invoke(null, null);
            Assert.That(_activeField.GetValue(null), Is.SameAs(operation));
            Assert.That(completion.Task.IsCompleted, Is.False);
            Assert.That(File.Exists(_resultPath), Is.True);
            var originalResult = File.ReadAllBytes(_resultPath);
            var timeoutData = AIBridgeJson.DeserializeObject(File.ReadAllText(_resultPath));
            Assert.That(timeoutData["success"], Is.EqualTo(false));
            Assert.That(timeoutData["id"], Is.EqualTo(_requestId));
            var timeoutRecord = (Dictionary<string, object>)timeoutData["data"];
            Assert.That(timeoutRecord["status"], Is.EqualTo("timed_out_waiting_for_pipeline"));
            AssertRecord(Status(), "timed_out_waiting_for_pipeline", true, true, true);
            Assert.That(Value<bool>(Status(), "timedOut"), Is.True);
            Assert.That(Value<bool>(Status(), "resultWritten"), Is.True);
            Assert.That(HasAsyncUpdate(), Is.True);
            var originalWriteTime = File.GetLastWriteTimeUtc(_resultPath);

            Assert.That(Step(operation, out var result, out var release), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(release, Is.False);
            Method("OnAsyncUpdate").Invoke(null, null);
            CollectionAssert.AreEqual(originalResult, File.ReadAllBytes(_resultPath));
            Assert.That(File.GetLastWriteTimeUtc(_resultPath), Is.EqualTo(originalWriteTime));

            var finalStatus = remoteSuccess ? "validated" : "compile_failed";
            completion.SetResult(new PipelineCodeResult
            {
                success = remoteSuccess, status = finalStatus,
                error = remoteSuccess ? null : "Late compilation failure", httpStatus = 200
            });
            Method("OnAsyncUpdate").Invoke(null, null);
            Assert.That(_activeField.GetValue(null), Is.Null);
            Assert.That(HasAsyncUpdate(), Is.False);
            CollectionAssert.AreEqual(originalResult, File.ReadAllBytes(_resultPath));
            Assert.That(File.GetLastWriteTimeUtc(_resultPath), Is.EqualTo(originalWriteTime));
            AssertRecord(Status(), finalStatus, false, false, false);
            Assert.That(Value<bool>(Status(), "timedOut"), Is.True);
        }

        [TestCase("fault", "pipelineTaskUnknown")]
        [TestCase("canceled", "pipelineTaskUnknown")]
        [TestCase("missing", "pipelineResultMissing")]
        [TestCase("unknown", "remoteUnknown")]
        public void UnconfirmedCompletion_PreservesBusyUnknownPlaceholder(string outcome, string expectedErrorCode)
        {
            var completion = new TaskCompletionSource<PipelineCodeResult>();
            var operation = CreateOperation(completion, int.MaxValue);
            if (outcome == "fault") completion.SetException(new InvalidOperationException("private-token-do-not-serialize"));
            else if (outcome == "canceled") completion.SetCanceled();
            else completion.SetResult(outcome == "missing" ? null : new PipelineCodeResult
            {
                status = "unknown", mayStillBeRunning = true, errorCode = "remoteUnknown"
            });

            Assert.That(Step(operation, out var result, out var release), Is.True);
            Assert.That(result, Is.Not.Null);
            Assert.That(result.success, Is.False);
            Assert.That(release, Is.False);
            var placeholder = _activeField.GetValue(null);
            Assert.That(placeholder.GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            Assert.That(HasAsyncUpdate(), Is.False);
            AssertRecord(Status(), "unknown", false, true, true);
            Assert.That(Value<string>(Status(), "errorCode"), Is.EqualTo(expectedErrorCode));
            Assert.That(AIBridgeJson.Serialize(result, true), Does.Not.Contain("private-token-do-not-serialize"));
            AssertRecord(LoadRecord(), "unknown", false, true, true);
            Assert.That(Step(placeholder, out result, out release), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(release, Is.False);
            Assert.That(_activeField.GetValue(null), Is.SameAs(placeholder));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecoverActiveRecord_ConvertsToUnknownPreservesBusyAndNeverRewritesExistingResult(bool resultWritten)
        {
            var record = CreateRecord(int.MaxValue);
            Set(record, "resultWritten", resultWritten);
            Method("SavePipelineRecord").Invoke(null, new[] { record });
            byte[] originalResult = null;
            if (resultWritten)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_resultPath));
                File.WriteAllText(_resultPath, "{\"sentinel\":\"original wait result\"}");
                originalResult = File.ReadAllBytes(_resultPath);
            }

            Method("RecoverPipelineOperation").Invoke(null, null);
            var placeholder = _activeField.GetValue(null);
            Assert.That(placeholder.GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            AssertRecord(Status(), "unknown", false, true, true);
            Assert.That(Value<string>(Status(), "errorCode"), Is.EqualTo("pipelineStateLost"));
            Assert.That(Value<bool>(Status(), "resultWritten"), Is.True);
            AssertRecord(LoadRecord(), "unknown", false, true, true);
            Assert.That(File.Exists(_resultPath), Is.True);
            if (originalResult != null) CollectionAssert.AreEqual(originalResult, File.ReadAllBytes(_resultPath));
            else
            {
                var result = AIBridgeJson.DeserializeObject(File.ReadAllText(_resultPath));
                Assert.That(result["success"], Is.EqualTo(false));
                Assert.That(result["id"], Is.EqualTo(_requestId));
            }
            var firstResult = File.ReadAllBytes(_resultPath);
            _activeField.SetValue(null, null);
            Method("RecoverPipelineOperation").Invoke(null, null);
            Assert.That(_activeField.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            CollectionAssert.AreEqual(firstResult, File.ReadAllBytes(_resultPath));
            Assert.That(Step(_activeField.GetValue(null), out var ignoredResult, out var release), Is.False);
            Assert.That(ignoredResult, Is.Null);
            Assert.That(release, Is.False);
        }

        [Test]
        public void RecoverCorruptExistingLatestRecord_RetainsBusyUnknownPlaceholder()
        {
            var latestPath = RecordPath(null);
            Directory.CreateDirectory(Path.GetDirectoryName(latestPath));
            File.WriteAllText(latestPath, "{\"backend\":\"pipeline\",\"requestId\":");
            Method("RecoverPipelineOperation").Invoke(null, null);
            var placeholder = _activeField.GetValue(null);
            Assert.That(placeholder, Is.Not.Null);
            Assert.That(placeholder.GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            var status = (CommandResult)Method("GetCodeStatus").Invoke(null, new object[]
            {
                new CommandRequest { id = "corrupt-status", type = "code", @params = new Dictionary<string, object>() }
            });
            Assert.That(status.success, Is.True);
            AssertRecord(status.data, "unknown", false, true, true);
            Assert.That(Value<string>(status.data, "errorCode"), Is.EqualTo("pipelineStateInvalid"));
        }

        [Test]
        public void StartPipeline_WhenRequestRecordIsCorrupt_RefusesPostAndRetainsPlaceholder()
        {
            var path = RecordPath(_requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{broken");
            var result = (CommandResult)Method("StartPipelineDryRun").Invoke(null, new object[]
            {
                new CommandRequest { id = _requestId, type = "code", @params = new Dictionary<string, object>
                    { { "dry-run", true }, { "timeout", 5000 } } },
                CreateSourceText("return 1;")
            });
            Assert.That(result, Is.Not.Null);
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("state is unavailable"));
            Assert.That(_activeField.GetValue(null), Is.Not.Null);
            Assert.That(_activeField.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
        }

        [Test]
        public void ExistingCorruptRequestRecord_IsDistinguishedFromNotFound()
        {
            var path = RecordPath(_requestId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "{broken");
            var arguments = new object[] { _requestId, false };
            var method = Method("LoadPipelineRecordWithStatus");
            var loaded = method.Invoke(null, arguments);
            Assert.That(loaded, Is.Null);
            Assert.That(arguments[1], Is.EqualTo(true));
        }

        [Test]
        public void RecoverInactiveBusyRecord_RestoresPlaceholderWithoutChangingStoredStatus()
        {
            var record = CreateRecord(int.MaxValue);
            Set(record, "active", false);
            Set(record, "status", "unknown");
            Set(record, "mayStillBeRunning", true);
            Set(record, "resultWritten", true);
            Method("SavePipelineRecord").Invoke(null, new[] { record });
            var originalRecord = File.ReadAllBytes(RecordPath(_requestId));
            Method("RecoverPipelineOperation").Invoke(null, null);
            AssertRecord(Status(), "unknown", false, true, true);
            Assert.That(_activeField.GetValue(null).GetType().Name, Is.EqualTo("PipelineUnknownOperation"));
            CollectionAssert.AreEqual(originalRecord, File.ReadAllBytes(RecordPath(_requestId)));
            Assert.That(File.Exists(_resultPath), Is.False);
        }

        private static object CreateSourceText(string code)
        {
            var type = typeof(CodeCommand).GetNestedType("SourceText", BindingFlags.NonPublic);
            var source = Activator.CreateInstance(type, true);
            Set(source, "OriginalCode", code);
            Set(source, "Code", code);
            Set(source, "Kind", "inline");
            Set(source, "Path", "inline");
            return source;
        }

        private object CreateOperation(TaskCompletionSource<PipelineCodeResult> completion, int timeoutMs)
        {
            var type = typeof(CodeCommand).GetNestedType("PipelineDryRunAsyncOperation", BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null);
            var operation = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { CreateRecord(timeoutMs), completion.Task }, null);
            _activeField.SetValue(null, operation);
            EditorApplication.update += _asyncUpdate;
            return operation;
        }

        private object CreateRecord(int timeoutMs)
        {
            var type = typeof(CodeCommand).GetNestedType("PipelineOperationRecord", BindingFlags.NonPublic);
            Assert.That(type, Is.Not.Null);
            var record = Activator.CreateInstance(type, true);
            Set(record, "requestId", _requestId);
            Set(record, "operationId", _requestId);
            Set(record, "source", "file");
            Set(record, "sourcePath", SourcePath);
            Set(record, "timeoutMs", timeoutMs);
            return record;
        }

        private object Status()
        {
            // 直接查询私有状态入口，不经过会读取或修改授权设置的 execute 路由。
            var result = (CommandResult)Method("GetCodeStatus").Invoke(null, new object[]
            {
                new CommandRequest
                {
                    id = "status-" + _requestId, type = "code",
                    @params = new Dictionary<string, object> { { "requestId", _requestId } }
                }
            });
            Assert.That(result.success, Is.True);
            return result.data;
        }

        private object LoadRecord()
        {
            var record = Method("LoadPipelineRecord").Invoke(null, new object[] { _requestId });
            Assert.That(record, Is.Not.Null);
            return record;
        }

        private static bool Step(object operation, out CommandResult result, out bool release)
        {
            var arguments = new object[] { null, false };
            var stepped = (bool)operation.GetType().GetMethod("Step").Invoke(operation, arguments);
            result = (CommandResult)arguments[0];
            release = (bool)arguments[1];
            return stepped;
        }

        private static void AssertRecord(object record, string status, bool active, bool busy, bool mayStillBeRunning)
        {
            Assert.That(Value<string>(record, "status"), Is.EqualTo(status));
            Assert.That(Value<bool>(record, "active"), Is.EqualTo(active));
            Assert.That(Value<bool>(record, "busy"), Is.EqualTo(busy));
            Assert.That(Value<bool>(record, "mayStillBeRunning"), Is.EqualTo(mayStillBeRunning));
        }

        private static T Value<T>(object record, string name)
        {
            return (T)record.GetType().GetField(name, InstanceFields).GetValue(record);
        }

        private static void Set(object record, string name, object value)
        {
            record.GetType().GetField(name, InstanceFields).SetValue(record, value);
        }

        private static object Invoke(object operation, string name)
        {
            return operation.GetType().GetMethod(name).Invoke(operation, null);
        }

        private static MethodInfo Method(string name)
        {
            var method = typeof(CodeCommand).GetMethod(name, StaticPrivate);
            Assert.That(method, Is.Not.Null, name);
            return method;
        }

        private static string RecordPath(string requestId)
        {
            return (string)Method("PipelineRecordPath").Invoke(null, new object[] { requestId });
        }

        private void BackupFile(string path)
        {
            _fileBackups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        }

        private bool HasAsyncUpdate()
        {
            var callbacks = (Delegate)_updateField.GetValue(null);
            if (callbacks == null) return false;
            foreach (var callback in callbacks.GetInvocationList())
                if (callback.Equals(_asyncUpdate)) return true;
            return false;
        }

        private void RemoveAsyncUpdate()
        {
            // 去掉所有相同订阅，既保存未订阅状态，也能原样恢复原有重复订阅。
            var callbacks = (Delegate)_updateField.GetValue(null);
            if (callbacks == null) return;
            foreach (var callback in callbacks.GetInvocationList())
                if (callback.Equals(_asyncUpdate)) EditorApplication.update -= _asyncUpdate;
        }
    }
}
