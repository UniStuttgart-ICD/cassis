using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ModelContextProtocol.Server;

namespace Cassis;

internal static class ToolDescriptionCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Descriptions =
        new(CreateDescriptions);

    public static bool TryGet(string exposedName, out string description)
    {
        if (!string.IsNullOrWhiteSpace(exposedName) &&
            Descriptions.Value.TryGetValue(exposedName, out var value))
        {
            description = value;
            return true;
        }

        description = string.Empty;
        return false;
    }

    private static IReadOnlyDictionary<string, string> CreateDescriptions()
    {
        var registeredNames = new HashSet<string>(
            ToolCategories.AllTools(),
            StringComparer.OrdinalIgnoreCase);

        var candidates = typeof(ToolDescriptionCatalog).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.Static |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            .Select(method =>
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                var prompt = method.GetCustomAttribute<McpServerPromptAttribute>();
                if (tool == null && prompt == null)
                    return null;

                var attributeName = tool?.Name ?? prompt?.Name;
                var exposedName = attributeName?.Trim() is { Length: > 0 } name
                    ? name
                    : method.Name;

                return new ToolDescriptionCandidate(
                    exposedName,
                    method.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    !string.IsNullOrWhiteSpace(attributeName));
            })
            .Where(candidate =>
                candidate != null &&
                registeredNames.Contains(candidate.Name))
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase);

        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates)
        {
            var entries = group.ToArray();
            var explicitlyNamed = entries.Where(candidate => candidate.HasExplicitName).ToArray();
            var selected = explicitlyNamed.Length == 1
                ? explicitlyNamed[0]
                : entries.Length == 1
                    ? entries[0]
                    : null;

            if (selected == null || string.IsNullOrWhiteSpace(selected.Description))
                continue;

            descriptions.Add(group.Key, selected.Description!);
        }

        return descriptions;
    }

    private sealed record ToolDescriptionCandidate(
        string Name,
        string? Description,
        bool HasExplicitName);
}
