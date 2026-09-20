using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cassis.Diagnostics;
using ModelContextProtocol.Protocol;
using Rhino;

namespace Cassis;

/// <summary>
/// Process-wide MCP server lifetime. Independent of any Grasshopper document or Cassis component.
/// </summary>
public static class CassisRuntime
{
    private static readonly object Gate = new();
    private static CassisHost? _server;
    private static CancellationTokenSource? _cts;
    private static string _status = "Stopped";
    public const string McpEndpoint = "http://localhost:3003/mcp/";
    public const string AgentSetupUrl = "https://github.com/UniStuttgart-ICD/cassis/blob/main/docs/agent-setup.md";

    private static string _prefix = McpEndpoint;
    private static HashSet<string> _enabledTools = new(ToolCategories.AllTools(), StringComparer.OrdinalIgnoreCase);
    private static DateTime? _serverStartTime;
    private static DateTime? _lastMessageTime;
    private static DateTime? _lastToolCall;
    private static string _lastToolName = string.Empty;
    private static int _totalMessagesReceived;
    private static bool _clientInitialized;
    private static int _startInFlight;
    private static int _transportRestartInProgress;
    private static readonly ConcurrentQueue<string> Messages = new();

    /// <summary>Raised when status or stats change (UI should refresh).</summary>
    public static event Action? Changed;

