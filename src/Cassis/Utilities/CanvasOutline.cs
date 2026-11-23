// Copied whole from ghsh src/Ghsh.Cli/GhJson/CanvasOutline.cs (commit c20e6ae); only the namespace differs.
// Change it there first, then copy, so both tools give the same outline.
// Pure C# for net48 and net8: no JSON library, no Grasshopper types, no ranges.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cassis.Utilities;

internal sealed class OutlineComponent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string NickName { get; set; } = "";
    // Passed through unchanged into chapter pages.
    public object? InstanceGuid { get; set; }
    public object? Pivot { get; set; }
    // Null when the component is not a script.
    public string? ScriptLanguage { get; set; }
    public string ScriptCode { get; set; } = "";
    public string? ScribbleText { get; set; }
    public IReadOnlyList<string> InputNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> OutputNames { get; set; } = Array.Empty<string>();
}

internal sealed class OutlineGroup
{
    public string Ref { get; set; } = "";
    public string? InstanceGuid { get; set; }
    public string Name { get; set; } = "";
    public IReadOnlyList<int> Members { get; set; } = Array.Empty<int>();
}

internal sealed class OutlineWire
{
    public int FromId { get; set; }
    public int ToId { get; set; }
    public string FromPort { get; set; } = "?";
    public string ToPort { get; set; } = "?";
}

internal sealed class OutlineCanvas
{
    public string? Title { get; set; }
    public IReadOnlyList<OutlineComponent> Components { get; set; } = Array.Empty<OutlineComponent>();
    public IReadOnlyList<OutlineGroup> Groups { get; set; } = Array.Empty<OutlineGroup>();
    public IReadOnlyList<OutlineWire> Wires { get; set; } = Array.Empty<OutlineWire>();
}

internal sealed class OutlineGroupNotFoundException : Exception
{
    public OutlineGroupNotFoundException(string selector)
        : base($"No group matches exact ID or instanceGuid '{selector}'.")
    {
    }
}

// Book view of a canvas: groups are chapters, wires inside a chapter are its page.
// Results are Dictionary<string, object?> and List<object?> in output order.
internal static class CanvasOutline
{
    public const string UngroupedRef = "ungrouped";
    private const int ListCap = 20;
    private const int FlowCap = 30;
    private const int HeaderCap = 300;

    // Live observation: first line of the Rhino 8 C# script template.
    private const string TemplateComment = "Grasshopper Script Instance";

    // shortcut: only "C#" was observed live; add other defaults when seen.
    private static readonly HashSet<string> DefaultScriptNickNames =
        new(StringComparer.OrdinalIgnoreCase) { "C#", "Py3", "IPy2", "VB" };

    private static readonly HashSet<string> ControlNames =
        new(StringComparer.Ordinal)
        {
            "Number Slider", "Boolean Toggle", "Button",
            "Value List", "Panel", "Colour Swatch"
        };

    private static readonly string[] PythonQuotes = { "\"\"\"", "'''" };
    private static readonly string[] PythonDirectives = { "r:", "requirements:", "venv:", "env:" };

    private sealed class GroupNode
    {
        public GroupNode(string reference, string? instanceGuid, string name, HashSet<int> members)
        {
            Ref = reference;
            InstanceGuid = instanceGuid;
            Name = name;
            Members = members;
        }

        public string Ref { get; }
        public string? InstanceGuid { get; }
        public string Name { get; }
        public HashSet<int> Members { get; }
        public GroupNode? Parent { get; set; }
        public List<GroupNode> Children { get; } = new();
        public GroupNode Top => Parent?.Top ?? this;
    }

    private sealed class Model
    {
        public Dictionary<int, OutlineComponent> Components { get; } = new();
        public List<GroupNode> Groups { get; } = new();
        public List<GroupNode> Empty { get; } = new();
        public Dictionary<int, GroupNode> Home { get; } = new();
        public List<(GroupNode A, GroupNode B)> Overlaps { get; } = new();
        public IReadOnlyList<OutlineWire> Wires { get; set; } = Array.Empty<OutlineWire>();
    }

