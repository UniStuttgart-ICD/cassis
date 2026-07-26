using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.HttpListener;

/// <summary>
/// Defines basic lifecycle control for an MCP server transport.
/// </summary>
public interface IServerTransport : IAsyncDisposable
{
    /// <summary>Starts accepting HTTP requests.</summary>
    /// <param name="cancellationToken">Token to monitor for cancellation.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops listening and releases all resources.</summary>
    Task StopAsync();

    /// <summary>Sends a JSON-RPC message to a client session.</summary>
    /// <param name="message">The JSON-RPC message to send.</param>
    /// <param name="sessionId">The session ID to send the message to. If null, broadcasts to all sessions.</param>
    Task SendMessageAsync(JsonRpcMessage message, string? sessionId = null);

    /// <summary>Raised whenever a JSON‑RPC message is received.</summary>
    event Action<JsonRpcMessage>? MessageReceived;
}