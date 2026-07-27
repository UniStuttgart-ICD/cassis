using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Microsoft.Extensions.Logging;
using Cassis.Models;
using Cassis.Extensions;
using Rhino;

namespace Cassis.Services;

/// <summary>
/// Simplified implementation of the Grasshopper component service for MVP.
/// Provides direct component operations without caching or performance monitoring.
/// </summary>
public class GrasshopperComponentService : IGrasshopperComponentService
{
    private readonly ILogger<GrasshopperComponentService> _logger;

    public GrasshopperComponentService(ILogger<GrasshopperComponentService> logger)
    {
        _logger = logger;
    }

    #region Component Creation

    public async Task<ComponentCreationResult> AddComponentAsync(string type, double x, double y,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return CreateErrorResult("Component type cannot be null or empty");
        }

        _logger.LogInformation("Adding component: type={Type}, x={X}, y={Y}", type, x, y);

        return await InvokeOnUiThreadWithDocumentAsync(
            doc =>
            {
                var component = CreateComponent(type);
                if (component == null)
                {
                    return CreateErrorResult($"Could not create component of type '{type}'");
                }

                AddComponentToDocument(doc, component, x, y);

                // Verify component was added
                var addedComponent = doc.Objects.FirstOrDefault(obj => obj.InstanceGuid == component.InstanceGuid);
                if (addedComponent == null)
                {
                    _logger.LogWarning("Component was not found in document after addition. Document has {ObjectCount} objects", doc.Objects.Count);
                }
                else
                {
                    _logger.LogDebug("Component verified in document: {ComponentId} at position ({X}, {Y})",
                        component.InstanceGuid, component.Attributes?.Pivot.X ?? 0, component.Attributes?.Pivot.Y ?? 0);
                }

                _logger.LogInformation("Successfully added component {ComponentId} of type {Type}",
                    component.InstanceGuid, component.GetType().Name);

                return CreateSuccessResult(component);
            },
            error => CreateErrorResult(error));
    }

    public async Task<ComponentCreationResult> AddPythonScriptComponentAsync(string script, double x, double y, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return CreateErrorResult("Script content cannot be null or empty");
        }

        McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
        McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

        return await InvokeOnUiThreadWithDocumentAsync(
            doc =>
            {
                var component = CreatePythonScriptComponent(script);
                if (component == null)
                {
                    return CreateErrorResult("Unable to create Python script component");
                }

                AddComponentToDocument(doc, component, x, y);

                _logger.LogInformation("Successfully added Python script component {ComponentId} with script content",
                    component.InstanceGuid);

                return CreateSuccessResult(component);
            },
            error => CreateErrorResult(error));
    }

    public async Task<ComponentCreationResult> AddCSharpScriptComponentAsync(string script, double x, double y, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return CreateErrorResult("Script content cannot be null or empty");
        }

        McpExtensions.ValidateRange(nameof(x), x, -10000, 10000);
        McpExtensions.ValidateRange(nameof(y), y, -10000, 10000);

        return await InvokeOnUiThreadWithDocumentAsync(
            doc =>
            {
                var component = CreateCSharpScriptComponent(script);
                if (component == null)
                {
                    return CreateErrorResult("Unable to create C# script component");
                }

                AddComponentToDocument(doc, component, x, y);

                _logger.LogInformation("Successfully added C# script component {ComponentId} with script content",
                    component.InstanceGuid);

                return CreateSuccessResult(component);
            },
            error => CreateErrorResult(error));
    }

    #endregion

    #region Component Information & Inspection

    public async Task<ComponentValueResult> SetComponentValueAsync(string componentId, string parameterName,
        object value, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ComponentValueResult>();

        // Validate input parameters
        if (string.IsNullOrWhiteSpace(componentId))
        {
            return new ComponentValueResult(
                false,
                null,
                parameterName,
                null,
                null,
                "Component ID cannot be null or empty");
        }

        if (string.IsNullOrWhiteSpace(parameterName))
        {
            return new ComponentValueResult(
                false,
                componentId,
                null,
                null,
                null,
                "Parameter name cannot be null or empty");
        }

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        componentId,
                        parameterName,
                        null,
                        null,
                        "No active Grasshopper document"));
                    return;
                }

                if (!Guid.TryParse(componentId, out Guid guid))
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        componentId,
                        parameterName,
                        null,
                        null,
                        $"Invalid component ID: {componentId}"));
                    return;
                }

                var obj = doc.FindObject(guid, true);
                if (obj == null)
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        componentId,
                        parameterName,
                        null,
                        null,
                        $"Component not found: {componentId}"));
                    return;
                }

                // Resolve target parameter
                IGH_Param? targetParam = null;
                if (obj is IGH_Component ghComponent)
                {
                    targetParam = ResolveParameter(ghComponent, parameterName, isTargetParam: true);
                }
                else if (obj is IGH_Param ghParam)
                {
                    targetParam = ghParam;
                }

                if (targetParam == null)
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        componentId,
                        parameterName,
                        null,
                        null,
                        $"Parameter not found: {parameterName}"));
                    return;
                }

                // Special-cases for some document objects
                if (obj is GH_NumberSlider slider)
                {
                    // Handle InitCode parameter to set range and value
                    if (string.Equals(parameterName, "InitCode", StringComparison.OrdinalIgnoreCase))
                    {
                        var initCode = value?.ToString() ?? string.Empty;
                        slider.SetInitCode(initCode);
                    }
                    // Handle Value parameter - try to parse as number and set
                    else if (double.TryParse(value?.ToString(), out var d))
                    {
                        var decimalValue = (decimal)d;
                        
                        // Get current slider range and value
                        // GH_NumberSlider has a Slider property that contains the actual slider control
                        decimal min = 0m, max = 1m, currentValue = 0.5m;
                        try
                        {
                            // Access via Slider property if available
                            if (slider.Slider != null)
                            {
                                min = slider.Slider.Minimum;
                                max = slider.Slider.Maximum;
                                // Try to get current value from slider, fallback to midpoint
                                try
                                {
                                    var valueProp = slider.Slider.GetType().GetProperty("Value");
                                    if (valueProp != null)
                                    {
                                        var val = valueProp.GetValue(slider.Slider);
                                        if (val != null && decimal.TryParse(val.ToString(), out var parsed))
                                            currentValue = parsed;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Rhino.RhinoApp.WriteLine($"[MCP WARN] Error reading slider value, using midpoint: {ex.Message}");
                                    currentValue = (min + max) / 2;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Slider property access failed; defaults (0..1) are reasonable
                            Rhino.RhinoApp.WriteLine($"[MCP WARN] Error accessing slider range, using defaults: {ex.Message}");
                        }
                        
                        // If value is outside current range, adjust the range using SetInitCode
                        if (decimalValue < min || decimalValue > max)
                        {
                            // Set new range with some padding: extend range to include the new value
                            var diff = Math.Abs((double)(decimalValue - currentValue));
                            var rangePadding = Math.Max((decimal)(diff * 0.2), 10m);
                            var newMin = Math.Min(min, decimalValue - rangePadding);
                            var newMax = Math.Max(max, decimalValue + rangePadding);
                            var newInitCode = $"{newMin} < {decimalValue} < {newMax}";
                            slider.SetInitCode(newInitCode);
                        }
                        else
                        {
                            // Value is within range, just set it
                            slider.SetSliderValue(decimalValue);
                        }
                    }
                    else
                    {
                        tcs.SetResult(new ComponentValueResult(
                            false,
                            componentId,
                            parameterName,
                            null,
                            null,
                            $"Invalid value for Number Slider: {value}"));
                        return;
                    }
                }
                else if (obj is GH_Panel panel)
                {
                    panel.UserText = value?.ToString() ?? string.Empty;
                }
                else
                {
                    // Generic persistent data setter by parameter type
                    if (!TrySetParamPersistentData(targetParam, value))
                    {
                        tcs.SetResult(new ComponentValueResult(
                            false,
                            componentId,
                            targetParam.Name,
                            null,
                            null,
                            $"Setting values on parameter type '{targetParam.GetType().Name}' is not supported"));
                        return;
                    }
                }

                doc.NewSolution(false);

                var result = new ComponentValueResult(
                    true,
                    componentId,
                    targetParam.Name,
                    value?.ToString(),
                    "Component value updated");

                _logger.LogInformation("Set component {ComponentId} parameter {Parameter} to {Value}",
                    componentId, targetParam.Name, value);

                tcs.SetResult(result);

                // Local helpers
                static IGH_Param? ResolveParameter(IGH_Component comp, string nameOrIndex, bool isTargetParam)
                {
                    var list = isTargetParam ? comp.Params.Input : comp.Params.Output;

                    var byName = list.FirstOrDefault(p =>
                        string.Equals(p.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.NickName, nameOrIndex, StringComparison.OrdinalIgnoreCase));
                    if (byName != null) return byName;

                    if (int.TryParse(nameOrIndex, out var idx) && idx >= 0 && idx < list.Count)
                        return list[idx];

                    return null;
                }

                static bool TrySetParamPersistentData(IGH_Param param, object? valueObj)
                {
                    try
                    {
                        var s = valueObj?.ToString() ?? string.Empty;

                        switch (param)
                        {
                            case Param_String ps:
                                ps.PersistentData.Clear();
                                ps.PersistentData.Append(new Grasshopper.Kernel.Types.GH_String(s));
                                return true;

                            case Param_Number pn:
                                if (double.TryParse(s, out var d2))
                                {
                                    pn.PersistentData.Clear();
                                    pn.PersistentData.Append(new Grasshopper.Kernel.Types.GH_Number(d2));
                                    return true;
                                }
                                return false;

                            case Grasshopper.Kernel.Parameters.Param_Integer pi:
                                if (int.TryParse(s, out var i))
                                {
                                    pi.PersistentData.Clear();
                                    pi.PersistentData.Append(new Grasshopper.Kernel.Types.GH_Integer(i));
                                    return true;
                                }
                                return false;

                            case Grasshopper.Kernel.Parameters.Param_Boolean pb:
                                if (bool.TryParse(s, out var b))
                                {
                                    pb.PersistentData.Clear();
                                    pb.PersistentData.Append(new Grasshopper.Kernel.Types.GH_Boolean(b));
                                    return true;
                                }
                                return false;

                            default:
                                return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Rhino.RhinoApp.WriteLine($"[MCP WARN] Error setting persistent data: {ex.Message}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting component value for {ComponentId}", componentId);
                tcs.SetResult(new ComponentValueResult(
                    false,
                    componentId,
                    parameterName,
                    null,
                    null,
                    ex.Message));
            }
        });

        return await tcs.Task;
    }

    public async Task<ComponentInfoResult> GetComponentInfoAsync(string componentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(componentId))
        {
            return new ComponentInfoResult(
                false,
                ErrorMessage: "Component ID cannot be null or empty");
        }

        return await InvokeOnUiThreadWithDocumentAsync(
            doc =>
            {
                if (!Guid.TryParse(componentId, out Guid guid))
                {
                    return new ComponentInfoResult(
                        false,
                        ErrorMessage: $"Invalid component ID: {componentId}");
                }

                var component = doc.FindObject(guid, false);
                if (component == null)
                {
                    return new ComponentInfoResult(
                        false,
                        ErrorMessage: $"Component not found: {componentId}");
                }

                var inputs = new List<ParameterInfo>();
                var outputs = new List<ParameterInfo>();
                var runtimeMessages = new List<string>();
                bool hasErrors = false;
                bool hasWarnings = false;
                string? scriptContent = null;

                if (component is IGH_Component ghComponent)
                {
                    foreach (var param in ghComponent.Params.Input)
                        inputs.Add(new ParameterInfo(
                            param.Name,
                            param.NickName,
                            param.Type.Name,
                            param.Optional,
                            param.Description ?? ""));

                    foreach (var param in ghComponent.Params.Output)
                        outputs.Add(new ParameterInfo(
                            param.Name,
                            param.NickName,
                            param.Type.Name,
                            Description: param.Description ?? ""));

                    // Get runtime messages and status
                    foreach (var message in ghComponent.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                    {
                        hasErrors = true;
                        runtimeMessages.Add($"ERROR: {message}");
                    }
                    
                    foreach (var message in ghComponent.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                    {
                        hasWarnings = true;
                        runtimeMessages.Add($"WARNING: {message}");
                    }
                    
                    foreach (var message in ghComponent.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
                    {
                        runtimeMessages.Add($"INFO: {message}");
                    }

                    // Try to get script content if this is a script component
                    try
                    {
                        scriptContent = ExtractScriptContent(ghComponent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not extract script content from component {ComponentId}", componentId);
                    }
                }

                var componentInfo = new Models.ComponentInfo(
                    component.InstanceGuid.ToString(),
                    component.GetType().Name,
                    component.NickName,
                    component.Category,
                    component.SubCategory,
                    component.Description,
                    component.Attributes.Pivot.X,
                    component.Attributes.Pivot.Y,
                    inputs,
                    outputs,
                    hasErrors,
                    hasWarnings,
                    runtimeMessages,
                    scriptContent,
                    component.Keywords?.ToList());

                _logger.LogInformation("Retrieved info for component {ComponentId}", componentId);
                return new ComponentInfoResult(true, componentInfo);
            },
            error => new ComponentInfoResult(false, ErrorMessage: error));
    }

    /// <summary>
    /// Attempts to extract script content from a script component.
    /// </summary>
    private static string? ExtractScriptContent(IGH_Component ghComponent)
    {
        var componentType = ghComponent.GetType();
        
        // Try different methods to get script content based on component type
        try
        {
            // For RhinoCodePluginGH components, try to get Context.Script
            var contextProperty = componentType.GetProperty("Context", 
                System.Reflection.BindingFlags.Instance | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.NonPublic);
            
            if (contextProperty != null)
            {
                var context = contextProperty.GetValue(ghComponent);
                if (context != null)
                {
                    var scriptProperty = context.GetType().GetProperty("Script");
                    if (scriptProperty != null)
                    {
                        var scriptObj = scriptProperty.GetValue(context);
                        if (scriptObj != null)
                        {
                            // Try to get the script text/source
                            var textProperty = scriptObj.GetType().GetProperty("Text") ?? 
                                             scriptObj.GetType().GetProperty("Source") ?? 
                                             scriptObj.GetType().GetProperty("Code");
                            
                            if (textProperty != null)
                            {
                                var scriptText = textProperty.GetValue(scriptObj) as string;
                                if (!string.IsNullOrEmpty(scriptText))
                                {
                                    return scriptText;
                                }
                            }
                        }
                    }
                }
            }
            
            // Fallback: try common script properties
            var scriptProps = new[] { "Script", "Code", "Source", "ScriptSource" };
            foreach (var propName in scriptProps)
            {
                var prop = componentType.GetProperty(propName, 
                    System.Reflection.BindingFlags.Instance | 
                    System.Reflection.BindingFlags.Public | 
                    System.Reflection.BindingFlags.NonPublic);
                
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    var value = prop.GetValue(ghComponent) as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - this is a best-effort extraction
            RhinoApp.WriteLine($"[MCP] Could not extract script content: {ex.Message}");
        }
        
        return null;
    }

    #endregion

    #region Component Connections

    public async Task<ComponentConnectionResult> ConnectComponentsAsync(string sourceId, string sourceParam,
        string targetId, string targetParam, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) ||
            string.IsNullOrWhiteSpace(targetId) ||
            string.IsNullOrWhiteSpace(sourceParam) ||
            string.IsNullOrWhiteSpace(targetParam))
        {
            return new ComponentConnectionResult(
                false,
                "Connection failed",
                sourceId,
                sourceParam,
                targetId,
                targetParam,
                "Missing required parameters");
        }

        return await InvokeOnUiThreadWithDocumentAsync(
            doc =>
            {
                if (!Guid.TryParse(sourceId, out Guid srcGuid) || !Guid.TryParse(targetId, out Guid dstGuid))
                {
                    return new ComponentConnectionResult(
                        false,
                        "Connection failed",
                        sourceId,
                        sourceParam,
                        targetId,
                        targetParam,
                        "Invalid component GUID(s)");
                }

                var srcObj = doc.FindObject(srcGuid, true);
                var dstObj = doc.FindObject(dstGuid, true);
                if (srcObj == null || dstObj == null)
                {
                    return new ComponentConnectionResult(
                        false,
                        "Connection failed",
                        sourceId,
                        sourceParam,
                        targetId,
                        targetParam,
                        "Component(s) not found");
                }

                // Resolve parameters
                var srcParamObj = ResolveParameter(srcObj, sourceParam, isTargetParam: false);
                var dstParamObj = ResolveParameter(dstObj, targetParam, isTargetParam: true);
                if (srcParamObj == null || dstParamObj == null)
                {
                    return new ComponentConnectionResult(
                        false,
                        "Connection failed",
                        sourceId,
                        sourceParam,
                        targetId,
                        targetParam,
                        "Parameter(s) not found");
                }

                // Sanity rules
                if (srcParamObj.Kind == GH_ParamKind.input)
                {
                    return new ComponentConnectionResult(
                        false,
                        "Connection failed",
                        sourceId,
                        sourceParam,
                        targetId,
                        targetParam,
                        "Source cannot be an input parameter");
                }
                if (dstParamObj.Kind == GH_ParamKind.output)
                {
                    return new ComponentConnectionResult(
                        false,
                        "Connection failed",
                        sourceId,
                        sourceParam,
                        targetId,
                        targetParam,
                        "Target cannot be an output parameter");
                }

                // Replace existing sources on target
                if (dstParamObj.SourceCount > 0)
                    dstParamObj.RemoveAllSources();

                // Connect
                dstParamObj.AddSource(srcParamObj);

                // Recompute
                dstParamObj.CollectData();
                dstParamObj.ComputeData();
                doc.NewSolution(false);

                _logger.LogInformation("Connected {SourceId}.{SourceParam} -> {TargetId}.{TargetParam}",
                    sourceId, srcParamObj.Name, targetId, dstParamObj.Name);

                return new ComponentConnectionResult(
                    true,
                    "Connection created",
                    sourceId,
                    srcParamObj.Name,
                    targetId,
                    dstParamObj.Name);
            },
            error => new ComponentConnectionResult(
                false,
                "Connection failed",
                sourceId,
                sourceParam,
                targetId,
                targetParam,
                error));
    }

    #endregion

    #region Component Factory Methods

    private static IGH_DocumentObject? CreateComponent(string type)
    {
        try
        {
            RhinoApp.WriteLine($"CreateComponent called with type: '{type}'");
            
            // First try to find the component by name using the ComponentServer
            var componentProxy = Grasshopper.Instances.ComponentServer.ObjectProxies
                .FirstOrDefault(p => p.Desc.Name.Equals(type, StringComparison.OrdinalIgnoreCase));
                
            if (componentProxy != null)
            {
                RhinoApp.WriteLine($"Found component proxy for '{type}', creating instance...");
                var instance = componentProxy.CreateInstance();
                RhinoApp.WriteLine($"Component instance created successfully: {instance?.GetType().Name}");
                return instance;
            }
            
            RhinoApp.WriteLine($"No component proxy found for '{type}', trying fallback types...");
            
            // Fallback to our simplified component creation for basic types
            string lowerType = type.ToLowerInvariant();

            IGH_DocumentObject? result = lowerType switch
            {
                "point" or "pt" or "pointparam" or "param_point" => new Param_Point(),
                "curve" or "crv" or "curveparam" or "param_curve" => new Param_Curve(),
                "panel" or "gh_panel" => new GH_Panel(),
                "slider" or "numberslider" or "gh_numberslider" => CreateNumberSlider(),
                "number" or "num" or "param_number" => new Param_Number(),
                "circle" => CreateComponentByName("Circle"),
                "line" => CreateComponentByName("Line"),
                "box" => CreateComponentByName("Box"),
                "sphere" => CreateComponentByName("Sphere"),
                "cylinder" => CreateComponentByName("Cylinder"),
                "cone" => CreateComponentByName("Cone"),
                "rectangle" => CreateComponentByName("Rectangle"),
                _ => null
            };

            // If no result from switch, try script component types
            if (result == null)
            {
                result = type switch
                {
                    "Python 3" or "Py3" or "Python Script" or "GhPython" or "Python" => CreatePythonScriptComponent(),
                    "C# Script" or "CSharp Script" or "C# Script Component" => CreateCSharpScriptComponent(),
                    "Script" => CreateGenericScriptComponent(),
                    _ => null
                };
            }
            
            if (result != null)
            {
                RhinoApp.WriteLine($"Fallback component created successfully: {result.GetType().Name}");
            }
            else
            {
                RhinoApp.WriteLine($"No fallback component type matched for '{type}'");
            }
            
            return result;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Error creating component of type '{type}': {ex.Message}");
            RhinoApp.WriteLine($"Stack trace: {ex.StackTrace}");
            return null;
        }
    }
    
    private static IGH_DocumentObject? CreateComponentByName(string name)
    {
        try
        {
            var componentProxy = Grasshopper.Instances.ComponentServer.ObjectProxies
                .FirstOrDefault(p => p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                
            if (componentProxy != null)
            {
                return componentProxy.CreateInstance();
            }
            else
            {
                RhinoApp.WriteLine($"Component with name '{name}' not found");
                return null;
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Error creating component by name '{name}': {ex.Message}");
            return null;
        }
    }

    private static GH_NumberSlider CreateNumberSlider()
    {
        var slider = new GH_NumberSlider();
        slider.SetInitCode("0.0 < 0.5 < 1.0");
        return slider;
    }

    private static IGH_DocumentObject? CreatePythonScriptComponent(string? scriptSource = null)
    {
        try
        {
            // First try to use RhinoCodePluginGH.Components.Python3Component.Create method
            try
            {
                var python3ComponentType = Type.GetType("RhinoCodePluginGH.Components.Python3Component, RhinoCodePluginGH");
                if (python3ComponentType != null)
                {
                    if (!string.IsNullOrWhiteSpace(scriptSource))
                    {
                        // Use the Create method that takes source code
                        var createMethod = python3ComponentType.GetMethod("Create", new[] { typeof(string), typeof(string), typeof(System.Drawing.Bitmap), typeof(bool) });
                        if (createMethod != null)
                        {
                            var component = createMethod.Invoke(null, new object?[] { "Python Script", scriptSource, null, false });
                            if (component is IGH_DocumentObject docObject)
                            {
                                RhinoApp.WriteLine("Created Python3Component using Create method with source");
                                return docObject;
                            }
                        }
                    }
                    else
                    {
                        // Use the Create method that creates empty script
                        var createMethod = python3ComponentType.GetMethod("Create", new[] { typeof(string), typeof(System.Drawing.Bitmap), typeof(bool) });
                        if (createMethod != null)
                        {
                            var component = createMethod.Invoke(null, new object?[] { "Python Script", null, false });
                            if (component is IGH_DocumentObject docObject)
                            {
                                RhinoApp.WriteLine("Created Python3Component using Create method");
                                return docObject;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Failed to create Python3Component via Create method: {ex.Message}");
            }
            
            // Fallback to finding component by name
            var pythonNames = new[] { "Python 3", "Py3", "Python Script", "GhPython", "Python" };
            
            foreach (var name in pythonNames)
            {
                var componentProxy = Grasshopper.Instances.ComponentServer.ObjectProxies
                    .FirstOrDefault(p => p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    
                if (componentProxy != null)
                {
                    RhinoApp.WriteLine($"Found Python script component: {name}");
                    return componentProxy.CreateInstance();
                }
            }
            
            // If no Python component found, try to create a generic script component
            RhinoApp.WriteLine("No Python script component found, falling back to generic Script component");
            return CreateGenericScriptComponent();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Error creating Python script component: {ex.Message}");
            return null;
        }
    }

    private static IGH_DocumentObject? CreateCSharpScriptComponent(string? scriptSource = null)
    {
        try
        {
            // First try to use RhinoCodePluginGH.Components.CSharpComponent.Create method
            try
            {
                var csharpComponentType = Type.GetType("RhinoCodePluginGH.Components.CSharpComponent, RhinoCodePluginGH");
                if (csharpComponentType != null)
                {
                    if (!string.IsNullOrWhiteSpace(scriptSource))
                    {
                        // Use the Create method that takes source code
                        var createMethod = csharpComponentType.GetMethod("Create", new[] { typeof(string), typeof(string), typeof(System.Drawing.Bitmap), typeof(bool) });
                        if (createMethod != null)
                        {
                            var component = createMethod.Invoke(null, new object?[] { "C# Script", scriptSource, null, false });
                            if (component is IGH_DocumentObject docObject)
                            {
                                RhinoApp.WriteLine("Created CSharpComponent using Create method with source");
                                return docObject;
                            }
                        }
                    }
                    else
                    {
                        // Use the Create method that creates empty script
                        var createMethod = csharpComponentType.GetMethod("Create", new[] { typeof(string), typeof(System.Drawing.Bitmap), typeof(bool) });
                        if (createMethod != null)
                        {
                            var component = createMethod.Invoke(null, new object?[] { "C# Script", null, false });
                            if (component is IGH_DocumentObject docObject)
                            {
                                RhinoApp.WriteLine("Created CSharpComponent using Create method");
                                return docObject;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Failed to create CSharpComponent via Create method: {ex.Message}");
            }
            
            // Fallback to finding component by name
            var csharpNames = new[] { "C# Script", "CSharp Script", "C# Script Component" };
            
            foreach (var name in csharpNames)
            {
                var componentProxy = Grasshopper.Instances.ComponentServer.ObjectProxies
                    .FirstOrDefault(p => p.Desc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    
                if (componentProxy != null)
                {
                    RhinoApp.WriteLine($"Found C# script component: {name}");
                    return componentProxy.CreateInstance();
                }
            }
            
            // If no C# component found, try to create a generic script component
            RhinoApp.WriteLine("No C# script component found, falling back to generic Script component");
            return CreateGenericScriptComponent();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Error creating C# script component: {ex.Message}");
            return null;
        }
    }

    private static IGH_DocumentObject? CreateGenericScriptComponent()
    {
        try
        {
            // Try to find generic Script component
            var componentProxy = Grasshopper.Instances.ComponentServer.ObjectProxies
                .FirstOrDefault(p => p.Desc.Name.Equals("Script", StringComparison.OrdinalIgnoreCase));
                
            if (componentProxy != null)
            {
                RhinoApp.WriteLine("Found generic Script component");
                return componentProxy.CreateInstance();
            }
            
            RhinoApp.WriteLine("No Script component found");
            return null;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"Error creating generic Script component: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Component Modification

    public async Task<ComponentValueResult> SetComponentScriptAsync(string componentId, string language, string script, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ComponentValueResult>();

        if (string.IsNullOrWhiteSpace(componentId))
        {
            return new ComponentValueResult(false, null, null, null, null, "Component ID cannot be null or empty");
        }
        if (string.IsNullOrWhiteSpace(script))
        {
            return new ComponentValueResult(false, componentId, null, null, null, "Script cannot be null or empty");
        }

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new ComponentValueResult(false, componentId, null, null, null, "No active Grasshopper document"));
                    return;
                }

                if (!Guid.TryParse(componentId, out Guid guid))
                {
                    tcs.SetResult(new ComponentValueResult(false, componentId, null, null, null, $"Invalid component ID: {componentId}"));
                    return;
                }

                var obj = doc.FindObject(guid, true);
                if (obj is not IGH_Component ghComponent)
                {
                    tcs.SetResult(new ComponentValueResult(false, componentId, null, null, null, "Component not found or not a script component"));
                    return;
                }

                var compType = ghComponent.GetType();
                var scriptToSet = script;

                _logger.LogInformation("Setting script for component {ComponentId} of type {ComponentType}", componentId, compType.Name);
                RhinoApp.WriteLine($"[MCP] SetComponentScript: id={componentId} type={compType.FullName} lang={language}");
                _logger.LogInformation("Original script length: {ScriptLength}, Language: {Language}", script.Length, language ?? "none");

                // If this is the unified Script component, try to explicitly set language via reflection first
                if (!string.IsNullOrWhiteSpace(language))
                {
                    var normalizedLanguage = language.Trim().ToLowerInvariant() switch
                    {
                        "py" or "python" or "python3" or "py3" => "Python",
                        "c#" or "cs" or "csharp" => "CSharp",
                        _ => language.Trim()
                    };

                    try
                    {
                        if (TrySetComponentLanguage(ghComponent, compType, normalizedLanguage, _logger))
                        {
                            _logger.LogInformation("Component language set to {Language} for {ComponentId}", normalizedLanguage, componentId);
                            RhinoApp.WriteLine($"[MCP] Language set to {normalizedLanguage} on component {componentId}");
                        }
                        else
                        {
                            _logger.LogDebug("No explicit language setter found on component type {ComponentType}", compType.Name);
                            RhinoApp.WriteLine($"[MCP] No explicit language setter found on {compType.FullName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set language on component {ComponentId}", componentId);
                    }
                }

                // ScriptComponent specific language handling
                if (!string.IsNullOrWhiteSpace(language) && compType.Name == "ScriptComponent")
                {
                    try
                    {
                        var scriptLanguage = language.Trim().ToLowerInvariant() switch
                        {
                            "py" or "python" or "python3" or "py3" => "Python3",
                            "c#" or "cs" or "csharp" => "CSharp",
                            _ => language.Trim()
                        };
                        
                        // Try to set the language via the Language property
                        var languageProperty = compType.GetProperty("Language", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (languageProperty != null && languageProperty.CanWrite)
                        {
                            languageProperty.SetValue(ghComponent, scriptLanguage);
                            _logger.LogInformation("Language set to {Language} on ScriptComponent {ComponentId}", scriptLanguage, componentId);
                            RhinoApp.WriteLine($"[MCP] Language set to {scriptLanguage} on ScriptComponent {componentId}");
                        }
                        else
                        {
                            _logger.LogDebug("Language property not found or not writable on ScriptComponent {ComponentId}", componentId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set language on ScriptComponent {ComponentId}", componentId);
                    }
                }

                if (!string.IsNullOrWhiteSpace(language) && language.Trim().Equals("python", StringComparison.OrdinalIgnoreCase))
                {
                    var hasShebang = script.StartsWith("#!", StringComparison.Ordinal);
                    var mentionsPython3 = script.IndexOf("python 3", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hasShebang && !mentionsPython3)
                    {
                        scriptToSet = "#! python 3\n\n" + script;
                        _logger.LogInformation("Added Python shebang to script");
                    }
                }

                _logger.LogInformation("Final script to set: {ScriptPreview}", scriptToSet.Length > 100 ? scriptToSet.Substring(0, 100) + "..." : scriptToSet);

                bool scriptSet = false;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

                // Strategy 1: RhinoCode in-place modification via ScriptSource property
                // Modifies the existing script content rather than replacing the script object,
                // which avoids putting the ScriptEditor into read-only mode.
                try
                {
                    var scriptSourceProp = compType.GetProperty("ScriptSource", flags);
                    if (scriptSourceProp != null)
                    {
                        var scriptSource = scriptSourceProp.GetValue(ghComponent);
                        if (scriptSource != null)
                        {
                            var ssType = scriptSource.GetType();
                            string[] codeProps = { "Code", "Text", "Script", "Source" };
                            foreach (var cpName in codeProps)
                            {
                                var cp = ssType.GetProperty(cpName, flags);
                                if (cp != null && cp.CanWrite && cp.PropertyType == typeof(string))
                                {
                                    cp.SetValue(scriptSource, scriptToSet);
                                    scriptSet = true;
                                    _logger.LogInformation("Script set via ScriptSource.{Property} for component {ComponentId}", cpName, componentId);
                                    RhinoApp.WriteLine($"[MCP] Script set via ScriptSource.{cpName} on {componentId}");
                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed ScriptSource in-place strategy for component {ComponentId}", componentId);
                }

                // Strategy 2: SetSource method
                if (!scriptSet)
                {
                    var setSourceMethod = compType.GetMethod("SetSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (setSourceMethod != null)
                    {
                        try
                        {
                            setSourceMethod.Invoke(ghComponent, new object[] { scriptToSet });
                            scriptSet = true;
                            _logger.LogInformation("Script set using SetSource method for component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] Script set via SetSource on {componentId}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to set script using SetSource method for component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] SetSource failed: {ex.Message}");
                        }
                    }
                }

                if (!scriptSet)
                {
                    var setScriptMethod = compType.GetMethod("SetScript", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    if (setScriptMethod != null)
                    {
                        try
                        {
                            setScriptMethod.Invoke(ghComponent, new object[] { scriptToSet });
                            scriptSet = true;
                            _logger.LogInformation("Script set using SetScript method for component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] Script set via SetScript(string) on {componentId}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to set script using SetScript method for component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] SetScript(string) failed: {ex.Message}");
                        }
                    }
                }

                // Rhino 7 C# Script component specific handling - PRIORITY for legacy components
                if (!scriptSet && compType.Name.Contains("CSNET_Script"))
                {
                    try
                    {
                        _logger.LogInformation("Attempting to set script on Rhino 7 C# component {ComponentId}", componentId);
                        RhinoApp.WriteLine($"[MCP] Setting script on Rhino 7 C# component {componentId}");
                        
                        // Try the ScriptCode property specifically for Rhino 7 C# components
                        var scriptCodeProp = compType.GetProperty("ScriptCode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (scriptCodeProp != null && scriptCodeProp.CanWrite && scriptCodeProp.PropertyType == typeof(string))
                        {
                            scriptCodeProp.SetValue(ghComponent, scriptToSet);
                            scriptSet = true;
                            _logger.LogInformation("Script set using ScriptCode property for Rhino 7 C# component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] Script set via ScriptCode property on Rhino 7 C# component {componentId}");
                        }
                        else
                        {
                            _logger.LogDebug("ScriptCode property not found or not writable on Rhino 7 C# component {ComponentId}", componentId);
                            RhinoApp.WriteLine($"[MCP] ScriptCode property not found on Rhino 7 C# component {componentId}");
                        }
                        
                        // Try the ScriptSource approach (Rhino 7 legacy method)
                        if (!scriptSet)
                        {
                            var scriptSourceProperty = compType.GetProperty("ScriptSource");
                            if (scriptSourceProperty != null)
                            {
                                try
                                {
                                    var scriptSource = scriptSourceProperty.GetValue(ghComponent);
                                    if (scriptSource != null)
                                    {
                                        var scriptCodeProperty = scriptSource.GetType().GetProperty("ScriptCode");
                                        if (scriptCodeProperty != null)
                                        {
                                            // Set the main script code
                                            scriptCodeProperty.SetValue(scriptSource, scriptToSet);
                                            
                                            // Try to set UsingCode if available (for using statements)
                                            var usingCodeProperty = scriptSource.GetType().GetProperty("UsingCode");
                                            if (usingCodeProperty != null)
                                            {
                                                var usingCode = "using System;\nusing Rhino.Geometry;\nusing System.Collections.Generic;";
                                                usingCodeProperty.SetValue(scriptSource, usingCode);
                                            }
                                            
                                            // Try to set AdditionalCode if available (for helper classes)
                                            var additionalCodeProperty = scriptSource.GetType().GetProperty("AdditionalCode");
                                            if (additionalCodeProperty != null)
                                            {
                                                additionalCodeProperty.SetValue(scriptSource, "");
                                            }
                                            
                                            scriptSet = true;
                                            _logger.LogInformation("Script set using ScriptSource.ScriptCode for Rhino 7 C# component {ComponentId}", componentId);
                                            RhinoApp.WriteLine($"[MCP] Script set via ScriptSource.ScriptCode on Rhino 7 C# component {componentId}");
                                            
                                            // Force recompilation using ExpireSolution (Rhino 7 approach)
                                            var expireSolutionMethod = compType.GetMethod("ExpireSolution", new[] { typeof(bool) });
                                            if (expireSolutionMethod != null)
                                            {
                                                expireSolutionMethod.Invoke(ghComponent, new object[] { true });
                                                _logger.LogInformation("Component expired using ExpireSolution(true) for {ComponentId}", componentId);
                                                RhinoApp.WriteLine($"[MCP] Component expired using ExpireSolution(true) for {componentId}");
                                            }
                                            
                                            // Also try to schedule a solution from the document
                                            var activeDoc = Grasshopper.Instances.ActiveCanvas?.Document;
                                            if (activeDoc != null)
                                            {
                                                var scheduleSolutionMethod = activeDoc.GetType().GetMethod("ScheduleSolution", new[] { typeof(int), typeof(System.Action<Grasshopper.Kernel.GH_Document>) });
                                                if (scheduleSolutionMethod != null)
                                                {
                                                    scheduleSolutionMethod.Invoke(activeDoc, new object[] { 1, (System.Action<Grasshopper.Kernel.GH_Document>)(_ => { }) });
                                                    _logger.LogInformation("Document solution scheduled for {ComponentId}", componentId);
                                                    RhinoApp.WriteLine($"[MCP] Document solution scheduled for {componentId}");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to set C# script using ScriptSource.ScriptCode for component {ComponentId}", componentId);
                                    RhinoApp.WriteLine($"[MCP] ScriptSource.ScriptCode failed: {ex.Message}");
                                }
                            }
                        }
                        
                        // If ScriptCode didn't work, try alternative properties
                        if (!scriptSet)
                        {
                            string[] altPropNames = { "Code", "Script", "Source", "Text" };
                            foreach (var altPropName in altPropNames)
                            {
                                var altProp = compType.GetProperty(altPropName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                                if (altProp != null && altProp.CanWrite && altProp.PropertyType == typeof(string))
                                {
                                    try
                                    {
                                        altProp.SetValue(ghComponent, scriptToSet);
                                        scriptSet = true;
                                        _logger.LogInformation("Script set using alternative property {PropertyName} for Rhino 7 C# component {ComponentId}", altPropName, componentId);
                                        RhinoApp.WriteLine($"[MCP] Script set via {altPropName} property on Rhino 7 C# component {componentId}");
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogDebug(ex, "Failed to set script using alternative property {PropertyName} for Rhino 7 C# component {ComponentId}", altPropName, componentId);
                                    }
                                }
                            }
                        }
                        
                        // If still not set, try setting via input parameters
                        if (!scriptSet)
                        {
                            try
                            {
                                foreach (var input in ghComponent.Params.Input)
                                {
                                    if ((input.Name?.IndexOf("code", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 || 
                                        (input.Name?.IndexOf("script", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                    {
                                        if (input is Param_String ps)
                                        {
                                            ps.PersistentData.Clear();
                                            ps.PersistentData.Append(new Grasshopper.Kernel.Types.GH_String(scriptToSet));
                                            scriptSet = true;
                                            _logger.LogInformation("Script set via input parameter {Param} for Rhino 7 C# component {ComponentId}", input.Name, componentId);
                                            RhinoApp.WriteLine($"[MCP] Script set via input parameter {input.Name} on Rhino 7 C# component {componentId}");
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to set script via input parameters for Rhino 7 C# component {ComponentId}", componentId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set script using ScriptCode property for Rhino 7 C# component {ComponentId}", componentId);
                        RhinoApp.WriteLine($"[MCP] Failed to set script on Rhino 7 C# component {componentId}: {ex.Message}");
                    }
                }

                // General property-based script setting (fallback for other component types)
                if (!scriptSet)
                {
                    string[] propNames = { "Code", "ScriptSource", "Text", "ScriptText", "Source", "ScriptCode" };
                    foreach (var propName in propNames)
                    {
                        var prop = compType.GetProperty(propName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (prop != null && prop.CanWrite && prop.PropertyType == typeof(string))
                        {
                            try
                            {
                                prop.SetValue(ghComponent, scriptToSet);
                                scriptSet = true;
                                _logger.LogInformation("Script set using property {PropertyName} for component {ComponentId}", propName, componentId);
                                RhinoApp.WriteLine($"[MCP] Script set via {propName} property on {componentId}");
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to set script using property {PropertyName} for component {ComponentId}", propName, componentId);
                            }
                        }
                    }
                }

                // Try setting via an input parameter (unified Script component often exposes a code input)
                if (!scriptSet)
                {
                    try
                    {
                        var candidateParamNames = new[] { "Code", "Script", "Source", "Text", "ScriptText" };
                        foreach (var input in ghComponent.Params.Input)
                        {
                            var name = input.Name ?? string.Empty;
                            var nick = input.NickName ?? string.Empty;
                            if (candidateParamNames.Any(c => name.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0 || nick.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                // Only attempt for string-capable params
                                if (input is Param_String ps)
                                {
                                    ps.PersistentData.Clear();
                                    ps.PersistentData.Append(new Grasshopper.Kernel.Types.GH_String(scriptToSet));
                                    scriptSet = true;
                                    _logger.LogInformation("Script set via input parameter {Param} for component {ComponentId}", input.Name, componentId);
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed attempting to set script via input parameter for component {ComponentId}", componentId);
                    }
                }

                if (!scriptSet)
                {
                    _logger.LogError("Unable to set script on component {ComponentId} of type {ComponentType}", componentId, compType.Name);
                    // Last-resort fallback: inject code via a Panel and wire it into a likely 'Code/Script/Source' input
                    try
                    {
                        var targetInput = ghComponent.Params.Input.FirstOrDefault(p =>
                            (p.Name?.IndexOf("code", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                            (p.Name?.IndexOf("script", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                            (p.Name?.IndexOf("source", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                            (p.NickName?.IndexOf("code", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                            (p.NickName?.IndexOf("script", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                            (p.NickName?.IndexOf("source", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

                        if (targetInput != null)
                        {
                            var panel = new GH_Panel
                            {
                                UserText = scriptToSet
                            };

                            // Place panel near the component
                            var pivot = ghComponent.Attributes?.Pivot ?? new System.Drawing.PointF(0, 0);
                            panel.CreateAttributes();
                            if (panel.Attributes != null)
                            {
                                panel.Attributes.Pivot = new System.Drawing.PointF(pivot.X - 200, pivot.Y);
                            }

                            doc.AddObject(panel, false);

                            // GH_Panel itself is an IGH_Param; wire it directly as source
                            IGH_Param panelParam = panel;
                            if (targetInput.SourceCount > 0)
                            {
                                targetInput.RemoveAllSources();
                            }
                            targetInput.AddSource(panelParam);

                            // Recompute to propagate
                            targetInput.CollectData();
                            targetInput.ComputeData();
                            doc.NewSolution(false);

                            _logger.LogInformation("Script injected via Panel and wired into input {InputName} for component {ComponentId}", targetInput.Name, componentId);
                            tcs.SetResult(new ComponentValueResult(true, componentId, "Script", scriptToSet, "Script injected via panel and wired to input"));
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Panel injection fallback failed for component {ComponentId}", componentId);
                    }

                    tcs.SetResult(new ComponentValueResult(false, componentId, null, null, null, "Unable to set script on this component type"));
                    return;
                }

                // Sync parameters if available (e.g. Python components)
                try
                {
                    var syncMethod = compType.GetMethod("SetParametersFromScript", flags);
                    if (syncMethod != null && syncMethod.GetParameters().Length == 0)
                    {
                        syncMethod.Invoke(ghComponent, null);
                        _logger.LogInformation("Called SetParametersFromScript for {ComponentId}", componentId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SetParametersFromScript failed for {ComponentId}", componentId);
                }

                // Expire solution on the component to trigger recompute
                ghComponent.ExpireSolution(true);

                // Log success
                _logger.LogInformation("Successfully set script for component {ComponentId}. Script length: {ScriptLength}", componentId, scriptToSet.Length);

                tcs.SetResult(new ComponentValueResult(true, componentId, "Script", scriptToSet, "Script updated successfully"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting script for component {ComponentId}", componentId);
                tcs.SetResult(new ComponentValueResult(false, componentId, null, null, null, ex.Message));
            }
        });

        return await tcs.Task;
    }

    private static bool TrySetComponentLanguage(IGH_Component component, Type compType, string normalizedLanguage, ILogger logger)
    {
        try
        {
            // Common method names to set language
            string[] methodNames =
            {
                "SetLanguage", "ChangeLanguage", "SetScriptLanguage", "SelectLanguage", "ChooseLanguage"
            };

            foreach (var name in methodNames)
            {
                var method = compType.GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (method == null) continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                var paramType = parameters[0].ParameterType;
                try
                {
                    if (paramType == typeof(string))
                    {
                        method.Invoke(component, new object[] { normalizedLanguage });
                        logger.LogDebug("Language set via method {Method} with string value {Language}", name, normalizedLanguage);
                        return true;
                    }
                    else if (paramType.IsEnum)
                    {
                        // Try to match enum value by name contains
                        var names = Enum.GetNames(paramType);
                        var match = names.FirstOrDefault(n => n.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || n.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (match != null)
                        {
                            var enumVal = Enum.Parse(paramType, match);
                            method.Invoke(component, new object[] { enumVal });
                            logger.LogDebug("Language set via method {Method} with enum value {Enum}", name, match);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Attempt to set language via method {Method} failed", name);
                }
            }

            // Heuristic: scan any single-parameter methods whose name suggests language/engine
            try
            {
                foreach (var m in compType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (m.GetParameters().Length != 1) continue;
                    var n = m.Name;
                    if (n.IndexOf("lang", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("engine", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var ptype = m.GetParameters()[0].ParameterType;
                    try
                    {
                        if (ptype == typeof(string))
                        {
                            m.Invoke(component, new object[] { normalizedLanguage });
                            logger.LogDebug("Language set via heuristic method {Method} with string {Language}", n, normalizedLanguage);
                            return true;
                        }
                        if (ptype.IsEnum)
                        {
                            var names = Enum.GetNames(ptype);
                            var match = names.FirstOrDefault(x => x.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || x.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (match != null)
                            {
                                var val = Enum.Parse(ptype, match);
                                m.Invoke(component, new object[] { val });
                                logger.LogDebug("Language set via heuristic method {Method} with enum {Enum}", n, match);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Heuristic method {Method} failed", n);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Heuristic language method scan failed");
            }

            // Common property/field names to set language
            string[] memberNames =
            {
                "Language", "ScriptLanguage", "SelectedLanguage", "Engine", "ActiveLanguage"
            };

            foreach (var mn in memberNames)
            {
                var prop = compType.GetProperty(mn, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                {
                    try
                    {
                        if (prop.PropertyType == typeof(string))
                        {
                            prop.SetValue(component, normalizedLanguage);
                            logger.LogDebug("Language set via property {Property} with string value {Language}", mn, normalizedLanguage);
                            return true;
                        }
                        else if (prop.PropertyType.IsEnum)
                        {
                            var names = Enum.GetNames(prop.PropertyType);
                            var match = names.FirstOrDefault(n => n.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || n.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (match != null)
                            {
                                var enumVal = Enum.Parse(prop.PropertyType, match);
                                prop.SetValue(component, enumVal);
                                logger.LogDebug("Language set via property {Property} with enum value {Enum}", mn, match);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Attempt to set language via property {Property} failed", mn);
                    }
                }

                var field = compType.GetField(mn, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    try
                    {
                        if (field.FieldType == typeof(string))
                        {
                            field.SetValue(component, normalizedLanguage);
                            logger.LogDebug("Language set via field {Field} with string value {Language}", mn, normalizedLanguage);
                            return true;
                        }
                        else if (field.FieldType.IsEnum)
                        {
                            var names = Enum.GetNames(field.FieldType);
                            var match = names.FirstOrDefault(n => n.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || n.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (match != null)
                            {
                                var enumVal = Enum.Parse(field.FieldType, match);
                                field.SetValue(component, enumVal);
                                logger.LogDebug("Language set via field {Field} with enum value {Enum}", mn, match);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Attempt to set language via field {Field} failed", mn);
                    }
                }
            }

            // Heuristic: scan any property/field containing language/engine
            try
            {
                foreach (var p in compType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var n = p.Name;
                    if (!p.CanWrite) continue;
                    if (n.IndexOf("lang", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("engine", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    try
                    {
                        if (p.PropertyType == typeof(string))
                        {
                            p.SetValue(component, normalizedLanguage);
                            logger.LogDebug("Language set via heuristic property {Property} with string {Language}", n, normalizedLanguage);
                            return true;
                        }
                        if (p.PropertyType.IsEnum)
                        {
                            var names = Enum.GetNames(p.PropertyType);
                            var match = names.FirstOrDefault(x => x.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || x.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (match != null)
                            {
                                var val = Enum.Parse(p.PropertyType, match);
                                p.SetValue(component, val);
                                logger.LogDebug("Language set via heuristic property {Property} with enum {Enum}", n, match);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Heuristic property {Property} failed", n);
                    }
                }

                foreach (var f in compType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var n = f.Name;
                    if (n.IndexOf("lang", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("engine", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    try
                    {
                        if (f.FieldType == typeof(string))
                        {
                            f.SetValue(component, normalizedLanguage);
                            logger.LogDebug("Language set via heuristic field {Field} with string {Language}", n, normalizedLanguage);
                            return true;
                        }
                        if (f.FieldType.IsEnum)
                        {
                            var names = Enum.GetNames(f.FieldType);
                            var match = names.FirstOrDefault(x => x.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase) || x.IndexOf(normalizedLanguage, StringComparison.OrdinalIgnoreCase) >= 0);
                            if (match != null)
                            {
                                var val = Enum.Parse(f.FieldType, match);
                                f.SetValue(component, val);
                                logger.LogDebug("Language set via heuristic field {Field} with enum {Enum}", n, match);
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Heuristic field {Field} failed", n);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Heuristic language property/field scan failed");
            }

            // As a last resort, dump available members once for diagnostics
            try
            {
                var members = compType.GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    .Select(m => m.MemberType + ":" + m.Name)
                    .Take(200);
                logger.LogInformation("Script component type {Type} has members: {Members}", compType.FullName, string.Join(", ", members));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to dump member list for diagnostics");
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unexpected error in TrySetScriptViaReflection");
            return false;
        }
    }

    #endregion

    #region Component Listing & Canvas State

    public async Task<AvailableComponentsResult> ListAvailableComponentsAsync(string? category = null, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<AvailableComponentsResult>();

        _logger.LogInformation("Listing available components. Category filter: {Category}", category ?? "None");

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var componentProxies = Grasshopper.Instances.ComponentServer.ObjectProxies;
                if (componentProxies == null)
                {
                    tcs.SetResult(new AvailableComponentsResult(
                        false,
                        new List<AvailableComponentInfo>().AsReadOnly(),
                        0,
                        "ComponentServer not available"));
                    return;
                }

                var components = new List<AvailableComponentInfo>();

                foreach (var proxy in componentProxies)
                {
                    if (proxy?.Desc == null) continue;

                    // Apply category filter if specified
                    if (!string.IsNullOrWhiteSpace(category) &&
                        !proxy.Desc.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var componentInfo = new AvailableComponentInfo(
                        proxy.Desc.Name,
                        proxy.Desc.Category,
                        proxy.Desc.SubCategory,
                        proxy.Desc.Description,
                        false); // Note: Obsolete status not available in this Grasshopper version

                    components.Add(componentInfo);
                }

                // Sort by category, then by name for better organization
                var sortedComponents = components
                    .OrderBy(c => c.Category)
                    .ThenBy(c => c.SubCategory)
                    .ThenBy(c => c.Name)
                    .ToList();

                var result = new AvailableComponentsResult(
                    true,
                    sortedComponents.AsReadOnly(),
                    sortedComponents.Count);

                _logger.LogInformation("Found {Count} available components", sortedComponents.Count);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing available components");
                tcs.SetResult(new AvailableComponentsResult(
                    false,
                    new List<AvailableComponentInfo>().AsReadOnly(),
                    0,
                    $"Error listing components: {ex.Message}"));
            }
        });

        return await tcs.Task;
    }

        public async Task<CanvasStateResult> CaptureCanvasStateAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<CanvasStateResult>();

        _logger.LogInformation("Capturing canvas state");

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new CanvasStateResult(
                        false,
                        "No active Grasshopper document",
                        null,
                        null,
                        DateTime.UtcNow,
                        0,
                        0,
                        new List<CanvasComponentInfo>(),
                        new List<CanvasConnectionInfo>(),
                        "No active Grasshopper document"));
                    return;
                }

                // Force document refresh to ensure all objects are properly registered
                doc.NewSolution(false);

                var components = new List<CanvasComponentInfo>();
                var connections = new List<CanvasConnectionInfo>();

                // Log the total object count for debugging
                _logger.LogDebug("Document has {TotalObjects} total objects", doc.Objects.Count);

                // Capture all components with better logging
                foreach (var obj in doc.Objects)
                {
                    _logger.LogDebug("Processing object: {ObjectType} - {ObjectName} - {ObjectGuid}", 
                        obj.GetType().Name, obj.NickName, obj.InstanceGuid);
                    
                    if (obj is IGH_Component component)
                    {
                        _logger.LogDebug("Found component: {ComponentType} - {ComponentName} - {ComponentGuid}", 
                            component.GetType().Name, component.NickName, component.InstanceGuid);
                        
                        var componentInfo = CaptureComponentInfo(component);
                        components.Add(componentInfo);
                    }
                    else if (obj is Grasshopper.Kernel.Special.GH_Group group)
                    {
                        // Groups don't have traditional connections, but we can log them for debugging
                        _logger.LogDebug("Group {GroupName} at position ({X}, {Y})", 
                            group.NickName, group.Attributes.Pivot.X, group.Attributes.Pivot.Y);
                    }
                    else
                    {
                        _logger.LogDebug("Object is not a component or group: {ObjectType} - {ObjectName}", 
                            obj.GetType().Name, obj.NickName);
                    }
                }

                // Capture all connections
                foreach (var obj in doc.Objects)
                {
                    if (obj is IGH_Component component)
                    {
                        var componentConnections = CaptureComponentConnections(component);
                        connections.AddRange(componentConnections);
                    }
                    else if (obj is Grasshopper.Kernel.Special.GH_Group group)
                    {
                        // Groups don't have traditional connections, but we can log them for debugging
                        _logger.LogDebug("Group {GroupName} at position ({X}, {Y})", 
                            group.NickName, group.Attributes.Pivot.X, group.Attributes.Pivot.Y);
                    }
                }

                var result = new CanvasStateResult(
                    true,
                    $"Canvas state captured successfully at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                    doc.DisplayName,
                    doc.FilePath,
                    DateTime.UtcNow,
                    components.Count,
                    connections.Count,
                    components,
                    connections);

                _logger.LogInformation("Canvas state captured: {ComponentCount} components, {ConnectionCount} connections from {TotalObjects} total objects",
                    components.Count, connections.Count, doc.Objects.Count);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error capturing canvas state");
                tcs.SetResult(new CanvasStateResult(
                    false,
                    "Error capturing canvas state",
                    null,
                    null,
                    DateTime.UtcNow,
                    0,
                    0,
                    new List<CanvasComponentInfo>(),
                    new List<CanvasConnectionInfo>(),
                    ex.Message));
            }
        });

        return await tcs.Task;
    }

    private CanvasComponentInfo CaptureComponentInfo(IGH_Component component)
    {
        var inputs = new List<CanvasParameterInfo>();
        var outputs = new List<CanvasParameterInfo>();

        // Capture input parameters
        for (int i = 0; i < component.Params.Input.Count; i++)
        {
            var param = component.Params.Input[i];
            var paramInfo = new CanvasParameterInfo(
                param.NickName,
                i,
                param.TypeName,
                !param.VolatileData.IsEmpty,
                param.SourceCount > 0,
                param.SourceCount > 0 ? param.Sources[0].InstanceGuid.ToString() : null);
            inputs.Add(paramInfo);
        }

        // Capture output parameters
        for (int i = 0; i < component.Params.Output.Count; i++)
        {
            var param = component.Params.Output[i];
            var paramInfo = new CanvasParameterInfo(
                param.NickName,
                i,
                param.TypeName,
                !param.VolatileData.IsEmpty,
                param.Recipients.Count > 0,
                param.Recipients.Count > 0 ? param.Recipients[0].InstanceGuid.ToString() : null);
            outputs.Add(paramInfo);
        }

        // Get component position
        double x = 0, y = 0;
        if (component.Attributes?.Pivot != null)
        {
            x = component.Attributes.Pivot.X;
            y = component.Attributes.Pivot.Y;
        }

        // Get execution status
        string executionStatus = GetExecutionStatus(component);

        // Get runtime messages
        var errors = GetComponentErrors(component);
        var warnings = GetComponentWarnings(component);
        var infoMessages = GetComponentMessages(component);

        var supportsPreview = Utilities.ComponentStateHelper.SupportsPreview(component);
        var previewEnabled = supportsPreview && Utilities.ComponentStateHelper.GetPreviewEnabled(component);

        return new CanvasComponentInfo(
            component.InstanceGuid.ToString(),
            component.GetType().Name,
            component.NickName,
            component.Category,
            component.SubCategory,
            x,
            y,
            Utilities.ComponentStateHelper.GetEnabled(component),
            supportsPreview,
            previewEnabled,
            component.Phase != GH_SolutionPhase.Computed,
            executionStatus,
            errors,
            warnings,
            infoMessages,
            inputs,
            outputs);
    }

    private List<CanvasConnectionInfo> CaptureComponentConnections(IGH_Component component)
    {
        var connections = new List<CanvasConnectionInfo>();

        // Capture input connections
        for (int i = 0; i < component.Params.Input.Count; i++)
        {
            var param = component.Params.Input[i];
            if (param.SourceCount > 0)
            {
                var connection = new CanvasConnectionInfo(
                    param.Sources[0].InstanceGuid.ToString(),
                    $"Output_{i}", // Use input parameter index as a reasonable approximation
                    component.InstanceGuid.ToString(),
                    $"Input_{i}");
                connections.Add(connection);
            }
        }

        return connections;
    }

    private string GetExecutionStatus(IGH_Component component)
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

    private List<string> GetComponentMessages(IGH_Component component)
    {
        var messages = new List<string>();
        
        // Get runtime messages from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
        {
            messages.Add($"INFO: {message}");
        }

        return messages;
    }

    private List<string> GetComponentWarnings(IGH_Component component)
    {
        var warnings = new List<string>();
        
        // Get runtime warnings from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
        {
            warnings.Add($"WARNING: {message}");
        }

        return warnings;
    }

    private List<string> GetComponentErrors(IGH_Component component)
    {
        var errors = new List<string>();
        
        // Get runtime errors from the component
        foreach (var message in component.RuntimeMessages(GH_RuntimeMessageLevel.Error))
        {
            errors.Add($"ERROR: {message}");
        }

        return errors;
    }

    public async Task<ComponentValueResult> ModifyComponentParametersAsync(string componentId, Dictionary<string, string> newNames,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ComponentValueResult>();

                        if (string.IsNullOrWhiteSpace(componentId))
                {
                    return new ComponentValueResult(
                        false,
                        null,
                        null,
                        null,
                        "Component ID cannot be null or empty");
                }

                if (newNames == null || newNames.Count == 0)
                {
                    return new ComponentValueResult(
                        false,
                        null,
                        null,
                        null,
                        "New names dictionary cannot be null or empty");
                }

        _logger.LogInformation("Modifying component parameters: componentId={ComponentId}, newNames={NewNames}",
            componentId, string.Join(", ", newNames.Select(kv => $"{kv.Key}={kv.Value}")));

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        null,
                        null,
                        null,
                        "No active Grasshopper document"));
                    return;
                }

                // Find the component by ID using the new search method
                var component = FindComponentByGuid(componentId);
                
                if (component == null)
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        null,
                        null,
                        null,
                        $"Component with ID '{componentId}' not found"));
                    return;
                }

                // Check if this is a script component that supports parameter modification
                if (!IsScriptComponent(component))
                {
                    tcs.SetResult(new ComponentValueResult(
                        false,
                        null,
                        null,
                        null,
                        $"Component '{component.Name}' is not a script component that supports parameter modification"));
                    return;
                }

                var modifiedCount = 0;
                var errors = new List<string>();

                // Process input parameters
                for (int i = 0; i < component.Params.Input.Count; i++)
                {
                    var paramKey = $"input_{i}";
                    if (newNames.ContainsKey(paramKey))
                    {
                        try
                        {
                            var param = component.Params.Input[i];
                            // Try to modify the parameter name
                            if (ModifyParameterName(param, newNames[paramKey]))
                            {
                                modifiedCount++;
                                _logger.LogInformation("Modified input parameter {Index} name to '{NewName}'", 
                                    i, newNames[paramKey]);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to modify input parameter {i}: {ex.Message}");
                        }
                    }
                }

                // Process output parameters
                for (int i = 0; i < component.Params.Output.Count; i++)
                {
                    var paramKey = $"output_{i}";
                    if (newNames.ContainsKey(paramKey))
                    {
                        try
                        {
                            var param = component.Params.Output[i];
                            // Try to modify the parameter name
                            if (ModifyParameterName(param, newNames[paramKey]))
                            {
                                modifiedCount++;
                                _logger.LogInformation("Modified output parameter {Index} name to '{NewName}'", 
                                    i, newNames[paramKey]);
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to modify output parameter {i}: {ex.Message}");
                        }
                    }
                }

                // Refresh the component to update the UI
                component.ExpireSolution(true);
                doc.NewSolution(false);

                var result = new ComponentValueResult(
                    modifiedCount > 0,
                    componentId,
                    "Parameters",
                    modifiedCount.ToString(),
                    "Parameter names modified successfully",
                    errors.Count > 0 ? string.Join("; ", errors) : null);

                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error modifying component parameters");
                tcs.SetResult(new ComponentValueResult(
                    false,
                    null,
                    null,
                    null,
                    $"Error modifying component parameters: {ex.Message}"));
            }
        });

        return await tcs.Task;
    }

    private bool IsScriptComponent(IGH_Component component)
    {
        // Check if this is a script component that supports parameter modification
        // Look for common script component types
        var componentType = component.GetType().Name.ToLower();
        return componentType.Contains("script") || 
               componentType.Contains("python") || 
               componentType.Contains("csharp") ||
               componentType.Contains("c#");
    }

    private bool ModifyParameterName(IGH_Param param, string newName)
    {
        try
        {
            // Try to access the parameter as a ScriptVariableParam or similar
            // that supports name modification
            var paramType = param.GetType();
            
            // Use reflection to find and set the VariableName property if it exists
            var variableNameProperty = paramType.GetProperty("VariableName");
            if (variableNameProperty != null && variableNameProperty.CanWrite)
            {
                variableNameProperty.SetValue(param, newName);
                return true;
            }

            // Fallback: try to set the NickName directly
            var nickNameProperty = paramType.GetProperty("NickName");
            if (nickNameProperty != null && nickNameProperty.CanWrite)
            {
                nickNameProperty.SetValue(param, newName);
                return true;
            }

            // If we can't modify the name, return false
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds a component by its InstanceGuid
    /// </summary>
    private IGH_Component? FindComponentByGuid(string componentId)
    {
        try
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
            {
                _logger.LogWarning("No active Grasshopper document for component search");
                return null;
            }

            _logger.LogDebug("Searching for component {ComponentId} in document with {TotalObjects} objects", 
                componentId, doc.Objects.Count);

            // Try to parse the componentId as a GUID first
            if (Guid.TryParse(componentId, out var guid))
            {
                var component = doc.FindObject(guid, false) as IGH_Component;
                
                if (component != null)
                {
                    _logger.LogDebug("Found component by GUID: {ComponentId} -> {ComponentName} ({ComponentType})", 
                        componentId, component.NickName, component.GetType().Name);
                    return component;
                }
                else
                {
                    _logger.LogDebug("GUID {ComponentId} found in document but is not a component", componentId);
                }
            }

            // Fallback: try string comparison
            var componentByString = doc.Objects.OfType<IGH_Component>().FirstOrDefault(obj => 
                obj.InstanceGuid.ToString() == componentId);
            
            if (componentByString != null)
            {
                _logger.LogDebug("Found component by GUID: {ComponentId} -> {ComponentName} ({ComponentType})", 
                    componentId, componentByString.NickName, componentByString.GetType().Name);
                return componentByString;
            }

            // Log all components for debugging
            var allComponents = doc.Objects.OfType<IGH_Component>().ToList();
            _logger.LogDebug("Available components in document: {ComponentCount}", allComponents.Count);
            foreach (var comp in allComponents)
            {
                _logger.LogDebug("  Component: {ComponentType} - {ComponentName} - {ComponentGuid}", 
                    comp.GetType().Name, comp.NickName, comp.InstanceGuid);
            }

            _logger.LogWarning("Component not found with ID: {ComponentId}", componentId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding component by GUID: {ComponentId}", componentId);
            return null;
        }
    }

    /// <summary>
    /// Gets all components with their InstanceGuids for consistent identification
    /// </summary>
    private List<IGH_Component> GetAllComponents()
    {
        try
        {
            var doc = Instances.ActiveCanvas?.Document;
            if (doc == null)
            {
                _logger.LogWarning("No active Grasshopper document for component enumeration");
                return new List<IGH_Component>();
            }

            var components = doc.Objects
                .OfType<IGH_Component>()
                .ToList();

            _logger.LogDebug("Found {ComponentCount} components in document", components.Count);
            return components;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all components");
            return new List<IGH_Component>();
        }
    }

    /// <summary>
    /// Modifies the parameter names and types of a script component (Python, C#, etc.)
    /// </summary>
    public async Task<ComponentModificationResult> ModifyScriptComponentParametersAsync(string componentId, Dictionary<string, string> parameterConfig, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ComponentModificationResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new ComponentModificationResult(
                        false,
                        "No active Grasshopper document"));
                    return;
                }

                // Find the component
                var component = FindComponentByGuid(componentId);
                if (component == null)
                {
                    tcs.SetResult(new ComponentModificationResult(
                        false,
                        $"Component not found: {componentId}"));
                    return;
                }

                var modifications = new List<string>();
                var errors = new List<string>();

                // Process each parameter modification
                foreach (var kvp in parameterConfig)
                {
                    try
                    {
                        var paramIndex = kvp.Key;
                        var newName = kvp.Value;

                        // Parse parameter index (e.g., "input_0", "output_1")
                        var parts = paramIndex.Split('_');
                        if (parts.Length != 2 || !int.TryParse(parts[1], out var index))
                        {
                            errors.Add($"Invalid parameter index format: {paramIndex}");
                            continue;
                        }

                        var isInput = parts[0].Equals("input", StringComparison.OrdinalIgnoreCase);
                        var isOutput = parts[0].Equals("output", StringComparison.OrdinalIgnoreCase);

                        if (!isInput && !isOutput)
                        {
                            errors.Add($"Parameter type must be 'input' or 'output': {paramIndex}");
                            continue;
                        }

                        var paramList = isInput ? component.Params.Input : component.Params.Output;
                        
                        if (index < 0 || index >= paramList.Count)
                        {
                            errors.Add($"Parameter index out of range: {index} (max: {paramList.Count - 1})");
                            continue;
                        }

                        var param = paramList[index];
                        
                        // Try to modify the parameter name using standard Grasshopper APIs
                        if (ModifyScriptParameterName(param, newName))
                        {
                            modifications.Add($"Modified {paramIndex} to '{newName}'");
                        }
                        else
                        {
                            errors.Add($"Could not modify parameter {paramIndex}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error modifying {kvp.Key}: {ex.Message}");
                    }
                }

                // Refresh the component
                component.ExpireSolution(true);
                doc.NewSolution(false);

                var success = modifications.Count > 0 && errors.Count == 0;
                var message = success 
                    ? $"Successfully modified {modifications.Count} parameters"
                    : $"Modified {modifications.Count} parameters with {errors.Count} errors";

                var result = new ComponentModificationResult(success, message)
                {
                    Modifications = modifications,
                    Errors = errors
                };

                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error modifying component parameters");
                tcs.SetResult(new ComponentModificationResult(
                    false,
                    $"Error modifying parameters: {ex.Message}"));
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Modifies the name of a script parameter using standard Grasshopper APIs
    /// </summary>
    private bool ModifyScriptParameterName(IGH_Param param, string newName)
    {
        try
        {
            // For script components, we need to handle different parameter types
            var paramType = param.GetType();
            
            // Try to access the VariableName property if it exists (Rhino 8+)
            var variableNameProperty = paramType.GetProperty("VariableName");
            if (variableNameProperty != null && variableNameProperty.CanWrite)
            {
                variableNameProperty.SetValue(param, newName);
                _logger.LogDebug("Modified parameter VariableName to: {NewName}", newName);
                return true;
            }

            // Try to access the NickName property (standard Grasshopper)
            var nickNameProperty = paramType.GetProperty("NickName");
            if (nickNameProperty != null && nickNameProperty.CanWrite)
            {
                nickNameProperty.SetValue(param, newName);
                _logger.LogDebug("Modified parameter NickName to: {NewName}", newName);
                return true;
            }

            // Try to access the Name property (standard Grasshopper)
            var nameProperty = paramType.GetProperty("Name");
            if (nameProperty != null && nameProperty.CanWrite)
            {
                nameProperty.SetValue(param, newName);
                _logger.LogDebug("Modified parameter Name to: {NewName}", newName);
                return true;
            }

            // If none of the above work, try using the existing ModifyParameterName method
            return ModifyParameterName(param, newName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error modifying script parameter name to '{NewName}'", newName);
            return false;
        }
    }

    /// <summary>
    /// Modifies the name of a parameter using reflection
    /// </summary>

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the active Grasshopper document, or null if none is available.
    /// </summary>
    private static GH_Document? GetActiveDocument()
    {
        return Instances.ActiveCanvas?.Document;
    }

    /// <summary>
    /// Executes an action on the UI thread and returns a task that completes with the result.
    /// </summary>
    private static async Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();
        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return await tcs.Task;
    }

    /// <summary>
    /// Executes an action on the UI thread with document validation.
    /// </summary>
    private async Task<T> InvokeOnUiThreadWithDocumentAsync<T>(
        Func<GH_Document, T> action,
        Func<string, T> errorResult)
    {
        var tcs = new TaskCompletionSource<T>();
        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = GetActiveDocument();
                if (doc == null)
                {
                    tcs.SetResult(errorResult("No active Grasshopper document"));
                    return;
                }
                tcs.SetResult(action(doc));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in UI thread operation");
                tcs.SetResult(errorResult(ex.Message));
            }
        });
        return await tcs.Task;
    }

    /// <summary>
    /// Finds a component by its GUID string, returning null if not found.
    /// </summary>
    private IGH_Component? FindComponentById(GH_Document doc, string componentId)
    {
        if (!Guid.TryParse(componentId, out var guid))
        {
            _logger.LogWarning("Invalid component ID format: {ComponentId}", componentId);
            return null;
        }

        var obj = doc.FindObject(guid, true);
        if (obj is IGH_Component component)
        {
            return component;
        }

        _logger.LogWarning("Component not found or not a component: {ComponentId}", componentId);
        return null;
    }

    /// <summary>
    /// Normalizes a language identifier to a standard format.
    /// </summary>
    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return string.Empty;

        return language.Trim().ToLowerInvariant() switch
        {
            "py" or "python" or "python3" or "py3" => "Python",
            "c#" or "cs" or "csharp" => "CSharp",
            _ => language.Trim()
        };
    }

    /// <summary>
    /// Adds a component to the document at the specified position and refreshes the solution.
    /// </summary>
    private static void AddComponentToDocument(GH_Document doc, IGH_DocumentObject component, double x, double y)
    {
        if (component.Attributes == null)
        {
            component.CreateAttributes();
        }

        component.Attributes!.Pivot = new PointF((float)x, (float)y);

        if (component is IGH_Component ghComponent)
        {
            doc.AddObject(ghComponent, false);
        }
        else
        {
            doc.AddObject(component, false);
        }

        doc.NewSolution(false);
    }

    /// <summary>
    /// Creates a ComponentCreationResult from a component.
    /// </summary>
    private static ComponentCreationResult CreateSuccessResult(IGH_DocumentObject component)
    {
        return new ComponentCreationResult(
            true,
            component.InstanceGuid.ToString(),
            component.GetType().Name,
            component.NickName,
            component.Attributes?.Pivot.X ?? 0,
            component.Attributes?.Pivot.Y ?? 0);
    }

    /// <summary>
    /// Creates a ComponentCreationResult for an error case.
    /// </summary>
    private static ComponentCreationResult CreateErrorResult(string errorMessage)
    {
        return new ComponentCreationResult(
            false,
            null,
            null,
            null,
            0,
            0,
            errorMessage);
    }

    /// <summary>
    /// Resolves a parameter by name or index from a document object (component or param).
    /// </summary>
    private static IGH_Param? ResolveParameter(IGH_DocumentObject obj, string nameOrIndex, bool isTargetParam)
    {
        if (obj is IGH_Component comp)
        {
            var list = isTargetParam ? comp.Params.Input : comp.Params.Output;

            var byName = list.FirstOrDefault(p =>
                string.Equals(p.Name, nameOrIndex, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.NickName, nameOrIndex, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            if (int.TryParse(nameOrIndex, out var idx) && idx >= 0 && idx < list.Count)
                return list[idx];
        }
        else if (obj is IGH_Param param)
        {
            return param;
        }

        return null;
    }

    #endregion
}