    public static Dictionary<string, object?> Outline(OutlineCanvas canvas)
    {
        var model = Build(canvas);
        var chapters = new List<object?>();
        foreach (var group in Ordered(model.Groups.Where(g => g.Parent is null)))
            chapters.Add(Node(model, group));
        var ungrouped = model.Components.Keys.Count(id => !model.Home.ContainsKey(id));
        if (ungrouped > 0)
        {
            chapters.Add(new Dictionary<string, object?>
            {
                ["ref"] = UngroupedRef,
                ["title"] = "(ungrouped)",
                ["derived"] = true,
                ["components"] = ungrouped
            });
        }

        var flows = model.Wires
            .Select(wire => (
                From: ChapterRef(model, wire.FromId),
                To: ChapterRef(model, wire.ToId)))
            .Where(flow => flow.From != flow.To)
            .GroupBy(flow => flow)
            .OrderByDescending(flow => flow.Count())
            .ThenBy(flow => flow.Key.From, StringComparer.Ordinal)
            .ThenBy(flow => flow.Key.To, StringComparer.Ordinal)
            .Select(flow => $"{flow.Key.From}>{flow.Key.To}x{flow.Count()}")
            .ToArray();

        var outline = new Dictionary<string, object?>
        {
            ["title"] = canvas.Title,
            ["components"] = model.Components.Count,
            ["connections"] = model.Wires.Count,
            ["chapters"] = chapters,
            ["flow"] = flows.Take(FlowCap).ToList()
        };
        if (flows.Length > FlowCap)
            outline["flowOmitted"] = flows.Length - FlowCap;
        outline["gaps"] = new Dictionary<string, object?>
        {
            ["unnamedGroups"] = Gap(model.Groups
                .Where(group => IsUnnamed(group.Name))
                .Select(group => group.Ref)),
            ["defaultScriptNames"] = Gap(model.Components.Values
                .Where(component => IsScript(component) && HasDefaultNickName(component))
                .Select(component => component.Id.ToString())),
            ["ungrouped"] = ungrouped,
            ["emptyGroups"] = Gap(model.Empty.Select(group => group.Ref)),
            ["overlaps"] = Gap(model.Overlaps
                .Select(pair => $"{pair.A.Ref}+{pair.B.Ref}"))
        };
        return outline;
    }

    public static Dictionary<string, object?> Chapter(OutlineCanvas canvas, string selector)
    {
        var model = Build(canvas);
        HashSet<int> members;
        Dictionary<string, object?> chapter;
        GroupNode? group = null;
        if (string.Equals(selector, UngroupedRef, StringComparison.OrdinalIgnoreCase))
        {
            members = new HashSet<int>(model.Components.Keys.Where(id => !model.Home.ContainsKey(id)));
            chapter = new Dictionary<string, object?>
            {
                ["ref"] = UngroupedRef,
                ["title"] = "(ungrouped)",
                ["derived"] = true
            };
        }
        else
        {
            group = model.Groups.Concat(model.Empty).FirstOrDefault(candidate =>
                    string.Equals(candidate.Ref, selector, StringComparison.Ordinal) ||
                    string.Equals(candidate.InstanceGuid, selector, StringComparison.OrdinalIgnoreCase))
                ?? throw new OutlineGroupNotFoundException(selector);
            members = group.Members;
            chapter = new Dictionary<string, object?>
            {
                ["ref"] = group.Ref,
                ["instanceGuid"] = group.InstanceGuid
            };
            AddTitle(model, group, chapter);
            if (group.Parent is not null)
                chapter["parent"] = group.Parent.Ref;
            if (group.Children.Count > 0)
            {
                var children = new List<object?>();
                foreach (var child in Ordered(group.Children))
                {
                    var node = new Dictionary<string, object?> { ["ref"] = child.Ref };
                    AddTitle(model, child, node);
                    node["components"] = child.Members.Count;
                    children.Add(node);
                }
                chapter["children"] = children;
            }
        }

        var components = new List<object?>();
        foreach (var id in members.OrderBy(id => id))
        {
            var component = model.Components[id];
            var item = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["name"] = component.Name
            };
            var nickName = component.NickName.Trim();
            if (nickName.Length > 0 && nickName != component.Name)
                item["nickName"] = nickName;
            item["instanceGuid"] = component.InstanceGuid;
            item["pivot"] = component.Pivot;
            if (group is not null &&
                model.Home.TryGetValue(id, out var home) &&
                ChildOf(home, group) is { } child)
            {
                item["group"] = child.Ref;
            }
            if (component.ScriptLanguage is { } language)
                item["script"] = ScriptSummary(language, component.ScriptCode);
            components.Add(item);
        }
        chapter["components"] = components;

