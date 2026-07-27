using Grasshopper.Kernel;

namespace Cassis.Utilities;

/// <summary>
/// Reads and writes Grasshopper solver enable and viewport preview flags.
/// Enable/disable maps to <see cref="IGH_ActiveObject.Locked"/> (inverted: enabled = not locked).
/// Preview maps to <see cref="IGH_PreviewObject.Hidden"/> (inverted: preview on = not hidden).
/// </summary>
internal static class ComponentStateHelper
{
    internal static bool SupportsEnable(IGH_DocumentObject obj) => obj is IGH_ActiveObject;

    internal static bool SupportsPreview(IGH_DocumentObject obj) => obj is IGH_PreviewObject;

    /// <summary>True when the object participates in solutions (IGH_ActiveObject.Locked is false).</summary>
    internal static bool GetEnabled(IGH_DocumentObject obj) =>
        obj is IGH_ActiveObject active && !active.Locked;

    /// <summary>True when viewport preview is on (IGH_PreviewObject.Hidden is false).</summary>
    internal static bool GetPreviewEnabled(IGH_DocumentObject obj) =>
        obj is IGH_PreviewObject preview && !preview.Hidden;

    internal static void SetEnabled(IGH_DocumentObject obj, bool enabled)
    {
        if (obj is not IGH_ActiveObject active)
        {
            throw new InvalidOperationException(
                $"Object '{obj.NickName}' ({obj.GetType().Name}) does not support enable/disable.");
        }

        active.Locked = !enabled;
        if (enabled)
        {
            active.ExpireSolution(true);
        }
        else
        {
            active.ClearData();
        }
    }

    internal static void SetPreviewEnabled(IGH_DocumentObject obj, bool previewEnabled)
    {
        if (obj is not IGH_PreviewObject preview)
        {
            throw new InvalidOperationException(
                $"Object '{obj.NickName}' ({obj.GetType().Name}) does not support preview.");
        }

        preview.Hidden = !previewEnabled;
        obj.ExpirePreview(true);
    }
}
