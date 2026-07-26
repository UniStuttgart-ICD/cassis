using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Cassis.Extensions;
using Cassis.Models;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis.Tools;

/// <summary>
/// Tools for querying Grasshopper groups and their contained components.
/// </summary>
[McpServerToolType]
public static class GroupTools
{
    [McpServerTool(Name = "Get_Components_In_Group")]
    [Description("Gets all components within a specific group by group name")]
    public static async Task<CallToolResult> GetComponentsInGroup(
        [Description("Name of the group to search for components")] string groupName)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(groupName), groupName));

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var groups = document.Objects.OfType<GH_Group>()
                    .Where(g => string.Equals(g.NickName ?? string.Empty, groupName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (!groups.Any())
                {
                    return new
                    {
                        success = true,
                        groupName,
                        found = false,
                        componentCount = 0,
                        components = Array.Empty<object>(),
                        message = $"No groups found with name '{groupName}'"
                    };
                }

                var results = new List<object>();
                var seen = new HashSet<Guid>();

                foreach (var group in groups)
                {
                    foreach (var id in group.ObjectIDs)
                    {
                        if (!seen.Add(id)) continue;
                        var obj = document.FindObject(id, true);
                        if (obj == null) continue;

                        results.Add(new
                        {
                            id = obj.InstanceGuid,
                            type = obj.GetType().Name,
                            name = obj.Name ?? string.Empty,
                            nickName = obj.NickName ?? string.Empty,
                            position = new Position
                            {
                                X = obj.Attributes?.Pivot.X ?? 0f,
                                Y = obj.Attributes?.Pivot.Y ?? 0f
                            }
                        });
                    }
                }

                return new
                {
                    success = true,
                    groupName,
                    found = true,
                    componentCount = results.Count,
                    components = results
                };
            });

            return result;
        }, nameof(GetComponentsInGroup));
    }
}
