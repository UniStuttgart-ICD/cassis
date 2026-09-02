using System;
using System.ComponentModel;
using System.Drawing;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Cassis.Extensions;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace Cassis.Tools;

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
        [Description("Relative Y offset in canvas units")] double y,
        [Description("Minimum gap in canvas units between this component and neighbors (default 16)")]
        float padding = CanvasPlacement.DefaultPadding,
        [Description("When true, nudge away from overlapping components after the move (default true)")]
        bool avoidOverlap = true)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentInstanceGuid), componentInstanceGuid));
            McpExtensions.ValidateRange(nameof(x), x, -CanvasPlacement.CanvasLimit, CanvasPlacement.CanvasLimit);
            McpExtensions.ValidateRange(nameof(y), y, -CanvasPlacement.CanvasLimit, CanvasPlacement.CanvasLimit);
            padding = CanvasPlacement.ValidatePadding(padding);

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
                var requested = new PointF(current.X + (float)x, current.Y + (float)y);

                var placement = CanvasPlacement.PlaceOnCanvas(document, obj, requested, padding, avoidOverlap);
                document.NewSolution(false);

                return new
                {
                    success = true,
                    componentId = componentInstanceGuid,
                    delta = new { x, y },
                    requested = new { x = requested.X, y = requested.Y },
                    newPosition = new { x = placement.NewPivot.X, y = placement.NewPivot.Y },
                    nudged = placement.Nudged,
                    nudgeReason = placement.NudgeReason,
                    outOfCanvasBounds = placement.OutOfCanvasBounds,
                };
            });

            return result;
        }, nameof(MoveComponent));
    }
}
