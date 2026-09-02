using System.Collections.Generic;
using System.Drawing;
using Cassis.Utilities;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class CanvasPlacementTests
{
    private const float Padding = CanvasPlacement.DefaultPadding;

    [Test]
    public void FindBoundsDelta_ToolStateOverlap_PushesSecondBoxDownWithGap()
    {
        // Two Motus Tool State boxes (131×120) with bounds 46px apart in Y — heavy overlap.
        var occupied = new List<RectangleF> { new(568f, 100f, 131f, 120f) };
        var candidate = new RectangleF(568f, 146f, 131f, 120f);

        var delta = CanvasPlacement.FindBoundsDelta(candidate, occupied, Padding);

        var resolved = Offset(candidate, delta);
        Assert.That(delta, Is.Not.EqualTo(SizeF.Empty));
        Assert.That(Intersects(resolved, Inflate(occupied[0], Padding)), Is.False);

        var stepY = Math.Max(CanvasPlacement.MinStep, candidate.Height + Padding);
        Assert.That(resolved.Y, Is.EqualTo(candidate.Y + stepY));
    }

    [Test]
    public void FindBoundsDelta_MotusMoveSpacing_NoIntersectionAfterNudge()
    {
        // LIN 120×174 vs SET 102×100 around X=737, pivots ~45px apart in Y.
        var lin = new RectangleF(737f, 200f, 120f, 174f);
        var candidate = new RectangleF(737f, 245f, 102f, 100f);

        var delta = CanvasPlacement.FindBoundsDelta(candidate, new[] { lin }, Padding);
        var resolved = Offset(candidate, delta);

        Assert.That(Intersects(resolved, Inflate(lin, Padding)), Is.False);
    }

    [Test]
    public void FindBoundsDelta_ZeroPadding_AllowsEdgeTouching()
    {
        var occupied = new List<RectangleF> { new(0f, 0f, 100f, 100f) };
        var candidate = new RectangleF(0f, 100f, 50f, 50f);

        var delta = CanvasPlacement.FindBoundsDelta(candidate, occupied, padding: 0f);

        Assert.That(delta, Is.EqualTo(SizeF.Empty));
    }

    [Test]
    public void FindBoundsDelta_AvoidOverlapDisabled_ReturnsZeroDelta()
    {
        var occupied = new List<RectangleF> { new(0f, 0f, 100f, 100f) };
        var candidate = new RectangleF(10f, 10f, 50f, 50f);

        var delta = CanvasPlacement.FindBoundsDelta(candidate, occupied, Padding, avoidOverlap: false);

        Assert.That(delta, Is.EqualTo(SizeF.Empty));
    }

    [Test]
    public void FindBoundsDelta_EmptyOccupied_ReturnsZeroDelta()
    {
        var candidate = new RectangleF(100f, 100f, 131f, 120f);

        var delta = CanvasPlacement.FindBoundsDelta(candidate, new List<RectangleF>(), Padding);

        Assert.That(delta, Is.EqualTo(SizeF.Empty));
    }

    [Test]
    public void FindBoundsDelta_SearchCapExhausted_KeepsRequestedPosition()
    {
        // Block every candidate slot in the down-then-right grid with a large obstacle.
        var candidate = new RectangleF(0f, 0f, 40f, 40f);
        var occupied = new List<RectangleF> { new(-500f, -500f, 5000f, 5000f) };

        var delta = CanvasPlacement.FindBoundsDelta(candidate, occupied, Padding);

        Assert.That(delta, Is.EqualTo(SizeF.Empty));
    }

    [Test]
    public void FindBoundsDelta_CenterPivot_TranslatesBySameBoundsOriginDelta()
    {
        // Component with center pivot: bounds (100,100,70,44) → pivot at (135,122).
        var bounds = new RectangleF(100f, 100f, 70f, 44f);
        var pivot = new PointF(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));
        var occupied = new List<RectangleF> { new(100f, 100f, 70f, 44f) };
        var overlappingCandidate = new RectangleF(100f, 100f, 70f, 44f);

        var delta = CanvasPlacement.FindBoundsDelta(overlappingCandidate, occupied, Padding);
        Assert.That(delta, Is.Not.EqualTo(SizeF.Empty));

        var newPivot = new PointF(pivot.X + delta.Width, pivot.Y + delta.Height);
        var newBounds = Offset(bounds, delta);

        Assert.That(newBounds.X - bounds.X, Is.EqualTo(newPivot.X - pivot.X).Within(0.001f));
        Assert.That(newBounds.Y - bounds.Y, Is.EqualTo(newPivot.Y - pivot.Y).Within(0.001f));
    }

    [Test]
    public void ValidatePadding_RejectsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasPlacement.ValidatePadding(-1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasPlacement.ValidatePadding(201f));
        Assert.Throws<ArgumentOutOfRangeException>(() => CanvasPlacement.ValidatePadding(float.NaN));
    }

    private static RectangleF Offset(RectangleF rect, SizeF delta) =>
        new(rect.X + delta.Width, rect.Y + delta.Height, rect.Width, rect.Height);

    private static RectangleF Inflate(RectangleF rect, float padding)
    {
        var copy = rect;
        if (padding > 0f)
        {
            copy.Inflate(padding, padding);
        }

        return copy;
    }

    private static bool Intersects(RectangleF a, RectangleF b) => a.IntersectsWith(b);
}
