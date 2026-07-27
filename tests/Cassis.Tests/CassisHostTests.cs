using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace Cassis.Tests;

[TestFixture]
public class CassisHostTests
{
    private readonly string _testPrefix = CreateLoopbackPrefix("test");
    private readonly HttpClient _httpClient = new();

    private static string CreateLoopbackPrefix(string path)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://localhost:{port}/{path}/";
    }

    [Test]
    public void Constructor_Throws_For_Null_Prefix()
    {
        Assert.Throws<ArgumentNullException>(() => new CassisHost(null!));
    }

    [Test]
    public void Constructor_Throws_For_Empty_Prefix()
    {
        Assert.Throws<ArgumentException>(() => new CassisHost(""));
    }

    [Test]
    public void Constructor_Rejects_NonLoopback_Prefix()
    {
        Assert.Throws<ArgumentException>(() => new CassisHost("http://0.0.0.0:3001/test/"));
    }

    [Test]
    public void Constructor_Should_Initialize_With_Valid_Prefix()
    {
        var host = new CassisHost(_testPrefix);
        Assert.NotNull(host);
    }

    [Test]
    public void Constructor_Should_Register_Simplified_Services()
    {
        var host = new CassisHost(_testPrefix);
        Assert.NotNull(host);

        // Verify that the host initializes without complex service dependencies
        // This test ensures the simplified service registration works
        // The constructor should complete without throwing, indicating proper DI setup
    }

    [Test]
    public void MessageReceived_Event_Should_Be_Accessible()
    {
        var host = new CassisHost(_testPrefix);
        bool eventFired = false;
        JsonRpcMessage? receivedMessage = null;

        host.MessageReceived += msg =>
        {
            eventFired = true;
            receivedMessage = msg;
        };

        Assert.False(eventFired);
        Assert.Null(receivedMessage);
    }

    [Test]
    public async Task StartAsync_Should_Not_Throw()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await host.StartAsync(cts.Token);
        await host.StopAsync();
    }

    [Test]
    public async Task StopAsync_Should_Not_Throw_When_Not_Started()
    {
        await using var host = new CassisHost(_testPrefix);
        await host.StopAsync();
    }

    [Test]
    public async Task DisposeAsync_Should_Cleanup_Resources()
    {
        await using var host = new CassisHost(_testPrefix);
        await host.DisposeAsync();
    }

    [Test]
    public async Task MCP_Should_Discover_Tools_From_Assembly()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await host.StartAsync(cts.Token);

        var initializeRequest = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "initialize",
            Params = JsonSerializer.SerializeToNode(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = true },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            })
        };

        var initContent = new StringContent(
            JsonSerializer.Serialize(initializeRequest),
            Encoding.UTF8, "application/json");

        var initResponse = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'), initContent);
        Assert.That(initResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));

        await Task.Delay(100);

        var toolsListRequest = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        var toolsContent = new StringContent(
            JsonSerializer.Serialize(toolsListRequest),
            Encoding.UTF8, "application/json");
        toolsContent.Headers.Add("Mcp-Session-Id", "test-session-123");

        var toolsResponse = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'), toolsContent);

        Assert.That(toolsResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(toolsResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        var responseContent = await toolsResponse.Content.ReadAsStringAsync();
        Assert.That(responseContent, Is.Not.Empty);
        Assert.That(responseContent, Does.Contain("data:"));

        await host.StopAsync();
    }

    [Test]
    public async Task MCP_Should_Discover_Prompts_From_Assembly()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await host.StartAsync(cts.Token);

        var initializeRequest = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "initialize",
            Params = JsonSerializer.SerializeToNode(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { prompts = true },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            })
        };

        var initContent = new StringContent(
            JsonSerializer.Serialize(initializeRequest),
            Encoding.UTF8, "application/json");

        await _httpClient.PostAsync(_testPrefix.TrimEnd('/'), initContent);
        await Task.Delay(100);

        var promptsListRequest = new JsonRpcRequest
        {
            Id = new RequestId(3),
            Method = "prompts/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        var promptsContent = new StringContent(
            JsonSerializer.Serialize(promptsListRequest),
            Encoding.UTF8, "application/json");
        promptsContent.Headers.Add("Mcp-Session-Id", "test-session-456");

        var promptsResponse = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'), promptsContent);

        Assert.That(promptsResponse.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(promptsResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        var responseContent = await promptsResponse.Content.ReadAsStringAsync();
        Assert.That(responseContent, Is.Not.Empty);
        Assert.That(responseContent, Does.Contain("data:"));

        await host.StopAsync();
    }

    [Test]
    public async Task MCP_Should_Handle_CORS_Preflight_Request()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await host.StartAsync(cts.Token);

        var request = new HttpRequestMessage(HttpMethod.Options, _testPrefix.TrimEnd('/'));
        request.Headers.Add("Origin", "http://localhost:3000");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type");

        var response = await _httpClient.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(response.Headers.Contains("Access-Control-Allow-Origin"), Is.True);
        Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin"), Is.EqualTo(new[] { "http://localhost:3000" }));
        Assert.That(response.Headers.Contains("Access-Control-Allow-Methods"), Is.True);

        await host.StopAsync();
    }

    [Test]
    public async Task MCP_Should_Reject_NonLoopback_CORS_Preflight()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await host.StartAsync(cts.Token);

        var request = new HttpRequestMessage(HttpMethod.Options, _testPrefix.TrimEnd('/'));
        request.Headers.Add("Origin", "https://example.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await _httpClient.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden));

        await host.StopAsync();
    }

    [Test]
    public async Task MCP_Should_Reject_Invalid_Path_Requests()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await host.StartAsync(cts.Token);

        var response = await _httpClient.GetAsync(_testPrefix + "invalid/path");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));

        await host.StopAsync();
    }

    [Test]
    public async Task MCP_Should_Handle_Invalid_JSON_Gracefully()
    {
        await using var host = new CassisHost(_testPrefix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await host.StartAsync(cts.Token);

        var invalidJson = "{ invalid json content }";
        var content = new StringContent(invalidJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'), content);

        Assert.That(response.StatusCode, Is.Not.EqualTo(System.Net.HttpStatusCode.InternalServerError));

        await host.StopAsync();
    }

    // --- Regression tests for tool discovery and filtering ---

    private static async Task<string> GetToolsListContent(CassisHost host, string prefix, HttpClient client)
    {
        await host.StartAsync(CancellationToken.None);

        var initRequest = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "initialize",
            Params = JsonSerializer.SerializeToNode(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = true },
                clientInfo = new { name = "test-client", version = "1.0.0" }
            })
        };
        await client.PostAsync(prefix.TrimEnd('/'),
            new StringContent(JsonSerializer.Serialize(initRequest), Encoding.UTF8, "application/json"));
        await Task.Delay(100);

        var toolsRequest = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };
        var toolsContent = new StringContent(
            JsonSerializer.Serialize(toolsRequest), Encoding.UTF8, "application/json");
        toolsContent.Headers.Add("Mcp-Session-Id", "regression-test");

        var response = await client.PostAsync(prefix.TrimEnd('/'), toolsContent);
        return (await response.Content.ReadAsStringAsync()).ToLowerInvariant();
    }

    [Test]
    public async Task Regression_AssemblyRename_ToolsListReturnsAtLeastOneTool()
    {
        // Proves the Cassis assembly is discovered (before the fix, zero tools were returned)
        var prefix = CreateLoopbackPrefix("regression1");
        await using var host = new CassisHost(prefix);
        using var client = new HttpClient();

        var content = await GetToolsListContent(host, prefix, client);

        Assert.That(content, Does.Contain("\"name\":"),
            "tools/list must return at least one tool — Cassis assembly must be discoverable");

        await host.StopAsync();
    }

    [Test]
    public async Task Regression_AttributeName_ToolAppearsUnderAttributeNameNotMethodName()
    {
        // [McpServerTool(Name = "Get_ComponentCount")] should surface as "get_componentcount", not "getcomponentcount"
        var prefix = CreateLoopbackPrefix("regression2");
        await using var host = new CassisHost(prefix);
        using var client = new HttpClient();

        var content = await GetToolsListContent(host, prefix, client);

        Assert.That(content, Does.Contain("\"name\":\"get_componentcount\""),
            "Tool with Name attribute should appear under the attribute name (lowercased)");
        Assert.That(content, Does.Not.Contain("\"name\":\"getcomponentcount\""),
            "Tool should NOT appear under bare method name when Name attribute is set");

        await host.StopAsync();
    }

    [Test]
    public async Task Regression_MethodNameFallback_ToolAppearsUnderMethodName()
    {
        // [McpServerTool] with no Name on method GetSystemHealth → key "getsystemhealth"
        var prefix = CreateLoopbackPrefix("regression3");
        await using var host = new CassisHost(prefix);
        using var client = new HttpClient();

        var content = await GetToolsListContent(host, prefix, client);

        Assert.That(content, Does.Contain("\"name\":\"getsystemhealth\""),
            "Tool with no Name attribute should appear under its lowercased method name");

        await host.StopAsync();
    }

    [Test]
    public async Task Regression_RestrictedEnabledTools_OnlyExposesAllowedTools()
    {
        // Host restricted to ["getsystemhealth"] must expose that tool and hide others
        var prefix = CreateLoopbackPrefix("regression4");
        await using var host = new CassisHost(prefix, enabledTools: new[] { "getsystemhealth" });
        using var client = new HttpClient();

        var content = await GetToolsListContent(host, prefix, client);

        Assert.That(content, Does.Contain("\"name\":\"getsystemhealth\""),
            "Enabled tool must be present");
        Assert.That(content, Does.Not.Contain("\"name\":\"get_componentcount\""),
            "Non-enabled tool must be absent when enabledTools is restricted");

        await host.StopAsync();
    }

    [Test]
    public async Task Regression_ComponentStateTools_AreDiscoverable()
    {
        var prefix = CreateLoopbackPrefix("regression5");
        await using var host = new CassisHost(prefix);
        using var client = new HttpClient();

        var content = await GetToolsListContent(host, prefix, client);

        Assert.That(content, Does.Contain("\"name\":\"set_component_enabled\""));
        Assert.That(content, Does.Contain("\"name\":\"set_component_preview\""));
        Assert.That(content, Does.Contain("\"name\":\"get_component_state\""));

        await host.StopAsync();
    }
}
