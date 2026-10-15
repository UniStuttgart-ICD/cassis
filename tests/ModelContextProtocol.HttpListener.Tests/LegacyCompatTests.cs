using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace ModelContextProtocol.HttpListener.Tests;

[TestFixture]
public sealed class LegacyCompatTests
{
    private static readonly (string Name, string Value) ProtocolHeader = ("MCP-Protocol-Version", "2025-06-18");

    private static readonly McpServerOptions Options = new()
    {
        ServerInfo = new Implementation { Name = "Cassis", Version = "1.0.0" }
    };

    [Test]
    public async Task L1_InitializeReturnsSseWithSessionAndLegacyVersion()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix, Rpc(1, "initialize",
                """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}"""));

            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.ContentType, Does.StartWith("text/event-stream"));
            Assert.That(response.SessionId, Is.Not.Null.And.Not.Empty);
            Assert.That(response.Body, Does.Contain("\"protocolVersion\":\"2025-06-18\""));
            Assert.That(response.Body, Does.Not.Contain("resultType"));
        });
    }

    [Test]
    public async Task L2_NotificationOnLiveGetSessionIsAccepted()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, prefix.TrimEnd('/'));
            using var stream = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);
            var sessionId = GetSessionId(stream);

            var response = await PostAsync(client, prefix, Notification, ("Mcp-Session-Id", sessionId!));

            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Accepted));
        });
    }

    [Test]
    public async Task L2b_NotificationWithoutSessionIsRejected()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix, Notification);

            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task L3_UnknownToolReturnsTopLevelError()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix,
                Rpc(2, "tools/call", """{"name":"nosuchtool","arguments":{}}"""), ProtocolHeader);

            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Body, Does.Contain("\"code\":-32601"));
            using var json = ParseSseData(response.Body);
            Assert.That(json.RootElement.TryGetProperty("result", out _), Is.False);
            Assert.That(json.RootElement.GetProperty("error").GetProperty("message").GetString(),
                Does.Contain("nosuchtool"));
        });
    }

    [Test]
    public async Task L4_ToolsListIncludesFixtureEcho()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix, Rpc(3, "tools/list"), ProtocolHeader);

            Assert.That(response.Body, Does.Contain("echo"));
            Assert.That(response.Body, Does.Contain("tools"));
            Assert.That(response.Body, Does.Not.Contain("resultType"));
            Assert.That(response.Body, Does.Not.Contain("ttlMs"));
        });
    }

    [Test]
    public async Task L5_GetOpensSseStreamWithSession()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, prefix.TrimEnd('/'));
            using var response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType?.ToString(), Does.StartWith("text/event-stream"));
            Assert.That(GetSessionId(response), Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task L6_BatchArrayAnswersBothIds()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var batch = $"[{Rpc(41, "tools/list")},{Rpc(42, "tools/list")}]";

            var response = await PostAsync(client, prefix, batch, ProtocolHeader);

            Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Body, Does.Contain("\"id\":41"));
            Assert.That(response.Body, Does.Contain("\"id\":42"));
        });
    }

    [Test]
    public async Task L7_PingReturnsEmptyResult()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix, Rpc(4, "ping"), ProtocolHeader);

            using var json = ParseSseData(response.Body);
            Assert.That(json.RootElement.TryGetProperty("error", out _), Is.False);
            var result = json.RootElement.GetProperty("result");
            Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(result.EnumerateObject().Any(), Is.False);
        });
    }

    [Test]
    public async Task L8_MissingArgumentReturnsInvalidParams()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix,
                Rpc(5, "tools/call", """{"name":"echo","arguments":{}}"""), ProtocolHeader);

            using var json = ParseSseData(response.Body);
            Assert.That(json.RootElement.TryGetProperty("result", out _), Is.False);
            var error = json.RootElement.GetProperty("error");
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32602));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("Missing required argument 'text'"));
        });
    }

    [Test]
    public async Task L9_GarbageAndLegacyHeadersUseLegacyPath()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            foreach (var version in new[] { "foo", "2025-11-25" })
            {
                var response = await PostAsync(client, prefix, Rpc(6, "tools/list"),
                    ("MCP-Protocol-Version", version));

                Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK), version);
                Assert.That(response.ContentType, Does.StartWith("text/event-stream"), version);
            }
        });
    }

    [Test]
    public async Task L10_EchoReturnsContent()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix,
                Rpc(7, "tools/call", """{"name":"echo","arguments":{"text":"hi"}}"""), ProtocolHeader);

            Assert.That(response.Body, Does.Contain("\"text\":\"hi\""));
            Assert.That(response.Body, Does.Contain("content"));
        });
    }

    [Test]
    public async Task L11_RichResultDropsIsErrorToday()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix,
                Rpc(8, "tools/call", """{"name":"richresult","arguments":{}}"""), ProtocolHeader);

            Assert.That(response.Body, Does.Contain("rich failure"));
            Assert.That(response.Body, Does.Not.Contain("isError"));
        });
    }

    [Test]
    public async Task L12_ThrowingToolReturnsToolError()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var response = await PostAsync(client, prefix,
                Rpc(9, "tools/call", """{"name":"throws","arguments":{}}"""), ProtocolHeader);

            using var json = ParseSseData(response.Body);
            Assert.That(json.RootElement.TryGetProperty("result", out _), Is.False);
            var error = json.RootElement.GetProperty("error");
            Assert.That(error.GetProperty("code").GetInt32(), Is.EqualTo(-32001));
            Assert.That(error.GetProperty("message").GetString(), Does.Contain("fixture boom"));
        });
    }

    [Test]
    public async Task L13_PromptsListAndGet()
    {
        await WithTransportAsync(async (prefix, client) =>
        {
            var list = await PostAsync(client, prefix, Rpc(10, "prompts/list"), ProtocolHeader);
            Assert.That(list.Body, Does.Contain("greet"));

            var get = await PostAsync(client, prefix,
                Rpc(11, "prompts/get", """{"name":"greet","arguments":{"name":"Ada"}}"""), ProtocolHeader);
            Assert.That(get.Body, Does.Contain("Hello Ada"));
        });
    }

    private static string Rpc(int id, string method, string paramsJson = "{}") =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";

    private const string Notification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static async Task WithTransportAsync(Func<string, HttpClient, Task> test)
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, Options);
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            await test(prefix, client);
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, string? ContentType, string? SessionId, string Body)> PostAsync(
        HttpClient client,
        string prefix,
        string jsonBody,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, prefix.TrimEnd('/'))
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.ToString(), GetSessionId(response), body);
    }

    private static JsonDocument ParseSseData(string body)
    {
        var data = body.Split('\n').Select(line => line.Trim()).First(line => line.StartsWith("data:", StringComparison.Ordinal));
        return JsonDocument.Parse(data["data:".Length..]);
    }

    private static string? GetSessionId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null;

    private static string GetPrefix()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://localhost:{port}/mcp/";
    }
}
