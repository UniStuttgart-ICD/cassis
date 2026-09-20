using System.IO;
using Grasshopper;
using Grasshopper.Kernel;

namespace Cassis.Utilities;

/// <summary>
/// Minimal active-vs-Cassis-component snapshot for MCP responses.
/// </summary>
internal static class DocumentContextHelper
{
    /// <summary>One-line targeting rule for tool descriptions (not repeated in every payload).</summary>
    public const string TargetingNote =
        "Tools edit the active canvas doc; MCP runs with Grasshopper and does not require a Cassis component.";

    /// <summary>
    /// Compact context. Omits the targeting note (lives in tool descriptions).
    /// Only includes tip when active ≠ a doc that has a Cassis component.
    /// </summary>
    public static object BuildContext()
    {
        var active = Instances.ActiveCanvas?.Document;
        string? panelFile = null;
        var panelCount = 0;
        var onPanel = false;

        var server = Instances.DocumentServer;
        if (server != null)
        {
            foreach (GH_Document doc in server)
            {
                if (doc == null || !ContainsCassis(doc))
                {
                    continue;
                }

                panelCount++;
                var file = DocLabel(doc);
                if (ReferenceEquals(doc, active))
                {
                    onPanel = true;
                    panelFile = file;
                }
                else
                {
                    panelFile ??= file;
                }
            }
        }

        string? tip = null;
        if (panelCount > 0 && !onPanel)
        {
            tip = "active≠cassis-panel";
        }

        // Keep host/onHost keys for existing clients; value is an optional Cassis UI panel doc.
        return new
        {
            active = DocLabel(active),
            host = panelFile,
            onHost = onPanel,
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
