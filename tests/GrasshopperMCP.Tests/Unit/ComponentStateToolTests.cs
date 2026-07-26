using System;
using System.Linq;
using GrasshopperMCP;
using NUnit.Framework;

namespace GrasshopperMCP.Tests.Unit;

[TestFixture]
public class ComponentStateToolTests
{
    [Test]
    public void ToolCategories_RegistersComponentStateTools()
    {
        var all = ToolCategories.AllTools().ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.That(all, Does.Contain("Set_Component_Enabled"));
        Assert.That(all, Does.Contain("Set_Component_Preview"));
        Assert.That(all, Does.Contain("Get_Component_State"));
    }

    [Test]
    public void DefaultEnabled_IncludesComponentStateTools()
    {
        Assert.That(ToolCategories.DefaultEnabled, Does.Contain("Set_Component_Enabled"));
        Assert.That(ToolCategories.DefaultEnabled, Does.Contain("Set_Component_Preview"));
        Assert.That(ToolCategories.DefaultEnabled, Does.Contain("Get_Component_State"));
    }

    [Test]
    public void ToolSelection_IncludesComponentStateToolsWhenEnabled()
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "set_component_enabled",
            "set_component_preview",
            "get_component_state",
        };

        var types = ToolSelection.GetEnabledToolTypes(enabled).Select(t => t.Name).ToList();

        Assert.That(types, Does.Contain("ComponentStateTools"));
    }

    [Test]
    public void ToolSelection_ExposedNames_UseAttributeNames()
    {
        var type = typeof(GrasshopperMCP.Tools.ComponentStateTools);
        var method = type.GetMethod(nameof(GrasshopperMCP.Tools.ComponentStateTools.SetComponentEnabled))!;

        var exposed = ToolSelection.GetExposedName(
            method,
            method.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
                .FirstOrDefault()
                ?.Name);

        Assert.That(exposed, Is.EqualTo("set_component_enabled"));
    }
}
