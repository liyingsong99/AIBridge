using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIBridgeCLI.Core
{
    public sealed class HttpRuntimeTransportClient : IRuntimeTransportClient
    {
        private const string TransportName = "http";
        private const string CheckPassed = "passed";
        private const string CheckFailed = "failed";
        private const int HealthProbeTimeoutMs = 100;
        private const int CachedHealthProbeTimeoutMs = 500;
        private const int LocalPortScanIdleMissLimit = 8;
        private const int DiagnosticCommandTimeoutMs = 3000;

        private readonly RuntimeTransportOptions _options;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, CommandResult> _syncResults = new Dictionary<string, CommandResult>();

        public HttpRuntimeTransportClient(RuntimeTransportOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _options.TimeoutMs + 5000))
            };
        }

        public RuntimeTransportKind Kind => RuntimeTransportKind.Http;

        public IReadOnlyList<RuntimeTargetInfo> ListTargets(RuntimeTargetQueryOptions options = null)
        {
            options = options ?? RuntimeTargetQueryOptions.Quick;
            var targets = new List<RuntimeTargetInfo>();
            var cached = RuntimeDiscoveryClient.ReadFreshCache(RuntimeDiscoveryClient.DefaultCacheSeconds);
            var cachedHealthTimeout = options.ProbeLocalPorts ? CachedHealthProbeTimeoutMs : HealthProbeTimeoutMs;
            AddTargetFromHealth(targets, _options.HttpUrl, TryGetHealth(_options.HttpUrl), FindCachedTargetByUrl(cached, _options.HttpUrl));
            for (var i = 0; i < cached.Count; i++)
            {
                var target = cached[i];
                var targetUrl = target.reachableUrl ?? target.url;
                if (string.IsNullOrWhiteSpace(targetUrl)
                    || !RuntimeDiscoveryClient.MatchesRuntimeFilters(target, _options.PreferredPlatform, _options.PreferredProjectHint)
                    || ContainsTarget(targets, target.targetId, targetUrl))
                {
                    continue;
                }

                AddTargetFromHealth(targets, targetUrl, TryGetHealth(targetUrl, cachedHealthTimeout), target);
            }

            if (options.ProbeLocalPorts && !_options.HttpUrlExplicit && !HasPreferredFilters())
            {
                AddLocalPortScanTargets(targets);
            }

            targets.Sort(CompareTargets);
            return targets;
        }

        public RuntimeTargetInfo ResolveTarget(string target, RuntimeTargetQueryOptions options = null)
        {
            var targets = ListTargets(options);
            if (targets.Count == 0)
            {
                return null;
            }

            var resolvedTarget = string.IsNullOrWhiteSpace(target) ? RuntimeTransportOptions.DefaultTarget : target;
            if (string.Equals(resolvedTarget, RuntimeTransportOptions.DefaultTarget, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resolvedTarget, targets[0].targetId, StringComparison.OrdinalIgnoreCase))
            {
                return targets[0];
            }

            for (var i = 0; i < targets.Count; i++)
            {
                if (string.Equals(resolvedTarget, targets[i].targetId, StringComparison.OrdinalIgnoreCase))
                {
                    return targets[i];
                }
            }

            return null;
        }

        private void AddLocalPortScanTargets(List<RuntimeTargetInfo> targets)
        {
            var startPort = RuntimeDiscoveryClient.DefaultHttpPort;
            var endPort = Math.Min(65535, startPort + RuntimeDiscoveryClient.DefaultPortScanCount - 1);
            var foundAny = targets.Count > 0;
            var missesAfterHit = 0;
            for (var port = startPort; port <= endPort; port++)
            {
                var url = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
                if (targets.Exists(existing => string.Equals(existing.path, url, StringComparison.OrdinalIgnoreCase)))
                {
                    foundAny = true;
                    missesAfterHit = 0;
                    continue;
                }

                var beforeCount = targets.Count;
                AddTargetFromHealth(targets, url, TryGetHealth(url), null);
                if (targets.Count > beforeCount)
                {
                    foundAny = true;
                    missesAfterHit = 0;
                    continue;
                }

                if (!foundAny)
                {
                    continue;
                }

                missesAfterHit++;
                if (missesAfterHit >= LocalPortScanIdleMissLimit)
                {
                    break;
                }
            }
        }

        private void AddTargetFromHealth(
            List<RuntimeTargetInfo> targets,
            string baseUrl,
            JObject health,
            RuntimeDiscoveryTarget fallback)
        {
            if (targets == null || health == null || string.IsNullOrWhiteSpace(baseUrl))
            {
                return;
            }

            var targetId = ReadString(health, "targetId") ?? (fallback == null ? null : fallback.targetId);
            if (string.IsNullOrWhiteSpace(targetId))
            {
                targetId = "http";
            }

            if (ContainsTarget(targets, targetId, baseUrl))
            {
                return;
            }

            var url = baseUrl.TrimEnd('/');
            var platform = ReadString(health, "platform") ?? (fallback == null ? null : fallback.platform);
            var projectName = ReadString(health, "productName")
                ?? ReadString(health, "projectName")
                ?? (fallback == null ? null : fallback.projectName);
            var deviceName = ReadString(health, "deviceName") ?? (fallback == null ? null : fallback.deviceName);
            targets.Add(new RuntimeTargetInfo
            {
                targetId = targetId,
                path = url,
                heartbeatPath = url + "/aibridge/health",
                commandsPath = url + "/aibridge/commands",
                resultsPath = url + "/aibridge/results",
                screenshotsPath = url,
                source = fallback == null || string.IsNullOrWhiteSpace(fallback.source) ? "http-health" : fallback.source,
                platform = platform,
                projectName = projectName,
                deviceName = deviceName,
                targetKind = ResolveTargetKind(health, platform, url, fallback == null ? null : fallback.targetKind),
                reachable = true,
                connectionUrl = url,
                preferred = string.Equals(url, _options.HttpUrl, StringComparison.OrdinalIgnoreCase),
                stale = false,
                ageSeconds = 0,
                lastHeartbeatUtc = ReadString(health, "lastHeartbeatUtc") ?? (fallback == null ? null : fallback.lastSeenUtc),
                heartbeat = health
            });
        }

        private static bool ContainsTarget(List<RuntimeTargetInfo> targets, string targetId, string url)
        {
            return targets.Exists(existing =>
                (!string.IsNullOrWhiteSpace(targetId)
                    && string.Equals(existing.targetId, targetId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(url)
                    && string.Equals(existing.path, url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
        }

        private int CompareTargets(RuntimeTargetInfo left, RuntimeTargetInfo right)
        {
            var preferredCompare = CompareBoolTrueFirst(IsPreferredTarget(left), IsPreferredTarget(right));
            if (preferredCompare != 0)
            {
                return preferredCompare;
            }

            var filterCompare = CompareBoolTrueFirst(MatchesPreferredFilters(left), MatchesPreferredFilters(right));
            if (filterCompare != 0)
            {
                return filterCompare;
            }

            var rankCompare = GetTargetRank(left).CompareTo(GetTargetRank(right));
            if (rankCompare != 0)
            {
                return rankCompare;
            }

            var uptimeCompare = ReadUptimeSeconds(left).CompareTo(ReadUptimeSeconds(right));
            if (uptimeCompare != 0)
            {
                return uptimeCompare;
            }

            return string.Compare(left == null ? null : left.targetId, right == null ? null : right.targetId, StringComparison.OrdinalIgnoreCase);
        }

        private static double ReadUptimeSeconds(RuntimeTargetInfo target)
        {
            if (target == null || target.heartbeat == null)
            {
                return double.MaxValue;
            }

            var token = target.heartbeat["uptimeSeconds"];
            if (token == null)
            {
                return double.MaxValue;
            }

            return token.Type == JTokenType.Float || token.Type == JTokenType.Integer
                ? token.Value<double>()
                : double.MaxValue;
        }

        public RuntimeSendResult Send(RuntimeTargetInfo target, CommandRequest request)
        {
            if (request == null)
            {
                return new RuntimeSendResult
                {
                    Success = false,
                    Error = "Runtime HTTP request is null."
                };
            }

            try
            {
                var url = BuildUrl(target, "/aibridge/commands?timeoutMs=" + _options.TimeoutMs.ToString(CultureInfo.InvariantCulture));
                var json = JsonConvert.SerializeObject(request, Formatting.None, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                var responseJson = SendJson(url, HttpMethod.Post, json, ResolveToken(request));
                var result = RuntimeResultParser.Parse(request.id, responseJson);
                TryAttachRuntimeStatusConnection(result, request, target);
                TryPrepareScreenshotArtifact(result, request, target);
                lock (_syncResults)
                {
                    _syncResults[request.id] = result;
                }

                return new RuntimeSendResult
                {
                    Success = true,
                    CommandPath = url
                };
            }
            catch (Exception ex)
            {
                return new RuntimeSendResult
                {
                    Success = false,
                    Error = "HTTP runtime transport failed: " + ex.Message
                };
            }
        }

        public RuntimeReceiveResult WaitResult(RuntimeTargetInfo target, string commandId, int timeoutMs, int pollIntervalMs)
        {
            var startTime = DateTime.UtcNow;
            while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
            {
                lock (_syncResults)
                {
                    if (_syncResults.TryGetValue(commandId, out var cachedResult))
                    {
                        _syncResults.Remove(commandId);
                        return new RuntimeReceiveResult
                        {
                            Success = true,
                            Result = cachedResult
                        };
                    }
                }

                var result = TryPollResult(target, commandId);
                if (result != null)
                {
                    return new RuntimeReceiveResult
                    {
                        Success = true,
                        Result = result
                    };
                }

                Thread.Sleep(Math.Max(10, pollIntervalMs));
            }

            return new RuntimeReceiveResult
            {
                Success = false,
                TimedOut = true,
                Error = "Timeout waiting for HTTP runtime result."
            };
        }

        public void CleanupCommand(RuntimeTargetInfo target, string commandId)
        {
            if (target == null || string.IsNullOrEmpty(commandId))
            {
                return;
            }

            try
            {
                SendJson(BuildUrl(target, "/aibridge/commands/" + Uri.EscapeDataString(commandId)), HttpMethod.Delete, null, _options.Token, 1000);
            }
            catch
            {
            }
        }

        public RuntimeDiagnosticReport Diagnose(string target, RuntimeCommandTrace commandTrace = null)
        {
            var report = new RuntimeDiagnosticReport
            {
                transport = TransportName,
                runtimeDirectory = null,
                targetId = target
            };

            try
            {
                var targetInfo = ResolveTarget(target, RuntimeTargetQueryOptions.Probe);
                var diagnosticUrl = targetInfo == null || string.IsNullOrWhiteSpace(targetInfo.path)
                    ? _options.HttpUrl
                    : targetInfo.path;
                JObject health;
                string healthError;
                int healthStatusCode;
                if (!TryGetHealth(diagnosticUrl, _options.TimeoutMs, out health, out healthError, out healthStatusCode))
                {
                    report.checks.Add(new RuntimeDiagnosticCheck
                    {
                        name = "httpEndpoint",
                        status = CheckFailed,
                        detail = "HTTP endpoint did not return a ready health payload: " + diagnosticUrl + ". " + healthError,
                        fix = "Verify runtimeSettings.enableHttpTransport, bind/port, firewall, and --url."
                    });
                    report.summary = "HTTP endpoint health check failed or runtime is not ready.";
                    report.success = false;
                    return report;
                }

                report.targetId = ReadString(health, "targetId");
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "httpEndpoint",
                    status = CheckPassed,
                    detail = "HTTP health endpoint is reachable: " + BuildUrl(diagnosticUrl, "/aibridge/health")
                });
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "runtimeReady",
                    status = CheckPassed,
                    detail = BuildReadyDetail(health, healthStatusCode)
                });
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "authHeader",
                    status = string.IsNullOrEmpty(_options.Token) ? "warning" : CheckPassed,
                    detail = string.IsNullOrEmpty(_options.Token)
                        ? "No --token was provided. This is valid only when runtimeSettings.authToken is empty."
                        : "Authorization bearer token will be sent."
                });
                var pingResult = TryDiagnosticPing(diagnosticUrl);
                if (pingResult == null || !pingResult.success)
                {
                    report.checks.Add(new RuntimeDiagnosticCheck
                    {
                        name = "handlerRoundtrip",
                        status = CheckFailed,
                        detail = pingResult == null
                            ? "runtime.ping did not return a valid result."
                            : "runtime.ping failed: " + pingResult.error,
                        fix = "Ensure the Player or Editor Play Mode is still running and consuming Runtime Bridge commands."
                    });
                    report.success = false;
                    report.summary = "Runtime HTTP endpoint is reachable, but handler roundtrip failed.";
                    return report;
                }

                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "handlerRoundtrip",
                    status = CheckPassed,
                    detail = "runtime.ping handler responded."
                });
                report.suggestions.Add("Run: $CLI runtime status --transport http --url " + diagnosticUrl);
                if (!string.IsNullOrWhiteSpace(_options.PreferredPlatform))
                {
                    report.suggestions.Add("Filter is active: --platform " + _options.PreferredPlatform);
                }
                report.success = true;
                report.summary = "Runtime HTTP transport diagnostics passed.";
                return report;
            }
            catch (Exception ex)
            {
                report.checks.Add(new RuntimeDiagnosticCheck
                {
                    name = "httpEndpoint",
                    status = CheckFailed,
                    detail = ex.Message,
                    fix = "Verify the Player is running, HTTP transport is enabled, and the URL is reachable."
                });
                report.summary = ex.Message;
                report.success = false;
                return report;
            }
        }

        private JObject TryGetHealth(string baseUrl)
        {
            return TryGetHealth(baseUrl, HealthProbeTimeoutMs);
        }

        private JObject TryGetHealth(string baseUrl, int timeoutMs)
        {
            JObject health;
            string error;
            int statusCode;
            return TryGetHealth(baseUrl, timeoutMs, out health, out error, out statusCode) ? health : null;
        }

        private bool TryGetHealth(string baseUrl, int timeoutMs, out JObject health, out string error, out int statusCode)
        {
            health = null;
            error = null;
            statusCode = 0;
            try
            {
                using (var message = new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUrl, "/aibridge/health")))
                using (var cancellation = new CancellationTokenSource(Math.Max(100, timeoutMs)))
                {
                    if (!string.IsNullOrWhiteSpace(_options.Token))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
                    }

                    using (var response = _httpClient.SendAsync(message, cancellation.Token).GetAwaiter().GetResult())
                    {
                        statusCode = (int)response.StatusCode;
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (string.IsNullOrWhiteSpace(body))
                        {
                            error = "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
                            return false;
                        }

                        health = JObject.Parse(body);
                        if (!response.IsSuccessStatusCode)
                        {
                            error = "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + ": " + BuildHealthNotReadyReason(health);
                            return false;
                        }

                        if (!IsHealthReady(health))
                        {
                            error = BuildHealthNotReadyReason(health);
                            return false;
                        }

                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private CommandResult TryDiagnosticPing(string baseUrl)
        {
            var commandId = "diagnose_ping_" + Guid.NewGuid().ToString("N");
            var request = new CommandRequest
            {
                id = commandId,
                type = "runtime",
                @params = new Dictionary<string, object>
                {
                    ["action"] = "runtime.ping"
                }
            };

            try
            {
                var url = BuildUrl(baseUrl, "/aibridge/commands?timeoutMs=" + DiagnosticCommandTimeoutMs.ToString(CultureInfo.InvariantCulture));
                var json = JsonConvert.SerializeObject(request, Formatting.None, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                var responseJson = SendJson(url, HttpMethod.Post, json, ResolveToken(request), DiagnosticCommandTimeoutMs + 500);
                return RuntimeResultParser.Parse(commandId, responseJson);
            }
            catch (Exception ex)
            {
                return new CommandResult
                {
                    id = commandId,
                    success = false,
                    error = ex.Message
                };
            }
        }

        private CommandResult TryPollResult(RuntimeTargetInfo target, string commandId)
        {
            try
            {
                var json = SendJson(BuildUrl(target, "/aibridge/results/" + Uri.EscapeDataString(commandId)), HttpMethod.Get, null, _options.Token);
                return RuntimeResultParser.Parse(commandId, json);
            }
            catch
            {
                return null;
            }
        }

        private string SendJson(string url, HttpMethod method, string json, string token, int? timeoutMs = null)
        {
            using (var message = new HttpRequestMessage(method, url))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                if (json != null)
                {
                    message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                CancellationTokenSource cancellation = null;
                try
                {
                    if (timeoutMs.HasValue)
                    {
                        cancellation = new CancellationTokenSource(Math.Max(100, timeoutMs.Value));
                    }

                    var responseTask = cancellation == null
                        ? _httpClient.SendAsync(message)
                        : _httpClient.SendAsync(message, cancellation.Token);
                    using (var response = responseTask.GetAwaiter().GetResult())
                    {
                        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body))
                        {
                            throw new InvalidOperationException("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase);
                        }

                        return body;
                    }
                }
                finally
                {
                    if (cancellation != null)
                    {
                        cancellation.Dispose();
                    }
                }
            }
        }

        private byte[] SendBytes(string url, string token)
        {
            using (var message = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                using (var response = _httpClient.SendAsync(message).GetAwaiter().GetResult())
                {
                    var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase);
                    }

                    return bytes;
                }
            }
        }

        private void TryPrepareScreenshotArtifact(CommandResult result, CommandRequest request, RuntimeTargetInfo target)
        {
            if (result == null
                || !result.success
                || request == null
                || !string.Equals(RuntimePathHelper.GetRuntimeAction(request), "runtime.screenshot", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var data = result.data as JObject;
            if (data == null)
            {
                return;
            }

            // filename 由 Runtime 端 imagePath 推导，避免在返回体中重复携带
            var sourceImagePath = ReadString(data, "imagePath");
            var filename = string.IsNullOrWhiteSpace(sourceImagePath) ? null : Path.GetFileName(sourceImagePath);
            if (string.IsNullOrWhiteSpace(filename))
            {
                return;
            }

            try
            {
                var cachePath = BuildArtifactCachePath(filename, target);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                var bytes = SendBytes(BuildUrl(target, "/aibridge/artifacts/" + Uri.EscapeDataString(filename)), ResolveToken(request));
                File.WriteAllBytes(cachePath, bytes);
                data["imagePath"] = cachePath;
                data["artifactDownloaded"] = true;
            }
            catch (Exception ex)
            {
                if (TryGetRequestParam(request, "output", out var output) && !string.IsNullOrWhiteSpace(output))
                {
                    result.success = false;
                    result.error = "artifact_pull_failed: " + ex.Message;
                }
            }
        }

        private void TryAttachRuntimeStatusConnection(CommandResult result, CommandRequest request, RuntimeTargetInfo target)
        {
            if (result == null
                || !result.success
                || request == null
                || !string.Equals(RuntimePathHelper.GetRuntimeAction(request), "runtime.status", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var data = result.data as JObject;
            if (data == null)
            {
                return;
            }

            var connectionUrl = target == null || string.IsNullOrWhiteSpace(target.path) ? _options.HttpUrl : target.path.TrimEnd('/');
            var bindUrl = ReadString(data, "bindUrl") ?? ReadString(data, "httpUrl");
            if (!string.IsNullOrWhiteSpace(bindUrl))
            {
                data["bindUrl"] = bindUrl;
            }

            // 远程 Player 返回的本机 bind URL 不能直接给 CLI 使用，HTTP status 输出优先呈现本次实际连接 URL。
            data["httpUrl"] = connectionUrl;
            data["reachableUrl"] = connectionUrl;
            data["connectionUrl"] = connectionUrl;
            data["healthUrl"] = BuildUrl(connectionUrl, "/aibridge/health");
        }

        private bool HasPreferredFilters()
        {
            return !string.IsNullOrWhiteSpace(_options.PreferredPlatform)
                || !string.IsNullOrWhiteSpace(_options.PreferredProjectHint);
        }

        private bool MatchesPreferredFilters(RuntimeTargetInfo target)
        {
            if (target == null)
            {
                return false;
            }

            if (!HasPreferredFilters())
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_options.PreferredPlatform)
                && !ContainsIgnoreCase(target.platform ?? ReadString(target.heartbeat, "platform"), _options.PreferredPlatform))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_options.PreferredProjectHint))
            {
                var projectName = target.projectName
                    ?? ReadString(target.heartbeat, "projectName")
                    ?? ReadString(target.heartbeat, "productName");
                if (!string.Equals(projectName, _options.PreferredProjectHint, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsPreferredTarget(RuntimeTargetInfo target)
        {
            if (target == null)
            {
                return false;
            }

            if (target.preferred)
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(target.path)
                && string.Equals(target.path.TrimEnd('/'), _options.HttpUrl, StringComparison.OrdinalIgnoreCase);
        }

        private static int GetTargetRank(RuntimeTargetInfo target)
        {
            if (target == null)
            {
                return 100;
            }

            var platform = target.platform ?? ReadString(target.heartbeat, "platform");
            if (ContainsIgnoreCase(platform, "Android"))
            {
                return 0;
            }

            if (!IsLoopbackUrl(target.path) && !ContainsIgnoreCase(target.targetKind, "virtual"))
            {
                return 1;
            }

            if (IsLoopbackUrl(target.path))
            {
                return 2;
            }

            return ContainsIgnoreCase(target.targetKind, "virtual") ? 3 : 4;
        }

        private static int CompareBoolTrueFirst(bool left, bool right)
        {
            if (left == right)
            {
                return 0;
            }

            return left ? -1 : 1;
        }

        private static bool IsLoopbackUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.IsLoopback;
        }

        private static bool ContainsIgnoreCase(string value, string pattern)
        {
            return !string.IsNullOrWhiteSpace(value)
                && !string.IsNullOrWhiteSpace(pattern)
                && value.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static RuntimeDiscoveryTarget FindCachedTargetByUrl(List<RuntimeDiscoveryTarget> targets, string url)
        {
            if (targets == null || string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var normalizedUrl = url.TrimEnd('/');
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var targetUrl = target == null ? null : (target.reachableUrl ?? target.url);
                if (!string.IsNullOrWhiteSpace(targetUrl)
                    && string.Equals(targetUrl.TrimEnd('/'), normalizedUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            return null;
        }

        private static string ResolveTargetKind(JObject health, string platform, string url, string fallback)
        {
            var targetKind = ReadString(health, "targetKind");
            if (!string.IsNullOrWhiteSpace(targetKind))
            {
                return targetKind;
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            if (ContainsIgnoreCase(platform, "Android"))
            {
                return "android-player";
            }

            return IsLoopbackUrl(url) ? "local-player" : "remote-player";
        }

        private string BuildArtifactCachePath(string filename, RuntimeTargetInfo target)
        {
            var safeName = Path.GetFileName(filename);
            var baseUrl = target == null || string.IsNullOrWhiteSpace(target.path) ? _options.HttpUrl : target.path;
            var cacheRoot = Path.Combine(PathHelper.GetExchangeDirectory(), "runtime-cache", "http", SanitizePathPart(baseUrl));
            return Path.Combine(cacheRoot, safeName);
        }

        private static string SanitizePathPart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "default";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                builder.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' || c == '/' || c == '\\' ? '_' : c);
            }

            return builder.ToString();
        }

        private string BuildUrl(string path)
        {
            return BuildUrl(_options.HttpUrl, path);
        }

        private static string BuildUrl(string baseUrl, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return baseUrl;
            }

            return (baseUrl ?? string.Empty).TrimEnd('/') + "/" + path.TrimStart('/');
        }

        private string BuildUrl(RuntimeTargetInfo target, string path)
        {
            var baseUrl = target == null || string.IsNullOrWhiteSpace(target.path) ? _options.HttpUrl : target.path;
            return BuildUrl(baseUrl, path);
        }

        private static bool IsHealthReady(JObject health)
        {
            if (health == null)
            {
                return false;
            }

            var ready = ReadBool(health, "ready");
            if (ready.HasValue && !ready.Value)
            {
                return false;
            }

            var commandPumpReady = ReadBool(health, "commandPumpReady");
            if (commandPumpReady.HasValue && !commandPumpReady.Value)
            {
                return false;
            }

            return true;
        }

        private static string BuildReadyDetail(JObject health, int statusCode)
        {
            var age = ReadString(health, "lastMainThreadTickAgeMs");
            var state = ReadString(health, "runtimeState");
            return "Runtime is ready"
                + (statusCode > 0 ? " (HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty)
                + (string.IsNullOrWhiteSpace(state) ? string.Empty : ", state=" + state)
                + (string.IsNullOrWhiteSpace(age) ? string.Empty : ", lastMainThreadTickAgeMs=" + age)
                + ".";
        }

        private static string BuildHealthNotReadyReason(JObject health)
        {
            if (health == null)
            {
                return "invalid_health_payload";
            }

            var reason = ReadString(health, "commandPumpReason");
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return reason;
            }

            var state = ReadString(health, "runtimeState");
            return string.IsNullOrWhiteSpace(state) ? "runtime_not_ready" : "runtime_not_ready: " + state;
        }

        private string ResolveToken(CommandRequest request)
        {
            if (!string.IsNullOrWhiteSpace(_options.Token))
            {
                return _options.Token;
            }

            if (request == null || request.@params == null)
            {
                return null;
            }

            foreach (var pair in request.@params)
            {
                if (string.Equals(pair.Key, "token", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        private static bool TryGetRequestParam(CommandRequest request, string key, out string value)
        {
            value = null;
            if (request == null || request.@params == null)
            {
                return false;
            }

            foreach (var pair in request.@params)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToString(pair.Value, CultureInfo.InvariantCulture);
                    return true;
                }
            }

            return false;
        }

        private static string ReadString(JObject obj, string key)
        {
            return obj != null && obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value)
                ? value.Value<string>()
                : null;
        }

        private static bool? ReadBool(JObject obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out var value))
            {
                return null;
            }

            if (value.Type == JTokenType.Boolean)
            {
                return value.Value<bool>();
            }

            bool parsed;
            return bool.TryParse(value.Value<string>(), out parsed) ? parsed : (bool?)null;
        }
    }
}
