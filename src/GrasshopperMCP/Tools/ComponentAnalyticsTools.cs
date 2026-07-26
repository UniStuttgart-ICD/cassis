using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using GrasshopperMCP.Extensions;
using GrasshopperMCP.Models;
using GrasshopperMCP.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;
using Rhino.Geometry;

namespace GrasshopperMCP.Tools;

/// <summary>
/// Discovery tools for inspecting the current Grasshopper document.
/// </summary>
[McpServerToolType]
public static class ComponentAnalyticsTools
{
    [McpServerTool(Name = "Get_ComponentCount")]
    [Description("Returns counts of components, parameters, groups, and connections in the active document")]
    public static async Task<CallToolResult> GetComponentCount()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var summary = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var componentCount = document.Objects.OfType<IGH_Component>().Count();
                var paramCount = document.Objects.OfType<IGH_Param>().Count();
                var groupCount = document.Objects.OfType<GH_Group>().Count();

                var connectionCount = document.Objects
                    .OfType<IGH_Component>()
                    .SelectMany(c => c.Params.Input ?? Enumerable.Empty<IGH_Param>())
                    .Sum(param => param.SourceCount);

                return new
                {
                    success = true,
                    components = componentCount,
                    parameters = paramCount,
                    groups = groupCount,
                    connections = connectionCount
                };
            });

            return summary;
        }, nameof(GetComponentCount));
    }

    [McpServerTool(Name = "Get_AllComponents")]
    [Description("Lists all components in the active document with category and canvas location metadata")]
    public static async Task<CallToolResult> GetAllComponents()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var components = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                return document.Objects.Select(obj => new
                {
                    id = obj.InstanceGuid,
                    name = obj.Name ?? string.Empty,
                    nickName = obj.NickName ?? string.Empty,
                    category = (obj as IGH_Component)?.Category ?? "N/A",
                    subCategory = (obj as IGH_Component)?.SubCategory ?? "N/A",
                    typeName = obj.GetType().FullName ?? string.Empty,
                    position = new Position
                    {
                        X = obj.Attributes?.Pivot.X ?? 0f,
                        Y = obj.Attributes?.Pivot.Y ?? 0f
                    }
                }).ToList();
            });

            return new
            {
                success = true,
                count = components.Count,
                components
            };
        }, nameof(GetAllComponents));
    }

    [McpServerTool(Name = "GetDetailedComponentInfo")]
    [Description("Returns detailed wiring information for each component, including inputs, outputs, and runtime errors")]
    public static async Task<CallToolResult> GetDetailedComponentInfo()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var info = await CollectDetailedComponentInfo(componentId: null);
            return new
            {
                success = true,
                count = info.Count,
                components = info
            };
        }, nameof(GetDetailedComponentInfo));
    }

    [McpServerTool(Name = "GetDetailedComponentInfoById")]
    [Description("Returns detailed wiring information for a single component by GUID")]
    public static async Task<CallToolResult> GetDetailedComponentInfoById(
        [Description("Component GUID")] string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var info = await CollectDetailedComponentInfo(componentId);
            if (info.Count == 0)
            {
                return new
                {
                    success = false,
                    message = $"Component {componentId} not found"
                };
            }

            return new
            {
                success = true,
                component = info.Single()
            };
        }, nameof(GetDetailedComponentInfoById));
    }

    private static async Task<List<DetailedComponentRecord>> CollectDetailedComponentInfo(string? componentId)
    {
        return await UiThreadHelper.InvokeAsync(() =>
        {
            var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

            var components = document.Objects.OfType<IGH_Component>();
            if (!string.IsNullOrWhiteSpace(componentId) && Guid.TryParse(componentId, out var guid))
            {
                components = components.Where(c => c.InstanceGuid == guid);
            }

            var results = new List<DetailedComponentRecord>();

            foreach (var component in components)
            {
                var record = new DetailedComponentRecord
                {
                    Id = component.InstanceGuid,
                    Name = component.Name ?? string.Empty,
                    NickName = component.NickName ?? string.Empty,
                    TypeName = component.GetType().FullName ?? string.Empty,
                    Category = component.Category ?? string.Empty,
                    SubCategory = component.SubCategory ?? string.Empty,
                    Description = component.Description ?? string.Empty,
                    Keywords = component.Keywords?.ToList() ?? [],
                    Position = new Position
                    {
                        X = component.Attributes?.Pivot.X ?? 0f,
                        Y = component.Attributes?.Pivot.Y ?? 0f
                    },
                    Inputs = CreateParameterInfo(component.Params.Input),
                    Outputs = CreateParameterInfo(component.Params.Output),
                    Phase = component.Phase.ToString()
                };

                record.ErrorMessages = component.RuntimeMessages(GH_RuntimeMessageLevel.Error).ToList();
                record.WarningMessages = component.RuntimeMessages(GH_RuntimeMessageLevel.Warning).ToList();
                record.HasErrors = record.ErrorMessages.Count > 0;
                record.HasWarnings = record.WarningMessages.Count > 0;

                results.Add(record);
            }

            return results;
        });
    }

    private static List<ParameterRecord> CreateParameterInfo(IList<IGH_Param>? parameters)
    {
        if (parameters == null)
        {
            return new List<ParameterRecord>();
        }

        var records = new List<ParameterRecord>();

        for (var index = 0; index < parameters.Count; index++)
        {
            var param = parameters[index];
            var volatileData = param.VolatileData;
            
            // Extract actual values from the data tree
            string? values = null;
            int itemCount = 0;
            int branchCount = 0;
            
            if (volatileData != null && !volatileData.IsEmpty)
            {
                branchCount = volatileData.PathCount;
                var lines = new List<string>();
                
                foreach (var path in volatileData.Paths)
                {
                    var branch = volatileData.get_Branch(path);
                    if (branch == null) continue;
                    
                    itemCount += branch.Count;
                    
                    // Include path header if multiple branches
                    if (branchCount > 1)
                    {
                        lines.Add($"{path}");
                    }
                    
                    for (int j = 0; j < branch.Count; j++)
                    {
                        var item = branch[j];
                        var itemText = item switch
                        {
                            GH_String str => str.Value ?? string.Empty,
                            IGH_Goo goo => goo?.ToString() ?? string.Empty,
                            _ => item?.ToString() ?? string.Empty
                        };
                        
                        // For single branch, just list values; for multi-branch, include index
                        if (branchCount > 1)
                        {
                            lines.Add($"{j}. {itemText}");
                        }
                        else
                        {
                            lines.Add(itemText);
                        }
                    }
                }
                
                values = string.Join("\n", lines);
            }
            
            var record = new ParameterRecord
            {
                Index = index,
                Name = param.Name ?? string.Empty,
                NickName = param.NickName ?? string.Empty,
                Description = param.Description ?? string.Empty,
                TypeName = param.GetType().Name,
                HasData = param.SourceCount > 0 || !volatileData.IsEmpty,
                SourceCount = param.SourceCount,
                BranchCount = branchCount,
                ItemCount = itemCount,
                Values = values
            };

            foreach (var source in param.Sources)
            {
                if (source.Attributes?.GetTopLevel.DocObject is IGH_Component owner)
                {
                    record.Sources.Add(new ConnectionRecord
                    {
                        SourceComponentId = owner.InstanceGuid.ToString(),
                        SourceComponentName = owner.NickName ?? owner.Name ?? owner.GetType().Name,
                        SourceOutputIndex = owner.Params.Output.IndexOf(source),
                        SourceOutputName = source.NickName ?? source.Name ?? string.Empty
                    });
                }
            }

            records.Add(record);
        }

        return records;
    }

    private sealed class DetailedComponentRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NickName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Keywords { get; set; } = new();
        public Position Position { get; set; } = new();
        public List<ParameterRecord> Inputs { get; set; } = new();
        public List<ParameterRecord> Outputs { get; set; } = new();
        public bool HasErrors { get; set; }
        public bool HasWarnings { get; set; }
        public string Phase { get; set; } = string.Empty;
        public List<string> ErrorMessages { get; set; } = new();
        public List<string> WarningMessages { get; set; } = new();
    }

    private sealed class ParameterRecord
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NickName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public bool HasData { get; set; }
        public int SourceCount { get; set; }
        public int BranchCount { get; set; }
        public int ItemCount { get; set; }
        public string? Values { get; set; }
        public List<ConnectionRecord> Sources { get; set; } = new();
    }

    private sealed class ConnectionRecord
    {
        public string SourceComponentId { get; set; } = string.Empty;
        public string SourceComponentName { get; set; } = string.Empty;
        public int SourceOutputIndex { get; set; }
        public string SourceOutputName { get; set; } = string.Empty;
    }

    [McpServerTool(Name = "Get_Component_Output")]
    [Description("Reads runtime output values of a Grasshopper component with full metadata")]
    public static async Task<CallToolResult> GetComponentOutput(
        [Description("Component GUID as string")] string componentId,
        [Description("Output parameter index (default 0)")] int outputIndex = 0,
        [Description("Max items to return per branch (default 10)")] int maxItemsPerBranch = 10,
        [Description("Max branches to return (default 20)")] int maxBranches = 20)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentId, out var guid))
                    throw new ArgumentException($"Invalid component ID: {componentId}");

                var obj = doc.FindObject(guid, true)
                    ?? throw new InvalidOperationException($"Component not found: {componentId}");

                var output = new ComponentOutputData
                {
                    ComponentId = guid,
                    ComponentName = obj.Name ?? string.Empty,
                    ComponentType = obj.GetType().FullName ?? string.Empty,
                    Success = true
                };

                if (obj is IGH_Component comp)
                {
                    output.Category = comp.Category ?? string.Empty;
                    output.SubCategory = comp.SubCategory ?? string.Empty;
                    output.ExecutionPhase = comp.Phase.ToString();
                    output.Errors = comp.RuntimeMessages(GH_RuntimeMessageLevel.Error)
                        .Select(e => e.ToString()).ToList();
                }

                var param = GetOutputParam(obj, outputIndex)
                    ?? throw new InvalidOperationException($"Output index {outputIndex} not found on component");

                output.Output = ReadOutputData(param, outputIndex, maxItemsPerBranch, maxBranches);
                return output;
            });

            return result;
        }, nameof(GetComponentOutput));
    }

    private static IGH_Param? GetOutputParam(IGH_DocumentObject obj, int outputIndex)
    {
        if (obj is IGH_Component comp)
        {
            if (outputIndex < 0 || outputIndex >= comp.Params.Output.Count) return null;
            return comp.Params.Output[outputIndex];
        }
        if (obj is IGH_Param param) return outputIndex == 0 ? param : null;
        return null;
    }

    private static OutputData ReadOutputData(IGH_Param param, int index, int maxItems, int maxBranches)
    {
        var info = new OutputData
        {
            Index = index,
            Name = param.Name ?? string.Empty,
            NickName = param.NickName ?? string.Empty,
            TypeName = param.GetType().Name
        };

        var data = param.VolatileData;
        if (data == null || data.IsEmpty) return info;

        info.HasData = true;
        info.TotalBranchCount = data.PathCount;
        for (int i = 0; i < data.PathCount; i++)
        {
            var b = data.get_Branch(i);
            if (b != null) info.TotalItemCount += b.Count;
        }

        int branchesToRead = Math.Min(data.PathCount, maxBranches);
        for (int b = 0; b < branchesToRead; b++)
        {
            var branch = data.get_Branch(b);
            var path = data.get_Path(b);
            var branchInfo = new OutputBranch
            {
                Path = path?.ToString() ?? $"{{{b}}}",
                ItemCount = branch?.Count ?? 0
            };

            if (branch != null)
            {
                int toRead = Math.Min(branch.Count, maxItems);
                branchInfo.Truncated = branch.Count > maxItems;
                for (int i = 0; i < toRead; i++)
                    branchInfo.Items.Add(SerializeOutputItem(branch[i], i));
            }

            info.Branches.Add(branchInfo);
        }

        return info;
    }

    private static OutputItem SerializeOutputItem(object? goo, int index)
    {
        var item = new OutputItem { Index = index };
        if (goo == null) { item.TypeName = "null"; return item; }

        item.TypeName = goo.GetType().Name;
        item.IsGeometry = goo is GH_Point or GH_Curve or GH_Surface or GH_Brep or GH_Mesh
            or GH_Vector or GH_Plane or GH_Box or GH_Line or GH_Arc or GH_Circle;

        item.Value = goo switch
        {
            GH_Number num    => (object)num.Value,
            GH_Integer gi    => gi.Value,
            GH_Boolean gb    => gb.Value,
            GH_String gs     => gs.Value ?? string.Empty,
            GH_Colour gc     => $"#{gc.Value.R:X2}{gc.Value.G:X2}{gc.Value.B:X2}",
            GH_Point pt      => new { x = pt.Value.X, y = pt.Value.Y, z = pt.Value.Z },
            GH_Vector vec    => new { x = vec.Value.X, y = vec.Value.Y, z = vec.Value.Z },
            GH_Plane pl      => new { origin = new { x = pl.Value.OriginX, y = pl.Value.OriginY, z = pl.Value.OriginZ }, normal = new { x = pl.Value.ZAxis.X, y = pl.Value.ZAxis.Y, z = pl.Value.ZAxis.Z } },
            GH_Curve c when c.Value != null => (object)new { type = c.Value.GetType().Name, length = c.Value.GetLength(), isClosed = c.Value.IsClosed },
            GH_Mesh m when m.Value != null  => new { vertexCount = m.Value.Vertices.Count, faceCount = m.Value.Faces.Count, isClosed = m.Value.IsClosed },
            GH_Brep br when br.Value != null => new { faceCount = br.Value.Faces.Count, edgeCount = br.Value.Edges.Count, isSolid = br.Value.IsSolid },
            IGH_Goo ig       => new { gooType = ig.GetType().FullName, toString = ig.ToString() },
            _                => goo.ToString() ?? string.Empty
        };

        return item;
    }

    private sealed class ComponentOutputData
    {
        public Guid ComponentId { get; set; }
        public string ComponentName { get; set; } = string.Empty;
        public string ComponentType { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SubCategory { get; set; } = string.Empty;
        public string ExecutionPhase { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
        public bool Success { get; set; }
        public OutputData Output { get; set; } = new();
    }

    private sealed class OutputData
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string NickName { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public int TotalBranchCount { get; set; }
        public int TotalItemCount { get; set; }
        public bool HasData { get; set; }
        public List<OutputBranch> Branches { get; set; } = new();
    }

    private sealed class OutputBranch
    {
        public string Path { get; set; } = string.Empty;
        public int ItemCount { get; set; }
        public bool Truncated { get; set; }
        public List<OutputItem> Items { get; set; } = new();
    }

    private sealed class OutputItem
    {
        public int Index { get; set; }
        public string TypeName { get; set; } = string.Empty;
        public object? Value { get; set; }
        public bool IsGeometry { get; set; }
    }
}
