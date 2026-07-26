using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using System.Drawing.Drawing2D;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using GrasshopperMCP.Properties;
using Rhino;
using System.Net.NetworkInformation;
using Grasshopper.Kernel.Types;
using System.Windows.Forms;
using GrasshopperMCP.UI;
using System.IO;
using System.Reflection;
using System.Linq;
#if NET6_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace GrasshopperMCP
{
    /// <summary>
    /// New /dark UI variant of the MCP listener.
    /// The original component remains unchanged.
    /// </summary>
    public class McpListenerComponent : GH_Component
    {
        // === (same fields as original component) ===
        private GrasshopperMcpHost? _server;
        private CancellationTokenSource? _cts;
        private readonly List<string> _logs = new();
        private readonly List<string> _messages = new();

        private DateTime? _serverStartTime;
        private DateTime? _lastMessageTime;
        private DateTime? _lastConnectionTime;
        private int _totalMessagesReceived;
        private int _totalConnections;

        private string _currentStatus = "Stopped";
        private readonly object _statusLock = new object();
        private readonly List<string> _connectionLog = new();
        private DateTime? _lastToolCall;
        private string _lastToolName = string.Empty;
        private DateTime _lastExpireSolution = DateTime.MinValue;
        private bool _expirePending;
        private int _transportRestartInProgress;

        private bool _shouldBeRunning = false;
        private bool _debugMode = false;

#if NET6_0_OR_GREATER
        static McpListenerComponent()
        {
            // Prefer the plugin-local System.Text.Json to pick up schema types newer than Rhino's runtime carries.
            try
            {
                EnsurePreferredJsonLoaded();
                AssemblyLoadContext.Default.Resolving += ResolveAssemblyFromPluginDirectory;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Failed to register assembly resolver: {ex.Message}");
            }
        }

        private static void EnsurePreferredJsonLoaded()
        {
            try
            {
                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
                if (string.IsNullOrEmpty(pluginDirectory)) return;

                var candidatePath = Path.Combine(pluginDirectory, "System.Text.Json.dll");
                if (!File.Exists(candidatePath)) return;

                var candidateName = AssemblyName.GetAssemblyName(candidatePath);
                var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "System.Text.Json", StringComparison.OrdinalIgnoreCase));

                if (alreadyLoaded != null && alreadyLoaded.GetName().Version >= candidateName.Version) return;

                AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
                RhinoApp.WriteLine($"[MCP] Preferring System.Text.Json {candidateName.Version} from plugin folder.");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Failed to preload System.Text.Json: {ex.Message}");
            }
        }

        private static Assembly? ResolveAssemblyFromPluginDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            try
            {
                if (!string.Equals(assemblyName.Name, "System.Text.Json", StringComparison.OrdinalIgnoreCase)) return null;

                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
                if (string.IsNullOrEmpty(pluginDirectory)) return null;

                var candidatePath = Path.Combine(pluginDirectory, "System.Text.Json.dll");
                if (!File.Exists(candidatePath)) return null;

                var candidateName = AssemblyName.GetAssemblyName(candidatePath);
                if (candidateName.Version < assemblyName.Version) return null;

                var resolved = context.LoadFromAssemblyPath(candidatePath);
                RhinoApp.WriteLine($"[MCP] Resolved System.Text.Json {candidateName.Version} from plugin folder.");
                return resolved;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Assembly resolve failed for {assemblyName.FullName}: {ex.Message}");
                return null;
            }
        }