    public static string Status
    {
        get { lock (Gate) return _status; }
    }

    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _server != null && string.Equals(_status, "Running", StringComparison.Ordinal);
            }
        }
    }

    public static bool ClientInitialized
    {
        get { lock (Gate) return _clientInitialized; }
    }

    public static IReadOnlyCollection<string> EnabledTools
    {
        get
        {
            lock (Gate)
            {
                return _enabledTools.ToArray();
            }
        }
    }

    public static string[] SnapshotMessages() => Messages.ToArray();

    public static string GetUptimeShort()
    {
        lock (Gate)
        {
            if (_status == "Stopped" || !_serverStartTime.HasValue)
            {
                return string.Empty;
            }

            var d = DateTime.UtcNow - _serverStartTime.Value;
            if (d.TotalDays >= 1) return $"{d.Days}d {d.Hours}h";
            if (d.TotalHours >= 1) return $"{d.Hours}h {d.Minutes}m";
            if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
            return $"{d.TotalSeconds:F0}s";
        }
    }

    public static string GetMessageCountString()
    {
        lock (Gate) return _totalMessagesReceived.ToString();
    }

    public static string GetLastMsgShort()
    {
        lock (Gate)
        {
            if (!_lastMessageTime.HasValue) return "—";
            var d = DateTime.UtcNow - _lastMessageTime.Value;
            if (d.TotalMinutes < 1) return $"{d.TotalSeconds:F0}s ago";
            if (d.TotalHours < 1) return $"{d.TotalMinutes:F0}m ago";
            return $"{d.TotalHours:F1}h ago";
        }
    }

    public static string GetLastToolShort()
    {
        lock (Gate)
        {
            if (_lastToolCall == null || string.IsNullOrEmpty(_lastToolName)) return "—";
            var d = DateTime.UtcNow - _lastToolCall.Value;
            var when = d.TotalMinutes < 1 ? $"{d.TotalSeconds:F0}s ago" :
                       d.TotalHours < 1 ? $"{d.TotalMinutes:F0}m ago" :
                       $"{d.TotalHours:F1}h ago";
            return $"{_lastToolName} • {when}";
        }
    }

    /// <summary>
    /// Starts MCP if not already starting/running.
    /// When <paramref name="allTools"/> is true (auto-start), every registered tool is exposed.
    /// </summary>
    public static void RequestStart(string? prefix = null, IEnumerable<string>? enabledTools = null, bool allTools = false)
    {
        lock (Gate)
        {
            if (_server != null ||
                string.Equals(_status, "Starting", StringComparison.Ordinal) ||
                string.Equals(_status, "Restarting", StringComparison.Ordinal))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(prefix))
            {
                _prefix = prefix!;
            }

            if (allTools)
            {
                _enabledTools = new HashSet<string>(ToolCategories.AllTools(), StringComparer.OrdinalIgnoreCase);
            }
            else if (enabledTools != null)
            {
                _enabledTools = new HashSet<string>(enabledTools, StringComparer.OrdinalIgnoreCase);
            }

            _status = "Starting";
        }

        NotifyChanged();
        _ = StartCoreAsync();
    }

    /// <summary>Stops MCP if running. No-op while already stopping/stopped.</summary>
    public static void RequestStop()
    {
        CassisHost? server;
        CancellationTokenSource? cts;
        lock (Gate)
        {
            if (_server == null &&
                !string.Equals(_status, "Starting", StringComparison.Ordinal) &&
                !string.Equals(_status, "Restarting", StringComparison.Ordinal))
            {
                _status = "Stopped";
                NotifyChangedUnlocked();
                return;
            }

            server = _server;
            cts = _cts;
            _server = null;
            _cts = null;
            _status = "Stopping";
        }

        NotifyChanged();
        Task.Run(async () =>
        {
            var ok = await DisposeServerAsync(server, cts).ConfigureAwait(false);
            lock (Gate)
            {
                _status = ok ? "Stopped" : "Error";
                if (ok)
                {
                    ResetCountersUnlocked();
                }
            }

            NotifyChanged();
        });
    }

    public static bool IsToolEnabled(string name)
    {
        lock (Gate) return _enabledTools.Contains(name);
    }

    public static void SetToolEnabled(string name, bool enabled)
    {
        lock (Gate)
        {
            if (enabled)
            {
                _enabledTools.Add(name);
            }
            else
            {
                _enabledTools.Remove(name);
            }
        }

        RestartTransport();
    }

    public static void SetCategoryEnabled(IEnumerable<string> tools, bool enableAll)
    {
        lock (Gate)
        {
            foreach (var t in tools)
            {
                if (enableAll)
                {
                    _enabledTools.Add(t);
                }
                else
                {
                    _enabledTools.Remove(t);
                }
            }
        }

        RestartTransport();
    }

    public static void ReplaceEnabledTools(IEnumerable<string> tools)
    {
        lock (Gate)
        {
            _enabledTools = new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase);
        }

        RestartTransport();
    }

    /// <summary>Called from GH_AssemblyPriority when AutoStart is on.</summary>
    public static void TryAutoStart()
    {
        if (!CassisSettings.AutoStart)
        {
            RhinoApp.WriteLine("[Cassis] Auto-start disabled (enable via Cassis component menu).");
            return;
        }

        RhinoApp.WriteLine("[Cassis] Auto-starting MCP with all tools…");
        RequestStart(McpEndpoint, allTools: true);
    }

    private static async Task StartCoreAsync()
    {
        if (Interlocked.Exchange(ref _startInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            string prefix;
            HashSet<string> tools;
            lock (Gate)
            {
                prefix = _prefix;
                tools = new HashSet<string>(_enabledTools, StringComparer.OrdinalIgnoreCase);
            }

            RhinoApp.WriteLine("[MCP] Starting server...");
            string actualPrefix;
            try
            {
                actualPrefix = GetAvailablePort(prefix);
            }
            catch (Exception ex)
            {
                var report = McpStartupDiagnostics.WriteReport(ex, "Port check", prefix);
                RhinoApp.WriteLine($"[MCP ERROR] {ex.Message}");
                lock (Gate)
                {
                    _status = report.Written ? "Error (report written)" : "Error";
                    Messages.Enqueue(ex.Message);
                }

                NotifyChanged();
                return;
            }

            RhinoApp.WriteLine($"[MCP] Prefix: {actualPrefix}");

            CassisHost server;
            CancellationTokenSource cts;
            try
            {
                server = new CassisHost(actualPrefix, tools);
                cts = new CancellationTokenSource();
            }
            catch (Exception ex)
            {
                var report = McpStartupDiagnostics.WriteReport(ex, "Create CassisHost", prefix);
                RhinoApp.WriteLine($"[MCP ERROR] Failed to create server: {ex.Message}");
                lock (Gate)
                {
                    _status = report.Written ? "Error (report written)" : "Error";
                    Messages.Enqueue(
                        report.Written
                            ? $"MCP startup report: {report.Detail}"
                            : $"MCP startup report unavailable: {report.Detail}");
                }

                NotifyChanged();
                return;
            }

            server.MessageReceived += OnMessage;
            lock (Gate)
            {
                _server = server;
                _cts = cts;
                _prefix = actualPrefix;
                _serverStartTime = DateTime.UtcNow;
                _status = "Starting";
                _clientInitialized = false;
            }

            NotifyChanged();

            try
            {
                await server.StartAsync(cts.Token).ConfigureAwait(false);
                lock (Gate)
                {
                    if (ReferenceEquals(_server, server))
                    {
                        _status = "Running";
                        _serverStartTime = DateTime.UtcNow;
                    }
                }

                NotifyChanged();
                _ = ObserveServerCompletionAsync(server, cts, actualPrefix);
            }
            catch (Exception ex)
            {
                var report = McpStartupDiagnostics.WriteReport(ex, "StartAsync", actualPrefix);
                RhinoApp.WriteLine($"[MCP ERROR] Failed to start server: {ex.Message}");
                var owns = false;
                lock (Gate)
                {
                    if (ReferenceEquals(_server, server))
                    {
                        owns = true;
                        _server = null;
                        _cts = null;
                        _status = report.Written ? "Error (report written)" : "Error";
                        Messages.Enqueue(
                            report.Written
                                ? $"MCP startup report: {report.Detail}"
                                : $"MCP startup report unavailable: {report.Detail}");
                    }
                }

                if (owns)
                {
                    await DisposeServerAsync(server, cts).ConfigureAwait(false);
                }

                NotifyChanged();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _startInFlight, 0);
        }
    }

    private static void RestartTransport()
    {
        if (Interlocked.Exchange(ref _transportRestartInProgress, 1) == 1)
        {
            RhinoApp.WriteLine("[MCP INFO] Transport restart already in progress; skipping.");
            return;
        }

        CassisHost? server;
        CancellationTokenSource? cts;
        var shouldRestart = false;
        lock (Gate)
        {
            server = _server;
            cts = _cts;
            _server = null;
            _cts = null;
            if (server != null ||
                string.Equals(_status, "Running", StringComparison.Ordinal) ||
                string.Equals(_status, "Starting", StringComparison.Ordinal))
            {
                shouldRestart = true;
                _status = "Restarting";
            }
        }

        if (!shouldRestart)
        {
            Interlocked.Exchange(ref _transportRestartInProgress, 0);
            NotifyChanged();
            return;
        }

        NotifyChanged();
        Task.Run(async () =>
        {
            try
            {
                RhinoApp.WriteLine("[MCP INFO] Restarting transport after tool configuration change.");
                await DisposeServerAsync(server, cts).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _transportRestartInProgress, 0);
                lock (Gate)
                {
                    _status = "Starting";
                }

                NotifyChanged();
                await StartCoreAsync().ConfigureAwait(false);
            }
        });
    }

    private static async Task ObserveServerCompletionAsync(
        CassisHost server,
        CancellationTokenSource cts,
        string prefix)
    {
        Exception? failure = null;
        try
        {
            await server.Completion.ConfigureAwait(false);
            if (!cts.IsCancellationRequested)
            {
                failure = new InvalidOperationException("The MCP listener stopped without a shutdown request.");
            }
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            failure = ex;
        }

        if (failure is null)
        {
            return;
        }

        var report = McpStartupDiagnostics.WriteReport(failure, "Listener lifetime", prefix);
        RhinoApp.WriteLine($"[MCP ERROR] Listener stopped unexpectedly: {failure}");
        lock (Gate)
        {
            if (!ReferenceEquals(_server, server))
            {
                return;
            }

            _server = null;
            _cts = null;
            _status = report.Written ? "Error (report written)" : "Error";
            Messages.Enqueue(
                report.Written
                    ? $"MCP listener report: {report.Detail}"
                    : $"MCP listener report unavailable: {report.Detail}");
        }

        await DisposeServerAsync(server, cts).ConfigureAwait(false);
        NotifyChanged();
    }

    private static void OnMessage(JsonRpcMessage msg)
    {
        var ts = DateTime.UtcNow;
        var refresh = false;
        string? toolNameFromRequest = null;
        var looksLikeToolResponse = false;
        var clientInitialized = msg is JsonRpcNotification notification &&
                                notification.Method == "notifications/initialized";

        if (clientInitialized)
        {
            refresh = true;
        }
        else if (msg is JsonRpcRequest req && req.Method == "tools/call")
        {
            refresh = true;
            try
            {
                var p = JsonSerializer.Serialize(req.Params);
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(p);
                if (dict != null && dict.TryGetValue("name", out var n))
                {
                    toolNameFromRequest = n?.ToString() ?? "Unknown Tool";
                }
            }
            catch
            {
                toolNameFromRequest = "Unknown Tool";
            }
        }
        else if (msg is JsonRpcResponse resp && resp.Result != null)
        {
            try
            {
                var resultJson = JsonSerializer.Serialize(resp.Result);
                var resultDict = JsonSerializer.Deserialize<Dictionary<string, object>>(resultJson);
                if (resultDict != null &&
                    (resultDict.ContainsKey("content") ||
                     resultDict.ContainsKey("success") ||
                     resultDict.ContainsKey("result") ||
                     resultDict.ContainsKey("data")))
                {
                    looksLikeToolResponse = true;
                    refresh = true;
                }
            }
            catch
            {
                // ignore parse noise
            }
        }

        lock (Gate)
        {
            _lastMessageTime = ts;
            _totalMessagesReceived++;
            _clientInitialized |= clientInitialized;
            if (toolNameFromRequest != null)
            {
                _lastToolCall = ts;
                _lastToolName = toolNameFromRequest;
            }
            else if (looksLikeToolResponse)
            {
                _lastToolCall = ts;
                if (string.IsNullOrEmpty(_lastToolName))
                {
                    _lastToolName = "Tool Response";
                }
            }
        }

        Messages.Enqueue(JsonSerializer.Serialize(msg));
        if (refresh)
        {
            NotifyChanged();
        }
    }

    private static void ResetCountersUnlocked()
    {
        while (Messages.TryDequeue(out _))
        {
        }

        _totalMessagesReceived = 0;
        _lastMessageTime = null;
        _lastToolCall = null;
        _lastToolName = string.Empty;
        _clientInitialized = false;
        _serverStartTime = null;
    }

    private static async Task<bool> DisposeServerAsync(CassisHost? server, CancellationTokenSource? cts)
    {
        var succeeded = true;
        try
        {
            cts?.Cancel();
        }
        catch (Exception ex)
        {
            succeeded = false;
            RhinoApp.WriteLine($"[MCP WARN] Error cancelling server: {ex}");
        }

        if (server is not null)
        {
            try
            {
                await server.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                succeeded = false;
                RhinoApp.WriteLine($"[MCP WARN] Error stopping server: {ex}");
            }

            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                succeeded = false;
                RhinoApp.WriteLine($"[MCP WARN] Error disposing server: {ex}");
            }
        }

        try
        {
            cts?.Dispose();
        }
        catch (Exception ex)
        {
            succeeded = false;
            RhinoApp.WriteLine($"[MCP WARN] Error disposing cancellation source: {ex}");
        }

        return succeeded;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var c in ip.GetActiveTcpConnections())
            {
                if (c.LocalEndPoint.Port == port) return false;
            }

            foreach (var l in ip.GetActiveTcpListeners())
            {
                if (l.Port == port) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Error checking port availability: {ex.Message}");
            return false;
        }
    }

    private static string GetAvailablePort(string originalPrefix)
    {
        try
        {
            var uri = new Uri(originalPrefix);
            var basePort = uri.Port;
            if (IsPortAvailable(basePort)) return originalPrefix;
            throw new InvalidOperationException($"Port {basePort} is already in use.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            RhinoApp.WriteLine($"[MCP WARN] Error resolving available port: {ex.Message}");
            return originalPrefix;
        }
    }

    private static void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[Cassis WARN] Status listener error: {ex.Message}");
        }
    }

    private static void NotifyChangedUnlocked() => NotifyChanged();
}
