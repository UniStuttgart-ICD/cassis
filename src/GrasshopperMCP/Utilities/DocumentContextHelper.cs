using System.IO;
using Grasshopper;
using Grasshopper.Kernel;

namespace GrasshopperMCP.Utilities;

/// <summary>
/// Minimal active-vs-Cassis-host snapshot for MCP responses.
/// </summary>
internal static class DocumentContextHelper
{
    /// <summary>One-line targeting rule for tool descriptions (not repeated in every payload).</summary>
    public const string TargetingNote =
        "Tools edit the active canvas doc; Cassis MCP dies only if its host .gh is closed.";

    /// <summary>
    /// Compact context. Omits the targeting note (lives in tool descriptions).
    /// Only includes tip when active ≠ Cassis host.
    /// </summary>
    public static object BuildContext()
    {
        var active = Instances.ActiveCanvas?.Document;
        string? hostFile = null;
        var hostCount = 0;
        var onHost = false;

        var server = Instances.DocumentServer;
        if (server != null)
        {
            foreach (GH_Document doc in server)
            {
                if (doc == null || !ContainsCassis(doc))
                {
                    continue;
                }

                hostCount++;
                var file = DocLabel(doc);
                if (ReferenceEquals(doc, active))
                {
                    onHost = true;
                    hostFile = file;
                }
                else
                {
                    hostFile ??= file;
                }
            }
        }

        // Only emit tip when it matters (saves tokens on the common case).
        string? tip = null;
        if (hostCount > 0 && !onHost)
        {
            tip = "active≠cassis-host";
        }
        else if (hostCount == 0)
        {
            tip = "no-cassis-host";
        }

        return new
        {
            active = DocLabel(active),
            host = hostFile,
            onHost,
            tip
        };
    }

    public static string? DocLabel(GH_Document? doc)
    {
        if (doc == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(doc.FilePath))
        {
            return Path.GetFileName(doc.FilePath);
        }

        return doc.DisplayName;
    }

    public static bool ContainsCassis(GH_Document doc)
    {
        foreach (var obj in doc.Objects)
        {
            if (obj is McpListenerComponent)
            {
                return true;
            }
        }

        return false;
    }

    public static bool ActiveDocumentHostsCassis()
    {
        var doc = Instances.ActiveCanvas?.Document;
        return doc != null && ContainsCassis(doc);
    }
}
