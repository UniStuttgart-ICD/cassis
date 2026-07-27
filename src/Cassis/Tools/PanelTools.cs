using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Cassis.Extensions;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace Cassis.Tools;

/// <summary>
/// Tools for inspecting and editing Grasshopper panel components.
/// </summary>
[McpServerToolType]
public static class PanelTools
{
    private sealed record PanelSnapshot(
        bool IsConnected,
        List<string> DisplayLines,
        List<object> Branches,
        int TotalItems);

    [McpServerTool(Name = "List_Panels")]
    [Description("Lists Grasshopper panels in the active document with position metadata")]
    public static async Task<CallToolResult> ListPanels()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var panels = await UiThreadHelper.InvokeAsync(() =>
            {
                var result = new List<object>();
                var document = Instances.ActiveCanvas?.Document;

                if (document == null)
                {
                    return result;
                }

                foreach (var obj in document.Objects)
                {
                    if (obj is GH_Panel panel)
                    {
                        result.Add(ToPanelDescriptor(panel));
                        continue;
                    }

                    var typeName = obj?.GetType().FullName ?? string.Empty;
                    if (typeName.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Add(new
                        {
                            id = obj.InstanceGuid.ToString(),
                            name = obj.Name ?? "Panel",
                            nickName = obj.NickName ?? string.Empty,
                            x = obj.Attributes?.Pivot.X ?? 0f,
                            y = obj.Attributes?.Pivot.Y ?? 0f
                        });
                    }
                }

                return result;
            });

