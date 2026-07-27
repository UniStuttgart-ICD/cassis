using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cassis.Extensions;
using Cassis.Models;
using Cassis.Services;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Cassis.Tools;

/// <summary>
/// Tools for inspecting Rhino viewports and capturing images.
/// </summary>
[McpServerToolType]
public static class ViewportTools
{
    [McpServerTool(Name = "Capture_Viewport_Simple")]
    [Description("Compatibility alias: captures the active Rhino viewport to disk with default settings")]
    public static Task<CallToolResult> CaptureViewportSimple(
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60,
        [Description("Include base64-encoded image data in the response")] bool includeBase64 = false)
    {
        // Delegate to the full-featured capture with sensible defaults
        return CaptureViewport(
            viewportName: null,
            width: 1024,
            height: 768,
            includeGrid: false,
            includeAxes: false,
            transparentBackground: false,
            quality: 90,
            retentionSeconds: retentionSeconds,
            includeBase64: includeBase64);
    }

    [McpServerTool(Name = "List_Viewports")]
    [Description("Lists viewports in the active Rhino document with size and display metadata")]
    public static async Task<CallToolResult> ListViewports()
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = RhinoDoc.ActiveDoc;
                if (doc == null)
                {
                    throw new InvalidOperationException("No active Rhino document found");
                }

                var viewports = doc.Views
                    .Select(view =>
                    {
                        var viewport = view.MainViewport;
                        return new ViewportInfo
                        {
                            Name = viewport?.Name ?? $"Viewport_{view.RuntimeSerialNumber}",
                            Title = view.ActiveViewport?.Name ?? viewport?.Name ?? view.ActiveViewportID.ToString(),
                            DisplayMode = viewport?.DisplayMode?.EnglishName ?? "Unknown",
                            IsActive = doc.Views.ActiveView?.RuntimeSerialNumber == view.RuntimeSerialNumber,
                            Width = view.ClientRectangle.Width,
                            Height = view.ClientRectangle.Height
                        };
                    })
                    .ToList();

