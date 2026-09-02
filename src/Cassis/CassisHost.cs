using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cassis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.HttpListener;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis;

/// <summary>
/// Hosts an MCP server for Grasshopper using <see cref="HttpListenerMcpTransport"/>.
/// This MVP implementation uses simplified service architecture without caching,
/// feature flags, or performance monitoring.
/// </summary>
public sealed class CassisHost : IAsyncDisposable, IDisposable
{
    private readonly HttpListenerMcpTransport _transport;
    private readonly ILogger<CassisHost> _logger;

    /// <summary>
    /// Initializes the host for the specified <paramref name="prefix"/>.
    /// </summary>
    public CassisHost(string prefix, IEnumerable<string>? enabledTools = null)
    {
        if (prefix == null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("MCP prefix is required.", nameof(prefix));
        }

        var logPath = Path.Combine(Path.GetTempPath(), "cassis_debug.log");

        // ponytail: skip Microsoft.Extensions.DI — Speckle already loaded Abstractions 2.2
        Rhino.RhinoApp.WriteLine("[MCP] CassisHost constructor: Creating services...");
        var uiService = new GrasshopperUIService(NullLogger<GrasshopperUIService>.Instance);
        var documentService = new GrasshopperDocumentService(NullLogger<GrasshopperDocumentService>.Instance);
        var componentService = new GrasshopperComponentService(NullLogger<GrasshopperComponentService>.Instance);
        var healthChecks = new Diagnostics.IHealthCheck[]
        {
            new Diagnostics.GrasshopperConnectionHealthCheck(
                uiService, NullLogger<Diagnostics.GrasshopperConnectionHealthCheck>.Instance),
            new Diagnostics.MemoryHealthCheck(),
        };

        IServiceProvider services = new StaticServiceProvider(new Dictionary<Type, object>
        {
            [typeof(IGrasshopperUIService)] = uiService,
            [typeof(IGrasshopperDocumentService)] = documentService,
            [typeof(IGrasshopperComponentService)] = componentService,
            [typeof(IEnumerable<Diagnostics.IHealthCheck>)] = healthChecks,
        });

        _logger = NullLogger<CassisHost>.Instance;

        Rhino.RhinoApp.WriteLine("[MCP] CassisHost constructor: Configuring MCP server...");
        var enabledSet = enabledTools == null
            ? null
            : new HashSet<string>(enabledTools, StringComparer.OrdinalIgnoreCase);

        HashSet<string>? allowedToolNames = enabledSet == null
            ? null
            : new HashSet<string>(enabledSet.Select(n => n.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        var assemblyVersion = typeof(CassisHost).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Cassis assembly version is unavailable.");
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "Cassis",
                Title = "Cassis",
                Version = assemblyVersion.ToString(3),
            },
        };

        Rhino.RhinoApp.WriteLine("[MCP] CassisHost constructor: Creating transport...");
        _transport = new HttpListenerMcpTransport(
            prefix, options, NullLoggerFactory.Instance, services, logPath, allowedToolNames);
        Rhino.RhinoApp.WriteLine("[MCP] CassisHost constructor: Transport created.");

        Rhino.RhinoApp.WriteLine("[MCP] CassisHost constructor: Completed successfully!");
        _logger.LogInformation("CassisHost initialized with endpoint: {Prefix}", prefix);
        _logger.LogInformation("Debug log file: {LogPath}", logPath);
    }

    /// <summary>Occurs when an MCP message is received.</summary>
    public event Action<JsonRpcMessage>? MessageReceived
    {
        add
        {
            _transport.MessageReceived += value;
            _logger.LogDebug("MessageReceived event handler added");
        }
        remove
        {
            _transport.MessageReceived -= value;
            _logger.LogDebug("MessageReceived event handler removed");
        }
    }

    /// <summary>Completes when the transport listener stops.</summary>
    public Task Completion => _transport.Completion;

    /// <summary>Starts listening for requests.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting MCP transport...");
        await _transport.StartAsync(cancellationToken);
        _logger.LogInformation("MCP transport started successfully");
    }

    /// <summary>Stops listening for requests.</summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping MCP transport...");
        await _transport.StopAsync();
        _logger.LogInformation("MCP transport stopped");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger.LogDebug("Disposing CassisHost...");
        await _transport.DisposeAsync();
        _logger.LogDebug("CassisHost disposed");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class StaticServiceProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _map;

        public StaticServiceProvider(Dictionary<Type, object> map)
        {
            _map = map;
        }

        public object? GetService(Type serviceType)
        {
            return _map.TryGetValue(serviceType, out var service) ? service : null;
        }
    }
}
