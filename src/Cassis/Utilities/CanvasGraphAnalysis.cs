using System;
using System.Collections.Generic;
using System.Linq;

namespace Cassis.Utilities;

/// <summary>Component role on the canvas analysis graph.</summary>
public enum LayoutNodeKind
{
    Compute = 0,
    Panel = 1,
    Slider = 2,
    Chrome = 3,
    Other = 4,
}

/// <summary>Wire role — display/preview edges are down-weighted for communities.</summary>
public enum LayoutEdgeRole
{
    Data = 0,
    Display = 1,
}

/// <summary>One Leiden community (functional module candidate).</summary>
public sealed record GraphModule(
    int Id,
    IReadOnlyList<Guid> MemberIds,
    string Method);

/// <summary>Agent-facing snapshot of the enriched canvas graph.</summary>
public sealed record CanvasGraphSnapshot(
    string Method,
    int NodeCount,
    int DataEdgeCount,
    int DisplayEdgeCount,
    IReadOnlyDictionary<string, int> KindCounts,
    IReadOnlyList<GraphModule> Modules);

/// <summary>
/// Enrich the Sources graph: typed nodes/edges + Leiden communities (GraphRAG-style).
/// Pure — no Grasshopper types.
/// </summary>
public static class CanvasGraphAnalysis
{
    public const float DataEdgeWeight = 1f;
    public const float DisplayEdgeWeight = 0.15f;
    public const float SpatialEdgeWeight = 0.35f;
    public const float SpatialRadius = 420f;
    public const float DefaultResolution = 1f;

    public static CanvasGraphSnapshot Analyze(
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        IReadOnlyDictionary<Guid, Guid>? membership = null,
        int maxModuleSize = CanvasLayout.MaxCandidateSize,
        float resolution = DefaultResolution)
    {
        membership ??= new Dictionary<Guid, Guid>();
        var kindCounts = nodes
            .GroupBy(n => n.Kind.ToString().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        var dataEdges = edges.Count(e => e.Role == LayoutEdgeRole.Data);
        var displayEdges = edges.Count(e => e.Role == LayoutEdgeRole.Display);

        var clusterable = nodes
            .Where(n => !n.Frozen
                        && n.Kind != LayoutNodeKind.Chrome
                        && !membership.ContainsKey(n.Id))
            .Select(n => n.Id)
            .ToHashSet();

        var byId = nodes.ToDictionary(n => n.Id);
        var weighted = BuildWeightedProjection(clusterable, edges, byId);
        var communities = LeidenModularity.Cluster(
            clusterable.OrderBy(id => id).ToList(),
            weighted,
            resolution,
            maxModuleSize,
            seed: 42);

        var modules = communities
            .GroupBy(kv => kv.Value)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select((g, i) => new GraphModule(
                i,
                g.Select(kv => kv.Key).OrderBy(id => id).ToArray(),
                "leiden-modularity"))
            .Where(m => m.MemberIds.Count >= CanvasLayout.MinCandidateSize)
            .ToArray();

        return new CanvasGraphSnapshot(
            "leiden-modularity",
            nodes.Count,
            dataEdges,
            displayEdges,
            kindCounts,
            modules);
    }

    /// <summary>
    /// Undirected weighted projection: data edges strong, display weak,
    /// plus spatial bridges so GH neighborhoods cohere without wires.
    /// </summary>
    public static List<(Guid A, Guid B, float W)> BuildWeightedProjection(
        HashSet<Guid> nodes,
        IReadOnlyList<LayoutEdge> edges,
        Dictionary<Guid, LayoutNode> byId)
    {
        var weights = new Dictionary<(Guid, Guid), float>();

        void Add(Guid a, Guid b, float w)
        {
            if (a == b || !nodes.Contains(a) || !nodes.Contains(b))
            {
                return;
            }

            if (a.CompareTo(b) > 0)
            {
                (a, b) = (b, a);
            }

            var key = (a, b);
            weights[key] = weights.TryGetValue(key, out var cur) ? Math.Max(cur, w) : w;
        }

        foreach (var e in edges)
        {
            var w = e.Role == LayoutEdgeRole.Display ? DisplayEdgeWeight : DataEdgeWeight;
            Add(e.From, e.To, w);
        }

        // Spatial bridges via grid (O(n · k), not O(n²)) — proximity ≈ intent on GH canvases.
        AddSpatialBridges(nodes, byId, Add);

        return weights.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }

    /// <summary>
    /// Link nodes in the same / neighboring spatial cells within <see cref="SpatialRadius"/>.
    /// </summary>
    internal static void AddSpatialBridges(
        HashSet<Guid> nodes,
        Dictionary<Guid, LayoutNode> byId,
        Action<Guid, Guid, float> add)
    {
        var cell = Math.Max(1f, SpatialRadius);
        var buckets = new Dictionary<(int Cx, int Cy), List<Guid>>();
        foreach (var id in nodes)
        {
            if (!byId.TryGetValue(id, out var n))
            {
                continue;
            }

            var key = ((int)Math.Floor(n.PivotX / cell), (int)Math.Floor(n.PivotY / cell));
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<Guid>();
                buckets[key] = list;
            }

            list.Add(id);
        }

        foreach (var kv in buckets)
        {
            var (cx, cy) = kv.Key;
            var home = kv.Value;
            for (var ox = -1; ox <= 1; ox++)
            {
                for (var oy = -1; oy <= 1; oy++)
                {
                    if (!buckets.TryGetValue((cx + ox, cy + oy), out var other))
                    {
                        continue;
                    }

                    foreach (var aId in home)
                    {
                        var a = byId[aId];
                        foreach (var bId in other)
                        {
                            if (aId.CompareTo(bId) >= 0)
                            {
                                continue;
                            }

                            var b = byId[bId];
                            var dx = a.PivotX - b.PivotX;
                            var dy = a.PivotY - b.PivotY;
                            var dist = Math.Sqrt(dx * dx + dy * dy);
                            if (dist <= SpatialRadius && dist > 1e-3)
                            {
                                var falloff = (float)(1.0 - dist / SpatialRadius);
                                add(aId, bId, SpatialEdgeWeight * falloff);
                            }
                        }
                    }
                }
            }
        }
    }
}

