using System.Collections.Generic;
using System.IO;
using System.Linq;
using GrasshopperMCP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.HttpListener;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GrasshopperMCP;

/// <summary>
/// Hosts an MCP server for Grasshopper using <see cref="HttpListenerMcpTransport"/>.
/// This MVP implementation uses simplified service architecture without caching, 
/// feature flags, or performance monitoring.
/// </summary>
public sealed class GrasshopperMcpHost : IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _services;
    private readonly HttpListenerMcpTransport _transport;
    private readonly ILogger<GrasshopperMcpHost>? _logger;

    /// <summary>
    /// Initializes the host for the specified <paramref name="prefix"/>.
    /// </summary>
    public GrasshopperMcpHost(string prefix, IEnumerable<string>? enabledTools = null)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "grasshopper_mcp_debug.log");
        
        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Creating services...");
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Adding Grasshopper services...");
        // Add core Grasshopper services (simplified MVP architecture - direct implementations only)
        services.AddSingleton<IGrasshopperUIService, GrasshopperUIService>();
        services.AddSingleton<IGrasshopperDocumentService, GrasshopperDocumentService>();
        services.AddSingleton<IGrasshopperComponentService, GrasshopperComponentService>();

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Adding health checks...");
        // Add essential health checks only (MVP scope - connection and memory monitoring)
        services.AddSingleton<Diagnostics.IHealthCheck, Diagnostics.GrasshopperConnectionHealthCheck>();
        services.AddSingleton<Diagnostics.IHealthCheck, Diagnostics.MemoryHealthCheck>();
        services.AddSingleton<Diagnostics.GrasshopperHealthChecks>();

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Configuring MCP server...");
        var enabledSet = enabledTools == null
            ? null
            : new HashSet<string>(enabledTools, StringComparer.OrdinalIgnoreCase);
        var toolTypes = ToolSelection.GetEnabledToolTypes(enabledSet).ToArray();

        // allowedToolNames is passed to the transport to filter tools/list and tools/call.
        // All tools (Cassis and external) are subject to the same per-tool filtering.
        HashSet<string>? allowedToolNames = enabledSet == null
            ? null
            : new HashSet<string>(enabledSet.Select(n => n.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        services.AddMcpServer()
            .WithTools(toolTypes)
            .WithPromptsFromAssembly();

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Building service provider...");
        _services = services.BuildServiceProvider();
        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Service provider built.");
        _logger = _services.GetService<ILogger<GrasshopperMcpHost>>();

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Getting options...");
        McpServerOptions options;
        try
        {
            options = _services.GetRequiredService<IOptions<McpServerOptions>>().Value;
            Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Options resolved.");
        }
        catch (Exception ex)
        {
            Rhino.RhinoApp.WriteLine($"[MCP ERROR] GrasshopperMcpHost constructor: Failed to resolve options: {ex.Message}");
            throw;
        }

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Resolving logger factory...");
        var loggerFactory = _services.GetService<ILoggerFactory>();
        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Logger factory resolved.");

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Creating transport...");
        // Create transport
        _transport = new HttpListenerMcpTransport(prefix, options, loggerFactory, _services, logPath, allowedToolNames);
        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Transport created.");

        Rhino.RhinoApp.WriteLine("[MCP] GrasshopperMcpHost constructor: Completed successfully!");
        _logger?.LogInformation("GrasshopperMcpHost initialized with endpoint: {Prefix}", prefix);
        _logger?.LogInformation("Debug log file: {LogPath}", logPath);
    }

    /// <summary>Occurs when an MCP message is received.</summary>
    public event Action<JsonRpcMessage>? MessageReceived
    {
        add
        {
            _transport.MessageReceived += value;
            _logger?.LogDebug("MessageReceived event handler added");
        }
        remove
        {
            _transport.MessageReceived -= value;
            _logger?.LogDebug("MessageReceived event handler removed");
        }
    }

    /// <summary>Starts listening for requests.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting MCP transport...");
        await _transport.StartAsync(cancellationToken);
        _logger?.LogInformation("MCP transport started successfully");
    }

    /// <summary>Stops listening for requests.</summary>
    public async Task StopAsync()
    {
        _logger?.LogInformation("Stopping MCP transport...");
        await _transport.StopAsync();
        _logger?.LogInformation("MCP transport stopped");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger?.LogDebug("Disposing GrasshopperMcpHost...");
        await _transport.DisposeAsync();
        await _services.DisposeAsync();
        _logger?.LogDebug("GrasshopperMcpHost disposed");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
