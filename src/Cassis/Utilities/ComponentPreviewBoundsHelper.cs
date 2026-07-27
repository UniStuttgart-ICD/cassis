using System;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace Cassis.Utilities;

/// <summary>
/// Resolves world-space bounds from solved Grasshopper geometry (output volatile data).
/// This matches most default previews; custom DrawViewport* previews may differ.
/// </summary>
internal static class ComponentPreviewBoundsHelper
{
    internal static BoundingBox GetPreviewBounds(IGH_DocumentObject obj)
    {
        var union = BoundingBox.Empty;

        if (obj is IGH_Component component)
        {
            foreach (var param in component.Params.Output)
            {
                UnionVolatileData(param, ref union);
            }
        }
        else if (obj is IGH_Param param)
        {
            UnionVolatileData(param, ref union);
        }

        return union;
    }

    internal static IGH_DocumentObject FindDocumentObject(string componentId)
    {
        var document = Instances.ActiveCanvas?.Document
                       ?? throw new InvalidOperationException("No active Grasshopper document");

        if (!Guid.TryParse(componentId, out var guid))
        {
            throw new ArgumentException("Invalid component GUID format");
        }

        return document.FindObject(guid, false)
               ?? throw new InvalidOperationException($"Object {componentId} not found");
    }

    internal static void EnsurePreviewAndSolution(IGH_DocumentObject obj, bool ensurePreview, bool solve)
    {
        if (ensurePreview && ComponentStateHelper.SupportsPreview(obj) && !ComponentStateHelper.GetPreviewEnabled(obj))
        {
            ComponentStateHelper.SetPreviewEnabled(obj, true);
        }

        if (solve)
        {
            var document = Instances.ActiveCanvas?.Document;
            document?.NewSolution(false);
            obj.ExpirePreview(true);
        }
    }

    private static void UnionVolatileData(IGH_Param param, ref BoundingBox union)
    {
        foreach (var goo in param.VolatileData.AllData(true))
        {
            if (TryGetBounds(goo, out var bounds))
            {
                union = union.IsValid ? BoundingBox.Union(union, bounds) : bounds;
            }
        }
    }

    private static bool TryGetBounds(IGH_Goo? goo, out BoundingBox bounds)
    {
        bounds = BoundingBox.Unset;
        if (goo == null)
        {
            return false;
        }

        switch (goo)
        {
            case GH_Point point:
                bounds = new BoundingBox(point.Value, point.Value);
                return bounds.IsValid;
            case GH_Vector vector:
                bounds = new BoundingBox(Point3d.Origin, Point3d.Origin + vector.Value);
                return bounds.IsValid;
            case GH_Line line:
                bounds = new BoundingBox(line.Value.From, line.Value.To);
                return bounds.IsValid;
            case GH_Curve curve when curve.Value != null:
                bounds = curve.Value.GetBoundingBox(true);
                return bounds.IsValid;
            case GH_Surface surface when surface.Value != null:
                bounds = surface.Value.GetBoundingBox(true);
                return bounds.IsValid;
            case GH_Brep brep when brep.Value != null:
                bounds = brep.Value.GetBoundingBox(true);
                return bounds.IsValid;
            case GH_Mesh mesh when mesh.Value != null:
                bounds = mesh.Value.GetBoundingBox(true);
                return bounds.IsValid;
            case GH_Box box:
                bounds = box.Value.BoundingBox;
                return bounds.IsValid;
            default:
                break;
        }

        var geometry = GH_Convert.ToGeometryBase(goo);
        if (geometry == null)
        {
            return false;
        }

        bounds = geometry.GetBoundingBox(true);
        return bounds.IsValid;
    }
}
