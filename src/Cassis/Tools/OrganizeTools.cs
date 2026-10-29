using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Cassis.Extensions;
using Cassis.Utilities;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace Cassis.Tools;

/// <summary>
/// Dry-run-first canvas organize: Sugiyama layout, optional groups + Scribble titles.
/// </summary>
[McpServerToolType]
public static class OrganizeTools
{
    [McpServerTool(Name = "Organize_Canvas")]
    [Description(
        "Plans a left-to-right layout for the active Grasshopper canvas. " +
        "Default is dry-run (apply false). Set apply true to move pivots in one batch. " +
        "Default layoutMode is compact: organize computational blocks (function clusters) first with " +
        "input↔output pin alignment, then align blocks as wholes. Long wires are hidden on apply. " +
        "Use reflow to also strip islands L-to-R. " +
        "Walks param Sources so panels and sliders count as nodes. " +
        "Existing groups move as rigid supernodes. " +
        "Optional namedGroups creates a Consolas Scribble title at the top-left and wraps ungrouped members " +
        "(prefer larger clusters only). " +
        "Optional componentIds freezes everything else as obstacles.")]
    public static async Task<CallToolResult> OrganizeCanvas(
        [Description("When true, write pivots, optional groups, and hide long wires. Default false returns the plan only.")]
        bool apply = false,
        [Description("Optional InstanceGuids to move. Omit to organize the whole document. Others stay fixed.")]
        string[]? componentIds = null,
        [Description("When true, return groupCandidates for larger ungrouped function clusters. Agent supplies names via namedGroups.")]
        bool proposeGroups = false,
        [Description("Optional named groups as JSON array of {name, componentIds, color?}. Applied only when apply is true and ids are ungrouped.")]
        string? namedGroupsJson = null,
        [Description("compact (default) packs computational blocks then aligns them; reflow also places islands L-to-R.")]
        string layoutMode = "compact",
        [Description("Horizontal gap between layers (default 280).")]
        float columnGap = CanvasLayout.DefaultColumnGap,
        [Description("Vertical gap between nodes in a layer (default 40).")]
        float rowGap = CanvasLayout.DefaultRowGap,
        [Description("Space above a named block for the Scribble title (default 48).")]
        float titleBand = CanvasLayout.DefaultTitleBand)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            if (columnGap < 0 || rowGap < 0 || titleBand < 0 ||
                float.IsNaN(columnGap) || float.IsNaN(rowGap) || float.IsNaN(titleBand) ||
                float.IsInfinity(columnGap) || float.IsInfinity(rowGap) || float.IsInfinity(titleBand))
            {
                throw new ArgumentOutOfRangeException(nameof(columnGap), "Gaps and titleBand must be finite and >= 0.");
            }

            var mode = ParseLayoutMode(layoutMode);
            var namedSpecs = ParseNamedGroups(namedGroupsJson);
            HashSet<Guid>? filter = null;
            if (componentIds is { Length: > 0 })
            {
                filter = new HashSet<Guid>();
                foreach (var s in componentIds)
                {
                    if (!Guid.TryParse(s, out var g))
                    {
                        throw new ArgumentException($"Invalid GUID in componentIds: {s}");
                    }

                    filter.Add(g);
                }
            }

            var result = await UiThreadHelper.InvokeAsync(() =>
                RunOrganize(apply, filter, proposeGroups, namedSpecs, mode, columnGap, rowGap, titleBand));

