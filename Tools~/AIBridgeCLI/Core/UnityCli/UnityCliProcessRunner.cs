using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core.UnityCli
{
    /// <summary>有界 UTF-8 stdout/stderr，原生 argv，超时/取消时终止进程树。</summary>
    public sealed class UnityCliProcessRunner
    {
        private const int CleanupTimeoutMs = 1000;
        private const int MaxCaptureChars = 4194304;

        public UnityCliProcessResult Execute(UnityCliProcessRequest request, CancellationToken cancellationToken = default)
        {
            Validate(request);
            return ExecuteAsync(request, cancellationToken).GetAwaiter().GetResult();
        }

        private static void Validate(UnityCliProcessRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExecutablePath) || !Path.IsPathRooted(request.ExecutablePath))
                throw new ArgumentException("An absolute executable path is required.");
            if (request.TimeoutMs < 1 || request.TimeoutMs > UnityCliSettings.MaxProbeTimeoutMs)
                throw new ArgumentException("Timeout must be between 1 and 60000ms.");
            if (request.MaxOutputChars < 1 || request.MaxOutputChars > MaxCaptureChars || request.MaxRecords < 1)
                throw new ArgumentException("Invalid process output limits.");
            if (!Enum.IsDefined(typeof(UnityCliOutputFormat), request.OutputFormat))
                throw new ArgumentException("Unknown output format.");
        }

        private static async Task<UnityCliProcessResult> ExecuteAsync(UnityCliProcessRequest request, CancellationToken cancellationToken)
        {
            var result = new UnityCliProcessResult();
            var timer = Stopwatch.StartNew();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var readCancellation = new CancellationTokenSource())
            using (var process = new Process { StartInfo = CreateStartInfo(request) })
            {
                deadline.CancelAfter(request.TimeoutMs);
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    process.Start();
                    result.started = true;
                    // 两个流必须同时排空；即使捕获已满也继续读取，避免管道填满导致死锁。
                    var stdout = CaptureAsync(process.StandardOutput, request.MaxOutputChars, readCancellation.Token);
                    var stderr = CaptureAsync(process.StandardError, request.MaxOutputChars, readCancellation.Token);
                    await WaitForProcessAsync(process, deadline.Token, cancellationToken, result).ConfigureAwait(false);
                    await CollectOutputAsync(stdout, stderr, readCancellation, result).ConfigureAwait(false);
                    result.exitCode = process.HasExited ? process.ExitCode : (int?)null;
                    CompleteResult(request, result);
                }
                catch (OperationCanceledException)
                {
                    result.cancelled = cancellationToken.IsCancellationRequested;
                    result.timedOut = !result.cancelled;
                    result.retryable = true;
                    result.errorCode = result.cancelled ? "PROCESS_CANCELLED" : "PROCESS_TIMEOUT";
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                {
                    Kill(process);
                    result.errorCode = "PROCESS_START_OR_IO_FAILED";
                    result.error = ex.Message;
                }
            }
            result.durationMs = timer.ElapsedMilliseconds;
            return result;
        }

        private static ProcessStartInfo CreateStartInfo(UnityCliProcessRequest request)
        {
            var info = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                WorkingDirectory = request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            // ArgumentList 负责 Windows 引号、空格和尾部反斜杠，禁止拼接到 cmd / PowerShell。
            foreach (var argument in request.Arguments ?? Array.Empty<string>()) info.ArgumentList.Add(argument);
            return info;
        }

        private static async Task WaitForProcessAsync(Process process, CancellationToken deadline, CancellationToken cancellation, UnityCliProcessResult result)
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(deadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.cancelled = cancellation.IsCancellationRequested;
                result.timedOut = !result.cancelled;
                result.retryable = true;
                result.errorCode = result.cancelled ? "PROCESS_CANCELLED" : "PROCESS_TIMEOUT";
                result.error = result.cancelled ? "Unity CLI process cancelled." : "Unity CLI process timed out.";
                Kill(process);
                using (var cleanup = new CancellationTokenSource(CleanupTimeoutMs))
                {
                    try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* 清理本身有上限，保留原始超时信息。 */ }
                }
            }
        }

        private static async Task CollectOutputAsync(Task<CapturedOutput> stdout, Task<CapturedOutput> stderr,
            CancellationTokenSource cancellation, UnityCliProcessResult result)
        {
            var streams = Task.WhenAll(stdout, stderr);
            // 子进程可能把管道句柄传给后代；主进程退出也不能无期限等待 EOF。
            if (await Task.WhenAny(streams, Task.Delay(CleanupTimeoutMs)).ConfigureAwait(false) != streams)
            {
                cancellation.Cancel();
                result.errorCode = result.errorCode ?? "OUTPUT_DRAIN_TIMEOUT";
                result.error = result.error ?? "Output pipes did not close after process exit.";
                result.retryable = true;
            }
            var output = await streams.ConfigureAwait(false);
            result.stdout = output[0].Text;
            result.stderr = output[1].Text;
            result.outputTruncated = output[0].Truncated || output[1].Truncated;
        }

        private static async Task<CapturedOutput> CaptureAsync(StreamReader reader, int limit, CancellationToken cancellation)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            var result = new CapturedOutput();
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) > 0)
                {
                    var take = Math.Min(count, limit - text.Length);
                    text.Append(buffer, 0, take);
                    result.Truncated |= take < count;
                }
            }
            catch (OperationCanceledException) { result.Truncated = true; }
            result.Text = text.ToString();
            return result;
        }

        private static void CompleteResult(UnityCliProcessRequest request, UnityCliProcessResult result)
        {
            if (result.errorCode != null) return;
            if (result.exitCode != 0)
            {
                result.errorCode = "PROCESS_EXIT_NONZERO";
                result.error = "Unity CLI exited with code " + result.exitCode + ".";
                return;
            }
            if (result.outputTruncated)
            {
                result.errorCode = "OUTPUT_LIMIT_EXCEEDED";
                result.error = "Unity CLI output exceeded the capture limit.";
                return;
            }
            try
            {
                ParseRecords(request, result);
                result.success = true;
            }
            catch (JsonException ex)
            {
                result.errorCode = "INVALID_STRUCTURED_OUTPUT";
                result.error = ex.Message;
            }
        }

        private static void ParseRecords(UnityCliProcessRequest request, UnityCliProcessResult result)
        {
            if (request.OutputFormat == UnityCliOutputFormat.Text) return;
            if (request.OutputFormat == UnityCliOutputFormat.Json)
            {
                result.records.Add(JToken.Parse(result.stdout));
                return;
            }
            using (var reader = new StringReader(result.stdout))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (result.records.Count >= request.MaxRecords) throw new JsonReaderException("NDJSON record limit exceeded.");
                    result.records.Add(JToken.Parse(line));
                }
            }
            if (result.records.Count == 0) throw new JsonReaderException("NDJSON output was empty.");
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
            { /* 最佳努力终止，调用结果仍显示超时或启动失败。 */ }
        }

        private sealed class CapturedOutput
        {
            public string Text { get; set; }
            public bool Truncated { get; set; }
        }
    }
}
