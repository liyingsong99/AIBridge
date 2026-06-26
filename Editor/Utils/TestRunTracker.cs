using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIBridge.Internal.Json;
using NUnit.Framework.Interfaces;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace AIBridge.Editor
{
    /// <summary>
    /// Tracks native Unity test runs for AIBridge test run/status commands.
    /// </summary>
    [InitializeOnLoad]
    public static class TestRunTracker
    {
        private const int MaxKnownRunCount = 64;
        private const int PersistedStateSchemaVersion = 1;
        private const string StateDirectoryName = "test-runs";
        private const string StateFileName = "state.json";

        public enum TestRunStatus
        {
            Idle,
            Queued,
            Running,
            Passed,
            Failed,
            Timeout,
            Unknown
        }

        [Serializable]
        public class FailedTestInfo
        {
            public string name;
            public string message;
            public string stackTrace;
        }

        [Serializable]
        public class TestRunFilterInfo
        {
            public string testName;
            public string groupName;
            public string assemblyName;
        }

        private class TestRunState
        {
            public string runId;
            public TestRunStatus status;
            public TestMode modeValue;
            public string mode;
            public string testName;
            public string groupName;
            public string assemblyName;
            public DateTime queuedTime;
            public DateTime startTime;
            public DateTime? endTime;
            public int timeoutMs;
            public int total;
            public int passed;
            public int failed;
            public int skipped;
            public int inconclusive;
            public bool startedByInvocation;
            public bool attachedToExistingRun;
            public bool isRunning;
            public string nativeRunGuid;
            public string error;
            public TestRunFilterInfo requestedFilter;
            public TestRunFilterInfo executedFilter;
            public readonly List<FailedTestInfo> failedTests = new List<FailedTestInfo>();
        }

        private sealed class TestCallbacks : IErrorCallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                if (!HasActiveTrackedCurrentRun())
                {
                    return;
                }

                _currentState.total = testsToRun != null ? testsToRun.TestCaseCount : 0;
                PersistState();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                if (!HasActiveTrackedCurrentRun())
                {
                    AIBridgeLogger.LogWarning("Ignoring Unity test completion because no AIBridge test run is currently tracked.");
                    EnsureQueueUpdate();
                    return;
                }

                _currentState.isRunning = false;
                _currentState.endTime = DateTime.Now;
                _currentState.total = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount;
                _currentState.passed = result.PassCount;
                _currentState.failed = result.FailCount;
                _currentState.skipped = result.SkipCount;
                _currentState.inconclusive = result.InconclusiveCount;
                _currentState.status = result.FailCount > 0 ? TestRunStatus.Failed : TestRunStatus.Passed;

                LogSummary(_currentState.status);
                PersistState();
                EnsureQueueUpdate();
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (!HasActiveTrackedCurrentRun() || result == null || result.Test == null || result.Test.IsSuite)
                {
                    return;
                }

                if (result.TestStatus != UnityEditor.TestTools.TestRunner.Api.TestStatus.Failed)
                {
                    return;
                }

                _currentState.failedTests.Add(new FailedTestInfo
                {
                    name = result.FullName,
                    message = result.Message,
                    stackTrace = result.StackTrace
                });
            }

            public void OnError(string message)
            {
                if (!HasActiveTrackedCurrentRun())
                {
                    AIBridgeLogger.LogWarning("Ignoring Unity test error because no AIBridge test run is currently tracked.");
                    EnsureQueueUpdate();
                    return;
                }

                _currentState.isRunning = false;
                _currentState.endTime = DateTime.Now;
                _currentState.status = TestRunStatus.Failed;

                if (!string.IsNullOrEmpty(message))
                {
                    _currentState.error = message;
                    _currentState.failedTests.Add(new FailedTestInfo
                    {
                        name = "TestRunError",
                        message = message,
                        stackTrace = string.Empty
                    });
                }

                LogSummary(_currentState.status);
                PersistState();
                EnsureQueueUpdate();
            }
        }

        private static readonly TestCallbacks Callbacks = new TestCallbacks();
        private static readonly Queue<TestRunState> PendingRuns = new Queue<TestRunState>();
        private static readonly Dictionary<string, TestRunState> KnownRuns = new Dictionary<string, TestRunState>(StringComparer.Ordinal);
        private static readonly List<string> KnownRunOrder = new List<string>();
        private static TestRunnerApi _testRunnerApi;
        private static TestRunState _currentState;
        private static bool _initialized;
        private static string _stateFilePathOverride;

        static TestRunTracker()
        {
            Initialize();
        }

        public static void Initialize()
        {
            if (_initialized && _testRunnerApi != null)
            {
                return;
            }

            if (_testRunnerApi == null)
            {
                _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
                _testRunnerApi.RegisterCallbacks(Callbacks);
            }

            if (!_initialized)
            {
                RestorePersistedState();
                if (_currentState == null)
                {
                    _currentState = new TestRunState
                    {
                        status = TestRunStatus.Idle
                    };
                }

                if (IsNativeRunActive() || PendingRuns.Count > 0)
                {
                    EnsureQueueUpdate();
                }

                _initialized = true;
                AIBridgeLogger.LogDebug("TestRunTracker initialized");
            }
        }

        private static bool IsNativeTestRunRunning(string nativeRunGuid)
        {
            if (string.IsNullOrWhiteSpace(nativeRunGuid))
            {
                return false;
            }

            try
            {
                var method = typeof(TestRunnerApi).GetMethod("IsRunning", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (method == null)
                {
                    return false;
                }

                return (bool)method.Invoke(null, new object[] { nativeRunGuid });
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("Failed to inspect Unity test run state: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Start a new test run. If Unity TestRunner is busy, queue this run instead of reusing unrelated results.
        /// </summary>
        public static StartRunResult StartRun(string runId, TestMode mode, string testName, string groupName, string assemblyName, int timeoutMs)
        {
            Initialize();

            var state = CreateState(runId, mode, testName, groupName, assemblyName, timeoutMs);
            AddKnownRun(state);

            if (IsNativeRunActive() || PendingRuns.Count > 0)
            {
                PendingRuns.Enqueue(state);
                PersistState();
                EnsureQueueUpdate();

                return new StartRunResult
                {
                    runId = state.runId,
                    startedByInvocation = false,
                    attachedToExistingRun = false,
                    queuedByInvocation = true,
                    snapshot = BuildSnapshot(state)
                };
            }

            StartNativeRun(state);
            PersistState();

            return new StartRunResult
            {
                runId = state.runId,
                startedByInvocation = state.startedByInvocation,
                attachedToExistingRun = false,
                queuedByInvocation = false,
                snapshot = BuildSnapshot(state)
            };
        }

        public static TestRunSnapshot GetSnapshot(string runId = null)
        {
            Initialize();

            if (!string.IsNullOrWhiteSpace(runId))
            {
                if (KnownRuns.TryGetValue(runId, out var knownState))
                {
                    RefreshKnownStateIfLost(knownState);
                    return BuildSnapshot(knownState);
                }

                return BuildUnknownSnapshot(runId);
            }

            RefreshCurrentRunIfLost();

            var state = _currentState ?? new TestRunState
            {
                status = TestRunStatus.Idle
            };

            return BuildSnapshot(state);
        }

        private static TestRunState CreateState(string runId, TestMode mode, string testName, string groupName, string assemblyName, int timeoutMs)
        {
            var resolvedRunId = string.IsNullOrWhiteSpace(runId)
                ? Guid.NewGuid().ToString("N")
                : runId;

            return new TestRunState
            {
                runId = resolvedRunId,
                status = TestRunStatus.Queued,
                modeValue = mode,
                mode = ModeToString(mode),
                testName = NormalizeFilterValue(testName),
                groupName = NormalizeFilterValue(groupName),
                assemblyName = NormalizeFilterValue(assemblyName),
                queuedTime = DateTime.Now,
                timeoutMs = timeoutMs,
                startedByInvocation = false,
                attachedToExistingRun = false,
                requestedFilter = CreateFilterInfo(testName, groupName, assemblyName)
            };
        }

        private static void StartNativeRun(TestRunState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.status == TestRunStatus.Timeout)
            {
                return;
            }

            if (IsQueuedRunExpired(state))
            {
                MarkTimedOutBeforeStart(state);
                return;
            }

            var filter = new Filter
            {
                testMode = state.modeValue
            };

            AssignFilterValue(state.testName, value => filter.testNames = new[] { value });
            AssignFilterValue(state.groupName, value => filter.groupNames = new[] { value });
            AssignFilterValue(state.assemblyName, value => filter.assemblyNames = new[] { value });

            state.status = TestRunStatus.Running;
            state.startTime = DateTime.Now;
            state.startedByInvocation = true;
            state.attachedToExistingRun = false;
            state.isRunning = true;
            state.executedFilter = CreateFilterInfo(state.testName, state.groupName, state.assemblyName);
            _currentState = state;
            PersistState();

            var executionSettings = new ExecutionSettings(filter)
            {
                runSynchronously = false
            };

            try
            {
                state.nativeRunGuid = _testRunnerApi.Execute(executionSettings);
                PersistState();
            }
            catch (Exception ex)
            {
                state.isRunning = false;
                state.endTime = DateTime.Now;
                state.status = TestRunStatus.Failed;
                state.error = ex.Message;
                state.nativeRunGuid = null;
                state.failedTests.Add(new FailedTestInfo
                {
                    name = "TestRunStartError",
                    message = ex.Message,
                    stackTrace = ex.StackTrace
                });
                PersistState();
                EnsureQueueUpdate();
            }
        }

        private static void EnsureQueueUpdate()
        {
            EditorApplication.update -= OnQueueUpdate;
            EditorApplication.update += OnQueueUpdate;
        }

        private static void OnQueueUpdate()
        {
            if (IsNativeRunActive())
            {
                return;
            }

            while (PendingRuns.Count > 0)
            {
                var next = PendingRuns.Dequeue();
                PersistState();
                if (next.status == TestRunStatus.Timeout)
                {
                    continue;
                }

                if (IsQueuedRunExpired(next))
                {
                    MarkTimedOutBeforeStart(next);
                    continue;
                }

                // Unity TestRunner 是 Editor 级单例；这里只在上一轮完成后启动下一轮，避免不同 filter 互相串结果。
                StartNativeRun(next);
                if (next.isRunning)
                {
                    return;
                }
            }

            EditorApplication.update -= OnQueueUpdate;
            PersistState();
        }

        private static bool IsNativeRunActive()
        {
            // 先把重载后可能丢失的运行状态收口，避免队列一直卡在旧的 running 标记上。
            RefreshCurrentRunIfLost();
            return _currentState != null && _currentState.isRunning;
        }

        private static bool IsQueuedRunExpired(TestRunState state)
        {
            return state != null
                   && state.status == TestRunStatus.Queued
                   && state.timeoutMs > 0
                   && (DateTime.Now - state.queuedTime).TotalMilliseconds > state.timeoutMs;
        }

        private static void MarkTimedOutBeforeStart(TestRunState state)
        {
            if (state == null)
            {
                return;
            }

            state.status = TestRunStatus.Timeout;
            state.isRunning = false;
            state.endTime = DateTime.Now;
            state.error = "Test run timed out while waiting in the Unity TestRunner queue.";
            PersistState();
        }

        private static TestRunSnapshot BuildSnapshot(TestRunState state)
        {
            if (state == null)
            {
                return new TestRunSnapshot
                {
                    status = StatusToString(TestRunStatus.Idle),
                    queuePosition = -1
                };
            }

            UpdateTimeoutStatus(state);

            var endTime = state.endTime ?? DateTime.Now;
            var durationStart = state.startTime == default ? state.queuedTime : state.startTime;
            var duration = durationStart == default ? 0 : (endTime - durationStart).TotalSeconds;

            return new TestRunSnapshot
            {
                runId = state.runId,
                status = StatusToString(state.status),
                mode = state.mode,
                queuedAt = state.queuedTime == default ? null : state.queuedTime.ToString("o"),
                startedAt = state.startTime == default ? null : state.startTime.ToString("o"),
                duration = Math.Round(duration, 2),
                total = state.total,
                passed = state.passed,
                failed = state.failed,
                skipped = state.skipped,
                inconclusive = state.inconclusive,
                failedTests = new List<FailedTestInfo>(state.failedTests),
                startedByInvocation = state.startedByInvocation,
                attachedToExistingRun = state.attachedToExistingRun,
                queuePosition = GetQueuePosition(state),
                requestedFilter = state.requestedFilter,
                executedFilter = state.executedFilter,
                nativeRunGuid = state.nativeRunGuid,
                error = state.error
            };
        }

        private static TestRunSnapshot BuildUnknownSnapshot(string runId)
        {
            return new TestRunSnapshot
            {
                runId = runId,
                status = StatusToString(TestRunStatus.Unknown),
                queuePosition = -1,
                failedTests = new List<FailedTestInfo>(),
                error = "No Unity test run is known for the requested runId yet."
            };
        }

        private static void UpdateTimeoutStatus(TestRunState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.isRunning
                && state.status != TestRunStatus.Timeout
                && state.timeoutMs > 0
                && (DateTime.Now - state.startTime).TotalMilliseconds > state.timeoutMs)
            {
                state.status = TestRunStatus.Timeout;
                state.error = "Test run timed out. Unity may still be running tests.";
                PersistState();
            }
            else if (IsQueuedRunExpired(state))
            {
                MarkTimedOutBeforeStart(state);
            }
        }

        private static void RefreshCurrentRunIfLost()
        {
            RefreshKnownStateIfLost(_currentState);
        }

        private static void RefreshKnownStateIfLost(TestRunState state)
        {
            if (state == null || !state.isRunning)
            {
                return;
            }

            if (IsNativeTestRunRunning(state.nativeRunGuid))
            {
                return;
            }

            state.isRunning = false;
            state.status = TestRunStatus.Unknown;
            state.endTime = DateTime.Now;
            state.error = "Unity test run state was lost during reload.";
            PersistState();

            if (_currentState != null && string.Equals(_currentState.runId, state.runId, StringComparison.Ordinal))
            {
                EnsureQueueUpdate();
            }
        }

        private static int GetQueuePosition(TestRunState state)
        {
            if (state == null)
            {
                return -1;
            }

            if (state.isRunning)
            {
                return 0;
            }

            var position = 1;
            foreach (var pending in PendingRuns)
            {
                if (ReferenceEquals(pending, state))
                {
                    return position;
                }

                position++;
            }

            return -1;
        }

        private static void AddKnownRun(TestRunState state, bool trim = true)
        {
            if (state == null || string.IsNullOrEmpty(state.runId))
            {
                return;
            }

            KnownRuns[state.runId] = state;
            KnownRunOrder.Add(state.runId);
            if (trim)
            {
                TrimKnownRuns();
            }
        }

        private static void TrimKnownRuns()
        {
            while (KnownRunOrder.Count > MaxKnownRunCount)
            {
                var oldest = KnownRunOrder[0];
                KnownRunOrder.RemoveAt(0);

                if (_currentState != null && string.Equals(_currentState.runId, oldest, StringComparison.Ordinal))
                {
                    continue;
                }

                var stillPending = false;
                foreach (var pending in PendingRuns)
                {
                    if (string.Equals(pending.runId, oldest, StringComparison.Ordinal))
                    {
                        stillPending = true;
                        break;
                    }
                }

                if (!stillPending)
                {
                    KnownRuns.Remove(oldest);
                }
            }
        }

        private static bool HasCurrentRunId()
        {
            return _currentState != null && !string.IsNullOrEmpty(_currentState.runId);
        }

        private static bool HasActiveTrackedCurrentRun()
        {
            return HasCurrentRunId() && _currentState.isRunning;
        }

        private static string GetStateFilePath()
        {
            if (!string.IsNullOrWhiteSpace(_stateFilePathOverride))
            {
                return _stateFilePathOverride;
            }

            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectRoot, ".aibridge", StateDirectoryName, StateFileName);
        }

        private static void PersistState()
        {
            try
            {
                var path = GetStateFilePath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var state = new PersistedTestRunStore
                {
                    schemaVersion = PersistedStateSchemaVersion,
                    currentRunId = HasCurrentRunId() ? _currentState.runId : null,
                    updatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };

                foreach (var pending in PendingRuns)
                {
                    if (pending != null && !string.IsNullOrEmpty(pending.runId))
                    {
                        state.pendingRunIds.Add(pending.runId);
                    }
                }

                foreach (var runId in KnownRunOrder)
                {
                    if (KnownRuns.TryGetValue(runId, out var knownState))
                    {
                        state.runs.Add(ToPersistedState(knownState));
                    }
                }

                File.WriteAllText(path, AIBridgeJson.Serialize(state, pretty: true), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("Failed to persist Unity test run state: " + ex.Message);
            }
        }

        private static void RestorePersistedState()
        {
            try
            {
                var path = GetStateFilePath();
                if (!File.Exists(path))
                {
                    return;
                }

                var data = AIBridgeJson.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                if (data == null)
                {
                    return;
                }

                KnownRuns.Clear();
                KnownRunOrder.Clear();
                PendingRuns.Clear();

                var runs = GetList(data, "runs");
                if (runs != null)
                {
                    foreach (var item in runs)
                    {
                        var runData = item as Dictionary<string, object>;
                        var state = FromPersistedState(runData);
                        if (state != null)
                        {
                            AddKnownRun(state, trim: false);
                        }
                    }
                }

                var currentRunId = GetString(data, "currentRunId", null);
                if (!string.IsNullOrEmpty(currentRunId) && KnownRuns.TryGetValue(currentRunId, out var currentState))
                {
                    _currentState = currentState;
                }
                else if (KnownRunOrder.Count > 0)
                {
                    _currentState = KnownRuns[KnownRunOrder[KnownRunOrder.Count - 1]];
                }

                if (_currentState != null && _currentState.isRunning && !IsNativeTestRunRunning(_currentState.nativeRunGuid))
                {
                    _currentState.isRunning = false;
                    _currentState.status = TestRunStatus.Unknown;
                    _currentState.endTime = DateTime.Now;
                    _currentState.error = "Unity test run state was lost during reload.";
                }

                var pendingIds = GetList(data, "pendingRunIds");
                if (pendingIds != null)
                {
                    foreach (var item in pendingIds)
                    {
                        var runId = Convert.ToString(item, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(runId)
                            && KnownRuns.TryGetValue(runId, out var pendingState)
                            && pendingState.status == TestRunStatus.Queued)
                        {
                            PendingRuns.Enqueue(pendingState);
                        }
                    }
                }

                TrimKnownRuns();
                PersistState();
            }
            catch (Exception ex)
            {
                AIBridgeLogger.LogWarning("Failed to restore Unity test run state: " + ex.Message);
            }
        }

        private static PersistedTestRunState ToPersistedState(TestRunState state)
        {
            return new PersistedTestRunState
            {
                runId = state.runId,
                status = StatusToString(state.status),
                mode = state.mode,
                testName = state.testName,
                groupName = state.groupName,
                assemblyName = state.assemblyName,
                queuedTime = FormatDateTime(state.queuedTime),
                startTime = FormatDateTime(state.startTime),
                endTime = state.endTime.HasValue ? FormatDateTime(state.endTime.Value) : null,
                timeoutMs = state.timeoutMs,
                total = state.total,
                passed = state.passed,
                failed = state.failed,
                skipped = state.skipped,
                inconclusive = state.inconclusive,
                startedByInvocation = state.startedByInvocation,
                attachedToExistingRun = state.attachedToExistingRun,
                isRunning = state.isRunning,
                nativeRunGuid = state.nativeRunGuid,
                error = state.error,
                requestedFilter = state.requestedFilter,
                executedFilter = state.executedFilter,
                failedTests = new List<FailedTestInfo>(state.failedTests)
            };
        }

        private static TestRunState FromPersistedState(Dictionary<string, object> data)
        {
            if (data == null)
            {
                return null;
            }

            var runId = GetString(data, "runId", null);
            if (string.IsNullOrEmpty(runId))
            {
                return null;
            }

            var mode = GetString(data, "mode", "EditMode");
            var state = new TestRunState
            {
                runId = runId,
                status = ParseStatus(GetString(data, "status", "unknown")),
                modeValue = ParseMode(mode),
                mode = mode,
                testName = GetString(data, "testName", null),
                groupName = GetString(data, "groupName", null),
                assemblyName = GetString(data, "assemblyName", null),
                queuedTime = ParseDateTime(GetString(data, "queuedTime", null)),
                startTime = ParseDateTime(GetString(data, "startTime", null)),
                endTime = ParseNullableDateTime(GetString(data, "endTime", null)),
                timeoutMs = GetInt(data, "timeoutMs", 0),
                total = GetInt(data, "total", 0),
                passed = GetInt(data, "passed", 0),
                failed = GetInt(data, "failed", 0),
                skipped = GetInt(data, "skipped", 0),
                inconclusive = GetInt(data, "inconclusive", 0),
                startedByInvocation = GetBool(data, "startedByInvocation", false),
                attachedToExistingRun = GetBool(data, "attachedToExistingRun", false),
                isRunning = GetBool(data, "isRunning", false),
                nativeRunGuid = GetString(data, "nativeRunGuid", null),
                error = GetString(data, "error", null),
                requestedFilter = ParseFilter(data, "requestedFilter"),
                executedFilter = ParseFilter(data, "executedFilter")
            };

            state.failedTests.AddRange(ParseFailedTests(data));
            return state;
        }

        private static string FormatDateTime(DateTime value)
        {
            return value == default ? null : value.ToString("o", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseDateTime(string value)
        {
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : default;
        }

        private static DateTime? ParseNullableDateTime(string value)
        {
            return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static TestMode ParseMode(string value)
        {
            return string.Equals(value, "PlayMode", StringComparison.OrdinalIgnoreCase)
                ? TestMode.PlayMode
                : TestMode.EditMode;
        }

        private static TestRunStatus ParseStatus(string status)
        {
            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Queued;
            }

            if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Running;
            }

            if (string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Passed;
            }

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Failed;
            }

            if (string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Timeout;
            }

            if (string.Equals(status, "idle", StringComparison.OrdinalIgnoreCase))
            {
                return TestRunStatus.Idle;
            }

            return TestRunStatus.Unknown;
        }

        private static TestRunFilterInfo ParseFilter(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
            {
                return null;
            }

            var filterData = value as Dictionary<string, object>;
            if (filterData == null)
            {
                return null;
            }

            return CreateFilterInfo(
                GetString(filterData, "testName", null),
                GetString(filterData, "groupName", null),
                GetString(filterData, "assemblyName", null));
        }

        private static List<FailedTestInfo> ParseFailedTests(Dictionary<string, object> data)
        {
            var result = new List<FailedTestInfo>();
            var items = GetList(data, "failedTests");
            if (items == null)
            {
                return result;
            }

            foreach (var item in items)
            {
                var testData = item as Dictionary<string, object>;
                if (testData == null)
                {
                    continue;
                }

                result.Add(new FailedTestInfo
                {
                    name = GetString(testData, "name", null),
                    message = GetString(testData, "message", null),
                    stackTrace = GetString(testData, "stackTrace", null)
                });
            }

            return result;
        }

        private static string GetString(Dictionary<string, object> data, string key, string defaultValue)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int GetInt(Dictionary<string, object> data, string key, int defaultValue)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static bool GetBool(Dictionary<string, object> data, string key, bool defaultValue)
        {
            if (data == null || !data.TryGetValue(key, out var value) || value == null)
            {
                return defaultValue;
            }

            try
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static List<object> GetList(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out var value))
            {
                return null;
            }

            return value as List<object>;
        }

        private static string NormalizeFilterValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static TestRunFilterInfo CreateFilterInfo(string testName, string groupName, string assemblyName)
        {
            return new TestRunFilterInfo
            {
                testName = NormalizeFilterValue(testName),
                groupName = NormalizeFilterValue(groupName),
                assemblyName = NormalizeFilterValue(assemblyName)
            };
        }

        private static void AssignFilterValue(string value, Action<string> assignAction)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            assignAction(value);
        }

        private static string ModeToString(TestMode mode)
        {
            return mode == TestMode.PlayMode ? "PlayMode" : "EditMode";
        }

        private static string StatusToString(TestRunStatus status)
        {
            switch (status)
            {
                case TestRunStatus.Queued:
                    return "queued";
                case TestRunStatus.Running:
                    return "running";
                case TestRunStatus.Passed:
                    return "passed";
                case TestRunStatus.Failed:
                    return "failed";
                case TestRunStatus.Timeout:
                    return "timeout";
                case TestRunStatus.Unknown:
                    return "unknown";
                default:
                    return "idle";
            }
        }

        internal static void ReloadPersistedStateForTests(string stateFilePath)
        {
            _stateFilePathOverride = stateFilePath;
            PendingRuns.Clear();
            KnownRuns.Clear();
            KnownRunOrder.Clear();
            _currentState = null;
            _initialized = true;

            RestorePersistedState();
            if (_currentState == null)
            {
                _currentState = new TestRunState
                {
                    status = TestRunStatus.Idle
                };
            }
        }

        internal static void ResetStateForTests()
        {
            PendingRuns.Clear();
            KnownRuns.Clear();
            KnownRunOrder.Clear();
            _currentState = new TestRunState
            {
                status = TestRunStatus.Idle
            };
            _stateFilePathOverride = null;
            _initialized = true;
        }

        private static void LogSummary(TestRunStatus status)
        {
            var snapshot = GetSnapshot();
            AIBridgeLogger.LogInfo(
                $"Test run {StatusToString(status)}. runId={snapshot.runId}, mode={snapshot.mode}, total={snapshot.total}, passed={snapshot.passed}, failed={snapshot.failed}, skipped={snapshot.skipped}, inconclusive={snapshot.inconclusive}, duration={snapshot.duration:F2}s");
        }

        [Serializable]
        private class PersistedTestRunStore
        {
            public int schemaVersion;
            public string currentRunId;
            public string updatedAtUtc;
            public List<string> pendingRunIds = new List<string>();
            public List<PersistedTestRunState> runs = new List<PersistedTestRunState>();
        }

        [Serializable]
        private class PersistedTestRunState
        {
            public string runId;
            public string status;
            public string mode;
            public string testName;
            public string groupName;
            public string assemblyName;
            public string queuedTime;
            public string startTime;
            public string endTime;
            public int timeoutMs;
            public int total;
            public int passed;
            public int failed;
            public int skipped;
            public int inconclusive;
            public bool startedByInvocation;
            public bool attachedToExistingRun;
            public bool isRunning;
            public string nativeRunGuid;
            public string error;
            public TestRunFilterInfo requestedFilter;
            public TestRunFilterInfo executedFilter;
            public List<FailedTestInfo> failedTests = new List<FailedTestInfo>();
        }
    }

    [Serializable]
    public class StartRunResult
    {
        public string runId;
        public bool startedByInvocation;
        public bool attachedToExistingRun;
        public bool queuedByInvocation;
        public TestRunSnapshot snapshot;
    }

    [Serializable]
    public class TestRunSnapshot
    {
        public string runId;
        public string status;
        public string mode;
        public string queuedAt;
        public string startedAt;
        public double duration;
        public int total;
        public int passed;
        public int failed;
        public int skipped;
        public int inconclusive;
        public List<TestRunTracker.FailedTestInfo> failedTests;
        public bool startedByInvocation;
        public bool attachedToExistingRun;
        public int queuePosition;
            public TestRunTracker.TestRunFilterInfo requestedFilter;
            public TestRunTracker.TestRunFilterInfo executedFilter;
            public string nativeRunGuid;
            public string error;
        }
}
