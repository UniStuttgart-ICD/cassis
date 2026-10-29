using System;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Geometry;

namespace Cassis.Utilities;

/// <summary>
/// Selects Grasshopper objects and frames their preview geometry in Rhino viewports.
/// Mirrors Grasshopper's own "Zoom on preview": union of IGH_PreviewObject.ClippingBox, then ZoomBoundingBox.
/// </summary>
internal static class ComponentFocusHelper
{
    internal const double DefaultPadding = 0.1;

    internal sealed class FocusSet
    {
        public List<IGH_DocumentObject> Found { get; } = new();

        public List<string> NotFound { get; } = new();

        public List<string> NoPreview { get; } = new();

        public BoundingBox Bounds { get; set; } = BoundingBox.Empty;
    }

    /// <summary>Resolves GUIDs and unions their preview bounds. Throws when nothing is found or nothing has preview.</summary>
    internal static FocusSet Resolve(GH_Document doc, IEnumerable<string> componentIds)
    {
        var set = new FocusSet();
        foreach (var id in componentIds)
        {
            var obj = Guid.TryParse(id, out var guid) ? doc.FindObject(guid, false) : null;
            if (obj == null)
            {
                set.NotFound.Add(id);
                continue;
            }

            set.Found.Add(obj);
            var box = GetBounds(obj);
            if (!box.IsValid)
            {
                set.NoPreview.Add(id);
                continue;
            }

            set.Bounds = set.Bounds.IsValid ? BoundingBox.Union(set.Bounds, box) : box;
        }

        if (set.Found.Count == 0)
        {
            throw new ArgumentException($"No Grasshopper objects found for: {string.Join(", ", set.NotFound)}");
        }

        if (!set.Bounds.IsValid)
        {
            throw new InvalidOperationException(
                $"No preview geometry for: {string.Join(", ", set.NoPreview)}. Enable preview or solve the definition.");
        }

        return set;
    }

    /// <summary>Replaces the canvas selection; Grasshopper draws selected objects' preview in the selection colour.</summary>
    internal static void Select(GH_Document doc, IEnumerable<IGH_DocumentObject> objects)
    {
        doc.DeselectAll();
        foreach (var obj in objects)
        {
            obj.Attributes.Selected = true;
        }

        // Only a "selected only" preview filter caches the draw list; ExpirePreview also marks the doc modified.
        if (doc.PreviewFilter == GH_PreviewFilter.Selected)
        {
            doc.ExpirePreview(true);
        }

        Instances.RedrawCanvas();
    }

    /// <summary>Zooms each view to the padded bounds and redraws it.</summary>
    internal static void Zoom(IEnumerable<RhinoView> views, BoundingBox bounds, double padding)
    {
        var box = PadBounds(bounds, padding);
        foreach (var view in views)
        {
            view.ActiveViewport.ZoomBoundingBox(box);
            view.Redraw();
        }
    }

    /// <summary>Grows the box so the framed geometry gets a margin; must also handle zero-size boxes (a single point).</summary>
    internal static BoundingBox PadBounds(BoundingBox box, double padding)
    {
        var size = box.Diagonal.Length;

        // A point has no size: give it a 1-unit margin so ZoomBoundingBox gets a real box.
        var padded = box;
        padded.Inflate(size > 0 ? size * padding : 1.0);
        return padded;
    }

    private static BoundingBox GetBounds(IGH_DocumentObject obj)
    {
        if (obj is IGH_PreviewObject preview && preview.ClippingBox.IsValid)
        {
            return preview.ClippingBox;
        }

        return ComponentPreviewBoundsHelper.GetPreviewBounds(obj);
    }
}
