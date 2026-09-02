using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cassis.Models;
using Cassis.Utilities;

namespace Cassis.Services;

/// <summary>
/// Service for interacting with Grasshopper components in the MVP implementation.
/// Provides essential component operations without advanced features like caching.
/// </summary>
public interface IGrasshopperComponentService
{
    /// <summary>
    /// Adds a component to the Grasshopper canvas.
    /// </summary>
    Task<ComponentCreationResult> AddComponentAsync(string type, double x, double y,
        float padding = CanvasPlacement.DefaultPadding, bool avoidOverlap = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a value on a component parameter.
    /// </summary>
    Task<ComponentValueResult> SetComponentValueAsync(string componentId, string parameterName, object value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets information about a component.
    /// </summary>
    Task<ComponentInfoResult> GetComponentInfoAsync(string componentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects two components together.
    /// </summary>
    Task<ComponentConnectionResult> ConnectComponentsAsync(
        string sourceId, string sourceParam,
        string targetId, string targetParam, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets script source code on a script component (e.g., Python or C#).
    /// Language is used as a hint for components that support multiple languages.
    /// </summary>
    Task<ComponentValueResult> SetComponentScriptAsync(
        string componentId,
        string language,
        string script,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a Python script component to the Grasshopper canvas with the specified script content.
    /// </summary>
    Task<ComponentCreationResult> AddPythonScriptComponentAsync(string script, double x, double y,
        float padding = CanvasPlacement.DefaultPadding, bool avoidOverlap = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a C# script component to the Grasshopper canvas with the specified script content.
    /// </summary>
    Task<ComponentCreationResult> AddCSharpScriptComponentAsync(string script, double x, double y,
        float padding = CanvasPlacement.DefaultPadding, bool avoidOverlap = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all available component types that can be added to the canvas.
    /// </summary>
    Task<AvailableComponentsResult> ListAvailableComponentsAsync(
        string? category = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the current state of the Grasshopper canvas including components, connections, and layout.
    /// </summary>
    Task<CanvasStateResult> CaptureCanvasStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies the parameter names of a script component to provide more descriptive interfaces.
    /// </summary>
    Task<ComponentValueResult> ModifyComponentParametersAsync(string componentId, Dictionary<string, string> newNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Modifies the parameter names and types of a script component (Python, C#, etc.).
    /// </summary>
    Task<ComponentModificationResult> ModifyScriptComponentParametersAsync(string componentId, Dictionary<string, string> parameterConfig,
        CancellationToken cancellationToken = default);
}