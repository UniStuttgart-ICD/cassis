using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Cassis.Utilities;

/// <summary>
/// PNG encoding for images returned to MCP clients.
/// </summary>
internal static class ImageEncoding
{
    /// <summary>Longest edge Claude models accept without downscaling.</summary>
    internal const int MaxEdge = 2576;

    /// <summary>Encodes as PNG, downscaling first if the longest edge exceeds <see cref="MaxEdge"/>.</summary>
    internal static byte[] EncodePng(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(bitmap.Width, bitmap.Height));
        if (scale < 1.0)
        {
            using var scaled = new Bitmap(bitmap, (int)(bitmap.Width * scale), (int)(bitmap.Height * scale));
            scaled.Save(stream, ImageFormat.Png);
        }
        else
        {
            bitmap.Save(stream, ImageFormat.Png);
        }

        return stream.ToArray();
    }

    /// <summary>Saves a PNG only when the caller named a path, and returns that path. The file is never auto-deleted.</summary>
    internal static string? SavePng(Bitmap bitmap, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var path = Path.GetExtension(filePath!) switch
        {
            ".png" => filePath!,
            "" => filePath + ".png",
            _ => Path.ChangeExtension(filePath!, ".png")
        };

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
