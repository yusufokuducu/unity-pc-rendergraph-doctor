using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace YusufOkuducu.PcRenderGraphDoctor.Editor
{
    /// <summary>
    /// Secure loopback-only (127.0.0.1) Streamable HTTP MCP Server for PC Render Graph & GPU Resident Drawer Doctor.
    /// Enforces Origin/Host DNS-rebinding guards, per-session Bearer/Mcp-Session-Id tokens,
    /// and thread-safe main-thread execution via EditorApplication.update.
    /// </summary>
    public sealed class RenderGraphDoctorMcpLiveServer : IDisposable
    {
        private sealed class MainThreadRpcWorkItem
        {
            public string JsonRpcBody;
            public TaskCompletionSource<string> Completion;
        }

        private static RenderGraphDoctorMcpLiveServer _sharedInstance;

        private readonly ConcurrentQueue<MainThreadRpcWorkItem> _mainThreadQueue = new ConcurrentQueue<MainThreadRpcWorkItem>();
        private readonly int _ownerThreadId;
        private readonly bool _dispatchDirectlyForTests;
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private int _port;
        private string _sessionToken;
        private bool _isRunning;

        public static RenderGraphDoctorMcpLiveServer Shared => _sharedInstance ?? (_sharedInstance = new RenderGraphDoctorMcpLiveServer());

        public bool IsRunning => _isRunning;
        public int Port => _port;
        public string SessionToken => _sessionToken;
        public string EndpointUrl => _isRunning ? $"http://127.0.0.1:{_port}/mcp/" : string.Empty;

        public RenderGraphDoctorMcpLiveServer(bool dispatchDirectlyOnBackgroundThreadForTests = false)
        {
            _ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            _dispatchDirectlyForTests = dispatchDirectlyOnBackgroundThreadForTests;
        }

        [MenuItem("Window/Analysis/MCP Servers/Toggle PC RenderGraph Doctor MCP Server")]
        public static void ToggleSharedServerMenu()
        {
            if (Shared.IsRunning)
            {
                Shared.Stop();
                Debug.Log("[PcRenderGraphDoctor MCP] Stopped loopback HTTP MCP server.");
            }
            else
            {
                Shared.Start(0);
                Debug.Log($"[PcRenderGraphDoctor MCP] Listening on {Shared.EndpointUrl} (Bearer Token: {Shared.SessionToken})");
            }
        }

        public bool Start(int preferredPort = 0, string customSessionToken = null)
        {
            if (_isRunning)
                return true;

            _port = preferredPort > 0 ? preferredPort : AllocateFreeLoopbackPort();
            _sessionToken = !string.IsNullOrWhiteSpace(customSessionToken)
                ? customSessionToken.Trim()
                : GenerateCryptographicToken();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/mcp/");
            _listener.Start();

            _cts = new CancellationTokenSource();
            _isRunning = true;

            EditorApplication.update -= PumpMainThreadQueue;
            EditorApplication.update += PumpMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;

            WriteDiscoveryFile();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return true;
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            EditorApplication.update -= PumpMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;

            while (_mainThreadQueue.TryDequeue(out MainThreadRpcWorkItem abandoned))
                abandoned.Completion.TrySetCanceled();
        }

        public void PumpMainThreadQueue()
        {
            int processed = 0;
            while (processed < 32 && _mainThreadQueue.TryDequeue(out MainThreadRpcWorkItem item))
            {
                processed++;
                try
                {
                    string response = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(item.JsonRpcBody);
                    item.Completion.TrySetResult(response);
                }
                catch (Exception ex)
                {
                    string err = $"{{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{{\"code\":-32603,\"message\":\"Internal error: {RenderGraphDoctorMcpBridge.EscapeJson(ex.Message)}\"}}}}";
                    item.Completion.TrySetResult(err);
                }
            }
        }

        public static bool IsAllowedOriginHeader(string originHeader)
        {
            if (string.IsNullOrWhiteSpace(originHeader))
                return true;
            if (!Uri.TryCreate(originHeader.Trim(), UriKind.Absolute, out Uri uri))
                return false;
            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAllowedHostHeader(string hostHeader)
        {
            if (string.IsNullOrWhiteSpace(hostHeader))
                return false;
            string trimmed = hostHeader.Trim();
            int colon = trimmed.LastIndexOf(':');
            string hostOnly = (colon > 0 && !trimmed.StartsWith("[", StringComparison.Ordinal))
                ? trimmed.Substring(0, colon)
                : trimmed;
            return string.Equals(hostOnly, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(hostOnly, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAuthorizedRequest(HttpListenerRequest req)
        {
            string auth = req.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string provided = auth.Substring("Bearer ".Length).Trim();
                if (CryptographicFixedTimeEquals(provided, _sessionToken))
                    return true;
            }
            string sessionHeader = req.Headers["Mcp-Session-Id"];
            return !string.IsNullOrEmpty(sessionHeader) && CryptographicFixedTimeEquals(sessionHeader.Trim(), _sessionToken);
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => ProcessHttpContextAsync(ctx), token);
            }
        }

        private async Task ProcessHttpContextAsync(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                if (!IsAllowedHostHeader(req.UserHostName) && !IsAllowedHostHeader(req.Headers["Host"]))
                {
                    WriteHttpResponse(res, 403, "{\"error\":\"Forbidden Host header.\"}");
                    return;
                }
                if (!IsAllowedOriginHeader(req.Headers["Origin"]))
                {
                    WriteHttpResponse(res, 403, "{\"error\":\"Forbidden Origin header.\"}");
                    return;
                }
                if (!IsAuthorizedRequest(req))
                {
                    WriteHttpResponse(res, 401, "{\"error\":\"Unauthorized.\"}");
                    return;
                }
                if (!string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    WriteHttpResponse(res, 405, "{\"error\":\"Method Not Allowed.\"}");
                    return;
                }

                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);

                string rpcResponse;
                if (_dispatchDirectlyForTests || Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
                {
                    rpcResponse = RenderGraphDoctorMcpBridge.HandleJsonRpcRequest(body);
                }
                else
                {
                    var work = new MainThreadRpcWorkItem
                    {
                        JsonRpcBody = body,
                        Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    };
                    _mainThreadQueue.Enqueue(work);
                    rpcResponse = await work.Completion.Task.ConfigureAwait(false);
                }

                res.Headers["Mcp-Session-Id"] = _sessionToken;
                if (string.IsNullOrEmpty(rpcResponse))
                {
                    res.StatusCode = 202;
                    res.Close();
                }
                else
                {
                    WriteHttpResponse(res, 200, rpcResponse);
                }
            }
            catch (Exception ex)
            {
                try { WriteHttpResponse(res, 500, $"{{\"error\":\"{RenderGraphDoctorMcpBridge.EscapeJson(ex.Message)}\"}}"); }
                catch { }
            }
        }

        private static void WriteHttpResponse(HttpListenerResponse res, int statusCode, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json ?? string.Empty);
            res.StatusCode = statusCode;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        private void WriteDiscoveryFile()
        {
            try
            {
                string tempDir = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);
                string discoveryPath = Path.Combine(tempDir, "mcp-pc-rendergraph-doctor.json");
                File.WriteAllText(
                    discoveryPath,
                    $"{{\"server\":\"{RenderGraphDoctorMcpBridge.ServerName}\",\"version\":\"{RenderGraphDoctorMcpBridge.PackageVersion}\",\"endpoint\":\"{EndpointUrl}\",\"token\":\"{_sessionToken}\"}}",
                    Encoding.UTF8);
            }
            catch { }
        }

        private static int AllocateFreeLoopbackPort()
        {
            var tcp = new TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }

        private static string GenerateCryptographicToken()
        {
            byte[] bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(48);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        private static bool CryptographicFixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public void Dispose() => Stop();
    }
}
