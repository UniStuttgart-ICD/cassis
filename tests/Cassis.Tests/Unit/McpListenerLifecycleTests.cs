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
}
