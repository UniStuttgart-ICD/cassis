using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Cassis.Services;
using Microsoft.Extensions.Logging;

namespace Cassis.Diagnostics;

/// <summary>
/// Health check that verifies Grasshopper connection and document state.
/// </summary>
public class GrasshopperConnectionHealthCheck : HealthCheckBase
{
    private readonly IGrasshopperUIService _uiService;
    private readonly ILogger<GrasshopperConnectionHealthCheck> _logger;

    public GrasshopperConnectionHealthCheck(
        IGrasshopperUIService uiService,
        ILogger<GrasshopperConnectionHealthCheck> logger)
        : base("Grasshopper Connection", TimeSpan.FromSeconds(3), "grasshopper", "connection")
    {
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HealthCheckResult> CheckHealthInternalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _uiService.SafeInvokeOnUIThreadAsync<HealthCheckResult, HealthCheckResult>(
                () =>
                {
                    var doc = Instances.ActiveCanvas?.Document;

                    if (doc == null) return HealthCheckResult.Unhealthy("No active Grasshopper document found");

                                    var totalObjects = doc.Objects.Count;
                var componentCount = doc.Objects.OfType<IGH_Component>().Count();
                var documentName = doc.DisplayName ?? "Untitled";
                var documentPath = doc.FilePath ?? "Not saved";

                var data = new Dictionary<string, object>
                {
                    ["total_objects"] = totalObjects,
                    ["component_count"] = componentCount,
                    ["document_name"] = documentName,
                    ["document_path"] = documentPath,
                    ["canvas_available"] = Instances.ActiveCanvas != null,
                    ["last_check"] = DateTime.UtcNow
                };

                if (totalObjects == 0) return HealthCheckResult.Degraded("Document is empty", null, data);

                return HealthCheckResult.Healthy($"Connected to '{documentName}' with {componentCount} components ({totalObjects} total objects)",
                    data);
                },
                ex => HealthCheckResult.Unhealthy("Failed to access Grasshopper", ex),
                cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Grasshopper connection health check failed");
            return HealthCheckResult.Unhealthy("Health check execution failed", ex);
        }
    }
}

/// <summary>
/// Health check that monitors system memory usage.
/// </summary>
public class MemoryHealthCheck : HealthCheckBase
{
    private readonly long _warningThresholdBytes;
    private readonly long _criticalThresholdBytes;

    public MemoryHealthCheck(long warningThresholdMB = 500, long criticalThresholdMB = 1000)
        : base("Memory Usage", TimeSpan.FromSeconds(1), "system", "memory")
    {
        _warningThresholdBytes = warningThresholdMB * 1024 * 1024;
        _criticalThresholdBytes = criticalThresholdMB * 1024 * 1024;
    }

    protected override Task<HealthCheckResult> CheckHealthInternalAsync(CancellationToken cancellationToken)
    {
        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var privateBytes = process.PrivateMemorySize64;

        var data = new Dictionary<string, object>
        {
            ["working_set_mb"] = workingSet / (1024 * 1024),
            ["private_bytes_mb"] = privateBytes / (1024 * 1024),
            ["warning_threshold_mb"] = _warningThresholdBytes / (1024 * 1024),
            ["critical_threshold_mb"] = _criticalThresholdBytes / (1024 * 1024),
            ["gc_total_memory_mb"] = GC.GetTotalMemory(false) / (1024 * 1024)
        };

        if (workingSet > _criticalThresholdBytes)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Memory usage is critical: {workingSet / (1024 * 1024)}MB", null, data));

        if (workingSet > _warningThresholdBytes)
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Memory usage is high: {workingSet / (1024 * 1024)}MB", null, data));

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Memory usage is normal: {workingSet / (1024 * 1024)}MB", data));
    }
}





/// <summary>
/// Simplified health check aggregator for MVP - only provides basic status information.
/// </summary>
public class GrasshopperHealthChecks
{
    private readonly ILogger<GrasshopperHealthChecks> _logger;

    public GrasshopperHealthChecks(ILogger<GrasshopperHealthChecks> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets basic system status information.
    /// </summary>
    /// <returns>Basic system status</returns>
    public async Task<object> GetBasicStatusAsync()
    {
        _logger.LogInformation("Getting basic system status");

        return new
        {
            Status = "Operational",
            Timestamp = DateTime.UtcNow,
            Message = "Cassis MVP is running"
        };
    }
}


