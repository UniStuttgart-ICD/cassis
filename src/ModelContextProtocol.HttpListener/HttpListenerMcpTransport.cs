using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.ComponentModel;

namespace ModelContextProtocol.HttpListener;

/// <summary>
/// HTTP transport implementing the Streamable HTTP protocol (single MCP endpoint).
/// </summary>
public sealed class HttpListenerMcpTransport : IServerTransport
{
    private const int MaxConcurrentRequests = 32;
    private const int MaxRequestBodyBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RequestBodyTimeout = TimeSpan.FromSeconds(30);

    private readonly System.Net.HttpListener _listener = new();
    private readonly McpServerOptions _options;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly string _endpointPath;
    private readonly ConcurrentDictionary<string, HttpSession> _sessions = new();
    private readonly ILogger<HttpListenerMcpTransport>? _logger;
    private readonly string? _debugLogPath;
    private readonly HashSet<string>? _allowedToolNames;
    private readonly SemaphoreSlim _requestSlots = new(MaxConcurrentRequests, MaxConcurrentRequests);

    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;
    private bool _stopping;
    private readonly object _lock = new object();

    // Tool and prompt discovery collections
    private readonly Dictionary<string, MethodInfo> _toolMethods = new();
    private readonly Dictionary<string, MethodInfo> _promptMethods = new();

    /// <summary>Raised whenever a JSON‑RPC message is received.</summary>
    public event Action<JsonRpcMessage>? MessageReceived;

    /// <summary>
    /// Completes when the listener loop stops and faults when it stops unexpectedly.
    /// </summary>
    public Task Completion => _listenTask ?? Task.CompletedTask;

    public HttpListenerMcpTransport(
        string prefix,
        McpServerOptions options,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? serviceProvider = null,
        string? debugLogPath = null,
        HashSet<string>? allowedToolNames = null)
    {
        var uri = new Uri(prefix);
        if (!uri.IsLoopback)
        {
            throw new ArgumentException("The MCP listener only accepts loopback prefixes.", nameof(prefix));
        }

        _listener.Prefixes.Add(prefix);

        // Also listen on 127.0.0.1 so clients that resolve localhost differently can connect.
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            var ipv4Prefix = new UriBuilder(uri.Scheme, "127.0.0.1", uri.Port, uri.AbsolutePath).Uri.ToString();
            if (!ipv4Prefix.EndsWith("/"))
            {
                ipv4Prefix += "/";
            }

            if (!_listener.Prefixes.Contains(ipv4Prefix))
            {
                _listener.Prefixes.Add(ipv4Prefix);
            }

            // Cursor and other clients often resolve localhost -> 127.0.0.1; mirror root prefix too.
            var ipv4RootPrefix = $"{uri.Scheme}://127.0.0.1:{uri.Port}/";

            if (!_listener.Prefixes.Contains(ipv4RootPrefix))
            {
                _listener.Prefixes.Add(ipv4RootPrefix);
            }
        }

        // Also listen on root for legacy SSE fallback
        var rootPrefix = $"{uri.Scheme}://{uri.Authority}/";
        if (!_listener.Prefixes.Contains(rootPrefix))
        {
            _listener.Prefixes.Add(rootPrefix);
        }
        _options = options;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _debugLogPath = debugLogPath;
        _allowedToolNames = allowedToolNames;
        _logger = loggerFactory?.CreateLogger<HttpListenerMcpTransport>();

        _endpointPath = uri.AbsolutePath.TrimEnd('/');

        WriteDebugLog($"[MCP HTTP] MCP endpoint: {_endpointPath}");
        WriteDebugLog($"[MCP HTTP] Prefix: {prefix}");
        WriteDebugLog($"[MCP HTTP] Running as admin check...");