            return result;
        }, nameof(OrganizeCanvas));
    }

    private static LayoutMode ParseLayoutMode(string? layoutMode)
    {
        if (string.IsNullOrWhiteSpace(layoutMode) ||
            layoutMode.Equals("compact", StringComparison.OrdinalIgnoreCase))
        {
            return LayoutMode.Compact;
        }

        if (layoutMode.Equals("reflow", StringComparison.OrdinalIgnoreCase))
        {
            return LayoutMode.Reflow;
        }

        throw new ArgumentException("layoutMode must be \"compact\" or \"reflow\".");
    }

    private static object RunOrganize(
        bool apply,
        HashSet<Guid>? filter,
        bool proposeGroups,
        List<NamedGroupSpec> namedSpecs,
        LayoutMode mode,
        float columnGap,
        float rowGap,
        float titleBand)
    {
        var doc = Instances.ActiveCanvas?.Document
                  ?? throw new InvalidOperationException("No active Grasshopper document");

        var (nodes, edges, memberships, alreadyGrouped) = BuildGraph(doc, filter);
        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            memberships,
            columnGap,
            rowGap,
            titleBand,
            proposeGroups,
            namedSpecs.Count > 0 ? namedSpecs : null,
            mode);

        object? applied = null;
        var undoOk = false;
        var wiresHidden = 0;
        if (apply)
        {
            undoOk = TryBeginUndo(doc, "Cassis Organize Canvas");
            ApplyMoves(doc, plan.Moves);
            wiresHidden = ApplyWireHides(doc, plan.WireHides);
            var groupResults = new List<object>();
            foreach (var ng in namedSpecs)
            {
                groupResults.Add(ApplyNamedGroup(doc, ng, plan.Scribbles, titleBand, alreadyGrouped));
            }

            doc.NewSolution(false);
            applied = new
            {
                moveCount = plan.Moves.Count,
                wiresHidden,
                groups = groupResults,
                undoRecorded = undoOk,
            };
        }

        return new
        {
            success = true,
            dryRun = !apply,
            layoutMode = mode == LayoutMode.Reflow ? "reflow" : "compact",
            moves = plan.Moves.Select(m => new
            {
                id = m.Id.ToString(),
                from = new { x = m.FromX, y = m.FromY },
                to = new { x = m.ToX, y = m.ToY },
            }),
            supernodes = plan.Supernodes.Select(s => new
            {
                groupId = s.GroupId.ToString(),
                memberIds = s.MemberIds.Select(id => id.ToString()),
            }),
            groupCandidates = plan.GroupCandidates.Select(c => new
            {
                provisionalName = c.ProvisionalName,
                componentIds = c.ComponentIds.Select(id => id.ToString()),
            }),
            scribbles = plan.Scribbles.Select(s => new
            {
                name = s.Name,
                text = s.Text,
                x = s.X,
                y = s.Y,
            }),
            wireHides = plan.WireHides.Select(w => new
            {
                from = w.From.ToString(),
                to = w.To.ToString(),
                length = Math.Round(w.Length, 1),
            }),
            applied,
            warnings = BuildResponseWarnings(plan.Warnings, filter, apply, undoOk),
        };
    }

    private static IEnumerable<string> BuildResponseWarnings(
        IReadOnlyList<string> planWarnings,
        HashSet<Guid>? filter,
        bool apply,
        bool undoOk)
    {
        foreach (var w in planWarnings)
        {
            yield return w;
        }

        if (filter != null)
        {
            yield return "componentIds filter is active. Objects outside the filter stay fixed. Cross-boundary wires may still cross.";
        }

        if (apply && !undoOk)
        {
            yield return "Undo record was not created. Apply may need several Ctrl+Z steps.";
        }
    }

    private static (List<LayoutNode> Nodes, List<LayoutEdge> Edges, List<LayoutMembership> Memberships, HashSet<Guid> AlreadyGrouped)
        BuildGraph(GH_Document doc, HashSet<Guid>? filter)
    {
        var memberships = new List<LayoutMembership>();
        var alreadyGrouped = new HashSet<Guid>();
        foreach (var obj in doc.Objects.OfType<GH_Group>())
        {
            var ids = obj.ObjectIDs?.ToList() ?? new List<Guid>();
            memberships.Add(new LayoutMembership(obj.InstanceGuid, ids));
            foreach (var id in ids)
            {
                alreadyGrouped.Add(id);
            }
        }

        // Expand nested group membership into leaf set for "already grouped" leaves
        var groupIds = memberships.Select(m => m.GroupId).ToHashSet();
        var expandedGrouped = new HashSet<Guid>();
        foreach (var m in memberships)
        {
            foreach (var id in ExpandLeaves(m.GroupId, memberships, groupIds))
            {
                expandedGrouped.Add(id);
            }
        }

        var nodes = new List<LayoutNode>();
        foreach (var obj in doc.Objects)
        {
            if (obj is GH_Group)
            {
                continue;
            }

            if (obj.Attributes == null)
            {
                obj.CreateAttributes();
            }

            var bounds = obj.Attributes?.Bounds ?? RectangleF.Empty;
            var pivot = obj.Attributes?.Pivot ?? PointF.Empty;
            var w = bounds.Width > 0 ? bounds.Width : 80f;
            var h = bounds.Height > 0 ? bounds.Height : 40f;
            var frozen = filter != null && !filter.Contains(obj.InstanceGuid);
            // Existing scribbles outside dataflow stay put unless filtered in as members of a move set
            if (obj is GH_Scribble && filter == null && !expandedGrouped.Contains(obj.InstanceGuid))
            {
                frozen = true;
            }

            nodes.Add(new LayoutNode(obj.InstanceGuid, pivot.X, pivot.Y, w, h, frozen));
        }

        var nodeSet = nodes.Select(n => n.Id).ToHashSet();
        var edges = new List<LayoutEdge>();
        var edgeSet = new HashSet<(Guid, Guid)>();

        void AddEdge(Guid from, Guid to)
        {
            if (from == to || !nodeSet.Contains(from) || !nodeSet.Contains(to))
            {
                return;
            }

            if (edgeSet.Add((from, to)))
            {
                edges.Add(new LayoutEdge(from, to));
            }
        }

        foreach (var obj in doc.Objects)
        {
            if (obj is GH_Group)
            {
                continue;
            }

            foreach (var param in EnumerateParams(obj))
            {
                if (param.SourceCount == 0)
                {
                    continue;
                }

                foreach (var src in param.Sources)
                {
                    if (src == null)
                    {
                        continue;
                    }

                    var fromOwner = ResolveOwnerGuid(src);
                    var toOwner = obj.InstanceGuid;
                    AddEdge(fromOwner, toOwner);
                }
            }
        }

        return (nodes, edges, memberships, expandedGrouped);
    }

    private static IEnumerable<Guid> ExpandLeaves(
        Guid groupId,
        List<LayoutMembership> memberships,
        HashSet<Guid> groupIds)
    {
        var map = memberships.ToDictionary(m => m.GroupId, m => m.MemberIds);
        var stack = new Stack<Guid>();
        stack.Push(groupId);
        var seen = new HashSet<Guid>();
        while (stack.Count > 0)
        {
            var g = stack.Pop();
            if (!map.TryGetValue(g, out var members))
            {
                continue;
            }

            foreach (var id in members)
            {
                if (groupIds.Contains(id))
                {
                    if (seen.Add(id))
                    {
                        stack.Push(id);
                    }
                }
                else
                {
                    yield return id;
                }
            }
        }
    }

    private static IEnumerable<IGH_Param> EnumerateParams(IGH_DocumentObject obj)
    {
        if (obj is IGH_Component comp)
        {
            foreach (var p in comp.Params.Input)
            {
                yield return p;
            }

            yield break;
        }

        if (obj is IGH_Param param)
        {
            yield return param;
        }
    }

    private static Guid ResolveOwnerGuid(IGH_Param src)
    {
        var top = src.Attributes?.GetTopLevel?.DocObject;
        if (top != null)
        {
            return top.InstanceGuid;
        }

        return src.InstanceGuid;
    }

    private static void ApplyMoves(GH_Document doc, IReadOnlyList<LayoutMove> moves)
    {
        foreach (var move in moves)
        {
            var obj = doc.FindObject(move.Id, false);
            if (obj?.Attributes == null)
            {
                continue;
            }

            obj.Attributes.Pivot = new PointF(move.ToX, move.ToY);
            obj.Attributes.ExpireLayout();
        }
    }

    /// <summary>
    /// Hide wires into destination params when the laid-out span is too long (GH_ParamWireDisplay.hidden).
    /// </summary>
    private static int ApplyWireHides(GH_Document doc, IReadOnlyList<WireHideHint> hints)
    {
        if (hints.Count == 0)
        {
            return 0;
        }

        var hidePairs = hints.Select(h => (h.From, h.To)).ToHashSet();
        var count = 0;
        foreach (var obj in doc.Objects)
        {
            if (obj is GH_Group)
            {
                continue;
            }

            foreach (var param in EnumerateParams(obj))
            {
                if (param.SourceCount == 0)
                {
                    continue;
                }

                var toOwner = obj.InstanceGuid;
                var hide = false;
                foreach (var src in param.Sources)
                {
                    if (src == null)
                    {
                        continue;
                    }

                    var fromOwner = ResolveOwnerGuid(src);
                    if (hidePairs.Contains((fromOwner, toOwner)))
                    {
                        hide = true;
                        break;
                    }
                }

                if (!hide)
                {
                    continue;
                }

                param.WireDisplay = GH_ParamWireDisplay.hidden;
                count++;
            }
        }

        return count;
    }

    private static object ApplyNamedGroup(
        GH_Document doc,
        NamedGroupSpec spec,
        IReadOnlyList<ScribblePlan> scribbles,
        float titleBand,
        HashSet<Guid> alreadyGrouped)
    {
        var members = new List<IGH_DocumentObject>();
        var skipped = new List<string>();
        foreach (var id in spec.ComponentIds)
        {
            if (alreadyGrouped.Contains(id))
            {
                skipped.Add(id.ToString());
                continue;
            }

            var obj = doc.FindObject(id, false);
            if (obj == null || obj is GH_Group)
            {
                skipped.Add(id.ToString());
                continue;
            }

            members.Add(obj);
        }

        if (members.Count == 0)
        {
            return new
            {
                success = false,
                name = spec.Name,
                message = "No ungrouped members to wrap.",
                skipped,
            };
        }

        float minX = float.MaxValue, minY = float.MaxValue;
        foreach (var m in members)
        {
            var b = m.Attributes?.Bounds ?? RectangleF.Empty;
            var p = m.Attributes?.Pivot ?? PointF.Empty;
            minX = Math.Min(minX, b.Width > 0 ? b.Left : p.X);
            minY = Math.Min(minY, b.Height > 0 ? b.Top : p.Y);
        }

        var scribblePlan = scribbles.FirstOrDefault(s =>
            string.Equals(s.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
        var sx = scribblePlan?.X ?? minX;
        var sy = scribblePlan?.Y ?? (minY - titleBand);
        var text = scribblePlan?.Text ?? spec.Name.ToUpperInvariant();

        var scribble = CreateScribble(text, sx, sy);
        if (!doc.AddObject(scribble, false))
        {
            return new { success = false, name = spec.Name, message = "Failed to add Scribble." };
        }

        var group = new GH_Group();
        group.CreateAttributes();
        group.NickName = spec.Name;
        if (!string.IsNullOrWhiteSpace(spec.Color))
        {
            try
            {
                group.Colour = ColorTranslator.FromHtml(spec.Color!);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[MCP WARN] Invalid group color '{spec.Color}': {ex.Message}");
            }
        }

        foreach (var m in members)
        {
            group.AddObject(m.InstanceGuid);
            alreadyGrouped.Add(m.InstanceGuid);
        }

        group.AddObject(scribble.InstanceGuid);
        alreadyGrouped.Add(scribble.InstanceGuid);
        doc.AddObject(group, false);

        return new
        {
            success = true,
            name = spec.Name,
            groupId = group.InstanceGuid.ToString(),
            scribbleId = scribble.InstanceGuid.ToString(),
            memberCount = members.Count,
            skipped,
        };
    }

    private static GH_Scribble CreateScribble(string text, float x, float y)
    {
        var scribble = new GH_Scribble();
        scribble.CreateAttributes();
        scribble.Text = text;
        try
        {
            // Consolas bold ~40pt — matches plan defaults.
            scribble.Font = new Font("Consolas", 40f, FontStyle.Bold, GraphicsUnit.Point);
        }
        catch (Exception ex)
        {
            // shortcut: GH scribble Font setter differs by build; Text still lands
            RhinoApp.WriteLine($"[MCP WARN] Scribble font not set: {ex.Message}");
        }

        scribble.Attributes!.Pivot = new PointF(x, y);
        scribble.Attributes.ExpireLayout();
        return scribble;
    }

    private static bool TryBeginUndo(GH_Document doc, string name)
    {
        try
        {
            // Grasshopper records subsequent mutations under this event when supported.
            doc.UndoUtil.RecordEvent(name);
            return true;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[MCP WARN] Organize undo record failed: {ex.Message}");
            return false;
        }
    }

    private static List<NamedGroupSpec> ParseNamedGroups(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<NamedGroupSpec>();
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new ArgumentException("namedGroupsJson must be a JSON array of {name, componentIds, color?}.");
        }

        var list = new List<NamedGroupSpec>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Each namedGroups entry needs a name.");
            }

            if (!el.TryGetProperty("componentIds", out var idsEl) ||
                idsEl.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new ArgumentException($"namedGroups '{name}' needs componentIds array.");
            }

            var ids = new List<Guid>();
            foreach (var idEl in idsEl.EnumerateArray())
            {
                var s = idEl.GetString();
                if (!Guid.TryParse(s, out var g))
                {
                    throw new ArgumentException($"Invalid componentId in namedGroups '{name}': {s}");
                }

                ids.Add(g);
            }

            string? color = null;
            if (el.TryGetProperty("color", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                color = c.GetString();
            }

            list.Add(new NamedGroupSpec(name!, ids, color));
        }

        return list;
    }
}
