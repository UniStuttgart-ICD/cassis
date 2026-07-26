using System.Collections.Generic;

namespace GrasshopperMCP.Models;

/// <summary>
/// Result of component creation operation.
/// </summary>
public record ComponentCreationResult(
    bool Success,
    string? ComponentId,
    string? Type,
    string? Name,
    float X,
    float Y,
    string? ErrorMessage = null);

/// <summary>
/// Result of component value setting operation.
/// </summary>
public record ComponentValueResult(
    bool Success,
    string? ComponentId,
    string? ParameterName,
    string? Value,
    string? Message,
    string? ErrorMessage = null);

/// <summary>
/// Information about a component parameter.
/// </summary>
public record ParameterInfo(
    string Name,
    string NickName,
    string Type,
    bool Optional = false,
    string Description = "");

/// <summary>
/// Detailed information about a component.
/// </summary>
public record ComponentInfo(
    string Id,
    string Type,
    string Name,
    string Category,
    string SubCategory,
    string Description,
    float X,
    float Y,
    IReadOnlyList<ParameterInfo> Inputs,
    IReadOnlyList<ParameterInfo> Outputs,
    bool HasErrors = false,
    bool HasWarnings = false,
    IReadOnlyList<string>? RuntimeMessages = null,
    string? ScriptContent = null,
    IReadOnlyList<string>? Keywords = null);

/// <summary>
/// Result of component connection operation.
/// </summary>
public record ComponentConnectionResult(
    bool Success,
    string Message,
    string? SourceId = null,
    string? SourceParam = null,
    string? TargetId = null,
    string? TargetParam = null,
    string? ErrorMessage = null);

/// <summary>
/// Result of getting component information.
/// </summary>
public record ComponentInfoResult(
    bool Success,
    ComponentInfo? Component = null,
    string? ErrorMessage = null);

/// <summary>
/// Information about an available component type.
/// </summary>
public record AvailableComponentInfo(
    string Name,
    string Category,
    string SubCategory,
    string Description,
    bool IsObsolete = false);

/// <summary>
/// Result of listing available components.
/// </summary>
public record AvailableComponentsResult(
    bool Success,
    IReadOnlyList<AvailableComponentInfo> Components,
    int TotalCount,
    string? ErrorMessage = null);

/// <summary>
/// Result of component parameter modification operation.
/// </summary>
public record ComponentModificationResult(
    bool Success,
    string Message,
    IReadOnlyList<string>? Modifications = null,
    IReadOnlyList<string>? Errors = null);