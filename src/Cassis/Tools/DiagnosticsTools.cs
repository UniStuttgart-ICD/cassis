using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Cassis.Diagnostics;
using Cassis.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis.Tools;

/// <summary>
/// Simplified tools for basic system diagnostics and health monitoring.
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    /// <summary>
    /// Gets basic system health information from essential health checks only.
    /// </summary>
    [McpServerTool]
    [Description("Get basic system health information from essential health checks (Grasshopper connection and memory usage)")]
    public static async Task<CallToolResult> GetSystemHealth(
        IEnumerable<IHealthCheck> healthChecks)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(healthChecks), healthChecks));

            var healthCheckList = healthChecks.ToList();

            // Filter to only essential health checks for MVP
            var essentialChecks = healthCheckList.Where(hc =>
                hc.Name.IndexOf("Grasshopper Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                hc.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            var healthResults = await Task.WhenAll(
                essentialChecks.Select(hc => hc.CheckHealthAsync()));

            var overallStatus = healthResults.All(r => r.IsHealthy) ? "Healthy" :
                healthResults.Any(r => r.Status == HealthStatus.Unhealthy) ? "Unhealthy" : "Degraded";

            var diagnostics = new
            {
                Timestamp = DateTime.UtcNow,
                OverallStatus = overallStatus,
                HealthChecks = healthResults.Zip(essentialChecks, (result, check) => new
                {
                    Name = check.Name,
                    Status = result.Status.ToString(),
                    Description = result.Description,
                    IsHealthy = result.IsHealthy,
                    Error = result.Exception?.Message
                }).ToList(),
                Summary = new
                {
                    TotalChecks = healthResults.Length,
                    HealthyCount = healthResults.Count(r => r.IsHealthy),
                    UnhealthyCount = healthResults.Count(r => !r.IsHealthy)
                }
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(GetSystemHealth));
    }

    /// <summary>
    /// Runs a specific essential health check by name.
    /// </summary>
    [McpServerTool]
    [Description("Run a specific essential health check by name (Grasshopper Connection or Memory Usage)")]
    public static async Task<CallToolResult> RunHealthCheck(
        IEnumerable<IHealthCheck> healthChecks,
        [Description("Name of the health check to run")]
        string healthCheckName)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired(
                (nameof(healthChecks), healthChecks),
                (nameof(healthCheckName), healthCheckName));

            var healthCheck = healthChecks.FirstOrDefault(hc =>
                string.Equals(hc.Name, healthCheckName, StringComparison.OrdinalIgnoreCase));

            if (healthCheck == null)
            {
                var availableChecks = healthChecks
                    .Where(hc => hc.Name.IndexOf("Grasshopper Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                hc.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(hc => hc.Name).ToList();

                var errorResult = new
                {
                    Error = $"Health check '{healthCheckName}' not found",
                    AvailableHealthChecks = availableChecks,
                    Timestamp = DateTime.UtcNow
                };

                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(errorResult, new JsonSerializerOptions { WriteIndented = true })
                        }
                    ]
                };
            }

            var result = await healthCheck.CheckHealthAsync();

            var healthCheckResult = new
            {
                Name = healthCheck.Name,
                Status = result.Status.ToString(),
                Description = result.Description,
                IsHealthy = result.IsHealthy,
                Error = result.Exception?.Message,
                Timestamp = DateTime.UtcNow
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(healthCheckResult, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(RunHealthCheck));
    }

    /// <summary>
    /// Lists essential health checks available in the MVP.
    /// </summary>
    [McpServerTool]
    [Description("List essential health checks available in the MVP (Grasshopper connection and memory usage)")]
    public static async Task<CallToolResult> ListHealthChecks(
        IEnumerable<IHealthCheck> healthChecks)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(healthChecks), healthChecks));

            // Filter to only essential health checks for MVP
            var essentialChecks = healthChecks
                .Where(hc => hc.Name.IndexOf("Grasshopper Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            hc.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(hc => new
                {
                    Name = hc.Name,
                    Tags = hc.Tags,
                    Timeout = $"{hc.Timeout.TotalSeconds}s"
                })
                .OrderBy(hc => hc.Name)
                .ToList();

            var result = new
            {
                HealthChecks = essentialChecks,
                TotalCount = essentialChecks.Count,
                Timestamp = DateTime.UtcNow,
                Note = "Only essential health checks are available in MVP: Grasshopper connection and memory usage"
            };

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
                    }
                ]
            };
        }, nameof(ListHealthChecks));
    }
}