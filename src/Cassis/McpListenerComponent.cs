using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Cassis.Properties;
using Cassis.UI;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Rhino;

namespace Cassis
{
    /// <summary>
    /// Canvas UI for the process-wide MCP runtime (start/stop, auto-start toggle, tool selection).
    /// </summary>
    public class McpListenerComponent : GH_Component
    {
        public const string McpEndpoint = CassisRuntime.McpEndpoint;
        public const string AgentSetupUrl = CassisRuntime.AgentSetupUrl;

        private HashSet<string> _enabledTools = new(ToolCategories.DefaultEnabled);
        private bool _subscribed;
        private DateTime _lastExpireSolution = DateTime.MinValue;
        private bool _expirePending;
        private readonly object _uiLock = new();

        public McpListenerComponent()
            : base(
                "Cassis",
                "Cassis",
                "MCP server control panel (auto-starts with Grasshopper unless disabled)",
                "Cassis",
                "MCP")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("Prefix", "P", "HTTP prefix", GH_ParamAccess.item, McpEndpoint);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("Messages", "M", "Received JSON-RPC messages", GH_ParamAccess.list);
        }

        public override void CreateAttributes()
        {
            m_attributes = new ToolPanelAttributes(this);
            EnsureSubscribed();
            SyncToolsFromRuntime();
            ExpireSolution(true);
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            EnsureSubscribed();
            SyncToolsFromRuntime();
        }

        /// <summary>
        /// After auto-start (all tools), reflect the live runtime selection in the component UI.
        /// </summary>
        private void SyncToolsFromRuntime()
        {
            var status = CassisRuntime.Status;
            if (string.Equals(status, "Stopped", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(status))
            {
                return;
            }

            _enabledTools = new HashSet<string>(CassisRuntime.EnabledTools, StringComparer.OrdinalIgnoreCase);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            Unsubscribe();
            base.RemovedFromDocument(document);
        }

        private void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }

