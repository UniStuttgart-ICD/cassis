using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Cassis.Diagnostics
{
    /// <summary>
    /// Represents the status of a health check.
    /// </summary>
    public enum HealthStatus
    {
        /// <summary>
        /// The component is healthy.
        /// </summary>
        Healthy,

        /// <summary>
        /// The component is degraded but still functional.
        /// </summary>
        Degraded,

        /// <summary>
        /// The component is unhealthy.
        /// </summary>
        Unhealthy
    }

    /// <summary>
    /// Represents a health check that can be performed on a system component.
    /// </summary>
    public interface IHealthCheck
    {
        /// <summary>
        /// Gets the name of this health check.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the maximum time this health check should take.
        /// </summary>
        TimeSpan Timeout { get; }

        /// <summary>
        /// Gets the tags associated with this health check for grouping and filtering.
        /// </summary>
        IReadOnlyList<string> Tags { get; }

        /// <summary>
        /// Performs the health check.
        /// </summary>
        /// <param name="cancellationToken">Token to cancel the health check</param>
        /// <returns>The result of the health check</returns>
        Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents the result of a health check.
    /// </summary>
    public class HealthCheckResult
    {
        public HealthCheckResult(HealthStatus status, string? description = null, Exception? exception = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            Status = status;
            Description = description;
            Exception = exception;
            Data = data ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Gets the status of the health check.
        /// </summary>
        public HealthStatus Status { get; }

        /// <summary>
        /// Gets the description of the health check result.
        /// </summary>
        public string? Description { get; }

        /// <summary>
        /// Gets the exception that occurred during the health check, if any.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Gets additional data about the health check result.
        /// </summary>
        public IReadOnlyDictionary<string, object> Data { get; }



        /// <summary>
        /// Gets whether the health check passed.
        /// </summary>
        public bool IsHealthy => Status == HealthStatus.Healthy;

        /// <summary>
        /// Creates a healthy result.
        /// </summary>
        public static HealthCheckResult Healthy(string? description = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new HealthCheckResult(HealthStatus.Healthy, description, null, data);
        }

        /// <summary>
        /// Creates a degraded result.
        /// </summary>
        public static HealthCheckResult Degraded(string? description = null, Exception? exception = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new HealthCheckResult(HealthStatus.Degraded, description, exception, data);
        }

        /// <summary>
        /// Creates an unhealthy result.
        /// </summary>
        public static HealthCheckResult Unhealthy(string? description = null, Exception? exception = null,
            IReadOnlyDictionary<string, object>? data = null)
        {
            return new HealthCheckResult(HealthStatus.Unhealthy, description, exception, data);
        }
    }

    /// <summary>
    /// Base class for health checks that provides common functionality.
    /// </summary>
    public abstract class HealthCheckBase : IHealthCheck
    {
        protected HealthCheckBase(string name, TimeSpan? timeout = null, params string[] tags)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Timeout = timeout ?? TimeSpan.FromSeconds(5);
            Tags = tags?.ToList() ?? new List<string>();
        }

        public string Name { get; }
        public TimeSpan Timeout { get; }
        public IReadOnlyList<string> Tags { get; }

        public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(Timeout);
                using var combinedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                var result = await CheckHealthInternalAsync(combinedCts.Token);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                var result = HealthCheckResult.Unhealthy($"Health check '{Name}' timed out after {Timeout}");
                return result;
            }
            catch (Exception ex)
            {
                var result = HealthCheckResult.Unhealthy($"Health check '{Name}' threw an exception", ex);
                return result;
            }
        }

        /// <summary>
        /// Derived classes must implement the actual health check logic.
        /// </summary>
        protected abstract Task<HealthCheckResult> CheckHealthInternalAsync(CancellationToken cancellationToken);
    }
}
