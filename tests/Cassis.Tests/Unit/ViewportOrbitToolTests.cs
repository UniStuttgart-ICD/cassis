using System;
using System.Linq;
using Cassis;
using Cassis.Utilities;
using NUnit.Framework;
using Rhino.Geometry;

namespace Cassis.Tests.Unit;

[TestFixture]
public class ViewportOrbitToolTests
{
    [Test]
    public void ToolCategories_RegistersOrbitObject()
    {
        var all = ToolCategories.AllTools().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That(all, Does.Contain("Orbit_Object"));
        Assert.That(ToolCategories.Categories["Viewport"], Does.Contain("Orbit_Object"));
    }

    [Test]
    public void ResolvePreset_Front_IsHorizonLookingAlongY()
    {
        var (azimuth, elevation) = ViewportOrbitHelper.ResolvePreset("front");
        Assert.That(azimuth, Is.EqualTo(270.0));
        Assert.That(elevation, Is.EqualTo(0.0));
    }

    [Test]
    public void CameraLocationFromSpherical_Top_IsAboveTarget()
    {
        var target = Point3d.Origin;
        var camera = ViewportOrbitHelper.CameraLocationFromSpherical(target, 10, 0, 90);
        Assert.That(camera.Z, Is.GreaterThan(target.Z));
        Assert.That(camera.DistanceTo(target), Is.EqualTo(10).Within(1e-6));
    }

    [Test]
    public void ApplyRelativeStep_OrbitLeft_ChangesAzimuth()
    {
        var target = Point3d.Origin;
        var start = new Point3d(10, 0, 0);
        var moved = ViewportOrbitHelper.ApplyRelativeStep(target, start, "orbit_left", 90, 1.25);
        Assert.That(moved.Y, Is.GreaterThan(0));
        Assert.That(moved.X, Is.LessThan(0.01));
    }

    [Test]
    public void RadiusForBounds_ScalesWithDistanceFactor()
    {
        var bounds = new BoundingBox(new Point3d(0, 0, 0), new Point3d(10, 0, 0));
        var near = ViewportOrbitHelper.RadiusForBounds(bounds, 1.0);
        var far = ViewportOrbitHelper.RadiusForBounds(bounds, 3.0);
        Assert.That(far, Is.GreaterThan(near));
    }

    [Test]
    public void NeedsInitialFrame_DistantCamera_ReturnsTrue()
    {
        var bounds = new BoundingBox(new Point3d(0, 0, 0), new Point3d(1, 1, 1));
        var needs = ViewportOrbitHelper.NeedsInitialFrame(
            new Point3d(500, 0, 0),
            new Vector3d(-1, 0, 0),
            bounds.Center,
            bounds);
        Assert.That(needs, Is.True);
    }
}
