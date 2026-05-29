using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AIBridgeCodeIndex
{
    internal sealed class CodeIndexServer
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly CodeIndexOptions _options;
        private readonly object _statusLock = new object();
        private readonly object _statusFileLock = new object();
        private readonly object _refreshLock = new object();
        private readonly object _workspaceLock = new object();
        private CodeIndexWorkspace _workspace;
        private TcpListener _listener;
        private CodeIndexStatus _status;
        private Task _warmupTask;
        private Task _refreshTask;
        private Task _unityMonitorTask;
        private volatile bool _shutdownRequested;

        public CodeIndexServer(CodeIndexOptions options)
        {
            _options = options;
            _workspace = new CodeIndexWorkspace(options.ProjectRoot);
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
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _status = CreateInitialStatus(endpoint);
            WriteStatus();

            _warmupTask = WarmupAsync();
            if (_options.UnityPid > 0)
            {
                _unityMonitorTask = MonitorUnityProcessAsync();
            }

            while (!_shutdownRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_shutdownRequested)
                    {
                        break;
                    }

                    throw;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }

            if (_warmupTask != null)
            {
                await Task.WhenAny(_warmupTask, Task.Delay(500));
            }

            if (_unityMonitorTask != null)
            {
                await Task.WhenAny(_unityMonitorTask, Task.Delay(100));
            }

            if (_refreshTask != null)
            {
                await Task.WhenAny(_refreshTask, Task.Delay(500));
            }

            CleanupTransientState();
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

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                try
                {
                    var request = await ReadRequestAsync(stream);
                    if (request == null)
                    {
                        return;
                    }

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
                        var query = JsonConvert.DeserializeObject<CodeIndexRequest>(request.BodyText);
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

                    await WriteResponseAsync(stream, 404, new { success = false, error = "Not found" });
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
                    await WriteResponseAsync(stream, 500, new { success = false, error = ex.Message });
                }
            }
        }

        private async Task<CodeIndexResponse> ExecuteQueryAsync(CodeIndexRequest query)
        {
            var status = GetStatusSnapshot();
            if (query == null || string.IsNullOrWhiteSpace(query.action))
            {
                return BuildFailure(status, "Missing action.");
            }

            if (!string.Equals(status.state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFailure(status, "Unity snapshot workspace is not ready. Current state: " + status.state);
            }

            var workspace = GetWorkspace();
            var refreshNeeded = MarkRefreshIfNeeded(workspace);
            status = GetStatusSnapshot();
            if (!string.Equals(status.state, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return BuildFailure(status, "Unity snapshot workspace is not ready. Current state: " + status.state);
            }

            var response = await workspace.QueryAsync(query.action, query.parameters);
            response.success = true;
            response.semantic = true;
            response.source = "unity-snapshot";
            response.state = status.state;
            response.stale = status.stale;
            response.projectRoot = status.projectRoot;
            response.solution = workspace.SolutionPath;
            response.workspaceMode = workspace.WorkspaceMode;
            response.snapshotExists = workspace.SnapshotExists;
            response.snapshotVersion = workspace.SnapshotVersion;
            response.generationId = workspace.GenerationId;
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
            lock (_statusLock)
            {
                _status.snapshotExists = workspace.SnapshotExists;
                _status.snapshotVersion = workspace.SnapshotVersion;
                _status.generationId = workspace.GenerationId;
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

            WriteStatus();
            if (refreshNeeded)
            {
                ScheduleBackgroundRefresh();
            }

            return response;
        }

        private static CodeIndexResponse BuildFailure(CodeIndexStatus status, string error)
        {
            return new CodeIndexResponse
            {
                success = false,
                semantic = false,
                source = "unity-snapshot",
                state = status == null ? "unknown" : status.state,
                stale = true,
                projectRoot = status == null ? null : status.projectRoot,
                solution = status == null ? null : status.solution,
                workspaceMode = status == null ? "unity-snapshot" : status.workspaceMode,
                snapshotExists = status != null && status.snapshotExists,
                snapshotVersion = status == null ? 0 : status.snapshotVersion,
                generationId = status == null ? null : status.generationId,
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
                error = error
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
            try
            {
                await nextWorkspace.WarmupAsync();
                if (_shutdownRequested)
                {
                    return;
                }

                if (!IsStatusReady())
                {
                    return;
                }

                var stale = nextWorkspace.IsStale();
                var staleReason = nextWorkspace.StaleReason;
                lock (_workspaceLock)
                {
                    _workspace = nextWorkspace;
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

                WriteStatus();
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
        }

        private void RefreshStaleState()
        {
            RefreshStaleState(GetWorkspace());
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

        private async Task MonitorUnityProcessAsync()
        {
            var missingTicks = 0;
            while (!_shutdownRequested)
            {
                if (!IsProcessAlive(_options.UnityPid))
                {
                    missingTicks++;
                    if (missingTicks >= 3)
                    {
                        UpdateStatus("stopping", "Unity process exited; stopping code_index daemon.");
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

        private static bool IsProcessAlive(int processId)
        {
            if (processId <= 0)
            {
                return false;
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private void RequestShutdown()
        {
            _shutdownRequested = true;
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
            });
        }

        private CodeIndexStatus CreateInitialStatus(string endpoint)
        {
            var now = DateTimeOffset.Now.ToString("o");
            return new CodeIndexStatus
            {
                projectRoot = _options.ProjectRoot,
                projectHash = ComputeProjectHash(_options.ProjectRoot),
                unityPid = _options.UnityPid,
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
            lock (_statusLock)
            {
                return new CodeIndexStatus
                {
                    projectRoot = _status.projectRoot,
                    projectHash = _status.projectHash,
                    unityPid = _status.unityPid,
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
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                        var lockPath = Path.Combine(directory, "lock.json");
                        File.WriteAllText(lockPath, JsonConvert.SerializeObject(GetStatusSnapshot(), Formatting.Indented, JsonSettings), Encoding.UTF8);
                    }

                    File.WriteAllText(_options.StatusPath, JsonConvert.SerializeObject(GetStatusSnapshot(), Formatting.Indented, JsonSettings), Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to write status: " + ex.Message);
            }
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

                DeleteFileIfExists(_options.StatusPath);
                DeleteFileIfExists(Path.Combine(directory, "lock.json"));

                var tempDirectory = Path.Combine(directory, "temp");
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to clean transient state: " + ex.Message);
            }
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

        private async Task<HttpRequestData> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var memory = new MemoryStream();
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return null;
                }

                memory.Write(buffer, 0, read);
                if (memory.Length > 65536)
                {
                    throw new InvalidOperationException("HTTP header is too large.");
                }

                headerEnd = FindHeaderEnd(memory.GetBuffer(), (int)memory.Length);
            }

            var bytes = memory.ToArray();
            var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2)
            {
                return null;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var contentLengthText))
            {
                int.TryParse(contentLengthText, out contentLength);
            }

            var bodyOffset = headerEnd + 4;
            var body = new MemoryStream();
            if (bytes.Length > bodyOffset)
            {
                body.Write(bytes, bodyOffset, bytes.Length - bodyOffset);
            }

            while (body.Length < contentLength)
            {
                var remaining = Math.Min(buffer.Length, contentLength - (int)body.Length);
                var read = await stream.ReadAsync(buffer, 0, remaining);
                if (read <= 0)
                {
                    break;
                }

                body.Write(buffer, 0, read);
            }

            return new HttpRequestData
            {
                Method = requestLine[0].ToUpperInvariant(),
                Path = requestLine[1],
                Headers = headers,
                BodyText = Encoding.UTF8.GetString(body.ToArray())
            };
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

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, object body)
        {
            var statusText = statusCode == 200 ? "OK" : statusCode == 403 ? "Forbidden" : statusCode == 404 ? "Not Found" : "Error";
            var json = JsonConvert.SerializeObject(body, Formatting.None, JsonSettings);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var header = "HTTP/1.1 " + statusCode + " " + statusText + "\r\n"
                         + "Content-Type: application/json; charset=utf-8\r\n"
                         + "Content-Length: " + bodyBytes.Length + "\r\n"
                         + "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
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
            public string BodyText { get; set; }
        }
    }
}
