using System.Collections.Generic;
using Grasshopper.Kernel;

namespace Cassis.Utilities;

/// <summary>
/// Maps script component types (Rhino 8 and legacy GH1) to their language by exact type name.
/// </summary>
internal static class ScriptComponentKind
{
    private static readonly Dictionary<string, string> Languages = new()
    {
        ["RhinoCodePluginGH.Components.CSharpComponent"] = "C#",
        ["RhinoCodePluginGH.Components.Python3Component"] = "Python",
        ["RhinoCodePluginGH.Components.IronPython2Component"] = "Python",
        ["RhinoCodePluginGH.Components.ScriptComponent"] = "Script",
        ["ScriptComponents.Component_CSNET_Script"] = "C#",
        ["ScriptComponents.Component_VBNET_Script"] = "VB",
        ["GhPython.Component.ZuiPythonComponent"] = "Python",
    };

    /// <summary>Returns the script language, or null when the object is not a script component.</summary>
    internal static string? GetLanguage(IGH_DocumentObject obj) =>
        Languages.TryGetValue(obj.GetType().FullName ?? string.Empty, out var lang) ? lang : null;
}
