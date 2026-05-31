using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AIBridgeCodeIndex
{
    internal sealed class CodeIndexQueryScheduler : IDisposable
    {
        private const int DefaultQueueTimeoutMs = 60000;
        private const int DefaultExecuteTimeoutMs = 30000;
        private const int MaxTimeoutMs = 600000;
        private const int QueryCacheTtlMs = 300000;
        private const int MaxQueryCacheEntries = 128;
        private const int MaxQueryCacheResponseChars = 262144;

        private readonly object _sync = new object();
        private readonly LinkedList<ScheduledQuery> _queue = new LinkedList<ScheduledQuery>();
        private readonly Dictionary<string, QueryCacheEntry> _queryCache = new Dictionary<string, QueryCacheEntry>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly Func<CodeIndexRequest, CancellationToken, Task<CodeIndexResponse>> _executeAsync;
        private readonly Func<string, string, CodeIndexResponse> _buildFailure;
        private readonly Action _statusChanged;
        private readonly int _capacity;
        private readonly Task _workerTask;
        private ScheduledQuery _active;
        private long _lastQueuedMs;
        private long _lastExecutionMs;
        private long _totalQueued;
        private long _totalCompleted;
        private long _totalTimedOut;
        private long _totalDeduplicated;
        private long _queryCacheHits;
        private long _queryCacheMisses;
        private string _queryCacheGenerationHash;
        private bool _disposed;

        public CodeIndexQueryScheduler(
            int capacity,
            Func<CodeIndexRequest, CancellationToken, Task<CodeIndexResponse>> executeAsync,
            Func<string, string, CodeIndexResponse> buildFailure,
            Action statusChanged)
        {
            _capacity = Math.Max(1, capacity);
            _executeAsync = executeAsync;
            _buildFailure = buildFailure;
            _statusChanged = statusChanged;
            _totalDeduplicated = 0;
            _workerTask = Task.Run(ProcessLoopAsync);
        }

        public Task<CodeIndexResponse> EnqueueAsync(CodeIndexRequest request, CancellationToken cancellationToken)
        {
            var query = request ?? new CodeIndexRequest();
            var scheduled = new ScheduledQuery
            {
                Request = query,
                RequestId = Guid.NewGuid().ToString("N"),
                Action = string.IsNullOrWhiteSpace(query.action) ? "unknown" : query.action.Trim().ToLowerInvariant(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                QueueTimeoutMs = NormalizeTimeout(query.queueTimeoutMs, DefaultQueueTimeoutMs),
                ExecuteTimeoutMs = NormalizeTimeout(query.executeTimeoutMs, DefaultExecuteTimeoutMs),
                Completion = new TaskCompletionSource<CodeIndexResponse>(TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_sync)
            {
                ThrowIfDisposed();
                CodeIndexResponse cachedResponse;
                if (TryGetCachedResponse(scheduled, out cachedResponse))
                {
                    return Task.FromResult(cachedResponse);
                }

                if (_queue.Count >= _capacity)
                {
                    return Task.FromResult(BuildQueueFailure(scheduled, "queue_full", "Code index query queue is full."));
                }

                scheduled.Node = _queue.AddLast(scheduled);
                _totalQueued++;
            }

            scheduled.CancellationRegistration = cancellationToken.Register(() => CancelQueued(scheduled, "client_cancelled", "Code index query was cancelled before execution."));
            scheduled.QueueTimeoutCancellation = new CancellationTokenSource();
            scheduled.QueueTimeoutCancellation.CancelAfter(scheduled.QueueTimeoutMs);
            scheduled.QueueTimeoutRegistration = scheduled.QueueTimeoutCancellation.Token.Register(() => CancelQueued(
                scheduled,
                "queue_timeout",
                "Code index query timed out in queue after " + scheduled.QueueTimeoutMs + "ms."));

            _signal.Release();
            NotifyStatusChanged();
            return scheduled.Completion.Task;
        }

        public CodeIndexQuerySchedulerStats GetStats()
        {
            lock (_sync)
            {
                return new CodeIndexQuerySchedulerStats
                {
                    QueueLength = _queue.Count,
                    QueueCapacity = _capacity,
                    ActiveRequestId = _active == null ? null : _active.RequestId,
                    ActiveAction = _active == null ? null : _active.Action,
                    ActiveStartedAt = _active == null ? null : _active.ActiveStartedAt,
                    LastQueuedMs = _lastQueuedMs,
                    LastExecutionMs = _lastExecutionMs,
                    TotalQueued = _totalQueued,
                    TotalCompleted = _totalCompleted,
                    TotalTimedOut = _totalTimedOut,
                    TotalDeduplicated = _totalDeduplicated,
                    QueryCacheCount = _queryCache.Count,
                    QueryCacheHits = _queryCacheHits,
                    QueryCacheMisses = _queryCacheMisses
                };
            }
        }

        public async Task StopAsync(int timeoutMs)
        {
            if (_disposed)
            {
                return;
            }

            _shutdown.Cancel();
            _signal.Release();
            CancelAllQueued();
            await Task.WhenAny(_workerTask, Task.Delay(Math.Max(1, timeoutMs)));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            if (_workerTask.IsCompleted)
            {
                _signal.Dispose();
                _shutdown.Dispose();
            }
        }

        private async Task ProcessLoopAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                ScheduledQuery query;
                lock (_sync)
                {
                    if (_queue.Count == 0)
                    {
                        continue;
                    }

                    query = _queue.First.Value;
                    _queue.RemoveFirst();
                    query.Node = null;
                    query.Started = true;
                    query.ActiveStartedAt = DateTimeOffset.Now.ToString("o");
                    _active = query;
                    _lastQueuedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - query.EnqueuedAtUtc).TotalMilliseconds);
                }

                DisposeQueueWaitRegistrations(query);
                NotifyStatusChanged();
                await ExecuteScheduledQueryAsync(query);
            }
        }

        private async Task ExecuteScheduledQueryAsync(ScheduledQuery query)
        {
            var stopwatch = Stopwatch.StartNew();
            CodeIndexResponse response = null;
            var executeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var timeoutTask = Task.Delay(query.ExecuteTimeoutMs, _shutdown.Token);
            var executionTimedOut = false;
            Task<CodeIndexResponse> executionTask = null;

            try
            {
                executionTask = _executeAsync(query.Request, executeCancellation.Token);
                var completed = await Task.WhenAny(executionTask, timeoutTask);
                if (completed == executionTask)
                {
                    response = await executionTask;
                }
                else
                {
                    executionTimedOut = true;
                    executeCancellation.Cancel();
                    response = BuildQueueFailure(
                        query,
                        "execute_timeout",
                        "Code index query execution timed out after " + query.ExecuteTimeoutMs + "ms.");
                    CompleteQuery(query, response, stopwatch.ElapsedMilliseconds, timedOut: true, clearActive: false);

                    try
                    {
                        await executionTask;
                    }
                    catch
                    {
                    }

                    ClearTimedOutActive(query, stopwatch.ElapsedMilliseconds);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                response = BuildQueueFailure(query, "execute_timeout", "Code index query execution was cancelled.");
            }
            catch (Exception ex)
            {
                response = BuildQueueFailure(query, "execute_failed", ex.Message);
            }
            finally
            {
                executeCancellation.Dispose();
            }

            CompleteQuery(query, response, stopwatch.ElapsedMilliseconds, executionTimedOut, clearActive: true);
        }

        private void CompleteQuery(ScheduledQuery query, CodeIndexResponse response, long executionMs, bool timedOut, bool clearActive)
        {
            lock (_sync)
            {
                _lastExecutionMs = executionMs;
                if (timedOut)
                {
                    _totalTimedOut++;
                }
                else
                {
                    _totalCompleted++;
                }

                if (clearActive && ReferenceEquals(_active, query))
                {
                    _active = null;
                }
            }

            if (!timedOut)
            {
                StoreCachedResponse(query, response);
            }

            DecorateResponse(query, response, executionMs);
            query.Completion.TrySetResult(response);
            NotifyStatusChanged();
        }

        private void ClearTimedOutActive(ScheduledQuery query, long executionMs)
        {
            lock (_sync)
            {
                _lastExecutionMs = executionMs;
                if (ReferenceEquals(_active, query))
                {
                    _active = null;
                }
            }

            NotifyStatusChanged();
        }

        private void CancelQueued(ScheduledQuery query, string errorCode, string message)
        {
            var removed = false;
            lock (_sync)
            {
                if (query.Started || query.Node == null)
                {
                    return;
                }

                _queue.Remove(query.Node);
                query.Node = null;
                removed = true;
                if (string.Equals(errorCode, "queue_timeout", StringComparison.OrdinalIgnoreCase))
                {
                    _totalTimedOut++;
                }
            }

            if (!removed)
            {
                return;
            }

            DisposeQueueWaitRegistrations(query);
            query.Completion.TrySetResult(BuildQueueFailure(query, errorCode, message));
            NotifyStatusChanged();
        }

        private void CancelAllQueued()
        {
            List<ScheduledQuery> queued;
            lock (_sync)
            {
                queued = new List<ScheduledQuery>(_queue);
                _queue.Clear();
                for (var i = 0; i < queued.Count; i++)
                {
                    queued[i].Node = null;
                }
            }

            for (var i = 0; i < queued.Count; i++)
            {
                var query = queued[i];
                DisposeQueueWaitRegistrations(query);
                query.Completion.TrySetResult(BuildQueueFailure(query, "client_cancelled", "Code index daemon is stopping."));
            }

            NotifyStatusChanged();
        }

        private CodeIndexResponse BuildQueueFailure(ScheduledQuery query, string errorCode, string message)
        {
            var response = _buildFailure == null
                ? new CodeIndexResponse { success = false, error = message }
                : _buildFailure(errorCode, message);
            DecorateResponse(query, response, 0);
            return response;
        }

        private void DecorateResponse(ScheduledQuery query, CodeIndexResponse response, long executionMs)
        {
            if (response == null)
            {
                return;
            }

            response.requestId = query.RequestId;
            response.queuedMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - query.EnqueuedAtUtc).TotalMilliseconds);
            response.executionMs = Math.Max(0, executionMs);
            var stats = GetStats();
            response.queueLength = stats.QueueLength;
            response.queueCapacity = stats.QueueCapacity;
            response.activeRequestId = stats.ActiveRequestId;
            response.activeAction = stats.ActiveAction;
            response.activeStartedAt = stats.ActiveStartedAt;
            response.lastQueuedMs = stats.LastQueuedMs;
            response.lastExecutionMs = stats.LastExecutionMs;
            response.totalQueued = stats.TotalQueued;
            response.totalCompleted = stats.TotalCompleted;
            response.totalTimedOut = stats.TotalTimedOut;
            response.totalDeduplicated = stats.TotalDeduplicated;
            response.queryCacheCount = stats.QueryCacheCount;
            response.queryCacheHits = stats.QueryCacheHits;
            response.queryCacheMisses = stats.QueryCacheMisses;
        }

        private bool TryGetCachedResponse(ScheduledQuery query, out CodeIndexResponse response)
        {
            response = null;
            if (!CanCache(query.Request))
            {
                return false;
            }

            EnsureCacheGeneration(query.Request.generationHash);
            var key = BuildCacheKey(query.Request);
            QueryCacheEntry entry;
            if (!_queryCache.TryGetValue(key, out entry))
            {
                _queryCacheMisses++;
                return false;
            }

            if ((DateTimeOffset.UtcNow - entry.StoredAtUtc).TotalMilliseconds > QueryCacheTtlMs)
            {
                _queryCache.Remove(key);
                _queryCacheMisses++;
                return false;
            }

            _queryCacheHits++;
            entry.LastUsedAtUtc = DateTimeOffset.UtcNow;
            response = CloneResponse(entry.Response);
            response.cacheHit = true;
            DecorateResponse(query, response, 0);
            return true;
        }

        private void StoreCachedResponse(ScheduledQuery query, CodeIndexResponse response)
        {
            if (response == null || !response.success || !CanCache(query.Request))
            {
                return;
            }

            var serialized = JsonConvert.SerializeObject(response);
            if (serialized.Length > MaxQueryCacheResponseChars)
            {
                return;
            }

            lock (_sync)
            {
                EnsureCacheGeneration(query.Request.generationHash);
                _queryCache[BuildCacheKey(query.Request)] = new QueryCacheEntry
                {
                    Response = CloneResponse(response),
                    StoredAtUtc = DateTimeOffset.UtcNow,
                    LastUsedAtUtc = DateTimeOffset.UtcNow
                };

                TrimQueryCache();
            }
        }

        private void EnsureCacheGeneration(string generationHash)
        {
            if (string.IsNullOrWhiteSpace(generationHash))
            {
                return;
            }

            if (string.Equals(_queryCacheGenerationHash, generationHash, StringComparison.Ordinal))
            {
                return;
            }

            _queryCache.Clear();
            _queryCacheGenerationHash = generationHash;
        }

        private void TrimQueryCache()
        {
            while (_queryCache.Count > MaxQueryCacheEntries)
            {
                string oldestKey = null;
                var oldestUsedAt = DateTimeOffset.MaxValue;
                foreach (var pair in _queryCache)
                {
                    if (pair.Value.LastUsedAtUtc < oldestUsedAt)
                    {
                        oldestUsedAt = pair.Value.LastUsedAtUtc;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null)
                {
                    return;
                }

                _queryCache.Remove(oldestKey);
            }
        }

        private static bool CanCache(CodeIndexRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.generationHash))
            {
                return false;
            }

            var action = string.IsNullOrWhiteSpace(request.action) ? string.Empty : request.action.Trim().ToLowerInvariant();
            return string.Equals(action, "symbol", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "definition", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "references", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "callers", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "implementations", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(action, "derived", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCacheKey(CodeIndexRequest request)
        {
            var parameters = request.parameters == null ? string.Empty : JsonConvert.SerializeObject(request.parameters);
            return request.generationHash + "|" + request.action.Trim().ToLowerInvariant() + "|" + parameters;
        }

        private static CodeIndexResponse CloneResponse(CodeIndexResponse response)
        {
            return JsonConvert.DeserializeObject<CodeIndexResponse>(JsonConvert.SerializeObject(response));
        }

        private static int NormalizeTimeout(int value, int fallback)
        {
            if (value <= 0)
            {
                return fallback;
            }

            return Math.Min(MaxTimeoutMs, Math.Max(100, value));
        }

        private void DisposeQueueWaitRegistrations(ScheduledQuery query)
        {
            query.CancellationRegistration.Dispose();
            query.QueueTimeoutRegistration.Dispose();
            if (query.QueueTimeoutCancellation != null)
            {
                query.QueueTimeoutCancellation.Dispose();
                query.QueueTimeoutCancellation = null;
            }
        }

        private void NotifyStatusChanged()
        {
            if (_statusChanged != null)
            {
                _statusChanged();
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CodeIndexQueryScheduler));
            }
        }

        private sealed class ScheduledQuery
        {
            public CodeIndexRequest Request { get; set; }
            public string RequestId { get; set; }
            public string Action { get; set; }
            public DateTimeOffset EnqueuedAtUtc { get; set; }
            public int QueueTimeoutMs { get; set; }
            public int ExecuteTimeoutMs { get; set; }
            public TaskCompletionSource<CodeIndexResponse> Completion { get; set; }
            public LinkedListNode<ScheduledQuery> Node { get; set; }
            public CancellationTokenRegistration CancellationRegistration { get; set; }
            public CancellationTokenSource QueueTimeoutCancellation { get; set; }
            public CancellationTokenRegistration QueueTimeoutRegistration { get; set; }
            public bool Started { get; set; }
            public string ActiveStartedAt { get; set; }
        }

        private sealed class QueryCacheEntry
        {
            public CodeIndexResponse Response { get; set; }
            public DateTimeOffset StoredAtUtc { get; set; }
            public DateTimeOffset LastUsedAtUtc { get; set; }
        }
    }

    internal sealed class CodeIndexQuerySchedulerStats
    {
        public int QueueLength { get; set; }
        public int QueueCapacity { get; set; }
        public string ActiveRequestId { get; set; }
        public string ActiveAction { get; set; }
        public string ActiveStartedAt { get; set; }
        public long LastQueuedMs { get; set; }
        public long LastExecutionMs { get; set; }
        public long TotalQueued { get; set; }
        public long TotalCompleted { get; set; }
        public long TotalTimedOut { get; set; }
        public long TotalDeduplicated { get; set; }
        public int QueryCacheCount { get; set; }
        public long QueryCacheHits { get; set; }
        public long QueryCacheMisses { get; set; }
    }
}
