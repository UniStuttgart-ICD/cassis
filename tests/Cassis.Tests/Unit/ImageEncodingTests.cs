using System.Drawing;
using System.IO;
using Cassis.Utilities;
using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class ImageEncodingTests
{
    private string _folder = string.Empty;
    private Bitmap _bitmap = null!;

    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _bitmap = new Bitmap(2, 2);
    }

    [TearDown]
    public void TearDown()
    {
        _bitmap.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void SavePng_NoPath_WritesNothing(string? filePath)
    {
        Assert.That(ImageEncoding.SavePng(_bitmap, filePath), Is.Null);
    }

    [TestCase("shot.png", "shot.png")]
    [TestCase("shot", "shot.png")]
    [TestCase("shot.jpg", "shot.png")]
    public void SavePng_WithPath_CreatesFolderAndSavesPng(string name, string expected)
    {
        var requested = Path.Combine(_folder, "nested", name);

        var saved = ImageEncoding.SavePng(_bitmap, requested);

        Assert.That(saved, Is.EqualTo(Path.Combine(_folder, "nested", expected)));
        Assert.That(File.Exists(saved!), Is.True);
    }
}