            return new
            {
                success = true,
                count = panels.Count,
                panels
            };
        }, nameof(ListPanels));
    }

    [McpServerTool(Name = "Get_Panel_Text")]
    [Description("Gets the content of a Grasshopper panel. Use format='structured' for machine-readable " +
        "JSON with branches/paths/items, or format='raw' (default) for the full multiline string.")]
    public static async Task<CallToolResult> GetPanelText(
        [Description("Component GUID of the panel")] string componentId,
        [Description("Output format: 'raw' (default) or 'structured' (JSON with branches/items/metadata)")] string format = "raw")
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));
            var normalizedFormat = NormalizePanelFormat(format);

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var panel = FindPanel(componentId);
                var snapshot = BuildPanelSnapshot(panel);

                if (normalizedFormat == "raw")
                {
                    var text = NormalizeLineEndings(string.Join("\n", snapshot.DisplayLines));
                    return new
                    {
                        success = true,
                        componentId,
                        format = "raw",
                        isConnected = snapshot.IsConnected,
                        lineCount = snapshot.DisplayLines.Count,
                        text
                    };
                }

                return (object)new
                {
                    success = true,
                    componentId,
                    format = "structured",
                    isConnected = snapshot.IsConnected,
                    totalItems = snapshot.TotalItems,
                    branchCount = snapshot.Branches.Count,
                    branches = snapshot.Branches
                };
            });

            return result;
        }, nameof(GetPanelText));
    }

    [McpServerTool(Name = "Set_Panel_Text")]
    [Description("Sets content on a disconnected Grasshopper panel. Provide either 'text' (single string) or 'items' (string array, " +
        "each item becomes a line). Do not provide both.")]
    public static async Task<CallToolResult> SetPanelText(
        [Description("Component GUID of the panel")] string componentId,
        [Description("Text content to set (mutually exclusive with items)")] string? text = null,
        [Description("List of items to set as multiline panel data (mutually exclusive with text)")] string[]? items = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            if (text != null && items != null)
            {
                throw new ArgumentException("Provide either 'text' or 'items', not both");
            }

            if (text == null && items == null)
            {
                throw new ArgumentException("Provide either 'text' or 'items'");
            }

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var panel = FindPanel(componentId);
                var document = Instances.ActiveCanvas?.Document
                    ?? throw new InvalidOperationException("No active Grasshopper document");

                if (panel.SourceCount > 0)
                {
                    throw new InvalidOperationException("Cannot set text on a connected panel. Disconnect it first.");
                }

                string mode;
                string content;
                int itemCount;

                if (items != null)
                {
                    mode = "items";
                    content = NormalizeLineEndings(string.Join(Environment.NewLine, items));
                    itemCount = items.Length;
                }
                else
                {
                    mode = "text";
                    content = NormalizeLineEndings(text!);
                    itemCount = content.Split('\n').Length;
                }

                panel.SetUserText(content);
                panel.ExpireSolution(recompute: true);
                document.NewSolution(false);

                return new
                {
                    success = true,
                    componentId,
                    mode,
                    itemCount,
                    text = content
                };
            });

            return result;
        }, nameof(SetPanelText));
    }

    private static object ToPanelDescriptor(GH_Panel panel)
    {
        var snapshot = BuildPanelSnapshot(panel);
        var value = string.Join("\n", snapshot.DisplayLines);

        return new
        {
            id = panel.InstanceGuid.ToString(),
            name = panel.Name ?? "Panel",
            nickName = panel.NickName ?? string.Empty,
            x = panel.Attributes?.Pivot.X ?? 0f,
            y = panel.Attributes?.Pivot.Y ?? 0f,
            text = value
        };
    }

    private static PanelSnapshot BuildPanelSnapshot(GH_Panel panel)
    {
        if (panel.SourceCount > 0)
        {
            var volatileData = panel.VolatileData;
            if (volatileData != null && !volatileData.IsEmpty)
            {
                return BuildSnapshotFromVolatileData(volatileData);
            }

            return new PanelSnapshot(true, new List<string>(), new List<object>(), 0);
        }

        return BuildSnapshotFromUserText(panel.UserText ?? string.Empty);
    }

    private static PanelSnapshot BuildSnapshotFromVolatileData(Grasshopper.Kernel.Data.IGH_Structure data)
    {
        var displayLines = new List<string>();
        var branches = new List<object>();
        var totalItems = 0;
        var paths = data.Paths;
        var isMultiBranch = paths.Count > 1;

        foreach (var path in paths)
        {
            var branch = data.get_Branch(path);
            if (branch == null) continue;

            var branchItems = new List<string>();
            for (int i = 0; i < branch.Count; i++)
            {
                var item = branch[i];
                var itemText = item switch
                {
                    GH_String str => str.Value ?? string.Empty,
                    IGH_Goo goo => goo?.ToString() ?? string.Empty,
                    _ => item?.ToString() ?? string.Empty
                };
                branchItems.Add(itemText);
            }

            totalItems += branchItems.Count;
            branches.Add(new { path = path.ToString(), items = branchItems });

            if (isMultiBranch)
            {
                displayLines.Add($"{path}");
                foreach (var item in branchItems)
                {
                    displayLines.Add($"  {item}");
                }
            }
            else
            {
                displayLines.AddRange(branchItems);
            }
        }

        return new PanelSnapshot(true, displayLines, branches, totalItems);
    }

    private static PanelSnapshot BuildSnapshotFromUserText(string text)
    {
        var normalized = NormalizeLineEndings(text);
        var lines = normalized.Split('\n').ToList();
        var branches = new List<object>
        {
            new { path = "{0}", items = lines }
        };

        return new PanelSnapshot(false, lines, branches, lines.Count);
    }

    private static GH_Panel FindPanel(string componentId)
    {
        var document = Instances.ActiveCanvas?.Document
            ?? throw new InvalidOperationException("No active Grasshopper document");

        if (!Guid.TryParse(componentId, out var guid))
        {
            throw new ArgumentException("Invalid panel component GUID format");
        }

        var obj = document.FindObject(guid, true);
        if (obj is not GH_Panel panel)
        {
            throw new ArgumentException($"Component {componentId} is not a panel or was not found");
        }

        return panel;
    }

    private static string NormalizePanelFormat(string format)
    {
        var normalized = format?.Trim().ToLowerInvariant() ?? "raw";
        if (normalized != "raw" && normalized != "structured")
        {
            throw new ArgumentException($"Invalid format '{format}'. Use 'raw' or 'structured'.");
        }

        return normalized;
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n");
    }
}
