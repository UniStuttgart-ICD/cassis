using System;
using System.Collections.Generic;
using System.Linq;
using Cassis.Utilities;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

// Same cases as ghsh GhJsonQueryCommandTests, so both copies of CanvasOutline stay in step.
[TestFixture]
public class CanvasOutlineTests
{
    private static OutlineCanvas Book() => new()
    {
        Title = "Book",
        Components = new[]
        {
            new OutlineComponent
            {
                Id = 1, Name = "C# Script", NickName = "01 Context",
                InstanceGuid = "11111111-1111-1111-1111-111111111111", Pivot = "10,10",
                ScriptLanguage = "csharp",
                ScriptCode = "// Builds the guide curves.\n// Inputs: Guides. Outputs: Anchors.\n#region Usings\nusing System;\n#endregion\npublic class Script_Instance {}"
            },
            new OutlineComponent
            {
                Id = 2, Name = "C# Script", NickName = "C#", ScriptLanguage = "csharp",
                ScriptCode = "// Grasshopper Script Instance\n#region Usings\nusing System;\n#endregion\npublic class Script_Instance {}",
                InputNames = new[] { "x", "y" }, OutputNames = new[] { "out" }
            },
            new OutlineComponent { Id = 3, Name = "Number Slider" },
            new OutlineComponent { Id = 4, Name = "Panel" },
            new OutlineComponent { Id = 5, Name = "Scribble", ScribbleText = "ROBOT BASE" },
            new OutlineComponent { Id = 6, Name = "Panel" },
            new OutlineComponent { Id = 7, Name = "Panel" },
            new OutlineComponent { Id = 8, Name = "Curve" },
            new OutlineComponent { Id = 9, Name = "Point" },
        },
        Wires = new[]
        {
            Wire(3, 0, 1, 0), Wire(1, 1, 2, 0), Wire(2, 0, 6, 0), Wire(8, 0, 1, 2),
        },
        Groups = new[]
        {
            Group("20", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Authoring", 1, 2, 3),
            Group("21", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Inputs", 3),
            Group("22", "cccccccc-cccc-cccc-cccc-cccccccccccc", "", 5, 6),
            Group("23", "dddddddd-dddd-dddd-dddd-dddddddddddd", "Group", 4, 7),
            Group("24", "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "Empty"),
            Group("25", "ffffffff-ffff-ffff-ffff-ffffffffffff", "Overlap", 2, 6),
        }
    };

    private static OutlineWire Wire(int from, int fromPort, int to, int toPort) => new()
    {
        FromId = from, FromPort = fromPort.ToString(), ToId = to, ToPort = toPort.ToString()
    };

    private static OutlineGroup Group(string reference, string guid, string name, params int[] members) => new()
    {
        Ref = reference, InstanceGuid = guid, Name = name, Members = members
    };

    private static Dictionary<string, object?> Map(object? value) => (Dictionary<string, object?>)value!;

    private static List<object?> List(object? value) => (List<object?>)value!;

    private static List<string> Strings(object? value) => (List<string>)value!;

    [Test]
    public void Outline_BuildsAChapterTreeWithFallbackTitlesAndGaps()
    {
        var outline = CanvasOutline.Outline(Book());
        var chapters = List(outline["chapters"]).Select(Map).ToDictionary(chapter => (string)chapter["ref"]!);

        Assert.That(chapters.Keys.OrderBy(key => key, StringComparer.Ordinal),
            Is.EqualTo(new[] { "20", "22", "23", "25", "ungrouped" }));
        var authoring = chapters["20"];
        Assert.That(authoring["title"], Is.EqualTo("Authoring"));
        Assert.That(authoring.ContainsKey("derived"), Is.False);
        Assert.That(authoring["components"], Is.EqualTo(3));
        Assert.That(authoring["controls"], Is.EqualTo(1));
        // Script 2 also sits in the smaller overlapping group, so it is listed there.
        Assert.That(Strings(authoring["scripts"]), Is.EqualTo(new[] { "01 Context" }));
        Assert.That(Map(List(authoring["children"]).Single())["ref"], Is.EqualTo("21"));
        Assert.That(Strings(chapters["25"]["scripts"]), Is.EqualTo(new[] { "C# Script (x, y -> out)" }));

        Assert.That(chapters["22"]["title"], Is.EqualTo("ROBOT BASE"));
        Assert.That(chapters["22"]["derived"], Is.True);
        Assert.That(chapters["23"]["title"], Is.EqualTo("2 Panel"));
        Assert.That(chapters["ungrouped"]["components"], Is.EqualTo(2));

        // Component 6 is in two groups of equal size; the first one is its chapter.
        Assert.That(Strings(outline["flow"]).OrderBy(flow => flow, StringComparer.Ordinal),
            Is.EqualTo(new[] { "20>25x1", "25>22x1", "ungrouped>20x1" }));

        var gaps = Map(outline["gaps"]);
        Assert.That(Map(gaps["unnamedGroups"])["count"], Is.EqualTo(2));
        Assert.That(Strings(Map(gaps["defaultScriptNames"])["refs"]), Is.EqualTo(new[] { "2" }));
        Assert.That(gaps["ungrouped"], Is.EqualTo(2));
        Assert.That(Strings(Map(gaps["emptyGroups"])["refs"]), Is.EqualTo(new[] { "24" }));
        Assert.That(Strings(Map(gaps["overlaps"])["refs"]), Is.EqualTo(new[] { "20+25", "22+25" }));
    }

    [Test]
    public void Chapter_ReturnsComponentsWiresAndScriptSummaries()
    {
        var chapter = CanvasOutline.Chapter(Book(), "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        Assert.That(chapter["ref"], Is.EqualTo("20"));
        Assert.That(chapter["title"], Is.EqualTo("Authoring"));
        var components = List(chapter["components"]).Select(Map).ToDictionary(item => (int)item["id"]!);
        Assert.That(components.Keys, Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(components[1]["instanceGuid"], Is.EqualTo("11111111-1111-1111-1111-111111111111"));
        Assert.That(components[1]["pivot"], Is.EqualTo("10,10"));
        var script = Map(components[1]["script"]);
        Assert.That(script["language"], Is.EqualTo("csharp"));
        Assert.That(script["lines"], Is.EqualTo(6));
        Assert.That(script["header"], Is.EqualTo("Builds the guide curves. Inputs: Guides. Outputs: Anchors."));
        // The template comment is not a header.
        Assert.That(Map(components[2]["script"]).ContainsKey("header"), Is.False);
        Assert.That(components[2]["nickName"], Is.EqualTo("C#"));
        Assert.That(components[3]["group"], Is.EqualTo("21"));
        Assert.That(Strings(chapter["wires"]).OrderBy(w => w, StringComparer.Ordinal),
            Is.EqualTo(new[] { "1.1>2.0", "3.0>1.0" }));
        Assert.That(Strings(chapter["boundary"]).OrderBy(w => w, StringComparer.Ordinal),
            Is.EqualTo(new[] { "2.0>6.0", "8.0>1.2" }));
    }

    [Test]
    public void Chapter_ReadsUngroupedAndRejectsAnUnknownGroup()
    {
        var ungrouped = CanvasOutline.Chapter(Book(), "ungrouped");

        Assert.That(List(ungrouped["components"]).Select(Map).Select(item => (int)item["id"]!),
            Is.EqualTo(new[] { 8, 9 }));
        Assert.Throws<OutlineGroupNotFoundException>(() => CanvasOutline.Chapter(Book(), "999"));
    }

    [TestCase("python", "#! python 3\n# r: numpy\n\"\"\"Scales x.\n\nInputs: x.\n\"\"\"\nimport math\na = x", "Scales x. Inputs: x.")]
    [TestCase("python", "# Scales x.\n# Inputs: x.\nimport math", "Scales x. Inputs: x.")]
    [TestCase("csharp", "#region Usings\nusing System;\n#endregion\n\n/* Fits arcs.\n * Outputs: Arcs. */\npublic class A {}", "Fits arcs. Outputs: Arcs.")]
    [TestCase("csharp", "using System;\npublic class A { // not a header\n}", null)]
    [TestCase("vbscript", "' note", null)]
    public void ScriptHeader_ReadsTheFirstCommentBlockBeforeCode(string language, string code, string? expected)
    {
        Assert.That(CanvasOutline.ScriptHeader(code, language), Is.EqualTo(expected));
    }

    [Test]
    public void Registry_ExposesTheBookToolsByDefault()
    {
        Assert.That(ToolCategories.AllTools(), Does.Contain("Canvas_Outline").And.Contain("Canvas_Chapter").And.Contain("Rename_Objects"));
        Assert.That(ToolCategories.DefaultEnabled, Does.Contain("Canvas_Outline").And.Contain("Canvas_Chapter").And.Contain("Rename_Objects"));
        Assert.That(ToolCategories.DefaultEnabled, Does.Not.Contain("Get_Components_In_Group"));
        Assert.That(ToolCategories.ReadOnly, Does.Contain("Canvas_Outline").And.Contain("Canvas_Chapter"));
        Assert.That(ToolCategories.ReadOnly, Does.Not.Contain("Rename_Objects"));
    }
}