        chapter["wires"] = model.Wires
            .Where(wire => members.Contains(wire.FromId) && members.Contains(wire.ToId))
            .Select(WireText)
            .ToList();
        chapter["boundary"] = model.Wires
            .Where(wire => members.Contains(wire.FromId) != members.Contains(wire.ToId))
            .Select(WireText)
            .ToList();
        return chapter;
    }

    public static Dictionary<string, object?> ScriptSummary(string language, string code)
    {
        var summary = new Dictionary<string, object?>
        {
            ["language"] = language,
            ["lines"] = code.Length == 0 ? 0 : code.Split('\n').Length
        };
        if (ScriptHeader(code, language) is { } header)
            summary["header"] = header;
        return summary;
    }

    // First comment block before any code; usings, imports and directives are skipped.
    public static string? ScriptHeader(string code, string language)
    {
        var python = language is "python" or "ironpython";
        if (!python && language != "csharp")
            return null;

        var text = new List<string>();
        var inRegion = false;
        string? blockEnd = null;
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.Trim();
            if (blockEnd is not null)
            {
                var end = line.IndexOf(blockEnd, StringComparison.Ordinal);
                text.Add((end < 0 ? line : line.Substring(0, end)).TrimStart('*').Trim());
                if (end >= 0)
                    break;
                continue;
            }
            if (inRegion)
            {
                inRegion = !line.StartsWith("#endregion", StringComparison.Ordinal);
                continue;
            }
            if (line.Length == 0)
            {
                if (text.Count > 0)
                    break;
                continue;
            }

            var opener = python
                ? PythonQuotes.FirstOrDefault(quote => line.StartsWith(quote, StringComparison.Ordinal))
                : line.StartsWith("/*", StringComparison.Ordinal) ? "/*" : null;
            if (opener is not null)
            {
                var closer = python ? opener : "*/";
                var rest = line.Substring(opener.Length);
                var end = rest.IndexOf(closer, StringComparison.Ordinal);
                text.Add((end < 0 ? rest : rest.Substring(0, end)).TrimStart('*').Trim());
                if (end >= 0)
                    break;
                blockEnd = closer;
                continue;
            }

            var skipped = python
                ? line.StartsWith("#!", StringComparison.Ordinal) ||
                  IsPythonDirective(line) ||
                  line.StartsWith("import ", StringComparison.Ordinal) ||
                  line.StartsWith("from ", StringComparison.Ordinal)
                : line.StartsWith("using ", StringComparison.Ordinal) ||
                  (line.StartsWith("#", StringComparison.Ordinal) &&
                   !line.StartsWith("#region", StringComparison.Ordinal));
            if (!python && line.StartsWith("#region", StringComparison.Ordinal))
            {
                if (text.Count > 0)
                    break;
                inRegion = true;
                continue;
            }
            if (skipped)
            {
                if (text.Count > 0)
                    break;
                continue;
            }

            var marker = python ? "#" : "//";
            if (!line.StartsWith(marker, StringComparison.Ordinal))
                break;
            var comment = line.TrimStart(python ? '#' : '/').Trim();
            if (comment != TemplateComment)
                text.Add(comment);
        }

        var header = string.Join(" ", text.Where(part => part.Any(char.IsLetterOrDigit)));
        if (header.Length == 0)
            return null;
        return header.Length <= HeaderCap
            ? header
            : header.Substring(0, HeaderCap) + "...";
    }

    private static bool IsPythonDirective(string line)
    {
        var body = line.TrimStart('#').TrimStart();
        return PythonDirectives.Any(prefix => body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static Model Build(OutlineCanvas canvas)
    {
        var model = new Model { Wires = canvas.Wires };
        foreach (var component in canvas.Components)
        {
            if (!model.Components.ContainsKey(component.Id))
                model.Components.Add(component.Id, component);
        }

        foreach (var group in canvas.Groups)
        {
            var members = new HashSet<int>(group.Members.Where(model.Components.ContainsKey));
            var node = new GroupNode(group.Ref, group.InstanceGuid, group.Name, members);
            (members.Count == 0 ? model.Empty : model.Groups).Add(node);
        }

        var groups = model.Groups;
        for (var i = 0; i < groups.Count; i++)
        {
            var a = groups[i];
            a.Parent = groups
                .Where(b => b != a && a.Members.IsProperSubsetOf(b.Members))
                .OrderBy(b => b.Members.Count)
                .FirstOrDefault();
            for (var j = i + 1; j < groups.Count; j++)
            {
                var b = groups[j];
                if (a.Members.Overlaps(b.Members) &&
                    !a.Members.IsProperSubsetOf(b.Members) &&
                    !b.Members.IsProperSubsetOf(a.Members))
                {
                    model.Overlaps.Add((a, b));
                }
            }
        }
        foreach (var group in groups)
            group.Parent?.Children.Add(group);

        // A component in several groups belongs to the smallest one.
        foreach (var group in groups.OrderBy(group => group.Members.Count))
        {
            foreach (var id in group.Members)
            {
                if (!model.Home.ContainsKey(id))
                    model.Home.Add(id, group);
            }
        }

        return model;
    }

    private static Dictionary<string, object?> Node(Model model, GroupNode group)
    {
        var node = new Dictionary<string, object?> { ["ref"] = group.Ref };
        AddTitle(model, group, node);
        node["components"] = group.Members.Count;
        var controls = group.Members.Count(id => ControlNames.Contains(model.Components[id].Name));
        if (controls > 0)
            node["controls"] = controls;
        var scripts = group.Members
            .Where(id => model.Home[id] == group)
            .OrderBy(id => id)
            .Select(id => model.Components[id])
            .Where(IsScript)
            .Select(ScriptLabel)
            .ToList();
        if (scripts.Count > 0)
            node["scripts"] = scripts;
        if (group.Children.Count > 0)
        {
            var children = new List<object?>();
            foreach (var child in Ordered(group.Children))
                children.Add(Node(model, child));
            node["children"] = children;
        }
        return node;
    }

    private static void AddTitle(Model model, GroupNode group, Dictionary<string, object?> node)
    {
        if (!IsUnnamed(group.Name))
        {
            node["title"] = group.Name.Trim();
            return;
        }

        var members = group.Members
            .OrderBy(id => id)
            .Select(id => model.Components[id])
            .ToArray();
        var scribble = members
            .Select(member => member.ScribbleText)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        var scripts = members
            .Where(member => IsScript(member) && !HasDefaultNickName(member))
            .Select(member => member.NickName.Trim())
            .Take(3)
            .ToArray();
        node["title"] = scribble?.Trim() ??
            (scripts.Length > 0
                ? string.Join(", ", scripts)
                : string.Join(", ", members
                    .GroupBy(member => member.Name)
                    .OrderByDescending(type => type.Count())
                    .ThenBy(type => type.Key, StringComparer.Ordinal)
                    .Take(3)
                    .Select(type => $"{type.Count()} {type.Key}")));
        node["derived"] = true;
    }

    private static string ScriptLabel(OutlineComponent component)
    {
        if (!HasDefaultNickName(component))
            return component.NickName.Trim();
        return $"{component.Name} ({Parameters(component.InputNames)} -> " +
            $"{Parameters(component.OutputNames)})";
    }

    private static string Parameters(IReadOnlyList<string> names)
    {
        var present = names.Where(name => !string.IsNullOrEmpty(name)).ToArray();
        return string.Join(", ", present.Take(3)) + (present.Length > 3 ? ", ..." : "");
    }

    private static string WireText(OutlineWire wire) =>
        $"{wire.FromId}.{wire.FromPort}>{wire.ToId}.{wire.ToPort}";

    private static string ChapterRef(Model model, int componentId) =>
        model.Home.TryGetValue(componentId, out var home)
            ? home.Top.Ref
            : UngroupedRef;

    private static GroupNode? ChildOf(GroupNode group, GroupNode ancestor)
    {
        var current = group;
        while (current.Parent is not null && current.Parent != ancestor)
            current = current.Parent;
        return current.Parent == ancestor ? current : null;
    }

    private static IEnumerable<GroupNode> Ordered(IEnumerable<GroupNode> groups) =>
        groups
            .OrderByDescending(group => group.Members.Count)
            .ThenBy(group => group.Ref, StringComparer.Ordinal);

    private static Dictionary<string, object?> Gap(IEnumerable<string> refs)
    {
        var all = refs.ToArray();
        return new Dictionary<string, object?>
        {
            ["count"] = all.Length,
            ["refs"] = all.Take(ListCap).ToList()
        };
    }

    // "Group" is Grasshopper's default group name.
    private static bool IsUnnamed(string name) =>
        string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), "Group", StringComparison.Ordinal);

    private static bool IsScript(OutlineComponent component) =>
        component.ScriptLanguage is not null;

    private static bool HasDefaultNickName(OutlineComponent component)
    {
        var nickName = component.NickName.Trim();
        return nickName.Length == 0 ||
            nickName == component.Name ||
            DefaultScriptNickNames.Contains(nickName);
    }
}
