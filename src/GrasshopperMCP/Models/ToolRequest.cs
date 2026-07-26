using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GrasshopperMCP.Models;

/// <summary>
/// Represents a tool request coming from a client.
/// </summary>
public class ToolRequest
{
    /// <summary>
    /// Tool type
    /// </summary>
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Tool parameters
    /// </summary>
    [JsonProperty("parameters")]
    public Dictionary<string, object> Parameters { get; set; } = new();

    /// <summary>
    /// Initializes a new instance of the request.
    /// </summary>
    /// <param name="type">Tool type</param>
    /// <param name="parameters">Tool parameters</param>
    public ToolRequest(string type, Dictionary<string, object>? parameters = null)
    {
        Type = type;
        Parameters = parameters ?? new Dictionary<string, object>();
    }

    /// <summary>
    /// Gets the specified parameter value.
    /// </summary>
    /// <typeparam name="T">Parameter type</typeparam>
    /// <param name="name">Parameter name</param>
    /// <returns>Parameter value</returns>
    public T? GetParameter<T>(string name)
    {
        if (Parameters.TryGetValue(name, out object? value))
        {
            if (value is T typedValue) return typedValue;

            // Try to convert
            try
            {
                return (T?)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                // Convert from JObject if needed
                if (value is Newtonsoft.Json.Linq.JObject jObject) return jObject.ToObject<T>();

                // Convert from JArray if needed
                if (value is Newtonsoft.Json.Linq.JArray jArray) return jArray.ToObject<T>();
            }
        }

        // Return default if conversion fails
        return default;
    }
}

/// <summary>
/// Response returned to the caller after a tool executes.
/// </summary>
public class Response
{
    /// <summary>
    /// Indicates whether the request was successful
    /// </summary>
    [JsonProperty("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Result data if successful
    /// </summary>
    [JsonProperty("data")]
    public object? Data { get; set; }

    /// <summary>
    /// Error message if any
    /// </summary>
    [JsonProperty("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Creates a success response
    /// </summary>
    /// <param name="data">Optional data</param>
    /// <returns>The response instance</returns>
    public static Response Ok(object? data = null)
    {
        return new Response
        {
            Success = true,
            Data = data
        };
    }

    /// <summary>
    /// Creates an error response
    /// </summary>
    /// <param name="errorMessage">Error message</param>
    /// <returns>The response instance</returns>
    public static Response CreateError(string errorMessage)
    {
        return new Response
        {
            Success = false,
            Data = null,
            Error = errorMessage
        };
    }
}