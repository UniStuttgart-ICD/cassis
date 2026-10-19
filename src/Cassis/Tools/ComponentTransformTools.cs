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
    [Description(
        "Moves a component by a relative offset (not absolute). " +
        "For designed grids set avoidOverlap:false — otherwise a nudge can land far from the requested point. " +
        "Check response requested vs newPosition when nudged:true. " +
        "Vertical pitch for Material rows ~160–180; chain columns ≥250–300 apart.")]
    public static async Task<CallToolResult> MoveComponent(
        [Description("Component GUID to move")] string componentInstanceGuid,
        [Description("Relative X offset in canvas units — compute from get_allcomponents / getdetailed pivots")] double x,
        [Description("Relative Y offset in canvas units — re-read pivots after any nudge to avoid drift")] double y,
        [Description("Gap used only when avoidOverlap is true (default 16; too tight for tall GH components)")]
        float padding = CanvasPlacement.DefaultPadding,
        [Description("Nudge away from overlaps after move. Default false for intentional layouts; set true only when filling gaps.")]
        bool avoidOverlap = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentInstanceGuid), componentInstanceGuid));
            CanvasPlacement.ValidateCoordinate(nameof(x), x);
            CanvasPlacement.ValidateCoordinate(nameof(y), y);
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
