using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace ModelContextProtocol.HttpListener.Tests;

[TestFixture]
public sealed class HttpListenerMcpTransportTests
{
    [Test]
    public void ConstructorRejectsNonLoopbackPrefix()
    {
        Assert.Throws<ArgumentException>(() =>
            new HttpListenerMcpTransport("http://0.0.0.0:3001/mcp/", new McpServerOptions()));
    }

    [Test]
    public async Task PreflightEchoesLoopbackOrigin()
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, new McpServerOptions());
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            using var request = CreatePreflight(prefix, "http://localhost:5173");
            using var response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                response.Headers.GetValues("Access-Control-Allow-Origin"),
                Is.EqualTo(new[] { "http://localhost:5173" }));
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    [Test]
    public async Task PreflightRejectsNonLoopbackOrigin()
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, new McpServerOptions());
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            using var request = CreatePreflight(prefix, "https://example.com");
            using var response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    [Test]
    public async Task PreflightRejectsNonHttpOrigin()
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, new McpServerOptions());
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            using var request = CreatePreflight(prefix, "vscode-webview://local");
            using var response = await client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    [Test]
    public async Task PostRejectsOversizedBody()
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, new McpServerOptions());
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            using var content = new ByteArrayContent(new byte[(4 * 1024 * 1024) + 1]);
            using var response = await client.PostAsync(prefix.TrimEnd('/'), content);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    [Test]
    public async Task UnexpectedListenerStopFaultsCompletion()
    {
        var prefix = GetPrefix();
        var logPath = Path.Combine(Path.GetTempPath(), $"cassis-listener-test-{Guid.NewGuid():N}.log");
        try
        {
            await using var transport = new HttpListenerMcpTransport(
                prefix,
                new McpServerOptions(),
                debugLogPath: logPath);
            await transport.StartAsync();

            var listenerField = typeof(HttpListenerMcpTransport).GetField(
                "_listener",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var listener = (System.Net.HttpListener?)listenerField?.GetValue(transport);
            Assert.That(listener, Is.Not.Null);

            listener!.Stop();

            var exception = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await transport.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(exception!.Message, Does.Contain("unexpectedly"));
            Assert.That(File.ReadAllText(logPath), Does.Contain("Listener stopped unexpectedly"));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Test]
    public async Task SustainedSequentialAndConcurrentJsonRpcRequestsKeepListenerAlive()
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, new McpServerOptions());
        await transport.StartAsync();
        var received = 0;
        transport.MessageReceived += _ => Interlocked.Increment(ref received);

        using var client = new HttpClient();
        for (var index = 0; index < 50; index++)
        {
            var responseText = await SendUnknownRequestAsync(client, prefix, index);
            Assert.That(responseText, Does.Contain("\"jsonrpc\":\"2.0\""));
            Assert.That(responseText, Does.Contain("\"code\":-32601"));
        }

        var clients = Enumerable.Range(0, 4).Select(async clientIndex =>
        {
            using var concurrentClient = new HttpClient();
            for (var index = 0; index < 25; index++)
            {
                var requestId = 50 + (clientIndex * 25) + index;
                var responseText = await SendUnknownRequestAsync(concurrentClient, prefix, requestId);
                Assert.That(responseText, Does.Contain("\"jsonrpc\":\"2.0\""));
                Assert.That(responseText, Does.Contain("\"code\":-32601"));
            }
        });

        await Task.WhenAll(clients);

        Assert.That(received, Is.EqualTo(150));
        Assert.That(transport.Completion.IsCompleted, Is.False);
        var finalResponse = await SendUnknownRequestAsync(client, prefix, 151);
        Assert.That(finalResponse, Does.Contain("\"code\":-32601"));
    }

    [Test]
    public async Task RequestFailureIsLoggedAndDoesNotStopListener()
    {
        var prefix = GetPrefix();
        var logPath = Path.Combine(Path.GetTempPath(), $"cassis-request-test-{Guid.NewGuid():N}.log");
        try
        {
            await using var transport = new HttpListenerMcpTransport(
                prefix,
                new McpServerOptions(),
                debugLogPath: logPath);
            await transport.StartAsync();

            Action<ModelContextProtocol.Protocol.JsonRpcMessage> failingHandler =
                _ => throw new InvalidOperationException("request failure probe");
            transport.MessageReceived += failingHandler;

            using var client = new HttpClient();
            var failedResponse = await SendUnknownRequestAsync(client, prefix, 1);
            Assert.That(failedResponse, Does.Contain("\"code\":-32603"));
            Assert.That(File.ReadAllText(logPath), Does.Contain("request failure probe"));

            transport.MessageReceived -= failingHandler;
            var healthyResponse = await SendUnknownRequestAsync(client, prefix, 2);
            Assert.That(healthyResponse, Does.Contain("\"code\":-32601"));
            Assert.That(transport.Completion.IsCompleted, Is.False);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    private static HttpRequestMessage CreatePreflight(string prefix, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, prefix.TrimEnd('/'));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return request;
    }

    private static async Task<string> SendUnknownRequestAsync(HttpClient client, string prefix, int id)
    {
        using var content = new StringContent(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"test/unknown\",\"params\":{{}}}}",
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(prefix.TrimEnd('/'), content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    private static string GetPrefix()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://localhost:{port}/mcp/";
    }
}
