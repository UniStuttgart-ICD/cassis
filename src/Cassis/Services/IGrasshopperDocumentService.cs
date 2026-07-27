using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cassis.Models;

namespace Cassis.Services;

/// <summary>
/// Service for interacting with Grasshopper documents in the MVP implementation.
/// Provides essential document operations without complex change tracking.
/// </summary>
public interface IGrasshopperDocumentService
{
    /// <summary>
    /// Gets information about the current Grasshopper document.
    /// </summary>
    Task<DocumentInfoResult> GetDocumentInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the current document while preserving essential MCP components.
    /// </summary>
    Task<DocumentClearResult> ClearDocumentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes specific components by their IDs instead of clearing the entire document.
    /// </summary>
    Task<DocumentClearResult> RemoveComponentsAsync(string[] componentIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the current document.
    /// </summary>
    Task<DocumentSaveResult> SaveDocumentAsync(string? filePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a .gh / .ghx file into the Grasshopper canvas.
    /// </summary>
    Task<DocumentLoadResult> LoadDocumentAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the active Grasshopper document (optionally saving first).
    /// </summary>
    Task<DocumentLoadResult> CloseDocumentAsync(bool saveFirst = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new empty Grasshopper document on the canvas.
    /// </summary>
    Task<DocumentCreationResult> NewDocumentAsync(CancellationToken cancellationToken = default);
}