/// <summary>
/// Leiden-style modularity clustering: Louvain local move + connectedness refinement
/// + aggregation, with recursive split when communities exceed maxSize (GraphRAG pattern).
/// Deterministic with a fixed seed.
/// </summary>
public static class LeidenModularity
{
    public static Dictionary<Guid, int> Cluster(
        IReadOnlyList<Guid> nodes,
        IReadOnlyList<(Guid A, Guid B, float W)> edges,
        float resolution,
        int maxSize,
        int seed)
    {
        if (nodes.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        if (nodes.Count == 1)
        {
            return new Dictionary<Guid, int> { [nodes[0]] = 0 };
        }

        var rng = new Random(seed);
        var partition = RunLeiden(nodes, edges, resolution, rng);

        // GraphRAG: recursively subdivide oversized leaf communities.
        var nextId = partition.Values.DefaultIfEmpty(-1).Max() + 1;
        foreach (var group in partition.GroupBy(kv => kv.Value).ToList())
        {
            var members = group.Select(kv => kv.Key).ToList();
            if (members.Count <= maxSize)
            {
                continue;
            }

            var subEdges = edges
                .Where(e => partition.ContainsKey(e.A) && partition.ContainsKey(e.B)
                            && members.Contains(e.A) && members.Contains(e.B))
                .ToList();
            var sub = Cluster(members, subEdges, resolution * 1.35f, maxSize, seed ^ members.Count * 397);
            var remap = new Dictionary<int, int>();
            foreach (var kv in sub)
            {
                if (!remap.TryGetValue(kv.Value, out var nid))
                {
                    nid = nextId++;
                    remap[kv.Value] = nid;
                }

                partition[kv.Key] = nid;
            }
        }

        return Relabel(partition);
    }

    private static Dictionary<Guid, int> RunLeiden(
        IReadOnlyList<Guid> nodes,
        IReadOnlyList<(Guid A, Guid B, float W)> edges,
        float resolution,
        Random rng)
    {
        var nodeSet = nodes.ToHashSet();
        var adj = nodeSet.ToDictionary(n => n, _ => new List<(Guid N, float W)>());
        float totalWeight = 0f;
        foreach (var (a, b, w) in edges)
        {
            if (!nodeSet.Contains(a) || !nodeSet.Contains(b) || a == b || w <= 0f)
            {
                continue;
            }

            adj[a].Add((b, w));
            adj[b].Add((a, w));
            totalWeight += w;
        }

        if (totalWeight <= 0f)
        {
            // Disconnected isolates — each alone, then we'll merge nothing.
            return nodes.Select((n, i) => (n, i)).ToDictionary(t => t.n, t => t.i);
        }

        var community = nodes.ToDictionary(n => n, n => n.GetHashCode());
        // Stable initial ids 0..n-1
        var ordered = nodes.OrderBy(n => n).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            community[ordered[i]] = i;
        }

        var strength = nodeSet.ToDictionary(
            n => n,
            n => adj[n].Sum(x => x.W));

        var improved = true;
        var passes = 0;
        while (improved && passes < 12)
        {
            passes++;
            improved = false;
            var order = ordered.OrderBy(_ => rng.Next()).ToList();
            foreach (var u in order)
            {
                var best = community[u];
                var bestGain = 0f;
                var neighCom = new Dictionary<int, float>();
                foreach (var (v, w) in adj[u])
                {
                    var c = community[v];
                    neighCom[c] = neighCom.TryGetValue(c, out var cur) ? cur + w : w;
                }

                var ku = strength[u];
                foreach (var kv in neighCom)
                {
                    if (kv.Key == community[u])
                    {
                        continue;
                    }

                    // ΔQ ≈ e_ic - resolution * k_u * K_c / 2m  (simplified Louvain gain)
                    var kc = CommunityStrength(community, strength, kv.Key, u);
                    var gain = kv.Value - resolution * ku * kc / (2f * totalWeight);
                    if (gain > bestGain + 1e-8f)
                    {
                        bestGain = gain;
                        best = kv.Key;
                    }
                }

                if (best != community[u])
                {
                    community[u] = best;
                    improved = true;
                }
            }

            // Leiden refinement: split communities that are not connected.
            community = RefineConnected(community, adj);
        }

        return Relabel(community);
    }

