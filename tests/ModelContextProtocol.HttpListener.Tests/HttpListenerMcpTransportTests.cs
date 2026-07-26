using System.Net;
using System.Net.Sockets;
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

    private static HttpRequestMessage CreatePreflight(string prefix, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, prefix.TrimEnd('/'));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        return request;
    }

    private static string GetPrefix()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://localhost:{port}/mcp/";
    }
}
