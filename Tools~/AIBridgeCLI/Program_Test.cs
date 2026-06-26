using System;
using System.Collections.Generic;
using AIBridgeCLI.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI
{
    partial class Program
    {
        /// <summary>
        /// 执行 Unity TestRunner 测试，并按本次 runId 轮询状态直到结束或超时。
        /// </summary>
        static int HandleTestRun(ParsedArgs parsed, OutputMode outputMode)
        {
            var testTimeout = parsed.Options.TryGetValue("timeout", out var timeoutValue) && int.TryParse(timeoutValue, out var timeoutMs)
                ? timeoutMs
                : 120000;
            var pollInterval = parsed.Options.TryGetValue("poll-interval", out var pollValue) && int.TryParse(pollValue, out var pollMs)
                ? pollMs
                : 500;
            var commandTimeout = parsed.Options.TryGetValue("transport-timeout", out var transportValue) && int.TryParse(transportValue, out var transportMs)
                ? transportMs
                : GetDefaultUnityCompileTransportTimeout(testTimeout, pollInterval);

            var sender = CreateCommandSender(commandTimeout, parsed);
            var startTime = DateTime.Now;
            var runId = PathHelper.GenerateCommandId();
            var startAcknowledged = false;

            var startParams = new Dictionary<string, object>
            {
                { "action", "run" },
                { "mode", parsed.Options.TryGetValue("mode", out var mode) ? mode : "EditMode" },
                { "timeout", testTimeout },
                { "runId", runId }
            };

            AddOptionalParam(startParams, "testName", parsed.Options, "test-name");
            AddOptionalParam(startParams, "groupName", parsed.Options, "group-name");
            AddOptionalParam(startParams, "assemblyName", parsed.Options, "assembly-name");

            if (outputMode == OutputMode.Pretty)
            {
                OutputFormatter.PrintInfo($"Starting Unity tests ({startParams["mode"]})...");
            }

            var startResult = sender.SendCommand(new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = "test",
                @params = startParams
            });

            if (!startResult.success)
            {
                if (!IsTransportTimeoutError(startResult.error))
                {
                    return FinishTestResult(parsed, outputMode, TestResultPayload.FromFailure(runId, startResult.error));
                }

                if (outputMode == OutputMode.Pretty)
                {
                    OutputFormatter.PrintInfo("Unity did not acknowledge the test request within the transport timeout. Waiting for test status...");
                }
            }
            else
            {
                startAcknowledged = true;
                var data = startResult.data as JObject;
                if (data != null)
                {
                    var startPayload = BuildTestPayload(data, runId, startTime, null);
                    if (startPayload.IsFinal)
                    {
                        return FinishTestResult(parsed, outputMode, startPayload);
                    }
                }
            }

            while ((DateTime.Now - startTime).TotalMilliseconds < testTimeout)
            {
                System.Threading.Thread.Sleep(pollInterval);

                var statusResult = sender.SendCommand(new CommandRequest
                {
                    id = PathHelper.GenerateCommandId(),
                    type = "test",
                    @params = new Dictionary<string, object>
                    {
                        { "action", "status" },
                        { "runId", runId }
                    }
                });

                if (!statusResult.success)
                {
                    continue;
                }

                var statusData = statusResult.data as JObject;
                if (statusData == null)
                {
                    continue;
                }

                var statusPayload = BuildTestPayload(statusData, runId, startTime, null);
                if (startAcknowledged && IsLostTestRunStatus(runId, statusPayload.Status, statusPayload.StatusConfirmed))
                {
                    return FinishTestResult(parsed, outputMode, TestResultPayload.FromFailure(
                        runId,
                        BuildLostTestRunMessage(runId, statusPayload.Error)));
                }

                if (!statusPayload.IsFinal)
                {
                    continue;
                }

                return FinishTestResult(parsed, outputMode, statusPayload);
            }

            return FinishTestResult(parsed, outputMode, TestResultPayload.TimedOut(
                runId,
                (DateTime.Now - startTime).TotalSeconds,
                $"Test run timed out after {testTimeout}ms. Unity may still be running tests or the run may still be queued."));
        }

        /// <summary>
        /// 查询最近一次或指定 runId 的 Unity TestRunner 测试状态。
        /// </summary>
        static int HandleTestStatus(ParsedArgs parsed, OutputMode outputMode)
        {
            var transportTimeout = parsed.Options.TryGetValue("timeout", out var timeoutValue) && int.TryParse(timeoutValue, out var timeoutMs)
                ? timeoutMs
                : DEFAULT_TIMEOUT;

            var sender = CreateCommandSender(transportTimeout, parsed);
            var statusParams = new Dictionary<string, object>
            {
                { "action", "status" }
            };
            AddOptionalParam(statusParams, "runId", parsed.Options, "run-id");

            var result = sender.SendCommand(new CommandRequest
            {
                id = PathHelper.GenerateCommandId(),
                type = "test",
                @params = statusParams
            });

            if (!result.success)
            {
                return FinishTestResult(parsed, outputMode, TestResultPayload.FromFailure(null, result.error));
            }

            var data = result.data as JObject;
            if (data == null)
            {
                return FinishTestResult(parsed, outputMode, new TestResultPayload
                {
                    Success = true,
                    Status = "idle",
                    StatusConfirmed = true,
                    FailedTests = new List<object>(),
                    QueuePosition = -1
                });
            }

            var payload = BuildTestPayload(data, null, DateTime.Now, null);
            payload.Success = payload.Status == "idle" || payload.Status == "queued" || payload.Status == "running" || payload.Status == "passed";
            payload.StatusConfirmed = true;

            return FinishTestResult(parsed, outputMode, payload);
        }

        static TestResultPayload BuildTestPayload(JObject data, string fallbackRunId, DateTime startTime, string fallbackError)
        {
            var payload = new TestResultPayload
            {
                RunId = (string)data?["runId"] ?? fallbackRunId,
                Status = (string)data?["status"] ?? "failed",
                Mode = (string)data?["mode"],
                QueuedAt = (string)data?["queuedAt"],
                StartedAt = (string)data?["startedAt"],
                Duration = (double?)data?["duration"] ?? (DateTime.Now - startTime).TotalSeconds,
                Total = (int?)data?["total"] ?? 0,
                Passed = (int?)data?["passed"] ?? 0,
                Failed = (int?)data?["failed"] ?? 0,
                Skipped = (int?)data?["skipped"] ?? 0,
                Inconclusive = (int?)data?["inconclusive"] ?? 0,
                StartedByInvocation = (bool?)data?["startedByInvocation"] ?? false,
                AttachedToExistingRun = (bool?)data?["attachedToExistingRun"] ?? false,
                QueuedByInvocation = (bool?)data?["queuedByInvocation"] ?? false,
                StatusConfirmed = true,
                QueuePosition = (int?)data?["queuePosition"] ?? -1,
                FailedTests = ConvertFailedTests(data?["failedTests"] as JArray),
                Error = (string)data?["error"] ?? fallbackError,
                RequestedFilter = data?["requestedFilter"] as JObject,
                ExecutedFilter = data?["executedFilter"] as JObject
            };
            payload.Success = payload.Status == "passed";
            return payload;
        }

        internal static bool IsLostTestRunStatus(string expectedRunId, string status, bool statusConfirmed)
        {
            return statusConfirmed
                   && !string.IsNullOrWhiteSpace(expectedRunId)
                   && string.Equals(status, "unknown", StringComparison.OrdinalIgnoreCase);
        }

        static string BuildLostTestRunMessage(string runId, string statusError)
        {
            var message = "Unity test run state was lost for runId " + runId
                          + ". This can happen after a domain reload or if the AIBridge test state cache was cleared. "
                          + "The native Unity test may have finished, but AIBridge cannot confirm this run result.";

            if (!string.IsNullOrWhiteSpace(statusError))
            {
                message += " Last status error: " + statusError;
            }

            return message;
        }

        static List<object> ConvertFailedTests(JArray failedTestsArray)
        {
            var failedTests = new List<object>();
            if (failedTestsArray == null)
            {
                return failedTests;
            }

            foreach (var failedTest in failedTestsArray)
            {
                failedTests.Add(new
                {
                    name = (string)failedTest["name"],
                    message = (string)failedTest["message"],
                    stackTrace = (string)failedTest["stackTrace"]
                });
            }

            return failedTests;
        }

        static void AddOptionalParam(Dictionary<string, object> target, string targetKey, Dictionary<string, string> source, string sourceKey)
        {
            if (source.TryGetValue(sourceKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                target[targetKey] = value;
            }
        }

        static void OutputTestResult(OutputMode outputMode, TestResultPayload payload)
        {
            if (payload == null)
            {
                payload = TestResultPayload.FromFailure(null, "Missing test result payload.");
            }

            if (outputMode == OutputMode.Raw || outputMode == OutputMode.Quiet)
            {
                var jsonResult = new
                {
                    success = payload.Success,
                    runId = payload.RunId,
                    status = payload.Status,
                    mode = payload.Mode,
                    queuedAt = payload.QueuedAt,
                    startedAt = payload.StartedAt,
                    duration = Math.Round(payload.Duration, 2),
                    total = payload.Total,
                    passed = payload.Passed,
                    failed = payload.Failed,
                    skipped = payload.Skipped,
                    inconclusive = payload.Inconclusive,
                    startedByInvocation = payload.StartedByInvocation,
                    attachedToExistingRun = payload.AttachedToExistingRun,
                    queuedByInvocation = payload.QueuedByInvocation,
                    statusConfirmed = payload.StatusConfirmed,
                    queuePosition = payload.QueuePosition,
                    requestedFilter = payload.RequestedFilter,
                    executedFilter = payload.ExecutedFilter,
                    failedTests = payload.FailedTests,
                    error = payload.Error
                };
                Console.WriteLine(JsonConvert.SerializeObject(jsonResult, Formatting.None));
                return;
            }

            if (payload.Success && payload.Status != "running" && payload.Status != "idle" && payload.Status != "queued")
            {
                OutputFormatter.PrintSuccess($"Unity tests passed in {payload.Duration:F1}s");
            }
            else if (payload.Status == "queued")
            {
                OutputFormatter.PrintInfo("Unity tests are queued.");
            }
            else if (payload.Status == "running")
            {
                OutputFormatter.PrintInfo("Unity tests are still running.");
            }
            else if (payload.Status == "idle")
            {
                OutputFormatter.PrintInfo("No Unity test run is active.");
            }
            else if (!string.IsNullOrEmpty(payload.Error))
            {
                OutputFormatter.PrintError(payload.Error);
            }
            else if (payload.Status == "timeout")
            {
                OutputFormatter.PrintError($"Unity tests timed out after {payload.Duration:F1}s");
            }
            else
            {
                OutputFormatter.PrintError($"Unity tests failed in {payload.Duration:F1}s");
            }

            if (!string.IsNullOrEmpty(payload.RunId))
            {
                Console.WriteLine($"  runId: {payload.RunId}");
            }
            Console.WriteLine($"  status: {payload.Status}");
            if (!string.IsNullOrEmpty(payload.Mode))
            {
                Console.WriteLine($"  mode: {payload.Mode}");
            }
            if (!string.IsNullOrEmpty(payload.QueuedAt))
            {
                Console.WriteLine($"  queuedAt: {payload.QueuedAt}");
            }
            if (!string.IsNullOrEmpty(payload.StartedAt))
            {
                Console.WriteLine($"  startedAt: {payload.StartedAt}");
            }
            Console.WriteLine($"  duration: {payload.Duration:F2}s");
            if (payload.QueuePosition >= 0)
            {
                Console.WriteLine($"  queuePosition: {payload.QueuePosition}");
            }
            Console.WriteLine($"  total: {payload.Total}");
            Console.WriteLine($"  passed: {payload.Passed}");
            Console.WriteLine($"  failed: {payload.Failed}");
            Console.WriteLine($"  skipped: {payload.Skipped}");
            Console.WriteLine($"  inconclusive: {payload.Inconclusive}");

            foreach (var failedTest in payload.FailedTests)
            {
                var failedTestObj = failedTest as dynamic;
                Console.WriteLine($"  failedTest: {failedTestObj.name}");
                if (!string.IsNullOrEmpty(failedTestObj.message))
                {
                    Console.WriteLine($"    message: {failedTestObj.message}");
                }
            }
        }

        static int FinishTestResult(ParsedArgs parsed, OutputMode outputMode, TestResultPayload payload)
        {
            if (payload == null)
            {
                payload = TestResultPayload.FromFailure(null, "Missing test result payload.");
            }

            TryAttachWorkflowResult(parsed, new CommandResult
            {
                success = payload.Success,
                error = payload.Error,
                data = new
                {
                    runId = payload.RunId,
                    status = payload.Status,
                    mode = payload.Mode,
                    queuedAt = payload.QueuedAt,
                    startedAt = payload.StartedAt,
                    duration = Math.Round(payload.Duration, 2),
                    total = payload.Total,
                    passed = payload.Passed,
                    failed = payload.Failed,
                    skipped = payload.Skipped,
                    inconclusive = payload.Inconclusive,
                    startedByInvocation = payload.StartedByInvocation,
                    attachedToExistingRun = payload.AttachedToExistingRun,
                    queuedByInvocation = payload.QueuedByInvocation,
                    statusConfirmed = payload.StatusConfirmed,
                    queuePosition = payload.QueuePosition,
                    requestedFilter = payload.RequestedFilter,
                    executedFilter = payload.ExecutedFilter,
                    failedTests = payload.FailedTests
                }
            }, payload.Success ? 0 : 1);

            OutputTestResult(outputMode, payload);
            return payload.Success ? 0 : 1;
        }

        private sealed class TestResultPayload
        {
            public bool Success;
            public string RunId;
            public string Status;
            public string Mode;
            public string QueuedAt;
            public string StartedAt;
            public double Duration;
            public int Total;
            public int Passed;
            public int Failed;
            public int Skipped;
            public int Inconclusive;
            public bool StartedByInvocation;
            public bool AttachedToExistingRun;
            public bool QueuedByInvocation;
            public bool StatusConfirmed;
            public int QueuePosition;
            public List<object> FailedTests;
            public string Error;
            public JObject RequestedFilter;
            public JObject ExecutedFilter;

            public bool IsFinal => Status == "passed" || Status == "failed" || Status == "timeout";

            public static TestResultPayload FromFailure(string runId, string error)
            {
                return new TestResultPayload
                {
                    Success = false,
                    RunId = runId,
                    Status = "failed",
                    Duration = 0,
                    FailedTests = new List<object>(),
                    Error = error,
                    StatusConfirmed = false,
                    QueuePosition = -1
                };
            }

            public static TestResultPayload TimedOut(string runId, double duration, string error)
            {
                return new TestResultPayload
                {
                    Success = false,
                    RunId = runId,
                    Status = "timeout",
                    Duration = duration,
                    FailedTests = new List<object>(),
                    Error = error,
                    StatusConfirmed = false,
                    QueuePosition = -1
                };
            }
        }
    }
}
