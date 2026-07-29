using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AIBridgeCodeIndex
{
    internal sealed class CodeIndexServer
    {
        private const int QueryQueueCapacity = 64;
        private const int StatusFileWriteRetryCount = 5;
        private const int StatusFileWriteRetryDelayMs = 20;
        private const int SourceFileCountForForcedMemoryCollection = 1000;
        private const int MaxConcurrentClients = 32;
        private const int MaxHttpHeaderBytes = 65536;
        private const int MaxRequestBodyBytes = 262144;
        private const int NetworkOperationTimeoutMs = 10000;
        private const int BackgroundTaskStopTimeoutMs = 500;
        private const int QuerySchedulerStopTimeoutMs = 500;
        private const int DaemonLaunchLockWaitMs = 1500;
        private const int DaemonLaunchLockRetryDelayMs = 100;
        private const long ProcessStartTicksTolerance = TimeSpan.TicksPerSecond * 2L;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr processHandle);

        private readonly CodeIndexOptions _options;
        private readonly object _statusLock = new object();
        private readonly object _statusFileLock = new object();
        private readonly object _refreshLock = new object();
        private readonly object _workspaceLock = new object();
        private readonly object _clientLock = new object();
        private readonly SemaphoreSlim _queryGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _clientGate = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly HashSet<TcpClient> _activeClients = new HashSet<TcpClient>();
        private readonly HashSet<Task> _activeHandlers = new HashSet<Task>();
        private readonly CodeIndexQueryScheduler _queryScheduler;
        private CodeIndexWorkspace _workspace;
        private TcpListener _listener;
        private CodeIndexStatus _status;
        private Task _warmupTask;
        private Task _refreshTask;
        private Task _ownerMonitorTask;
        private volatile bool _shutdownRequested;

        public CodeIndexServer(CodeIndexOptions options)
        {
            _options = options;
            _workspace = new CodeIndexWorkspace(options.ProjectRoot);
            _queryScheduler = new CodeIndexQueryScheduler(
                QueryQueueCapacity,
                ExecuteScheduledQueryAsync,
                BuildScheduledFailure,
                WriteStatus);
        }

        private CodeIndexWorkspace GetWorkspace()
        {
            lock (_workspaceLock)
            {
                return _workspace;
            }
        }

        public async Task RunAsync()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();

                var endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
                _status = CreateInitialStatus(endpoint);
                WriteStatus();

                _warmupTask = WarmupAsync();
                if (_options.OwnerPid > 0)
                {
                    _ownerMonitorTask = MonitorOwnerProcessAsync();
                }

                while (!_shutdownRequested)
                {
                    try
                    {
                        // 在 accept 前取得名额，避免慢客户端无限制地占用 handler 与 socket。
                        await _clientGate.WaitAsync(_shutdown.Token);
                        var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                        StartClientHandler(client);
                    }
                    catch (OperationCanceledException) when (_shutdownRequested)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (_shutdownRequested)
                    {
                        break;
                    }
                    catch (SocketException) when (_shutdownRequested)
                    {
                        break;
                    }
                }
            }
            finally
            {
                RequestShutdown();
                await WaitForHandlersAsync();
                await WaitForBackgroundTaskAsync(_warmupTask, BackgroundTaskStopTimeoutMs);
                await WaitForBackgroundTaskAsync(_ownerMonitorTask, BackgroundTaskStopTimeoutMs);
                await WaitForBackgroundTaskAsync(_refreshTask, BackgroundTaskStopTimeoutMs);
                await _queryScheduler.StopAsync(QuerySchedulerStopTimeoutMs);

                DisposeWorkspace(GetWorkspace());
                _queryScheduler.Dispose();
                _shutdown.Dispose();
                _clientGate.Dispose();
                CleanupTransientState();
            }
        }

        private async Task WarmupAsync()
        {
            var workspace = GetWorkspace();
            try
            {
                UpdateStatus("loading", null);
                await workspace.WarmupAsync();

                if (_shutdownRequested)
                {
                    return;
                }

                var stale = workspace.IsStale();
                var staleReason = workspace.StaleReason;

                lock (_statusLock)
                {
                    if (_status == null || !string.Equals(_status.state, "loading", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _status.state = "ready";
                    _status.solution = workspace.SolutionPath;
                    _status.workspaceMode = workspace.WorkspaceMode;
                    _status.snapshotExists = workspace.SnapshotExists;
                    _status.snapshotVersion = workspace.SnapshotVersion;
                    _status.generationId = workspace.GenerationId;
                    _status.snapshotContentHash = workspace.SnapshotContentHash;
                    _status.assemblyCount = workspace.AssemblyCount;
                    _status.sourceFileCount = workspace.SourceFileCount;
                    _status.excludedAssemblyCount = workspace.ExcludedAssemblyCount;
                    _status.excludedSourceFileCount = workspace.ExcludedSourceFileCount;
                    _status.includePackageCacheSourceAssemblies = workspace.IncludePackageCacheSourceAssemblies;
                    _status.buildTarget = workspace.BuildTarget;
                    _status.unityVersion = workspace.UnityVersion;
                    _status.staleReason = staleReason;
                    _status.loadedProjects = workspace.LoadedProjects;
                    _status.loadedDocuments = workspace.LoadedDocuments;
                    _status.stale = stale;
                    _status.message = null;
                    _status.updatedAt = DateTimeOffset.Now.ToString("o");
                }

                WriteStatus();
            }
            catch (Exception ex)
            {
                if (_shutdownRequested)
                {
                    return;
                }

                lock (_statusLock)
                {
                    if (_status == null || string.Equals(_status.state, "stopping", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    _status.state = "failed";
                    _status.solution = workspace.SolutionPath;
                    _status.workspaceMode = workspace.WorkspaceMode;
                    _status.snapshotExists = workspace.SnapshotExists;
                    _status.snapshotVersion = workspace.SnapshotVersion;
                    _status.generationId = workspace.GenerationId;
                    _status.snapshotContentHash = workspace.SnapshotContentHash;
                    _status.assemblyCount = workspace.AssemblyCount;
                    _status.sourceFileCount = workspace.SourceFileCount;
                    _status.excludedAssemblyCount = workspace.ExcludedAssemblyCount;
                    _status.excludedSourceFileCount = workspace.ExcludedSourceFileCount;
                    _status.includePackageCacheSourceAssemblies = workspace.IncludePackageCacheSourceAssemblies;
                    _status.buildTarget = workspace.BuildTarget;
                    _status.unityVersion = workspace.UnityVersion;
                    _status.staleReason = workspace.StaleReason;
                    _status.loadedProjects = workspace.LoadedProjects;
                    _status.loadedDocuments = workspace.LoadedDocuments;
                    _status.stale = true;
                    _status.message = ex.Message;
                    _status.updatedAt = DateTimeOffset.Now.ToString("o");
                }

                WriteStatus();
                Log("Warmup failed: " + ex);
            }
        }

        private void StartClientHandler(TcpClient client)
        {
            var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_clientLock)
            {
                if (_shutdownRequested)
                {
                    client.Dispose();
                    _clientGate.Release();
                    return;
                }

                _activeClients.Add(client);
                _activeHandlers.Add(completion.Task);
            }

            _ = Task.Run(() => HandleClientAsync(client, completion));
        }

        private void UnregisterClientHandler(TcpClient client, Task handler)
        {
            lock (_clientLock)
            {
                _activeClients.Remove(client);
                _activeHandlers.Remove(handler);
            }

            _clientGate.Release();
        }

        private async Task HandleClientAsync(TcpClient client, TaskCompletionSource<object> completion)
        {
            Exception failure = null;
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    try
                    {
                        var request = await ReadRequestHeaderAsync(stream);
                        if (request == null)
                        {
                            return;
                        }

                        // 仅解析完 header 就认证；未认证请求不再读取可能很大的正文。
                        if (!IsAuthorized(request))
                        {
                            await WriteResponseAsync(stream, 403, new { success = false, error = "Forbidden" });
                            return;
                        }

                        if (request.Method == "GET" && request.Path == "/status")
                        {
                            var refreshNeeded = MarkRefreshIfNeeded(GetWorkspace());
                            await WriteResponseAsync(stream, 200, CodeIndexResponse.FromStatus(GetStatusSnapshot()));
                            if (refreshNeeded)
                            {
                                ScheduleBackgroundRefresh();
                            }

                            return;
                        }

                        if (request.Method == "POST" && request.Path == "/query")
                        {
                            var bodyText = await ReadRequestBodyAsync(stream, request);
                            var query = JsonConvert.DeserializeObject<CodeIndexRequest>(bodyText);
                            var response = await ExecuteQueryAsync(query);
                            await WriteResponseAsync(stream, response.success ? 200 : 409, response);
                            return;
                        }

                        if (request.Method == "POST" && request.Path == "/shutdown")
                        {
                            UpdateStatus("stopping", null);
                            await WriteResponseAsync(stream, 200, CodeIndexResponse.FromStatus(GetStatusSnapshot()));
                            RequestShutdown();
                            return;
                        }

                        // Connection: close 模式下，未知路由不需要也不应等待请求正文。
                        await WriteResponseAsync(stream, 404, new { success = false, error = "Not found" });
                    }
                    catch (HttpRequestException ex)
                    {
                        Log("Invalid HTTP request: " + ex);
                        await TryWriteResponseAsync(stream, 400, new { success = false, error = "Bad request" });
                    }
                    catch (OperationCanceledException) when (_shutdownRequested)
                    {
                    }
                    catch (IOException ex)
                    {
                        if (!IsClientDisconnect(ex))
                        {
                            Log("Request failed: " + ex);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Request failed: " + ex);
                        await TryWriteResponseAsync(stream, 500, new { success = false, error = "Internal server error" });
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                // handler 无论在读取、执行还是写回阶段失败，finally 都会注销并归还客户端名额。
                UnregisterClientHandler(client, completion.Task);
                if (failure == null)
                {
                    completion.TrySetResult(null);
                }
                else
                {
                    completion.TrySetException(failure);
                }
            }
        }

        private async Task<CodeIndexResponse> ExecuteQueryAsync(CodeIndexRequest query)
        {
            AttachCurrentGeneration(query);
            return await _queryScheduler.EnqueueAsync(query, _shutdown.Token);
        }

        private void AttachCurrentGeneration(CodeIndexRequest query)
        {
            if (query == null || !string.IsNullOrWhiteSpace(query.generationHash))
            {
                return;
            }

            query.generationHash = GetCurrentGenerationHash();
        }

        private string GetCurrentGenerationHash()
        {
            var status = GetStatusSnapshot();
            return !string.IsNullOrWhiteSpace(status.snapshotContentHash)
                ? status.snapshotContentHash
                : status.generationId;
        }

        private static string GetWorkspaceGenerationHash(CodeIndexWorkspace workspace)
        {
            if (workspace == null)
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(workspace.SnapshotContentHash)
                ? workspace.SnapshotContentHash
                : workspace.GenerationId;
        }

        private async Task<CodeIndexResponse> ExecuteScheduledQueryAsync(CodeIndexRequest query, CancellationToken cancellationToken)
        {
            await _queryGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await ExecuteQueryCoreAsync(query, cancellationToken);
            }
            finally
            {
                _queryGate.Release();
            }
        }

        private async Task<CodeIndexResponse> ExecuteQueryCoreAsync(CodeIndexRequest query, CancellationToken cancellationToken)
        {
            var status = GetStatusSnapshot();
            if (query == null || string.IsNullOrWhiteSpace(query.action))
            {
                return BuildFailure(status, "Missing action.", "missing_action");
            }

            query.action = query.action.Trim().ToLowerInvariant();
            if (!IsSupportedQueryAction(query.action))
            {
                return BuildFailure(status, "Unsupported code_index action: " + query.action, "unsupported_action");
            }

            if (!string.Equals(status.state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFailure(status, "Unity snapshot workspace is not ready. Current state: " + status.state, "workspace_not_ready");
            }

            var workspace = GetWorkspace();
            var refreshNeeded = MarkRefreshIfNeeded(workspace);
            status = GetStatusSnapshot();
            if (!string.Equals(status.state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFailure(status, "Unity snapshot workspace is not ready. Current state: " + status.state, "workspace_not_ready");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var response = await ExecuteSingleWorkspaceQueryAsync(query, workspace, status, cancellationToken);

            WriteStatus();
            if (refreshNeeded)
            {
                ScheduleBackgroundRefresh();
            }

            return response;
        }

        private async Task<CodeIndexResponse> ExecuteSingleWorkspaceQueryAsync(
            CodeIndexRequest query,
            CodeIndexWorkspace workspace,
            CodeIndexStatus status,
            CancellationToken cancellationToken)
        {
            var response = await workspace.QueryAsync(query.action, query.parameters);
            cancellationToken.ThrowIfCancellationRequested();
            PopulateWorkspaceResponse(response, workspace, status);
            UpdateStatusFromWorkspace(workspace);
            return response;
        }

        private static bool IsSupportedQueryAction(string action)
        {
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "symbol":
                case "definition":
                    return true;
                default:
                    return false;
            }
        }

        private static void PopulateWorkspaceResponse(CodeIndexResponse response, CodeIndexWorkspace workspace, CodeIndexStatus status)
        {
            response.success = response.error == null;
            if (string.IsNullOrWhiteSpace(response.source))
            {
                response.source = "unity-snapshot";
            }

            response.state = status.state;
            response.stale = status.stale;
            response.projectRoot = status.projectRoot;
            response.solution = workspace.SolutionPath;
            response.workspaceMode = workspace.WorkspaceMode;
            response.snapshotExists = workspace.SnapshotExists;
            response.snapshotVersion = workspace.SnapshotVersion;
            response.generationId = workspace.GenerationId;
            response.snapshotContentHash = workspace.SnapshotContentHash;
            response.assemblyCount = workspace.AssemblyCount;
            response.sourceFileCount = workspace.SourceFileCount;
            response.excludedAssemblyCount = workspace.ExcludedAssemblyCount;
            response.excludedSourceFileCount = workspace.ExcludedSourceFileCount;
            response.includePackageCacheSourceAssemblies = workspace.IncludePackageCacheSourceAssemblies;
            response.buildTarget = workspace.BuildTarget;
            response.unityVersion = workspace.UnityVersion;
            response.staleReason = workspace.StaleReason;
            response.loadedProjects = workspace.LoadedProjects;
            response.loadedDocuments = workspace.LoadedDocuments;
        }

        private void UpdateStatusFromWorkspace(CodeIndexWorkspace workspace)
        {
            lock (_statusLock)
            {
                _status.snapshotExists = workspace.SnapshotExists;
                _status.snapshotVersion = workspace.SnapshotVersion;
                _status.generationId = workspace.GenerationId;
                _status.snapshotContentHash = workspace.SnapshotContentHash;
                _status.assemblyCount = workspace.AssemblyCount;
                _status.sourceFileCount = workspace.SourceFileCount;
                _status.excludedAssemblyCount = workspace.ExcludedAssemblyCount;
                _status.excludedSourceFileCount = workspace.ExcludedSourceFileCount;
                _status.includePackageCacheSourceAssemblies = workspace.IncludePackageCacheSourceAssemblies;
                _status.buildTarget = workspace.BuildTarget;
                _status.unityVersion = workspace.UnityVersion;
                _status.staleReason = workspace.StaleReason;
                _status.loadedProjects = workspace.LoadedProjects;
                _status.loadedDocuments = workspace.LoadedDocuments;
                _status.updatedAt = DateTimeOffset.Now.ToString("o");
            }
        }

        private CodeIndexResponse BuildScheduledFailure(string errorCode, string error)
        {
            return BuildFailure(GetStatusSnapshot(), error, errorCode);
        }

        private static CodeIndexResponse BuildFailure(CodeIndexStatus status, string error, string errorCode)
        {
            return new CodeIndexResponse
            {
                success = false,
                source = "unity-snapshot",
                state = status == null ? "unknown" : status.state,
                stale = true,
                projectRoot = status == null ? null : status.projectRoot,
                solution = status == null ? null : status.solution,
                workspaceMode = status == null ? "unity-snapshot" : status.workspaceMode,
                snapshotExists = status != null && status.snapshotExists,
                snapshotVersion = status == null ? 0 : status.snapshotVersion,
                generationId = status == null ? null : status.generationId,
                snapshotContentHash = status == null ? null : status.snapshotContentHash,
                assemblyCount = status == null ? 0 : status.assemblyCount,
                sourceFileCount = status == null ? 0 : status.sourceFileCount,
                excludedAssemblyCount = status == null ? 0 : status.excludedAssemblyCount,
                excludedSourceFileCount = status == null ? 0 : status.excludedSourceFileCount,
                includePackageCacheSourceAssemblies = status != null && status.includePackageCacheSourceAssemblies,
                buildTarget = status == null ? null : status.buildTarget,
                unityVersion = status == null ? null : status.unityVersion,
                staleReason = status == null ? "unknown" : status.staleReason,
                loadedProjects = status == null ? 0 : status.loadedProjects,
                loadedDocuments = status == null ? 0 : status.loadedDocuments,
                error = error,
                errorCode = errorCode
            };
        }

        private bool MarkRefreshIfNeeded(CodeIndexWorkspace workspace)
        {
            if (!_options.AutoRefresh || !IsStatusReady() || workspace == null || !workspace.IsStale())
            {
                if (IsStatusReady())
                {
                    RefreshStaleState(workspace);
                }

                return false;
            }

            lock (_statusLock)
            {
                var scheduled = false;
                if (_status != null && string.Equals(_status.state, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    _status.stale = true;
                    _status.staleReason = workspace.StaleReason;
                    _status.message = "Unity compilation snapshot changed; refreshing Code Index workspace in background.";
                    _status.updatedAt = DateTimeOffset.Now.ToString("o");
                    scheduled = true;
                }

                if (!scheduled)
                {
                    return false;
                }
            }

            WriteStatus();
            return true;
        }

        private void ScheduleBackgroundRefresh()
        {
            lock (_refreshLock)
            {
                if (_shutdownRequested)
                {
                    return;
                }

                if (_refreshTask != null && !_refreshTask.IsCompleted)
                {
                    return;
                }

                // 查询先使用上一个可用 generation；后台完成后再原子替换 workspace 状态。
                _refreshTask = Task.Run(RefreshWorkspaceInBackgroundAsync);
            }
        }

        private async Task RefreshWorkspaceInBackgroundAsync()
        {
            var nextWorkspace = new CodeIndexWorkspace(_options.ProjectRoot);
            CodeIndexWorkspace oldWorkspaceToDispose = null;
            var swapped = false;
            try
            {
                await nextWorkspace.WarmupAsync();

                if (_shutdownRequested)
                {
                    return;
                }

                await _queryGate.WaitAsync();
                try
                {
                    if (_shutdownRequested || !IsStatusReady())
                    {
                        return;
                    }

                    var stale = nextWorkspace.IsStale();
                    var staleReason = nextWorkspace.StaleReason;
                    lock (_workspaceLock)
                    {
                        oldWorkspaceToDispose = _workspace;
                        _workspace = nextWorkspace;
                        swapped = true;
                    }

                    lock (_statusLock)
                    {
                        if (_status == null || !string.Equals(_status.state, "ready", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        _status.state = "ready";
                        _status.solution = nextWorkspace.SolutionPath;
                        _status.workspaceMode = nextWorkspace.WorkspaceMode;
                        _status.snapshotExists = nextWorkspace.SnapshotExists;
                        _status.snapshotVersion = nextWorkspace.SnapshotVersion;
                        _status.generationId = nextWorkspace.GenerationId;
                        _status.snapshotContentHash = nextWorkspace.SnapshotContentHash;
                        _status.assemblyCount = nextWorkspace.AssemblyCount;
                        _status.sourceFileCount = nextWorkspace.SourceFileCount;
                        _status.excludedAssemblyCount = nextWorkspace.ExcludedAssemblyCount;
                        _status.excludedSourceFileCount = nextWorkspace.ExcludedSourceFileCount;
                        _status.includePackageCacheSourceAssemblies = nextWorkspace.IncludePackageCacheSourceAssemblies;
                        _status.buildTarget = nextWorkspace.BuildTarget;
                        _status.unityVersion = nextWorkspace.UnityVersion;
                        _status.staleReason = staleReason;
                        _status.loadedProjects = nextWorkspace.LoadedProjects;
                        _status.loadedDocuments = nextWorkspace.LoadedDocuments;
                        _status.stale = stale;
                        _status.message = null;
                        _status.updatedAt = DateTimeOffset.Now.ToString("o");
                    }

                    _queryScheduler.InvalidateCacheForGeneration(GetWorkspaceGenerationHash(nextWorkspace));
                    WriteStatus();
                }
                finally
                {
                    _queryGate.Release();
                }

                DisposeWorkspace(oldWorkspaceToDispose, trimWorkingSet: true);
                oldWorkspaceToDispose = null;
            }
            catch (Exception ex)
            {
                if (_shutdownRequested)
                {
                    return;
                }

                var currentWorkspace = GetWorkspace();
                lock (_statusLock)
                {
                    if (_status != null && string.Equals(_status.state, "ready", StringComparison.OrdinalIgnoreCase))
                    {
                        _status.state = "ready";
                        _status.stale = true;
                        _status.staleReason = currentWorkspace == null ? "backgroundRefreshFailed" : currentWorkspace.StaleReason;
                        _status.message = "Background refresh failed: " + ex.Message;
                        _status.updatedAt = DateTimeOffset.Now.ToString("o");
                    }
                }

                WriteStatus();
                Log("Background refresh failed: " + ex);
            }
            finally
            {
                DisposeWorkspace(oldWorkspaceToDispose);
                if (!swapped)
                {
                    DisposeWorkspace(nextWorkspace);
                }
            }
        }

        private void RefreshStaleState()
        {
            RefreshStaleState(GetWorkspace());
        }

        private void DisposeWorkspace(CodeIndexWorkspace workspace)
        {
            DisposeWorkspace(workspace, trimWorkingSet: true);
        }

        private void DisposeWorkspace(CodeIndexWorkspace workspace, bool trimWorkingSet)
        {
            if (workspace == null)
            {
                return;
            }

            try
            {
                var shouldCollect = ShouldCollectReleasedWorkspaceMemory(workspace);
                workspace.Dispose();
                if (shouldCollect)
                {
                    CollectReleasedWorkspaceMemory(trimWorkingSet);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to dispose stale Code Index workspace: " + ex.Message);
            }
        }

        private static bool ShouldCollectReleasedWorkspaceMemory(CodeIndexWorkspace workspace)
        {
            return workspace != null
                   && workspace.SourceFileCount >= SourceFileCountForForcedMemoryCollection;
        }

        private void CollectReleasedWorkspaceMemory(bool trimWorkingSet)
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                if (trimWorkingSet)
                {
                    TrimCurrentProcessWorkingSet();
                }
            }
            catch (Exception ex)
            {
                Log("Failed to collect released Code Index workspace memory: " + ex.Message);
            }
        }

        private void TrimCurrentProcessWorkingSet()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    EmptyWorkingSet(process.Handle);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to trim Code Index working set: " + ex.Message);
            }
        }

        private void RefreshStaleState(CodeIndexWorkspace workspace)
        {
            var stale = workspace == null || workspace.IsStale();
            var staleReason = workspace == null ? "missingWorkspace" : workspace.StaleReason;
            lock (_statusLock)
            {
                if (_status != null && string.Equals(_status.state, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    _status.stale = stale;
                    _status.staleReason = staleReason;
                    _status.updatedAt = DateTimeOffset.Now.ToString("o");
                }
            }

            WriteStatus();
        }

        private bool IsStatusReady()
        {
            lock (_statusLock)
            {
                return _status != null && string.Equals(_status.state, "ready", StringComparison.OrdinalIgnoreCase);
            }
        }

        private async Task MonitorOwnerProcessAsync()
        {
            var missingTicks = 0;
            while (!_shutdownRequested)
            {
                var ownerState = GetOwnerProcessState();
                lock (_statusLock)
                {
                    if (_status != null)
                    {
                        _status.ownerAlive = ownerState.Alive;
                        _status.ownerMonitorMode = ownerState.MonitorMode;
                    }
                }

                if (!ownerState.Alive)
                {
                    missingTicks++;
                    if (missingTicks >= 3)
                    {
                        UpdateStatus("stopping", "Owner process exited; stopping code_index daemon.");
                        RequestShutdown();
                        return;
                    }
                }
                else
                {
                    missingTicks = 0;
                }

                await Task.Delay(1000);
            }
        }

        private OwnerProcessState GetOwnerProcessState()
        {
            if (_options.OwnerPid <= 0)
            {
                return new OwnerProcessState(true, "none");
            }

            try
            {
                using (var process = Process.GetProcessById(_options.OwnerPid))
                {
                    if (process.HasExited)
                    {
                        return new OwnerProcessState(false, GetOwnerMonitorMode());
                    }

                    if (_options.OwnerStartTicks <= 0L)
                    {
                        return new OwnerProcessState(true, "pidOnly");
                    }

                    var startTicks = process.StartTime.ToUniversalTime().Ticks;
                    var matches = Math.Abs(startTicks - _options.OwnerStartTicks) <= TimeSpan.FromSeconds(2).Ticks;
                    return new OwnerProcessState(matches, "verified");
                }
            }
            catch
            {
                return new OwnerProcessState(false, GetOwnerMonitorMode());
            }
        }

        private string GetOwnerMonitorMode()
        {
            if (_options.OwnerPid <= 0)
            {
                return "none";
            }

            return _options.OwnerStartTicks > 0L ? "verified" : "pidOnly";
        }

        private struct OwnerProcessState
        {
            public readonly bool Alive;
            public readonly string MonitorMode;

            public OwnerProcessState(bool alive, string monitorMode)
            {
                Alive = alive;
                MonitorMode = monitorMode;
            }
        }

        private void RequestShutdown()
        {
            if (_shutdownRequested)
            {
                return;
            }

            _shutdownRequested = true;
            _shutdown.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch
            {
            }

            TcpClient[] clients;
            lock (_clientLock)
            {
                clients = new TcpClient[_activeClients.Count];
                _activeClients.CopyTo(clients);
            }

            // 关闭 socket 以打断不支持 CancellationToken 的底层网络读写，随后再等待 handler 退出。
            foreach (var client in clients)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }

        private async Task WaitForHandlersAsync()
        {
            Task[] handlers;
            lock (_clientLock)
            {
                handlers = new Task[_activeHandlers.Count];
                _activeHandlers.CopyTo(handlers);
            }

            if (handlers.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(handlers);
            }
            catch (Exception ex)
            {
                Log("Client handler ended with an error during shutdown: " + ex);
            }
        }

        private static async Task WaitForBackgroundTaskAsync(Task task, int timeoutMs)
        {
            if (task != null)
            {
                await Task.WhenAny(task, Task.Delay(timeoutMs));
            }
        }

        private CodeIndexStatus CreateInitialStatus(string endpoint)
        {
            var now = DateTimeOffset.Now.ToString("o");
            return new CodeIndexStatus
            {
                projectRoot = _options.ProjectRoot,
                projectHash = ComputeProjectHash(_options.ProjectRoot),
                unityPid = _options.UnityPid,
                ownerPid = _options.OwnerPid,
                ownerStartTicks = _options.OwnerStartTicks,
                ownerAlive = _options.OwnerPid <= 0 || GetOwnerProcessState().Alive,
                ownerMonitorMode = GetOwnerMonitorMode(),
                daemonPid = Process.GetCurrentProcess().Id,
                endpoint = endpoint,
                token = _options.Token,
                state = "starting",
                stale = true,
                solution = _workspace.SolutionPath,
                workspaceMode = _workspace.WorkspaceMode,
                snapshotExists = _workspace.SnapshotExists,
                snapshotVersion = _workspace.SnapshotVersion,
                generationId = _workspace.GenerationId,
                snapshotContentHash = _workspace.SnapshotContentHash,
                assemblyCount = _workspace.AssemblyCount,
                sourceFileCount = _workspace.SourceFileCount,
                excludedAssemblyCount = _workspace.ExcludedAssemblyCount,
                excludedSourceFileCount = _workspace.ExcludedSourceFileCount,
                includePackageCacheSourceAssemblies = _workspace.IncludePackageCacheSourceAssemblies,
                buildTarget = _workspace.BuildTarget,
                unityVersion = _workspace.UnityVersion,
                staleReason = "starting",
                startedAt = now,
                updatedAt = now
            };
        }

        private void UpdateStatus(string state, string message)
        {
            lock (_statusLock)
            {
                _status.state = state;
                _status.message = message;
                _status.updatedAt = DateTimeOffset.Now.ToString("o");
            }

            WriteStatus();
        }

        private CodeIndexStatus GetStatusSnapshot()
        {
            CodeIndexStatus snapshot;
            lock (_statusLock)
            {
                snapshot = new CodeIndexStatus
                {
                    projectRoot = _status.projectRoot,
                    projectHash = _status.projectHash,
                    unityPid = _status.unityPid,
                    ownerPid = _status.ownerPid,
                    ownerStartTicks = _status.ownerStartTicks,
                    ownerAlive = _status.ownerAlive,
                    ownerMonitorMode = _status.ownerMonitorMode,
                    daemonPid = _status.daemonPid,
                    endpoint = _status.endpoint,
                    token = _status.token,
                    state = _status.state,
                    stale = _status.stale,
                    solution = _status.solution,
                    workspaceMode = _status.workspaceMode,
                    snapshotExists = _status.snapshotExists,
                    snapshotVersion = _status.snapshotVersion,
                    generationId = _status.generationId,
                    snapshotContentHash = _status.snapshotContentHash,
                    assemblyCount = _status.assemblyCount,
                    sourceFileCount = _status.sourceFileCount,
                    excludedAssemblyCount = _status.excludedAssemblyCount,
                    excludedSourceFileCount = _status.excludedSourceFileCount,
                    includePackageCacheSourceAssemblies = _status.includePackageCacheSourceAssemblies,
                    buildTarget = _status.buildTarget,
                    unityVersion = _status.unityVersion,
                    staleReason = _status.staleReason,
                    loadedProjects = _status.loadedProjects,
                    loadedDocuments = _status.loadedDocuments,
                    startedAt = _status.startedAt,
                    updatedAt = _status.updatedAt,
                    message = _status.message
                };
            }

            ApplyQuerySchedulerStats(snapshot);
            return snapshot;
        }

        private void ApplyQuerySchedulerStats(CodeIndexStatus status)
        {
            if (status == null || _queryScheduler == null)
            {
                return;
            }

            var stats = _queryScheduler.GetStats();
            status.queueLength = stats.QueueLength;
            status.queueCapacity = stats.QueueCapacity;
            status.activeRequestId = stats.ActiveRequestId;
            status.activeAction = stats.ActiveAction;
            status.activeStartedAt = stats.ActiveStartedAt;
            status.lastQueuedMs = stats.LastQueuedMs;
            status.lastExecutionMs = stats.LastExecutionMs;
            status.totalQueued = stats.TotalQueued;
            status.totalCompleted = stats.TotalCompleted;
            status.totalTimedOut = stats.TotalTimedOut;
            status.totalDeduplicated = stats.TotalDeduplicated;
            status.queryCacheCount = stats.QueryCacheCount;
            status.queryCacheHits = stats.QueryCacheHits;
            status.queryCacheMisses = stats.QueryCacheMisses;
        }

        private void WriteStatus()
        {
            if (string.IsNullOrWhiteSpace(_options.StatusPath))
            {
                return;
            }

            try
            {
                lock (_statusFileLock)
                {
                    var directory = Path.GetDirectoryName(_options.StatusPath);
                    if (string.IsNullOrEmpty(directory))
                    {
                        return;
                    }

                    // status 发布也必须加入 Editor 的启动临界区，保证退出时的“校验后删除”不会与新实例发布交错。
                    using (var launchLock = TryAcquireDaemonLaunchLock(directory))
                    {
                        if (launchLock == null)
                        {
                            Log("Skipped status write because the daemon launch lock is unavailable.");
                            return;
                        }

                        var json = JsonConvert.SerializeObject(GetStatusSnapshot(), Formatting.Indented, JsonSettings);
                        Directory.CreateDirectory(directory);
                        var lockPath = Path.Combine(directory, "lock.json");
                        WriteAllTextAtomic(lockPath, json);
                        WriteAllTextAtomic(_options.StatusPath, json);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to write status: " + ex.Message);
            }
        }

        private static void WriteAllTextAtomic(string path, string contents)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = path + "." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tempPath, contents, Encoding.UTF8);
            try
            {
                for (var attempt = 0; attempt < StatusFileWriteRetryCount; attempt++)
                {
                    try
                    {
                        ReplaceStatusFile(tempPath, path);

                        return;
                    }
                    catch (IOException)
                    {
                        if (attempt == StatusFileWriteRetryCount - 1)
                        {
                            throw;
                        }

                        Thread.Sleep(StatusFileWriteRetryDelayMs);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (attempt == StatusFileWriteRetryCount - 1)
                        {
                            throw;
                        }

                        Thread.Sleep(StatusFileWriteRetryDelayMs);
                    }
                }
            }
            finally
            {
                DeleteFileIfExists(tempPath);
            }
        }

        private static void ReplaceStatusFile(string tempPath, string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tempPath, path, null);
                    return;
                }
                catch (PlatformNotSupportedException)
                {
                }
            }

            File.Move(tempPath, path, true);
        }

        private void CleanupTransientState()
        {
            if (string.IsNullOrWhiteSpace(_options.StatusPath))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_options.StatusPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                // 与 Editor 的启动临界区使用同一把跨进程锁。拿不到锁时宁可遗留文件，不能删除新 daemon 已接管的共享状态。
                using (var launchLock = TryAcquireDaemonLaunchLock(directory))
                {
                    if (launchLock == null)
                    {
                        Log("Skipped shared transient-state cleanup because the daemon launch lock is unavailable.");
                    }
                    else
                    {
                        DeleteSharedFileIfOwned(_options.StatusPath, SharedStateFileKind.Status);
                        DeleteSharedFileIfOwned(Path.Combine(directory, "lock.json"), SharedStateFileKind.Lock);
                        DeleteLegacyMarkerIfOwned(Path.Combine(directory, "daemon-process.json"));
                    }
                }

                // PID 专属 marker 不参与共享接管；只校验并删除当前 PID 对应的实例记录。
                DeletePidMarkerIfOwned(Path.Combine(directory, "daemon-processes", GetCurrentDaemonPid() + ".json"));
                DeletePrivateTemporaryFiles(directory);
            }
            catch (Exception ex)
            {
                Log("Failed to clean transient state: " + ex.Message);
            }
        }

        private FileStream TryAcquireDaemonLaunchLock(string directory)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(DaemonLaunchLockWaitMs);
            var launchLockPath = Path.Combine(directory, "daemon-launch.lock");
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return new FileStream(launchLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    Thread.Sleep(DaemonLaunchLockRetryDelayMs);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(DaemonLaunchLockRetryDelayMs);
                }
            }

            return null;
        }

        private void DeleteSharedFileIfOwned(string path, SharedStateFileKind kind)
        {
            string json;
            if (!TryReadAllText(path, out json) || !SharedStateMatchesCurrentDaemon(json, kind))
            {
                return;
            }

            // 在 launch lock 内重读，避免身份检查和删除之间的 TOCTOU 覆盖窗口。
            string verifiedJson;
            if (!TryReadAllText(path, out verifiedJson)
                || !string.Equals(json, verifiedJson, StringComparison.Ordinal)
                || !SharedStateMatchesCurrentDaemon(verifiedJson, kind))
            {
                return;
            }

            DeleteFileIfExists(path);
        }

        private void DeleteLegacyMarkerIfOwned(string path)
        {
            string json;
            if (!TryReadAllText(path, out json) || !LegacyMarkerMatchesCurrentDaemon(json))
            {
                return;
            }

            string verifiedJson;
            if (!TryReadAllText(path, out verifiedJson)
                || !string.Equals(json, verifiedJson, StringComparison.Ordinal)
                || !LegacyMarkerMatchesCurrentDaemon(verifiedJson))
            {
                return;
            }

            DeleteFileIfExists(path);
        }

        private void DeletePidMarkerIfOwned(string path)
        {
            string json;
            if (TryReadAllText(path, out json) && LegacyMarkerMatchesCurrentDaemon(json))
            {
                DeleteFileIfExists(path);
            }
        }

        private bool SharedStateMatchesCurrentDaemon(string json, SharedStateFileKind kind)
        {
            int daemonPid;
            string token;
            if (!TryReadInt32JsonProperty(json, "daemonPid", out daemonPid) || daemonPid != GetCurrentDaemonPid())
            {
                return false;
            }

            // status/lock 由同一快照写入；token 是当前 daemon 的稳定实例身份，绝不能记录到日志。
            return TryReadStringJsonProperty(json, "token", out token)
                   && !string.IsNullOrEmpty(_options.Token)
                   && string.Equals(token, _options.Token, StringComparison.Ordinal);
        }

        private bool LegacyMarkerMatchesCurrentDaemon(string json)
        {
            int daemonPid;
            long startedAtUtcTicks;
            if (!TryReadInt32JsonProperty(json, "daemonPid", out daemonPid)
                || daemonPid != GetCurrentDaemonPid()
                || !TryReadInt64JsonProperty(json, "startedAtUtcTicks", out startedAtUtcTicks)
                || startedAtUtcTicks <= 0L)
            {
                return false;
            }

            long currentStartedAtUtcTicks;
            return TryGetCurrentProcessStartUtcTicks(out currentStartedAtUtcTicks)
                   && Math.Abs(currentStartedAtUtcTicks - startedAtUtcTicks) <= ProcessStartTicksTolerance;
        }

        private static int GetCurrentDaemonPid()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static bool TryGetCurrentProcessStartUtcTicks(out long startedAtUtcTicks)
        {
            startedAtUtcTicks = 0L;
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    startedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    return startedAtUtcTicks > 0L;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadAllText(string path, out string contents)
        {
            contents = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return false;
                }

                contents = File.ReadAllText(path, Encoding.UTF8);
                return !string.IsNullOrEmpty(contents);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadInt32JsonProperty(string json, string propertyName, out int value)
        {
            value = 0;
            try
            {
                var valueObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (valueObject == null || !valueObject.TryGetValue(propertyName, out var raw) || raw == null)
                {
                    return false;
                }

                return int.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadInt64JsonProperty(string json, string propertyName, out long value)
        {
            value = 0L;
            try
            {
                var valueObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (valueObject == null || !valueObject.TryGetValue(propertyName, out var raw) || raw == null)
                {
                    return false;
                }

                return long.TryParse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture), out value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadStringJsonProperty(string json, string propertyName, out string value)
        {
            value = null;
            try
            {
                var valueObject = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (valueObject == null || !valueObject.TryGetValue(propertyName, out var raw) || raw == null)
                {
                    return false;
                }

                value = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture);
                return value != null;
            }
            catch
            {
                return false;
            }
        }

        private static void DeletePrivateTemporaryFiles(string directory)
        {
            try
            {
                var pidPrefix = "." + GetCurrentDaemonPid() + ".";
                foreach (var path in Directory.EnumerateFiles(directory, "*" + pidPrefix + "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    DeleteFileIfExists(path);
                }
            }
            catch
            {
                // 临时文件清理失败不应影响共享状态的安全退出。
            }
        }

        private enum SharedStateFileKind
        {
            Status,
            Lock
        }

        private static void DeleteFileIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private bool IsAuthorized(HttpRequestData request)
        {
            if (string.IsNullOrEmpty(_options.Token))
            {
                return true;
            }

            return request.Headers.TryGetValue("X-AIBridge-CodeIndex-Token", out var token)
                && string.Equals(token, _options.Token, StringComparison.Ordinal);
        }

        private async Task<HttpRequestData> ReadRequestHeaderAsync(NetworkStream stream)
        {
            // 必须在认证和路由判定前停止于 CRLFCRLF，避免错误路由或未认证请求的正文被提前读入。
            var buffer = new byte[1];
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            using (var memory = new MemoryStream())
            {
                timeout.CancelAfter(NetworkOperationTimeoutMs);
                var headerEnd = -1;
                while (headerEnd < 0)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token);
                    if (read <= 0)
                    {
                        return null;
                    }

                    memory.WriteByte(buffer[0]);
                    if (memory.Length > MaxHttpHeaderBytes)
                    {
                        throw new HttpRequestException("HTTP header is too large.");
                    }

                    headerEnd = FindHeaderEnd(memory.GetBuffer(), (int)memory.Length);
                }

                var bytes = memory.ToArray();
                var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
                var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0)
                {
                    throw new HttpRequestException("HTTP request line is missing.");
                }

                var requestLine = lines[0].Split(' ');
                if (requestLine.Length < 3)
                {
                    throw new HttpRequestException("HTTP request line is invalid.");
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        throw new HttpRequestException("HTTP header is invalid.");
                    }

                    headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                }

                var contentLength = 0;
                if (headers.TryGetValue("Content-Length", out var contentLengthText)
                    && (!int.TryParse(contentLengthText, out contentLength) || contentLength < 0))
                {
                    throw new HttpRequestException("Content-Length is invalid.");
                }

                if (contentLength > MaxRequestBodyBytes)
                {
                    throw new HttpRequestException("Request body is too large.");
                }

                return new HttpRequestData
                {
                    Method = requestLine[0].ToUpperInvariant(),
                    Path = requestLine[1],
                    Headers = headers,
                    ContentLength = contentLength
                };
            }
        }

        private async Task<string> ReadRequestBodyAsync(NetworkStream stream, HttpRequestData request)
        {
            if (request == null || request.ContentLength == 0)
            {
                return string.Empty;
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            using (var body = new MemoryStream(request.ContentLength))
            {
                timeout.CancelAfter(NetworkOperationTimeoutMs);
                var buffer = new byte[Math.Min(4096, request.ContentLength)];
                while (body.Length < request.ContentLength)
                {
                    var remaining = Math.Min(buffer.Length, request.ContentLength - (int)body.Length);
                    var read = await stream.ReadAsync(buffer, 0, remaining, timeout.Token);
                    if (read <= 0)
                    {
                        throw new HttpRequestException("Request body ended unexpectedly.");
                    }

                    body.Write(buffer, 0, read);
                }

                return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            }
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            for (var i = 3; i < length; i++)
            {
                if (bytes[i - 3] == '\r'
                    && bytes[i - 2] == '\n'
                    && bytes[i - 1] == '\r'
                    && bytes[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private async Task TryWriteResponseAsync(NetworkStream stream, int statusCode, object body)
        {
            try
            {
                await WriteResponseAsync(stream, statusCode, body);
            }
            catch (OperationCanceledException) when (_shutdownRequested)
            {
            }
            catch (IOException ex) when (IsClientDisconnect(ex))
            {
            }
        }

        private async Task WriteResponseAsync(NetworkStream stream, int statusCode, object body)
        {
            var statusText = statusCode == 200 ? "OK" : statusCode == 400 ? "Bad Request" : statusCode == 403 ? "Forbidden" : statusCode == 404 ? "Not Found" : "Error";
            var json = JsonConvert.SerializeObject(body, Formatting.None, JsonSettings);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
                         + "Content-Type: application/json; charset=utf-8\r\n"
                         + "Content-Length: " + bodyBytes.Length + "\r\n"
                         + "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
            {
                timeout.CancelAfter(NetworkOperationTimeoutMs);
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, timeout.Token);
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, timeout.Token);
            }
        }

        private static bool IsClientDisconnect(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                var socket = current as SocketException;
                if (socket != null
                    && (socket.SocketErrorCode == SocketError.ConnectionAborted
                        || socket.SocketErrorCode == SocketError.ConnectionReset
                        || socket.SocketErrorCode == SocketError.Shutdown))
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private void Log(string message)
        {
            try
            {
                var statusDirectory = string.IsNullOrEmpty(_options.StatusPath) ? null : Path.GetDirectoryName(_options.StatusPath);
                if (string.IsNullOrEmpty(statusDirectory))
                {
                    return;
                }

                var logDirectory = Path.Combine(statusDirectory, "logs");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, "daemon.log"), DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string ComputeProjectHash(string projectRoot)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(projectRoot.ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private sealed class HttpRequestData
        {
            public string Method { get; set; }
            public string Path { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public int ContentLength { get; set; }
        }
    }
}