                return new ViewportListResult
                {
                    Viewports = viewports,
                    ActiveViewportName = viewports.FirstOrDefault(v => v.IsActive)?.Name ?? string.Empty,
                    TotalCount = viewports.Count
                };
            });

            return result;
        }, nameof(ListViewports));
    }

    [McpServerTool(Name = "Capture_Viewport")]
    [Description("Captures a Rhino viewport to disk (and optionally base64) with configurable settings")]
    public static async Task<CallToolResult> CaptureViewport(
        [Description("Viewport name (optional; uses active viewport when omitted)")] string? viewportName = null,
        [Description("Output width in pixels")] int width = 1024,
        [Description("Output height in pixels")] int height = 768,
        [Description("Include grid in the capture")] bool includeGrid = false,
        [Description("Include world axes in the capture")] bool includeAxes = false,
        [Description("Use transparent background")] bool transparentBackground = false,
        [Description("Image quality for lossy encoders (1-100)")] int quality = 90,
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60,
        [Description("Include base64-encoded image data in the response")] bool includeBase64 = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            ValidateDimensions(width, height);
            ValidateQuality(quality);

            var captureResult = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document found");
                var view = ResolveViewport(doc, viewportName);
                if (view == null)
                {
                    var name = string.IsNullOrEmpty(viewportName) ? "active viewport" : $"viewport '{viewportName}'";
                    throw new ArgumentException($"Unable to locate {name}");
                }

                view.Redraw();
                using var bitmap = CaptureBitmap(view, width, height, includeGrid, includeAxes, transparentBackground);
                if (bitmap == null)
                {
                    throw new InvalidOperationException("Viewport capture returned no bitmap. Try lowering resolution or using the simple capture tool.");
                }

                var capturePath = WriteCaptureToDisk(bitmap, view, quality);
                var metadata = BuildMetadata(view, bitmap, capturePath);

                string? base64 = null;
                if (includeBase64)
                {
                    using var memoryStream = new MemoryStream();
                    bitmap.Save(memoryStream, ImageFormat.Png);
                    base64 = Convert.ToBase64String(memoryStream.ToArray());
                }

                ViewportCaptureCleanupService.RegisterForCleanup(capturePath, retentionSeconds);
                return ViewportCaptureResultClaude.CreateImageResponse(base64, metadata, includeBase64);
            });

            return captureResult;
        }, nameof(CaptureViewport));
    }

    [McpServerTool(Name = "Orbit_Object")]
    [Description(
        "Frames a Grasshopper component by GUID (from its solved output geometry / preview), orbits the Rhino camera, and captures the view. " +
        "Use viewPreset (front/top/iso/...) for absolute views, azimuthDegrees+elevationDegrees for precise placement, " +
        "or step (orbit_left/orbit_right/dolly_in/...) to nudge the camera. Set fitToBounds=true to zoom-extents instead of spherical distance.")]
    public static async Task<CallToolResult> OrbitObject(
        [Description("Grasshopper object GUID whose preview geometry to orbit around")] string componentId,
        [Description("Named view: front, back, left, right, top, bottom, iso (default when no angles/step given)")] string? viewPreset = null,
        [Description("Relative camera move: orbit_left, orbit_right, orbit_up, orbit_down, dolly_in, dolly_out, roll_left")] string? step = null,
        [Description("Horizontal orbit angle in degrees (0 = +X, 90 = +Y)")] double? azimuthDegrees = null,
        [Description("Vertical angle in degrees (-90 = bottom, 0 = horizon, 90 = top)")] double? elevationDegrees = null,
        [Description("Camera distance as a multiple of object size")] double distanceFactor = ViewportOrbitHelper.DefaultDistanceFactor,
        [Description("Degrees per orbit step action")] double stepDegrees = ViewportOrbitHelper.DefaultStepDegrees,
        [Description("Dolly scale per zoom step (multiplier for dolly_out, divisor for dolly_in)")] double dollyFactor = 1.25,
        [Description("When true, calls ZoomBoundingBox after aiming (overrides spherical distance; good for fitting, bad for orbit angles)")] bool fitToBounds = false,
        [Description("Turn preview on if the object supports it and preview is hidden")] bool ensurePreview = true,
        [Description("Re-solve the Grasshopper document before measuring preview bounds")] bool solve = true,
        [Description("Viewport name (optional; uses active viewport when omitted)")] string? viewportName = null,
        [Description("Output width in pixels")] int width = 1024,
        [Description("Output height in pixels")] int height = 768,
        [Description("Include grid in the capture")] bool includeGrid = false,
        [Description("Include world axes in the capture")] bool includeAxes = false,
        [Description("Use transparent background")] bool transparentBackground = false,
        [Description("Image quality for lossy encoders (1-100)")] int quality = 90,
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60,
        [Description("Include base64-encoded image data in the response")] bool includeBase64 = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(componentId), componentId));
            ValidateDimensions(width, height);
            ValidateQuality(quality);
            ViewportOrbitHelper.ClampDistanceFactor(distanceFactor);

            if (stepDegrees <= 0 || stepDegrees > 180)
            {
                throw new ArgumentOutOfRangeException(nameof(stepDegrees), "Step degrees must be between 0 (exclusive) and 180.");
            }

            if (dollyFactor <= 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(dollyFactor), "Dolly factor must be greater than 1.");
            }

            var captureResult = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document found");
                var view = ResolveViewport(doc, viewportName)
                           ?? throw new ArgumentException(
                               string.IsNullOrEmpty(viewportName)
                                   ? "Unable to locate active viewport"
                                   : $"Unable to locate viewport '{viewportName}'");

                var obj = ComponentPreviewBoundsHelper.FindDocumentObject(componentId);
                ComponentPreviewBoundsHelper.EnsurePreviewAndSolution(obj, ensurePreview, solve);

                var bounds = ComponentPreviewBoundsHelper.GetPreviewBounds(obj);
                if (!bounds.IsValid)
                {
                    throw new InvalidOperationException(
                        $"No preview geometry found for '{obj.NickName}'. Enable preview, solve the definition, or pick a component with visible geometry.");
                }

                var target = bounds.Center;
                var viewport = view.ActiveViewport;
                viewport.PushViewProjection();
                try
                {
                Point3d cameraLocation;
                double appliedAzimuth;
                double appliedElevation;
                string controlMode;

                if (!string.IsNullOrWhiteSpace(step))
                {
                    var stepCamera = viewport.CameraLocation;
                    if (ViewportOrbitHelper.NeedsInitialFrame(
                            stepCamera,
                            viewport.CameraDirection,
                            target,
                            bounds))
                    {
                        (appliedAzimuth, appliedElevation) = ViewportOrbitHelper.ResolvePreset("iso");
                        var radius = ViewportOrbitHelper.RadiusForBounds(bounds, distanceFactor);
                        stepCamera = ViewportOrbitHelper.CameraLocationFromSpherical(
                            target,
                            radius,
                            appliedAzimuth,
                            appliedElevation);
                    }

                    cameraLocation = ViewportOrbitHelper.ApplyRelativeStep(
                        target,
                        stepCamera,
                        step,
                        stepDegrees,
                        dollyFactor);
                    (appliedAzimuth, appliedElevation) = ViewportOrbitHelper.SphericalFromOffset(cameraLocation - target);
                    controlMode = "step";
                }
                else if (azimuthDegrees.HasValue || elevationDegrees.HasValue)
                {
                    appliedAzimuth = azimuthDegrees ?? ViewportOrbitHelper.ResolvePreset(viewPreset).AzimuthDegrees;
                    appliedElevation = elevationDegrees ?? ViewportOrbitHelper.ResolvePreset(viewPreset).ElevationDegrees;
                    var radius = ViewportOrbitHelper.RadiusForBounds(bounds, distanceFactor);
                    cameraLocation = ViewportOrbitHelper.CameraLocationFromSpherical(
                        target,
                        radius,
                        appliedAzimuth,
                        appliedElevation);
                    controlMode = "angles";
                }
                else
                {
                    (appliedAzimuth, appliedElevation) = ViewportOrbitHelper.ResolvePreset(
                        string.IsNullOrWhiteSpace(viewPreset) ? "iso" : viewPreset);
                    var radius = ViewportOrbitHelper.RadiusForBounds(bounds, distanceFactor);
                    cameraLocation = ViewportOrbitHelper.CameraLocationFromSpherical(
                        target,
                        radius,
                        appliedAzimuth,
                        appliedElevation);
                    controlMode = string.IsNullOrWhiteSpace(viewPreset) ? "preset_default_iso" : "preset";
                }

                viewport.SetCameraTarget(target, false);
                viewport.SetCameraLocation(cameraLocation, false);
                viewport.SetCameraDirection(target - cameraLocation, false);

                if (fitToBounds)
                {
                    viewport.ZoomBoundingBox(bounds);
                }

                view.Redraw();

                using var bitmap = CaptureBitmap(view, width, height, includeGrid, includeAxes, transparentBackground)
                                   ?? throw new InvalidOperationException(
                                       "Viewport capture returned no bitmap after orbiting. Try lowering resolution.");

                var capturePath = WriteCaptureToDisk(bitmap, view, quality);
                var metadata = BuildMetadata(view, bitmap, capturePath);
                ViewportCaptureCleanupService.RegisterForCleanup(capturePath, retentionSeconds);

                string? base64 = null;
                if (includeBase64)
                {
                    using var memoryStream = new MemoryStream();
                    bitmap.Save(memoryStream, ImageFormat.Png);
                    base64 = Convert.ToBase64String(memoryStream.ToArray());
                }

                var imageResponse = ViewportCaptureResultClaude.CreateImageResponse(base64, metadata, includeBase64);
                foreach (var content in imageResponse.Content)
                {
                    content.Annotations["componentId"] = componentId;
                    content.Annotations["nickName"] = obj.NickName ?? string.Empty;
                    content.Annotations["controlMode"] = controlMode;
                    content.Annotations["viewPreset"] = viewPreset ?? string.Empty;
                    content.Annotations["step"] = step ?? string.Empty;
                    content.Annotations["azimuthDegrees"] = appliedAzimuth;
                    content.Annotations["elevationDegrees"] = appliedElevation;
                    content.Annotations["cameraTarget"] = new { x = target.X, y = target.Y, z = target.Z };
                    content.Annotations["cameraLocation"] = new { x = cameraLocation.X, y = cameraLocation.Y, z = cameraLocation.Z };
                    content.Annotations["bounds"] = new
                    {
                        min = new { x = bounds.Min.X, y = bounds.Min.Y, z = bounds.Min.Z },
                        max = new { x = bounds.Max.X, y = bounds.Max.Y, z = bounds.Max.Z },
                    };
                }

                return new Dictionary<string, object?>
                {
                    ["success"] = true,
                    ["componentId"] = componentId,
                    ["nickName"] = obj.NickName ?? string.Empty,
                    ["previewEnabled"] = ComponentStateHelper.SupportsPreview(obj)
                        && ComponentStateHelper.GetPreviewEnabled(obj),
                    ["controlMode"] = controlMode,
                    ["viewPreset"] = viewPreset,
                    ["step"] = step,
                    ["azimuthDegrees"] = appliedAzimuth,
                    ["elevationDegrees"] = appliedElevation,
                    ["cameraTarget"] = new { x = target.X, y = target.Y, z = target.Z },
                    ["cameraLocation"] = new { x = cameraLocation.X, y = cameraLocation.Y, z = cameraLocation.Z },
                    ["fitToBounds"] = fitToBounds,
                    ["capture"] = imageResponse,
                };
                }
                finally
                {
                    viewport.PopViewProjection();
                }
            });

            return captureResult;
        }, nameof(OrbitObject));
    }

    [McpServerTool(Name = "Manage_NamedViews")]
    [Description(
        "Manage Rhino named views. action=list|restore|add|rename|delete. " +
        "Use name or index to identify views. Mutating actions (add, rename, delete) require confirm:true.")]
    public static async Task<CallToolResult> ManageNamedViews(
        [Description("Action: list, restore, add, rename, or delete")] string action,
        [Description("Named view name (lookup for restore/rename/delete; new name for add)")] string? name = null,
        [Description("New name when action=rename")] string? newName = null,
        [Description("Named view index (alternative to name)")] int? index = null,
        [Description("Target viewport name (optional; active viewport when omitted)")] string? viewportName = null,
        [Description("Must be true for add, rename, and delete")] bool? confirm = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(action), action));

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document found");
                return NamedViewHelper.Execute(doc, action, name, newName, index, viewportName, confirm);
            });

            return result;
        }, nameof(ManageNamedViews));
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width < 64 || width > 8192 || height < 64 || height > 8192)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be between 64 and 8192 pixels.");
        }
    }

    private static void ValidateQuality(int quality)
    {
        if (quality < 1 || quality > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be between 1 and 100.");
        }
    }

    private static RhinoView? ResolveViewport(RhinoDoc doc, string? viewportName)
    {
        if (string.IsNullOrWhiteSpace(viewportName))
        {
            return doc.Views.ActiveView ?? doc.Views.FirstOrDefault();
        }

        return doc.Views.FirstOrDefault(view =>
            string.Equals(view.MainViewport?.Name, viewportName, StringComparison.OrdinalIgnoreCase));
    }

    private static Bitmap? CaptureBitmap(RhinoView view, int width, int height, bool includeGrid, bool includeAxes, bool transparent)
    {
        try
        {
            var viewport = view.ActiveViewport;
            var previousGrid = viewport.ConstructionGridVisible;
            var previousAxes = viewport.WorldAxesVisible;

            viewport.ConstructionGridVisible = includeGrid;
            viewport.WorldAxesVisible = includeAxes;

            try
            {
                var capture = new ViewCapture
                {
                    Width = width,
                    Height = height,
                    ScaleScreenItems = true,
                    TransparentBackground = transparent
                };

                return capture.CaptureToBitmap(view);
            }
            finally
            {
                viewport.ConstructionGridVisible = previousGrid;
                viewport.WorldAxesVisible = previousAxes;
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[CaptureViewport] ViewCapture failed: {ex.Message}");
            return null;
        }
    }

    private static string WriteCaptureToDisk(Bitmap bitmap, RhinoView view, int quality)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Grasshopper",
            "Libraries",
            "Cassis",
            "captures");

        Directory.CreateDirectory(directory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var name = view.MainViewport?.Name ?? "Viewport";
        var filePath = Path.Combine(directory, $"viewport_{Sanitize(name)}_{timestamp}.png");

        try
        {
            // In .NET 8, ImageCodecInfo has compatibility issues, so we use direct Save with format
            // Quality parameter is only relevant for JPEG, PNG is lossless
            bitmap.Save(filePath, ImageFormat.Png);

            RhinoApp.WriteLine($"[CaptureViewport] Saved capture to {filePath}");
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[CaptureViewport] Failed to save capture: {ex.Message}");
            throw;
        }

        return filePath;
    }

    private static ViewportCaptureMetadata BuildMetadata(RhinoView view, Bitmap bitmap, string path)
    {
        long fileSize = 0;
        if (File.Exists(path))
        {
            fileSize = new FileInfo(path).Length;
        }

        return new ViewportCaptureMetadata
        {
            ViewportName = view.MainViewport?.Name ?? "Viewport",
            Width = bitmap.Width,
            Height = bitmap.Height,
            FileSize = fileSize,
            Timestamp = DateTime.UtcNow,
            FilePath = path
        };
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}
