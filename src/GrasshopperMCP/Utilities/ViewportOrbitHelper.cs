using System;
using Rhino;
using Rhino.Geometry;

namespace GrasshopperMCP.Utilities;

/// <summary>
/// Spherical camera placement and relative orbit steps around a target point.
/// </summary>
public static class ViewportOrbitHelper
{
    public const double DefaultDistanceFactor = 2.5;
    public const double DefaultStepDegrees = 30.0;
    public const double MinDistanceFactor = 0.25;
    public const double MaxDistanceFactor = 50.0;

    public static (double AzimuthDegrees, double ElevationDegrees) ResolvePreset(string? viewPreset)
    {
        if (string.IsNullOrWhiteSpace(viewPreset))
        {
            return (45.0, 35.264); // isometric default
        }

        return viewPreset.Trim().ToLowerInvariant() switch
        {
            "front" => (270.0, 0.0),
            "back" => (90.0, 0.0),
            "right" => (0.0, 0.0),
            "left" => (180.0, 0.0),
            "top" => (0.0, 90.0),
            "bottom" => (0.0, -90.0),
            "iso" or "isometric" => (45.0, 35.264),
            _ => throw new ArgumentException(
                $"Unknown view preset '{viewPreset}'. Use front, back, left, right, top, bottom, or iso."),
        };
    }

    public static Point3d CameraLocationFromSpherical(
        Point3d target,
        double radius,
        double azimuthDegrees,
        double elevationDegrees)
    {
        if (radius <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "Camera distance must be positive.");
        }

        var azimuth = RhinoMath.ToRadians(azimuthDegrees);
        var elevation = RhinoMath.ToRadians(elevationDegrees);
        var horizontal = radius * Math.Cos(elevation);

        var offset = new Vector3d(
            horizontal * Math.Cos(azimuth),
            horizontal * Math.Sin(azimuth),
            radius * Math.Sin(elevation));

        return target + offset;
    }

    public static double RadiusForBounds(BoundingBox bounds, double distanceFactor)
    {
        var diagonal = bounds.Diagonal.Length;
        var maxEdge = Math.Max(bounds.Max.X - bounds.Min.X, Math.Max(bounds.Max.Y - bounds.Min.Y, bounds.Max.Z - bounds.Min.Z));
        var size = Math.Max(diagonal, maxEdge);
        if (size <= RhinoMath.SqrtEpsilon)
        {
            size = 10.0;
        }

        return size * ClampDistanceFactor(distanceFactor);
    }

    public static double ClampDistanceFactor(double distanceFactor)
    {
        if (distanceFactor < MinDistanceFactor || distanceFactor > MaxDistanceFactor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(distanceFactor),
                $"Distance factor must be between {MinDistanceFactor} and {MaxDistanceFactor}.");
        }

        return distanceFactor;
    }

    public static Point3d ApplyRelativeStep(
        Point3d target,
        Point3d cameraLocation,
        string step,
        double stepDegrees,
        double dollyFactor)
    {
        var offset = cameraLocation - target;
        if (offset.Length <= RhinoMath.SqrtEpsilon)
        {
            offset = new Vector3d(1, 1, 1);
        }

        var radians = RhinoMath.ToRadians(stepDegrees);
        var normalized = step.Trim().ToLowerInvariant();

        Vector3d rotated = normalized switch
        {
            "orbit_left" or "left" => RotateAroundAxis(offset, Vector3d.ZAxis, radians),
            "orbit_right" or "right" => RotateAroundAxis(offset, Vector3d.ZAxis, -radians),
            "orbit_up" or "up" => RotateAroundAxis(offset, GetCameraRight(offset), -radians),
            "orbit_down" or "down" => RotateAroundAxis(offset, GetCameraRight(offset), radians),
            "dolly_in" or "zoom_in" => offset * (1.0 / dollyFactor),
            "dolly_out" or "zoom_out" => offset * dollyFactor,
            "roll_left" => RotateAroundAxis(offset, offset, radians),
            _ => throw new ArgumentException(
                $"Unknown step '{step}'. Use orbit_left, orbit_right, orbit_up, orbit_down, dolly_in, dolly_out, or roll_left."),
        };

        return target + rotated;
    }

    /// <summary>
    /// True when the viewport is not sensibly aimed at the target (step orbit needs a baseline frame).
    /// </summary>
    public static bool NeedsInitialFrame(Point3d cameraLocation, Vector3d cameraDirection, Point3d target, BoundingBox bounds)
    {
        var offset = cameraLocation - target;
        var idealDistance = RadiusForBounds(bounds, DefaultDistanceFactor);
        if (offset.Length < idealDistance * 0.1 || offset.Length > idealDistance * 8.0)
        {
            return true;
        }

        var toTarget = target - cameraLocation;
        if (!toTarget.Unitize())
        {
            return true;
        }

        var look = cameraDirection;
        if (!look.Unitize())
        {
            return true;
        }

        var angle = Vector3d.VectorAngle(toTarget, look);
        return angle > RhinoMath.ToRadians(15.0);
    }

    public static (double AzimuthDegrees, double ElevationDegrees) SphericalFromOffset(Vector3d offset)
    {
        if (offset.Length <= RhinoMath.SqrtEpsilon)
        {
            return (45.0, 35.264);
        }

        var azimuth = RhinoMath.ToDegrees(Math.Atan2(offset.Y, offset.X));
        var horizontal = Math.Sqrt((offset.X * offset.X) + (offset.Y * offset.Y));
        var elevation = RhinoMath.ToDegrees(Math.Atan2(offset.Z, horizontal));
        return (azimuth, elevation);
    }

    private static Vector3d RotateAroundAxis(Vector3d vector, Vector3d axis, double radians)
    {
        if (!axis.Unitize())
        {
            axis = Vector3d.ZAxis;
        }

        var transform = Transform.Rotation(radians, axis, Point3d.Origin);
        vector.Transform(transform);
        return vector;
    }

    private static Vector3d GetCameraRight(Vector3d viewOffset)
    {
        var forward = viewOffset;
        if (!forward.Unitize())
        {
            forward = -Vector3d.ZAxis;
        }

        var right = Vector3d.CrossProduct(Vector3d.ZAxis, forward);
        if (!right.Unitize())
        {
            right = Vector3d.XAxis;
        }

        return right;
    }
}
