using System;
using System.Collections.Generic;
using System.Linq;

namespace Cassis.Utilities;

/// <summary>
/// Canvas layout modes. Compact is the default (block-first: function clusters, then block align).
/// </summary>
public enum LayoutMode
{
    /// <summary>Computational blocks (function clusters) packed L→R with pin align; blocks aligned as wholes.</summary>
    Compact = 0,

    /// <summary>Same block packing, then place islands on a global L→R strip (stronger reorder).</summary>
    Reflow = 1,
}

/// <summary>
/// Pure Sugiyama-lite layout: computational blocks first, then block placement (no Grasshopper types).
/// </summary>
public static class CanvasLayout
{
    // Shelf-of-blocks default: 120/24 stays readable; 280/40 is still on the density scrubber (sparse).
    public const float DefaultColumnGap = 120f;
    public const float DefaultRowGap = 24f;
    public const float DefaultTitleBand = 48f;
    public const float DefaultIslandGap = 120f;
    public const float DefaultWireHideLength = 720f;
    public const float MaxExtentGrowth = 1.75f;
    public const int MinCandidateSize = 2;
    /// <summary>Scribbles / proposeGroups only for larger function clusters.</summary>
    public const int MinScribbleSize = 4;
    public const int MaxCandidateSize = 25;

