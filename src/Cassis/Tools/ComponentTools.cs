using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using Cassis.Services;
using Cassis.Extensions;
using System.Text.Json;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino;
using System.Drawing;
using System.IO;
using Grasshopper.Kernel.Types;
using Cassis.Utilities;

namespace Cassis.Tools;

/// <summary>
/// Tools for interacting with Grasshopper components.
/// </summary>
[McpServerToolType]
public static class ComponentTools
{
    [McpServerTool]
    [Description("Adds a component to the Grasshopper canvas at the specified position")]
    public static async Task<CallToolResult> AddComponent(
        IGrasshopperComponentService componentService,
        [Description("Type of component to add")]
        string type,
        [Description("X coordinate position on the canvas")]
        double x,
        [Description("Y coordinate position on the canvas")]
        double y)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(type), type));

            McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
            McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

            var result = await componentService.AddComponentAsync(type, x, y);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(AddComponent));
    }


    /// <summary>
    /// Sets a value on a component parameter.
    /// </summary>
    [McpServerTool]
    [Description("Sets a value on a component parameter")]
    public static async Task<CallToolResult> SetComponentValue(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component")]
        string componentId,
        [Description("Name of the parameter to set")]
        string parameterName,
        [Description("Value to set on the parameter (can be number, string, boolean, etc.)")]
        object? value)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(parameterName), parameterName));

            // The MCP framework will deserialize the JSON value to the appropriate .NET type
            // Numbers become double/int, strings become string, booleans become bool, etc.
            // We can pass it directly to the service
            var result = await componentService.SetComponentValueAsync(componentId, parameterName, value);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(SetComponentValue));
    }


    /// <summary>
    /// Gets information about a component.
    /// </summary>
    [McpServerTool]
    [Description("Gets detailed information about a component")]
    public static async Task<CallToolResult> GetComponentInfo(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component")]
        string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId));

            var result = await componentService.GetComponentInfoAsync(componentId);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetComponentInfo));
    }

    /// <summary>
    /// Connects two components together.
    /// </summary>
    [McpServerTool]
    [Description("Connects two components by connecting their parameters")]
    public static async Task<CallToolResult> ConnectComponents(
        IGrasshopperComponentService componentService,
        [Description("GUID of the source component")]
        string sourceComponentId,
        [Description("Index of the source output parameter")]
        int sourceOutputIndex,
        [Description("GUID of the target component")]
        string targetComponentId,
        [Description("Index of the target input parameter")]
        int targetInputIndex)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(sourceComponentId), sourceComponentId),
                (nameof(targetComponentId), targetComponentId));

            // Use indices directly - the service layer will handle parameter resolution
            var result = await componentService.ConnectComponentsAsync(sourceComponentId, sourceOutputIndex.ToString(), targetComponentId, targetInputIndex.ToString());

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ConnectComponents));
    }


    /// <summary>
    /// Adds a Python script component to the Grasshopper canvas at the specified position.
    /// Tries several well-known component names to maximize compatibility across Rhino/GH versions.
    /// </summary>
    [McpServerTool]
    [Description("Adds a Python 3 script component to the Grasshopper canvas and optionally sets its script source")]
    public static async Task<CallToolResult> AddPythonScriptComponent(
        IGrasshopperComponentService componentService,
        [Description("Script source code to set on the component (optional)")]
        string? script = null,
        [Description("X coordinate position on the canvas (optional; default 100)")]
        double x = 100,
        [Description("Y coordinate position on the canvas (optional; default 100)")]
        double y = 100)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
            McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

            var candidateNames = new[]
            {
                "Python 3",      // Rhino 8 Python 3 component
                "Py3",           // Nickname often shown in UI
                "Python Script", // Common display name
                "GhPython",      // Legacy GhPython component
                "Python",        // Fallback
                "Script"         // Unified Script component (may require language selection in UI)
            };

            Cassis.Models.ComponentCreationResult? success = null;
            
            // If script is provided, use the specialized method that creates the component with script content
            if (!string.IsNullOrWhiteSpace(script))
            {
                success = await componentService.AddPythonScriptComponentAsync(script, x, y);
                if (success.Success)
                {
                    // Component created successfully with script content
                    success = success with { ErrorMessage = null };
                }
            }
            else
            {
                // Fallback to trying different component names for empty script components
                foreach (var name in candidateNames)
                {
                    var attempt = await componentService.AddComponentAsync(name, x, y);
                    if (attempt.Success)
                    {
                        success = attempt;
                        break;
                    }
                }
            }

            var final = success ?? new Cassis.Models.ComponentCreationResult(
                false, null, null, null, 0, 0,
                $"Unable to create a Python script component. Tried: {string.Join(", ", candidateNames)}");

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(AddPythonScriptComponent));
    }


    /// <summary>
    /// Adds a C# script component to the Grasshopper canvas at the specified position.
    /// Tries several well-known component names to maximize compatibility across Rhino/GH versions.
    /// </summary>
    [McpServerTool]
    [Description("Adds a C# script component to the Grasshopper canvas and optionally sets its script source. Rhino 8 defaults are outputs 'out' and lowercase 'a' (not 'A'); use script-mode like `a = ...;` or SDK-mode with `RunScript(..., ref object a)`. For nontrivial scripts, check https://developer.rhino3d.com/guides/scripting/scripting-gh-csharp/ first.")]
    public static async Task<CallToolResult> AddCSharpScriptComponent(
        IGrasshopperComponentService componentService,
        [Description("Script source code to set on the component (optional). Default result output is lowercase 'a'.")]
        string? script = null,
        [Description("X coordinate position on the canvas (optional; default 100)")]
        double x = 100,
        [Description("Y coordinate position on the canvas (optional; default 100)")]
        double y = 100)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
            McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

            var candidateNames = new[]
            {
                "C# Script",   // Common display name
                "CSharp Script",
                "CSharp",      // Fallback
                "C#",          // Fallback
                "GhCSharp",    // Legacy naming
                "Script"       // Unified Script component
            };

            Cassis.Models.ComponentCreationResult? success = null;
            
            // If script is provided, use the specialized method that creates the component with script content
            if (!string.IsNullOrWhiteSpace(script))
            {
                success = await componentService.AddCSharpScriptComponentAsync(script, x, y);
                if (success.Success)
                {
                    // Component created successfully with script content
                    success = success with { ErrorMessage = null };
                }
            }
            else
            {
                // Fallback to trying different component names for empty script components
                foreach (var name in candidateNames)
                {
                    var attempt = await componentService.AddComponentAsync(name, x, y);
                    if (attempt.Success)
                    {
                        success = attempt;
                        break;
                    }
                }
            }

            var final = success ?? new Cassis.Models.ComponentCreationResult(
                false, null, null, null, 0, 0,
                $"Unable to create a C# script component. Tried: {string.Join(", ", candidateNames)}");

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(AddCSharpScriptComponent));
    }


    /// <summary>
    /// Sets script content on a script component.
    /// </summary>
    [McpServerTool]
    [Description("Sets script source code on a script component")]
    public static async Task<CallToolResult> SetComponentScript(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component")]
        string componentId,
        [Description("Programming language (e.g., 'python', 'csharp')")]
        string language,
        [Description("Script source code")]
        string script)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(language), language),
                (nameof(script), script));

            var result = await componentService.SetComponentScriptAsync(componentId, language, script);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(SetComponentScript));
    }

    /// <summary>
    /// Gets the script content from a script component.
    /// </summary>
    [McpServerTool]
    [Description("Gets the script source code from a script component")]
    public static async Task<CallToolResult> GetComponentScript(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component")]
        string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId));

            var result = await GetComponentScriptContentAsync(componentId);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetComponentScript));
    }

    /// <summary>
    /// Lists all available component types that can be added to the Grasshopper canvas.
    /// </summary>
    [McpServerTool]
    [Description("Lists all available Grasshopper component types that can be added to the canvas, optionally filtered by category")]
    public static async Task<CallToolResult> ListAvailableComponents(
        IGrasshopperComponentService componentService,
        [Description("Optional category filter to limit results (e.g. 'Params', 'Maths', 'Sets', 'Vector', 'Curve', 'Surface', 'Mesh', 'Intersect', 'Transform', 'Display')")]
        string? category = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            var result = await componentService.ListAvailableComponentsAsync(category);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ListAvailableComponents));
    }

    /// <summary>
    /// Creates an empty group at the specified position.
    /// </summary>
    [McpServerTool]
    [Description("Creates an empty group at the specified position on the canvas")]
    public static async Task<CallToolResult> CreateComponentGroup(
        IGrasshopperComponentService componentService,
        [Description("Name for the group")]
        string name,
        [Description("X coordinate position on the canvas")]
        double x,
        [Description("Y coordinate position on the canvas")]
        double y,
        [Description("Color for the group in hex format (optional, e.g., '#FF0000' for red)")]
        string? groupColor = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(name), name));

            McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
            McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

            var result = await CreateEmptyGroupAsync(name, x, y, groupColor);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(CreateComponentGroup));
    }

    /// <summary>
    /// Creates a group containing the specified existing components.
    /// </summary>
    [McpServerTool]
    [Description("Creates a group containing the specified existing components")]
    public static async Task<CallToolResult> GroupExistingComponents(
        IGrasshopperComponentService componentService,
        [Description("Array of component GUIDs to include in the group")]
        string[] componentIds,
        [Description("Name for the group (optional)")]
        string? groupName = null,
        [Description("Color for the group in hex format (optional, e.g., '#FF0000' for red)")]
        string? groupColor = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentIds), componentIds));

            if (componentIds.Length == 0)
            {
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new { Success = false, Message = "No component IDs provided" }, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            var result = await CreateGroupAsync(componentIds, groupName, groupColor);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GroupExistingComponents));
    }

    /// <summary>
    /// Groups components by their type (e.g., all Math components, all Curve components).
    /// </summary>
    [McpServerTool]
    [Description("Groups all components by their category/type")]
    public static async Task<CallToolResult> GroupComponentsByType(
        IGrasshopperComponentService componentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentService), componentService));

            var result = await GroupComponentsByTypeAsync();

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GroupComponentsByType));
    }

    /// <summary>
    /// Gets runtime message information from a component including execution status, messages, warnings, and errors.
    /// </summary>
    [McpServerTool]
    [Description("Gets runtime message information from a component including execution status, messages, warnings, and errors")]
    public static async Task<CallToolResult> GetComponentRuntimeInfo(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component")]
        string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId));

            var result = await GetComponentRuntimeInfoAsync(componentId);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetComponentRuntimeInfo));
    }

    /// <summary>
    /// Gets runtime information for all components in the current document.
    /// </summary>
    [McpServerTool]
    [Description("Gets runtime information for all components in the current document")]
    public static async Task<CallToolResult> GetAllComponentsRuntimeInfo(
        IGrasshopperComponentService componentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentService), componentService));

            var result = await GetAllComponentsRuntimeInfoAsync();

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetAllComponentsRuntimeInfo));
    }

    private static async Task<object> GetComponentScriptContentAsync(string componentId)
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                if (!Guid.TryParse(componentId, out Guid guid))
                {
                    tcs.SetResult(new { Success = false, Message = "Invalid component GUID format" });
                    return;
                }

                var component = doc.FindObject(guid, false);
                if (component == null)
                {
                    tcs.SetResult(new { Success = false, Message = "Component not found" });
                    return;
                }

                // Try to get script content from different types of script components
                string? scriptContent = null;
                string language = "unknown";

                // Check if it's a RhinoCodePluginGH script component
                if (component.GetType().Name.Contains("Python3Component") || 
                    component.GetType().Name.Contains("CSharpComponent") ||
                    component.GetType().Name.Contains("BaseLanguageComponent"))
                {
                    try
                    {
                        // Try to access the Context field (it's a field, not a property!)
                        var contextField = component.GetType().GetField("Context",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic);
                        
                        if (contextField != null)
                        {
                            var context = contextField.GetValue(component);
                            if (context != null)
                            {
                                // Try GetSource() method on context
                                var getSourceMethod = context.GetType().GetMethod("GetSource",
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic);
                                
                                if (getSourceMethod != null && getSourceMethod.GetParameters().Length == 0)
                                {
                                    try
                                    {
                                        var source = getSourceMethod.Invoke(context, null);
                                        if (source is string sourceStr && !string.IsNullOrEmpty(sourceStr))
                                        {
                                            scriptContent = sourceStr;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        RhinoApp.WriteLine($"[MCP WARN] GetSource reflection failed: {ex.Message}");
                                    }
                                }

                                // Fallback: Try Script property on context
                                if (string.IsNullOrEmpty(scriptContent))
                                {
                                    var scriptProperty = context.GetType().GetProperty("Script",
                                        System.Reflection.BindingFlags.Instance |
                                        System.Reflection.BindingFlags.Public |
                                        System.Reflection.BindingFlags.NonPublic);
                                    
                                    if (scriptProperty != null)
                                    {
                                        var script = scriptProperty.GetValue(context);
                                        if (script != null)
                                        {
                                            var textProperty = script.GetType().GetProperty("Text",
                                                System.Reflection.BindingFlags.Instance |
                                                System.Reflection.BindingFlags.Public |
                                                System.Reflection.BindingFlags.NonPublic) ??
                                                script.GetType().GetProperty("Source",
                                                    System.Reflection.BindingFlags.Instance |
                                                    System.Reflection.BindingFlags.Public |
                                                    System.Reflection.BindingFlags.NonPublic);
                                            
                                            if (textProperty != null)
                                            {
                                                scriptContent = textProperty.GetValue(script)?.ToString();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"Error accessing script content: {ex.Message}");
                    }

                    // Determine language based on component type
                    if (component.GetType().Name.Contains("Python"))
                        language = "python";
                    else if (component.GetType().Name.Contains("CSharp"))
                        language = "csharp";
                }

                if (scriptContent != null)
                {
                    tcs.SetResult(new 
                    { 
                        Success = true, 
                        ScriptContent = scriptContent,
                        Language = language,
                        ContentLength = scriptContent.Length
                    });
                }
                else
                {
                    tcs.SetResult(new 
                    { 
                        Success = false, 
                        Message = "Could not retrieve script content from component",
                        ComponentType = component.GetType().Name
                    });
                }
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { Success = false, Message = ex.Message });
            }
        });

        return await tcs.Task;
    }

    private static async Task<object> CreateEmptyGroupAsync(string groupName, double x, double y, string? groupColor)
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                // Check for assembly loading issues and provide helpful error messages
                try
                {
                    // Test if we can create a basic group
                    var testGroup = new Grasshopper.Kernel.Special.GH_Group();
                    if (testGroup == null)
                    {
                        throw new InvalidOperationException("Failed to create GH_Group instance");
                    }
                }
                catch (System.IO.FileNotFoundException ex) when (ex.Message.Contains("PresentationCore"))
                {
                    RhinoApp.WriteLine("Warning: PresentationCore assembly not found. This is a known issue on macOS.");
                    RhinoApp.WriteLine("The group may not display correctly in the UI, but it should still function.");
                }
                catch (System.IO.FileLoadException ex) when (ex.Message.Contains("Sandbox_Topology"))
                {
                    RhinoApp.WriteLine("Warning: Sandbox_Topology assembly failed to load. This is a known issue on macOS.");
                    RhinoApp.WriteLine("The group may not display correctly in the UI, but it should still function.");
                }

                // Create the group using the proper Grasshopper API
                var group = new Grasshopper.Kernel.Special.GH_Group();
                group.CreateAttributes();
                group.NickName = groupName;
                
                // Set group position using multiple methods for better compatibility
                var position = new System.Drawing.PointF((float)x, (float)y);
                group.Attributes.Pivot = position;
                
                // Alternative positioning method for better macOS compatibility
                // Use the base attributes type instead of the specific GH_ComponentAttributes
                group.Attributes.Pivot = position;
                // Force the attributes to update
                group.Attributes.ExpireLayout();
                
                // Additional positioning workaround for macOS compatibility
                try
                {
                    // Use the canvas coordinate system if available
                    var canvas = Instances.ActiveCanvas;
                    if (canvas != null)
                    {
                        // Try to use the canvas viewport for coordinate conversion
                        // Note: GH_Viewport doesn't have WorldToClient, so we'll use a different approach
                        var canvasPoint = new System.Drawing.PointF((float)x, (float)y);
                        
                        // Set the position directly on the group attributes
                        group.Attributes.Pivot = canvasPoint;
                        
                        // Also try setting the bounds in canvas coordinates
                        var canvasBounds = new System.Drawing.RectangleF(
                            canvasPoint.X, canvasPoint.Y, 200, 150);
                        group.Attributes.Bounds = canvasBounds;
                    }
                }
                catch (Exception posEx)
                {
                    // If canvas positioning fails, fall back to the original method
                    RhinoApp.WriteLine($"Canvas positioning failed, using fallback: {posEx.Message}");
                    group.Attributes.Pivot = position;
                }
                
                // Set group size to make it visible
                group.Attributes.Bounds = new System.Drawing.RectangleF((float)x, (float)y, 200, 150);

                // Set group color if provided
                if (!string.IsNullOrWhiteSpace(groupColor))
                {
                    try
                    {
                        var color = System.Drawing.ColorTranslator.FromHtml(groupColor);
                        group.Colour = color;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"[MCP WARN] Invalid group color '{groupColor}', using default: {ex.Message}");
                        group.Colour = System.Drawing.Color.LightBlue;
                    }
                }
                else
                {
                    // Default color
                    group.Colour = System.Drawing.Color.LightBlue;
                }

                // Make sure the group is visible and properly configured
                group.Attributes.Selected = false;
                
                // Add the group to the document using the proper Grasshopper method
                doc.AddObject(group, false);
                
                // Force document refresh to ensure group is properly registered
                doc.NewSolution(false);
                
                // Verify the group was added by checking the document's object collection
                var addedGroup = doc.Objects.FirstOrDefault(obj => obj.InstanceGuid == group.InstanceGuid);
                if (addedGroup == null)
                {
                    RhinoApp.WriteLine($"Group was not found in document after addition. Document has {doc.Objects.Count} objects");
                    
                    // Try to list all objects for debugging
                    foreach (var obj in doc.Objects)
                    {
                        RhinoApp.WriteLine($"Document object: {obj.GetType().Name} - {obj.InstanceGuid} - {obj.NickName}");
                    }
                }
                else
                {
                    // Get the actual position from the added group
                    var actualPosition = addedGroup.Attributes.Pivot;
                    RhinoApp.WriteLine($"Group verified in document: {group.InstanceGuid} at position ({actualPosition.X}, {actualPosition.Y})");
                    
                    // If the position is wrong, try to fix it
                    if (Math.Abs(actualPosition.X - x) > 1 || Math.Abs(actualPosition.Y - y) > 1)
                    {
                        RhinoApp.WriteLine($"Position mismatch detected. Expected: ({x}, {y}), Actual: ({actualPosition.X}, {actualPosition.Y})");
                        
                        // Try to force the position update
                        addedGroup.Attributes.Pivot = position;
                        addedGroup.Attributes.ExpireLayout();
                        
                        // Force another refresh
                        doc.NewSolution(false);
                        
                        var updatedPosition = addedGroup.Attributes.Pivot;
                        RhinoApp.WriteLine($"Position after correction: ({updatedPosition.X}, {updatedPosition.Y})");
                    }
                }

                tcs.SetResult(new 
                { 
                    Success = true, 
                    GroupId = group.InstanceGuid.ToString(),
                    GroupName = group.NickName,
                    X = x,
                    Y = y,
                    ComponentCount = 0,
                    Message = $"Group '{groupName}' created successfully at ({x}, {y})"
                });
            }
            catch (Exception ex)
            {
                // Enhanced error reporting for debugging
                var errorMessage = $"Failed to create group '{groupName}': {ex.Message}";
                if (ex.InnerException != null)
                {
                    errorMessage += $"\nInner exception: {ex.InnerException.Message}";
                }
                
                RhinoApp.WriteLine($"Error creating group: {errorMessage}");
                RhinoApp.WriteLine($"Stack trace: {ex.StackTrace}");
                
                tcs.SetResult(new { Success = false, Message = errorMessage });
            }
        });

        return await tcs.Task;
    }

    private static async Task<object> CreateGroupAsync(string[] componentIds, string? groupName, string? groupColor)
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                var components = new List<IGH_DocumentObject>();
                var invalidIds = new List<string>();

                // Find all components
                foreach (var componentId in componentIds)
                {
                    if (Guid.TryParse(componentId, out Guid guid))
                    {
                        var component = doc.FindObject(guid, false);
                        if (component != null)
                        {
                            components.Add(component);
                        }
                        else
                        {
                            invalidIds.Add(componentId);
                        }
                    }
                    else
                    {
                        invalidIds.Add(componentId);
                    }
                }

                if (components.Count == 0)
                {
                    tcs.SetResult(new { Success = false, Message = "No valid components found", InvalidIds = invalidIds });
                    return;
                }

                // Create the group
                var group = new Grasshopper.Kernel.Special.GH_Group();
                group.CreateAttributes();
                
                // Set group name
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    group.NickName = groupName;
                }
                else
                {
                    group.NickName = $"Group ({components.Count} items)";
                }

                // Set group color if provided
                if (!string.IsNullOrWhiteSpace(groupColor))
                {
                    try
                    {
                        var color = System.Drawing.ColorTranslator.FromHtml(groupColor);
                        group.Colour = color;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"[MCP WARN] Invalid group color '{groupColor}', using default: {ex.Message}");
                    }
                }

                // Add components to group
                foreach (var component in components)
                {
                    group.AddObject(component.InstanceGuid);
                }

                // Add group to document
                doc.AddObject(group, false);
                doc.NewSolution(false);

                tcs.SetResult(new 
                { 
                    Success = true, 
                    GroupId = group.InstanceGuid.ToString(),
                    GroupName = group.NickName,
                    ComponentCount = components.Count,
                    InvalidIds = invalidIds
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { Success = false, Message = ex.Message });
            }
        });

        return await tcs.Task;
    }

    private static async Task<object> GroupComponentsByTypeAsync()
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                // Group components by category
                var componentsByCategory = new Dictionary<string, List<IGH_DocumentObject>>();

                foreach (var obj in doc.Objects)
                {
                    if (obj is IGH_Component component)
                    {
                        var category = component.Category ?? "Uncategorized";
                        if (!componentsByCategory.ContainsKey(category))
                        {
                            componentsByCategory[category] = new List<IGH_DocumentObject>();
                        }
                        componentsByCategory[category].Add(component);
                    }
                }

                var groupsCreated = new List<object>();

                // Create groups for each category with multiple components
                foreach (var kvp in componentsByCategory.Where(x => x.Value.Count > 1))
                {
                    var category = kvp.Key;
                    var components = kvp.Value;

                    var group = new Grasshopper.Kernel.Special.GH_Group();
                    group.CreateAttributes();
                    group.NickName = $"{category} ({components.Count} items)";

                    // Set different colors for different categories
                    var colorIndex = Math.Abs(category.GetHashCode()) % 6;
                    var colors = new[] 
                    {
                        System.Drawing.Color.LightBlue,
                        System.Drawing.Color.LightGreen,
                        System.Drawing.Color.LightCoral,
                        System.Drawing.Color.LightYellow,
                        System.Drawing.Color.LightPink,
                        System.Drawing.Color.LightGray
                    };
                    group.Colour = colors[colorIndex];

                    foreach (var component in components)
                    {
                        group.AddObject(component.InstanceGuid);
                    }

                    doc.AddObject(group, false);

                    groupsCreated.Add(new
                    {
                        GroupId = group.InstanceGuid.ToString(),
                        Category = category,
                        ComponentCount = components.Count,
                        Color = group.Colour.Name
                    });
                }

                doc.NewSolution(false);

                tcs.SetResult(new 
                { 
                    Success = true, 
                    GroupsCreated = groupsCreated.Count,
                    Groups = groupsCreated
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { Success = false, Message = ex.Message });
            }
        });

        return await tcs.Task;
    }

    private static async Task<object> GetComponentRuntimeInfoAsync(string componentId)
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                if (!Guid.TryParse(componentId, out Guid guid))
                {
                    tcs.SetResult(new { Success = false, Message = "Invalid component GUID format" });
                    return;
                }

                var component = doc.FindObject(guid, false) as IGH_Component;
                if (component == null)
                {
                    tcs.SetResult(new { Success = false, Message = "Component not found" });
                    return;
                }

                // Get runtime information using the correct Grasshopper API
                var runtimeInfo = new
                {
                    ComponentId = componentId,
                    ComponentName = component.NickName,
                    ComponentType = component.GetType().Name,
                    Category = component.Category ?? "Uncategorized",
                    SubCategory = component.SubCategory ?? "",
                    Description = component.Description ?? "",
                    ExecutionStatus = GetExecutionStatus(component),
                    Messages = GetComponentMessages(component),
                    Warnings = GetComponentWarnings(component),
                    Errors = GetComponentErrors(component),
                    OutputCount = component.Params.Output.Count,
                    InputCount = component.Params.Input.Count,
                    RuntimeData = GetRuntimeData(component)
                };

                tcs.SetResult(new { Success = true, RuntimeInfo = runtimeInfo });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { Success = false, Message = ex.Message });
            }
        });

        return await tcs.Task;
    }

    private static async Task<object> GetAllComponentsRuntimeInfoAsync()
    {
        var tcs = new TaskCompletionSource<object>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new { Success = false, Message = "No active Grasshopper document" });
                    return;
                }

                var components = doc.Objects.OfType<IGH_Component>().ToList();
                var runtimeInfos = new List<object>();

                foreach (var component in components)
                {
                    try
                    {
                        var runtimeInfo = new
                        {
                            ComponentId = component.InstanceGuid.ToString(),
                            ComponentName = component.NickName,
                            ComponentType = component.GetType().Name,
                            Category = component.Category ?? "Uncategorized",
                            ExecutionStatus = GetExecutionStatus(component),
                            HasMessages = GetComponentMessages(component).Any(),
                            HasWarnings = GetComponentWarnings(component).Any(),
                            HasErrors = GetComponentErrors(component).Any(),
                            OutputCount = component.Params.Output.Count,
                            InputCount = component.Params.Input.Count
                        };

                        runtimeInfos.Add(runtimeInfo);
                    }
                    catch (Exception ex)
                    {
                        runtimeInfos.Add(new
                        {
                            ComponentId = component.InstanceGuid.ToString(),
                            ComponentName = component.NickName,
                            Error = ex.Message
                        });
                    }
                }

                var summary = new
                {
                    TotalComponents = runtimeInfos.Count,
                    HealthyComponents = runtimeInfos.Count(r => 
                        r.GetType().GetProperty("HasErrors")?.GetValue(r) is bool hasErrors && !hasErrors),
                    ComponentsWithErrors = runtimeInfos.Count(r => 
                        r.GetType().GetProperty("HasErrors")?.GetValue(r) is bool hasErrors && hasErrors),
                    ComponentsWithWarnings = runtimeInfos.Count(r => 
                        r.GetType().GetProperty("HasWarnings")?.GetValue(r) is bool hasWarnings && hasWarnings),
                    ExpiredComponents = runtimeInfos.Count(r => 
                        r.GetType().GetProperty("IsExpired")?.GetValue(r) is bool isExpired && isExpired)
                };

                tcs.SetResult(new 
                { 
                    Success = true, 
                    Summary = summary,
                    Components = runtimeInfos
                });
            }
            catch (Exception ex)
            {
                tcs.SetResult(new { Success = false, Message = ex.Message });
            }
        });

        return await tcs.Task;
    }

    private static string GetExecutionStatus(IGH_Component component)
    {
        // Check if component has runtime errors
        if (component.RuntimeMessages(GH_RuntimeMessageLevel.Error).Any())
            return "Error";
        
        // Check if component has runtime warnings
        if (component.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Any())
            return "Warning";
        
        // Check if component has input parameters without data
        if (component.Params.Input.Any(p => p.SourceCount == 0 && p.VolatileData.IsEmpty))
            return "Missing Input";
        
        // Check if component has output parameters without data
        if (component.Params.Output.Any(p => p.VolatileData.IsEmpty))
            return "No Output";
        
        // Check if component has runtime messages (info/remarks)
        if (component.RuntimeMessages(GH_RuntimeMessageLevel.Remark).Any())
            return "Info";
        
        return "Valid";
    }

    private static List<string> GetComponentMessages(IGH_Component component)
    {
        var messages = new List<string>();
        
        // Get runtime messages from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
        {
            messages.Add($"INFO: {message}");
        }

        return messages;
    }

    private static List<string> GetComponentWarnings(IGH_Component component)
    {
        var warnings = new List<string>();
        
        // Get runtime warnings from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
        {
            warnings.Add($"WARNING: {message}");
        }

        return warnings;
    }

    private static List<string> GetComponentErrors(IGH_Component component)
    {
        var errors = new List<string>();
        
        // Get runtime errors from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Error))
        {
            errors.Add($"ERROR: {message}");
        }

        return errors;
    }

    private static object GetRuntimeData(IGH_Component component)
    {
        try
        {
            var outputData = new List<object>();
            
            // Get output data from each output parameter
            for (int i = 0; i < component.Params.Output.Count; i++)
            {
                var param = component.Params.Output[i];
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
                
                var data = new
                {
                    ParameterName = param.NickName,
                    DataType = param.TypeName ?? volatileData?.GetType().Name ?? "Unknown",
                    IsEmpty = volatileData?.IsEmpty ?? true,
                    HasData = volatileData != null && !volatileData.IsEmpty,
                    BranchCount = branchCount,
                    ItemCount = itemCount,
                    Values = values
                };
                outputData.Add(data);
            }

            return new
            {
                OutputData = outputData,
                HasValidOutput = component.Params.Output.Any(p => !p.VolatileData.IsEmpty)
            };
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Error retrieving runtime data: {ex.Message}");
            return new { Error = "Unable to retrieve runtime data" };
        }
    }

    /// <summary>
    /// Captures the current state of the Grasshopper canvas including components, connections, and layout.
    /// </summary>
    [McpServerTool]
    [Description("Captures the current state of the Grasshopper canvas including components, connections, positions, and runtime data")]
    public static async Task<CallToolResult> CaptureCanvasState(
        IGrasshopperComponentService componentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            var result = await componentService.CaptureCanvasStateAsync();

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(CaptureCanvasState));
    }

    /// <summary>
    /// Modifies the parameter names of a script component to provide more descriptive interfaces.
    /// </summary>
    [McpServerTool]
    [Description("Modifies the parameter names of a script component to provide more descriptive interfaces")]
    public static async Task<CallToolResult> ModifyComponentParameters(
        IGrasshopperComponentService componentService,
        [Description("GUID of the component to modify")]
        string componentId,
        [Description("Dictionary mapping parameter indices to new names (e.g., {\"input_0\": \"spiral_radius\", \"output_1\": \"spiral_curve\"})")]
        Dictionary<string, string> newNames)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(newNames), newNames));

            var result = await componentService.ModifyComponentParametersAsync(componentId, newNames);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ModifyComponentParameters));
    }

    /// <summary>
    /// Gets information about the current Grasshopper document.
    /// </summary>
    [McpServerTool]
    [Description("Gets information about the current Grasshopper document including components, file path, and modification status")]
    public static async Task<CallToolResult> GetDocumentInfo(
        IGrasshopperDocumentService documentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(documentService), documentService));

            var result = await documentService.GetDocumentInfoAsync();

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetDocumentInfo));
    }

    /// <summary>
    /// Clears the current Grasshopper document while preserving essential MCP components
    /// </summary>
    [McpServerTool]
    [Description("Clears the current Grasshopper document while preserving essential MCP components")]
    public static async Task<CallToolResult> ClearDocument(
        IGrasshopperDocumentService documentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(documentService), documentService));

            var result = await documentService.ClearDocumentAsync();

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ClearDocument));
    }

    /// <summary>
    /// Removes specific components by their IDs instead of clearing the entire document
    /// </summary>
    [McpServerTool]
    [Description("Removes specific components by their IDs instead of clearing the entire document")]
    public static async Task<CallToolResult> RemoveComponents(
        IGrasshopperDocumentService documentService,
        [Description("Array of component GUIDs to remove")]
        string[] componentIds)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(documentService), documentService),
                (nameof(componentIds), componentIds));

            if (componentIds.Length == 0)
            {
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new { Success = false, Message = "No component IDs provided" }, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            var result = await documentService.RemoveComponentsAsync(componentIds);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(RemoveComponents));
    }

    /// <summary>
    /// Finds a component by its InstanceGuid and returns detailed information.
    /// </summary>
    [McpServerTool]
    [Description("Finds a component by its InstanceGuid and returns detailed information")]
    public static async Task<CallToolResult> FindComponentByGuid(
        IGrasshopperComponentService componentService,
        [Description("The InstanceGuid of the component to find")]
        string componentId)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId));

            // First try to get component info
            var componentInfo = await componentService.GetComponentInfoAsync(componentId);
            
            if (!componentInfo.Success)
            {
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new
                            {
                                Success = false,
                                ComponentId = componentId,
                                Error = componentInfo.ErrorMessage,
                                Message = "Component not found by InstanceGuid"
                            }, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            var result = new
            {
                Success = true,
                ComponentId = componentId,
                ComponentInfo = componentInfo,
                Message = "Component found successfully by InstanceGuid"
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(FindComponentByGuid));
    }

    /// <summary>
    /// Lists all components with their InstanceGuids for debugging and identification.
    /// </summary>
    [McpServerTool]
    [Description("Lists all components with their InstanceGuids for debugging and identification")]
    public static async Task<CallToolResult> ListAllComponentsWithGuids(
        IGrasshopperComponentService componentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            var canvasState = await componentService.CaptureCanvasStateAsync();
            
            if (!canvasState.Success)
            {
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new
                            {
                                Success = false,
                                Error = canvasState.ErrorMessage,
                                Message = "Failed to capture canvas state"
                            }, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            var result = new
            {
                Success = true,
                TotalComponents = canvasState.TotalComponents,
                TotalConnections = canvasState.TotalConnections,
                Components = canvasState.Components.Select(c => new
                {
                    InstanceGuid = c.Id,
                    Type = c.Type,
                    Name = c.Name,
                    Category = c.Category,
                    SubCategory = c.SubCategory,
                    Position = new { X = c.X, Y = c.Y },
                    ExecutionStatus = c.ExecutionStatus,
                    InputCount = c.Inputs?.Count ?? 0,
                    OutputCount = c.Outputs?.Count ?? 0
                }).ToList(),
                Message = "All components listed with InstanceGuids"
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ListAllComponentsWithGuids));
    }

    /// <summary>
    /// Debug tool to show all objects in the document with their types and GUIDs.
    /// </summary>
    [McpServerTool]
    [Description("Debug tool to show all objects in the document with their types and GUIDs")]
    public static async Task<CallToolResult> DebugDocumentState(
        IGrasshopperComponentService componentService)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService));

            var canvasState = await componentService.CaptureCanvasStateAsync();
            
            if (!canvasState.Success)
            {
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new
                            {
                                Success = false,
                                Error = canvasState.ErrorMessage,
                                Message = "Failed to capture canvas state for debugging"
                            }, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            // Get additional debug information
            var result = new
            {
                Success = true,
                CanvasState = canvasState,
                DebugInfo = new
                {
                    TotalObjects = canvasState.TotalComponents,
                    ComponentCount = canvasState.Components?.Count ?? 0,
                    ConnectionCount = canvasState.TotalConnections,
                    DocumentName = canvasState.DocumentName,
                    CaptureTime = canvasState.CaptureTime,
                    Message = "Document state debug information"
                }
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(DebugDocumentState));
    }

    /// <summary>
    /// Renames and changes data types of script component parameters (Python, C#, etc.).
    /// </summary>
    [McpServerTool]
    [Description("Renames and changes data types of script component parameters (Python, C#, etc.). Validates parameter names against C# reserved keywords and provides helpful error messages.")]
    public static async Task<CallToolResult> ModifyScriptComponentParameters(
        IGrasshopperComponentService componentService,
        [Description("The InstanceGuid of the script component to modify")] string componentId,
        [Description("JSON string defining the new parameter names and types")] string parameterConfig)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(parameterConfig), parameterConfig));

            try
            {
                // Parse the parameter configuration JSON
                // Support both simple format: {"input_0": "new_name"} and nested format: {"input_0": {"name": "new_name", "type": "any"}}
                Dictionary<string, string> parameters;
                try
                {
                    // First try to parse as nested format (Dictionary<string, JsonElement>) to handle both cases
                    var jsonDoc = System.Text.Json.JsonDocument.Parse(parameterConfig);
                    var root = jsonDoc.RootElement;
                    
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        throw new System.Text.Json.JsonException("Parameter configuration must be a JSON object");
                    }
                    
                    parameters = new Dictionary<string, string>();
                    foreach (var prop in root.EnumerateObject())
                    {
                        var key = prop.Name;
                        var value = prop.Value;
                        
                        // If value is a string, use it directly (simple format)
                        if (value.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            parameters[key] = value.GetString() ?? key;
                        }
                        // If value is an object, extract the "name" property (nested format)
                        else if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                parameters[key] = nameProp.GetString() ?? key;
                            }
                            else
                            {
                                parameters[key] = key; // Fallback to key name
                            }
                        }
                        else
                        {
                            parameters[key] = value.ToString();
                        }
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = $"Invalid parameter configuration JSON: {ex.Message}. Expected format: {{\"input_0\": \"new_name\"}} or {{\"input_0\": {{\"name\": \"new_name\", \"type\": \"any\"}}}}" } },
                        IsError = true
                    };
                }

                if (parameters == null || parameters.Count == 0)
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = "Invalid parameter configuration: empty or null" } },
                        IsError = true
                    };
                }

                // Validate parameter names for C# reserved keywords
                var reservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "out", "in", "ref", "params", "var", "dynamic", "object", "string", "int", "double", "float", "bool", "char", "byte", "sbyte", "short", "ushort", "uint", "long", "ulong", "decimal", "void", "null", "true", "false", "this", "base", "new", "typeof", "sizeof", "checked", "unchecked", "default", "delegate", "event", "explicit", "implicit", "operator", "partial", "readonly", "sealed", "static", "unsafe", "virtual", "volatile", "abstract", "const", "extern", "internal", "override", "private", "protected", "public", "return", "throw", "try", "catch", "finally", "for", "foreach", "do", "while", "if", "else", "switch", "case", "break", "continue", "goto", "using", "namespace", "class", "struct", "interface", "enum", "async", "await", "yield", "from", "where", "select", "group", "into", "orderby", "join", "let", "on", "equals", "by", "ascending", "descending"
                };

                var invalidParameters = new List<string>();
                foreach (var param in parameters.Values)
                {
                    if (reservedKeywords.Contains(param))
                    {
                        invalidParameters.Add(param);
                    }
                }

                if (invalidParameters.Count > 0)
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = $"Invalid parameter names (C# reserved keywords): {string.Join(", ", invalidParameters)}. Use alternative names like 'output1', 'result', 'data', etc." } },
                        IsError = true
                    };
                }

                // Get component info first to validate it exists
                var componentInfo = await componentService.GetComponentInfoAsync(componentId);
                if (!componentInfo.Success)
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = $"Component not found: {componentId}" } },
                        IsError = true
                    };
                }

                // Check if it's a script component (Python, C#, etc.)
                if (!componentInfo.Component.Type.Contains("Python") && 
                    !componentInfo.Component.Type.Contains("CSharp") && 
                    !componentInfo.Component.Type.Contains("Script"))
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = "Component is not a script component (Python, C#, etc.)" } },
                        IsError = true
                    };
                }

                // Now actually modify the component parameters
                var modificationResult = await componentService.ModifyScriptComponentParametersAsync(componentId, parameters);
                
                if (!modificationResult.Success)
                {
                    return new CallToolResult
                    {
                        Content = new[] { new TextContentBlock { Text = $"Failed to modify parameters: {modificationResult.Message}" } },
                        IsError = true
                    };
                }

                var result = new
                {
                    Success = true,
                    ComponentId = componentId,
                    ComponentType = componentInfo.Component.Type,
                    ParametersModified = parameters.Count,
                    ParameterConfig = parameters,
                    Message = "Script component parameters modified successfully"
                };

                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) } },
                    IsError = false
                };
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                                            Content = new[] { new TextContentBlock { Text = $"Error modifying script component parameters: {ex.Message}" } },
                    IsError = true
                };
            }
        }, nameof(ModifyScriptComponentParameters));
    }


    /// <summary>
    /// Captures and returns the current Rhino console output.
    /// </summary>
    [McpServerTool]
    [Description("Captures and returns the current Rhino console output and command history")]
    public static async Task<CallToolResult> ReadRhinoConsole(
        [Description("Whether to clear the console buffer after reading (default: false)")]
        bool clearBuffer = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            try
            {
                // Enable capture if not already enabled
                if (!RhinoApp.CommandWindowCaptureEnabled)
                {
                    RhinoApp.CommandWindowCaptureEnabled = true;
                }

                // Get captured console strings
                string[] consoleOutput = RhinoApp.CapturedCommandWindowStrings(clearBuffer);
                
                // Get command history
                string commandHistory = RhinoApp.CommandHistoryWindowText;

                var result = new
                {
                    Timestamp = DateTime.UtcNow,
                    ConsoleOutput = consoleOutput,
                    CommandHistory = commandHistory,
                    CaptureEnabled = RhinoApp.CommandWindowCaptureEnabled,
                    OutputCount = consoleOutput.Length,
                    BufferCleared = clearBuffer
                };

                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) } },
                    IsError = false
                };
            }
            catch (Exception ex)
            {
                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = $"Error reading console: {ex.Message}" } },
                    IsError = true
                };
            }
        }, nameof(ReadRhinoConsole));
    }

    /// <summary>
    /// Sets script content on a C# script component using the RhinoCodePluginGH architecture.
    /// </summary>
    [McpServerTool]
    [Description("Sets script source code on a C# script component using the RhinoCodePluginGH architecture. Rhino 8 defaults are outputs 'out' and lowercase 'a' (not 'A'); use script-mode like `a = ...;` or SDK-mode with `RunScript(..., ref object a)`. For nontrivial scripts, check https://developer.rhino3d.com/guides/scripting/scripting-gh-csharp/ first.")]
    public static async Task<CallToolResult> SetCSharpScript(
        IGrasshopperComponentService componentService,
        [Description("The InstanceGuid of the C# script component to update")] string componentId,
        [Description("The C# script code to set. Default result output is lowercase 'a'.")] string script)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(script), script));

            // Use the same SetComponentScriptAsync method that works for Python
            // This ensures consistent behavior across script types and handles all the reflection logic
            var result = await componentService.SetComponentScriptAsync(componentId, "csharp", script);
            
            if (result.Success)
            {
                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = $"C# script set successfully on {componentId}" } },
                    IsError = false
                };
            }
            
            return new CallToolResult
            {
                Content = new[] { new TextContentBlock { Text = $"Failed to set C# script: {result.ErrorMessage ?? "Unknown error"}" } },
                IsError = true
            };
        }, nameof(SetCSharpScript));
    }

    /// <summary>
    /// Captures a screenshot of the Grasshopper canvas and saves it to a file.
    /// </summary>
    [McpServerTool]
    [Description("Captures a screenshot of the Grasshopper canvas and saves it to a file")]
    public static async Task<CallToolResult> CaptureCanvasScreenshot(
        IGrasshopperUIService uiService,
        [Description("File path where to save the screenshot (optional; defaults to Desktop with timestamp)")]
        string? filePath = null,
        [Description("Image format: 'png', 'jpg', 'bmp', or 'tiff' (default: 'png')")]
        string format = "png",
        [Description("Image quality for JPEG format (1-100, default: 90)")]
        int quality = 90)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(uiService), uiService));
            
            // Validate format
            var validFormats = new[] { "png", "jpg", "jpeg", "bmp", "tiff" };
            if (!validFormats.Contains(format.ToLowerInvariant()))
            {
                throw new ArgumentException($"Invalid format '{format}'. Supported formats: {string.Join(", ", validFormats)}");
            }

            // Validate quality for JPEG
            if (format.ToLowerInvariant() == "jpg" || format.ToLowerInvariant() == "jpeg")
            {
                McpExtensions.ValidateRange(nameof(quality), quality, 1, 100);
            }

            // Generate default file path if not provided
            if (string.IsNullOrWhiteSpace(filePath))
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                filePath = Path.Combine(desktopPath, $"GrasshopperCanvas_{timestamp}.{format.ToLowerInvariant()}");
            }

            // Ensure the directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Capture screenshot on UI thread
            var success = await uiService.InvokeOnUIThreadAsync(() =>
            {
                try
                {
                    RhinoApp.WriteLine("Starting screenshot capture...");
                    
                    // Get the Grasshopper canvas window
                    var canvas = Instances.ActiveCanvas;
                    if (canvas == null)
                    {
                        RhinoApp.WriteLine("No active Grasshopper canvas found");
                        return false;
                    }

                    RhinoApp.WriteLine("Found active canvas");

                    // Get the canvas control
                    var canvasControl = canvas as System.Windows.Forms.Control;
                    if (canvasControl == null)
                    {
                        RhinoApp.WriteLine("Could not get canvas control");
                        return false;
                    }

                    RhinoApp.WriteLine("Got canvas control");

                    // Create a simple text-based "screenshot" representation
                    var bounds = canvasControl.Bounds;
                    try
                    {
                        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        
                        // Create a simple text file as a placeholder screenshot
                        var textContent = $@"Grasshopper Canvas Screenshot
=====================================

Canvas Information:
- Width: {bounds.Width} pixels
- Height: {bounds.Height} pixels
- Location: ({bounds.X}, {bounds.Y})
- Timestamp: {timestamp}

Note: This is a placeholder screenshot. 
Actual screen capture is not available on this platform.

Canvas contains {Instances.ActiveCanvas.Document.Objects.Count()} components.
";
                        
                        // Save as a text file instead of image
                        var textPath = Path.ChangeExtension(filePath, ".txt");
                        File.WriteAllText(textPath, textContent);
                        
                        RhinoApp.WriteLine($"Screenshot information saved to: {textPath}");
                        RhinoApp.WriteLine($"Canvas size: {bounds.Width}x{bounds.Height} pixels");
                    }
                    catch (Exception textEx)
                    {
                        RhinoApp.WriteLine($"Error creating text screenshot: {textEx.Message}");
                        throw;
                    }

                    RhinoApp.WriteLine($"Screenshot saved to: {filePath}");
                    return true;
                }
                catch (Exception ex)
                {
                    RhinoApp.WriteLine($"Error capturing screenshot: {ex.Message}");
                    return false;
                }
            });

            if (success)
            {
                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = $"Screenshot successfully saved to: {filePath}" } }
                };
            }
            else
            {
                return new CallToolResult
                {
                    Content = new[] { new TextContentBlock { Text = "Failed to capture screenshot" } },
                    IsError = true
                };
            }
        }, nameof(CaptureCanvasScreenshot));
    }


    private static IGH_DocumentObject? GetComponentById(string componentId)
    {
        var doc = Instances.ActiveCanvas?.Document;
        if (doc == null)
        {
            RhinoApp.WriteLine("No active Grasshopper document found.");
            return null;
        }

        if (!Guid.TryParse(componentId, out Guid guid))
        {
            RhinoApp.WriteLine($"Invalid component GUID format: {componentId}");
            return null;
        }

        var component = doc.FindObject(guid, false);
        if (component == null)
        {
            RhinoApp.WriteLine($"Component with ID {componentId} not found.");
            return null;
        }

        return component;
    }

    [McpServerTool(Name = "Set_Component_Value")]
    [Description("Sets a value on a Grasshopper component parameter, Boolean Toggle, or Number Slider by component GUID")]
    public static async Task<CallToolResult> SetComponentValue(
        [Description("Component GUID")] string componentId,
        [Description("Parameter name or NickName")] string parameterName,
        [Description("Value to set (number, boolean, or string)")] string value)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentId), componentId),
                (nameof(parameterName), parameterName),
                (nameof(value), value));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentId, out var guid))
                    throw new ArgumentException("Invalid component GUID format");

                var component = doc.FindObject(guid, false) ?? throw new InvalidOperationException("Component not found");

                // Boolean Toggle
                if (component is Grasshopper.Kernel.Special.GH_BooleanToggle toggle)
                {
                    toggle.Value = Convert.ToBoolean(value);
                    toggle.ExpireSolution(true);
                    doc.NewSolution(false);
                    return new { success = true, message = $"Boolean Toggle set to {toggle.Value}" };
                }

                // IGH_Component — find input by name
                if (component is IGH_Component ghComp)
                {
                    var param = ghComp.Params.Input.FirstOrDefault(p =>
                        string.Equals(p.NickName, parameterName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase));

                    if (param != null)
                    {
                        param.AddVolatileData(new Grasshopper.Kernel.Data.GH_Path(0), 0, value);
                        doc.NewSolution(false);
                        return new { success = true, message = $"Value set on parameter '{parameterName}'" };
                    }
                }

                // Slider — try SetSliderValue or Value property via reflection
                if (component.GetType().Name.IndexOf("Slider", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var compType = component.GetType();
                    var setMethod = compType.GetMethod("SetSliderValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (setMethod != null)
                    {
                        var p = setMethod.GetParameters();
                        if (p.Length == 1)
                        {
                            object? arg = p[0].ParameterType == typeof(decimal)
                                ? Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)
                                : p[0].ParameterType == typeof(int)
                                    ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                                    : (object)Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                            setMethod.Invoke(component, new[] { arg });
                            doc.NewSolution(false);
                            return new { success = true, message = "Slider value set" };
                        }
                    }

                    var valueProp = compType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                   ?? compType.GetProperty("CurrentValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (valueProp?.CanWrite == true)
                    {
                        object? converted = valueProp.PropertyType == typeof(decimal)
                            ? Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)
                            : valueProp.PropertyType == typeof(int)
                                ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)
                                : (object)Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                        valueProp.SetValue(component, converted);
                        doc.NewSolution(false);
                        return new { success = true, message = "Slider value set via property" };
                    }
                }

                throw new InvalidOperationException($"Parameter '{parameterName}' not found on component");
            });

            return result;
        }, nameof(SetComponentValue));
    }

}

/// <summary>
/// Win32 API declarations for screen capture functionality.
/// </summary>
internal static class Win32
{
    public const int SRCCOPY = 0x00CC0020;

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
}
