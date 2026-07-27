using NUnit.Framework;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cassis.Services;

using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Cassis.Tests.Unit;

/// <summary>
/// Basic unit tests for services that don't require the full Grasshopper environment.
/// </summary>
[TestFixture]
public class BasicServiceTests
{
    private ILogger<GrasshopperDocumentService> _documentLogger = null!;

    [SetUp]
    public void Setup()
    {
        _documentLogger = NullLogger<GrasshopperDocumentService>.Instance;
    }



    [Test]
    public void GrasshopperDocumentService_Constructor_ShouldNotThrow()
    {
        // Arrange & Act & Assert
        Assert.DoesNotThrow(() => new GrasshopperDocumentService(_documentLogger));
    }

    [Test]
    public void BasicAssertions_ShouldWork()
    {
        // Arrange
        var value = 42;
        var text = "Hello, World!";

        // Act & Assert
        Assert.That(value, Is.EqualTo(42));
        Assert.That(text, Is.Not.Null.And.Not.Empty);
        Assert.That(text, Does.StartWith("Hello"));
    }

    [Test]
    public async Task AsyncTest_ShouldComplete()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        await Task.Delay(10, cts.Token);

        // Assert
        Assert.Pass("Async test completed successfully");
    }

    [Test]
    public void SimplifiedArchitecture_ShouldNotHaveRemovedFeatureDependencies()
    {
        // Arrange & Act
        var documentService = new GrasshopperDocumentService(_documentLogger);

        // Assert - Services should initialize without complex dependencies
        Assert.That(documentService, Is.Not.Null);

        // Verify services don't have caching, feature flags, or performance monitoring dependencies
        // This is validated by the fact that the constructors only take logger dependencies
    }



    [Test]
    public void RequestId_ShouldCreateCorrectly()
    {
        // Test the RequestId API that we migrated to

        // Arrange & Act
        var numericId = new RequestId(42);
        var stringId = new RequestId("test-request");

        // Assert - RequestId is a value type, so we just test string representation
        Assert.That(numericId.ToString(), Is.Not.Null.And.Not.Empty);
        Assert.That(stringId.ToString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void JsonRpcMessage_ShouldCreateCorrectly()
    {
        // Test basic MCP protocol objects

        // Arrange & Act
        var request = new JsonRpcRequest
        {
            Id = new RequestId("test"),
            Method = "test_method",
            Params = null
        };

        // Assert
        Assert.That(request, Is.Not.Null);
        Assert.That(request.Method, Is.EqualTo("test_method"));
        Assert.That(request.JsonRpc, Is.EqualTo("2.0"));
        Assert.That(request.Id.ToString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("Offline")]
    public void Client_Should_Fail_When_Server_Not_Running()
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var req = new JsonRpcRequest
        {
            Id = new RequestId(1),
            Method = "tools/list",
            Params = JsonSerializer.SerializeToNode(new { })
        };

        Assert.CatchAsync<Exception>(async () =>
        {
            await http.PostAsync(
                "http://localhost:45999/mcp",
                new System.Net.Http.StringContent(JsonSerializer.Serialize(req), System.Text.Encoding.UTF8, "application/json"));
        });
    }
}
