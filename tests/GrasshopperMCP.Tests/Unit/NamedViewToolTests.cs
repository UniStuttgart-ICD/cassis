using System;
using System.Linq;
using GrasshopperMCP;
using GrasshopperMCP.Utilities;
using NUnit.Framework;

namespace GrasshopperMCP.Tests.Unit;

[TestFixture]
public class NamedViewToolTests
{
    [Test]
    public void ToolCategories_RegistersManageNamedViews()
    {
        var all = ToolCategories.AllTools().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(all, Does.Contain("Manage_NamedViews"));
        Assert.That(ToolCategories.Categories["Viewport"], Does.Contain("Manage_NamedViews"));
        Assert.That(ToolCategories.DefaultEnabled, Does.Contain("Manage_NamedViews"));
    }

    [Test]
    public void ParseAction_List_IsCaseInsensitive()
    {
        Assert.That(NamedViewHelper.ParseAction("LIST"), Is.EqualTo(NamedViewAction.List));
        Assert.That(NamedViewHelper.ParseAction(" restore "), Is.EqualTo(NamedViewAction.Restore));
    }

    [Test]
    public void ParseAction_Unknown_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => NamedViewHelper.ParseAction("capture"));
        Assert.That(ex!.Message, Does.Contain("Unknown action"));
    }

    [Test]
    public void ParseAction_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => NamedViewHelper.ParseAction(""));
    }

    [Test]
    public void RequireMutationConfirm_False_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => NamedViewHelper.RequireMutationConfirm(false));
        Assert.That(ex!.Message, Does.Contain("confirm: true"));
    }

    [Test]
    public void RequireMutationConfirm_Null_Throws()
    {
        Assert.Throws<ArgumentException>(() => NamedViewHelper.RequireMutationConfirm(null));
    }

    [Test]
    public void RequireLookup_MissingNameAndIndex_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => NamedViewHelper.RequireLookup(null, null));
        Assert.That(ex!.Message, Does.Contain("name or index"));
    }

    [Test]
    public void RequireLookup_WithName_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => NamedViewHelper.RequireLookup("Iso", null));
    }

    [Test]
    public void RequireLookup_WithIndex_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => NamedViewHelper.RequireLookup(null, 0));
    }
}
