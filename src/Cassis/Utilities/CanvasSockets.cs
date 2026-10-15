using System;
using System.Collections.Generic;
using Grasshopper.Kernel;

namespace Cassis.Utilities;

/// <summary>
/// Uniform socket access for components and standalone parameters (sliders, panels, floating params).
/// A standalone parameter is its own single input and output socket.
/// </summary>
internal static class CanvasSockets
{
    /// <summary>Finds a top-level component or standalone parameter by GUID string.</summary>
    public static IGH_DocumentObject? Find(GH_Document document, string id) =>
        Guid.TryParse(id, out var guid) && document.FindObject(guid, true) is { } obj && (obj is IGH_Component || obj is IGH_Param)
            ? obj
            : null;

    public static IList<IGH_Param> Inputs(IGH_DocumentObject obj) => obj switch
    {
        IGH_Component component => component.Params.Input,
        IGH_Param param => new[] { param },
        _ => Array.Empty<IGH_Param>()
    };

    public static IList<IGH_Param> Outputs(IGH_DocumentObject obj) => obj switch
    {
        IGH_Component component => component.Params.Output,
        IGH_Param param => new[] { param },
        _ => Array.Empty<IGH_Param>()
    };
}
