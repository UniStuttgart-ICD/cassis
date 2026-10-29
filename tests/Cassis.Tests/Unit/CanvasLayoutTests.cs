using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cassis.Utilities;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class CanvasLayoutTests
{
    private static Guid G(int n) => new Guid(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static LayoutNode Node(int id, float x, float y, float w = 80f, float h = 40f, bool frozen = false)
        => new(G(id), x, y, w, h, frozen);

    [Test]
    public void LineGraph_IncreasesXAlongFlow()
    {
        var nodes = new[]
        {
            Node(1, 500, 100),
            Node(2, 100, 100),
            Node(3, 300, 120),
        };
        var edges = new[]
        {
            new LayoutEdge(G(2), G(3)),
            new LayoutEdge(G(3), G(1)),
        };

        // Reflow builds L→R ranks; compact keeps existing X (already ordered here).
        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);

        var x2 = plan.Moves.First(m => m.Id == G(2)).ToX;
        var x3 = plan.Moves.First(m => m.Id == G(3)).ToX;
        var x1 = plan.Moves.First(m => m.Id == G(1)).ToX;
        Assert.That(x2, Is.LessThan(x3));
        Assert.That(x3, Is.LessThan(x1));
    }

    [Test]
    public void Diamond_SharesRanksOnParallelBranches()
    {
        // 1 → 2 → 4
        // 1 → 3 → 4
        var nodes = new[]
        {
            Node(1, 0, 0),
            Node(2, 50, -40),
            Node(3, 50, 40),
            Node(4, 100, 0),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(1), G(3)),
            new LayoutEdge(G(2), G(4)),
            new LayoutEdge(G(3), G(4)),
        };

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);
        var xs = plan.Moves.ToDictionary(m => m.Id, m => m.ToX);

        Assert.That(xs[G(2)], Is.EqualTo(xs[G(3)]).Within(1f));
        Assert.That(xs[G(1)], Is.LessThan(xs[G(2)]));
        Assert.That(xs[G(2)], Is.LessThan(xs[G(4)]));
    }

    [Test]
    public void Cycle_EmitsWarningAndFiniteRanks()
    {
        var nodes = new[] { Node(1, 200, 0), Node(2, 0, 0) };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(1)),
        };

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);

        Assert.That(plan.Warnings.Count, Is.GreaterThan(0));
        Assert.That(plan.Moves.All(m =>
            !float.IsNaN(m.ToX) && !float.IsInfinity(m.ToX) &&
            !float.IsNaN(m.ToY) && !float.IsInfinity(m.ToY)), Is.True);
    }

    [Test]
    public void Supernode_PreservesRelativeOffsets()
    {
        var groupId = G(99);
        var nodes = new[]
        {
            Node(1, 0, 0),
            Node(2, 100, 0),
            Node(3, 40, 30),
            Node(4, 60, 50),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(3)),
            new LayoutEdge(G(3), G(2)),
            new LayoutEdge(G(4), G(2)),
        };
        var groups = new[]
        {
            new LayoutMembership(groupId, new[] { G(3), G(4) }),
        };

        var plan = CanvasLayout.Compute(nodes, edges, groups);
        var m3 = plan.Moves.First(m => m.Id == G(3));
        var m4 = plan.Moves.First(m => m.Id == G(4));

        Assert.That(m4.ToX - m3.ToX, Is.EqualTo(20f).Within(0.1f));
        Assert.That(m4.ToY - m3.ToY, Is.EqualTo(20f).Within(0.1f));
    }

    [Test]
    public void Centroid_PreservedForMovableSet()
    {
        var nodes = new[]
        {
            Node(1, 1000, 500),
            Node(2, 1100, 500),
            Node(3, 1200, 500),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(3)),
        };

        var oldCx = nodes.Average(n => n.PivotX);
        var oldCy = nodes.Average(n => n.PivotY);
        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);
        float newCx = 0, newCy = 0;
        foreach (var n in nodes)
        {
            var m = plan.Moves.FirstOrDefault(x => x.Id == n.Id);
            newCx += m?.ToX ?? n.PivotX;
            newCy += m?.ToY ?? n.PivotY;
        }

        Assert.That(newCx / nodes.Length, Is.EqualTo(oldCx).Within(1f));
        Assert.That(newCy / nodes.Length, Is.EqualTo(oldCy).Within(1f));
    }

    [Test]
    public void FrozenFilter_DoesNotMoveOutsideIds()
    {
        var nodes = new[]
        {
            Node(1, 0, 0),
            Node(2, 100, 0),
            Node(3, 200, 0, frozen: true),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(3)),
        };

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);

        Assert.That(plan.Moves.Any(m => m.Id == G(3)), Is.False);
        Assert.That(plan.Moves.Any(m => m.Id == G(1) || m.Id == G(2)), Is.True);
    }

    [Test]
    public void ProposeGroups_SplitsOversizedIsland()
    {
        var nodes = Enumerable.Range(1, 30).Select(i => Node(i, i * 10f, 0)).ToArray();
        var edges = Enumerable.Range(1, 29)
            .Select(i => new LayoutEdge(G(i), G(i + 1)))
            .ToArray();

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            proposeGroups: true);

        Assert.That(plan.GroupCandidates.Count, Is.GreaterThan(1));
        Assert.That(plan.GroupCandidates.All(c => c.ComponentIds.Count <= CanvasLayout.MaxCandidateSize), Is.True);
    }

    [Test]
    public void NamedGroups_EmitScribbleAboveBlock()
    {
        var nodes = new[] { Node(1, 0, 100), Node(2, 100, 100) };
        var edges = new[] { new LayoutEdge(G(1), G(2)) };
        var named = new[] { new NamedGroupSpec("Segments at Ridges", new[] { G(1), G(2) }) };

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            namedGroups: named,
            mode: LayoutMode.Reflow);

        Assert.That(plan.Scribbles.Count, Is.EqualTo(1));
        Assert.That(plan.Scribbles[0].Text, Is.EqualTo("SEGMENTS AT RIDGES"));
        var leafYs = nodes.Select(n =>
        {
            var m = plan.Moves.FirstOrDefault(x => x.Id == n.Id);
            return m?.ToY ?? n.PivotY;
        });
        Assert.That(plan.Scribbles[0].Y, Is.LessThan(leafYs.Min()));
    }

    [Test]
    public void TallLayer_WrapsInsteadOfTower()
    {
        // One source → many sinks (same rank). Without wrap, height ≈ N * (h+gap).
        var nodes = new List<LayoutNode> { Node(1, 0, 0) };
        var edges = new List<LayoutEdge>();
        for (var i = 2; i <= 25; i++)
        {
            nodes.Add(Node(i, 100, (i - 2) * 10f));
            edges.Add(new LayoutEdge(G(1), G(i)));
        }

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            mode: LayoutMode.Reflow);
        float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
        foreach (var node in nodes)
        {
            var move = plan.Moves.FirstOrDefault(m => m.Id == node.Id);
            var x = move?.ToX ?? node.PivotX;
            var y = move?.ToY ?? node.PivotY;
            minY = Math.Min(minY, y - node.Height * 0.5f);
            maxY = Math.Max(maxY, y + node.Height * 0.5f);
            minX = Math.Min(minX, x - node.Width * 0.5f);
            maxX = Math.Max(maxX, x + node.Width * 0.5f);
        }

        var unwrappedH = 24 * (40f + CanvasLayout.DefaultRowGap);
        Assert.That(maxY - minY, Is.LessThan(unwrappedH * 0.55f));
        Assert.That(maxX - minX, Is.GreaterThan(CanvasLayout.DefaultColumnGap));
    }

    [Test]
    public void Compact_BlockChainGoesLeftToRightNotTower()
    {
        // One function-sized chain (≤ MaxCandidateSize) stacked on one X → L→R strip, not a tower.
        var n = CanvasLayout.MaxCandidateSize;
        var nodes = Enumerable.Range(1, n)
            .Select(i => Node(i, 200f, i * 8f))
            .ToArray();
        var edges = Enumerable.Range(1, n - 1)
            .Select(i => new LayoutEdge(G(i), G(i + 1)))
            .ToArray();

        var plan = CanvasLayout.Compute(nodes, edges, Array.Empty<LayoutMembership>());
        float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
        var xs = new Dictionary<Guid, float>();
        foreach (var node in nodes)
        {
            var move = plan.Moves.FirstOrDefault(m => m.Id == node.Id);
            var x = move?.ToX ?? node.PivotX;
            var y = move?.ToY ?? node.PivotY;
            xs[node.Id] = x;
            minY = Math.Min(minY, y - node.Height * 0.5f);
            maxY = Math.Max(maxY, y + node.Height * 0.5f);
            minX = Math.Min(minX, x - node.Width * 0.5f);
            maxX = Math.Max(maxX, x + node.Width * 0.5f);
        }

        Assert.That(xs[G(1)], Is.LessThan(xs[G(n / 2)]));
        Assert.That(xs[G(n / 2)], Is.LessThan(xs[G(n)]));
        Assert.That(maxY - minY, Is.LessThan(400f));
        Assert.That(maxX - minX, Is.GreaterThan(100f));
    }

    [Test]
    public void Compact_AlignsOneToOneChainPins()
    {
        var nodes = new[]
        {
            Node(1, 0, 0),
            Node(2, 100, 80),
            Node(3, 200, -40),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(3)),
        };

        var plan = CanvasLayout.Compute(nodes, edges, Array.Empty<LayoutMembership>());
        float Y(int id)
        {
            var m = plan.Moves.FirstOrDefault(x => x.Id == G(id));
            return m?.ToY ?? nodes.First(n => n.Id == G(id)).PivotY;
        }

        Assert.That(Y(1), Is.EqualTo(Y(2)).Within(1f));
        Assert.That(Y(2), Is.EqualTo(Y(3)).Within(1f));
    }

    [Test]
    public void WireHide_EmitsHintsWhenHopStaysLong()
    {
        // Frozen source far left; movable sink cluster packs in place → long hop remains.
        var nodes = new[]
        {
            Node(1, 0, 0, frozen: true),
            Node(2, 2000, 0),
            Node(3, 2100, 0),
        };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(3)),
        };

        var plan = CanvasLayout.Compute(nodes, edges, Array.Empty<LayoutMembership>());

        Assert.That(plan.WireHides.Any(w => w.From == G(1) && w.To == G(2)), Is.True);
        Assert.That(
            plan.WireHides.First(w => w.From == G(1) && w.To == G(2)).Length,
            Is.GreaterThanOrEqualTo(CanvasLayout.DefaultWireHideLength));
    }

    [Test]
    public void ProposeGroups_SkipsTinyClusters()
    {
        var nodes = new[] { Node(1, 0, 0), Node(2, 100, 0), Node(3, 200, 0) };
        var edges = new[]
        {
            new LayoutEdge(G(1), G(2)),
            new LayoutEdge(G(2), G(3)),
        };

        var plan = CanvasLayout.Compute(
            nodes,
            edges,
            Array.Empty<LayoutMembership>(),
            proposeGroups: true);

        Assert.That(plan.GroupCandidates.Count, Is.EqualTo(0));
    }

    [Test]
    public void OptionalLocalGhx_LayoutRunsWithoutThrow()
    {
        // Optional: set CASSIS_LAYOUT_GHX to a local .ghx. Never commit fixtures.
        var path = GhxLayoutGraph.ResolveFixturePath();
        if (path == null)
        {
            Assert.Ignore("Set CASSIS_LAYOUT_GHX to a local .ghx to run this check.");
        }

        var (nodes, edges, groups) = GhxLayoutGraph.Load(path!);
        Assert.That(nodes.Count, Is.GreaterThan(100));
        Assert.That(edges.Count, Is.GreaterThan(100));
        Assert.That(groups.Count, Is.GreaterThan(10));

        var movable = nodes.Where(n => !n.Frozen).ToList();
        float oldMinX = float.MaxValue, oldMinY = float.MaxValue, oldMaxX = float.MinValue, oldMaxY = float.MinValue;
        foreach (var node in movable)
        {
            oldMinX = Math.Min(oldMinX, node.PivotX - node.Width * 0.5f);
            oldMinY = Math.Min(oldMinY, node.PivotY - node.Height * 0.5f);
            oldMaxX = Math.Max(oldMaxX, node.PivotX + node.Width * 0.5f);
            oldMaxY = Math.Max(oldMaxY, node.PivotY + node.Height * 0.5f);
        }

        var oldW = oldMaxX - oldMinX;
        var oldH = oldMaxY - oldMinY;
        var oldCx = movable.Average(n => n.PivotX);
        var oldCy = movable.Average(n => n.PivotY);

        var plan = CanvasLayout.Compute(nodes, edges, groups, proposeGroups: true);

        // Local diagnose only (CASSIS_LAYOUT_PREVIEW=1). Interactive density scrubber — .ghx untouched.
        if (string.Equals(Environment.GetEnvironmentVariable("CASSIS_LAYOUT_PREVIEW"), "1", StringComparison.Ordinal))
        {
            WriteInteractiveDensityPreview(path!, nodes, edges, groups);
        }

        Assert.That(plan.Moves.Count, Is.GreaterThan(0));
        Assert.That(plan.Moves.All(m =>
            !float.IsNaN(m.ToX) && !float.IsInfinity(m.ToX) &&
            !float.IsNaN(m.ToY) && !float.IsInfinity(m.ToY)), Is.True);

        float newCx = 0, newCy = 0;
        float newMinX = float.MaxValue, newMinY = float.MaxValue, newMaxX = float.MinValue, newMaxY = float.MinValue;
        var n = 0;
        foreach (var node in movable)
        {
            var move = plan.Moves.FirstOrDefault(m => m.Id == node.Id);
            var x = move?.ToX ?? node.PivotX;
            var y = move?.ToY ?? node.PivotY;
            newCx += x;
            newCy += y;
            newMinX = Math.Min(newMinX, x - node.Width * 0.5f);
            newMinY = Math.Min(newMinY, y - node.Height * 0.5f);
            newMaxX = Math.Max(newMaxX, x + node.Width * 0.5f);
            newMaxY = Math.Max(newMaxY, y + node.Height * 0.5f);
            n++;
        }

        Assert.That(n, Is.GreaterThan(0));
        Assert.That(newCx / n, Is.EqualTo(oldCx).Within(5f));
        Assert.That(newCy / n, Is.EqualTo(oldCy).Within(5f));
        // Optional fixture only: block-first can grow a lot on messy defs — warn via HTML, don't fail CI skip path.
        TestContext.Out.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "extent growth W={0:0.00}x H={1:0.00}x",
            (newMaxX - newMinX) / oldW,
            (newMaxY - newMinY) / oldH));
        Assert.That(plan.GroupCandidates.All(c => c.ComponentIds.Count <= CanvasLayout.MaxCandidateSize), Is.True);
    }

    /// <summary>
    /// Interactive density scrubber: precomputes gap stops, HTML sliders interpolate, nodes are draggable.
    /// </summary>
    private static void WriteInteractiveDensityPreview(
        string ghxPath,
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        IReadOnlyList<LayoutMembership> groups)
    {
        var indexOf = nodes.Select((n, i) => (n.Id, i)).ToDictionary(t => t.Id, t => t.i);
        var edgeIdx = edges
            .Where(e => indexOf.ContainsKey(e.From) && indexOf.ContainsKey(e.To))
            .Select(e => new[] { indexOf[e.From], indexOf[e.To] })
            .ToList();

        var gapStops = new (float Col, float Row)[]
        {
            (280f, 40f),
            (200f, 32f),
            (140f, 26f),
            (100f, 20f),
            (70f, 16f),
            (48f, 12f),
        };

        float[] FlatXy(LayoutPlan? plan)
        {
            var xy = new float[nodes.Count * 2];
            Dictionary<Guid, (float X, float Y)>? moves = null;
            if (plan != null)
            {
                moves = plan.Moves.ToDictionary(m => m.Id, m => (m.ToX, m.ToY));
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (moves != null && moves.TryGetValue(n.Id, out var t))
                {
                    xy[i * 2] = t.X;
                    xy[i * 2 + 1] = t.Y;
                }
                else
                {
                    xy[i * 2] = n.PivotX;
                    xy[i * 2 + 1] = n.PivotY;
                }
            }

            return xy;
        }

        string Arr(IEnumerable<float> vals) =>
            "[" + string.Join(",", vals.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture))) + "]";

        var beforeXy = FlatXy(null);
        var stopsJson = new System.Text.StringBuilder("[");
        for (var s = 0; s < gapStops.Length; s++)
        {
            var (col, row) = gapStops[s];
            var packed = CanvasLayout.Compute(nodes, edges, groups, columnGap: col, rowGap: row);
            if (s > 0)
            {
                stopsJson.Append(',');
            }

            stopsJson.Append("{\"col\":")
                .Append(col.ToString("0", CultureInfo.InvariantCulture))
                .Append(",\"row\":")
                .Append(row.ToString("0", CultureInfo.InvariantCulture))
                .Append(",\"xy\":")
                .Append(Arr(FlatXy(packed)))
                .Append('}');
            TestContext.Out.WriteLine($"stop {col:0}/{row:0} moves={packed.Moves.Count}");
        }

        stopsJson.Append(']');

        static string JsStr(string? s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "\"\"";
            }

            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        }

        var groupsJson = new System.Text.StringBuilder("[");
        var gFirst = true;
        foreach (var g in groups)
        {
            var members = g.MemberIds
                .Where(id => indexOf.ContainsKey(id))
                .Select(id => indexOf[id])
                .ToList();
            if (members.Count == 0)
            {
                continue;
            }

            if (!gFirst)
            {
                groupsJson.Append(',');
            }

            gFirst = false;
            groupsJson.Append("{\"n\":").Append(JsStr(g.Name))
                .Append(",\"m\":[").Append(string.Join(",", members)).Append(']');
            if (!string.IsNullOrEmpty(g.ColourCss) && !g.ColourCss.Contains("255,255,255"))
            {
                groupsJson.Append(",\"c\":").Append(JsStr(g.ColourCss));
            }

            groupsJson.Append('}');
        }

        groupsJson.Append(']');

        var dataJson = new System.Text.StringBuilder();
        dataJson.Append("{\"n\":").Append(nodes.Count)
            .Append(",\"w\":").Append(Arr(nodes.Select(n => n.Width)))
            .Append(",\"h\":").Append(Arr(nodes.Select(n => n.Height)))
            .Append(",\"frozen\":[")
            .Append(string.Join(",", nodes.Select(n => n.Frozen ? "1" : "0")))
            .Append("],\"edges\":[")
            .Append(string.Join(",", edgeIdx.Select(p => $"[{p[0]},{p[1]}]")))
            .Append("],\"groups\":").Append(groupsJson)
            .Append(",\"before\":").Append(Arr(beforeXy))
            .Append(",\"stops\":").Append(stopsJson)
            .Append('}');

        var templatePath = new[]
            {
                Path.Combine(TestContext.CurrentContext.TestDirectory, "layout-density.template.html"),
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Unit", "layout-density.template.html"),
                Path.GetFullPath(Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "..", "..", "..", "..", "tests", "Cassis.Tests", "Unit", "layout-density.template.html")),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..", "tests", "Cassis.Tests", "Unit", "layout-density.template.html")),
            }
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("layout-density.template.html not found near test output.");

        var html = File.ReadAllText(templatePath)
            .Replace("__SOURCE__", Path.GetFileName(ghxPath))
            .Replace("__DATA__", dataJson.ToString());

        var outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "LCRL_layout_preview.html");
        File.WriteAllText(outPath, html);
        TestContext.Out.WriteLine(outPath);
    }

}
