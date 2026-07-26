using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace GrasshopperMCP.Tests.Unit;

[TestFixture, Category("Online")]
public class ToolsCallIntegrationTests
{
    private readonly HttpClient _http = new();

    // No dynamic port allocation required; tests hit external MCP

    [Test]
    public async Task Tools_Call_Should_Invoke_Core_MCP_Tool()
    {
        var endpoint = (Environment.GetEnvironmentVariable("MCP_URL") ?? "http://localhost:3003/mcp").TrimEnd('/');
        var initialize = new JsonRpcRequest
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
        try
        {
            var initResp = await _http.PostAsync(endpoint, new StringContent(
                JsonSerializer.Serialize(initialize), Encoding.UTF8, "application/json"));
            Assert.That(initResp.IsSuccessStatusCode, Is.True);
        }
        catch
        {
            Assert.Ignore("Online test requires MCP at MCP_URL or http://localhost:3003/mcp.");
        }

        // Call tools/call for core MCP functionality - test system health
        var call = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "getsystemhealth",
                arguments = new { }
            })
        };

        var callResp = await _http.PostAsync(endpoint, new StringContent(
            JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"));
        Assert.That(callResp.IsSuccessStatusCode, Is.True);

        var content = await callResp.Content.ReadAsStringAsync();
        // Expect an SSE stream containing a JsonRpcResponse with 'result' data
        Assert.That(content, Does.Contain("data:"));

    }

    [Test]
    public async Task Tools_List_Should_Not_Include_Removed_Features()
    {
        var endpoint = (Environment.GetEnvironmentVariable("MCP_URL") ?? "http://localhost:3003/mcp").TrimEnd('/');
        var initialize = new JsonRpcRequest
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
        try
        {
            var initResp = await _http.PostAsync(endpoint, new StringContent(
                JsonSerializer.Serialize(initialize), Encoding.UTF8, "application/json"));
            Assert.That(initResp.IsSuccessStatusCode, Is.True);
        }
        catch
        {
            Assert.Ignore("Online test requires MCP at MCP_URL or http://localhost:3003/mcp.");
        }

        // List tools to verify removed features are not present
        var listReq = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        var listResp = await _http.PostAsync(endpoint, new StringContent(
            JsonSerializer.Serialize(listReq), Encoding.UTF8, "application/json"));
        Assert.That(listResp.IsSuccessStatusCode, Is.True);

        var content = await listResp.Content.ReadAsStringAsync();

        // Verify removed features are not in the tools list
        Assert.That(content.ToLowerInvariant(), Does.Not.Contain("getfeatureflags"));
        Assert.That(content.ToLowerInvariant(), Does.Not.Contain("cache"));
        Assert.That(content.ToLowerInvariant(), Does.Not.Contain("performance"));

        // Verify core tools are present (document tools may be optional)
        Assert.That(content.ToLowerInvariant(), Does.Contain("getsystemhealth"));
        Assert.That(content.ToLowerInvariant(), Does.Contain("createpoint"));
        // Document tools not required on all deployments
    }
}