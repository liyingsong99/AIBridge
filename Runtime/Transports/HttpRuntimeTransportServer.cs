using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AIBridge.Internal.Json;
using UnityEngine;

namespace AIBridge.Runtime.Transports
{
    internal sealed class HttpRuntimeTransportServer : IDisposable
    {
        private const int DefaultPort = 27182;
        private const int MaxPort = 65535;
        private const int PortRetryCount = 50;
        private const int DefaultCommandTimeoutMs = 30000;
        private const int MinCommandTimeoutMs = 100;
        private const int MaxCommandTimeoutMs = 300000;
        private const int MaxRequestBytes = 16 * 1024 * 1024;
        private const string HealthPath = "/aibridge/health";
        private const string CommandsPath = "/aibridge/commands";
        private const string ResultsPathPrefix = "/aibridge/results/";
        private const string ArtifactsPathPrefix = "/aibridge/artifacts/";

        private readonly AIBridgeRuntime _runtime;
        private readonly AIBridgeRuntimeSettings _settings;
        private TcpListener _listener;
        private Thread _listenThread;
        private volatile bool _running;

        public HttpRuntimeTransportServer(AIBridgeRuntime runtime, AIBridgeRuntimeSettings settings)
        {
            _runtime = runtime;
            _settings = settings;
        }

        public bool IsRunning => _running;

        public int Port { get; private set; }

        public string Url { get; private set; }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            var bindAddress = ResolveBindAddress();
            var requestedPort = ResolvePort();
            BindListener(bindAddress, requestedPort);
            _running = true;
            Url = "http://" + ResolveDisplayHost(bindAddress) + ":" + Port.ToString(CultureInfo.InvariantCulture);

