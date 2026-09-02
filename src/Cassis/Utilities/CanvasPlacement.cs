using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cassis.Utilities;

/// <summary>
/// Non-overlapping canvas placement using axis-aligned bounds.
/// </summary>
public static class CanvasPlacement
{
    public const float DefaultPadding = 16f;
    public const float MaxPadding = 200f;
    public const float CanvasLimit = 10000f;
    public const int MaxCandidates = 80;
    public const float MinStep = 20f;

    /// <summary>
    /// Validates optional padding for MCP tools.
    /// </summary>
    public static float ValidatePadding(float padding)
    {
        if (float.IsNaN(padding) || float.IsInfinity(padding))
        {
            throw new ArgumentOutOfRangeException(nameof(padding), "Padding must be finite.");
        }

        if (padding < 0f || padding > MaxPadding)
        {
            throw new ArgumentOutOfRangeException(
                nameof(padding),
                padding,
                $"Padding must be between 0 and {MaxPadding}.");
        }

        return padding;
    }

    /// <summary>
    /// Finds a bounds-origin delta that clears occupied rectangles (pure geometry, testable without GH).
    /// </summary>
    public static SizeF FindBoundsDelta(
        RectangleF candidateBounds,
        IReadOnlyList<RectangleF> occupied,
        float padding,
        bool avoidOverlap = true)
    {
        if (!avoidOverlap || occupied.Count == 0)
        {
            return SizeF.Empty;
        }

        if (candidateBounds.Width <= 0f || candidateBounds.Height <= 0f)
        {
            return SizeF.Empty;
        }

        var inflatedOccupied = InflateCopies(occupied, padding);
        if (!IntersectsAny(candidateBounds, inflatedOccupied))
        {
            return SizeF.Empty;
        }

        var stepY = Math.Max(MinStep, candidateBounds.Height + padding);
        var stepX = candidateBounds.Width + padding;
        var start = candidateBounds.Location;
        var candidates = 0;

        // ponytail: O(n * candidates) AABB scan, spatial index if docs get huge
        for (var column = 0; candidates < MaxCandidates; column++)
        {
            for (var row = 0; row < MaxCandidates && candidates < MaxCandidates; row++)
            {
                var testBounds = new RectangleF(
                    start.X + (column * stepX),
                    start.Y + (row * stepY),
                    candidateBounds.Width,
                    candidateBounds.Height);

                candidates++;

                if (!IntersectsAny(testBounds, inflatedOccupied))
                {
                    return new SizeF(
                        testBounds.X - candidateBounds.X,
                        testBounds.Y - candidateBounds.Y);
                }
            }
        }

        return SizeF.Empty;
    }

    /// <summary>
    /// Places an object on the canvas, nudging away from overlaps when enabled.
    /// Object must already be in <paramref name="doc"/>.
    /// </summary>
    public static PlacementResult PlaceOnCanvas(
        GH_Document doc,
        IGH_DocumentObject obj,
        PointF requestedPivot,
        float padding = DefaultPadding,
        bool avoidOverlap = true)
    {
        if (obj.Attributes == null)
        {
            obj.CreateAttributes();
        }

        obj.Attributes!.Pivot = requestedPivot;
        obj.Attributes.ExpireLayout();

        var boundsBefore = obj.Attributes.Bounds;
        var occupied = CollectOccupiedBounds(doc, obj.InstanceGuid);

        var delta = FindBoundsDelta(boundsBefore, occupied, padding, avoidOverlap);
        var nudged = delta != SizeF.Empty;
        string? reason = null;

        if (avoidOverlap && !nudged && occupied.Count > 0 && IntersectsAny(boundsBefore, InflateCopies(occupied, padding)))
        {
            reason = $"Could not find a free slot within {MaxCandidates} placement candidates.";
        }

        if (nudged)
        {
            obj.Attributes.Pivot = new PointF(
                obj.Attributes.Pivot.X + delta.Width,
                obj.Attributes.Pivot.Y + delta.Height);
            obj.Attributes.ExpireLayout();
        }

        var finalPivot = obj.Attributes.Pivot;
        var outOfBounds = Math.Abs(finalPivot.X) > CanvasLimit || Math.Abs(finalPivot.Y) > CanvasLimit;

        return new PlacementResult(
            requestedPivot,
            finalPivot,
            nudged,
            reason,
            outOfBounds);
    }

    private static List<RectangleF> CollectOccupiedBounds(GH_Document doc, Guid excludeId)
    {
        return doc.Objects
            .Where(o => o.InstanceGuid != excludeId)
            .Where(o => o is not GH_Group)
            .Select(o => o.Attributes?.Bounds ?? RectangleF.Empty)
            .Where(b => b.Width > 0f && b.Height > 0f)
            .ToList();
    }

    private static List<RectangleF> InflateCopies(IReadOnlyList<RectangleF> occupied, float padding)
    {
        if (padding <= 0f)
        {
            return occupied.ToList();
        }

        var inflated = new List<RectangleF>(occupied.Count);
        foreach (var rect in occupied)
        {
            var copy = rect;
            copy.Inflate(padding, padding);
            inflated.Add(copy);
        }

        return inflated;
    }

    private static bool IntersectsAny(RectangleF candidate, IReadOnlyList<RectangleF> occupied)
    {
        foreach (var rect in occupied)
        {
            if (candidate.IntersectsWith(rect))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Result of a canvas placement attempt.
/// </summary>
public sealed record PlacementResult(
    PointF RequestedPivot,
    PointF NewPivot,
    bool Nudged,
    string? NudgeReason = null,
    bool OutOfCanvasBounds = false);
