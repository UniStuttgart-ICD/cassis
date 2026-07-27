using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Data;
using Cassis.Extensions;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis.Tools;

/// <summary>
/// Tools for toggling common Grasshopper UI elements and solver-related inputs.
/// </summary>
[McpServerToolType]
public static class ToggleTools
{
    [McpServerTool(Name = "Toggle_BooleanToggle")]
    [Description("Toggles a Grasshopper Boolean Toggle component by GUID and returns its new state")]
    public static async Task<CallToolResult> ToggleBooleanToggle(
        [Description("Component GUID of the Boolean Toggle")] string componentId)
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

                var obj = document.FindObject(guid, true);
                if (obj is not GH_BooleanToggle toggle)
                {
                    throw new ArgumentException($"Component {componentId} is not a Boolean Toggle or was not found");
                }

                toggle.Value = !toggle.Value;
                toggle.ExpireSolution(true);

                return new { success = true, componentId, value = toggle.Value };
            });

            return result;
        }, nameof(ToggleBooleanToggle));
    }

    [McpServerTool(Name = "Toggle_SolverExecute")]
    [Description("Finds a component with an 'Execute' input and toggles its state (connected Boolean Toggle or persistent bool value)")]
    public static async Task<CallToolResult> ToggleSolverExecute()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var solver = document.Objects
                    .OfType<IGH_Component>()
                    .FirstOrDefault(c => c.Params.Input.Any(p => string.Equals(p.Name, "Execute", StringComparison.OrdinalIgnoreCase)));

                if (solver == null)
                {
                    return new { success = false, message = "No component with an 'Execute' input was found" };
                }

                var execParam = solver.Params.Input.First(p => string.Equals(p.Name, "Execute", StringComparison.OrdinalIgnoreCase));

                bool newState;
                if (execParam.SourceCount > 0)
                {
                    var source = execParam.Sources[0];
                    if (source is GH_BooleanToggle toggle)
                    {
                        toggle.Value = !toggle.Value;
                        toggle.ExpireSolution(true);
                        newState = toggle.Value;
                    }
                    else
                    {
                        return new { success = false, message = $"'Execute' is connected to unsupported source type: {source.GetType().Name}" };
                    }
                }
                else
                {
                    if (execParam is not Param_Boolean booleanParam)
                    {
                        return new { success = false, message = "'Execute' input is not a boolean parameter" };
                    }

                    bool currentState = false;
                    var volatileData = booleanParam.VolatileData;
                    if (volatileData.PathCount > 0 && volatileData.get_Branch(0).Count > 0)
                    {
                        if (volatileData.get_Branch(0)[0] is GH_Boolean b)
                        {
                            currentState = b.Value;
                        }
                    }

                    newState = !currentState;
                    booleanParam.PersistentData.Clear();
                    booleanParam.PersistentData.Append(new GH_Boolean(newState), new GH_Path(0));
                    solver.ExpireSolution(true);
                }

                return new { success = true, componentId = solver.InstanceGuid.ToString(), newState };
            });

            return result;
        }, nameof(ToggleSolverExecute));
    }

    [McpServerTool(Name = "Toggle_SolverReset")]
    [Description("Finds a component with a 'Reset' input and triggers a connected Button to simulate a reset pulse")]
    public static async Task<CallToolResult> ToggleSolverReset()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var solver = document.Objects
                    .OfType<IGH_Component>()
                    .FirstOrDefault(c => c.Params.Input.Any(p => string.Equals(p.Name, "Reset", StringComparison.OrdinalIgnoreCase)));

                if (solver == null)
                {
                    return new { success = false, message = "No component with a 'Reset' input was found" };
                }

                var resetParam = solver.Params.Input.First(p => string.Equals(p.Name, "Reset", StringComparison.OrdinalIgnoreCase));
                if (resetParam.SourceCount == 0)
                {
                    return new { success = false, message = "'Reset' input is not connected to a Button" };
                }

                var source = resetParam.Sources[0];
                if (source is GH_ButtonObject button)
                {
                    // Simulate a button click by toggling ButtonDown and expiring solution
                    button.ButtonDown = true;
                    button.ExpireSolution(true);
                    button.ButtonDown = false;
                    button.ExpireSolution(true);
                    return new { success = true, componentId = solver.InstanceGuid.ToString(), triggered = true };
                }

                return new { success = false, message = $"'Reset' is connected to unsupported source type: {source.GetType().Name}" };
            });

            return result;
        }, nameof(ToggleSolverReset));
    }
}