            _listenThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "AIBridgeRuntimeHttpTransport"
            };
            _listenThread.Start();
            Debug.Log("[AIBridgeRuntime] HTTP transport listening: " + Url);
        }

        private void BindListener(IPAddress bindAddress, int requestedPort)
        {
            requestedPort = Math.Max(1, Math.Min(MaxPort, requestedPort));
            Exception lastError = null;
            var maxCandidate = Math.Min(MaxPort, requestedPort + PortRetryCount - 1);
            // 直接尝试绑定并在端口占用时递增，避免先检测端口再绑定产生竞态。
            for (var port = requestedPort; port <= maxCandidate; port++)
            {
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(bindAddress, port);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    if (port != requestedPort)
                    {
                        Debug.LogWarning("[AIBridgeRuntime] HTTP port " + requestedPort.ToString(CultureInfo.InvariantCulture)
                            + " is unavailable; using " + port.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    return;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    if (listener != null)
                    {
                        try { listener.Stop(); } catch { }
                    }

                    if (!IsAddressAlreadyInUse(ex))
                    {
                        throw;
                    }
                }
                catch
                {
                    if (listener != null)
                    {
                        try { listener.Stop(); } catch { }
                    }

                    throw;
                }
            }

            throw new InvalidOperationException(
                "No available AIBridge Runtime HTTP port from "
                + requestedPort.ToString(CultureInfo.InvariantCulture)
                + " to "
                + maxCandidate.ToString(CultureInfo.InvariantCulture)
                + ".",
                lastError);
        }

        public void Dispose()
        {
            _running = false;

            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch
            {
            }

            _listener = null;
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                }
                catch
                {
                    if (_running)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var request = ReadRequest(client.GetStream());
                    if (request == null)
                    {
                        return;
                    }

                    HandleRequest(client.GetStream(), request);
                }
                catch (Exception ex)
                {
                    try
                    {
                        WriteJson(client.GetStream(), 500, new Dictionary<string, object>
                        {
                            ["success"] = false,
                            ["error"] = ex.GetType().Name + ": " + ex.Message
                        });
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void HandleRequest(NetworkStream stream, HttpRequestData request)
        {
            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(stream, 200, _runtime.BuildHttpHealthData());
                return;
            }

            if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, CommandsPath, StringComparison.OrdinalIgnoreCase))
            {
                HandleCommand(stream, request);
                return;
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Path.StartsWith(ResultsPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateAuthorization(request, stream))
                {
                    return;
                }

                var commandId = Uri.UnescapeDataString(request.Path.Substring(ResultsPathPrefix.Length));
                if (_runtime.TryGetHttpResult(commandId, false, out var result))
                {
                    WriteJson(stream, 200, result);
                    return;
                }

                WriteJson(stream, 404, new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "result_not_found"
                });
                return;
            }

            if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                && request.Path.StartsWith(ArtifactsPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidateAuthorization(request, stream))
                {
                    return;
                }

                var filename = Uri.UnescapeDataString(request.Path.Substring(ArtifactsPathPrefix.Length));
                if (_runtime.TryReadHttpScreenshotArtifact(filename, out var bytes))
                {
                    WriteBinary(stream, 200, "image/png", bytes);
                    return;
                }

                WriteJson(stream, 404, new Dictionary<string, object>
                {
                    ["success"] = false,
                    ["error"] = "artifact_not_found"
                });
                return;
            }

            WriteJson(stream, 404, new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "not_found"
            });
        }

        private void HandleCommand(NetworkStream stream, HttpRequestData request)
        {
            if (!ValidateAuthorization(request, stream))
            {
                return;
            }

            Dictionary<string, object> data;
            try
            {
                data = AIBridgeJson.DeserializeObject(request.Body);
            }
            catch (Exception ex)
            {
                WriteJson(stream, 400, AIBridgeRuntimeCommandResult.FromFailure("http", "invalid_json: " + ex.Message));
                return;
            }

            var command = AIBridgeRuntimeCommand.FromDictionary(data);
            if (command == null)
            {
                WriteJson(stream, 400, AIBridgeRuntimeCommandResult.FromFailure("http", "invalid_command"));
                return;
            }

            if (string.IsNullOrEmpty(command.Id))
            {
                command.Id = "http_" + Guid.NewGuid().ToString("N");
            }

            var bearerToken = ReadBearerToken(request);
            if (string.IsNullOrEmpty(command.Token))
            {
                command.Token = bearerToken;
            }

            _runtime.EnqueueHttpCommand(command);

            var timeoutMs = ResolveCommandTimeout(request);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (_runtime.TryGetHttpResult(command.Id, true, out var result))
                {
                    WriteJson(stream, 200, result);
                    return;
                }

                // 命令必须回到 Unity 主线程执行；HTTP 线程只短轮询等待结果，避免跨线程调用 Unity API。
                Thread.Sleep(20);
            }

            WriteJson(stream, 504, AIBridgeRuntimeCommandResult.FromFailure(command.Id, "handler_timeout"));
        }

        private bool ValidateAuthorization(HttpRequestData request, NetworkStream stream)
        {
            var expectedToken = _settings == null ? null : _settings.authToken;
            if (string.IsNullOrEmpty(expectedToken))
            {
                return true;
            }

            var actualToken = ReadBearerToken(request);
            if (string.Equals(expectedToken, actualToken, StringComparison.Ordinal))
            {
                return true;
            }

            WriteJson(stream, 401, new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "auth_failed",
                ["code"] = "auth_failed"
            });
            return false;
        }

        private static string ReadBearerToken(HttpRequestData request)
        {
            if (request == null || request.Headers == null)
            {
                return null;
            }

            if (!request.Headers.TryGetValue("Authorization", out var value) || string.IsNullOrEmpty(value))
            {
                return null;
            }

            const string prefix = "Bearer ";
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return value.Substring(prefix.Length).Trim();
        }

        private int ResolveCommandTimeout(HttpRequestData request)
        {
            if (request.Query.TryGetValue("timeoutMs", out var raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                if (parsed < MinCommandTimeoutMs)
                {
                    return MinCommandTimeoutMs;
                }

                if (parsed > MaxCommandTimeoutMs)
                {
                    return MaxCommandTimeoutMs;
                }

                return parsed;
            }

            return DefaultCommandTimeoutMs;
        }

        private IPAddress ResolveBindAddress()
        {
            var bind = _settings == null ? null : _settings.httpBindAddress;
            if (string.IsNullOrWhiteSpace(bind))
            {
                bind = "127.0.0.1";
            }

            if (bind == "*" || bind == "+" || bind == "0.0.0.0")
            {
                return IPAddress.Any;
            }

            if (IPAddress.TryParse(bind, out var address))
            {
                return address;
            }

            var addresses = Dns.GetHostAddresses(bind);
            if (addresses != null && addresses.Length > 0)
            {
                return addresses[0];
            }

            return IPAddress.Loopback;
        }

        private int ResolvePort()
        {
            var port = _settings == null ? 0 : _settings.httpPort;
            return port <= 0 ? DefaultPort : port;
        }

        private static string ResolveDisplayHost(IPAddress bindAddress)
        {
            if (bindAddress == null || bindAddress.Equals(IPAddress.Any))
            {
                return "127.0.0.1";
            }

            return bindAddress.ToString();
        }

        private static bool IsAddressAlreadyInUse(SocketException ex)
        {
            return ex != null && ex.SocketErrorCode == SocketError.AddressAlreadyInUse;
        }

        private static HttpRequestData ReadRequest(NetworkStream stream)
        {
            var bytes = new List<byte>(4096);
            var buffer = new byte[1024];
            var headerEnd = -1;
            var contentLength = 0;

            while (bytes.Count < MaxRequestBytes)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    bytes.Add(buffer[i]);
                }

                if (headerEnd < 0)
                {
                    headerEnd = FindHeaderEnd(bytes);
                    if (headerEnd >= 0)
                    {
                        var headerText = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
                        contentLength = ParseContentLength(headerText);
                    }
                }

                if (headerEnd >= 0 && bytes.Count >= headerEnd + 4 + contentLength)
                {
                    break;
                }
            }

            if (headerEnd < 0)
            {
                return null;
            }

            var allBytes = bytes.ToArray();
            var headersText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var request = ParseHeaders(headersText);
            if (request == null)
            {
                return null;
            }

            var bodyStart = headerEnd + 4;
            if (contentLength > 0 && bodyStart + contentLength <= allBytes.Length)
            {
                request.Body = Encoding.UTF8.GetString(allBytes, bodyStart, contentLength);
            }
            else
            {
                request.Body = string.Empty;
            }

            return request;
        }

        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++)
            {
                if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10)
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static int ParseContentLength(string headersText)
        {
            var headers = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (var i = 1; i < headers.Length; i++)
            {
                var line = headers[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, colon).Trim();
                if (!string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring(colon + 1).Trim();
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return 0;
        }

        private static HttpRequestData ParseHeaders(string headersText)
        {
            var lines = headersText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var firstLine = lines[0].Split(' ');
            var request = new HttpRequestData
            {
                Method = firstLine.Length > 0 ? firstLine[0] : string.Empty,
                RawPath = firstLine.Length > 1 ? firstLine[1] : "/",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            var queryStart = request.RawPath.IndexOf('?');
            request.Path = queryStart >= 0 ? request.RawPath.Substring(0, queryStart) : request.RawPath;
            if (queryStart >= 0 && queryStart + 1 < request.RawPath.Length)
            {
                ParseQuery(request.RawPath.Substring(queryStart + 1), request.Query);
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            return request;
        }

        private static void ParseQuery(string query, Dictionary<string, string> values)
        {
            var parts = query.Split('&');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                var equals = part.IndexOf('=');
                if (equals < 0)
                {
                    values[Uri.UnescapeDataString(part)] = string.Empty;
                }
                else
                {
                    values[Uri.UnescapeDataString(part.Substring(0, equals))] = Uri.UnescapeDataString(part.Substring(equals + 1));
                }
            }
        }

        private static void WriteJson(NetworkStream stream, int statusCode, object body)
        {
            var json = AIBridgeJson.Serialize(body, pretty: false);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var header = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + GetStatusText(statusCode) + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n"
                + "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static void WriteBinary(NetworkStream stream, int statusCode, string contentType, byte[] bodyBytes)
        {
            if (bodyBytes == null)
            {
                bodyBytes = new byte[0];
            }

            var header = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + GetStatusText(statusCode) + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n"
                + "\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static string GetStatusText(int statusCode)
        {
            switch (statusCode)
            {
                case 200:
                    return "OK";
                case 400:
                    return "Bad Request";
                case 401:
                    return "Unauthorized";
                case 404:
                    return "Not Found";
                case 504:
                    return "Gateway Timeout";
                default:
                    return "Internal Server Error";
            }
        }

        private sealed class HttpRequestData
        {
            public string Method { get; set; }
            public string RawPath { get; set; }
            public string Path { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public Dictionary<string, string> Query { get; set; }
            public string Body { get; set; }
        }
    }
}