    public static LayoutPlan Compute(
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        IReadOnlyList<LayoutMembership> groups,
        float columnGap = DefaultColumnGap,
        float rowGap = DefaultRowGap,
        float titleBand = DefaultTitleBand,
        bool proposeGroups = false,
        IReadOnlyList<NamedGroupSpec>? namedGroups = null,
        LayoutMode mode = LayoutMode.Compact)
    {
        var warnings = new List<string>();
        var byId = nodes.ToDictionary(n => n.Id);
        var membership = BuildOutermostMembership(groups, byId.Keys.ToHashSet(), warnings);

        var unitOf = new Dictionary<Guid, Guid>();
        var unitMembers = new Dictionary<Guid, List<Guid>>();
        var unitSizes = new Dictionary<Guid, (float W, float H)>();
        var unitPivot = new Dictionary<Guid, (float X, float Y)>();
        var frozenUnits = new HashSet<Guid>();

        foreach (var node in nodes)
        {
            if (membership.TryGetValue(node.Id, out var groupId))
            {
                unitOf[node.Id] = groupId;
                if (!unitMembers.TryGetValue(groupId, out var list))
                {
                    list = new List<Guid>();
                    unitMembers[groupId] = list;
                    unitSizes[groupId] = (0f, 0f);
                    unitPivot[groupId] = (0f, 0f);
                }

                list.Add(node.Id);
            }
            else
            {
                unitOf[node.Id] = node.Id;
                unitMembers[node.Id] = new List<Guid> { node.Id };
                unitSizes[node.Id] = (node.Width, node.Height);
                unitPivot[node.Id] = (node.PivotX, node.PivotY);
                if (node.Frozen)
                {
                    frozenUnits.Add(node.Id);
                }
            }
        }

        foreach (var kv in unitMembers.ToList())
        {
            var unitId = kv.Key;
            var members = kv.Value;
            if (members.Count == 1 && unitId == members[0])
            {
                continue;
            }

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var anyFrozen = false;
            foreach (var mid in members)
            {
                var n = byId[mid];
                anyFrozen |= n.Frozen;
                minX = Math.Min(minX, n.PivotX - n.Width * 0.5f);
                minY = Math.Min(minY, n.PivotY - n.Height * 0.5f);
                maxX = Math.Max(maxX, n.PivotX + n.Width * 0.5f);
                maxY = Math.Max(maxY, n.PivotY + n.Height * 0.5f);
            }

            unitSizes[unitId] = (Math.Max(20f, maxX - minX), Math.Max(20f, maxY - minY));
            unitPivot[unitId] = ((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            if (anyFrozen)
            {
                frozenUnits.Add(unitId);
            }
        }

        // Repack members inside each GH group so supernodes are tight tiles (not old sprawl AABBs).
        var groupLocal = TightenGroupMembers(
            unitMembers,
            unitSizes,
            unitPivot,
            edges,
            byId,
            columnGap,
            rowGap,
            warnings);

        var unitEdges = new List<(Guid From, Guid To)>();
        foreach (var e in edges)
        {
            if (!unitOf.TryGetValue(e.From, out var a) || !unitOf.TryGetValue(e.To, out var b) || a == b)
            {
                continue;
            }

            unitEdges.Add((a, b));
        }

        var movable = unitMembers.Keys.Where(u => !frozenUnits.Contains(u)).ToHashSet();
        if (movable.Count == 0)
        {
            return EmptyPlan(warnings);
        }

        var needsTitle = BuildNeedsTitle(movable, unitOf, unitMembers, namedGroups);
        var islands = WeaklyConnectedIslands(movable, unitEdges);
        var unitTarget = new Dictionary<Guid, (float X, float Y)>();
        var unitStage = new Dictionary<Guid, int>();

        // Original global AABB for extent-growth warning
        var oldBounds = UnitBounds(movable, unitPivot, unitSizes);

        if (mode == LayoutMode.Compact)
        {
            // Block-first: pack each function cluster, then align clusters as wholes.
            foreach (var island in islands)
            {
                PackIslandBlocks(
                    island,
                    unitEdges,
                    unitPivot,
                    unitSizes,
                    needsTitle,
                    columnGap,
                    rowGap,
                    titleBand,
                    unitTarget,
                    unitStage,
                    warnings);
            }

        }
        else
        {
            // Reflow: same block packing per island, place islands L→R by old X.
            float cursorX = oldBounds.MinX;
            foreach (var island in islands.OrderBy(i => i.Average(u => unitPivot[u].X)))
            {
                PackIslandBlocks(
                    island,
                    unitEdges,
                    unitPivot,
                    unitSizes,
                    needsTitle,
                    columnGap,
                    rowGap,
                    titleBand,
                    unitTarget,
                    unitStage,
                    warnings);
                var localBounds = UnitBounds(island, unitTarget, unitSizes);
                var oldCy = island.Average(u => unitPivot[u].Y);
                var destMinY = oldCy - localBounds.Height * 0.5f;
                var ox = cursorX - localBounds.MinX;
                var oy = destMinY - localBounds.MinY;
                foreach (var u in island)
                {
                    var p = unitTarget[u];
                    unitTarget[u] = (p.X + ox, p.Y + oy);
                }

                cursorX += localBounds.Width + DefaultIslandGap;
            }
        }

        var leafTarget = ExpandToLeaves(movable, unitMembers, unitPivot, unitTarget, byId, groupLocal);
        var leafStage = new Dictionary<Guid, int>();
        foreach (var u in movable)
        {
            if (!unitStage.TryGetValue(u, out var stage) || !unitMembers.TryGetValue(u, out var members))
            {
                continue;
            }

            foreach (var mid in members)
            {
                if (leafTarget.ContainsKey(mid))
                {
                    leafStage[mid] = stage;
                }
            }
        }

        // One global centroid park (per-island park undoes inter-island separation).
        var leaves = leafTarget.Keys.ToHashSet();
        var oldLeaf = leaves.ToDictionary(id => id, id => (byId[id].PivotX, byId[id].PivotY));
        AnchorCentroid(leaves, oldLeaf, leafTarget);

        if (mode == LayoutMode.Compact)
        {
            var leafSizes = leaves.ToDictionary(id => id, id => (byId[id].Width, byId[id].Height));
            var oldLeafBounds = LeafBounds(oldLeaf, byId);
            ShelfPlaceSets(
                islands
                    .Select(island =>
                    {
                        var members = island
                            .SelectMany(u => unitMembers[u])
                            .Where(leaves.Contains)
                            .ToList();
                        float cx = 0, cy = 0;
                        foreach (var id in members)
                        {
                            var o = oldLeaf[id];
                            cx += o.PivotX;
                            cy += o.PivotY;
                        }

                        var inv = members.Count > 0 ? 1f / members.Count : 1f;
                        return (Members: (IReadOnlyCollection<Guid>)members, Cx: cx * inv, Cy: cy * inv);
                    })
                    .Where(t => t.Members.Count > 0)
                    .OrderBy(t => t.Cx)
                    .ThenBy(t => t.Cy)
                    .Select(t => t.Members)
                    .ToList(),
                leafTarget,
                leafSizes,
                Math.Max(rowGap, columnGap * 0.25f),
                oldLeafBounds);
            // Shelf place shifts the doc — re-park global centroid without restacking islands.
            AnchorCentroid(leaves, oldLeaf, leafTarget);
        }

        var newBounds = LeafBounds(leafTarget, byId);
        if (oldBounds.Width > 1f && newBounds.Width / oldBounds.Width > MaxExtentGrowth)
        {
            warnings.Add(
                $"Layout width grew {newBounds.Width / oldBounds.Width:0.0}× (cap {MaxExtentGrowth:0.00}). Prefer compact mode or a smaller selection.");
        }

        if (oldBounds.Height > 1f && newBounds.Height / oldBounds.Height > MaxExtentGrowth)
        {
            warnings.Add(
                $"Layout height grew {newBounds.Height / oldBounds.Height:0.0}× (cap {MaxExtentGrowth:0.00}). Layer wrap may need a tighter maxColumnHeight.");
        }

        var moves = new List<LayoutMove>();
        foreach (var id in leafTarget.Keys)
        {
            var n = byId[id];
            var t = leafTarget[id];
            if (Math.Abs(t.X - n.PivotX) > 0.5f || Math.Abs(t.Y - n.PivotY) > 0.5f)
            {
                moves.Add(new LayoutMove(id, n.PivotX, n.PivotY, t.X, t.Y));
            }
        }

        var wireHides = BuildWireHideHints(edges, byId, leafTarget, DefaultWireHideLength);

        var supernodes = unitMembers
            .Where(kv => kv.Value.Count > 1 || groups.Any(g => g.GroupId == kv.Key))
            .Select(kv => new SupernodeInfo(kv.Key, kv.Value.ToArray()))
            .ToArray();

        var candidates = proposeGroups
            ? ProposeFunctionalBlocks(nodes, edges, membership, warnings)
            : Array.Empty<GroupCandidate>();

        var scribbles = BuildScribbles(namedGroups, byId, leafTarget, titleBand);

        return new LayoutPlan(
            moves.ToArray(),
            supernodes,
            candidates,
            scribbles,
            wireHides,
            warnings,
            leafStage);
    }

    /// <summary>
    /// Split WCC into function-sized blocks, pack each L→R with pin align,
    /// shelf-place blocks by rank (not per-block centroid park — that restacks the mess),
    /// then park the whole island on its old centroid.
    /// </summary>
    private static void PackIslandBlocks(
        HashSet<Guid> island,
        List<(Guid From, Guid To)> unitEdges,
        Dictionary<Guid, (float X, float Y)> unitPivot,
        Dictionary<Guid, (float W, float H)> unitSizes,
        HashSet<Guid> needsTitle,
        float columnGap,
        float rowGap,
        float titleBand,
        Dictionary<Guid, (float X, float Y)> unitTarget,
        Dictionary<Guid, int> unitStage,
        List<string> warnings)
    {
        var rankHint = LongestPathRanks(island, unitEdges);
        var blocks = SplitIsland(island.ToList(), rankHint)
            .Select(c => c.ToHashSet())
            .Where(b => b.Count > 0)
            .ToList();
        if (blocks.Count == 0)
        {
            foreach (var u in island)
            {
                unitTarget[u] = unitPivot[u];
            }

            return;
        }

        // Density follows columnGap (was floored at 80 and capped growth at 1.75 → always sparse).
        var blockColGap = Math.Max(36f, Math.Min(columnGap, columnGap * 0.65f + 24f));
        var widthCap = Math.Min(MaxExtentGrowth, Math.Max(1.1f, 1.05f + columnGap / 400f));
        var blockGap = Math.Max(rowGap * 1.5f, columnGap * 0.45f);

        foreach (var block in blocks)
        {
            var local = PackIslandReflow(
                block,
                unitEdges,
                unitPivot,
                unitSizes,
                needsTitle,
                blockColGap,
                rowGap,
                titleBand,
                warnings);
            AlignPins(block, unitEdges, local, unitSizes, rowGap);

            // Cap block width vs its old footprint (tighter when gaps are small).
            var oldBlock = UnitBounds(block, unitPivot, unitSizes);
            var packed = UnitBounds(block, local, unitSizes);
            if (oldBlock.Width > 1f && packed.Width > oldBlock.Width * widthCap)
            {
                var cx = packed.MinX + packed.Width * 0.5f;
                var sx = (oldBlock.Width * widthCap) / packed.Width;
                foreach (var u in block.ToList())
                {
                    var p = local[u];
                    local[u] = (cx + (p.X - cx) * sx, p.Y);
                }

                packed = UnitBounds(block, local, unitSizes);
            }

            // Normalize to origin — rank-column placer assigns world slots.
            foreach (var u in block)
            {
                var p = local[u];
                unitTarget[u] = (p.X - packed.MinX, p.Y - packed.MinY);
            }
        }

        var oldB = UnitBounds(island, unitPivot, unitSizes);

        // Rank columns: same-rank functions stack vertically, columns advance L→R.
        PlaceBlocksInRankColumns(
            blocks,
            rankHint,
            unitPivot,
            unitTarget,
            unitSizes,
            blockGap,
            unitStage); // records hierarchy stage per unit

        // Global Y barycentric after block shelf — cuts crossings MedianSweep lost between blocks.
        BarycentricNudgeY(island, unitEdges, unitTarget, unitSizes, rankHint, rowGap);

        // Park the whole island, not each block (keeps column grid intact).
        var newB = UnitBounds(island, unitTarget, unitSizes);
        var ox = (oldB.MinX + oldB.Width * 0.5f) - (newB.MinX + newB.Width * 0.5f);
        var oy = (oldB.MinY + oldB.Height * 0.5f) - (newB.MinY + newB.Height * 0.5f);
        foreach (var u in island)
        {
            var p = unitTarget[u];
            unitTarget[u] = (p.X + ox, p.Y + oy);
        }

        newB = UnitBounds(island, unitTarget, unitSizes);
        if (oldB.Width > 1f && newB.Width > oldB.Width * MaxExtentGrowth)
        {
            warnings.Add(
                $"Block pack width grew {newB.Width / oldB.Width:0.0}×. Prefer a smaller selection.");
        }

        if (oldB.Height > 1f && newB.Height > oldB.Height * MaxExtentGrowth)
        {
            warnings.Add(
                $"Block pack height grew {newB.Height / oldB.Height:0.0}×. Prefer a smaller selection or reflow.");
        }
    }

    /// <summary>
    /// Place packed function blocks into hierarchical stages (L→R).
    /// Stages are balanced by block count so one mid-rank bucket cannot dominate.
    /// Tall stages wrap to a sub-lane (same stage).
    /// </summary>
    private static void PlaceBlocksInRankColumns(
        List<HashSet<Guid>> blocks,
        Dictionary<Guid, int> rankHint,
        Dictionary<Guid, (float X, float Y)> unitPivot,
        Dictionary<Guid, (float X, float Y)> target,
        Dictionary<Guid, (float W, float H)> sizes,
        float gap,
        Dictionary<Guid, int> unitStage)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        float MeanRank(HashSet<Guid> b)
        {
            float s = 0;
            foreach (var u in b)
            {
                s += rankHint.TryGetValue(u, out var r) ? r : 0;
            }

            return s / Math.Max(1, b.Count);
        }

        int RawRank(HashSet<Guid> b) => (int)Math.Round(MeanRank(b));

        var ordered = blocks
            .OrderBy(MeanRank)
            .ThenBy(b => b.Average(u => unitPivot[u].Y))
            .ThenBy(b => b.Average(u => unitPivot[u].X))
            .ToList();

        var rankGroups = ordered
            .GroupBy(RawRank)
            .OrderBy(g => g.Key)
            .Select(g => g
                .OrderBy(b => b.Average(u => unitPivot[u].Y))
                .ThenBy(b => b.Average(u => unitPivot[u].X))
                .ToList())
            .ToList();

        // Prefer more columns than √n when ranks allow — clearer L→R hierarchy.
        var maxStages = Math.Max(
            8,
            Math.Min(rankGroups.Count, (int)Math.Ceiling(Math.Sqrt(ordered.Count * 1.6) + 2)));
        var targetPerStage = Math.Max(1, (int)Math.Ceiling(ordered.Count / (double)maxStages));

        var stages = new List<List<HashSet<Guid>>>();
        var cur = new List<HashSet<Guid>>();
        foreach (var group in rankGroups)
        {
            if (cur.Count > 0
                && cur.Count + group.Count > targetPerStage
                && stages.Count < maxStages - 1)
            {
                stages.Add(cur);
                cur = new List<HashSet<Guid>>();
            }

            cur.AddRange(group);
        }

        if (cur.Count > 0)
        {
            stages.Add(cur);
        }

        var allHeights = ordered.Select(b => UnitBounds(b, target, sizes).Height).OrderBy(h => h).ToList();
        var medianBlockH = allHeights[allHeights.Count / 2];
        var maxColumnHeight = Math.Max(1100f, medianBlockH * 10f + gap * 9f);
        var stageGap = Math.Max(gap * 1.75f, gap + 48f);

        float cursorX = 0f;
        for (var stageIndex = 0; stageIndex < stages.Count; stageIndex++)
        {
            var stageBlocks = stages[stageIndex];
            float laneX = cursorX;
            float cursorY = 0f;
            float colW = 0f;
            float stageMaxX = cursorX;

            foreach (var block in stageBlocks)
            {
                var b = UnitBounds(block, target, sizes);
                if (cursorY > 0f && cursorY + b.Height > maxColumnHeight)
                {
                    laneX = stageMaxX + gap * 0.85f;
                    cursorY = 0f;
                    colW = 0f;
                }

                var dx = laneX - b.MinX;
                var dy = cursorY - b.MinY;
                foreach (var id in block)
                {
                    var p = target[id];
                    target[id] = (p.X + dx, p.Y + dy);
                    unitStage[id] = stageIndex;
                }

                var placed = UnitBounds(block, target, sizes);
                cursorY = placed.MaxY + gap;
                colW = Math.Max(colW, placed.Width);
                stageMaxX = Math.Max(stageMaxX, laneX + colW);
            }

            cursorX = stageMaxX + stageGap;
        }
    }

    /// <summary>
    /// Place packed sets L→R in the given order, wrap using oldExtent.Width as row budget
    /// (or strip/√n when that width is tiny). Keeps relative layout inside each set.
    /// </summary>
    private static void ShelfPlaceSets(
        IReadOnlyList<IReadOnlyCollection<Guid>> sets,
        Dictionary<Guid, (float X, float Y)> target,
        Dictionary<Guid, (float W, float H)> sizes,
        float gap,
        Bounds oldExtent)
    {
        if (sets.Count < 2)
        {
            return;
        }

        float stripW = 0f;
        var bounds = new Bounds[sets.Count];
        for (var i = 0; i < sets.Count; i++)
        {
            bounds[i] = UnitBounds(sets[i], target, sizes);
            stripW += bounds[i].Width + gap;
        }

        stripW = Math.Max(0f, stripW - gap);
        var rows = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(sets.Count)));
        // Prefer caller row budget (area-based); fall back to strip/√n.
        var rowWidth = oldExtent.Width > 1f
            ? oldExtent.Width
            : Math.Max(1f, stripW / rows);

