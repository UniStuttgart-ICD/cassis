using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Microsoft.Extensions.Logging;
using Cassis.Models;
using Rhino;

namespace Cassis.Services;

/// <summary>
/// Simplified implementation of the Grasshopper document service for MVP.
/// Provides direct document operations without complex change tracking or caching.
/// </summary>
public class GrasshopperDocumentService : IGrasshopperDocumentService
{
    private readonly ILogger<GrasshopperDocumentService> _logger;

    public GrasshopperDocumentService(ILogger<GrasshopperDocumentService> logger)
    {
        _logger = logger;
    }

    public async Task<DocumentInfoResult> GetDocumentInfoAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentInfoResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    var emptyResult = new DocumentInfoResult(
                        null,
                        null,
                        0,
                        new List<DocumentComponentInfo>().AsReadOnly(),
                        false,
                        false);
                    tcs.SetResult(emptyResult);
                    return;
                }

                var components = new List<DocumentComponentInfo>();
                foreach (var obj in doc.Objects)
                {
                    var componentInfo = new DocumentComponentInfo(
                        obj.InstanceGuid.ToString(),
                        obj.GetType().Name,
                        obj.NickName,
                        obj.Category,
                        obj.SubCategory);

                    components.Add(componentInfo);
                }

                var docInfo = new DocumentInfoResult(
                    doc.DisplayName,
                    doc.FilePath,
                    doc.Objects.Count,
                    components.AsReadOnly(),
                    doc.IsModified,
                    doc.Enabled);

                _logger.LogInformation("Retrieved document info for '{DocumentName}'", doc.DisplayName);
                tcs.SetResult(docInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting document info");
                var errorResult = new DocumentInfoResult(
                    null,
                    null,
                    0,
                    new List<DocumentComponentInfo>(),
                    false,
                    false);
                tcs.SetResult(errorResult);
            }
        });

        return await tcs.Task;
    }

    public async Task<DocumentClearResult> ClearDocumentAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentClearResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new DocumentClearResult(
                        false,
                        "No active Grasshopper document",
                        0,
                        0,
                        "No active Grasshopper document"));
                    return;
                }

                // Instead of clearing the entire document, just remove test components
                // This preserves the user's existing work and only removes what we created
                var testComponents = doc.Objects.Where(obj =>
                    // Remove components created during testing
                    (obj.NickName.Contains("Test") || 
                     obj.NickName.Contains("Slider") ||
                     obj.NickName.Contains("Panel") ||
                     obj.NickName.Contains("Py3") ||
                     obj.NickName.Contains("C#") ||
                     obj.NickName.Contains("Group")) &&
                    // But preserve essential MCP components
                    !obj.NickName.Contains("MCP") &&
                    !obj.NickName.Contains("Claude") &&
                    !obj.GetType().Name.Contains("GH_MCP") &&
                    !obj.Description.Contains("Machine Control Protocol")
                ).ToList();

                if (testComponents.Count > 0)
                {
                    doc.RemoveObjects(testComponents, false);
                    doc.NewSolution(false);
                }

                var result = new DocumentClearResult(
                    true,
                    $"Removed {testComponents.Count} test components, preserved essential components",
                    testComponents.Count,
                    doc.Objects.Count - testComponents.Count);

                _logger.LogInformation("Cleared test components, removed {RemovedCount} objects, preserved {PreservedCount}",
                    testComponents.Count, doc.Objects.Count - testComponents.Count);

                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing test components");
                tcs.SetResult(new DocumentClearResult(
                    false,
                    "Error clearing test components",
                    0,
                    0,
                    ex.Message));
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Removes specific components by their IDs instead of clearing the entire document
    /// </summary>
    public async Task<DocumentClearResult> RemoveComponentsAsync(string[] componentIds, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentClearResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var doc = Instances.ActiveCanvas?.Document;
                if (doc == null)
                {
                    tcs.SetResult(new DocumentClearResult(
                        false,
                        "No active Grasshopper document",
                        0,
                        0,
                        "No active Grasshopper document"));
                    return;
                }

                var componentsToRemove = new List<IGH_DocumentObject>();
                var invalidIds = new List<string>();

                // Find all components by their IDs
                foreach (var componentId in componentIds)
                {
                    if (Guid.TryParse(componentId, out Guid guid))
                    {
                        var component = doc.FindObject(guid, false);
                        if (component != null)
                        {
                            componentsToRemove.Add(component);
                        }
                        else
                        {
                            invalidIds.Add(componentId);
                        }
                    }
                    else
                    {
                        invalidIds.Add(componentId);
                    }
                }

                if (componentsToRemove.Count > 0)
                {
                    doc.RemoveObjects(componentsToRemove, false);
                    doc.NewSolution(false);
                }

                var result = new DocumentClearResult(
                    true,
                    $"Removed {componentsToRemove.Count} components, {invalidIds.Count} invalid IDs",
                    componentsToRemove.Count,
                    doc.Objects.Count);

                _logger.LogInformation("Removed {RemovedCount} components, {InvalidCount} invalid IDs",
                    componentsToRemove.Count, invalidIds.Count);

                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing components");
                tcs.SetResult(new DocumentClearResult(
                    false,
                    "Error removing components",
                    0,
                    0,
                    ex.Message));
            }
        });

        return await tcs.Task;
    }

    public async Task<DocumentSaveResult> SaveDocumentAsync(string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentSaveResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var editor = Instances.DocumentEditor;
                if (editor == null)
                {
                    tcs.SetResult(new DocumentSaveResult(
                        false,
                        "Grasshopper editor is not available. Open Grasshopper first.",
                        filePath,
                        "no editor"));
                    return;
                }

                if (!editor.ScriptAccess_IsDocument())
                {
                    tcs.SetResult(new DocumentSaveResult(
                        false,
                        "No Grasshopper document is open.",
                        filePath,
                        "no document"));
                    return;
                }

                bool ok;
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    var ext = Path.GetExtension(filePath).ToLowerInvariant();
                    if (ext is not (".gh" or ".ghx"))
                    {
                        tcs.SetResult(new DocumentSaveResult(
                            false,
                            "Save path must end with .gh or .ghx",
                            filePath,
                            "bad extension"));
                        return;
                    }

                    ok = editor.ScriptAccess_SaveDocumentAs(filePath);
                }
                else
                {
                    ok = editor.ScriptAccess_SaveDocument();
                }

                var doc = Instances.ActiveCanvas?.Document;
                var savedPath = doc?.FilePath ?? filePath;
                tcs.SetResult(ok
                    ? new DocumentSaveResult(true, "Document saved", savedPath)
                    : new DocumentSaveResult(false, "Save failed", savedPath, "ScriptAccess_Save returned false"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving document");
                tcs.SetResult(new DocumentSaveResult(false, "Error saving document", filePath, ex.Message));
            }
        });

        return await tcs.Task;
    }

    public async Task<DocumentLoadResult> LoadDocumentAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentLoadResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    tcs.SetResult(new DocumentLoadResult(false, "File path is required", filePath, "empty path"));
                    return;
                }

                if (!File.Exists(filePath))
                {
                    tcs.SetResult(new DocumentLoadResult(false, $"File not found: {filePath}", filePath, "not found"));
                    return;
                }

                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext is not (".gh" or ".ghx"))
                {
                    tcs.SetResult(new DocumentLoadResult(
                        false,
                        "File must be a Grasshopper definition (.gh or .ghx)",
                        filePath,
                        "bad extension"));
                    return;
                }

                var editor = Instances.DocumentEditor;
                if (editor == null)
                {
                    tcs.SetResult(new DocumentLoadResult(
                        false,
                        "Grasshopper editor is not available. Open Grasshopper first.",
                        filePath,
                        "no editor"));
                    return;
                }

                var ok = editor.ScriptAccess_OpenDocument(filePath);
                if (!ok)
                {
                    tcs.SetResult(new DocumentLoadResult(
                        false,
                        "Failed to open document",
                        filePath,
                        "ScriptAccess_OpenDocument returned false"));
                    return;
                }

                var doc = Instances.ActiveCanvas?.Document;
                var name = doc?.DisplayName ?? Path.GetFileName(filePath);
                var count = doc?.ObjectCount ?? 0;
                tcs.SetResult(new DocumentLoadResult(
                    true,
                    $"Opened '{name}' ({count} objects)",
                    doc?.FilePath ?? filePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading document from {Path}", filePath);
                tcs.SetResult(new DocumentLoadResult(false, "Error loading document", filePath, ex.Message));
            }
        });

        return await tcs.Task;
    }

    public async Task<DocumentLoadResult> CloseDocumentAsync(bool saveFirst = false,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentLoadResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var editor = Instances.DocumentEditor;
                if (editor == null)
                {
                    tcs.SetResult(new DocumentLoadResult(
                        false,
                        "Grasshopper editor is not available.",
                        null,
                        "no editor"));
                    return;
                }

                if (!editor.ScriptAccess_IsDocument())
                {
                    tcs.SetResult(new DocumentLoadResult(false, "No document is open", null, "no document"));
                    return;
                }

                var doc = Instances.ActiveCanvas?.Document;
                var path = doc?.FilePath;
                var name = doc?.DisplayName ?? "Untitled";

                if (saveFirst)
                {
                    if (!editor.ScriptAccess_SaveDocument())
                    {
                        tcs.SetResult(new DocumentLoadResult(
                            false,
                            "Save before close failed; document left open",
                            path,
                            "save failed"));
                        return;
                    }
                }

                var ok = editor.ScriptAccess_CloseDocument();
                tcs.SetResult(ok
                    ? new DocumentLoadResult(true, $"Closed '{name}'", path)
                    : new DocumentLoadResult(false, $"Failed to close '{name}'", path, "CloseDocument returned false"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing document");
                tcs.SetResult(new DocumentLoadResult(false, "Error closing document", null, ex.Message));
            }
        });

        return await tcs.Task;
    }

    public async Task<DocumentCreationResult> NewDocumentAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<DocumentCreationResult>();

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                var editor = Instances.DocumentEditor;
                if (editor == null)
                {
                    tcs.SetResult(new DocumentCreationResult(
                        false,
                        "Grasshopper editor is not available. Open Grasshopper first.",
                        "no editor"));
                    return;
                }

                var ok = editor.ScriptAccess_NewDocument();
                tcs.SetResult(ok
                    ? new DocumentCreationResult(true, "New document created")
                    : new DocumentCreationResult(false, "Failed to create new document", "NewDocument returned false"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new document");
                tcs.SetResult(new DocumentCreationResult(false, "Error creating new document", ex.Message));
            }
        });

        return await tcs.Task;
    }
}