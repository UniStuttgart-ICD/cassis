using System;
using System.ComponentModel;
using System.Drawing;
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
/// Tools that modify component placement on the canvas.
/// </summary>
[McpServerToolType]
public static class ComponentTransformTools
{
    [McpServerTool(Name = "Move_Component")]
    [Description("Moves a component by a relative offset on the Grasshopper canvas")]
    public static async Task<CallToolResult> MoveComponent(
        [Description("Component GUID to move")] string componentInstanceGuid,
        [Description("Relative X offset in canvas units")] double x,
        [Description("Relative Y offset in canvas units")] double y)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentInstanceGuid), componentInstanceGuid));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentInstanceGuid, out var guid))
                {
                    throw new ArgumentException("Invalid component GUID format");
                }

                var obj = document.FindObject(guid, false);
                if (obj?.Attributes == null)
                {
                    throw new ArgumentException($"Component {componentInstanceGuid} not found");
                }

                var current = obj.Attributes.Pivot;
                var updated = new PointF(current.X + (float)x, current.Y + (float)y);

                obj.Attributes.Pivot = updated;
                obj.Attributes.ExpireLayout();
                document.NewSolution(false);

                return new
                {
                    success = true,
                    componentId = componentInstanceGuid,
                    delta = new { x, y },
                    newPosition = new { x = updated.X, y = updated.Y }
                };
            });

            return result;
        }, nameof(MoveComponent));
    }
}
