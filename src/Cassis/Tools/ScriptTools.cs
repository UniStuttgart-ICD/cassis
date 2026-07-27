using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Cassis.Extensions;
using Cassis.Models;
using Cassis.Services;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace Cassis.Tools;

/// <summary>
/// Tools for inspecting, editing, and listing script components.
/// </summary>
[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "List_Python_Scripts")]
    [Description("Lists Python script components in the active document with positions")]
    public static async Task<CallToolResult> ListPythonScripts()
    {
        return await ListScriptsInternal(IsPythonScriptComponent, "python", nameof(ListPythonScripts));
    }

    [McpServerTool(Name = "List_CSharp_Scripts")]
    [Description("Lists C# script components in the active document with positions")]
    public static async Task<CallToolResult> ListCSharpScripts()
    {
        return await ListScriptsInternal(IsCSharpScriptComponent, "csharp", nameof(ListCSharpScripts));
    }

    [McpServerTool(Name = "Get_Python_Script")]
    [Description("Gets Python script source code from a script component")]
    public static async Task<CallToolResult> GetPythonScript(
        [Description("Component GUID of the Python script node")] string componentId)
    {
        return await GetScriptInternal(componentId, "python", nameof(GetPythonScript));
    }

    [McpServerTool(Name = "Get_CSharp_Script")]
    [Description("Gets C# script source code from a script component")]
    public static async Task<CallToolResult> GetCSharpScript(
        [Description("Component GUID of the C# script node")] string componentId)
    {
        return await GetScriptInternal(componentId, "csharp", nameof(GetCSharpScript));
    }

    [McpServerTool(Name = "Edit_Python_Script")]
    [Description("Updates the code for a Python script component and returns compilation diagnostics")]
    public static async Task<CallToolResult> EditPythonScript(
        IGrasshopperComponentService componentService,
        [Description("Component GUID of the Python script node")] string componentId,
        [Description("Python source code")] string code)
    {
        return await EditScriptInternal(componentService, componentId, "python", code);
    }

    [McpServerTool(Name = "Edit_CSharp_Script")]
    [Description("Updates the code for a C# script component and returns compilation diagnostics. Rhino 8 defaults are outputs 'out' and lowercase 'a' (not 'A'); use script-mode like `a = ...;` or SDK-mode with `RunScript(..., ref object a)`. For nontrivial scripts, check https://developer.rhino3d.com/guides/scripting/scripting-gh-csharp/ first.")]
    public static async Task<CallToolResult> EditCSharpScript(
        IGrasshopperComponentService componentService,
        [Description("Component GUID of the C# script node")] string componentId,
        [Description("C# source code. Default result output is lowercase 'a'; call Get_CSharp_Script_Errors after editing.")] string code)
    {
        return await EditScriptInternal(componentService, componentId, "csharp", code);
    }

    [McpServerTool(Name = "Edit_Script")]
    [Description("Surgical edit on a script component (Python or C#). Commands: 'str_replace' finds exactly one occurrence of old_str and replaces with new_str. 'insert' inserts new_str after insert_after_line (0 = beginning). 'delete' removes lines from start_line to end_line (1-indexed, inclusive). Use Get_Python_Script or Get_CSharp_Script first to see current code.")]
    public static async Task<CallToolResult> EditScript(
        IGrasshopperComponentService componentService,
        [Description("Component GUID")] string componentId,
        [Description("Command: 'str_replace', 'insert', or 'delete'")] string command = "str_replace",
        [Description("For str_replace: exact text to find (must match exactly once)")] string? old_str = null,
        [Description("For str_replace/insert: replacement or new text")] string? new_str = null,
        [Description("For insert: line number after which to insert (0 = beginning)")] int? insert_after_line = null,
        [Description("For delete: first line to delete (1-indexed)")] int? start_line = null,
        [Description("For delete: last line to delete (1-indexed, inclusive)")] int? end_line = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            // Read current script
            var (currentScript, language) = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document
                    ?? throw new InvalidOperationException("No active Grasshopper document");
                if (!Guid.TryParse(componentId, out var guid))
                    throw new ArgumentException("Invalid component GUID format");
                var component = doc.FindObject(guid, false)
                    ?? throw new ArgumentException($"Component {componentId} not found");
                var script = TryExtractScript(component)
                    ?? throw new InvalidOperationException("Could not read script from component");
                var lang = DetectLanguage(component);
                return (script, lang);
            });

            // Apply transformation based on command
            string newScript;
            string description;

            switch (command.ToLowerInvariant())
            {
                case "str_replace":
                {
                    if (string.IsNullOrEmpty(old_str))
                        throw new ArgumentException("old_str is required for str_replace command");
                    if (new_str == null)
                        throw new ArgumentException("new_str is required for str_replace command");

                    int count = CountOccurrences(currentScript, old_str);
                    if (count == 0)
                        throw new ArgumentException("No match found for old_str. Ensure it matches the current script exactly, including whitespace and indentation.");
                    if (count > 1)
                        throw new ArgumentException($"Found {count} matches for old_str. Provide more surrounding context to make a unique match.");

                    newScript = currentScript.Replace(old_str, new_str);
                    description = "Replaced 1 occurrence";
                    break;
                }
                case "insert":
                {
                    if (insert_after_line == null)
                        throw new ArgumentException("insert_after_line is required for insert command");
                    if (string.IsNullOrEmpty(new_str))
                        throw new ArgumentException("new_str is required for insert command");

                    var lines = currentScript.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                    int lineIndex = insert_after_line.Value;
                    if (lineIndex < 0 || lineIndex > lines.Count)
                        throw new ArgumentException($"insert_after_line {lineIndex} is out of range (0-{lines.Count})");

                    var newLines = new_str.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    lines.InsertRange(lineIndex, newLines);
                    newScript = string.Join("\n", lines);
                    description = $"Inserted {newLines.Length} line(s) after line {lineIndex}";
                    break;
                }
                case "delete":
                {
                    if (start_line == null || end_line == null)
                        throw new ArgumentException("start_line and end_line are required for delete command");
                    if (start_line < 1)
                        throw new ArgumentException("start_line must be >= 1");
                    if (end_line < start_line)
                        throw new ArgumentException("end_line must be >= start_line");

                    var lines = currentScript.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                    if (end_line > lines.Count)
                        throw new ArgumentException($"end_line {end_line} exceeds script length ({lines.Count} lines)");

                    int removeCount = end_line.Value - start_line.Value + 1;
                    lines.RemoveRange(start_line.Value - 1, removeCount);
                    newScript = string.Join("\n", lines);
                    description = $"Deleted {removeCount} line(s) ({start_line}-{end_line})";
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown command: {command}. Use 'str_replace', 'insert', or 'delete'.");
            }

            // Set modified script
            await componentService.SetComponentScriptAsync(componentId, language, newScript);

            // Return diagnostics
            var errors = await CollectScriptErrorsAsync(componentId);
            return new
            {
                success = true,
                componentId,
                language,
                command,
                description,
                diagnostics = errors
            };
        }, "Edit_Script");
    }

    [McpServerTool(Name = "Get_Python_Script_Errors")]
    [Description("Gets compilation errors and warnings for a Python script component")]
    public static async Task<CallToolResult> GetPythonScriptErrors(
        [Description("Component GUID of the Python script node")] string componentId)
    {
        return await GetScriptErrorsInternal(componentId, "python", nameof(GetPythonScriptErrors));
    }

    [McpServerTool(Name = "Get_CSharp_Script_Errors")]
    [Description("Gets compilation errors and warnings for a C# script component")]
    public static async Task<CallToolResult> GetCSharpScriptErrors(
        [Description("Component GUID of the C# script node")] string componentId)
    {
        return await GetScriptErrorsInternal(componentId, "csharp", nameof(GetCSharpScriptErrors));
    }

    private static async Task<CallToolResult> ListScriptsInternal(
        Func<IGH_DocumentObject, bool> predicate,
        string language,
        string operationName)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var scripts = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document;
                if (document == null)
                {
                    throw new InvalidOperationException("No active Grasshopper document");
                }

                return document.Objects
                    .Where(predicate)
                    .Select(obj => new ScriptComponentInfo
                    {
                        Id = obj.InstanceGuid,
                        Name = obj.Name ?? $"{language.ToUpperInvariant()} Script",
                        NickName = obj.NickName ?? string.Empty,
                        Position = new Position
                        {
                            X = obj.Attributes?.Pivot.X ?? 0f,
                            Y = obj.Attributes?.Pivot.Y ?? 0f
                        }
                    })
                    .ToList();
            });

            return new
            {
                success = true,
                language,
                count = scripts.Count,
                scripts
            };
        }, operationName);
    }

    private static async Task<CallToolResult> GetScriptInternal(string componentId, string expectedLanguage, string operationName)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var scriptResult = await UiThreadHelper.InvokeAsync(() =>
            {
                var document = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentId, out var guid))
                {
                    throw new ArgumentException("Invalid component GUID format");
                }

                var component = document.FindObject(guid, false) as IGH_DocumentObject;
                if (component == null)
                {
                    throw new ArgumentException($"Component {componentId} not found");
                }

                var language = DetectLanguage(component);
                if (!string.IsNullOrEmpty(expectedLanguage) &&
                    !string.Equals(language, expectedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    RhinoApp.WriteLine($"[ScriptTools] Language mismatch: expected {expectedLanguage}, detected {language}");
                }

                var code = TryExtractScript(component) ?? string.Empty;

                return new ScriptResult
                {
                    ComponentId = componentId,
                    Code = code,
                    Language = string.IsNullOrEmpty(language) ? expectedLanguage : language
                };
            });

            return scriptResult;
        }, operationName);
    }

    private static async Task<CallToolResult> EditScriptInternal(
        IGrasshopperComponentService componentService,
        string componentId,
        string language,
        string code)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentService), componentService),
                (nameof(componentId), componentId),
                (nameof(code), code));

            await componentService.SetComponentScriptAsync(componentId, language, code);

            // After updating the script, surface compilation diagnostics to the caller.
            var errors = await CollectScriptErrorsAsync(componentId);

            return new
            {
                success = true,
                componentId,
                language,
                diagnostics = errors
            };
        }, $"Edit_{language}_Script");
    }

    private static async Task<CallToolResult> GetScriptErrorsInternal(string componentId, string language, string operationName)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var diagnostics = await CollectScriptErrorsAsync(componentId);

            return new
            {
                success = true,
                componentId,
                language,
                diagnostics
            };
        }, operationName);
    }

    private static async Task<ScriptErrors> CollectScriptErrorsAsync(string componentId)
    {
        return await UiThreadHelper.InvokeAsync(() =>
        {
            var document = Instances.ActiveCanvas?.Document;
            if (document == null)
            {
                throw new InvalidOperationException("No active Grasshopper document");
            }

            if (!Guid.TryParse(componentId, out var guid))
            {
                throw new ArgumentException("Invalid component GUID format");
            }

            var component = document.FindObject(guid, false) as IGH_Component;
            if (component == null)
            {
                throw new ArgumentException($"Component {componentId} not found or is not a script component");
            }

            var errors = component.RuntimeMessages(GH_RuntimeMessageLevel.Error)
                .Select(message => new ScriptError
                {
                    Message = message,
                    Level = "error"
                })
                .ToList();

            var warnings = component.RuntimeMessages(GH_RuntimeMessageLevel.Warning)
                .Select(message => new ScriptError
                {
                    Message = message,
                    Level = "warning"
                })
                .ToList();

            return new ScriptErrors
            {
                Errors = errors,
                Warnings = warnings,
                RawMessage = errors.Count == 0 && warnings.Count == 0
                    ? "No script diagnostics reported."
                    : $"Errors: {errors.Count}, Warnings: {warnings.Count}"
            };
        });
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static bool IsPythonScriptComponent(IGH_DocumentObject obj)
    {
        var typeName = obj.GetType().FullName ?? string.Empty;
        return typeName.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCSharpScriptComponent(IGH_DocumentObject obj)
    {
        var typeName = obj.GetType().FullName ?? string.Empty;
        var simpleName = obj.GetType().Name ?? string.Empty;

        // Check for C# script component indicators
        // RhinoCodePluginGH uses "CSharpComponent" in the type name
        bool isCSharp = typeName.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        simpleName.IndexOf("CSharp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        (typeName.IndexOf("CS", StringComparison.OrdinalIgnoreCase) >= 0 && 
                         typeName.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0);

        // Exclude other script languages
        if (typeName.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 ||
            typeName.IndexOf("VB", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        // Also check if it's a script component by checking if it has script-related properties
        if (!isCSharp)
        {
            var compType = obj.GetType();
            // Check for common script component properties/methods
            var hasScriptProperty = compType.GetProperty("Script") != null ||
                                   compType.GetProperty("Context") != null ||
                                   compType.GetMethod("SetSource") != null;
            
            // If it has script properties but isn't Python/VB, and name suggests C#, it's likely C#
            if (hasScriptProperty && 
                (simpleName.IndexOf("CS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 simpleName.IndexOf("C#", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                isCSharp = true;
            }
        }

        return isCSharp;
    }

    private static string DetectLanguage(IGH_DocumentObject component)
    {
        var typeName = component.GetType().FullName ?? string.Empty;
        if (typeName.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "python";
        }

        if (typeName.IndexOf("csharp", StringComparison.OrdinalIgnoreCase) >= 0 ||
            typeName.IndexOf("cs", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "csharp";
        }

        return string.Empty;
    }

    private static string? TryExtractScript(IGH_DocumentObject component)
    {
        try
        {
            var compType = component.GetType();

            // Strategy 0: Try GetSource() method directly on component
            var allMethods = compType.GetMethods(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            
            var getSourceMethods = allMethods.Where(m => 
                m.Name.Equals("GetSource", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("GetScript", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("GetCode", StringComparison.OrdinalIgnoreCase)).ToList();
            
            foreach (var method in getSourceMethods)
            {
                try
                {
                    var paramCount = method.GetParameters().Length;
                    object? source = null;
                    if (paramCount == 0)
                    {
                        source = method.Invoke(component, null);
                    }
                    else if (paramCount == 1)
                    {
                        var paramType = method.GetParameters()[0].ParameterType;
                        object? paramValue = null;
                        if (paramType == typeof(bool))
                            paramValue = false;
                        else if (paramType.IsValueType)
                            paramValue = Activator.CreateInstance(paramType);
                        
                        source = method.Invoke(component, new[] { paramValue });
                    }
                    
                    if (source is string sourceStr && !string.IsNullOrEmpty(sourceStr))
                    {
                        return sourceStr;
                    }
                    else if (source != null)
                    {
                        var scriptText = ExtractScriptTextFromObject(source);
                        if (!string.IsNullOrEmpty(scriptText))
                        {
                            return scriptText;
                        }
                    }
                }
                catch (Exception)
                {
                    // Reflection method failed; continue to next strategy
                }
            }

            // Strategy 1: Try Context field (it's a field, not a property!)
            var contextField = compType.GetField("Context",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            
            if (contextField != null)
            {
                var context = contextField.GetValue(component);
                if (context != null)
                {
                    var contextType = context.GetType();
                    
                    // Try Context.GetSource() method
                    var contextGetSourceMethod = contextType.GetMethod("GetSource",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    
                    if (contextGetSourceMethod != null && contextGetSourceMethod.GetParameters().Length == 0)
                    {
                        try
                        {
                            var source = contextGetSourceMethod.Invoke(context, null);
                            if (source is string sourceStr && !string.IsNullOrEmpty(sourceStr))
                            {
                                return sourceStr;
                            }
                        }
                        catch (Exception)
                        {
                            // Context.GetSource() failed; try next strategy
                        }
                    }

                    // Try Context.GetScript() method
                    var getScriptMethod = contextType.GetMethod("GetScript",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    
                    if (getScriptMethod != null && getScriptMethod.GetParameters().Length == 0)
                    {
                        try
                        {
                            var scriptObj = getScriptMethod.Invoke(context, null);
                            if (scriptObj != null)
                            {
                                var scriptText = ExtractScriptTextFromObject(scriptObj);
                                if (!string.IsNullOrEmpty(scriptText))
                                {
                                    return scriptText;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Context.GetScript() failed; try next strategy
                        }
                    }

                    // Try Context.Script property
                    var scriptProperty = contextType.GetProperty("Script",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    
                    if (scriptProperty != null)
                    {
                        try
                        {
                            var scriptObj = scriptProperty.GetValue(context);
                            if (scriptObj != null)
                            {
                                var scriptText = ExtractScriptTextFromObject(scriptObj);
                                if (!string.IsNullOrEmpty(scriptText))
                                {
                                    return scriptText;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Context.Script property failed; try next strategy
                        }
                    }
                }
            }

            // Strategy 2: Direct Script/Code/Source properties on component
            var scriptProps = new[] { "Script", "Code", "Source", "ScriptSource", "Text", "SourceCode", "ScriptText" };
            foreach (var propName in scriptProps)
            {
                var prop = compType.GetProperty(propName,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                
                if (prop != null)
                {
                    try
                    {
                        var value = prop.GetValue(component);
                        if (value != null)
                        {
                            if (value is string strValue && !string.IsNullOrEmpty(strValue))
                            {
                                return strValue;
                            }
                            
                            var scriptText = ExtractScriptTextFromObject(value);
                            if (!string.IsNullOrEmpty(scriptText))
                            {
                                return scriptText;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Property access failed; continue to next property
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[ScriptTools] Failed to extract script: {ex.Message}");
        }

        return null;
    }

    private static string? ExtractScriptTextFromObject(object scriptObj)
    {
        if (scriptObj == null)
            return null;

        try
        {
            var scriptType = scriptObj.GetType();
            
            // Strategy 1: Try GetSource(), GetScript(), GetCode() methods
            var allMethods = scriptType.GetMethods(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            
            var getterMethods = allMethods.Where(m => 
                (m.Name.Equals("GetSource", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Equals("GetScript", StringComparison.OrdinalIgnoreCase) ||
                 m.Name.Equals("GetCode", StringComparison.OrdinalIgnoreCase)) &&
                m.GetParameters().Length == 0).ToList();
            
            foreach (var method in getterMethods)
            {
                try
                {
                    var result = method.Invoke(scriptObj, null);
                    if (result is string strResult && !string.IsNullOrEmpty(strResult))
                    {
                        return strResult;
                    }
                }
                catch (Exception)
                {
                    // Reflection method failed; continue to next method
                }
            }

            // Strategy 2: Try Text, Source, Code, Script properties
            var textProperty = scriptType.GetProperty("Text",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic) ??
                scriptType.GetProperty("Source",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic) ??
                scriptType.GetProperty("Code",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic) ??
                scriptType.GetProperty("Script",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
            
            if (textProperty != null)
            {
                var scriptText = textProperty.GetValue(scriptObj) as string;
                if (!string.IsNullOrEmpty(scriptText))
                {
                    return scriptText;
                }
            }
            
            // Strategy 3: Check all string properties
            var allProps = scriptType.GetProperties(System.Reflection.BindingFlags.Instance | 
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.NonPublic);
            
            foreach (var prop in allProps)
            {
                if (prop.PropertyType == typeof(string) && prop.CanRead)
                {
                    try
                    {
                        var value = prop.GetValue(scriptObj) as string;
                        if (!string.IsNullOrEmpty(value) && value.Length > 10)
                        {
                            // Check if it looks like code
                            if (value.Contains("#") || value.Contains("def ") || value.Contains("import ") || 
                                value.Contains("print(") || (value.Contains("=") && value.Contains("\n")))
                            {
                                return value;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Property access failed; continue to next property
                    }
                }
                else if (prop.CanRead)
                {
                    // Check nested objects for script-related properties
                    if (prop.Name.IndexOf("Script", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.Name.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        prop.Name.IndexOf("Code", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try
                        {
                            var value = prop.GetValue(scriptObj);
                            if (value != null)
                            {
                                // Recursively try to extract from nested object
                                var nestedText = ExtractScriptTextFromObject(value);
                                if (!string.IsNullOrEmpty(nestedText))
                                {
                                    return nestedText;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // Nested script extraction failed; continue
                        }
                    }
                }
            }

            // Strategy 4: Try ToString() if it's not the default object.ToString()
            var toStringResult = scriptObj.ToString();
            if (toStringResult != null && 
                toStringResult != scriptType.FullName && 
                toStringResult.Length > 10)
            {
                return toStringResult;
            }
        }
        catch (Exception)
        {
            // Script text extraction failed entirely; return null
        }

        return null;
    }

    [McpServerTool(Name = "Add_CSharp_Script_Component")]
    [Description("Adds a C# Script component to the Grasshopper canvas and optionally sets its script. Rhino 8 defaults are outputs 'out' and lowercase 'a' (not 'A'); use script-mode like `a = ...;` or SDK-mode with `RunScript(..., ref object a)`. For nontrivial scripts, check https://developer.rhino3d.com/guides/scripting/scripting-gh-csharp/ first.")]
    public static async Task<CallToolResult> AddCSharpScriptComponent(
        IGrasshopperComponentService componentService,
        [Description("X coordinate on the canvas")] double x = 100,
        [Description("Y coordinate on the canvas")] double y = 100,
        [Description("Optional C# script source. Default result output is lowercase 'a'; call Get_CSharp_Script_Errors after setting.")] string? script = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            string newId = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                var candidateNames = new[] { "C# Script", "CSharp Script", "CSharp", "C#", "GhCSharp", "Script" };

                foreach (var name in candidateNames)
                {
                    try
                    {
                        var proxy = Grasshopper.Instances.ComponentServer.FindObjectByName(name, true, true);
                        if (proxy == null) continue;

                        var component = proxy.CreateInstance() as IGH_DocumentObject;
                        if (component == null) continue;

                        component.CreateAttributes();
                        component.Attributes.Pivot = new System.Drawing.PointF((float)x, (float)y);
                        doc.AddObject(component, false);
                        doc.NewSolution(false);
                        return component.InstanceGuid.ToString();
                    }
                    catch (Exception)
                    {
                        // Candidate component name failed; try next
                    }
                }

                throw new InvalidOperationException($"Unable to create a C# Script component. Tried: C# Script, CSharp, C#, GhCSharp, Script");
            });

            if (!string.IsNullOrWhiteSpace(script))
                await componentService.SetComponentScriptAsync(newId, "csharp", script);

            return new { success = true, id = newId, x, y };
        }, nameof(AddCSharpScriptComponent));
    }

    [McpServerTool(Name = "Modify_Script_Component_Parameters")]
    [Description("Renames input/output parameters on a script component. Keys: 'input_0', 'output_1', 'input_0:type', 'input_0:access'. Types: Number, Integer, Boolean, String, Point, Vector, Curve, Brep, Mesh, Plane. Access: item, list, tree.")]
    public static async Task<CallToolResult> ModifyScriptComponentParameters(
        [Description("Component GUID")] string componentId,
        [Description("Parameter configuration: e.g. {\"input_0\": \"radius\", \"input_0:type\": \"Number\", \"input_0:access\": \"item\"}")] Dictionary<string, string> parameterConfig)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

                if (!Guid.TryParse(componentId, out var guid))
                    throw new ArgumentException("Invalid component GUID format");

                var component = doc.FindObject(guid, false) as IGH_Component
                    ?? throw new InvalidOperationException("Component not found or is not a component");

                var typeName = component.GetType().FullName?.ToLowerInvariant() ?? string.Empty;
                bool isScript = typeName.Contains("csharp") || typeName.Contains("python") || typeName.Contains("script");

                var nameChanges = new Dictionary<string, string>();
                var typeChanges = new Dictionary<string, string>();
                var accessChanges = new Dictionary<string, string>();

                foreach (var kvp in parameterConfig)
                {
                    if (kvp.Key.EndsWith(":type"))
                        typeChanges[kvp.Key[..^5]] = kvp.Value;
                    else if (kvp.Key.EndsWith(":access"))
                        accessChanges[kvp.Key[..^7]] = kvp.Value;
                    else
                        nameChanges[kvp.Key] = kvp.Value;
                }

                int modified = 0;
                var errors = new List<string>();

                foreach (var kvp in nameChanges)
                {
                    if (kvp.Key.StartsWith("input_") && int.TryParse(kvp.Key[6..], out int idx) && idx >= 0 && idx < component.Params.Input.Count)
                    { component.Params.Input[idx].NickName = kvp.Value; modified++; }
                    else if (kvp.Key.StartsWith("output_") && int.TryParse(kvp.Key[7..], out int oidx) && oidx >= 0 && oidx < component.Params.Output.Count)
                    {
                        if (isScript && oidx == 0) errors.Add("output_0 reserved as 'out' for script components");
                        else { component.Params.Output[oidx].NickName = kvp.Value; modified++; }
                    }
                    else errors.Add($"Invalid key: {kvp.Key}");
                }

                foreach (var kvp in typeChanges)
                {
                    if (kvp.Key.StartsWith("input_") && int.TryParse(kvp.Key[6..], out int idx) && idx >= 0 && idx < component.Params.Input.Count)
                    { if (ScriptParamTypeHelper.ChangeParameterType(component.Params.Input[idx], kvp.Value)) modified++; else errors.Add($"Type change failed: {kvp.Key}={kvp.Value}"); }
                    else if (kvp.Key.StartsWith("output_") && int.TryParse(kvp.Key[7..], out int oidx) && oidx >= 0 && oidx < component.Params.Output.Count)
                    {
                        if (isScript && oidx == 0) errors.Add("output_0 type is reserved");
                        else if (ScriptParamTypeHelper.ChangeParameterType(component.Params.Output[oidx], kvp.Value)) modified++;
                        else errors.Add($"Type change failed: {kvp.Key}={kvp.Value}");
                    }
                }

                foreach (var kvp in accessChanges)
                {
                    if (kvp.Key.StartsWith("input_") && int.TryParse(kvp.Key[6..], out int idx) && idx >= 0 && idx < component.Params.Input.Count)
                    { if (ScriptParamTypeHelper.ChangeParameterAccess(component.Params.Input[idx], kvp.Value)) modified++; else errors.Add($"Access change failed: {kvp.Key}={kvp.Value}"); }
                    else errors.Add("Access changes only apply to input parameters");
                }

                if (modified > 0)
                {
                    component.ExpireSolution(true);
                    doc.NewSolution(false);
                }

                var msg = $"Modified {modified} parameters";
                if (errors.Count > 0) msg += $". Errors: {string.Join(", ", errors)}";
                return new { success = modified > 0, message = msg, modifiedCount = modified, errors };
            });

            return result;
        }, nameof(ModifyScriptComponentParameters));
    }

    [McpServerTool(Name = "Get_Parameter_TypeHints")]
    [Description("Lists available type hints for a script component parameter. Returns the current hint and all available options. " +
        "Use this before setting a type hint with Modify_Script_Component_Parameters to discover valid values.")]
    public static async Task<CallToolResult> GetParameterTypeHints(
        [Description("Component GUID")] string componentId,
        [Description("0-based parameter index")] int parameterIndex,
        [Description("'input' or 'output' (default: 'input')")] string side = "input")
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var component = FindComponent(componentId);
                var isInput = ParseSide(side);
                var paramList = isInput ? component.Params.Input : component.Params.Output;

                if (parameterIndex < 0 || parameterIndex >= paramList.Count)
                    throw new ArgumentException($"Parameter index {parameterIndex} out of range (0..{paramList.Count - 1})");

                var param = paramList[parameterIndex];
                var info = ScriptParamTypeHelper.GetAvailableHints(param);

                return new
                {
                    success = true,
                    componentId,
                    side = isInput ? "input" : "output",
                    parameterIndex,
                    parameterName = param.NickName,
                    typeSystem = info.TypeSystem,
                    currentHint = info.CurrentHint ?? "none",
                    availableHints = info.AvailableHints
                };
            });

            return result;
        }, nameof(GetParameterTypeHints));
    }

    [McpServerTool(Name = "Add_Script_Parameter")]
    [Description("Adds a new input or output parameter to a script component. " +
        "Returns the index of the new parameter. For outputs, index 0 is reserved ('out' param) — new outputs append after it. " +
        "Use Get_Parameter_TypeHints to discover valid type names.")]
    public static async Task<CallToolResult> AddScriptParameter(
        [Description("Component GUID")] string componentId,
        [Description("'input' or 'output'")] string side,
        [Description("Parameter name")] string name,
        [Description("Optional tooltip description")] string? description = null,
        [Description("Type hint (e.g. 'Number', 'Point3d', 'String')")] string? type = null,
        [Description("Data access: 'item' (default), 'list', or 'tree'")] string access = "item",
        [Description("Mark input as optional (default: false, inputs only)")] bool optional = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentId), componentId),
                (nameof(side), side),
                (nameof(name), name));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var component = FindComponent(componentId);
                if (component is not IGH_VariableParameterComponent variableComponent)
                    throw new InvalidOperationException("Component does not support variable parameters (not a script component?)");

                var isInput = ParseSide(side);
                if (!isInput && optional)
                    throw new ArgumentException("'optional' only applies to input parameters");

                var ghSide = isInput ? GH_ParameterSide.Input : GH_ParameterSide.Output;
                var paramList = isInput ? component.Params.Input : component.Params.Output;
                var insertIndex = paramList.Count;

                if (!variableComponent.CanInsertParameter(ghSide, insertIndex))
                    throw new InvalidOperationException($"Component cannot insert a {side} at index {insertIndex}");

                var param = variableComponent.CreateParameter(ghSide, insertIndex)
                    ?? throw new InvalidOperationException("Component returned null from CreateParameter");

                if (isInput)
                    component.Params.RegisterInputParam(param);
                else
                    component.Params.RegisterOutputParam(param);

                variableComponent.VariableParameterMaintenance();

                // Apply metadata AFTER maintenance so component defaults don't overwrite
                ApplyRequestedMetadata(param, name, description, isInput ? optional : (bool?)null, access, type);

                component.Params.OnParametersChanged();
                component.ExpireSolution(true);
                Instances.ActiveCanvas?.Document?.NewSolution(false);

                return new
                {
                    success = true,
                    componentId,
                    side = isInput ? "input" : "output",
                    name,
                    index = insertIndex,
                    access,
                    type = type ?? "default"
                };
            });

            return result;
        }, nameof(AddScriptParameter));
    }

    [McpServerTool(Name = "Remove_Script_Parameter")]
    [Description("Removes an input or output parameter from a script component by index. " +
        "Output index 0 is reserved ('out' param) and cannot be removed.")]
    public static async Task<CallToolResult> RemoveScriptParameter(
        [Description("Component GUID")] string componentId,
        [Description("'input' or 'output'")] string side,
        [Description("0-based parameter index to remove")] int index)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(componentId), componentId),
                (nameof(side), side));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var component = FindComponent(componentId);
                if (component is not IGH_VariableParameterComponent variableComponent)
                    throw new InvalidOperationException("Component does not support variable parameters");

                var isInput = ParseSide(side);
                var ghSide = isInput ? GH_ParameterSide.Input : GH_ParameterSide.Output;
                var paramList = isInput ? component.Params.Input : component.Params.Output;

                if (index < 0 || index >= paramList.Count)
                    throw new ArgumentException($"Index {index} out of range (0..{paramList.Count - 1})");

                if (!isInput && IsReservedScriptOutput(component, index))
                    throw new ArgumentException("Output index 0 ('out') is reserved on script components and cannot be removed");

                if (!variableComponent.CanRemoveParameter(ghSide, index))
                    throw new InvalidOperationException($"Component cannot remove {side} at index {index}");

                // Capture before destroy so unregister targets the correct instance
                var paramToRemove = paramList[index];
                var removedName = paramToRemove.NickName;

                variableComponent.DestroyParameter(ghSide, index);

                if (isInput)
                    component.Params.UnregisterInputParameter(paramToRemove);
                else
                    component.Params.UnregisterOutputParameter(paramToRemove);

                variableComponent.VariableParameterMaintenance();
                component.Params.OnParametersChanged();
                component.ExpireSolution(true);
                Instances.ActiveCanvas?.Document?.NewSolution(false);

                var remaining = isInput ? component.Params.Input.Count : component.Params.Output.Count;

                return new
                {
                    success = true,
                    componentId,
                    side = isInput ? "input" : "output",
                    removedName,
                    removedIndex = index,
                    remainingCount = remaining
                };
            });

            return result;
        }, nameof(RemoveScriptParameter));
    }

    private static IGH_Component FindComponent(string componentId)
    {
        var doc = Instances.ActiveCanvas?.Document
            ?? throw new InvalidOperationException("No active Grasshopper document");
        if (!Guid.TryParse(componentId, out var guid))
            throw new ArgumentException("Invalid component GUID format");
        return doc.FindObject(guid, false) as IGH_Component
            ?? throw new InvalidOperationException("Component not found or is not a component");
    }

    private static bool ParseSide(string side)
    {
        return side.ToLowerInvariant() switch
        {
            "input" => true,
            "output" => false,
            _ => throw new ArgumentException($"Invalid side '{side}'. Use 'input' or 'output'")
        };
    }

    private static bool IsReservedScriptOutput(IGH_Component component, int index)
    {
        if (index != 0) return false;
        var typeName = component.GetType().FullName?.ToLowerInvariant() ?? string.Empty;
        return typeName.Contains("csharp") || typeName.Contains("python") || typeName.Contains("script");
    }

    private static void ApplyRequestedMetadata(IGH_Param param, string name, string? description, bool? optional, string access, string? type)
    {
        param.Name = name;
        param.NickName = name;
        if (!string.IsNullOrEmpty(description))
            param.Description = description;
        if (optional.HasValue)
            param.Optional = optional.Value;
        ScriptParamTypeHelper.ChangeParameterAccess(param, access);
        if (!string.IsNullOrEmpty(type))
        {
            if (!ScriptParamTypeHelper.ChangeParameterType(param, type))
                throw new ArgumentException($"Failed to set type hint '{type}'. Use Get_Parameter_TypeHints to list valid options.");
        }
    }

    private static class ScriptParamTypeHelper
    {
        private static readonly Dictionary<string, Type> TypeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Number"] = typeof(double), ["Double"] = typeof(double), ["Float"] = typeof(double),
            ["Integer"] = typeof(int), ["Int"] = typeof(int),
            ["Boolean"] = typeof(bool), ["Bool"] = typeof(bool),
            ["String"] = typeof(string), ["Text"] = typeof(string),
            ["Point"] = typeof(Rhino.Geometry.Point3d), ["Point3d"] = typeof(Rhino.Geometry.Point3d),
            ["Vector"] = typeof(Rhino.Geometry.Vector3d), ["Vector3d"] = typeof(Rhino.Geometry.Vector3d),
            ["Curve"] = typeof(Rhino.Geometry.Curve), ["Surface"] = typeof(Rhino.Geometry.Surface),
            ["Brep"] = typeof(Rhino.Geometry.Brep), ["Mesh"] = typeof(Rhino.Geometry.Mesh),
            ["Plane"] = typeof(Rhino.Geometry.Plane), ["Line"] = typeof(Rhino.Geometry.Line),
            ["Interval"] = typeof(Rhino.Geometry.Interval),
            ["DateTime"] = typeof(DateTime), ["Color"] = typeof(System.Drawing.Color),
        };

        private static readonly Dictionary<string, string> HintNameMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Boolean"] = "bool", ["Bool"] = "bool",
            ["Integer"] = "int", ["Int"] = "int",
            ["Number"] = "double", ["Double"] = "double", ["Float"] = "double",
            ["String"] = "string", ["Text"] = "string",
            ["Point"] = "Point3d", ["Point3d"] = "Point3d",
            ["Vector"] = "Vector3d", ["Vector3d"] = "Vector3d",
            ["Plane"] = "Plane",
            ["Interval"] = "Interval",
            ["UVInterval"] = "UVInterval",
            ["DateTime"] = "DateTime",
            ["Color"] = "Color",
            ["Line"] = "Line",
            ["Curve"] = "Curve",
            ["Surface"] = "Surface",
            ["Brep"] = "Brep",
            ["Mesh"] = "Mesh",
        };

        internal static bool ChangeParameterType(IGH_Param parameter, string typeName)
        {
            try
            {
                if (!TypeMap.TryGetValue(typeName, out var targetType)) return false;

                // Strategy 1: IScriptParameter.Converter (RhinoCode components)
                var paramType = parameter.GetType();
                var iScriptParam = paramType.GetInterface("IScriptParameter");
                if (iScriptParam != null)
                {
                    var converterProp = iScriptParam.GetProperty("Converter");
                    if (converterProp?.CanWrite == true)
                    {
                        var gh1 = AppDomain.CurrentDomain.GetAssemblies()
                            .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException) { return Array.Empty<Type>(); } })
                            .FirstOrDefault(t => t.Name == "Grasshopper1");
                        var getConv = gh1?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "GetConverters" && m.GetParameters().Length <= 1);
                        if (getConv?.Invoke(null, getConv.GetParameters().Length == 0 ? Array.Empty<object>() : new object?[] { null }) is System.Collections.IEnumerable converters)
                        {
                            foreach (var conv in converters)
                            {
                                var ttProp = conv.GetType().GetProperty("TargetType");
                                var typeProp = ttProp?.GetValue(conv)?.GetType().GetProperty("Type");
                                if (typeProp?.GetValue(ttProp?.GetValue(conv)) is Type t && t == targetType)
                                {
                                    converterProp.SetValue(parameter, conv);
                                    return true;
                                }
                            }
                        }
                    }
                }

                // Strategy 2: Legacy TypeHint via Param_ScriptVariable.Hints list
                if (HintNameMap.TryGetValue(typeName, out var hintTypeName))
                {
                    var hintsProperty = paramType.GetProperty("Hints", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var typeHintProperty = paramType.GetProperty("TypeHint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (hintsProperty?.GetValue(parameter) is System.Collections.IList hints && typeHintProperty?.CanWrite == true)
                    {
                        foreach (var hint in hints)
                        {
                            var currentName = hint?.GetType().GetProperty("TypeName")?.GetValue(hint) as string;
                            if (string.Equals(currentName, hintTypeName, StringComparison.OrdinalIgnoreCase))
                            {
                                typeHintProperty.SetValue(parameter, hint);
                                return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Error changing parameter type: {ex.Message}");
                return false;
            }
        }

        internal static bool ChangeParameterAccess(IGH_Param parameter, string accessValue)
        {
            try
            {
                parameter.Access = accessValue.ToLowerInvariant() switch
                {
                    "item" => GH_ParamAccess.item,
                    "list" => GH_ParamAccess.list,
                    "tree" => GH_ParamAccess.tree,
                    _ => throw new ArgumentException($"Invalid access value: {accessValue}. Use 'item', 'list', or 'tree'")
                };
                if (parameter is IGH_DocumentObject doc) doc.OnObjectChanged((GH_ObjectEventType)12);
                return true;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Error changing parameter access: {ex.Message}");
                return false;
            }
        }

        internal static (string TypeSystem, string? CurrentHint, List<object> AvailableHints) GetAvailableHints(IGH_Param parameter)
        {
            var available = new List<object>();
            string? current = null;
            string typeSystem = "unknown";

            var paramType = parameter.GetType();

            // Try legacy Param_ScriptVariable hints
            var hintsProperty = paramType.GetProperty("Hints", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var typeHintProperty = paramType.GetProperty("TypeHint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (hintsProperty != null)
            {
                typeSystem = "legacy";
                var hints = hintsProperty.GetValue(parameter) as System.Collections.IList;
                var currentHint = typeHintProperty?.GetValue(parameter);

                if (currentHint != null)
                    current = currentHint.GetType().GetProperty("TypeName")?.GetValue(currentHint) as string;

                if (hints != null)
                {
                    // Build reverse lookup: internalName -> first user-facing key in HintNameMap
                    var reverseHintNames = HintNameMap
                        .GroupBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

                    foreach (var hint in hints)
                    {
                        var hintName = hint?.GetType().GetProperty("TypeName")?.GetValue(hint) as string;
                        if (!string.IsNullOrEmpty(hintName))
                        {
                            var friendly = reverseHintNames.TryGetValue(hintName, out var mapped) ? mapped : hintName;
                            available.Add(new { displayName = friendly, internalName = hintName });
                        }
                    }
                }

                return (typeSystem, current, available);
            }

            // Try RhinoCode IScriptParameter.Converter
            var iScriptParam = paramType.GetInterface("IScriptParameter");
            if (iScriptParam != null)
            {
                typeSystem = "rhinocode";
                var converterProp = iScriptParam.GetProperty("Converter");
                var currentConv = converterProp?.GetValue(parameter);
                if (currentConv != null)
                {
                    var ttProp = currentConv.GetType().GetProperty("TargetType");
                    var typeProp = ttProp?.GetValue(currentConv)?.GetType().GetProperty("Type");
                    current = (typeProp?.GetValue(ttProp?.GetValue(currentConv)) as Type)?.Name;
                }

                var gh1 = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException) { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "Grasshopper1");
                var getConv = gh1?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetConverters" && m.GetParameters().Length <= 1);
                if (getConv?.Invoke(null, getConv.GetParameters().Length == 0 ? Array.Empty<object>() : new object?[] { null }) is System.Collections.IEnumerable converters)
                {
                    foreach (var conv in converters)
                    {
                        var ttProp = conv.GetType().GetProperty("TargetType");
                        var typeProp = ttProp?.GetValue(conv)?.GetType().GetProperty("Type");
                        if (typeProp?.GetValue(ttProp?.GetValue(conv)) is Type t)
                            available.Add(new { displayName = t.Name, internalName = t.Name });
                    }
                }
            }

            return (typeSystem, current, available);
        }
    }
}
