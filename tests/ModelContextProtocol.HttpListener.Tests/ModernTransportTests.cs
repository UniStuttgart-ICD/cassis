using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NUnit.Framework;

namespace ModelContextProtocol.HttpListener.Tests;

[TestFixture]
public sealed class ModernTransportTests
{
    private const string Version = "2026-07-28";
    private const string MetaDefault = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}""";

    private static readonly McpServerOptions Options = new()
    {
        ServerInfo = new Implementation { Name = "Cassis", Version = "1.0.0" }
    };

    private sealed record Reply(HttpStatusCode Status, string? ContentType, string? SessionId, string Body);

    [Test]
    public async Task M1_DiscoverReturnsStatelessCapabilities()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "server/discover");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.ContentType, Is.EqualTo("application/json"));
            Assert.That(reply.SessionId, Is.Null);

            using var doc = JsonDocument.Parse(reply.Body);
            var result = doc.RootElement.GetProperty("result");
            Assert.That(
                result.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()),
                Is.EqualTo(new[] { "2026-07-28", "2025-06-18" }));
            Assert.That(result.GetProperty("capabilities").TryGetProperty("tools", out _), Is.True);
            Assert.That(result.GetProperty("capabilities").TryGetProperty("prompts", out _), Is.True);
            AssertCacheHints(result);
            Assert.That(ServerInfoName(result), Is.EqualTo("Cassis"));
        });
    }

    [Test]
    public async Task M1b_DiscoverWithoutServerInfoOmitsServerInfoMeta()
    {
        await WithTransportAsync(new McpServerOptions(), async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "server/discover");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(reply.Body);
            var result = doc.RootElement.GetProperty("result");
            Assert.That(result.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("io.modelcontextprotocol/serverInfo", out _), Is.False);
        });
    }

    [Test]
    public async Task M2_ToolsListCarriesResultTypeAndCacheHints()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(reply.Body);
            var result = doc.RootElement.GetProperty("result");
            var names = result.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString());
            Assert.That(names, Does.Contain("echo"));
            AssertCacheHints(result);
        });
    }

    [Test]
    public async Task M3_ToolCallReturnsCompleteResult()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/call",
                """{"name":"echo","arguments":{"text":"hi"}}""");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.ContentType, Is.EqualTo("application/json"));
            using var doc = JsonDocument.Parse(reply.Body);
            var result = doc.RootElement.GetProperty("result");
            Assert.That(result.GetProperty("resultType").GetString(), Is.EqualTo("complete"));
            Assert.That(result.GetProperty("content")[0].GetProperty("text").GetString(), Is.EqualTo("hi"));
        });
    }

    [Test]
    public async Task M4_ToolCallAcceptsBase64EncodedMcpName()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/call",
                """{"name":"echo","arguments":{"text":"hi"}}""",
                headers: new Dictionary<string, string?> { ["Mcp-Name"] = "=?base64?ZWNobw==?=" });

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            using var doc = JsonDocument.Parse(reply.Body);
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), Is.EqualTo("hi"));
        });
    }

    [Test]
    public async Task M8a_PromptsListAndGetUseModernPath()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var list = await ModernPostAsync(client, prefix, "prompts/list");
            Assert.That(list.Status, Is.EqualTo(HttpStatusCode.OK));
            using (var listDoc = JsonDocument.Parse(list.Body))
            {
                var result = listDoc.RootElement.GetProperty("result");
                var names = result.GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString());
                Assert.That(names, Does.Contain("greet"));
                AssertCacheHints(result);
            }

            var get = await ModernPostAsync(client, prefix, "prompts/get",
                """{"name":"greet","arguments":{"name":"Ada"}}""");
            Assert.That(get.Status, Is.EqualTo(HttpStatusCode.OK));
            using var getDoc = JsonDocument.Parse(get.Body);
            var text = getDoc.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString();
            Assert.That(text, Is.EqualTo("Hello Ada"));
        });
    }

    [Test]
    public async Task M9_MissingOrMismatchedMcpNameIsRejected()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var echo = """{"name":"echo","arguments":{"text":"hi"}}""";

            var mismatch = await ModernPostAsync(client, prefix, "tools/call", echo,
                headers: new Dictionary<string, string?> { ["Mcp-Name"] = "foo" });
            AssertJsonRpcError(mismatch, HttpStatusCode.BadRequest, -32020);

            var missing = await ModernPostAsync(client, prefix, "tools/call", echo,
                headers: new Dictionary<string, string?> { ["Mcp-Name"] = null });
            AssertJsonRpcError(missing, HttpStatusCode.BadRequest, -32020);

            var malformed = await ModernPostAsync(client, prefix, "tools/call", echo,
                headers: new Dictionary<string, string?> { ["Mcp-Name"] = "=?base64?!!?=" });
            AssertJsonRpcError(malformed, HttpStatusCode.BadRequest, -32020);
        });
    }

    [Test]
    public async Task M10_MismatchedOrMissingMcpMethodIsRejected()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var wrong = await ModernPostAsync(client, prefix, "tools/call",
                """{"name":"echo","arguments":{"text":"hi"}}""",
                headers: new Dictionary<string, string?> { ["Mcp-Method"] = "tools/list" });
            AssertJsonRpcError(wrong, HttpStatusCode.BadRequest, -32020);

            var missing = await ModernPostAsync(client, prefix, "tools/list",
                headers: new Dictionary<string, string?> { ["Mcp-Method"] = null });
            AssertJsonRpcError(missing, HttpStatusCode.BadRequest, -32020);
        });
    }

    [Test]
    public async Task M11_HeaderMetaMismatchIsRejected()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                meta: MetaWithVersion("2099-01-01"));

            AssertJsonRpcError(reply, HttpStatusCode.BadRequest, -32020);
        });
    }

    [Test]
    public async Task M12_MetaWithoutHeaderIsRejected()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                headers: new Dictionary<string, string?> { ["MCP-Protocol-Version"] = null });

            AssertJsonRpcError(reply, HttpStatusCode.BadRequest, -32020);
        });
    }

    [Test]
    public async Task M13_UnsupportedVersionListsSupportedVersions()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                meta: MetaWithVersion("2099-01-01"),
                headers: new Dictionary<string, string?> { ["MCP-Protocol-Version"] = "2099-01-01" });

            AssertJsonRpcError(reply, HttpStatusCode.BadRequest, -32022);
            using var doc = JsonDocument.Parse(reply.Body);
            var data = doc.RootElement.GetProperty("error").GetProperty("data");
            Assert.That(
                data.GetProperty("supported").EnumerateArray().Select(v => v.GetString()),
                Is.EqualTo(new[] { "2026-07-28", "2025-06-18" }));
            Assert.That(data.GetProperty("requested").GetString(), Is.EqualTo("2099-01-01"));
        });
    }

    [Test]
    public async Task M14_MalformedMetaIsInvalidParamsNeverServerError()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var noMeta = await ModernPostAsync(client, prefix, "tools/list", meta: null);
            AssertJsonRpcError(noMeta, HttpStatusCode.BadRequest, -32602);

            var arrayMeta = await ModernPostAsync(client, prefix, "tools/list", meta: "[1]");
            AssertJsonRpcError(arrayMeta, HttpStatusCode.BadRequest, -32602);

            var numberVersion = await ModernPostAsync(client, prefix, "tools/list",
                meta: """{"io.modelcontextprotocol/protocolVersion":2026,"io.modelcontextprotocol/clientCapabilities":{}}""");
            AssertJsonRpcError(numberVersion, HttpStatusCode.BadRequest, -32602);
        });
    }

    [Test]
    public async Task M15_MissingClientCapabilitiesIsInvalidParams()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                meta: """{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}""");

            AssertJsonRpcError(reply, HttpStatusCode.BadRequest, -32602);
        });
    }

    [Test]
    public async Task M16_RemovedMethodsReturnNotFound()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            AssertJsonRpcError(await ModernPostAsync(client, prefix, "foo/bar"), HttpStatusCode.NotFound, -32601);
            AssertJsonRpcError(await ModernPostAsync(client, prefix, "ping"), HttpStatusCode.NotFound, -32601);
            AssertJsonRpcError(await ModernPostAsync(client, prefix, "subscriptions/listen"), HttpStatusCode.NotFound, -32601);
            AssertJsonRpcError(
                await ModernPostAsync(client, prefix, "resources/read", """{"uri":"x://a"}""",
                    headers: new Dictionary<string, string?> { ["Mcp-Name"] = "x://a" }),
                HttpStatusCode.NotFound,
                -32601);
        });
    }

    [Test]
    public async Task M17_BatchIsRejectedWithoutId()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var batch = $"[{ModernBody("tools/list", "{}", "1", MetaDefault)},{ModernBody("tools/list", "{}", "2", MetaDefault)}]";
            var reply = await ModernPostAsync(client, prefix, "tools/list", rawBody: batch);

            AssertJsonRpcError(reply, HttpStatusCode.BadRequest, -32600);
            using var doc = JsonDocument.Parse(reply.Body);
            Assert.That(doc.RootElement.TryGetProperty("id", out _), Is.False);
        });
    }

    [Test]
    public async Task M18_NotificationReturnsAccepted()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "notifications/initialized",
                rawBody: """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Accepted));
            Assert.That(reply.Body, Is.Empty);
        });
    }

    [Test]
    public async Task M19_InitializeWithModernHeaderUsesLegacySse()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "initialize",
                """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"c","version":"1"}}""",
                meta: null);

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.ContentType, Is.EqualTo("text/event-stream"));
            Assert.That(reply.SessionId, Is.Not.Null);
            using var doc = ParseSseData(reply.Body);
            Assert.That(doc.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString(), Is.EqualTo("2025-06-18"));
        });
    }

    [Test]
    public async Task M20_ModernRequestIgnoresSessionHeader()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                headers: new Dictionary<string, string?> { ["Mcp-Session-Id"] = "abc" });

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.ContentType, Is.EqualTo("application/json"));
            Assert.That(reply.SessionId, Is.Null);
        });
    }

    [Test]
    public async Task M21_MalformedJsonWithModernHeaderIsParseError()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list", rawBody: "{");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(reply.Body, Is.EqualTo("""{"jsonrpc":"2.0","error":{"code":-32700,"message":"Parse error"}}"""));
        });
    }

    [Test]
    public async Task M22_NullIdIsRejectedAsClientError()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list", idJson: "null");

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(reply.ContentType, Is.EqualTo("application/json"));
            using var doc = JsonDocument.Parse(reply.Body);
            Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32600));
        });
    }

    [Test]
    public async Task M23_FailingMessageHandlerReturnsInternalErrorAndListenerSurvives()
    {
        await WithTransportAsync(Options, async (transport, prefix, client) =>
        {
            Action<JsonRpcMessage> failing = _ => throw new InvalidOperationException("modern probe");
            transport.MessageReceived += failing;

            var failed = await ModernPostAsync(client, prefix, "tools/list");
            AssertJsonRpcError(failed, HttpStatusCode.OK, -32603);
            Assert.That(failed.Body, Does.Contain("modern probe"));

            transport.MessageReceived -= failing;
            var healthy = await ModernPostAsync(client, prefix, "tools/list");
            Assert.That(healthy.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(transport.Completion.IsCompleted, Is.False);
        });
    }

    [Test]
    public async Task M24_NonLoopbackOriginIsForbidden()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "server/discover",
                headers: new Dictionary<string, string?> { ["Origin"] = "https://example.com" });

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task L_LegacyToolsListWithoutMetaStaysSse()
    {
        await WithTransportAsync(Options, async (prefix, client) =>
        {
            var reply = await ModernPostAsync(client, prefix, "tools/list",
                meta: null,
                headers: new Dictionary<string, string?> { ["MCP-Protocol-Version"] = "2025-06-18" });

            Assert.That(reply.Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(reply.ContentType, Is.EqualTo("text/event-stream"));
        });
    }

    private static void AssertCacheHints(JsonElement result)
    {
        Assert.That(result.GetProperty("resultType").GetString(), Is.EqualTo("complete"));
        Assert.That(result.GetProperty("ttlMs").GetInt32(), Is.EqualTo(0));
        Assert.That(result.GetProperty("cacheScope").GetString(), Is.EqualTo("public"));
    }

    private static string? ServerInfoName(JsonElement result) =>
        result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo").GetProperty("name").GetString();

    private static void AssertJsonRpcError(Reply reply, HttpStatusCode status, int code)
    {
        Assert.That(reply.Status, Is.EqualTo(status));
        Assert.That(reply.ContentType, Is.EqualTo("application/json"));
        using var doc = JsonDocument.Parse(reply.Body);
        Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(code));
    }

    private static string MetaWithVersion(string version) => MetaDefault.Replace(Version, version);

    private static string ModernBody(string method, string paramsJson, string idJson, string? meta)
    {
        var parameters = JsonNode.Parse(paramsJson)!.AsObject();
        if (meta is not null)
        {
            parameters["_meta"] = JsonNode.Parse(meta);
        }

        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idJson),
            ["method"] = method,
            ["params"] = parameters
        };
        return body.ToJsonString();
    }

    private static string? DefaultMcpName(string method, string paramsJson)
    {
        var key = method switch
        {
            "tools/call" or "prompts/get" => "name",
            "resources/read" => "uri",
            _ => null
        };
        if (key is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(paramsJson);
        return doc.RootElement.TryGetProperty(key, out var value) ? value.GetString() : null;
    }

    /// <summary>
    /// Posts a modern request. Defaults: valid version header, Mcp-Method, Mcp-Name (when applicable)
    /// and _meta. Header value null removes that header; meta null omits _meta; rawBody replaces the body.
    /// </summary>
    private static async Task<Reply> ModernPostAsync(
        HttpClient client,
        string prefix,
        string method,
        string paramsJson = "{}",
        string idJson = "1",
        Dictionary<string, string?>? headers = null,
        string? meta = MetaDefault,
        string? rawBody = null)
    {
        var body = rawBody ?? ModernBody(method, paramsJson, idJson, meta);

        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MCP-Protocol-Version"] = Version,
            ["Mcp-Method"] = method
        };
        if (DefaultMcpName(method, paramsJson) is { } name)
        {
            all["Mcp-Name"] = name;
        }

        foreach (var (key, value) in headers ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, prefix.TrimEnd('/'))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (key, value) in all)
        {
            if (value is not null)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(
            response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.TryGetValues("Mcp-Session-Id", out var sessions) ? sessions.FirstOrDefault() : null,
            text);
    }

    private static JsonDocument ParseSseData(string body)
    {
        var data = body.Split('\n').Select(line => line.Trim()).First(line => line.StartsWith("data:", StringComparison.Ordinal));
        return JsonDocument.Parse(data["data:".Length..]);
    }

    private static Task WithTransportAsync(McpServerOptions options, Func<string, HttpClient, Task> test) =>
        WithTransportAsync(options, (_, prefix, client) => test(prefix, client));

    private static async Task WithTransportAsync(
        McpServerOptions options,
        Func<HttpListenerMcpTransport, string, HttpClient, Task> test)
    {
        var prefix = GetPrefix();
        await using var transport = new HttpListenerMcpTransport(prefix, options);
        await transport.StartAsync();

        try
        {
            using var client = new HttpClient();
            await test(transport, prefix, client);
        }
        finally
        {
            await transport.StopAsync();
        }
    }

    private static string GetPrefix()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return $"http://localhost:{port}/mcp/";
    }
}
