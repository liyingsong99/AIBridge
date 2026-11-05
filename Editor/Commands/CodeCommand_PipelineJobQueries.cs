using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using AIBridge.Internal.Json;
using UnityEditor;

namespace AIBridge.Editor
{
    public partial class CodeCommand
    {
        private const int MaxJobWaiters = 32;
        private const int MaxJobListFiles = 4096;
        private static readonly Dictionary<string, LocalJobWait> JobWaiters = new Dictionary<string, LocalJobWait>();

        private static CommandResult ListPipelineJobs(CommandRequest request)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "limit") return CommandResult.Failure(request.id, "job_list accepts only limit.");
            var limit = request.GetParam("limit", 20);
            if (limit < 1 || limit > 100) return CommandResult.Failure(request.id, "limit must be between 1 and 100.");
            try
            {
                var records = ReadLocalJobs(out var invalidRecords);
                records.Sort((a, b) => string.Compare(b.startedAt, a.startedAt, StringComparison.Ordinal));
                var jobs = new List<object>();
                foreach (var record in records)
                {
                    if (jobs.Count >= limit) break;
                    jobs.Add(new { requestId = record.requestId, remoteJobId = record.remoteJobId, status = record.status,
                        jobState = record.jobState, executionEnded = record.executionEnded, busy = record.busy,
                        waitCanceled = record.waitCanceled, startedAt = record.startedAt, lastObservedAt = record.lastObservedAt,
                        sideEffectsUnknown = true, upstreamExecutionTimeoutDisabled = true });
                }
                return CommandResult.Success(request.id, new { localOnly = true, jobs, count = jobs.Count, total = records.Count, invalidRecords, incomplete = invalidRecords != 0 });
            }
            catch (Exception)
            {
                return CommandResult.Failure(request.id, "Local job records are corrupt, unreadable or exceed the scan limit; no remote request was made.");
            }
        }

        private static List<PipelineOperationRecord> ReadLocalJobs(out int invalidRecords)
        {
            invalidRecords = 0;
            var directory = Path.GetDirectoryName(PipelineRecordPath(null));
            var records = new List<PipelineOperationRecord>();
            string[] files;
            try { files = Directory.GetFiles(directory, "*.json"); }
            catch (DirectoryNotFoundException) { return records; }
            if (files.Length > MaxJobListFiles) throw new IOException();
            foreach (var file in files)
            {
                if (Path.GetFileName(file) == "latest.json") continue;
                try
                {
                    var data = AIBridgeJson.DeserializeObjectStrict(File.ReadAllText(file));
                    var id = data == null ? null : ReadRecordText(data, "requestId");
                    if (string.IsNullOrEmpty(id) || Path.GetFileName(file) != HashText(id) + ".json")
                    { invalidRecords++; continue; }
                    var record = LoadPipelineRecordFile(file, id, out var invalid);
                    if (invalid || record == null) { invalidRecords++; continue; }
                    if (record.detached) records.Add(record);
                }
                catch (Exception) { invalidRecords++; }
            }
            return records;
        }

        private static CommandResult WaitPipelineJob(CommandRequest request)
        {
            foreach (var key in request.@params.Keys)
                if (key != "action" && key != "requestId" && key != "timeout")
                    return CommandResult.Failure(request.id, "job_wait accepts only requestId and timeout.");
            if (!request.@params.TryGetValue("requestId", out var idValue) || !(idValue is string id) || string.IsNullOrWhiteSpace(id))
                return CommandResult.Failure(request.id, "job_wait requires the original AIBridge requestId.");
            var timeout = request.GetParam("timeout", DefaultTimeoutMs);
            if (timeout < MinTimeoutMs || timeout > MaxTimeoutMs)
                return CommandResult.Failure(request.id, "job_wait timeout must be 1000-60000 ms.");
            var wait = new LocalJobWait { RequestId = request.id, TargetId = id, TimeoutMs = timeout };
            var immediate = ReadJobWaitResult(wait, false);
            if (immediate != null) return immediate;
            if (JobWaiters.Count >= MaxJobWaiters || JobWaiters.ContainsKey(request.id))
                return CommandResult.Failure(request.id, "Too many local job waiters or duplicate wait request.");
            JobWaiters.Add(request.id, wait);
            EditorApplication.update -= UpdateJobWaiters; EditorApplication.update += UpdateJobWaiters;
            return null;
        }

        private static CommandResult ReadJobWaitResult(LocalJobWait wait, bool expired)
        {
            PipelineOperationRecord record = null;
            if (_activeOperation is PipelineJobOperation operation && operation.RequestId == wait.TargetId)
                record = operation.BuildStatusData() as PipelineOperationRecord;
            if (record == null)
            {
                record = LoadPipelineRecordWithStatus(wait.TargetId, out var invalid);
                if (invalid) return CommandResult.Failure(wait.RequestId, "Job record is corrupt or unreadable; do not replay.");
            }
            if (record == null || !record.detached) return CommandResult.Failure(wait.RequestId, "No registered detached job matches requestId.");
            if (record.executionEnded)
                return record.status == "validated" || record.status == "executed" ? CommandResult.Success(wait.RequestId, record)
                    : FailureWithData(wait.RequestId, record.error ?? "Job ended without business success.", record);
            if (expired || record.status == "unknown" || record.waitCanceled)
                return FailureWithData(wait.RequestId, "Observation ended without a confirmed execution result; no cancellation or replay was sent.", record);
            return null;
        }

        private static void UpdateJobWaiters()
        {
            foreach (var pair in new List<KeyValuePair<string, LocalJobWait>>(JobWaiters))
            {
                var wait = pair.Value;
                if (wait.Watch.ElapsedMilliseconds < wait.NextReadMs) continue;
                wait.NextReadMs = wait.Watch.ElapsedMilliseconds + 100;
                CommandResult result;
                try { result = ReadJobWaitResult(wait, wait.Watch.ElapsedMilliseconds >= wait.TimeoutMs); }
                catch (Exception) { result = CommandResult.Failure(wait.RequestId, "Local job observation failed; no remote request was made."); }
                if (result == null) continue;
                JobWaiters.Remove(pair.Key); WriteResultFile(result);
            }
            if (JobWaiters.Count == 0) EditorApplication.update -= UpdateJobWaiters;
        }

        private sealed class LocalJobWait
        {
            public string RequestId, TargetId;
            public int TimeoutMs;
            public long NextReadMs;
            public readonly Stopwatch Watch = Stopwatch.StartNew();
        }
    }
}