    private static float CommunityStrength(
        Dictionary<Guid, int> community,
        Dictionary<Guid, float> strength,
        int c,
        Guid exclude)
    {
        float s = 0f;
        foreach (var kv in community)
        {
            if (kv.Value == c && kv.Key != exclude)
            {
                s += strength[kv.Key];
            }
        }

        return s;
    }

    private static Dictionary<Guid, int> RefineConnected(
        Dictionary<Guid, int> community,
        Dictionary<Guid, List<(Guid N, float W)>> adj)
    {
        var result = new Dictionary<Guid, int>(community);
        var next = community.Values.DefaultIfEmpty(-1).Max() + 1;
        foreach (var group in community.GroupBy(kv => kv.Value).ToList())
        {
            var members = group.Select(kv => kv.Key).ToHashSet();
            if (members.Count <= 1)
            {
                continue;
            }

            var seen = new HashSet<Guid>();
            var first = true;
            var originalId = group.Key;
            foreach (var start in members.OrderBy(id => id))
            {
                if (!seen.Add(start))
                {
                    continue;
                }

                var stack = new Stack<Guid>();
                stack.Push(start);
                var component = new List<Guid>();
                while (stack.Count > 0)
                {
                    var u = stack.Pop();
                    component.Add(u);
                    foreach (var (v, _) in adj[u])
                    {
                        if (members.Contains(v) && seen.Add(v))
                        {
                            stack.Push(v);
                        }
                    }
                }

                var id = first ? originalId : next++;
                first = false;
                foreach (var n in component)
                {
                    result[n] = id;
                }
            }
        }

        return result;
    }

    private static Dictionary<Guid, int> Relabel(Dictionary<Guid, int> partition)
    {
        var map = new Dictionary<int, int>();
        var next = 0;
        var result = new Dictionary<Guid, int>();
        foreach (var id in partition.Keys.OrderBy(k => k))
        {
            var c = partition[id];
            if (!map.TryGetValue(c, out var nid))
            {
                nid = next++;
                map[c] = nid;
            }

            result[id] = nid;
        }

        return result;
    }
}
