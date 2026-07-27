using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

/// <summary>
/// Integration tests for Cassis Script components (Python and C#) using SSE.
/// Tests the addpythonscriptcomponent and addcsharpscriptcomponent tools.
/// Skipped automatically when no live Cassis server is reachable at MCP_URL.
/// </summary>
[TestFixture, Category("Online")]
public class ScriptComponentSseTests
{
    private readonly HttpClient _http = new();
    private readonly string _endpoint;

    public ScriptComponentSseTests()
    {
        _endpoint = (Environment.GetEnvironmentVariable("MCP_URL") ?? "http://localhost:3003/mcp").TrimEnd('/');
    }

    [OneTimeSetUp]
    public void CheckServerAvailable()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            probe.GetAsync(_endpoint).GetAwaiter().GetResult();
        }
        catch
        {
            Assert.Ignore($"Skipping Online tests: Cassis server not reachable at {_endpoint}");
        }
    }

    [Test]
    public async Task AddPythonScriptComponent_Should_Work_With_SSE()
    {
        // Arrange
        var testScript = @"#! python 3
import rhinoscriptsyntax as rs

def main():
    print(""Hello from Python script component!"")
    pt = rs.AddPoint(0, 0, 0)
    return pt

if __name__ == ""__main__"":
    result = main()
    print(f""Result: {result}"")
";

        var call = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addpythonscriptcomponent",
                arguments = new
                {
                    script = testScript,
                    x = 300,
                    y = 300
                }
            })
        };

        // Act
        var response = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"));

        // Assert
        Assert.That(response.IsSuccessStatusCode, Is.True, 
            $"Expected successful response, got {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        
        // Accept both 202 (SSE-handled) and 200 (direct reply) responses
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            Assert.That(content, Does.Contain("Accepted"), "Expected 202 Accepted response");
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // For 200 responses, verify JSON structure
            Assert.That(content, Does.Contain("data:"), "Expected SSE data format");
        }
    }

    [Test]
    public async Task AddCSharpScriptComponent_Should_Work_With_SSE()
    {
        // Arrange
        var testScript = @"using Rhino.Geometry;
using System;

public class Script_Instance : GH_ScriptInstance
{
    private void RunScript(ref object A, ref object B, ref object C)
    {
        var pt = new Point3d(0,0,0);
        A = pt;
        var ln = new Line(pt, new Point3d(1,1,1));
        B = ln;
        var cr = new Circle(Plane.WorldXY, 1.0);
        C = cr;
        Print(""C# Script component is working!"");
    }
}
";

        var call = new JsonRpcRequest
        {
            Id = new RequestId(2),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addcsharpscriptcomponent",
                arguments = new
                {
                    script = testScript,
                    x = 400,
                    y = 400
                }
            })
        };

        // Act
        var response = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"));

        // Assert
        Assert.That(response.IsSuccessStatusCode, Is.True, 
            $"Expected successful response, got {response.StatusCode}");

        var content = await response.Content.ReadAsStringAsync();
        
        // Accept both 202 (SSE-handled) and 200 (direct reply) responses
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            Assert.That(content, Does.Contain("Accepted"), "Expected 202 Accepted response");
        }
        else if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            // For 200 responses, verify JSON structure
            Assert.That(content, Does.Contain("data:"), "Expected SSE data format");
        }
    }

    [Test]
    public async Task ScriptComponents_Should_Handle_Invalid_Scripts()
    {
        // Arrange - Test with empty script
        var call = new JsonRpcRequest
        {
            Id = new RequestId(3),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addpythonscriptcomponent",
                arguments = new
                {
                    script = "",
                    x = 500,
                    y = 500
                }
            })
        };

        // Act
        var response = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"));

        // Assert - Should still return success (validation happens in Grasshopper)
        Assert.That(response.IsSuccessStatusCode, Is.True, 
            "Expected successful response even with empty script");
    }

    [Test]
    public async Task ScriptComponents_Should_Handle_Missing_Parameters()
    {
        // Arrange - Test with missing required parameters
        var call = new JsonRpcRequest
        {
            Id = new RequestId(4),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addpythonscriptcomponent",
                arguments = new
                {
                    script = "print('test')"
                    // Missing x, y coordinates
                }
            })
        };

        // Act
        var response = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(call), Encoding.UTF8, "application/json"));

        // Assert - Should handle missing parameters gracefully
        Assert.That(response.IsSuccessStatusCode, Is.True, 
            "Expected successful response even with missing parameters");
    }

    [Test]
    public async Task ScriptComponents_Should_Support_Different_Languages()
    {
        // Arrange - Test both Python and C# in sequence
        var pythonScript = "print('Python test')";
        var csharpScript = "Print(\"C# test\");";

        var pythonCall = new JsonRpcRequest
        {
            Id = new RequestId(5),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addpythonscriptcomponent",
                arguments = new
                {
                    script = pythonScript,
                    x = 600,
                    y = 600
                }
            })
        };

        var csharpCall = new JsonRpcRequest
        {
            Id = new RequestId(6),
            Method = "tools/call",
            Params = JsonSerializer.SerializeToNode(new
            {
                name = "addcsharpscriptcomponent",
                arguments = new
                {
                    script = csharpScript,
                    x = 700,
                    y = 700
                }
            })
        };

        // Act & Assert - Both should succeed
        var pythonResponse = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(pythonCall), Encoding.UTF8, "application/json"));
        Assert.That(pythonResponse.IsSuccessStatusCode, Is.True, "Python component should succeed");

        var csharpResponse = await _http.PostAsync(_endpoint, new StringContent(
            JsonSerializer.Serialize(csharpCall), Encoding.UTF8, "application/json"));
        Assert.That(csharpResponse.IsSuccessStatusCode, Is.True, "C# component should succeed");
    }
}