        var cursorX = oldExtent.MinX;
        var cursorY = oldExtent.MinY;
        var rowH = 0f;
        var rowStarted = false;

        for (var i = 0; i < sets.Count; i++)
        {
            var b = bounds[i];
            if (b.Width < 1f && b.Height < 1f)
            {
                continue;
            }

            if (rowStarted && cursorX + b.Width > oldExtent.MinX + rowWidth)
            {
                cursorX = oldExtent.MinX;
                cursorY += rowH + gap;
                rowH = 0f;
            }

            var dx = cursorX - b.MinX;
            var dy = cursorY - b.MinY;
            foreach (var id in sets[i])
            {
                var p = target[id];
                target[id] = (p.X + dx, p.Y + dy);
            }

            cursorX += b.Width + gap;
            rowH = Math.Max(rowH, b.Height);
            rowStarted = true;
        }
    }

    /// <summary>
    /// Snap 1:1 chains so source and sink share Y (input↔output node points). Soft median otherwise.
    /// </summary>
    private static void AlignPins(
        HashSet<Guid> block,
        List<(Guid From, Guid To)> unitEdges,
        Dictionary<Guid, (float X, float Y)> target,
        Dictionary<Guid, (float W, float H)> unitSizes,
        float rowGap)
    {
        var outDeg = block.ToDictionary(u => u, _ => 0);
        var inDeg = block.ToDictionary(u => u, _ => 0);
        var blockEdges = new List<(Guid From, Guid To)>();
        foreach (var (from, to) in unitEdges)
        {
            if (!block.Contains(from) || !block.Contains(to))
            {
                continue;
            }

            outDeg[from]++;
            inDeg[to]++;
            blockEdges.Add((from, to));
        }

        foreach (var (from, to) in blockEdges)
        {
            if (outDeg[from] == 1 && inDeg[to] == 1)
            {
                var y = (target[from].Y + target[to].Y) * 0.5f;
                var a = target[from];
                var b = target[to];
                target[from] = (a.X, y);
                target[to] = (b.X, y);
                continue;
            }

            // Fan: pull the singleton side to the other's Y when degrees allow a clean snap.
            if (outDeg[from] == 1 && inDeg[to] > 1)
            {
                var a = target[from];
                target[from] = (a.X, target[to].Y);
            }
            else if (outDeg[from] > 1 && inDeg[to] == 1)
            {
                var b = target[to];
                target[to] = (b.X, target[from].Y);
            }
        }

        // Resolve vertical overlaps in the same X column after snaps.
        var byColumn = block
            .GroupBy(u => (int)Math.Round(target[u].X / 8f))
            .Select(g => g.OrderBy(u => target[u].Y).ToList());
        foreach (var col in byColumn)
        {
            for (var i = 1; i < col.Count; i++)
            {
                var prev = col[i - 1];
                var cur = col[i];
                var prevBottom = target[prev].Y + unitSizes[prev].H * 0.5f;
                var curTop = target[cur].Y - unitSizes[cur].H * 0.5f;
                var gap = prevBottom + rowGap - curTop;
                if (gap > 0)
                {
                    var t = target[cur];
                    target[cur] = (t.X, t.Y + gap);
                }
            }
        }
    }

    private static WireHideHint[] BuildWireHideHints(
        IReadOnlyList<LayoutEdge> edges,
        Dictionary<Guid, LayoutNode> byId,
        Dictionary<Guid, (float X, float Y)> leafTarget,
        float hideLength)
    {
        var hints = new List<WireHideHint>();
        foreach (var e in edges)
        {
            if (!byId.TryGetValue(e.From, out var fromNode) || !byId.TryGetValue(e.To, out var toNode))
            {
                continue;
            }

            var (fx, fy) = leafTarget.TryGetValue(e.From, out var ft)
                ? ft
                : (fromNode.PivotX, fromNode.PivotY);
            var (tx, ty) = leafTarget.TryGetValue(e.To, out var tt)
                ? tt
                : (toNode.PivotX, toNode.PivotY);

            // Approximate wire as right edge of source → left edge of sink.
            var x0 = fx + fromNode.Width * 0.5f;
            var x1 = tx - toNode.Width * 0.5f;
            var dx = x1 - x0;
            var dy = ty - fy;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len >= hideLength)
            {
                hints.Add(new WireHideHint(e.From, e.To, len));
            }
        }

        return hints.ToArray();
    }

    /// <summary>
    /// Reflow: longest-path layers → wrapped columns in a local strip.
    /// </summary>
    private static Dictionary<Guid, (float X, float Y)> PackIslandReflow(
        HashSet<Guid> island,
        List<(Guid From, Guid To)> unitEdges,
        Dictionary<Guid, (float X, float Y)> unitPivot,
        Dictionary<Guid, (float W, float H)> unitSizes,
        HashSet<Guid> needsTitle,
        float columnGap,
        float rowGap,
        float titleBand,
        List<string> warnings)
    {
        var (layers, adjOut, adjIn) = BuildRankedLayers(island, unitEdges, unitPivot, warnings);
        MedianSweep(layers, adjOut, adjIn, sweeps: 6);

        var medianH = island.Select(u => unitSizes[u].H).OrderBy(h => h).ElementAt(island.Count / 2);
        var maxColumnHeight = Math.Max(480f, medianH * 12f + rowGap * 11f);
        var target = new Dictionary<Guid, (float X, float Y)>();
        float cursorX = 0f;
        var layerKeys = layers.Keys.OrderBy(k => k).ToList();

        foreach (var layerIndex in layerKeys)
        {
            var row = layers[layerIndex];
            float subX = cursorX;
            float cursorY = 0f;
            float laneMaxW = 0f;
            float layerMaxX = cursorX;

            foreach (var u in row)
            {
                var (w, h) = unitSizes[u];
                var band = needsTitle.Contains(u) ? titleBand : 0f;
                var blockH = band + h;
                if (cursorY > 0f && cursorY + blockH > maxColumnHeight)
                {
                    subX = layerMaxX + columnGap * 0.55f;
                    cursorY = 0f;
                    laneMaxW = 0f;
                }

                target[u] = (subX + w * 0.5f, cursorY + band + h * 0.5f);
                cursorY = target[u].Y + h * 0.5f + rowGap;
                laneMaxW = Math.Max(laneMaxW, w);
                layerMaxX = Math.Max(layerMaxX, subX + laneMaxW);
            }

            cursorX = layerMaxX + columnGap;
        }

        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var layerIndex in layerKeys)
            {
                foreach (var u in layers[layerIndex])
                {
                    var neighborYs = new List<float>();
                    foreach (var p in adjIn[u])
                    {
                        if (target.TryGetValue(p, out var tp))
                        {
                            neighborYs.Add(tp.Y);
                        }
                    }

                    foreach (var c in adjOut[u])
                    {
                        if (target.TryGetValue(c, out var tc))
                        {
                            neighborYs.Add(tc.Y);
                        }
                    }

                    if (neighborYs.Count == 0)
                    {
                        continue;
                    }

                    neighborYs.Sort();
                    var med = neighborYs[neighborYs.Count / 2];
                    var cur = target[u];
                    // Snap toward connected node points (input↔output), not a soft blend.
                    target[u] = (cur.X, med);
                }

                var ordered = layers[layerIndex].OrderBy(u => target[u].Y).ToList();
                layers[layerIndex] = ordered;
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1];
                    var cur = ordered[i];
                    if (Math.Abs(target[prev].X - target[cur].X) > unitSizes[cur].W)
                    {
                        continue;
                    }

                    var prevBottom = target[prev].Y + unitSizes[prev].H * 0.5f;
                    var curTop = target[cur].Y - unitSizes[cur].H * 0.5f;
                    var gap = prevBottom + rowGap - curTop;
                    if (gap > 0)
                    {
                        var t = target[cur];
                        target[cur] = (t.X, t.Y + gap);
                    }
                }
            }
        }

        return target;
    }

    private static (Dictionary<int, List<Guid>> Layers, Dictionary<Guid, List<Guid>> AdjOut, Dictionary<Guid, List<Guid>> AdjIn)
        BuildRankedLayers(
            HashSet<Guid> island,
            List<(Guid From, Guid To)> unitEdges,
            Dictionary<Guid, (float X, float Y)> unitPivot,
            List<string> warnings)
    {
        var adjOut = island.ToDictionary(u => u, _ => new List<Guid>());
        var adjIn = island.ToDictionary(u => u, _ => new List<Guid>());
        var softDropped = 0;
        foreach (var (from, to) in unitEdges)
        {
            if (!island.Contains(from) || !island.Contains(to))
            {
                continue;
            }

            if (unitPivot[from].X > unitPivot[to].X + 1f)
            {
                softDropped++;
                continue;
            }

            adjOut[from].Add(to);
            adjIn[to].Add(from);
        }

        if (softDropped > 0)
        {
            warnings.Add($"Deferred {softDropped} right-to-left edge(s) for ranking (model X order).");
        }

        BreakCyclesDfs(island, adjOut, adjIn, warnings);

        var rank = island.ToDictionary(u => u, _ => 0);
        var order = TopoSort(island, adjOut, adjIn, warnings);
        foreach (var u in order)
        {
            var best = 0;
            foreach (var p in adjIn[u])
            {
                if (rank.TryGetValue(p, out var pr))
                {
                    best = Math.Max(best, pr + 1);
                }
            }

            rank[u] = best;
        }

        var layers = island
            .GroupBy(u => rank[u])
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(u => unitPivot[u].Y).ThenBy(u => u).ToList());

        return (layers, adjOut, adjIn);
    }

    private static void BreakCyclesDfs(
        HashSet<Guid> nodes,
        Dictionary<Guid, List<Guid>> adjOut,
        Dictionary<Guid, List<Guid>> adjIn,
        List<string> warnings)
    {
        var color = nodes.ToDictionary(n => n, _ => 0); // 0 white 1 gray 2 black
        var dropped = 0;

        void Visit(Guid u)
        {
            color[u] = 1;
            foreach (var v in adjOut[u].ToList())
            {
                if (color[v] == 1)
                {
                    // back-edge: drop
                    adjOut[u].Remove(v);
                    adjIn[v].Remove(u);
                    dropped++;
                    continue;
                }

                if (color[v] == 0)
                {
                    Visit(v);
                }
            }

            color[u] = 2;
        }

        foreach (var u in nodes.OrderBy(n => n))
        {
            if (color[u] == 0)
            {
                Visit(u);
            }
        }

        if (dropped > 0)
        {
            warnings.Add($"Removed {dropped} cycle back-edge(s) for ranking (DFS feedback).");
        }
    }

    private static List<HashSet<Guid>> WeaklyConnectedIslands(
        HashSet<Guid> movable,
        List<(Guid From, Guid To)> unitEdges)
    {
        var undirected = movable.ToDictionary(u => u, _ => new List<Guid>());
        foreach (var (a, b) in unitEdges)
        {
            if (!movable.Contains(a) || !movable.Contains(b))
            {
                continue;
            }

            undirected[a].Add(b);
            undirected[b].Add(a);
        }

        var seen = new HashSet<Guid>();
        var islands = new List<HashSet<Guid>>();
        foreach (var start in movable.OrderBy(u => u))
        {
            if (!seen.Add(start))
            {
                continue;
            }

            var stack = new Stack<Guid>();
            stack.Push(start);
            var island = new HashSet<Guid>();
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                island.Add(u);
                foreach (var v in undirected[u])
                {
                    if (seen.Add(v))
                    {
                        stack.Push(v);
                    }
                }
            }

            islands.Add(island);
        }

        return islands;
    }

    private static HashSet<Guid> BuildNeedsTitle(
        HashSet<Guid> movable,
        Dictionary<Guid, Guid> unitOf,
        Dictionary<Guid, List<Guid>> unitMembers,
        IReadOnlyList<NamedGroupSpec>? namedGroups)
    {
        var needsTitle = new HashSet<Guid>();
        if (namedGroups == null)
        {
            return needsTitle;
        }

        foreach (var ng in namedGroups)
        {
            var leaves = ng.ComponentIds.ToHashSet();
            foreach (var leaf in leaves)
            {
                if (unitOf.TryGetValue(leaf, out var u) && movable.Contains(u))
                {
                    needsTitle.Add(u);
                }
            }

            foreach (var u in movable)
            {
                var members = unitMembers[u];
                if (members.Count > 0 && members.All(leaves.Contains))
                {
                    needsTitle.Add(u);
                }
            }
        }

        return needsTitle;
    }

    private static Dictionary<Guid, (float X, float Y)> ExpandToLeaves(
        HashSet<Guid> movable,
        Dictionary<Guid, List<Guid>> unitMembers,
        Dictionary<Guid, (float X, float Y)> unitPivot,
        Dictionary<Guid, (float X, float Y)> unitTarget,
        Dictionary<Guid, LayoutNode> byId,
        Dictionary<Guid, (float X, float Y)> groupLocal)
    {
        var leafTarget = new Dictionary<Guid, (float X, float Y)>();
        foreach (var u in movable)
        {
            if (!unitTarget.TryGetValue(u, out var t))
            {
                continue;
            }

            var members = unitMembers[u];
            if (members.Count == 1 && members[0] == u)
            {
                leafTarget[u] = t;
                continue;
            }

            // Prefer tightened local offsets; fall back to rigid translate of the old pose.
            var useLocal = members.All(groupLocal.ContainsKey);
            if (useLocal)
            {
                foreach (var mid in members)
                {
                    var o = groupLocal[mid];
                    leafTarget[mid] = (t.X + o.X, t.Y + o.Y);
                }

                continue;
            }

            var old = unitPivot[u];
            var dx = t.X - old.X;
            var dy = t.Y - old.Y;
            foreach (var mid in members)
            {
                var n = byId[mid];
                leafTarget[mid] = (n.PivotX + dx, n.PivotY + dy);
            }
        }

        return leafTarget;
    }

    /// <summary>
    /// Pack leaves inside each multi-member group into a tight L→R block.
    /// Updates unitSizes to the packed footprint; returns offsets from the unit center.
    /// </summary>
    private static Dictionary<Guid, (float X, float Y)> TightenGroupMembers(
        Dictionary<Guid, List<Guid>> unitMembers,
        Dictionary<Guid, (float W, float H)> unitSizes,
        Dictionary<Guid, (float X, float Y)> unitPivot,
        IReadOnlyList<LayoutEdge> edges,
        Dictionary<Guid, LayoutNode> byId,
        float columnGap,
        float rowGap,
        List<string> warnings)
    {
        var local = new Dictionary<Guid, (float X, float Y)>();
        var innerGap = Math.Max(24f, Math.Min(columnGap * 0.45f, 80f));
        var tightened = 0;

        foreach (var kv in unitMembers)
        {
            var unitId = kv.Key;
            var members = kv.Value;
            if (members.Count == 1 && members[0] == unitId)
            {
                continue;
            }

            var memberSet = members.ToHashSet();
            var memberSizes = members.ToDictionary(m => m, m => (byId[m].Width, byId[m].Height));
            var ranks = LongestPathRanks(memberSet, edges);
            var ordered = members
                .OrderBy(m => ranks.TryGetValue(m, out var r) ? r : 0)
                .ThenBy(m => byId[m].PivotY)
                .ThenBy(m => byId[m].PivotX)
                .ToList();

            // Simple rank columns — avoid nested PackIslandReflow on every group.
            var packed = new Dictionary<Guid, (float X, float Y)>();
            var byRank = ordered
                .GroupBy(m => ranks.TryGetValue(m, out var r) ? r : 0)
                .OrderBy(g => g.Key)
                .ToList();
            float cursorX = 0f;
            foreach (var band in byRank)
            {
                float cursorY = 0f;
                float colW = 0f;
                foreach (var m in band)
                {
                    var (w, h) = memberSizes[m];
                    packed[m] = (cursorX + w * 0.5f, cursorY + h * 0.5f);
                    cursorY += h + rowGap;
                    colW = Math.Max(colW, w);
                }

                cursorX += colW + innerGap;
            }

            var bounds = UnitBounds(memberSet, packed, memberSizes);
            var cx = bounds.MinX + bounds.Width * 0.5f;
            var cy = bounds.MinY + bounds.Height * 0.5f;
            foreach (var m in members)
            {
                var p = packed[m];
                local[m] = (p.X - cx, p.Y - cy);
            }

            var oldSize = unitSizes[unitId];
            // Keep unitPivot (old centroid) so AnchorCentroid parks the group near home.
            unitSizes[unitId] = (Math.Max(20f, bounds.Width), Math.Max(20f, bounds.Height));
            if (oldSize.W * oldSize.H > unitSizes[unitId].W * unitSizes[unitId].H * 1.4f)
            {
                tightened++;
            }
        }

        if (tightened > 0)
        {
            warnings.Add($"Tightened {tightened} group supernode(s) to packed member bounds.");
        }

        return local;
    }

    private static void AnchorCentroid(
        HashSet<Guid> ids,
        Dictionary<Guid, (float X, float Y)> oldPivot,
        Dictionary<Guid, (float X, float Y)> target)
    {
        if (ids.Count == 0)
        {
            return;
        }

        float oldCx = 0, oldCy = 0, newCx = 0, newCy = 0;
        foreach (var id in ids)
        {
            oldCx += oldPivot[id].X;
            oldCy += oldPivot[id].Y;
            newCx += target[id].X;
            newCy += target[id].Y;
        }

        var inv = 1f / ids.Count;
        var ox = (oldCx * inv) - (newCx * inv);
        var oy = (oldCy * inv) - (newCy * inv);
        foreach (var id in ids.ToList())
        {
            var t = target[id];
            target[id] = (t.X + ox, t.Y + oy);
        }
    }

    private readonly record struct Bounds(float MinX, float MinY, float MaxX, float MaxY)
    {
        public float Width => MaxX - MinX;
        public float Height => MaxY - MinY;
    }

    private static Bounds UnitBounds(
        IEnumerable<Guid> ids,
        Dictionary<Guid, (float X, float Y)> pivot,
        Dictionary<Guid, (float W, float H)> sizes)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        var any = false;
        foreach (var id in ids)
        {
            any = true;
            var (x, y) = pivot[id];
            var (w, h) = sizes[id];
            minX = Math.Min(minX, x - w * 0.5f);
            minY = Math.Min(minY, y - h * 0.5f);
            maxX = Math.Max(maxX, x + w * 0.5f);
            maxY = Math.Max(maxY, y + h * 0.5f);
        }

        return any ? new Bounds(minX, minY, maxX, maxY) : new Bounds(0, 0, 0, 0);
    }

    private static Bounds LeafBounds(
        Dictionary<Guid, (float X, float Y)> leafTarget,
        Dictionary<Guid, LayoutNode> byId)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var kv in leafTarget)
        {
            var n = byId[kv.Key];
            var (x, y) = kv.Value;
            minX = Math.Min(minX, x - n.Width * 0.5f);
            minY = Math.Min(minY, y - n.Height * 0.5f);
            maxX = Math.Max(maxX, x + n.Width * 0.5f);
            maxY = Math.Max(maxY, y + n.Height * 0.5f);
        }

        return leafTarget.Count == 0 ? new Bounds(0, 0, 0, 0) : new Bounds(minX, minY, maxX, maxY);
    }

    private static ScribblePlan[] BuildScribbles(
        IReadOnlyList<NamedGroupSpec>? namedGroups,
        Dictionary<Guid, LayoutNode> byId,
        Dictionary<Guid, (float X, float Y)> leafTarget,
        float titleBand)
    {
        if (namedGroups == null)
        {
            return Array.Empty<ScribblePlan>();
        }

        var scribbles = new List<ScribblePlan>();
        foreach (var ng in namedGroups)
        {
            var ids = ng.ComponentIds.Where(byId.ContainsKey).ToList();
            if (ids.Count == 0)
            {
                continue;
            }

            float minX = float.MaxValue, minY = float.MaxValue;
            foreach (var id in ids)
            {
                var (x, y) = leafTarget.TryGetValue(id, out var t)
                    ? t
                    : (byId[id].PivotX, byId[id].PivotY);
                var n = byId[id];
                minX = Math.Min(minX, x - n.Width * 0.5f);
                minY = Math.Min(minY, y - n.Height * 0.5f);
            }

            scribbles.Add(new ScribblePlan(
                ng.Name,
                ng.Name.ToUpperInvariant(),
                minX,
                minY - titleBand));
        }

        return scribbles.ToArray();
    }

    private static LayoutPlan EmptyPlan(List<string> warnings) =>
        new(Array.Empty<LayoutMove>(), Array.Empty<SupernodeInfo>(), Array.Empty<GroupCandidate>(),
            Array.Empty<ScribblePlan>(), Array.Empty<WireHideHint>(), warnings);

    private static Dictionary<Guid, Guid> BuildOutermostMembership(
        IReadOnlyList<LayoutMembership> groups,
        HashSet<Guid> knownLeaves,
        List<string> warnings)
    {
        var groupIds = groups.Select(g => g.GroupId).ToHashSet();
        var parent = new Dictionary<Guid, Guid>();
        foreach (var g in groups)
        {
            foreach (var mid in g.MemberIds)
            {
                if (groupIds.Contains(mid))
                {
                    parent[mid] = g.GroupId;
                }
            }
        }

        Guid RootOf(Guid g)
        {
            var seen = new HashSet<Guid>();
            while (parent.TryGetValue(g, out var p))
            {
                if (!seen.Add(g))
                {
                    warnings.Add($"Group cycle involving {g:D}");
                    break;
                }

                g = p;
            }

            return g;
        }

        var leafToRoot = new Dictionary<Guid, Guid>();
        foreach (var g in groups)
        {
            var root = RootOf(g.GroupId);
            foreach (var mid in g.MemberIds)
            {
                if (groupIds.Contains(mid) || !knownLeaves.Contains(mid))
                {
                    continue;
                }

                if (!leafToRoot.ContainsKey(mid))
                {
                    leafToRoot[mid] = root;
                }
                else
                {
                    leafToRoot[mid] = RootOf(leafToRoot[mid]);
                    var other = RootOf(root);
                    if (!parent.ContainsKey(leafToRoot[mid]) && parent.ContainsKey(other))
                    {
                        leafToRoot[mid] = other;
                    }
                }
            }
        }

        return leafToRoot;
    }

    private static List<Guid> TopoSort(
        HashSet<Guid> nodes,
        Dictionary<Guid, List<Guid>> adjOut,
        Dictionary<Guid, List<Guid>> adjIn,
        List<string> warnings)
    {
        var indeg = nodes.ToDictionary(n => n, n => adjIn[n].Count);
        var q = new Queue<Guid>(nodes.Where(n => indeg[n] == 0).OrderBy(n => unitStable(n)));
        var result = new List<Guid>();
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            result.Add(u);
            foreach (var v in adjOut[u])
            {
                indeg[v]--;
                if (indeg[v] == 0)
                {
                    q.Enqueue(v);
                }
            }
        }

        if (result.Count != nodes.Count)
        {
            warnings.Add("Topo sort incomplete after cycle break; appending remaining nodes.");
            foreach (var n in nodes.OrderBy(n => n))
            {
                if (!result.Contains(n))
                {
                    result.Add(n);
                }
            }
        }

        return result;

        static Guid unitStable(Guid g) => g;
    }

    private static void MedianSweep(
        Dictionary<int, List<Guid>> layers,
        Dictionary<Guid, List<Guid>> adjOut,
        Dictionary<Guid, List<Guid>> adjIn,
        int sweeps = 2)
    {
        var keys = layers.Keys.OrderBy(k => k).ToList();
        for (var sweep = 0; sweep < sweeps; sweep++)
        {
            for (var i = 1; i < keys.Count; i++)
            {
                OrderByMedian(layers[keys[i]], adjIn, layers);
            }

            for (var i = keys.Count - 2; i >= 0; i--)
            {
                OrderByMedian(layers[keys[i]], adjOut, layers);
            }
        }
    }

    /// <summary>
    /// Pull each unit's Y toward the median of its neighbors (barycentric), then de-overlap
    /// within the same rank. Keeps X from column placement.
    /// </summary>
    private static void BarycentricNudgeY(
        HashSet<Guid> island,
        List<(Guid From, Guid To)> unitEdges,
        Dictionary<Guid, (float X, float Y)> target,
        Dictionary<Guid, (float W, float H)> sizes,
        Dictionary<Guid, int> rankHint,
        float rowGap)
    {
        var adj = island.ToDictionary(u => u, _ => new List<Guid>());
        foreach (var (from, to) in unitEdges)
        {
            if (!island.Contains(from) || !island.Contains(to))
            {
                continue;
            }

            adj[from].Add(to);
            adj[to].Add(from);
        }

        for (var pass = 0; pass < 4; pass++)
        {
            foreach (var u in island)
            {
                var ns = adj[u];
                if (ns.Count == 0)
                {
                    continue;
                }

                var ys = ns.Select(n => target[n].Y).OrderBy(y => y).ToList();
                var med = ys[ys.Count / 2];
                var cur = target[u];
                // Blend so one pass does not yank a whole column apart.
                target[u] = (cur.X, cur.Y * 0.35f + med * 0.65f);
            }

            foreach (var layer in island.GroupBy(u => rankHint.TryGetValue(u, out var r) ? r : 0))
            {
                var ordered = layer.OrderBy(u => target[u].Y).ThenBy(u => u).ToList();
                for (var i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1];
                    var cur = ordered[i];
                    // Only resolve when they share an X lane (same column).
                    if (Math.Abs(target[prev].X - target[cur].X) > Math.Max(sizes[prev].W, sizes[cur].W))
                    {
                        continue;
                    }

                    var prevBottom = target[prev].Y + sizes[prev].H * 0.5f;
                    var curTop = target[cur].Y - sizes[cur].H * 0.5f;
                    var need = prevBottom + rowGap - curTop;
                    if (need > 0)
                    {
                        var t = target[cur];
                        target[cur] = (t.X, t.Y + need);
                    }
                }
            }
        }
    }

    private static void OrderByMedian(
        List<Guid> layer,
        Dictionary<Guid, List<Guid>> neighborMap,
        Dictionary<int, List<Guid>> layers)
    {
        var pos = new Dictionary<Guid, int>();
        foreach (var kv in layers)
        {
            for (var i = 0; i < kv.Value.Count; i++)
            {
                pos[kv.Value[i]] = i;
            }
        }

        layer.Sort((a, b) =>
        {
            var cmp = MedianNeighbor(a, neighborMap, pos).CompareTo(MedianNeighbor(b, neighborMap, pos));
            return cmp != 0 ? cmp : a.CompareTo(b);
        });
    }

    private static float MedianNeighbor(
        Guid u,
        Dictionary<Guid, List<Guid>> neighborMap,
        Dictionary<Guid, int> pos)
    {
        if (!neighborMap.TryGetValue(u, out var ns) || ns.Count == 0)
        {
            return pos.TryGetValue(u, out var p) ? p : 0f;
        }

        var idxs = ns.Where(pos.ContainsKey).Select(n => (float)pos[n]).OrderBy(x => x).ToList();
        return idxs.Count == 0 ? 0f : idxs[idxs.Count / 2];
    }

    private static GroupCandidate[] ProposeFunctionalBlocks(
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        Dictionary<Guid, Guid> membership,
        List<string> warnings)
    {
        var ungrouped = nodes.Where(n => !n.Frozen && !membership.ContainsKey(n.Id)).Select(n => n.Id).ToHashSet();
        if (ungrouped.Count < MinCandidateSize)
        {
            return Array.Empty<GroupCandidate>();
        }

        var undirected = ungrouped.ToDictionary(id => id, _ => new List<Guid>());
        foreach (var e in edges)
        {
            if (!ungrouped.Contains(e.From) || !ungrouped.Contains(e.To))
            {
                continue;
            }

            undirected[e.From].Add(e.To);
            undirected[e.To].Add(e.From);
        }

        var seen = new HashSet<Guid>();
        var islands = new List<List<Guid>>();
        foreach (var start in ungrouped.OrderBy(id => id))
        {
            if (!seen.Add(start))
            {
                continue;
            }

            var stack = new Stack<Guid>();
            stack.Push(start);
            var island = new List<Guid>();
            while (stack.Count > 0)
            {
                var u = stack.Pop();
                island.Add(u);
                foreach (var v in undirected[u])
                {
                    if (seen.Add(v))
                    {
                        stack.Push(v);
                    }
                }
            }

            islands.Add(island);
        }

        var rankHint = LongestPathRanks(ungrouped, edges);
        var candidates = new List<GroupCandidate>();
        var blockIndex = 1;
        foreach (var island in islands.OrderByDescending(i => i.Count))
        {
            foreach (var chunk in SplitIsland(island, rankHint))
            {
                if (chunk.Count < MinScribbleSize)
                {
                    continue;
                }

                candidates.Add(new GroupCandidate($"Block {blockIndex++}", chunk.ToArray()));
            }
        }

        if (candidates.Count == 0 && ungrouped.Count >= MinScribbleSize)
        {
            warnings.Add("proposeGroups found no sized blocks; try naming manually.");
        }

        return candidates.ToArray();
    }

    private static Dictionary<Guid, int> LongestPathRanks(
        HashSet<Guid> nodes,
        IReadOnlyList<LayoutEdge> edges)
    {
        return LongestPathRanks(nodes, edges.Select(e => (e.From, e.To)));
    }

    private static Dictionary<Guid, int> LongestPathRanks(
        HashSet<Guid> nodes,
        IEnumerable<(Guid From, Guid To)> edges)
    {
        var adjOut = nodes.ToDictionary(n => n, _ => new List<Guid>());
        var indeg = nodes.ToDictionary(n => n, _ => 0);
        foreach (var (from, to) in edges)
        {
            if (!nodes.Contains(from) || !nodes.Contains(to) || from == to)
            {
                continue;
            }

            adjOut[from].Add(to);
            indeg[to]++;
        }

        var q = new Queue<Guid>(nodes.Where(n => indeg[n] == 0));
        var rank = nodes.ToDictionary(n => n, _ => 0);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            foreach (var v in adjOut[u])
            {
                rank[v] = Math.Max(rank[v], rank[u] + 1);
                if (--indeg[v] == 0)
                {
                    q.Enqueue(v);
                }
            }
        }

        return rank;
    }

    private static IEnumerable<List<Guid>> SplitIsland(List<Guid> island, Dictionary<Guid, int> rankHint)
    {
        if (island.Count <= MaxCandidateSize)
        {
            yield return island;
            yield break;
        }

        var byRank = island
            .GroupBy(id => rankHint.TryGetValue(id, out var r) ? r : 0)
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())
            .ToList();

        var bucket = new List<Guid>();
        foreach (var band in byRank)
        {
            foreach (var id in band)
            {
                bucket.Add(id);
                if (bucket.Count >= MaxCandidateSize)
                {
                    yield return bucket;
                    bucket = new List<Guid>();
                }
            }

            if (bucket.Count >= MinCandidateSize && bucket.Count >= MaxCandidateSize / 2)
            {
                yield return bucket;
                bucket = new List<Guid>();
            }
        }

        if (bucket.Count >= MinCandidateSize)
        {
            yield return bucket;
        }
        else if (bucket.Count > 0)
        {
            yield return bucket;
        }
    }
}

