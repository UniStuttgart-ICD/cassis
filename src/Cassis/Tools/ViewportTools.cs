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
using Grasshopper;
using Grasshopper.Kernel;
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
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60)
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
            retentionSeconds: retentionSeconds);
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
    [Description("Captures a Rhino viewport and returns it as a PNG image plus JSON metadata (the PNG is also saved to disk for retentionSeconds)")]
    public static async Task<CallToolResult> CaptureViewport(
        [Description("Viewport name (optional; uses active viewport when omitted)")] string? viewportName = null,
        [Description("Output width in pixels")] int width = 1024,
        [Description("Output height in pixels")] int height = 768,
        [Description("Include grid in the capture")] bool includeGrid = false,
        [Description("Include world axes in the capture")] bool includeAxes = false,
        [Description("Use transparent background")] bool transparentBackground = false,
        [Description("Image quality for lossy encoders (1-100)")] int quality = 90,
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60,
        [Description("Optional GH object GUIDs: temporarily zoom to their preview for this capture only (camera restored afterwards, selection unchanged)")] string[]? componentIds = null,
        [Description("Temporarily zoom extents for this capture only (camera restored afterwards)")] bool zoomExtents = false)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            ValidateDimensions(width, height);
            ValidateQuality(quality);

            var frameComponents = componentIds != null && componentIds.Length > 0;
            if (frameComponents && zoomExtents)
            {
                throw new ArgumentException("Use componentIds or zoomExtents, not both");
            }

            var captureResult = await UiThreadHelper.InvokeAsync(() =>
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document found");
                var view = ResolveViewport(doc, viewportName);
                if (view == null)
                {
                    var name = string.IsNullOrEmpty(viewportName) ? "active viewport" : $"viewport '{viewportName}'";
                    throw new ArgumentException($"Unable to locate {name}");
                }

                var viewport = view.ActiveViewport;
                var framed = frameComponents || zoomExtents;
                ComponentFocusHelper.FocusSet? focus = null;

                // Explicit snapshot: live tests showed Push/PopViewProjection does not undo ZoomBoundingBox.
                var savedCamera = framed ? new Rhino.DocObjects.ViewportInfo(viewport) : null;

                try
                {
                    if (frameComponents)
                    {
                        focus = ComponentFocusHelper.Resolve(ActiveGhDocument(), componentIds!);
                        ComponentFocusHelper.Zoom([view], focus.Bounds, ComponentFocusHelper.DefaultPadding);
                    }
                    else if (zoomExtents)
                    {
                        viewport.ZoomExtents();
                    }

                    view.Redraw();
                    using var bitmap = CaptureBitmap(view, width, height, includeGrid, includeAxes, transparentBackground);
                    if (bitmap == null)
                    {
                        throw new InvalidOperationException("Viewport capture returned no bitmap. Try lowering resolution or using the simple capture tool.");
                    }

                    var png = EncodePng(bitmap);
                    var capturePath = WriteCaptureToDisk(png, view);
                    var metadata = BuildMetadata(view, bitmap, capturePath);
                    metadata.Framing = frameComponents ? "components" : zoomExtents ? "extents" : null;
                    metadata.NotFound = focus?.NotFound.Count > 0 ? focus.NotFound : null;
                    metadata.NoPreview = focus?.NoPreview.Count > 0 ? focus.NoPreview : null;
                    ViewportCaptureCleanupService.RegisterForCleanup(capturePath, retentionSeconds);
                    return McpExtensions.CreateImageResult(png, metadata);
                }
                finally
                {
                    if (savedCamera != null)
                    {
                        viewport.SetViewProjection(savedCamera, false);
                        viewport.SetCameraTarget(savedCamera.TargetPoint, false);
                        view.Redraw();
                    }
                }
            });

            return captureResult;
        }, nameof(CaptureViewport));
    }

    [McpServerTool(Name = "Focus_Components")]
    [Description(
        "Selects Grasshopper objects by GUID (their preview turns the selection colour) and zooms Rhino viewports to their preview geometry. " +
        "Changes the canvas selection and the camera; call Capture_Viewport afterwards to see the result.")]
    public static async Task<CallToolResult> FocusComponents(
        [Description("Grasshopper object GUIDs to focus")] string[] componentIds,
        [Description("Viewport names to zoom; omit for the active viewport, [\"*\"] for all viewports")] string[]? viewports = null,
        [Description("Replace the canvas selection with these objects so their preview is highlighted")] bool select = true,
        [Description("Zoom the viewports to the objects' preview bounds")] bool zoom = true,
        [Description("Margin around the bounds as a fraction of their diagonal")] double padding = ComponentFocusHelper.DefaultPadding)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            if (componentIds == null || componentIds.Length == 0)
            {
                throw new ArgumentException("componentIds must contain at least one GUID");
            }

            McpExtensions.ValidateRange(nameof(padding), padding, 0, 10);

            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("No active Rhino document found");
                var ghDoc = ActiveGhDocument();
                var views = zoom ? ResolveViews(doc, viewports) : new List<RhinoView>();
                var focus = ComponentFocusHelper.Resolve(ghDoc, componentIds);

                if (select)
                {
                    ComponentFocusHelper.Select(ghDoc, focus.Found);
                }

                if (zoom)
                {
                    ComponentFocusHelper.Zoom(views, focus.Bounds, padding);
                }

                doc.Views.Redraw();

                return new
                {
                    success = true,
                    focused = focus.Found.Select(o => new
                    {
                        id = o.InstanceGuid.ToString(),
                        nick = o.NickName ?? string.Empty,
                        previewHidden = o is IGH_PreviewObject p && p.Hidden,
                    }).ToList(),
                    notFound = focus.NotFound,
                    noPreview = focus.NoPreview,
                    bounds = BoundsJson(focus.Bounds),
                    viewports = views.Select(v => v.MainViewport?.Name).ToList(),
                    selected = select,
                    zoomed = zoom,
                };
            });
        }, nameof(FocusComponents));
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
        [Description("Retention window in seconds before the on-disk capture is cleaned up")] int retentionSeconds = 60)
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

                var png = EncodePng(bitmap);
                var capturePath = WriteCaptureToDisk(png, view);
                var metadata = BuildMetadata(view, bitmap, capturePath);
                ViewportCaptureCleanupService.RegisterForCleanup(capturePath, retentionSeconds);

                return McpExtensions.CreateImageResult(png, new Dictionary<string, object?>
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
                    ["bounds"] = BoundsJson(bounds),
                    ["capture"] = metadata,
                });
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
        if (width < 64 || width > 2576 || height < 64 || height > 2576)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be between 64 and 2576 pixels (larger images exceed model image limits).");
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

    private static List<RhinoView> ResolveViews(RhinoDoc doc, string[]? viewportNames)
    {
        if (viewportNames == null || viewportNames.Length == 0)
        {
            return [ResolveViewport(doc, null) ?? throw new ArgumentException("Unable to locate active viewport")];
        }

        if (viewportNames.Contains("*"))
        {
            return doc.Views.ToList();
        }

        return viewportNames
            .Select(name => ResolveViewport(doc, name) ?? throw new ArgumentException($"Unable to locate viewport '{name}'"))
            .ToList();
    }

    private static object BoundsJson(BoundingBox bounds) => new
    {
        min = new { x = bounds.Min.X, y = bounds.Min.Y, z = bounds.Min.Z },
        max = new { x = bounds.Max.X, y = bounds.Max.Y, z = bounds.Max.Z },
    };

    private static GH_Document ActiveGhDocument() =>
        Instances.ActiveCanvas?.Document ?? throw new InvalidOperationException("No active Grasshopper document");

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

    private static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static string WriteCaptureToDisk(byte[] png, RhinoView view)
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
            File.WriteAllBytes(filePath, png);

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
