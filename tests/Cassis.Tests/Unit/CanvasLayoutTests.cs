using System;
using System.Collections.Generic;
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
    public void LocalGhx_LCRL_Steel_LayoutRunsWithoutThrow()
    {
        // Local Downloads / CASSIS_LAYOUT_GHX only — never commit the .ghx.
        var path = GhxLayoutGraph.ResolveFixturePath();
        if (path == null)
        {
            Assert.Ignore("Set CASSIS_LAYOUT_GHX or place LCRL_Development_Steel_SpeckleV3.ghx in Downloads.");
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

        // Optional HTML diagnose dump (set CASSIS_LAYOUT_PREVIEW=1). Does not modify the .ghx.
        if (string.Equals(Environment.GetEnvironmentVariable("CASSIS_LAYOUT_PREVIEW"), "1", StringComparison.Ordinal))
        {
            WriteLayoutPreviewHtml(path!, nodes, edges, plan);
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
        // Compact keeps X; Y may grow when resolving stacked overlaps — reflow wraps instead.
        Assert.That((newMaxX - newMinX) / oldW, Is.LessThan(1.5f));
        Assert.That((newMaxY - newMinY) / oldH, Is.LessThan(3f));
        Assert.That(plan.GroupCandidates.All(c => c.ComponentIds.Count <= CanvasLayout.MaxCandidateSize), Is.True);
    }

    private static void WriteLayoutPreviewHtml(
        string ghxPath,
        IReadOnlyList<LayoutNode> nodes,
        IReadOnlyList<LayoutEdge> edges,
        LayoutPlan plan)
    {
        var byId = nodes.ToDictionary(n => n.Id);
        var to = plan.Moves.ToDictionary(m => m.Id, m => (m.ToX, m.ToY));
        float BeforeX(Guid id) => byId[id].PivotX;
        float BeforeY(Guid id) => byId[id].PivotY;
        float AfterX(Guid id) => to.TryGetValue(id, out var t) ? t.ToX : byId[id].PivotX;
        float AfterY(Guid id) => to.TryGetValue(id, out var t) ? t.ToY : byId[id].PivotY;

        string Panel(string title, Func<Guid, float> fx, Func<Guid, float> fy, float x0, out float w, out float h)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var n in nodes)
            {
                var x = fx(n.Id);
                var y = fy(n.Id);
                minX = Math.Min(minX, x - n.Width * 0.5f);
                minY = Math.Min(minY, y - n.Height * 0.5f);
                maxX = Math.Max(maxX, x + n.Width * 0.5f);
                maxY = Math.Max(maxY, y + n.Height * 0.5f);
            }

            const float scale = 0.12f;
            w = (maxX - minX) * scale + 40f;
            h = (maxY - minY) * scale + 60f;
            var sb = new System.Text.StringBuilder();
            sb.Append($"<g transform=\"translate({x0},{0})\">");
            sb.Append($"<text x=\"10\" y=\"24\" font-family=\"Consolas,monospace\" font-size=\"18\" font-weight=\"bold\">{title}</text>");
            sb.Append($"<text x=\"10\" y=\"44\" font-family=\"Consolas,monospace\" font-size=\"12\" fill=\"#666\">{nodes.Count} objs · {edges.Count} edges · growth {(maxX - minX):0}/{(maxY - minY):0}</text>");
            float MapX(float x) => 20 + (x - minX) * scale;
            float MapY(float y) => 55 + (y - minY) * scale;
            foreach (var e in edges)
            {
                if (!byId.ContainsKey(e.From) || !byId.ContainsKey(e.To))
                {
                    continue;
                }

                // Skip edge drawing when huge — keeps preview generation snappy.
                if (edges.Count <= 2500)
                {
                    sb.Append($"<line x1=\"{MapX(fx(e.From)):0.1}\" y1=\"{MapY(fy(e.From)):0.1}\" x2=\"{MapX(fx(e.To)):0.1}\" y2=\"{MapY(fy(e.To)):0.1}\" stroke=\"#bbb\" stroke-width=\"0.4\"/>");
                }
            }

            foreach (var n in nodes)
            {
                var fill = n.Frozen ? "#cde" : "#fc9";
                var rw = Math.Max(2f, n.Width * scale);
                var rh = Math.Max(2f, n.Height * scale);
                sb.Append($"<rect x=\"{MapX(fx(n.Id)) - rw * 0.5f:0.1}\" y=\"{MapY(fy(n.Id)) - rh * 0.5f:0.1}\" width=\"{rw:0.1}\" height=\"{rh:0.1}\" fill=\"{fill}\" stroke=\"#333\" stroke-width=\"0.3\"/>");
            }

            sb.Append("</g>");
            return sb.ToString();
        }

        var s1 = Panel("BEFORE", BeforeX, BeforeY, 0, out var w1, out var h1);
        var s2 = Panel("AFTER compact (solver — .ghx untouched)", AfterX, AfterY, w1 + 20, out var w2, out var h2);
        var html = $@"<!DOCTYPE html>
<html><head><meta charset=utf-8><title>LCRL layout preview</title>
<style>body{{margin:0;background:#f6f6f4;font-family:Consolas,monospace}} .note{{padding:12px 16px;color:#333}}</style>
</head><body>
<div class=note>From CanvasLayout.Compute (compact). Source: {System.IO.Path.GetFileName(ghxPath)}. Warnings: {string.Join(" | ", plan.Warnings)}</div>
<svg xmlns=""http://www.w3.org/2000/svg"" width=""{w1 + w2 + 40:0}"" height=""{Math.Max(h1, h2) + 20:0}"" style=""background:#fff"">
{s1}{s2}
</svg></body></html>";
        var outPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "LCRL_layout_preview.html");
        System.IO.File.WriteAllText(outPath, html);
        TestContext.Out.WriteLine(outPath);
    }
}