public sealed record LayoutNode(
    Guid Id,
    float PivotX,
    float PivotY,
    float Width,
    float Height,
    bool Frozen = false);

public sealed record LayoutEdge(Guid From, Guid To);

public sealed record LayoutMembership(
    Guid GroupId,
    IReadOnlyList<Guid> MemberIds,
    string? Name = null,
    string? ColourCss = null);

public sealed record NamedGroupSpec(string Name, IReadOnlyList<Guid> ComponentIds, string? Color = null);

public sealed record LayoutMove(Guid Id, float FromX, float FromY, float ToX, float ToY);

public sealed record SupernodeInfo(Guid GroupId, IReadOnlyList<Guid> MemberIds);

public sealed record GroupCandidate(string ProvisionalName, IReadOnlyList<Guid> ComponentIds);

public sealed record ScribblePlan(string Name, string Text, float X, float Y);

/// <summary>Hide wires into <see cref="To"/> when laid-out length exceeds the threshold.</summary>
public sealed record WireHideHint(Guid From, Guid To, float Length);

public sealed record LayoutPlan(
    IReadOnlyList<LayoutMove> Moves,
    IReadOnlyList<SupernodeInfo> Supernodes,
    IReadOnlyList<GroupCandidate> GroupCandidates,
    IReadOnlyList<ScribblePlan> Scribbles,
    IReadOnlyList<WireHideHint> WireHides,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<Guid, int>? StageByNode = null);