            CassisRuntime.Changed += OnRuntimeChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            CassisRuntime.Changed -= OnRuntimeChanged;
            _subscribed = false;
        }

        private void OnRuntimeChanged()
        {
            ThrottledExpireSolution();
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            try
            {
                Message = string.Empty;
                string prefix = string.Empty;
                da.GetData(0, ref prefix);
                _ = prefix;
                da.SetDataList(0, CassisRuntime.SnapshotMessages());
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"MCP Component Error: {ex.Message}");
                da.SetDataList(0, new List<string> { $"Error: {ex.Message}" });
            }
        }

        private void ThrottledExpireSolution()
        {
            var expireNow = false;
            lock (_uiLock)
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

            System.Threading.Tasks.Task.Delay(500).ContinueWith(
                _ =>
                {
                    lock (_uiLock)
                    {
                        _expirePending = false;
                        _lastExpireSolution = DateTime.UtcNow;
                    }

                    QueueCanvasRefresh();
                },
                System.Threading.Tasks.TaskScheduler.Default);
        }

        private void QueueCanvasRefresh()
        {
            RhinoApp.InvokeOnUiThread((Action)(() => ExpireSolution(true)));
        }

        public string GetButtonText()
        {
            var status = CassisRuntime.Status;
            if (status.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                return "Restart Server";
            }

            return status switch
            {
                "Running" => "Stop Server",
                "Starting" => "Starting...",
                "Stopping" => "Stopping...",
                "Restarting" => "Restarting...",
                "Stopped" => "Start Server",
                _ => "Start Server"
            };
        }

        public string GetStatusLabel() => CassisRuntime.Status;

        public string GetUptimeShort() => CassisRuntime.GetUptimeShort();

        public string GetMessageCountString() => CassisRuntime.GetMessageCountString();

        public string GetLastMsgShort() => CassisRuntime.GetLastMsgShort();

        public string GetLastToolShort() => CassisRuntime.GetLastToolShort();

        public bool ShouldShowAgentSetup() => ShouldShowAgentSetup(CassisRuntime.ClientInitialized);

        internal static bool ShouldShowAgentSetup(bool clientInitialized) => !clientInitialized;

        public void CopyMcpUrl()
        {
            try
            {
                Eto.Forms.Clipboard.Instance.Text = McpEndpoint;
                RhinoApp.WriteLine($"[Cassis] Copied MCP URL: {McpEndpoint}");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Cassis WARN] Could not copy the MCP URL: {ex.Message}");
            }
        }

        public void OpenAgentSetup()
        {
            try
            {
                RhinoApp.RunScript($"_-OpenURL \"{AgentSetupUrl}\"", false);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Cassis WARN] Could not open agent setup: {ex.Message}");
            }
        }

        public void HandleButtonClick()
        {
            try
            {
                var status = CassisRuntime.Status;
                if (!CanToggleServer(status))
                {
                    return;
                }

                if (string.Equals(status, "Running", StringComparison.Ordinal))
                {
                    CassisRuntime.RequestStop();
                }
                else
                {
                    CassisRuntime.RequestStart(McpEndpoint, _enabledTools);
                }

                ExpireSolution(true);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Error handling button click: {ex.Message}");
            }
        }

        internal static bool CanToggleServer(string status)
        {
            return status != "Starting" &&
                   status != "Stopping" &&
                   status != "Restarting";
        }

        public int EnabledToolCount => _enabledTools.Count;

        public bool IsToolEnabled(string name) => _enabledTools.Contains(name);

        public void ToggleTool(string name)
        {
            RecordUndoEvent("Toggle MCP Tool");
            if (!_enabledTools.Remove(name))
            {
                _enabledTools.Add(name);
            }

            CassisRuntime.SetToolEnabled(name, _enabledTools.Contains(name));
            ExpireSolution(true);
        }

        public void ToggleCategory(string categoryName, string[] tools)
        {
            RecordUndoEvent("Toggle MCP Tool Category");
            var allEnabled = tools.All(t => _enabledTools.Contains(t));
            foreach (var t in tools)
            {
                if (allEnabled)
                {
                    _enabledTools.Remove(t);
                }
                else
                {
                    _enabledTools.Add(t);
                }
            }

            CassisRuntime.SetCategoryEnabled(tools, !allEnabled);
            ExpireSolution(true);
        }

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

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            GH_DocumentObject.Menu_AppendSeparator(menu);
            GH_DocumentObject.Menu_AppendItem(
                menu,
                "Auto-start MCP when Grasshopper loads",
                (s, e) =>
                {
                    CassisSettings.AutoStart = !CassisSettings.AutoStart;
                    RhinoApp.WriteLine(
                        CassisSettings.AutoStart
                            ? "[Cassis] Auto-start enabled (applies next time Grasshopper loads)."
                            : "[Cassis] Auto-start disabled (applies next time Grasshopper loads).");
                },
                true,
                CassisSettings.AutoStart);
            GH_DocumentObject.Menu_AppendItem(menu, "Open Agent Setup", (s, e) => OpenAgentSetup());
            GH_DocumentObject.Menu_AppendItem(menu, "Copy MCP URL", (s, e) => CopyMcpUrl());
            GH_DocumentObject.Menu_AppendSeparator(menu);
            foreach (var kvp in ToolCategories.Categories)
            {
                var category = kvp.Key;
                var tools = kvp.Value;
                var catItem = GH_DocumentObject.Menu_AppendItem(menu, category);
                foreach (var tool in tools)
                {
                    var toolName = tool;
                    var enabled = IsToolEnabled(tool);
                    GH_DocumentObject.Menu_AppendItem(
                        catItem.DropDown,
                        tool,
                        (s, e) => ToggleTool(toolName),
                        true,
                        enabled);
                }
            }
        }

        public override Guid ComponentGuid => new Guid("2F3EBD4C-3848-4C6A-9A60-9D59E5D6A5C8");

        protected override Bitmap Icon => Resources.cassis_icon;
    }
}