#endif

        public McpListenerComponent() : base(
            "Cassis",
            "Cassis",
            "Start MCP server",
            "Cassis",
            "MCP")
        {
        }

        // === I/O ===
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Prefix", "P", "HTTP prefix", GH_ParamAccess.item, "http://localhost:3003/mcp/");
        }
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Messages", "M", "Received JSON-RPC messages", GH_ParamAccess.list);
        }

        // === UI () ===
        public override void CreateAttributes()
        {
            m_attributes = new GrasshopperMCP.UI.ToolPanelAttributes(this);
            ExpireSolution(true);
        }

        // === Solve/Server lifecycle (same behavior as original) ===
        protected override void SolveInstance(IGH_DataAccess da)
        {
            try
            {
                Message = "";

                string prefix = "";
                if (!da.GetData(0, ref prefix)) return;

                if (_shouldBeRunning && _server == null)
                {
                    try
                    {
                        Rhino.RhinoApp.WriteLine("[MCP] Starting server...");
                        var actualPrefix = GetAvailablePort(prefix);
                        Rhino.RhinoApp.WriteLine($"[MCP] Prefix: {actualPrefix}");

                        Rhino.RhinoApp.WriteLine("[MCP] Creating GrasshopperMcpHost...");
                        _server = new GrasshopperMcpHost(actualPrefix, _enabledTools);
                        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost created successfully");
                        _server.MessageReceived += OnMessage;
                        _cts = new CancellationTokenSource();

                        Rhino.RhinoApp.WriteLine("[MCP] Starting Task.Run for StartAsync...");
                        Task.Run(async () =>
                        {
                            try
                            {
                                Rhino.RhinoApp.WriteLine("[MCP] Calling StartAsync...");
                                await _server.StartAsync(_cts.Token);
                                Rhino.RhinoApp.WriteLine("[MCP] StartAsync completed successfully!");

                                lock (_statusLock)
                                {
                                    _currentStatus = "Running";
                                    _serverStartTime = DateTime.UtcNow;
                                }

                                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                                {
                                    ExpireSolution(true);
                                }));
                            }
                            catch (Exception ex)
                            {
                                Rhino.RhinoApp.WriteLine($"[MCP ERROR] Failed to start server: {ex.Message}");
                                Rhino.RhinoApp.WriteLine($"[MCP ERROR] {ex.StackTrace}");
                                lock (_statusLock)
                                {
                                    _currentStatus = $"Error: {ex.Message}";
                                }
                                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => ExpireSolution(true)));
                            }
                        });
                        lock (_statusLock)
                        {
                            _serverStartTime = DateTime.UtcNow;
                            _currentStatus = "Starting";
                            _totalConnections = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_statusLock) _currentStatus = $"Error: {ex.Message}";
                        SafeDisposeServer();
                    }
                }
                else if (!_shouldBeRunning && _server != null)
                {
                    try
                    {
                        var s = _server;
                        var cts = _cts;
                        _server = null;
                        _cts = null;

                        lock (_statusLock) _currentStatus = "Stopping";

                        Task.Run(async () =>
                        {
                            try
                            {
                                cts?.Cancel();
                                await s.StopAsync().ConfigureAwait(false);
                                await s.DisposeAsync().ConfigureAwait(false);
                                cts?.Dispose();

                                lock (_statusLock) _currentStatus = "Stopped";
                                ResetCounters();
                            }
                            catch (Exception ex)
                            {
                                Rhino.RhinoApp.WriteLine($"[MCP WARN] Error stopping server: {ex.Message}");
                                lock (_statusLock) _currentStatus = "Error";
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Rhino.RhinoApp.WriteLine($"[MCP WARN] Error during server shutdown: {ex.Message}");
                        lock (_statusLock) _currentStatus = "Error";
                        _server = null;
                        _cts?.Dispose();
                        _cts = null;
                        ResetCounters();
                    }
                }

                da.SetDataList(0, _messages);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"MCP Component Error: {ex.Message}");
                _shouldBeRunning = false;
                lock (_statusLock) _currentStatus = "Error";
                da.SetDataList(0, new List<string> { $"Error: {ex.Message}" });
            }
        }

        private void ResetCounters()
        {
            _messages.Clear();
            _logs.Clear();
            _connectionLog.Clear();
            _totalMessagesReceived = 0;
            _totalConnections = 0;
            _lastMessageTime = null;
            _lastConnectionTime = null;
        }

        private void SafeDisposeServer()
        {
            var server = _server;
            var cts = _cts;
            _server = null;
            _cts = null;

            Task.Run(async () =>
            {
                try
                {
                    if (server is not null)
                    {
                        await server.StopAsync().ConfigureAwait(false);
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                    cts?.Dispose();
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine($"[MCP WARN] Error disposing server: {ex.Message}");
                }
            });
        }

        // === Networking utils (same idea as original) ===
        private static bool IsPortAvailable(int port)
        {
            try
            {
                var ip = IPGlobalProperties.GetIPGlobalProperties();
                foreach (var c in ip.GetActiveTcpConnections()) if (c.LocalEndPoint.Port == port) return false;
                foreach (var l in ip.GetActiveTcpListeners()) if (l.Port == port) return false;
                return true;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine($"[MCP WARN] Error checking port availability: {ex.Message}");
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
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine($"[MCP WARN] Error resolving available port: {ex.Message}");
                return originalPrefix;
            }
        }

        // === Message hook (enhanced tool call tracking) ===
        private void OnMessage(JsonRpcMessage msg)
        {
            var ts = DateTime.UtcNow;
            var refreshListenerUi = false;
            string? toolNameFromRequest = null;
            var looksLikeToolResponse = false;

            if (msg is JsonRpcRequest req && req.Method == "tools/call")
            {
                refreshListenerUi = true;
                try
                {
                    var p = JsonSerializer.Serialize(req.Params);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(p);
                    if (dict != null && dict.TryGetValue("name", out var n))
                    {
                        toolNameFromRequest = n?.ToString() ?? "Unknown Tool";
                    }
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine($"[MCP WARN] Error parsing tool call name: {ex.Message}");
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
                        refreshListenerUi = true;
                    }
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine($"[MCP WARN] Error parsing tool response: {ex.Message}");
                }
            }

            lock (_statusLock)
            {
                _lastMessageTime = ts;
                _totalMessagesReceived++;

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

            _messages.Add(JsonSerializer.Serialize(msg));

            if (refreshListenerUi)
            {
                ThrottledExpireSolution();
            }
        }

        /// <summary>
        /// Throttles ExpireSolution calls to at most once per 500ms with a trailing edge
        /// to ensure the final message in a burst still triggers a UI update.
        /// </summary>
        private void ThrottledExpireSolution()
        {
            var expireNow = false;
            lock (_statusLock)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastExpireSolution).TotalMilliseconds >= 500)
                {
                    _lastExpireSolution = now;
                    _expirePending = false;
                    expireNow = true;
                }
                else if (_expirePending)
                {
                    return;
                }
                else
                {
                    _expirePending = true;
                }
            }

            if (expireNow)
            {
                QueueCanvasRefresh();
                return;
            }

            Task.Delay(500).ContinueWith(
                _ =>
                {
                    lock (_statusLock)
                    {
                        _expirePending = false;
                        _lastExpireSolution = DateTime.UtcNow;
                    }

                    QueueCanvasRefresh();
                },
                TaskScheduler.Default);
        }

        /// <summary>
        /// Refreshes the listener canvas. Must not be called while holding <see cref="_statusLock"/>.
        /// </summary>
        private void QueueCanvasRefresh()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => ExpireSolution(true)));
        }

        // === UI contract for ServerAttributes / ToolPanelAttributes ===
        public string GetButtonText()
        {
            lock (_statusLock)
            {
                return _currentStatus switch
                {
                    "Running" => "Stop Server",
                    "Starting" => "Starting...",
                    "Stopping" => "Stopping...",
                    "Stopped" => "Start Server",
                    "Error" => "Restart Server",
                    _ => "Start Server"
                };
            }
        }

        public string GetStatusLabel()
        {
            lock (_statusLock) return _currentStatus;
        }
        public string GetUptimeShort()
        {
            lock (_statusLock)
            {
                if (_currentStatus == "Stopped" || !_serverStartTime.HasValue) return "";
                var d = DateTime.UtcNow - _serverStartTime.Value;
                if (d.TotalDays >= 1) return $"{d.Days}d {d.Hours}h";
                if (d.TotalHours >= 1) return $"{d.Hours}h {d.Minutes}m";
                if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
                return $"{d.TotalSeconds:F0}s";
            }
        }
        public string GetMessageCountString()
        {
            lock (_statusLock) return _totalMessagesReceived.ToString();
        }
        public string GetLastMsgShort()
        {
            lock (_statusLock)
            {
                if (!_lastMessageTime.HasValue) return "—";
                var d = DateTime.UtcNow - _lastMessageTime.Value;
                if (d.TotalMinutes < 1) return $"{d.TotalSeconds:F0}s ago";
                if (d.TotalHours < 1) return $"{d.TotalMinutes:F0}m ago";
                return $"{d.TotalHours:F1}h ago";
            }
        }
        public string GetLastToolShort()
        {
            lock (_statusLock)
            {
                if (_lastToolCall == null || string.IsNullOrEmpty(_lastToolName)) return "—";
                var d = DateTime.UtcNow - _lastToolCall.Value;
                string when = d.TotalMinutes < 1 ? $"{d.TotalSeconds:F0}s ago" :
                              d.TotalHours < 1 ? $"{d.TotalMinutes:F0}m ago" :
                              $"{d.TotalHours:F1}h ago";
                return $"{_lastToolName} • {when}";
            }
        }

        public void HandleButtonClick()
        {
            try
            {
                lock (_statusLock)
                {
                    _shouldBeRunning = _currentStatus == "Running" ? false : true;
                    _currentStatus = _shouldBeRunning ? "Starting" : "Stopping";
                }
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() => { ExpireSolution(true); }));
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine($"[MCP WARN] Error handling button click: {ex.Message}");
                lock (_statusLock) _currentStatus = "Error";
            }
}

        // === Tool enable/disable ===
        private HashSet<string> _enabledTools = new(ToolCategories.DefaultEnabled);

        public int EnabledToolCount => _enabledTools.Count;

        public bool IsToolEnabled(string name) => _enabledTools.Contains(name);

        public void ToggleTool(string name)
        {
            RecordUndoEvent("Toggle MCP Tool");
            if (!_enabledTools.Remove(name))
                _enabledTools.Add(name);
            RestartTransport();
        }

        public void ToggleCategory(string categoryName, string[] tools)
        {
            RecordUndoEvent("Toggle MCP Tool Category");
            bool allEnabled = tools.All(t => _enabledTools.Contains(t));
            foreach (var t in tools)
            {
                if (allEnabled)
                    _enabledTools.Remove(t);
                else
                    _enabledTools.Add(t);
            }
            RestartTransport();
        }

        private void RestartTransport()
        {
            if (Interlocked.Exchange(ref _transportRestartInProgress, 1) == 1)
            {
                Rhino.RhinoApp.WriteLine("[MCP INFO] Transport restart already in progress; skipping duplicate restart request.");
                return;
            }

            var server = _server;
            var cts = _cts;
            _server = null;
            _cts = null;

            lock (_statusLock)
            {
                if (_shouldBeRunning)
                {
                    _currentStatus = "Restarting";
                }
            }

            Task.Run(async () =>
            {
                try
                {
                    Rhino.RhinoApp.WriteLine("[MCP INFO] Restarting transport after tool configuration change.");
                    cts?.Cancel();
                    if (server is not null)
                    {
                        await server.StopAsync().ConfigureAwait(false);
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                    cts?.Dispose();
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine($"[MCP WARN] Error during transport restart cleanup: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _transportRestartInProgress, 0);
                    if (_shouldBeRunning)
                    {
                        Rhino.RhinoApp.InvokeOnUiThread((Action)(() => ExpireSolution(true)));
                    }
                }
            });
        }

        // === GH serialization ===
        public override bool Write(GH_IWriter writer)
        {
            base.Write(writer);
            writer.SetString("EnabledTools", string.Join(",", _enabledTools));
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            base.Read(reader);
            if (reader.ItemExists("EnabledTools"))
            {
                var csv = reader.GetString("EnabledTools");
                var allKnown = new HashSet<string>(ToolCategories.AllTools());
                _enabledTools = new HashSet<string>(
                    csv.Split(',').Where(t => allKnown.Contains(t)));
            }
            return true;
        }

        // === Right-click menu ===
        protected override void AppendAdditionalComponentMenuItems(System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            foreach (var kvp in ToolCategories.Categories)
            {
                var category = kvp.Key;
                var tools = kvp.Value;
                var catItem = GH_DocumentObject.Menu_AppendItem(menu, category);
                foreach (var tool in tools)
                {
                    bool enabled = IsToolEnabled(tool);
                    var toolName = tool;
                    GH_DocumentObject.Menu_AppendItem(catItem.DropDown, tool, (s, e) => ToggleTool(toolName), true, enabled);
                }
            }
        }

        // GH IDs
        public override Guid ComponentGuid => new Guid("2F3EBD4C-3848-4C6A-9A60-9D59E5D6A5C8");
        protected override Bitmap Icon => Resources.cassis_icon;
    }
}
