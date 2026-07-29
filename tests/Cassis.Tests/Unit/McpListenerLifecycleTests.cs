using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class McpListenerLifecycleTests
{
    [TestCase("Starting")]
    [TestCase("Stopping")]
    [TestCase("Restarting")]
    public void TransitionalState_DisablesServerToggle(string status)
    {
        Assert.That(McpListenerComponent.CanToggleServer(status), Is.False);
    }

    [TestCase("Stopped")]
    [TestCase("Running")]
    [TestCase("Error")]
    public void StableState_AllowsServerToggle(string status)
    {
        Assert.That(McpListenerComponent.CanToggleServer(status), Is.True);
    }

    [Test]
    public void UninitializedClient_ShowsAgentSetup()
    {
        Assert.That(McpListenerComponent.ShouldShowAgentSetup(false), Is.True);
    }

    [Test]
    public void InitializedClient_HidesAgentSetup()
    {
        Assert.That(McpListenerComponent.ShouldShowAgentSetup(true), Is.False);
    }

    [Test]
    public void SetupActions_UsePublicEndpointAndGuide()
    {
        Assert.Multiple(() =>
        {
            Assert.That(McpListenerComponent.McpEndpoint, Is.EqualTo("http://localhost:3003/mcp/"));
            Assert.That(
                McpListenerComponent.AgentSetupUrl,
                Is.EqualTo("https://github.com/UniStuttgart-ICD/cassis/blob/main/docs/agent-setup.md"));
        });
    }
}
