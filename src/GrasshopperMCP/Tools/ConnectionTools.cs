using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using GrasshopperMCP.Extensions;
using GrasshopperMCP.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace GrasshopperMCP.Tools;

/// <summary>
/// Enhanced tools for managing connections between Grasshopper components.
/// </summary>
[McpServerToolType]
public static class ConnectionTools
{
    [McpServerTool(Name = "Connect_Components_By_Name")]
    [Description("Connects two components using port names, avoiding common pitfalls like the C# 'out' socket")]
    public static async Task<CallToolResult> ConnectComponentsByName(
        [Description("Source component GUID")] string sourceComponentId,
        [Description("Target component GUID")] string targetComponentId,
        [Description("Optional source output nickname")] string? sourceOutputName = null,
        [Description("Optional target input nickname")] string? targetInputName = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            var outcome = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var source = FindComponent(document, sourceComponentId);
                var target = FindComponent(document, targetComponentId);

                var sourceIndex = ResolveOutputIndex(source, sourceOutputName);
                var targetIndex = ResolveInputIndex(target, targetInputName);

                var validation = ValidateConnectionCore(document, sourceComponentId, sourceIndex, targetComponentId, targetIndex);
                if (!validation.CanConnect)
                {
                    return new
                    {
                        success = false,
                        message = validation.Message,
                        issues = validation.Issues,
                        validation
                    };
                }

                PerformConnection(source, sourceIndex, target, targetIndex);

                return new
                {
                    success = true,
                    message = $"Connected {source.NickName}[{sourceIndex}] → {target.NickName}[{targetIndex}]",
                    validation
                };
            });

            return outcome;
        }, nameof(ConnectComponentsByName));
    }

    [McpServerTool(Name = "ConnectComponentsWithValidation")]
    [Description("Connects components by index with validation and detailed diagnostic information")]
    public static async Task<CallToolResult> ConnectComponentsWithValidation(
        [Description("Source component GUID")] string sourceComponentId,
        [Description("Source output index")] int sourceOutputIndex,
        [Description("Target component GUID")] string targetComponentId,
        [Description("Target input index")] int targetInputIndex)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            var outcome = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var source = FindComponent(document, sourceComponentId);
                var target = FindComponent(document, targetComponentId);

                var validation = ValidateConnectionCore(document, sourceComponentId, sourceOutputIndex, targetComponentId, targetInputIndex);
                if (!validation.CanConnect)
                {
                    return new
                    {
                        success = false,
                        message = validation.Message,
                        issues = validation.Issues,
                        validation
                    };
                }

                PerformConnection(source, sourceOutputIndex, target, targetInputIndex);

                return new
                {
                    success = true,
                    message = $"Connected {source.NickName}[{sourceOutputIndex}] → {target.NickName}[{targetInputIndex}]",
                    validation
                };
            });

            return outcome;
        }, nameof(ConnectComponentsWithValidation));
    }

    [McpServerTool(Name = "ValidateConnection")]
    [Description("Validates that a connection between two component sockets is possible before attempting it")]
    public static async Task<CallToolResult> ValidateConnection(
        [Description("Source component GUID")] string sourceComponentId,
        [Description("Source output index")] int sourceOutputIndex,
        [Description("Target component GUID")] string targetComponentId,
        [Description("Target input index")] int targetInputIndex)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");
                return ValidateConnectionCore(document, sourceComponentId, sourceOutputIndex, targetComponentId, targetInputIndex);
            });

            return result;
        }, nameof(ValidateConnection));
    }

    [McpServerTool(Name = "GetAllConnections")]
    [Description("Returns a snapshot of all connections between components in the active document")]
    public static async Task<CallToolResult> GetAllConnections()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var connections = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var records = new List<ConnectionRecord>();

                foreach (var component in document.Objects.OfType<IGH_Component>())
                {
                    var targetId = component.InstanceGuid.ToString();

                    for (var inputIndex = 0; inputIndex < component.Params.Input.Count; inputIndex++)
                    {
                        var input = component.Params.Input[inputIndex];
                        if (input.SourceCount == 0)
                        {
                            continue;
                        }

                        foreach (var source in input.Sources)
                        {
                            var owner = source.Attributes?.GetTopLevel.DocObject as IGH_Component;
                            if (owner == null)
                            {
                                continue;
                            }

                            records.Add(new ConnectionRecord
                            {
                                SourceComponentId = owner.InstanceGuid.ToString(),
                                SourceComponentName = owner.NickName ?? owner.Name ?? owner.GetType().Name,
                                SourceOutputIndex = owner.Params.Output.IndexOf(source),
                                SourceOutputName = source.NickName ?? source.Name ?? string.Empty,
                                TargetComponentId = targetId,
                                TargetComponentName = component.NickName ?? component.Name ?? component.GetType().Name,
                                TargetInputIndex = inputIndex,
                                TargetInputName = input.NickName ?? input.Name ?? string.Empty
                            });
                        }
                    }
                }

                return records;
            });

            return new
            {
                success = true,
                count = connections.Count,
                connections
            };
        }, nameof(GetAllConnections));
    }

    private static IGH_Component FindComponent(GH_Document document, string componentId)
    {
        if (!Guid.TryParse(componentId, out var guid))
        {
            throw new ArgumentException($"Invalid component GUID: {componentId}");
        }

        var component = document.FindObject(guid, false) as IGH_Component;
        if (component == null)
        {
            throw new ArgumentException($"Component {componentId} not found");
        }

        return component;
    }

    private static int ResolveOutputIndex(IGH_Component component, string? nickname)
    {
        if (component.Params.Output == null || component.Params.Output.Count == 0)
        {
            throw new InvalidOperationException($"Component {component.NickName} has no outputs");
        }

        if (!string.IsNullOrWhiteSpace(nickname))
        {
            for (var i = 0; i < component.Params.Output.Count; i++)
            {
                var candidate = component.Params.Output[i];
                var name = candidate.NickName ?? candidate.Name ?? string.Empty;
                if (string.Equals(name, nickname, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // Fallback: choose the first non-"out" socket, then the first socket.
        for (var i = 0; i < component.Params.Output.Count; i++)
        {
            var name = component.Params.Output[i].NickName ?? component.Params.Output[i].Name ?? string.Empty;
            if (!string.Equals(name.Trim(), "out", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    private static int ResolveInputIndex(IGH_Component component, string? nickname)
    {
        if (component.Params.Input == null || component.Params.Input.Count == 0)
        {
            throw new InvalidOperationException($"Component {component.NickName} has no inputs");
        }

        if (!string.IsNullOrWhiteSpace(nickname))
        {
            for (var i = 0; i < component.Params.Input.Count; i++)
            {
                var candidate = component.Params.Input[i];
                var name = candidate.NickName ?? candidate.Name ?? string.Empty;
                if (string.Equals(name, nickname, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static void PerformConnection(IGH_Component source, int sourceOutputIndex, IGH_Component target, int targetInputIndex)
    {
        if (source.Params.Output == null || sourceOutputIndex >= source.Params.Output.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceOutputIndex), "Source output index out of range");
        }

        if (target.Params.Input == null || targetInputIndex >= target.Params.Input.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInputIndex), "Target input index out of range");
        }

        var sourceParam = source.Params.Output[sourceOutputIndex];
        var targetParam = target.Params.Input[targetInputIndex];

        targetParam.RemoveAllSources();
        targetParam.AddSource(sourceParam);

        target.ExpireSolution(true);
        RhinoApp.WriteLine($"[ConnectionTools] Connected {source.NickName}[{sourceOutputIndex}] → {target.NickName}[{targetInputIndex}]");
    }

    private static ConnectionValidationResult ValidateConnectionCore(
        GH_Document document,
        string sourceComponentId,
        int sourceOutputIndex,
        string targetComponentId,
        int targetInputIndex)
    {
        var result = new ConnectionValidationResult();
        var issues = new List<string>();

        var sourceInfo = CreateComponentInfo(document, sourceComponentId, issues, requireOutputIndex: sourceOutputIndex);
        var targetInfo = CreateComponentInfo(document, targetComponentId, issues, requireInputIndex: targetInputIndex);

        result.SourceInfo = sourceInfo;
        result.TargetInfo = targetInfo;

        if (issues.Count == 0)
        {
            result.CanConnect = true;
            result.Message = "Connection is valid";
        }
        else
        {
            result.CanConnect = false;
            result.Message = "Connection validation failed";
            result.Issues = issues;
        }

        return result;
    }

    private static ComponentConnectionInfo CreateComponentInfo(
        GH_Document document,
        string componentId,
        List<string> issues,
        int? requireOutputIndex = null,
        int? requireInputIndex = null)
    {
        if (!Guid.TryParse(componentId, out var guid))
        {
            issues.Add($"Invalid component GUID: {componentId}");
            return new ComponentConnectionInfo { Exists = false, Id = Guid.Empty };
        }

        var component = document.FindObject(guid, false) as IGH_Component;
        if (component == null)
        {
            issues.Add($"Component not found: {componentId}");
            return new ComponentConnectionInfo { Exists = false, Id = guid };
        }

        var info = new ComponentConnectionInfo
        {
            Exists = true,
            Id = guid,
            Name = component.NickName ?? component.Name ?? component.GetType().Name,
            InputCount = component.Params.Input?.Count ?? 0,
            OutputCount = component.Params.Output?.Count ?? 0,
            InputNames = component.Params.Input?.Select(p => p.NickName ?? p.Name ?? string.Empty).ToList() ?? new List<string>(),
            OutputNames = component.Params.Output?.Select(p => p.NickName ?? p.Name ?? string.Empty).ToList() ?? new List<string>()
        };

        if (requireOutputIndex.HasValue &&
            (requireOutputIndex.Value < 0 || requireOutputIndex.Value >= info.OutputCount))
        {
            issues.Add($"Source output index {requireOutputIndex.Value} is out of range (component has {info.OutputCount} outputs)");
        }

        if (requireInputIndex.HasValue &&
            (requireInputIndex.Value < 0 || requireInputIndex.Value >= info.InputCount))
        {
            issues.Add($"Target input index {requireInputIndex.Value} is out of range (component has {info.InputCount} inputs)");
        }

        return info;
    }

    private sealed class ConnectionValidationResult
    {
        public bool CanConnect { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<string> Issues { get; set; } = new();
        public ComponentConnectionInfo? SourceInfo { get; set; }
        public ComponentConnectionInfo? TargetInfo { get; set; }
    }

    private sealed class ComponentConnectionInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool Exists { get; set; }
        public int InputCount { get; set; }
        public int OutputCount { get; set; }
        public List<string> InputNames { get; set; } = new();
        public List<string> OutputNames { get; set; } = new();
    }

    [McpServerTool(Name = "Connect_Components")]
    [Description("Connects two Grasshopper components by output/input parameter index, replacing any existing connection on the target input")]
    public static async Task<CallToolResult> ConnectComponents(
        [Description("Source component GUID")] string sourceComponentId,
        [Description("Source output parameter index")] int sourceOutputIndex,
        [Description("Target component GUID")] string targetComponentId,
        [Description("Target input parameter index")] int targetInputIndex)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(sourceComponentId, out var srcGuid))
                    throw new ArgumentException("Invalid source GUID");
                if (!Guid.TryParse(targetComponentId, out var dstGuid))
                    throw new ArgumentException("Invalid target GUID");

                var srcObj = doc.FindObject(srcGuid, false) as IGH_Component
                    ?? throw new InvalidOperationException("Source component not found");
                var dstObj = doc.FindObject(dstGuid, false) as IGH_Component
                    ?? throw new InvalidOperationException("Target component not found");

                if (sourceOutputIndex < 0 || sourceOutputIndex >= srcObj.Params.Output.Count)
                    throw new ArgumentOutOfRangeException(nameof(sourceOutputIndex), $"Source has {srcObj.Params.Output.Count} outputs");
                if (targetInputIndex < 0 || targetInputIndex >= dstObj.Params.Input.Count)
                    throw new ArgumentOutOfRangeException(nameof(targetInputIndex), $"Target has {dstObj.Params.Input.Count} inputs");

                var srcParam = srcObj.Params.Output[sourceOutputIndex];
                var dstParam = dstObj.Params.Input[targetInputIndex];
                dstParam.RemoveAllSources();
                dstParam.AddSource(srcParam);
                dstObj.ExpireSolution(true);

                return new { success = true, message = $"Connected {srcObj.NickName}[{sourceOutputIndex}] → {dstObj.NickName}[{targetInputIndex}]" };
            });

            return result;
        }, nameof(ConnectComponents));
    }

    [McpServerTool(Name = "ReconnectByName")]
    [Description("Connects components by output port name, falling back to the first non-'out' output if the name is not found")]
    public static async Task<CallToolResult> ReconnectByName(
        [Description("Source component GUID")] string sourceComponentId,
        [Description("Target component GUID")] string targetComponentId,
        [Description("Source output port name (default: 'a')")] string outputName = "a",
        [Description("Target input parameter index (default: 0)")] int targetInputIndex = 0)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(sourceComponentId, out var srcGuid))
                    throw new ArgumentException("Invalid source GUID");
                if (!Guid.TryParse(targetComponentId, out var dstGuid))
                    throw new ArgumentException("Invalid target GUID");

                var srcObj = doc.FindObject(srcGuid, false) as IGH_Component
                    ?? throw new InvalidOperationException("Source component not found");
                var dstObj = doc.FindObject(dstGuid, false) as IGH_Component
                    ?? throw new InvalidOperationException("Target component not found");

                // Resolve source output: by name, then first non-"out", then index 0
                int srcIndex = -1;
                for (int i = 0; i < srcObj.Params.Output.Count; i++)
                {
                    var n = srcObj.Params.Output[i].NickName ?? srcObj.Params.Output[i].Name ?? string.Empty;
                    if (string.Equals(n, outputName, StringComparison.OrdinalIgnoreCase)) { srcIndex = i; break; }
                }
                if (srcIndex < 0)
                {
                    for (int i = 0; i < srcObj.Params.Output.Count; i++)
                    {
                        var n = (srcObj.Params.Output[i].NickName ?? srcObj.Params.Output[i].Name ?? string.Empty).Trim();
                        if (!string.Equals(n, "out", StringComparison.OrdinalIgnoreCase)) { srcIndex = i; break; }
                    }
                }
                if (srcIndex < 0) srcIndex = 0;

                if (targetInputIndex < 0 || targetInputIndex >= dstObj.Params.Input.Count)
                    throw new ArgumentOutOfRangeException(nameof(targetInputIndex), $"Target has {dstObj.Params.Input.Count} inputs");

                var dstParam = dstObj.Params.Input[targetInputIndex];
                dstParam.RemoveAllSources();
                dstParam.AddSource(srcObj.Params.Output[srcIndex]);
                dstObj.ExpireSolution(true);

                return new { success = true, message = $"Connected {srcObj.NickName}[{srcIndex}] → {dstObj.NickName}[{targetInputIndex}]" };
            });

            return result;
        }, nameof(ReconnectByName));
    }

    private sealed class ConnectionRecord
    {
        public string SourceComponentId { get; set; } = string.Empty;
        public string SourceComponentName { get; set; } = string.Empty;
        public int SourceOutputIndex { get; set; }
        public string SourceOutputName { get; set; } = string.Empty;
        public string TargetComponentId { get; set; } = string.Empty;
        public string TargetComponentName { get; set; } = string.Empty;
        public int TargetInputIndex { get; set; }
        public string TargetInputName { get; set; } = string.Empty;
    }
}
