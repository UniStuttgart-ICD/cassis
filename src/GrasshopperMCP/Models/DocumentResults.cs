using System.Collections.Generic;

namespace GrasshopperMCP.Models;

/// <summary>
/// Basic information about a document component.
/// </summary>
public record DocumentComponentInfo(
    string Id,
    string Type,
    string Name,
    string Category,
    string SubCategory);

/// <summary>
/// Result of document information retrieval.
/// </summary>
public record DocumentInfoResult(
    string? Name,
    string? Path,
    int ComponentCount,
    IReadOnlyList<DocumentComponentInfo> Components,
    bool Modified,
    bool Enabled);

/// <summary>
/// Result of document clearing operation.
/// </summary>
public record DocumentClearResult(
    bool Success,
    string Message,
    int RemovedCount,
    int PreservedCount,
    string? ErrorMessage = null);

/// <summary>
/// Result of document save operation.
/// </summary>
public record DocumentSaveResult(
    bool Success,
    string Message,
    string? Path = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of document load operation.
/// </summary>
public record DocumentLoadResult(
    bool Success,
    string Message,
    string? Path = null,
    string? ErrorMessage = null);



/// <summary>
/// Result of document creation operation.
/// </summary>
public record DocumentCreationResult(
    bool Success,
    string Message,
    string? ErrorMessage = null);

/// <summary>
/// Information about solution status.
/// </summary>
public record SolutionStatus(
    bool IsValid,
    bool HasErrors,
    bool HasWarnings,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Result of getting solution status.
/// </summary>
public record SolutionStatusResult(
    bool Success,
    SolutionStatus? Status = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of solution update operation.
/// </summary>
public record SolutionUpdateResult(
    bool Success,
    string Message,
    int UpdatedComponents,
    string? ErrorMessage = null);

/// <summary>
/// Detailed information about a component in the canvas state.
/// </summary>
public record CanvasComponentInfo(
    string Id,
    string Type,
    string Name,
    string Category,
    string SubCategory,
    double X,
    double Y,
    bool IsEnabled,
    bool SupportsPreview,
    bool PreviewEnabled,
    bool IsExpired,
    string ExecutionStatus,
    List<string> Errors,
    List<string> Warnings,
    List<string> InfoMessages,
    List<CanvasParameterInfo> Inputs,
    List<CanvasParameterInfo> Outputs);

/// <summary>
/// Information about a component parameter in the canvas state.
/// </summary>
public record CanvasParameterInfo(
    string Name,
    int Index,
    string DataType,
    bool HasData,
    bool IsConnected,
    string? ConnectedTo);

/// <summary>
/// Information about a connection between components.
/// </summary>
public record CanvasConnectionInfo(
    string SourceComponentId,
    string SourceParameter,
    string TargetComponentId,
    string TargetParameter);

/// <summary>
/// Result of canvas state capture operation.
/// </summary>
public record CanvasStateResult(
    bool Success,
    string Message,
    string? DocumentName,
    string? DocumentPath,
    DateTime CaptureTime,
    int TotalComponents,
    int TotalConnections,
    List<CanvasComponentInfo> Components,
    List<CanvasConnectionInfo> Connections,
    string? ErrorMessage = null);