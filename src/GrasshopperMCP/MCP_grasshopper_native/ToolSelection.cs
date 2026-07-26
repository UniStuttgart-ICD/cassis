using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;

namespace GrasshopperMCP;

public static class ToolSelection
{
    /// <summary>
    /// Resolves the lowercased exposed name for an MCP-attributed method.
    /// Uses the attribute's <c>Name</c> if non-empty, otherwise falls back to the method name.
    /// </summary>
    public static string GetExposedName(MethodInfo method, string? attributeName)
        => (attributeName?.Trim() is { Length: > 0 } n ? n : method.Name).ToLowerInvariant();

    /// <summary>
    /// Returns tool-type classes whose [McpServerTool] methods have at least one name in <paramref name="enabledNames"/>.
    /// If <paramref name="enabledNames"/> is null, returns all tool types.
    /// </summary>
    public static IEnumerable<Type> GetEnabledToolTypes(HashSet<string>? enabledNames)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null)
                continue;

            if (enabledNames == null)
            {
                yield return type;
                continue;
            }

            var hasEnabled = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
                .Any(m => enabledNames.Contains(
                    GetExposedName(m, m.GetCustomAttribute<McpServerToolAttribute>()?.Name)));

            if (hasEnabled)
                yield return type;
        }
    }
}
