using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Rhino;
#if NET6_0_OR_GREATER
using System.Runtime.Loader;
#endif

namespace Cassis;

/// <summary>
/// Runs when Grasshopper loads the Cassis assembly. Starts MCP when AutoStart is enabled.
/// </summary>
public sealed class CassisAssemblyPriority : GH_AssemblyPriority
{
#if NET6_0_OR_GREATER
    static CassisAssemblyPriority()
    {
        try
        {
            EnsurePreferredJsonLoaded();
            AssemblyLoadContext.Default.Resolving += ResolveAssemblyFromPluginDirectory;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Failed to register assembly resolver: {ex.Message}");
        }
    }

    private static void EnsurePreferredJsonLoaded()
    {
        try
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrEmpty(pluginDirectory)) return;

            var candidatePath = Path.Combine(pluginDirectory, "System.Text.Json.dll");
            if (!File.Exists(candidatePath)) return;

            var candidateName = AssemblyName.GetAssemblyName(candidatePath);
            var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "System.Text.Json", StringComparison.OrdinalIgnoreCase));

            if (alreadyLoaded != null && alreadyLoaded.GetName().Version >= candidateName.Version) return;

            AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
            RhinoApp.WriteLine($"[MCP] Preferring System.Text.Json {candidateName.Version} from plugin folder.");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Failed to preload System.Text.Json: {ex.Message}");
        }
    }

    private static Assembly? ResolveAssemblyFromPluginDirectory(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        try
        {
            if (!string.Equals(assemblyName.Name, "System.Text.Json", StringComparison.OrdinalIgnoreCase)) return null;

            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrEmpty(pluginDirectory)) return null;

            var candidatePath = Path.Combine(pluginDirectory, "System.Text.Json.dll");
            if (!File.Exists(candidatePath)) return null;

            var candidateName = AssemblyName.GetAssemblyName(candidatePath);
            if (candidateName.Version < assemblyName.Version) return null;

            var resolved = context.LoadFromAssemblyPath(candidatePath);
            RhinoApp.WriteLine($"[MCP] Resolved System.Text.Json {candidateName.Version} from plugin folder.");
            return resolved;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Assembly resolve failed for {assemblyName.FullName}: {ex.Message}");
            return null;
        }
    }
#endif

    public override GH_LoadingInstruction PriorityLoad()
    {
        try
        {
            CassisSettings.EnsureLoaded();
            CassisRuntime.TryAutoStart();
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[Cassis ERROR] Auto-start failed: {ex.Message}");
        }

        return GH_LoadingInstruction.Proceed;
    }
}