        // Discover tools and prompts from assemblies
        WriteDebugLog("[MCP DEBUG] Beginning DiscoverToolsAndPrompts...");
        DiscoverToolsAndPrompts();
        WriteDebugLog("[MCP DEBUG] DiscoverToolsAndPrompts finished.");
    }

    private void WriteDebugLog(string message)
    {
        try
        {
            if (!string.IsNullOrEmpty(_debugLogPath))
            {
                File.AppendAllText(_debugLogPath, $"[{DateTime.UtcNow:O}] {message}\n");
            }
        }
        catch (Exception ex)
        {
            // Debug log write failure is non-critical; avoid recursive logging
            _logger?.LogWarning("Failed to write debug log: {Message}", ex.Message);
        }
        _logger?.LogInformation("{Message}", message);
    }

    private static string GetExposedName(MethodInfo method, string? attributeName)
        => (attributeName?.Trim() is { Length: > 0 } n ? n : method.Name).ToLowerInvariant();

    /// <summary>
    /// Discovers MCP tools and prompts using reflection.
    /// </summary>
    private void DiscoverToolsAndPrompts()
    {
        try
        {
            WriteDebugLog("[MCP DEBUG] Starting tool and prompt discovery...");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .ToList();

            WriteDebugLog($"[MCP DEBUG] Scanning {assemblies.Count} assemblies for MCP tool types");

            foreach (var assembly in assemblies)
            {
                IList<Type> toolTypes;
                IList<Type> promptTypes;
                try
                {
                    var types = assembly.GetTypes();
                    toolTypes = types
                        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                        .ToList();
                    promptTypes = types
                        .Where(t => t.GetCustomAttribute<McpServerPromptTypeAttribute>() != null)
                        .ToList();
                }
                catch (ReflectionTypeLoadException)
                {
                    continue;
                }

                if (toolTypes.Count == 0 && promptTypes.Count == 0)
                    continue;

                WriteDebugLog($"[MCP DEBUG] Found {toolTypes.Count} tool types and {promptTypes.Count} prompt types in {assembly.GetName().Name}");

                // Discover tools
                foreach (var type in toolTypes)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                        .ToList();

                    foreach (var method in methods)
                    {
                        var key = GetExposedName(method, method.GetCustomAttribute<McpServerToolAttribute>()?.Name);
                        if (_allowedToolNames != null && !_allowedToolNames.Contains(key))
                            continue;
                        if (!_toolMethods.ContainsKey(key))
                        {
                            _toolMethods[key] = method;
                            WriteDebugLog($"[MCP DEBUG] Registered tool: {key}");
                        }
                        else
                        {
                            WriteDebugLog($"[MCP WARNING] Duplicate tool name '{key}' from {method.DeclaringType?.Name}.{method.Name} -- skipping.");
                        }
                    }
                }

                // Discover prompts
                foreach (var type in promptTypes)
                {
                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
                        .ToList();

                    foreach (var method in methods)
                    {
                        var key = GetExposedName(method, method.GetCustomAttribute<McpServerPromptAttribute>()?.Name);
                        if (!_promptMethods.ContainsKey(key))
                        {
                            _promptMethods[key] = method;
                            WriteDebugLog($"[MCP DEBUG] Registered prompt: {key}");
                        }
                        else
                        {
                            WriteDebugLog($"[MCP WARNING] Duplicate prompt name '{key}' from {method.DeclaringType?.Name}.{method.Name} -- skipping.");
                        }
                    }
                }
            }

            WriteDebugLog($"[MCP DEBUG] Discovery complete. {_toolMethods.Count} tools, {_promptMethods.Count} prompts registered");
        }
        catch (Exception ex)
        {
            WriteDebugLog($"[MCP ERROR] Error during tool discovery: {ex.Message}");
            WriteDebugLog($"[MCP ERROR] Stack trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Checks if running as administrator on Windows.
    /// </summary>
    private static bool IsRunningAsAdministrator()
    {
#if NET8_0_OR_GREATER
        try
        {
            // Only check on Windows
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
            {
                return true; // Not applicable on non-Windows
            }
            
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // Cannot determine admin status; assume non-admin
            return false;
        }
#else
        // On .NET Standard 2.0, we can't check admin status reliably, assume false
        return false;
#endif
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is not null)
            throw new InvalidOperationException("Server already started");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Start the listener asynchronously to avoid blocking the UI thread
        await Task.Run(() =>
        {
            try
            {
                WriteDebugLog($"Attempting to start HttpListener on: {string.Join(", ", _listener.Prefixes)}");
                _listener.Start();
                WriteDebugLog($"HttpListener started successfully. IsListening: {_listener.IsListening}");
                WriteDebugLog($"Server initialization complete. Listening for connections...");
            }
            catch (HttpListenerException ex)
            {
                var isAdmin = IsRunningAsAdministrator();
                WriteDebugLog($"HttpListener failed to start: {ex.Message}");
                WriteDebugLog($"Error code: {ex.ErrorCode}");
                WriteDebugLog($"Running as Administrator: {isAdmin}");
                WriteDebugLog($"This might be due to:");
                WriteDebugLog($"- Administrator privileges required for HTTP listener");
                WriteDebugLog($"- Port already in use by another application");
                WriteDebugLog($"- Firewall blocking the connection");
                WriteDebugLog($"- URL reservation required (netsh http add urlacl)");
                _logger?.LogError("HttpListener failed to start: {Message}", ex.Message);
                _logger?.LogError("Error code: {ErrorCode}", ex.ErrorCode);
                _logger?.LogError("Running as Administrator: {IsAdmin}", isAdmin);
                throw;
            }
            catch (Exception ex)
            {
                WriteDebugLog($"Unexpected error starting listener: {ex.Message}");
                WriteDebugLog($"Stack trace: {ex.StackTrace}");
                _logger?.LogError("Unexpected error starting listener: {Message}", ex.Message);
                throw;
            }
        }, cancellationToken);

        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        WriteDebugLog($"Listen task started. Server should be ready for connections.");
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
#if NETSTANDARD2_0
                context = await _listener.GetContextAsync();
                if (token.IsCancellationRequested) break;
#else
                context = await _listener.GetContextAsync().WaitAsync(token);
#endif
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _stopping)
            {
                WriteDebugLog("[MCP HTTP] Listener stopped because cancellation was requested.");
                break;
            }
            catch (HttpListenerException ex) when (token.IsCancellationRequested || _stopping)
            {
                WriteDebugLog($"[MCP HTTP] Listener stopped during shutdown: {ex}");
                break;
            }
            catch (ObjectDisposedException ex) when (token.IsCancellationRequested || _stopping || _disposed)
            {
                WriteDebugLog($"[MCP HTTP] Listener disposed during shutdown: {ex}");
                break;
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[MCP ERROR] Listener stopped unexpectedly: {ex}");
                _logger?.LogError(ex, "MCP listener stopped unexpectedly");
                throw new InvalidOperationException("The MCP listener stopped unexpectedly.", ex);
            }

            if (!_requestSlots.Wait(0))
            {
                context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                context.Response.Close();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleContextAsync(context, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Server shutdown cancels active requests.
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Unhandled MCP request error");
                    try
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        context.Response.Close();
                    }
                    catch
                    {
                        // The client may already have closed the response.
                    }
                }
                finally
                {
                    _requestSlots.Release();
                }
            });
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";

        // Add debugging to see what's being requested

        // only accept requests to the configured MCP endpoint or root for legacy SSE
        bool isRoot = string.Equals(path, string.Empty, StringComparison.OrdinalIgnoreCase);
        bool matchesEndpoint = string.Equals(path, _endpointPath, StringComparison.OrdinalIgnoreCase);
        if (!matchesEndpoint && !isRoot)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
            return;
        }


        var requestOrigin = context.Request.Headers["Origin"];
        if (requestOrigin is not null && !IsOriginAllowed(requestOrigin))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            context.Response.Close();
            return;
        }

        if (context.Request.HttpMethod == "OPTIONS")
        {
            SetCorsOrigin(context.Response, requestOrigin);
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Mcp-Session-Id, Origin, Last-Event-Id";
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.Close();
            return;
        }

        switch (context.Request.HttpMethod)
        {
            case "GET":
                await HandleGetSseAsync(context, token);
                break;
            case "POST":
                await HandlePostAsync(context, token);
                break;
            case "OPTIONS":
                // simple CORS preflight (handled above, but kept for safety)
                SetCorsOrigin(context.Response, requestOrigin);
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Accept, Mcp-Session-Id, Origin, Last-Event-Id";
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
                break;
            default:
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                break;
        }
    }

    /// <summary>
    /// Sends a JSON-RPC message to a specific session or broadcasts to all sessions.
    /// </summary>
    public async Task SendMessageAsync(JsonRpcMessage message, string? sessionId = null)
    {
        if (sessionId != null)
        {
            await TrySendToSessionAsync(message, sessionId).ConfigureAwait(false);
            return;
        }

        var tasks = _sessions.Values.Select(session => session.SendMessageAsync(message));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles GET requests to open a standalone SSE stream.
    /// </summary>
    private async Task HandleGetSseAsync(HttpListenerContext context, CancellationToken token)
    {
        PruneInactiveSessions();

        string sessionId = Guid.NewGuid().ToString("N");
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        SetCorsOrigin(context.Response, context.Request.Headers["Origin"]);
        context.Response.Headers["Mcp-Session-Id"] = sessionId;
        // Ensure the connection is treated as streaming and not buffered by proxies
        context.Response.SendChunked = true;
        context.Response.KeepAlive = true;
        context.Response.Headers["X-Accel-Buffering"] = "no";


        // Handle SSE streaming directly without separate transport
        using var writer = CreateSseWriter(context.Response.OutputStream);
        var session = new HttpSession(sessionId, writer);
        if (!_sessions.TryAdd(sessionId, session))
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.Close();
            return;
        }

        try
        {
            // Keep connection alive with SSE comment heartbeats to prevent idle disconnects
            // Comment lines beginning with ':' are ignored by EventSource but keep the TCP stream active
            while (!token.IsCancellationRequested)
            {
                await writer.WriteLineAsync($": keepalive {DateTime.UtcNow:o}");
                await writer.WriteLineAsync();
                await writer.FlushAsync();
                await Task.Delay(15000, token); // 15s heartbeat
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when server is shutting down
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("SSE stream error for session {SessionId}: {Message}", sessionId, ex.Message);
        }
        finally
        {
            _sessions.TryRemove(sessionId, out _);
            context.Response.Close();
        }
    }

    /// <summary>
    /// Handles POST requests to send JSON‑RPC messages to the server.
    /// </summary>
    private async Task HandlePostAsync(HttpListenerContext context, CancellationToken token)
    {
        if (context.Request.ContentLength64 > MaxRequestBodyBytes)
        {
            context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
            context.Response.Close();
            return;
        }

        // look up existing session from header
        string? sessionId = context.Request.Headers["Mcp-Session-Id"];
        HttpSession? session = null;
        if (sessionId is not null)
            _sessions.TryGetValue(sessionId, out session);

        // parse JSON‑RPC messages (supports arrays, single objects, or JSONL sequences)
        List<JsonRpcMessage> messages;
        using var requestBodyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestBodyCts.CancelAfter(RequestBodyTimeout);
        try
        {
            messages = await ParseJsonRpcMessagesAsync(
                context.Request.InputStream,
                MaxRequestBodyBytes,
                requestBodyCts.Token);
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
            context.Response.Close();
            return;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            context.Response.StatusCode = (int)HttpStatusCode.RequestTimeout;
            context.Response.Close();
            return;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }

        bool containsRequest = messages.Any(msg => msg is JsonRpcRequest);
        if (messages.Count == 0)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.Close();
            return;
        }

        if (!containsRequest)
        {
            // only notifications/responses -> return 202 Accepted
            if (session is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.Close();
                return;
            }

            foreach (var msg in messages)
            {
                MessageReceived?.Invoke(msg);
            }

            context.Response.StatusCode = (int)HttpStatusCode.Accepted;
            context.Response.Close();
            return;
        }

        // Route to an active GET SSE session when the client supplied a live session id.
        if (sessionId != null
            && _sessions.TryGetValue(sessionId, out var existingSseSession)
            && existingSseSession.CanAcceptMessages)
        {
            var allDelivered = true;
            foreach (var msg in messages)
            {
                if (!await ProcessMessageAsync(msg, sessionId).ConfigureAwait(false))
                {
                    allDelivered = false;
                }
            }

            if (allDelivered)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }

            _sessions.TryRemove(sessionId, out _);
            _logger?.LogDebug(
                "POST could not deliver to SSE session {SessionId}; opening POST response stream",
                sessionId);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
            _logger?.LogDebug(
                "POST with inactive Mcp-Session-Id {SessionId}; opening a new response stream",
                sessionId);
        }

        // Fallback: short-lived SSE stream on this POST response (always use a fresh id).
        string responseSessionId = Guid.NewGuid().ToString("N");
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        SetCorsOrigin(context.Response, context.Request.Headers["Origin"]);
        context.Response.Headers["Mcp-Session-Id"] = responseSessionId;
        context.Response.SendChunked = true;
        context.Response.KeepAlive = true;
        context.Response.Headers["X-Accel-Buffering"] = "no";


        using var writer = CreateSseWriter(context.Response.OutputStream);
        var tempSession = new HttpSession(responseSessionId, writer);
        if (!_sessions.TryAdd(responseSessionId, tempSession))
        {
            responseSessionId = Guid.NewGuid().ToString("N");
            tempSession = new HttpSession(responseSessionId, writer);
            context.Response.Headers["Mcp-Session-Id"] = responseSessionId;
            if (!_sessions.TryAdd(responseSessionId, tempSession))
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
                return;
            }
        }

        try
        {
            foreach (var msg in messages)
            {
                await ProcessMessageAsync(msg, responseSessionId);
            }

            // Briefly keep the stream open to flush responses, also send a heartbeat once
            await writer.WriteLineAsync(": post-fallback-keepalive");
            await writer.WriteLineAsync();
            await writer.FlushAsync();
            await Task.Delay(100, token);
        }
        catch (OperationCanceledException)
        {
            // Expected when server is shutting down
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("POST SSE stream error for session {SessionId}: {Message}", responseSessionId, ex.Message);
        }
        finally
        {
            _sessions.TryRemove(responseSessionId, out _);
            context.Response.Close();
        }
    }

    private static async Task<List<JsonRpcMessage>> ParseJsonRpcMessagesAsync(
        Stream input,
        int maxBytes,
        CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await CopyToAsyncWithCancellation(input, buffer, maxBytes, token);
        var payload = buffer.ToArray();

        if (payload.Length == 0)
        {
            return new List<JsonRpcMessage>();
        }

        var messages = new List<JsonRpcMessage>();
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = true
        });

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Comment)
            {
                continue;
            }

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    if (el.Deserialize<JsonRpcMessage>() is { } msg)
                    {
                        messages.Add(msg);
                    }
                }
            }
            else
            {
                if (root.Deserialize<JsonRpcMessage>() is { } msg)
                {
                    messages.Add(msg);
                }
            }
        }

        return messages;
    }

    private static async Task CopyToAsyncWithCancellation(
        Stream input,
        Stream destination,
        int maxBytes,
        CancellationToken token)
    {
        var buffer = new byte[81920];
        var totalBytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
        {
            totalBytes += read;
            if (totalBytes > maxBytes)
            {
                throw new InvalidDataException($"Request body exceeds {maxBytes} bytes.");
            }

            await destination.WriteAsync(buffer, 0, read, token);
        }
    }

    /// <summary>
    /// Processes an MCP message through the server framework and sends responses back.
    /// </summary>
    /// <returns>False when a JSON-RPC response could not be written to the session stream.</returns>
    private async Task<bool> ProcessMessageAsync(JsonRpcMessage message, string sessionId)
    {
        try
        {

            if (message is JsonRpcRequest request)
            {

                // Handle core MCP protocol requests
                var response = request.Method switch
                {
                    "initialize" => await HandleInitializeRequest(request),
                    "tools/list" => await HandleToolsListRequest(request),
                    "tools/call" => await HandleToolCallRequest(request),
                    "prompts/list" => await HandlePromptsListRequest(request),
                    "prompts/get" => await HandlePromptGetRequest(request),
                    "resources/list" => await HandleResourcesListRequest(request),
                    "resources/read" => await HandleResourceReadRequest(request),
                    _ => CreateErrorResponse(request.Id, -32601, "Method not found", $"Unknown method: {request.Method}")
                };

                MessageReceived?.Invoke(message);

                if (response != null)
                {
                    return await TrySendToSessionAsync(response, sessionId).ConfigureAwait(false);
                }

                return true;
            }

            if (message is JsonRpcNotification notification)
            {

                // Handle notifications (like initialized)
                switch (notification.Method)
                {
                    case "notifications/initialized":
                        break;
                    case "notifications/cancelled":
                        break;
                    default:
                        break;
                }
            }

            MessageReceived?.Invoke(message);
            return true;
        }
        catch (Exception ex)
        {

            // Send error response if it was a request
            if (message is JsonRpcRequest request)
            {
                var errorResponse = CreateErrorResponse(request.Id, -32603, "Internal error", ex.Message);
                MessageReceived?.Invoke(message);
                return await TrySendToSessionAsync(errorResponse, sessionId).ConfigureAwait(false);
            }

            return false;
        }
    }

    private async Task<bool> TrySendToSessionAsync(JsonRpcMessage message, string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        return await session.SendMessageAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the MCP initialize request and returns the initialize response.
    /// </summary>
    private async Task<JsonRpcResponse> HandleInitializeRequest(JsonRpcRequest request)
    {
        var serverInfo = _options.ServerInfo
            ?? throw new InvalidOperationException("MCP server information is not configured.");

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToNode(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = new { }, prompts = new { } },
                serverInfo
            })
        };

        return response;
    }

    /// <summary>
    /// Handles tools/list request by returning discovered MCP tools.
    /// </summary>
    private async Task<JsonRpcResponse> HandleToolsListRequest(JsonRpcRequest request)
    {

        var tools = new List<object>();

        foreach (var kvp in _toolMethods)
        {
            var method = kvp.Value;
            var toolName = kvp.Key;

            try
            {
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? $"Execute {method.Name}";

                var parameters = new Dictionary<string, object>();
                var properties = new Dictionary<string, object>();
                var required = new List<string>();

                foreach (var param in method.GetParameters())
                {
                    // Skip service injection parameters
                    if (param.ParameterType.IsInterface && param.ParameterType.Name.StartsWith("I"))
                        continue;

                    var paramName = param.Name!;
                    var paramDescription = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? paramName;

                    // For object? or object types, use anyOf to accept any JSON value type
                    var paramType = param.ParameterType;
                    object schemaDef;
                    
                    // Check if it's object type (handle nullable reference types)
                    var underlyingType = Nullable.GetUnderlyingType(paramType) ?? paramType;
                    if (underlyingType == typeof(object))
                    {
                        // Use anyOf to accept any JSON value type (string, number, boolean, null, object, array)
                        schemaDef = new
                        {
                            anyOf = new object[]
                            {
                                new { type = "string" },
                                new { type = "number" },
                                new { type = "integer" },
                                new { type = "boolean" },
                                new { type = "null" },
                                new { type = "object" },
                                new { type = "array" }
                            },
                            description = paramDescription
                        };
                    }
                    else
                    {
                        schemaDef = new
                        {
                            type = GetJsonSchemaType(paramType),
                            description = paramDescription
                        };
                    }

                    properties[paramName] = schemaDef;

                    if (!param.HasDefaultValue)
                    {
                        required.Add(paramName);
                    }
                }

                if (properties.Any())
                {
                    parameters["type"] = "object";
                    parameters["properties"] = properties;
                    if (required.Any())
                        parameters["required"] = required;
                }

                var tool = new
                {
                    name = toolName,
                    description = description,
                    inputSchema = parameters.Any() ? (object)parameters : new { type = "object" }
                };

                tools.Add(tool);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[MCP WARN] Error building schema for tool '{kvp.Key}': {ex.Message}");
            }
        }


        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToNode(new { tools })
        };
    }

    /// <summary>
    /// Handles tools/call request by invoking the specified tool.
    /// </summary>
    private async Task<JsonRpcResponse> HandleToolCallRequest(JsonRpcRequest request)
    {
        try
        {
            // Avoid direct JsonNode/JsonObject to keep netstandard2.0 compatibility
            var rawParamsJson = JsonSerializer.Serialize(request.Params, McpJsonUtilities.DefaultOptions);
            var toolParams = JsonSerializer.Deserialize<ToolCallParams>(rawParamsJson, McpJsonUtilities.DefaultOptions);

            if (toolParams == null)
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Unable to parse parameters");

            var toolNameLower = toolParams.Name?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(toolNameLower))
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Missing tool name");

            if (!_toolMethods.TryGetValue(toolNameLower, out var method))
                return CreateErrorResponse(request.Id, -32601, "Not found", $"Tool '{toolNameLower}' not found");

            var argMap = toolParams.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);

            // Build parameter list: resolve DI services for interface parameters, bind JSON for value params
            var parameters = method.GetParameters();
            var invokeArgs = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];

                if (p.ParameterType.IsInterface && p.ParameterType.Name.StartsWith("I"))
                {
                    if (_serviceProvider == null)
                        return CreateErrorResponse(request.Id, -32000, "Server error", "Service provider not available");
                    invokeArgs[i] = _serviceProvider.GetService(p.ParameterType);
                    continue;
                }

                var paramName = p.Name!;
                if (argMap.TryGetValue(paramName, out var elem))
                {
                    invokeArgs[i] = elem.Deserialize(p.ParameterType, McpJsonUtilities.DefaultOptions);
                }
                else if (p.HasDefaultValue)
                {
                    invokeArgs[i] = p.DefaultValue;
                }
                else
                {
                    return CreateErrorResponse(request.Id, -32602, "Invalid params", $"Missing required argument '{paramName}'");
                }
            }

            var resultObj = method.Invoke(null, invokeArgs);
            if (resultObj is Task task)
            {
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (taskType.IsGenericType)
                {
                    var resultProperty = taskType.GetProperty("Result");
                    var taskResult = resultProperty?.GetValue(task);
                    
                    // Handle CallToolResult properly using reflection
                    if (taskResult != null && taskResult.GetType().Name == "CallToolResult")
                    {
                        var contentProperty = taskResult.GetType().GetProperty("Content");
                        if (contentProperty != null)
                        {
                            var content = contentProperty.GetValue(taskResult);
                            return new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToNode(new { content }) };
                        }
                    }
                    
                    return new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToNode(new { content = taskResult }) };
                }
                return new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToNode(new { success = true }) };
            }

            // Handle CallToolResult properly using reflection
            if (resultObj != null && resultObj.GetType().Name == "CallToolResult")
            {
                var contentProperty = resultObj.GetType().GetProperty("Content");
                if (contentProperty != null)
                {
                    var content = contentProperty.GetValue(resultObj);
                    return new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToNode(new { content }) };
                }
            }

            return new JsonRpcResponse { Id = request.Id, Result = JsonSerializer.SerializeToNode(new { content = resultObj }) };
        }
        catch (TargetInvocationException ex)
        {
            return CreateErrorResponse(request.Id, -32001, "Tool error", ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(request.Id, -32000, "Server error", ex.Message);
        }
    }

    private sealed class ToolCallParams
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public Dictionary<string, System.Text.Json.JsonElement>? Arguments { get; set; }
    }

    /// <summary>
    /// Handles prompts/list request by returning discovered MCP prompts.
    /// </summary>
    private async Task<JsonRpcResponse> HandlePromptsListRequest(JsonRpcRequest request)
    {
        var prompts = new List<object>();

        foreach (var kvp in _promptMethods)
        {
            var method = kvp.Value;
            var promptName = kvp.Key;

            try
            {
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? $"Execute {method.Name}";

                var arguments = new List<object>();
                foreach (var param in method.GetParameters())
                {
                    if (param.ParameterType.IsInterface && param.ParameterType.Name.StartsWith("I"))
                        continue;

                    var arg = new Dictionary<string, object>
                    {
                        ["name"] = param.Name!,
                        ["description"] = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? param.Name!,
                        ["required"] = !param.HasDefaultValue
                    };
                    arguments.Add(arg);
                }

                var prompt = new
                {
                    name = promptName,
                    description = description,
                    arguments = arguments
                };

                prompts.Add(prompt);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[MCP WARN] Error building schema for prompt '{kvp.Key}': {ex.Message}");
            }
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToNode(new { prompts })
        };
    }

    /// <summary>
    /// Handles prompts/get request by invoking the specified prompt.
    /// </summary>
    private async Task<JsonRpcResponse> HandlePromptGetRequest(JsonRpcRequest request)
    {
        try
        {
            var rawParamsJson = JsonSerializer.Serialize(request.Params, McpJsonUtilities.DefaultOptions);
            var promptParams = JsonSerializer.Deserialize<PromptGetParams>(rawParamsJson, McpJsonUtilities.DefaultOptions);

            if (promptParams == null)
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Unable to parse parameters");

            var promptNameLower = promptParams.Name?.ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(promptNameLower))
                return CreateErrorResponse(request.Id, -32602, "Invalid params", "Missing prompt name");

            if (!_promptMethods.TryGetValue(promptNameLower, out var method))
                return CreateErrorResponse(request.Id, -32601, "Not found", $"Prompt '{promptNameLower}' not found");

            var argMap = promptParams.Arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var parameters = method.GetParameters();
            var invokeArgs = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];

                if (p.ParameterType.IsInterface && p.ParameterType.Name.StartsWith("I"))
                {
                    if (_serviceProvider == null)
                        return CreateErrorResponse(request.Id, -32000, "Server error", "Service provider not available");
                    invokeArgs[i] = _serviceProvider.GetService(p.ParameterType);
                    continue;
                }

                var paramName = p.Name!;
                if (argMap.TryGetValue(paramName, out var val))
                {
                    invokeArgs[i] = val;
                }
                else if (p.HasDefaultValue)
                {
                    invokeArgs[i] = p.DefaultValue;
                }
                else
                {
                    return CreateErrorResponse(request.Id, -32602, "Invalid params", $"Missing required argument '{paramName}'");
                }
            }

            var resultObj = method.Invoke(null, invokeArgs);

            // ChatMessage has Role and Text properties — map to MCP prompt response
            var messages = new List<object>();
            if (resultObj != null)
            {
                var roleProperty = resultObj.GetType().GetProperty("Role");
                var textProperty = resultObj.GetType().GetProperty("Text");

                var roleValue = roleProperty?.GetValue(resultObj);
                var roleStr = roleValue?.ToString()?.ToLowerInvariant() ?? "user";
                var textStr = textProperty?.GetValue(resultObj)?.ToString() ?? string.Empty;

                messages.Add(new
                {
                    role = roleStr,
                    content = new { type = "text", text = textStr }
                });
            }

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = JsonSerializer.SerializeToNode(new { messages })
            };
        }
        catch (TargetInvocationException ex)
        {
            return CreateErrorResponse(request.Id, -32001, "Prompt error", ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(request.Id, -32000, "Server error", ex.Message);
        }
    }

    private sealed class PromptGetParams
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public Dictionary<string, string>? Arguments { get; set; }
    }

    /// <summary>
    /// Handles resources/list request.
    /// </summary>
    private async Task<JsonRpcResponse> HandleResourcesListRequest(JsonRpcRequest request)
    {
        var resources = new List<object>();

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToNode(new { resources })
        };
    }

    /// <summary>
    /// Handles resources/read request.
    /// </summary>
    private async Task<JsonRpcResponse> HandleResourceReadRequest(JsonRpcRequest request)
    {
        return CreateErrorResponse(request.Id, -32601, "Not implemented", "Resource reading not yet implemented");
    }

    /// <summary>
    /// Gets the JSON schema type for a .NET type.
    /// </summary>
    private string GetJsonSchemaType(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
        if (type == typeof(bool)) return "boolean";
        if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))) return "array";
        return "object";
    }

    /// <summary>
    /// Creates a JSON-RPC error response.
    /// </summary>
    private JsonRpcResponse CreateErrorResponse(RequestId id, int code, string message, string? data = null)
    {
        var errorData = new
        {
            code,
            message,
            data
        };

        return new JsonRpcResponse
        {
            Id = id,
            Result = JsonSerializer.SerializeToNode(new { error = errorData })
        };
    }

    /// <summary>
    /// Allows browser requests only from loopback origins.
    /// </summary>
    private bool IsOriginAllowed(string origin)
    {
        try
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                return false;
            }

            if (!string.Equals(originUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(originUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return originUri.IsLoopback;
        }
        catch
        {
            // Malformed origin header; reject for safety
            return false;
        }
    }

    private static void SetCorsOrigin(HttpListenerResponse response, string? origin)
    {
        if (!string.IsNullOrWhiteSpace(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
            response.Headers["Vary"] = "Origin";
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (_stopping) return;
            _stopping = true;
            if (_cts is not null)
            {
                _cts.Cancel();
                try
                {
                    if (_listener.IsListening) _listener.Stop();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning("Error stopping HttpListener: {Message}", ex.Message);
                }
            }
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteDebugLog($"[MCP ERROR] Listener task was already faulted when stopping: {ex}");
                _logger?.LogError(ex, "Listener task was already faulted when stopping");
            }
        }

        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return;
        }

        await StopAsync().ConfigureAwait(false);

        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        foreach (var s in _sessions.Values)
            await s.DisposeAsync();
        _sessions.Clear();

        try
        {
            _listener.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Error closing HttpListener: {Message}", ex.Message);
        }
    }

    private void PruneInactiveSessions()
    {
        foreach (var entry in _sessions)
        {
            if (!entry.Value.CanAcceptMessages)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    private static StreamWriter CreateSseWriter(Stream outputStream)
    {
        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var writer = new StreamWriter(outputStream, encoding) { AutoFlush = true };
        return writer;
    }

    private sealed class HttpSession : IAsyncDisposable
    {
        public string Id { get; }
        public bool CanAcceptMessages
        {
            get
            {
                lock (_lock)
                {
                    return !_disposed && _writer != null;
                }
            }
        }

        private readonly StreamWriter? _writer;
        private readonly object _lock = new();
        private bool _disposed;

        public HttpSession(string id, StreamWriter? writer = null)
        {
            Id = id;
            _writer = writer;
        }

        public async Task<bool> SendMessageAsync(JsonRpcMessage message)
        {
            StreamWriter? writer;
            lock (_lock)
            {
                if (_disposed || _writer == null)
                {
                    return false;
                }

                writer = _writer;
            }

            try
            {
                var json = JsonSerializer.Serialize(message);
                await writer.WriteLineAsync($"data: {json}").ConfigureAwait(false);
                await writer.WriteLineAsync().ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                lock (_lock)
                {
                    _disposed = true;
                }

                return false;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                if (_disposed) return new ValueTask();
                _disposed = true;
            }

            _writer?.Dispose();
            return new ValueTask();
        }
    }
}
