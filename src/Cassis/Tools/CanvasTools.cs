using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Cassis.Extensions;
using Cassis.Utilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Rhino;

namespace Cassis.Tools;

/// <summary>
/// Tools for exporting Grasshopper canvases to disk for downstream LLM consumption.
/// </summary>
[McpServerToolType]
public static class CanvasTools
{
    [McpServerTool(Name = "Capture_Canvas")]
    [Description("Compatibility alias: returns the currently visible Grasshopper canvas as an image. Saves a PNG only when filePath is given")]
    public static Task<CallToolResult> CaptureCanvas(
        [Description("Optional path to also save a PNG; the file is kept")] string? filePath = null)
    {
        // Delegate to existing implementation to preserve behavior
        return SaveVisibleCanvas(filePath);
    }

    [McpServerTool(Name = "Save_Visible_Canvas")]
    [Description("Returns the currently visible Grasshopper canvas as an image. Saves a PNG only when filePath is given")]
    public static async Task<CallToolResult> SaveVisibleCanvas(
        [Description("Optional path to also save a PNG; the file is kept")] string? filePath = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var canvas = Instances.ActiveCanvas ?? throw new InvalidOperationException("No active Grasshopper canvas.");

                using var bitmap = canvas.GetCanvasScreenBuffer(GH_CanvasMode.Export);
                var savedPath = ImageEncoding.SavePng(bitmap, filePath);
                return McpExtensions.CreateImageResult(
                    ImageEncoding.EncodePng(bitmap),
                    new { success = true, file = savedPath, width = bitmap.Width, height = bitmap.Height });
            });

            return result;
        }, nameof(SaveVisibleCanvas));
    }

    [McpServerTool(Name = "Save_HiRes_Canvas")]
    [Description("Exports a high-resolution tiled image of the entire Grasshopper definition")]
    public static async Task<CallToolResult> SaveHiResCanvas(
        [Description("Output folder to write tiles (defaults to Desktop/GrasshopperHiRes)")] string? outputFolder = null,
        [Description("Base filename without extension (defaults to timestamp)")] string? fileNameWithoutExtension = null,
        [Description("Zoom factor applied during export (1.0 = 100%)")] double zoom = 1.0)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            if (zoom <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(zoom), "Zoom must be greater than zero.");
            }

            var result = await UiThreadHelper.InvokeAsync(() =>
            {
                var canvas = Instances.ActiveCanvas ?? throw new InvalidOperationException("No active Grasshopper canvas.");
                var document = canvas.Document ?? throw new InvalidOperationException("No active Grasshopper document.");

                var folder = ResolveHiResFolder(outputFolder);
                Directory.CreateDirectory(folder);

                var baseName = string.IsNullOrWhiteSpace(fileNameWithoutExtension)
                    ? $"GrasshopperCanvas_{DateTime.Now:yyyyMMdd_HHmmss}"
                    : fileNameWithoutExtension!;

                var bounds = document.BoundingBox(false);
                var exportRect = Rectangle.Round(bounds);

                var settings = new GH_Canvas.GH_ImageSettings
                {
                    Folder = folder,
                    Zoom = (float)zoom
                };

                var imageSettingsType = settings.GetType();
                var filenameProperty = imageSettingsType.GetProperty("Name") ??
                                       imageSettingsType.GetProperty("Filename") ??
                                       imageSettingsType.GetProperty("FileName");
                filenameProperty?.SetValue(settings, baseName);

                Size totalSize;
                var files = canvas.GenerateHiResImage(exportRect, settings, out totalSize) ?? Enumerable.Empty<string>();

                return new
                {
                    success = true,
                    folder,
                    baseName,
                    zoom,
                    totalSize = new { width = totalSize.Width, height = totalSize.Height },
                    files = files.ToArray()
                };
            });

            return result;
        }, nameof(SaveHiResCanvas));
    }

    private static string ResolveHiResFolder(string? outputFolder)
    {
        if (!string.IsNullOrWhiteSpace(outputFolder))
        {
            return outputFolder;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "GrasshopperHiRes");
    }
}
