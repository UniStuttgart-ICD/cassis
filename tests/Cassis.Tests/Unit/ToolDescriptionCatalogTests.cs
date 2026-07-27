using System.Linq;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class ToolDescriptionCatalogTests
{
    [Test]
    public void TryGet_RegisteredTool_ReturnsItsDescription()
    {
        var found = ToolDescriptionCatalog.TryGet("Get_Panel_Text", out var description);

        Assert.That(found, Is.True);
        Assert.That(description, Does.StartWith("Gets the content of a Grasshopper panel."));
    }

    [Test]
    public void TryGet_UnknownTool_ReturnsFalse()
    {
        var found = ToolDescriptionCatalog.TryGet("Not_A_Cassis_Tool", out var description);

        Assert.That(found, Is.False);
        Assert.That(description, Is.Empty);
    }

    [Test]
    public void AllCompiledPanelTools_HaveDescriptions()
    {
        var missingNames = ToolCategories.AllTools()
            .Where(name => !ToolDescriptionCatalog.TryGet(name, out _))
            .ToArray();

        Assert.That(missingNames, Is.Empty,
            $"Missing MCP descriptions: {string.Join(", ", missingNames)}");
    }
}
