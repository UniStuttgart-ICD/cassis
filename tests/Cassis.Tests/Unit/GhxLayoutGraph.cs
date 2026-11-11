using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Cassis.Utilities;

namespace Cassis.Tests.Unit;

/// <summary>
/// Offline GHX → layout graph. For local fixtures only — do not commit large .ghx files.
/// </summary>
internal static class GhxLayoutGraph
{
    /// <summary>
    /// Optional local .ghx path via CASSIS_LAYOUT_GHX. No default project file — CI skips.
    /// </summary>
    public static string? ResolveFixturePath()
    {
        var env = Environment.GetEnvironmentVariable("CASSIS_LAYOUT_GHX");
        return !string.IsNullOrWhiteSpace(env) && File.Exists(env) ? env : null;
    }

    public static (List<LayoutNode> Nodes, List<LayoutEdge> Edges, List<LayoutMembership> Groups)
        Load(string path)
    {
        var doc = XDocument.Load(path);
        var objects = doc.Descendants("chunk")
            .Where(c => (string?)c.Attribute("name") == "Object")
            .ToList();

        var paramToOwner = new Dictionary<Guid, Guid>();
        var nodes = new List<LayoutNode>();
        var groups = new List<LayoutMembership>();
        var rawEdges = new List<(Guid SrcParam, Guid ToOwner)>();

        foreach (var obj in objects)
        {
            var typeName = ItemText(obj, "Name") ?? string.Empty;
            var container = obj.Elements("chunks")
                .Elements("chunk")
                .FirstOrDefault(c => (string?)c.Attribute("name") == "Container");
            if (container == null)
            {
                continue;
            }

            var ownerText = ItemText(container, "InstanceGuid");
            if (!Guid.TryParse(ownerText, out var ownerId))
            {
                continue;
            }

            foreach (var ig in container.Descendants("item")
                         .Where(i => (string?)i.Attribute("name") == "InstanceGuid"))
            {
                if (Guid.TryParse(ig.Value.Trim(), out var pid))
                {
                    paramToOwner[pid] = ownerId;
                }
            }

            if (string.Equals(typeName, "Group", StringComparison.OrdinalIgnoreCase))
            {
                var memberIds = container.Elements("items")
                    .Elements("item")
                    .Where(i => (string?)i.Attribute("name") == "ID")
                    .Select(i => Guid.TryParse(i.Value.Trim(), out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty)
                    .ToList();
                var nick = ItemText(container, "NickName");
                var colour = ItemTextArgb(container, "Colour");
                groups.Add(new LayoutMembership(ownerId, memberIds, nick, colour));
                continue;
            }

            var (px, py, w, h) = ReadPivotBounds(container);
            var frozen = string.Equals(typeName, "Scribble", StringComparison.OrdinalIgnoreCase);
            nodes.Add(new LayoutNode(ownerId, px, py, w, h, frozen));

            foreach (var src in container.Descendants("item")
                         .Where(i => (string?)i.Attribute("name") == "Source"))
            {
                if (Guid.TryParse(src.Value.Trim(), out var srcParam))
                {
                    rawEdges.Add((srcParam, ownerId));
                }
            }
        }

        var edges = new List<LayoutEdge>();
        var seen = new HashSet<(Guid, Guid)>();
        foreach (var (srcParam, toOwner) in rawEdges)
        {
            if (!paramToOwner.TryGetValue(srcParam, out var fromOwner))
            {
                continue;
            }

            if (fromOwner == toOwner)
            {
                continue;
            }

            if (seen.Add((fromOwner, toOwner)))
            {
                edges.Add(new LayoutEdge(fromOwner, toOwner));
            }
        }

        return (nodes, edges, groups);
    }

    private static string? ItemText(XElement parent, string name)
    {
        return parent.Elements("items")
            .Elements("item")
            .FirstOrDefault(i => (string?)i.Attribute("name") == name)
            ?.Value
            ?.Trim();
    }

    /// <summary>GH drawing colour item → css rgba, or null.</summary>
    private static string? ItemTextArgb(XElement parent, string name)
    {
        var item = parent.Elements("items")
            .Elements("item")
            .FirstOrDefault(i => (string?)i.Attribute("name") == name);
        var raw = item?.Element("ARGB")?.Value?.Trim() ?? item?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = raw.Split(';');
        if (parts.Length != 4
            || !int.TryParse(parts[0], out var a)
            || !int.TryParse(parts[1], out var r)
            || !int.TryParse(parts[2], out var g)
            || !int.TryParse(parts[3], out var b))
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "rgba({0},{1},{2},{3:0.###})",
            r, g, b, Math.Min(1.0, a / 255.0));
    }

    private static (float X, float Y, float W, float H) ReadPivotBounds(XElement container)
    {
        float x = 0, y = 0, w = 80, h = 40;
        var attrs = container.Elements("chunks")
            .Elements("chunk")
            .FirstOrDefault(c => (string?)c.Attribute("name") == "Attributes");
        if (attrs == null)
        {
            return (x, y, w, h);
        }

        var pivot = attrs.Elements("items")
            .Elements("item")
            .FirstOrDefault(i => (string?)i.Attribute("name") == "Pivot");
        if (pivot != null)
        {
            x = ParseF(pivot.Element("X")?.Value);
            y = ParseF(pivot.Element("Y")?.Value);
        }

        var bounds = attrs.Elements("items")
            .Elements("item")
            .FirstOrDefault(i => (string?)i.Attribute("name") == "Bounds");
        if (bounds != null)
        {
            w = Math.Max(1f, ParseF(bounds.Element("W")?.Value, 80f));
            h = Math.Max(1f, ParseF(bounds.Element("H")?.Value, 40f));
        }

        return (x, y, w, h);
    }

    private static float ParseF(string? s, float fallback = 0f)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return fallback;
        }

        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
    }
}
