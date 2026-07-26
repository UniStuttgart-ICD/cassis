using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using GrasshopperMCP.Extensions;
using GrasshopperMCP.Services;
using GrasshopperMCP.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace GrasshopperMCP.Tools;

/// <summary>
/// Tools for Grasshopper document IO and recomputation.
/// </summary>
[McpServerToolType]
public static class DocumentTools
{
    // Targeting rule stated once (GetDocumentInfo). Other tools stay short.

    [McpServerTool(Name = "LoadDocument")]
    [Description("Open .gh/.ghx as active canvas. Returns {ok,file,n,ctx}. Use Canvas_Snapshot for contents.")]
    public static async Task<CallToolResult> LoadDocument(
        IGrasshopperDocumentService documentService,
        [Description("Absolute .gh/.ghx path")]
        string filePath)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(documentService), documentService),
                (nameof(filePath), filePath));

            var result = await documentService.LoadDocumentAsync(filePath);
            if (!result.Success)
            {
                return new { ok = false, err = result.ErrorMessage ?? result.Message };
            }

            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = Instances.ActiveCanvas?.Document;
                return new
                {
                    ok = true,
                    file = DocumentContextHelper.DocLabel(doc) ?? Path.GetFileName(result.Path),
                    n = doc?.ObjectCount,
                    ctx = DocumentContextHelper.BuildContext()
                };
            });
        }, nameof(LoadDocument));
    }

    [McpServerTool(Name = "SaveDocument")]
    [Description("Save active doc. Optional filePath = Save As (.gh/.ghx).")]
    public static async Task<CallToolResult> SaveDocument(
        IGrasshopperDocumentService documentService,
        [Description("Optional Save As path")]
        string? filePath = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(documentService), documentService));
            var result = await documentService.SaveDocumentAsync(filePath);
            return result.Success
                ? (object)new { ok = true, file = string.IsNullOrEmpty(result.Path) ? null : Path.GetFileName(result.Path) }
                : new { ok = false, err = result.ErrorMessage ?? result.Message };
        }, nameof(SaveDocument));
    }

    [McpServerTool(Name = "CloseDocument")]
    [Description("Close active doc (discards unsaved unless saveFirst). Closing Cassis host stops MCP.")]
    public static async Task<CallToolResult> CloseDocument(
        IGrasshopperDocumentService documentService,
        [Description("Save before close")] bool saveFirst = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(documentService), documentService));

            var closingHost = await UiThreadHelper.InvokeAsync(DocumentContextHelper.ActiveDocumentHostsCassis);
            var result = await documentService.CloseDocumentAsync(saveFirst);
            if (!result.Success)
            {
                return new { ok = false, err = result.ErrorMessage ?? result.Message };
            }

            var ctx = await UiThreadHelper.InvokeAsync(DocumentContextHelper.BuildContext);
            return closingHost
                ? (object)new { ok = true, file = Path.GetFileName(result.Path), killedCassis = true, ctx }
                : new { ok = true, file = Path.GetFileName(result.Path), ctx };
        }, nameof(CloseDocument));
    }

    [McpServerTool(Name = "NewDocument")]
    [Description("New empty active doc. Cassis keeps running if its host .gh stays open.")]
    public static async Task<CallToolResult> NewDocument(IGrasshopperDocumentService documentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(documentService), documentService));
            var result = await documentService.NewDocumentAsync();
            if (!result.Success)
            {
                return new { ok = false, err = result.ErrorMessage ?? result.Message };
            }

            var ctx = await UiThreadHelper.InvokeAsync(DocumentContextHelper.BuildContext);
            return new { ok = true, ctx };
        }, nameof(NewDocument));
    }

    [McpServerTool(Name = "GetDocumentInfo")]
    [Description(
        "Active doc summary + Cassis host. No component list (use Canvas_Snapshot). " +
        DocumentContextHelper.TargetingNote)]
    public static async Task<CallToolResult> GetDocumentInfo()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    return new { ok = true, open = false, ctx = DocumentContextHelper.BuildContext() };
                }

                return new
                {
                    ok = true,
                    open = true,
                    file = DocumentContextHelper.DocLabel(doc),
                    path = doc.FilePath,
                    n = doc.ObjectCount,
                    mod = doc.IsModified,
                    cassis = DocumentContextHelper.ContainsCassis(doc),
                    ctx = DocumentContextHelper.BuildContext()
                };
            });
        }, nameof(GetDocumentInfo));
    }

    [McpServerTool(Name = "ListOpenDocuments")]
    [Description("Open docs: file, n, active, cassis. No component data.")]
    public static async Task<CallToolResult> ListOpenDocuments()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var server = Instances.DocumentServer;
                var activeDoc = Instances.ActiveCanvas?.Document;
                var docs = new List<object>();
                if (server != null)
                {
                    foreach (GH_Document doc in server)
                    {
                        if (doc == null)
                        {
                            continue;
                        }

                        docs.Add(new
                        {
                            file = DocumentContextHelper.DocLabel(doc),
                            n = doc.ObjectCount,
                            mod = doc.IsModified,
                            active = ReferenceEquals(doc, activeDoc),
                            cassis = DocumentContextHelper.ContainsCassis(doc)
                        });
                    }
                }

                return new { ok = true, n = docs.Count, docs };
            });
        }, nameof(ListOpenDocuments));
    }

    [McpServerTool(Name = "ForceDocumentSolution")]
    [Description("Forces a complete solution of the active Grasshopper document")]
    public static async Task<CallToolResult> ForceDocumentSolution(
        [Description("Expire all components before recomputing")] bool expireAll = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await ForceSolutionAsync(expireAll);
            return new
            {
                success = result.Success,
                message = result.Message,
                expireAll
            };
        }, nameof(ForceDocumentSolution));
    }

    [McpServerTool(Name = "GetSolutionState")]
    [Description("Returns the current solution state of the active Grasshopper document. " +
        "States: PreProcess (idle, safe to read), Process (solving, poll again), " +
        "PostProcess (done, safe to read), Aborted (interrupted, retry ForceDocumentSolution). " +
        "Call this after saving a file or triggering a recomputation to confirm the canvas has finished solving before reading component outputs.")]
    public static async Task<CallToolResult> GetSolutionState()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document
                    ?? throw new InvalidOperationException("No active Grasshopper document");

                var state = document.SolutionState;
                return new
                {
                    success = true,
                    solutionState = state.ToString(),
                    isSolved = state == GH_ProcessStep.PostProcess
                };
            });

            return result;
        }, nameof(GetSolutionState));
    }

    [McpServerTool(Name = "ExpireComponent")]
    [Description("Expires a component by GUID and schedules it for recomputation")]
    public static async Task<CallToolResult> ExpireComponent(
        [Description("Component GUID")] string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentId, out var guid))
                {
                    throw new ArgumentException("Invalid component GUID format");
                }

                var obj = document.FindObject(guid, false);
                if (obj is not IGH_Component component)
                {
                    throw new ArgumentException($"Component {componentId} not found or is not a GH_Component");
                }

                component.ExpireSolution(true);
                return new
                {
                    success = true,
                    message = $"Component {component.NickName} expired and scheduled for recompute"
                };
            });

            return result;
        }, nameof(ExpireComponent));
    }

    [McpServerTool(Name = "RecomputeScriptComponents")]
    [Description("Expires all script components (Python/C#) and recomputes the document")]
    public static async Task<CallToolResult> RecomputeScriptComponents()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var count = 0;
                foreach (var obj in document.Objects)
                {
                    if (obj is IGH_Component component)
                    {
                        var typeName = component.GetType().FullName?.ToLowerInvariant() ?? string.Empty;
                        if (typeName.Contains("script") &&
                            (typeName.Contains("python") || typeName.Contains("csharp") || typeName.Contains("c#")))
                        {
                            component.ExpireSolution(true);
                            count++;
                        }
                    }
                }

                if (count > 0)
                {
                    document.NewSolution(false);
                }

                return new
                {
                    success = true,
                    message = count > 0
                        ? $"Recomputed {count} script components"
                        : "No script components found to recompute",
                    scriptComponentCount = count
                };
            });

            return result;
        }, nameof(RecomputeScriptComponents));
    }

    [McpServerTool(Name = "Canvas_Snapshot")]
    [Description("Returns a compact token-efficient map of the active canvas: toggles, sliders, panels, scripts, groups, and errors.")]
    public static async Task<CallToolResult> CanvasSnapshot()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = Instances.ActiveCanvas?.Document
                          ?? throw new InvalidOperationException("No active Grasshopper document.");

                var toggles = new List<object>();
                var sliders = new List<object>();
                var panels = new List<object>();
                var scripts = new List<object>();
                var groups = new List<object>();
                var errors = new List<object>();
                var typeCounts = new Dictionary<string, int>();

                foreach (var obj in doc.Objects)
                {
                    if (obj == null) continue;
                    var typeName = obj.GetType().FullName ?? string.Empty;

                    if (obj is GH_BooleanToggle toggle)
                    {
                        toggles.Add(new { id = obj.InstanceGuid.ToString(), nick = string.IsNullOrEmpty(toggle.NickName) ? toggle.Name ?? string.Empty : toggle.NickName, state = toggle.Value });
                        continue;
                    }

                    if (obj is GH_NumberSlider slider)
                    {
                        sliders.Add(new { id = obj.InstanceGuid.ToString(), nick = string.IsNullOrEmpty(slider.NickName) ? slider.Name ?? string.Empty : slider.NickName, value = slider.CurrentValue, min = slider.Slider.Minimum, max = slider.Slider.Maximum });
                        continue;
                    }

                    if (obj is GH_Panel panel)
                    {
                        var content = panel.UserText ?? string.Empty;
                        panels.Add(new { id = obj.InstanceGuid.ToString(), nick = panel.NickName ?? string.Empty, preview = content.Length > 50 ? content.Substring(0, 50) : content });
                        continue;
                    }

                    if (obj is GH_Group group)
                    {
                        if (!string.IsNullOrWhiteSpace(group.NickName))
                            groups.Add(new { name = group.NickName, members = group.ObjectIDs?.Count ?? 0 });
                        continue;
                    }

                    if (typeName.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var scriptProp = obj.GetType().GetProperty("ScriptInstance");
                        if (scriptProp != null)
                        {
                            var lang = typeName.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 ? "Python" : "C#";
                            bool hasErrors = obj is IGH_ActiveObject active && active.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error;
                            scripts.Add(new { id = obj.InstanceGuid.ToString(), lang, nick = obj.NickName ?? string.Empty, hasErrors });
                            if (hasErrors)
                                errors.Add(new { id = obj.InstanceGuid.ToString(), name = obj.Name ?? lang + " Script", message = $"{lang} script '{obj.NickName}' has errors" });
                            continue;
                        }
                    }

                    var compName = obj.Name ?? typeName;
                    if (!string.IsNullOrEmpty(compName))
                    {
                        typeCounts.TryGetValue(compName, out var cnt);
                        typeCounts[compName] = cnt + 1;
                    }

                    if (obj is IGH_ActiveObject activeObj && activeObj.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error)
                    {
                        var msgs = activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Error);
                        errors.Add(new { id = obj.InstanceGuid.ToString(), name = obj.Name ?? typeName, message = msgs != null && msgs.Count > 0 ? msgs[0].ToString() : "error" });
                    }
                }

                var componentTypes = typeCounts
                    .OrderByDescending(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                var fileName = doc.FilePath;
                fileName = !string.IsNullOrEmpty(fileName) ? System.IO.Path.GetFileName(fileName) : "Untitled.gh";

                return (object)new { file = fileName, componentCount = doc.ObjectCount, componentTypes, toggles, sliders, panels, scripts, groups, errors };
            });
        }, nameof(CanvasSnapshot));
    }

    private static async Task<(bool Success, string Message)> ForceSolutionAsync(bool expireAll)
    {
        return await UiThreadHelper.InvokeAsync(() =>
        {
            var document = Instances.ActiveCanvas?.Document;
            if (document == null)
            {
                return (false, "No active Grasshopper document found");
            }

            RhinoApp.WriteLine($"[DocumentTools] Forcing solution (expireAll={expireAll})");

            if (expireAll)
            {
                var expired = 0;
                foreach (var obj in document.Objects)
                {
                    if (obj is IGH_Component component)
                    {
                        component.ExpireSolution(true);
                        expired++;
                    }
                }

                RhinoApp.WriteLine($"[DocumentTools] Expired {expired} components prior to recompute");
            }

            document.NewSolution(expireAll);

            return (true, $"Document solution completed across {document.ObjectCount} objects");
        });
    }
}
