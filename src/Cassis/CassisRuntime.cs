using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Cassis.Diagnostics;
using Cassis.Routing;
using Grasshopper.Kernel;
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
    private static CassisHost? _worker;
    private static CancellationTokenSource? _workerCts;
    private static int _workerPort;
    private static System.Threading.Timer? _heartbeat;
    private static bool _watching;
    private static int _publishing;
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
                return (_server != null || _worker != null) && string.Equals(_status, "Running", StringComparison.Ordinal);
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
        lock (Gate)
        {
            if (_server == null &&
                _worker == null &&
                !string.Equals(_status, "Starting", StringComparison.Ordinal) &&
                !string.Equals(_status, "Restarting", StringComparison.Ordinal))
            {
                _status = "Stopped";
                NotifyChangedUnlocked();
                return;
            }

            _status = "Stopping";
        }

        NotifyChanged();
        Task.Run(async () =>
        {
            var ok = await StopHostsAsync().ConfigureAwait(false);
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
            HashSet<string> tools;
            lock (Gate)
            {
                tools = new HashSet<string>(_enabledTools, StringComparer.OrdinalIgnoreCase);
            }

            RhinoApp.WriteLine("[MCP] Starting server...");
            try
            {
                await StartHostsAsync(tools).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var report = McpStartupDiagnostics.WriteReport(ex, "StartAsync", McpEndpoint);
                RhinoApp.WriteLine($"[MCP ERROR] Failed to start server: {ex.Message}");
                lock (Gate)
                {
                    _status = report.Written ? "Error (report written)" : "Error";
                    Messages.Enqueue(
                        report.Written
                            ? $"MCP startup report: {report.Detail}"
                            : $"MCP startup report unavailable: {report.Detail}");
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

        var shouldRestart = false;
        lock (Gate)
        {
            if (_server != null ||
                _worker != null ||
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
                await StopHostsAsync().ConfigureAwait(false);
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

        var isDoor = false;
        var isWorker = false;
        lock (Gate)
        {
            isDoor = ReferenceEquals(_server, server);
            isWorker = ReferenceEquals(_worker, server);
        }

        if (isDoor)
        {
            FrontDoorLock.Release(CassisSettings.SettingsDirectory, Process.GetCurrentProcess().Id);
            lock (Gate)
            {
                if (ReferenceEquals(_server, server))
                {
                    _server = null;
                    _cts = null;
                }
            }

            await DisposeServerAsync(server, cts).ConfigureAwait(false);
            RhinoApp.WriteLine($"[Cassis] Port 3003 listener stopped: {failure.Message}");
            NotifyChanged();
            return;
        }

        if (!isWorker)
        {
            return;
        }

        var report = McpStartupDiagnostics.WriteReport(failure, "Listener lifetime", prefix);
        RhinoApp.WriteLine($"[MCP ERROR] Listener stopped unexpectedly: {failure}");
        lock (Gate)
        {
            if (!ReferenceEquals(_worker, server))
            {
                return;
            }

            _worker = null;
            _workerCts = null;
            _workerPort = 0;
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
        // 2026-07-28 clients never send notifications/initialized; any request carrying the protocol version counts.
        var modernClient = msg is JsonRpcRequest { Params: JsonObject modernParams } &&
                           modernParams["_meta"] is JsonObject meta &&
                           meta.ContainsKey("io.modelcontextprotocol/protocolVersion");

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
            refresh |= modernClient && !_clientInitialized;
            _clientInitialized |= clientInitialized || modernClient;
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

    private static async Task StartHostsAsync(HashSet<string> tools)
    {
        var port = AllocateLoopbackPort();
        var workerPrefix = $"http://127.0.0.1:{port}/mcp/";
        var worker = new CassisHost(workerPrefix, tools);
        worker.UseDocumentFocus(CanvasFocus.RunAsync);
        var workerCts = new CancellationTokenSource();
        try
        {
            await worker.StartAsync(workerCts.Token).ConfigureAwait(false);
        }
        catch
        {
            await DisposeServerAsync(worker, workerCts).ConfigureAwait(false);
            throw;
        }

        worker.MessageReceived += OnMessage;
        lock (Gate)
        {
            _worker = worker;
            _workerCts = workerCts;
            _workerPort = port;
            _serverStartTime = DateTime.UtcNow;
            _status = "Running";
            _clientInitialized = false;
        }

        RhinoApp.WriteLine($"[MCP] Worker: {workerPrefix}");
        PublishInstance();
        WatchDocuments();
        StartHeartbeat();
        await TryStartDoorAsync(tools).ConfigureAwait(false);
        NotifyChanged();
        _ = ObserveServerCompletionAsync(worker, workerCts, workerPrefix);
    }

    private static int _doorAttempt;

    private static async Task TryStartDoorAsync(HashSet<string> tools)
    {
        if (Interlocked.Exchange(ref _doorAttempt, 1) == 1)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_server != null)
                {
                    return;
                }
            }

            if (!IsPortAvailable(3003))
            {
                RhinoApp.WriteLine("[Cassis] Port 3003 is already in use. This Rhino registers its files and waits.");
                return;
            }

            var pid = Process.GetCurrentProcess().Id;
            if (!FrontDoorLock.TryAcquire(CassisSettings.SettingsDirectory, pid, InstanceFiles.IsProcessAlive))
            {
                return;
            }

            var door = new CassisHost(McpEndpoint, tools);
            door.UseRouter(RouteAsync);
            var cts = new CancellationTokenSource();
            try
            {
                await door.StartAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FrontDoorLock.Release(CassisSettings.SettingsDirectory, pid);
                await DisposeServerAsync(door, cts).ConfigureAwait(false);
                RhinoApp.WriteLine($"[Cassis] Could not bind port 3003: {ex.Message}");
                return;
            }

            door.MessageReceived += OnMessage;
            lock (Gate)
            {
                _server = door;
                _cts = cts;
                _prefix = McpEndpoint;
            }

            RhinoApp.WriteLine($"[MCP] Prefix: {McpEndpoint}");
            _ = ObserveServerCompletionAsync(door, cts, McpEndpoint);
        }
        finally
        {
            Interlocked.Exchange(ref _doorAttempt, 0);
        }
    }

    private static async Task<string?> RouteAsync(JsonRpcRequest request, string? root, bool modern)
    {
        var instances = InstanceFiles.ReadLive(
            CassisSettings.SettingsDirectory,
            DateTime.UtcNow,
            InstanceFiles.IsProcessAlive,
            InstanceMatcher.StaleAfter);
        var match = InstanceMatcher.Match(root, instances);
        if (match.Kind != InstanceMatchKind.Ok || string.IsNullOrWhiteSpace(match.DocumentPath))
        {
            return WorkerCall.ErrorJson(request, match.Detail);
        }

        return await WorkerCall.ForwardAsync(match.Port, match.DocumentPath, request, modern, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static void PublishInstance()
    {
        if (Interlocked.Exchange(ref _publishing, 1) == 1)
        {
            return;
        }

        try
        {
            int port;
            lock (Gate)
            {
                port = _workerPort;
            }

            if (port == 0)
            {
                return;
            }

            InstanceSnapshot snapshot;
            if (!RhinoApp.InvokeRequired)
            {
                snapshot = CanvasFocus.Capture(Process.GetCurrentProcess().Id, port);
            }
            else
            {
                snapshot = Utilities.UiThreadHelper.InvokeAsync(
                    () => CanvasFocus.Capture(Process.GetCurrentProcess().Id, port)).GetAwaiter().GetResult();
            }
            InstanceFiles.Write(CassisSettings.SettingsDirectory, snapshot);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[Cassis WARN] Could not write the instance file: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _publishing, 0);
        }
    }

    private static void WatchDocuments()
    {
        if (_watching)
        {
            return;
        }

        var server = Grasshopper.Instances.DocumentServer;
        if (server == null)
        {
            return;
        }

        _watching = true;
        server.DocumentAdded += OnDocumentAdded;
        server.DocumentRemoved += OnDocumentRemoved;
        foreach (GH_Document document in server)
        {
            HookDocument(document);
        }
    }

    private static void OnDocumentAdded(GH_DocumentServer sender, GH_Document doc)
    {
        HookDocument(doc);
        PublishInstance();
    }

    private static void OnDocumentRemoved(GH_DocumentServer sender, GH_Document doc)
    {
        PublishInstance();
    }

    private static void HookDocument(GH_Document document)
    {
        document.FilePathChanged += OnFilePathChanged;
    }

    private static void OnFilePathChanged(object sender, GH_DocFilePathEventArgs args)
    {
        PublishInstance();
    }

    private static void StartHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    PublishInstance();
                    HashSet<string> tools;
                    var hasDoor = false;
                    lock (Gate)
                    {
                        hasDoor = _server != null;
                        tools = new HashSet<string>(_enabledTools, StringComparer.OrdinalIgnoreCase);
                    }

                    if (!hasDoor)
                    {
                        TryStartDoorAsync(tools).GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"[Cassis WARN] Instance heartbeat failed: {ex.Message}");
                }
            },
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> StopHostsAsync()
    {
        System.Threading.Timer? timer;
        CassisHost? door;
        CancellationTokenSource? doorCts;
        CassisHost? worker;
        CancellationTokenSource? workerCts;
        lock (Gate)
        {
            timer = _heartbeat;
            _heartbeat = null;
            door = _server;
            doorCts = _cts;
            worker = _worker;
            workerCts = _workerCts;
            _server = null;
            _cts = null;
            _worker = null;
            _workerCts = null;
            _workerPort = 0;
        }

        timer?.Dispose();
        var doorOk = await DisposeServerAsync(door, doorCts).ConfigureAwait(false);
        var workerOk = await DisposeServerAsync(worker, workerCts).ConfigureAwait(false);
        var pid = Process.GetCurrentProcess().Id;
        InstanceFiles.Delete(CassisSettings.SettingsDirectory, pid);
        FrontDoorLock.Release(CassisSettings.SettingsDirectory, pid);
        return doorOk && workerOk;
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
