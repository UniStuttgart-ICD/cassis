using NUnit.Framework;

namespace Cassis.Tests.Unit;

[TestFixture]
public class CassisSettingsTests
{
    [Test]
    public void ParseAutoStart_True()
    {
        Assert.That(CassisSettings.ParseAutoStart("{\"autoStart\":true}", false), Is.True);
    }

    [Test]
    public void ParseAutoStart_False()
    {
        Assert.That(CassisSettings.ParseAutoStart("{\"autoStart\":false}", true), Is.False);
    }

    [Test]
    public void ParseAutoStart_Missing_UsesDefault()
    {
        Assert.That(CassisSettings.ParseAutoStart("{}", true), Is.True);
        Assert.That(CassisSettings.ParseAutoStart("", false), Is.False);
    }
}
