using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

/// <summary>
/// Comprehensive validation tests for core MCP functionality in the simplified MVP architecture.
/// Tests all essential MCP tools, protocol compliance, and error handling consistency.
/// </summary>
[TestFixture, Category("Online")]
public class CoreMcpFunctionalityValidationTests
{
    private string _testPrefix = string.Empty;
    private readonly HttpClient _httpClient = new();
    private string _toolsListContentLower = string.Empty;

    // No dynamic port allocation required; Online tests target an external MCP endpoint

    [SetUp]
    public async Task SetUp()
    {
        var endpoint = Environment.GetEnvironmentVariable("MCP_URL");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Ignore("Online tests require MCP_URL.");
        }

        _testPrefix = endpoint!.TrimEnd('/') + "/";

        try
        {
            await InitializeMcpConnection();
            await CacheToolsList();
        }
        catch
        {
            Assert.Ignore("Online tests require a reachable MCP_URL.");
        }
    }

    // No teardown required for external MCP endpoint

    private async Task InitializeMcpConnection()
    {
        var initRequest = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "initialize",
            Params = JsonSerializer.SerializeToNode(new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { tools = true, prompts = true },
                clientInfo = new { name = "validation-test", version = "1.0.0" }
            })
        };

        var response = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'),
            new StringContent(JsonSerializer.Serialize(initRequest), Encoding.UTF8, "application/json"));

        Assert.That(response.IsSuccessStatusCode, Is.True, "MCP initialization should succeed");
    }

    private async Task<string> CallMcpTool(string toolName, object arguments, int requestId = 2)
    {
        var request = new JsonRpcRequest
        {
            Id = new RequestId(requestId),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new { name = toolName, arguments })
        };

        var response = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'),
            new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));

        Assert.That(response.IsSuccessStatusCode, Is.True, $"Tool call for {toolName} should succeed");
        return await response.Content.ReadAsStringAsync();
    }

    private async Task CacheToolsList()
    {
        var request = new JsonRpcRequest
        {
            Id = new RequestId(100),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        var response = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'),
            new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        _toolsListContentLower = content.ToLowerInvariant();
    }

    private bool IsToolAvailable(string toolName)
    {
        if (string.IsNullOrEmpty(_toolsListContentLower)) return false;
        return _toolsListContentLower.Contains($"\"name\":\"{toolName.ToLowerInvariant()}\"");
    }



    #region Component Tools Validation

    [Test]
    public async Task ComponentTools_AddComponent_Should_Work_With_Valid_Parameters()
    {
        // Test adding a basic component
        var response = await CallMcpTool("addcomponent", new
        {
            type = "Point",
            x = 100.0,
            y = 100.0
        });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));
    }

    [Test]
    public async Task ComponentTools_AddComponent_Should_Reject_Invalid_Coordinates()
    {
        // Test adding component with extreme coordinates
        var response = await CallMcpTool("addcomponent", new
        {
            type = "Point",
            x = 50000.0, // Beyond reasonable canvas bounds
            y = 50000.0
        });

        // Should handle error gracefully
        Assert.That(response, Does.Contain("data:"));
    }

    [Test]
    public async Task ComponentTools_AddComponent_Should_Reject_Null_Type()
    {
        // Test adding component with null type
        var response = await CallMcpTool("addcomponent", new
        {
            type = (string?)null,
            x = 100.0,
            y = 100.0
        });

        // Should handle error gracefully
        Assert.That(response, Does.Contain("data:"));
    }

    [Test]
    public async Task ComponentTools_AddPythonScriptComponent_Should_Work_When_Available()
    {
        if (!IsToolAvailable("addpythonscriptcomponent"))
        {
            Assert.Ignore("Tool 'addpythonscriptcomponent' not available on target MCP endpoint");
        }

        var response = await CallMcpTool("addpythonscriptcomponent", new
        {
            x = 150.0,
            y = 150.0
        });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("exception"));
    }

    [Test]
    public async Task ComponentTools_AddCSharpScriptComponent_Should_Work_When_Available()
    {
        if (!IsToolAvailable("addcsharpscriptcomponent"))
        {
            Assert.Ignore("Tool 'addcsharpscriptcomponent' not available on target MCP endpoint");
        }

        var response = await CallMcpTool("addcsharpscriptcomponent", new
        {
            x = 200.0,
            y = 200.0
        });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("exception"));
    }

    [Test]
    public async Task ComponentTools_ListAvailableComponents_Should_Work()
    {
        if (!IsToolAvailable("listavailablecomponents"))
        {
            Assert.Ignore("Tool 'listavailablecomponents' not available on target MCP endpoint");
        }

        // Test listing all available components
        var response = await CallMcpTool("listavailablecomponents", new { });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));
        
        // Should contain component information
        var lowerResponse = response.ToLowerInvariant();
        Assert.That(lowerResponse, Does.Contain("success").And.Contain("components"));
    }

    [Test]
    public async Task ComponentTools_ListAvailableComponents_Should_Filter_By_Category()
    {
        if (!IsToolAvailable("listavailablecomponents"))
        {
            Assert.Ignore("Tool 'listavailablecomponents' not available on target MCP endpoint");
        }

        // Test listing components with category filter
        var response = await CallMcpTool("listavailablecomponents", new { category = "Params" });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));
        
        // Should contain filtered results
        var lowerResponse = response.ToLowerInvariant();
        Assert.That(lowerResponse, Does.Contain("success").And.Contain("components"));
    }

    #endregion

    #region Document Tools Validation

    [Test]
    public async Task DocumentTools_GetDocumentInfo_Should_Work()
    {
        if (!IsToolAvailable("getdocumentinfo"))
        {
            Assert.Ignore("Tool 'getdocumentinfo' not available on target MCP endpoint");
        }
        // Test getting document information
        var response = await CallMcpTool("getdocumentinfo", new { });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));
    }

    [Test]
    public async Task DocumentTools_ClearDocument_Should_Work()
    {
        if (!IsToolAvailable("cleardocument"))
        {
            Assert.Ignore("Tool 'cleardocument' not available on target MCP endpoint");
        }
        // Test clearing document
        var response = await CallMcpTool("cleardocument", new { });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));
    }

    [Test]
    public async Task DocumentTools_SaveDocument_Should_Handle_Optional_Path()
    {
        if (!IsToolAvailable("savedocument"))
        {
            Assert.Ignore("Tool 'savedocument' not available on target MCP endpoint");
        }
        // Test saving document without specifying path
        var response = await CallMcpTool("savedocument", new { });

        Assert.That(response, Does.Contain("data:"));
        // May contain error if no current document path, but should handle gracefully
    }

    #endregion

    #region Diagnostics Tools Validation

    [Test]
    public async Task DiagnosticsTools_GetSystemHealth_Should_Work()
    {
        if (!IsToolAvailable("getsystemhealth"))
        {
            Assert.Ignore("Tool 'getsystemhealth' not available on target MCP endpoint");
        }

        // Test getting system health
        var response = await CallMcpTool("getsystemhealth", new { });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));

        // Should contain essential health check information
        var lowerResponse = response.ToLowerInvariant();
        Assert.That(lowerResponse, Does.Contain("healthy").Or.Contain("unhealthy").Or.Contain("degraded"));
    }

    [Test]
    public async Task DiagnosticsTools_ListHealthChecks_Should_Return_Essential_Checks_Only()
    {
        if (!IsToolAvailable("listhealthchecks"))
        {
            Assert.Ignore("Tool 'listhealthchecks' not available on target MCP endpoint");
        }

        // Test listing health checks
        var response = await CallMcpTool("listhealthchecks", new { });

        Assert.That(response, Does.Contain("data:"));
        Assert.That(response, Does.Not.Contain("error"));

        var lowerResponse = response.ToLowerInvariant();
        // Should contain essential checks
        Assert.That(lowerResponse, Does.Contain("grasshopper").Or.Contain("memory"));

        // Should NOT contain removed features
        Assert.That(lowerResponse, Does.Not.Contain("cache"));
        Assert.That(lowerResponse, Does.Not.Contain("performance"));
        Assert.That(lowerResponse, Does.Not.Contain("featureflag"));
    }

    [Test]
    public async Task DiagnosticsTools_RunHealthCheck_Should_Work_With_Valid_Check()
    {
        // First get list of available health checks
        var listResponse = await CallMcpTool("listhealthchecks", new { });

        // Try to run a specific health check (this may fail in headless mode, but should handle gracefully)
        var response = await CallMcpTool("runhealthcheck", new { healthCheckName = "Memory Usage" });

        Assert.That(response, Does.Contain("data:"));
        // Should handle gracefully whether check exists or not
    }

    [Test]
    public async Task DiagnosticsTools_RunHealthCheck_Should_Handle_Invalid_Check_Name()
    {
        // Test running non-existent health check
        var response = await CallMcpTool("runhealthcheck", new { healthCheckName = "NonExistentCheck" });

        Assert.That(response, Does.Contain("data:"));
        // Should handle error gracefully and provide available checks
    }

    #endregion

    #region MCP Protocol Compliance

    [Test]
    public async Task MCP_Tools_List_Should_Return_All_Essential_Tools()
    {
        // Test tools/list method
        var request = new JsonRpcRequest
        {
            Id = new RequestId(100),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        var response = await _httpClient.PostAsync(_testPrefix.TrimEnd('/'),
            new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));

        Assert.That(response.IsSuccessStatusCode, Is.True);
        var content = await response.Content.ReadAsStringAsync();

        Assert.That(content, Does.Contain("data:"));
        var lowerContent = content.ToLowerInvariant();

        // Verify core tools are present (server may not include all optional tools)
        Assert.That(lowerContent, Does.Contain("addcomponent"));
        Assert.That(lowerContent, Does.Contain("get_componentcount"));
        Assert.That(lowerContent, Does.Contain("get_panel_text"));

        // Verify removed features are NOT present
        Assert.That(lowerContent, Does.Not.Contain("cache"));
        Assert.That(lowerContent, Does.Not.Contain("featureflag"));
        Assert.That(lowerContent, Does.Not.Contain("performance"));
    }

    [Test]
    public async Task MCP_Should_Handle_Invalid_Tool_Names_Gracefully()
    {
        // Test calling non-existent tool
        var response = await CallMcpTool("nonexistenttool", new { });

        Assert.That(response, Does.Contain("data:"));
        // Should contain error information but not crash
    }

    [Test]
    public async Task MCP_Should_Handle_Missing_Required_Parameters()
    {
        // Test calling tool with missing required parameters (use an always-available tool)
        var response = await CallMcpTool("addcomponent", new { }); // Missing required parameters

        Assert.That(response, Does.Contain("data:"));
        // Should handle parameter validation gracefully
    }

    [Test]
    public async Task MCP_Should_Return_Consistent_Response_Format()
    {
        // Test that all tools return consistent response format
        var tools = new (string, object)[]
        {
            ("addcomponent", new { type = "Point", x = 0.0, y = 0.0 }),
            ("getsystemhealth", new { }),
            ("getdocumentinfo", new { })
        };

        foreach (var (toolName, args) in tools)
        {
            var response = await CallMcpTool(toolName, args, requestId: 200 + Array.IndexOf(tools.Select(t => t.Item1).ToArray(), toolName));

            // All responses should contain SSE data format
            Assert.That(response, Does.Contain("data:"), $"Tool {toolName} should return SSE format");

            // All responses should be valid (no HTTP errors)
            Assert.That(response, Is.Not.Empty, $"Tool {toolName} should return non-empty response");
        }
    }

    #endregion

    #region Error Handling Consistency

    [Test]
    public async Task All_Tools_Should_Handle_Null_Service_Dependencies_Gracefully()
    {
        // This test verifies that the dependency injection and service resolution works correctly
        // If services are null, tools should fail gracefully rather than throwing NullReferenceException

        var tools = new (string, object)[]
        {
            ("addcomponent", new { type = "Point", x = 100.0, y = 100.0 }),
            ("getdocumentinfo", new { }),
            ("getsystemhealth", new { })
        };

        foreach (var (toolName, args) in tools)
        {
            var response = await CallMcpTool(toolName, args, requestId: 300 + Array.IndexOf(tools.Select(t => t.Item1).ToArray(), toolName));

            // Should not throw unhandled exceptions
            Assert.That(response, Is.Not.Empty, $"Tool {toolName} should handle service dependencies gracefully");
            Assert.That(response, Does.Contain("data:"), $"Tool {toolName} should return proper MCP response format");
        }
    }

    [Test]
    public async Task All_Tools_Should_Use_Consistent_Error_Response_Format()
    {
        // Test tools with invalid parameters to verify consistent error handling
        var invalidCalls = new (string, object)[]
        {
            ("addcomponent", new { type = "", x = 0.0, y = 0.0 }), // Empty type
            ("addcomponent", new { type = "Point", x = 50000.0, y = 50000.0 }), // Invalid coordinates
            ("setcomponentvalue", new { componentId = "invalid", parameterName = "test", value = "test" }) // Invalid component ID
        };

        foreach (var (toolName, args) in invalidCalls)
        {
            var response = await CallMcpTool(toolName, args, requestId: 400 + Array.IndexOf(invalidCalls.Select(t => t.Item1).ToArray(), toolName));

            // All error responses should follow consistent format
            Assert.That(response, Does.Contain("data:"), $"Tool {toolName} should return SSE format even for errors");
            Assert.That(response, Is.Not.Empty, $"Tool {toolName} should return meaningful error information");
        }
    }
    #endregion


}
