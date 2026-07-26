using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Cassis.Extensions;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis.Tools;

/// <summary>
/// Tools for enabling/disabling components and toggling viewport preview.
/// </summary>
[McpServerToolType]
public static class ComponentStateTools
{
    [McpServerTool(Name = "Set_Component_Enabled")]
    [Description("Enables or disables a Grasshopper object by GUID (sets IGH_ActiveObject.Locked inverted). Disabled objects are skipped during solutions.")]
    public static async Task<CallToolResult> SetComponentEnabled(
        [Description("Object GUID on the canvas")] string componentId,
        [Description("True to enable, false to disable")] bool enabled)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var obj = FindDocumentObject(componentId);
                var previous = ComponentStateHelper.GetEnabled(obj);
                if (previous == enabled)
                {
                    return new
                    {
                        success = true,
                        componentId,
                        enabled,
                        changed = false,
                        message = "Already in requested state",
                    };
                }

                ComponentStateHelper.SetEnabled(obj, enabled);
                obj.OnPingDocument();
                Instances.ActiveCanvas?.Document?.NewSolution(false);

                return new
                {
                    success = true,
                    componentId,
                    enabled = ComponentStateHelper.GetEnabled(obj),
                    changed = true,
                };
            });

            return result;
        }, nameof(SetComponentEnabled));
    }

    [McpServerTool(Name = "Set_Component_Preview")]
    [Description("Turns viewport preview on or off for a Grasshopper object by GUID (sets IGH_PreviewObject.Hidden inverted).")]
    public static async Task<CallToolResult> SetComponentPreview(
        [Description("Object GUID on the canvas")] string componentId,
        [Description("True to show preview in Rhino viewports, false to hide")] bool enabled)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var obj = FindDocumentObject(componentId);
                if (!ComponentStateHelper.SupportsPreview(obj))
                {
                    throw new InvalidOperationException(
                        $"Object '{obj.NickName}' ({obj.GetType().Name}) does not support preview.");
                }

                var previous = ComponentStateHelper.GetPreviewEnabled(obj);
                if (previous == enabled)
                {
                    return new
                    {
                        success = true,
                        componentId,
                        previewEnabled = enabled,
                        changed = false,
                        message = "Already in requested state",
                    };
                }

                ComponentStateHelper.SetPreviewEnabled(obj, enabled);
                obj.OnPingDocument();

                return new
                {
                    success = true,
                    componentId,
                    previewEnabled = ComponentStateHelper.GetPreviewEnabled(obj),
                    changed = true,
                };
            });

            return result;
        }, nameof(SetComponentPreview));
    }

    [McpServerTool(Name = "Get_Component_State")]
    [Description("Returns enabled and preview state for a Grasshopper object by GUID.")]
    public static async Task<CallToolResult> GetComponentState(
        [Description("Object GUID on the canvas")] string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var obj = FindDocumentObject(componentId);

                return new
                {
                    success = true,
                    componentId,
                    nickName = obj.NickName ?? string.Empty,
                    typeName = obj.GetType().Name,
                    supportsEnable = ComponentStateHelper.SupportsEnable(obj),
                    enabled = ComponentStateHelper.SupportsEnable(obj)
                        ? ComponentStateHelper.GetEnabled(obj)
                        : (bool?)null,
                    supportsPreview = ComponentStateHelper.SupportsPreview(obj),
                    previewEnabled = ComponentStateHelper.SupportsPreview(obj)
                        ? ComponentStateHelper.GetPreviewEnabled(obj)
                        : (bool?)null,
                };
            });

            return result;
        }, nameof(GetComponentState));
    }

    private static IGH_DocumentObject FindDocumentObject(string componentId)
    {
        var document = Instances.ActiveCanvas?.Document
                       ?? throw new InvalidOperationException("No active Grasshopper document");

        if (!Guid.TryParse(componentId, out var guid))
        {
            throw new ArgumentException("Invalid component GUID format");
        }

        return document.FindObject(guid, false)
               ?? throw new InvalidOperationException($"Object {componentId} not found");
    }
}
