using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GrasshopperMCP.Extensions;

/// <summary>
/// Extension methods for MCP operations and error handling.
/// </summary>
public static class McpExtensions
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a standardized error response for MCP tools.
    /// </summary>
    public static CallToolResult CreateErrorResult(string message, Exception? exception = null)
    {
        var errorResponse = new
        {
            success = false,
            error = message,
            details = exception?.Message,
            type = exception?.GetType().Name
        };

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(errorResponse, CompactJson)
                }
            ]
        };
    }

    /// <summary>
    /// Creates a standardized success response for MCP tools.
    /// </summary>
    public static CallToolResult CreateSuccessResult(object data)
    {
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(data, CompactJson)
                }
            ]
        };
    }

    /// <summary>
    /// Safely executes an async operation and returns appropriate MCP result.
    /// </summary>
    public static async Task<CallToolResult> SafeExecuteAsync(Func<Task<object>> operation, string operationName)
    {
        try
        {
            var result = await operation();
            return CreateSuccessResult(result);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResult($"Invalid argument for {operationName}: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            return CreateErrorResult($"Invalid operation for {operationName}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            return CreateErrorResult($"Unexpected error in {operationName}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Validates required parameters and throws ArgumentException if any are missing.
    /// </summary>
    public static void ValidateRequired(params (string name, object? value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
                throw new ArgumentException($"Required parameter '{name}' is missing or empty");
    }

    /// <summary>
    /// Validates numeric ranges and throws ArgumentOutOfRangeException if invalid.
    /// </summary>
    public static void ValidateRange(string paramName, double value, double min = double.MinValue,
        double max = double.MaxValue)
    {
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(paramName, value,
                $"Parameter '{paramName}' value {value} is outside valid range [{min}, {max}]");
    }
}