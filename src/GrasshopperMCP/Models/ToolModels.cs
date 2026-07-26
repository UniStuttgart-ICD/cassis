using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GrasshopperMCP.Models;

/// <summary>
/// Simple coordinate used for canvas placement metadata.
/// </summary>
public sealed class Position
{
    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }
}

/// <summary>
/// Result payload for returning script source code.
/// </summary>
public sealed class ScriptResult
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("componentId")]
    public string ComponentId { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single script diagnostic entry.
/// </summary>
public sealed class ScriptError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;
}

/// <summary>
/// Aggregate of script compilation diagnostics.
/// </summary>
public sealed class ScriptErrors
{
    [JsonPropertyName("errors")]
    public List<ScriptError> Errors { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<ScriptError> Warnings { get; set; } = new();

    [JsonPropertyName("rawMessage")]
    public string RawMessage { get; set; } = string.Empty;
}

/// <summary>
/// Lightweight descriptor for script components.
/// </summary>
public sealed class ScriptComponentInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("nickName")]
    public string NickName { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public Position Position { get; set; } = new();
}

/// <summary>
/// Metadata captured with viewport screenshots.
/// </summary>
public sealed class ViewportCaptureMetadata
{
    [JsonPropertyName("viewportName")]
    public string ViewportName { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;
}

/// <summary>
/// Claude-compatible image source metadata.
/// </summary>
public sealed class ClaudeImageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "image/png";

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// Claude-compatible image content container.
/// </summary>
public sealed class ClaudeImageContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image";

    [JsonPropertyName("source")]
    public ClaudeImageSource Source { get; set; } = new();

    [JsonPropertyName("annotations")]
    public Dictionary<string, object> Annotations { get; set; } = new();
}

/// <summary>
/// Response payload for viewport capture requests.
/// </summary>
public sealed class ViewportCaptureResultClaude
{
    [JsonPropertyName("content")]
    public List<ClaudeImageContent> Content { get; set; } = new();

    public static ViewportCaptureResultClaude CreateImageResponse(string? base64Data, ViewportCaptureMetadata metadata, bool includeBase64 = true)
    {
        var result = new ViewportCaptureResultClaude();
        var imageContent = new ClaudeImageContent();

        if (includeBase64 && !string.IsNullOrEmpty(base64Data))
        {
            imageContent.Source = new ClaudeImageSource
            {
                Type = "base64",
                MediaType = "image/png",
                Data = base64Data
            };
        }
        else
        {
            imageContent.Source = new ClaudeImageSource
            {
                Type = "file_reference",
                MediaType = "image/png",
                Data = string.Empty
            };
        }

        imageContent.Annotations["viewport"] = metadata.ViewportName;
        imageContent.Annotations["width"] = metadata.Width;
        imageContent.Annotations["height"] = metadata.Height;
        imageContent.Annotations["fileSize"] = metadata.FileSize;
        imageContent.Annotations["timestamp"] = metadata.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        imageContent.Annotations["filePath"] = metadata.FilePath;

        if (!string.IsNullOrWhiteSpace(metadata.FilePath))
        {
            var expiration = Services.ViewportCaptureCleanupService.GetExpirationTime(metadata.FilePath);
            if (expiration.HasValue)
            {
                imageContent.Annotations["expiresAt"] = expiration.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }
        }

        result.Content.Add(imageContent);
        return result;
    }
}

/// <summary>
/// Information about a Rhino viewport.
/// </summary>
public sealed class ViewportInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("displayMode")]
    public string DisplayMode { get; set; } = string.Empty;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

/// <summary>
/// Listing of available viewports.
/// </summary>
public sealed class ViewportListResult
{
    [JsonPropertyName("viewports")]
    public List<ViewportInfo> Viewports { get; set; } = new();

    [JsonPropertyName("activeViewportName")]
    public string ActiveViewportName { get; set; } = string.Empty;

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Simple 3D vector for JSON serialization.
/// </summary>
public sealed class Vec3
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("z")]
    public double Z { get; set; }
}

/// <summary>
/// Information about a Rhino named view.
/// </summary>
public sealed class NamedViewInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("namedViewId")]
    public string NamedViewId { get; set; } = string.Empty;

    [JsonPropertyName("cameraLocation")]
    public Vec3? CameraLocation { get; set; }

    [JsonPropertyName("cameraDirection")]
    public Vec3? CameraDirection { get; set; }
}

/// <summary>
/// Listing of named views in the active Rhino document.
/// </summary>
public sealed class NamedViewListResult
{
    [JsonPropertyName("namedViews")]
    public List<NamedViewInfo> NamedViews { get; set; } = new();

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}
