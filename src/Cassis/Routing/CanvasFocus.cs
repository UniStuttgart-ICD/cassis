using System;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using ModelContextProtocol.Protocol;
using Rhino;

namespace Cassis.Routing;

/// <summary>
/// Points the canvas at one document for a single tool call, then restores it.
/// </summary>
internal static class CanvasFocus
{
    // shortcut: one routed call at a time per Rhino, upgrade if calls must overlap
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<JsonRpcMessage> RunAsync(string documentPath, Func<Task<JsonRpcMessage>> action)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = new FocusState();
            await Utilities.UiThreadHelper.InvokeAsync(() => state.Switch(documentPath)).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                if (state.Switched)
                {
                    try
                    {
                        await Utilities.UiThreadHelper.InvokeAsync(() => state.Restore()).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"[Cassis WARN] Could not restore the canvas: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static InstanceSnapshot Capture(int pid, int port)
    {
        var snapshot = new InstanceSnapshot
        {
            Pid = pid,
            Port = port,
            Utc = DateTime.UtcNow,
        };

        var server = Instances.DocumentServer;
        if (server == null)
        {
            return snapshot;
        }

        var active = Instances.ActiveCanvas?.Document;
        foreach (GH_Document document in server)
        {
            if (document == null || string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            snapshot.Paths.Add(document.FilePath);
            if (ReferenceEquals(document, active))
            {
                snapshot.ActivePath = document.FilePath;
            }
        }

        return snapshot;
    }

    private static GH_Document? Find(string documentPath)
    {
        var server = Instances.DocumentServer;
        if (server == null)
        {
            return null;
        }

        var wanted = InstanceMatcher.Normalize(documentPath);
        foreach (GH_Document document in server)
        {
            if (document == null || string.IsNullOrWhiteSpace(document.FilePath))
            {
                continue;
            }

            if (string.Equals(InstanceMatcher.Normalize(document.FilePath), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    private sealed class FocusState
    {
        private GH_Document? _previous;

        public bool Switched { get; private set; }

        public void Switch(string documentPath)
        {
            var canvas = Instances.ActiveCanvas ?? throw new InvalidOperationException("Grasshopper has no active canvas.");
            var target = Find(documentPath) ?? throw new InvalidOperationException($"Grasshopper file is not open: {documentPath}");
            _previous = canvas.Document;
            if (ReferenceEquals(_previous, target))
            {
                return;
            }

            canvas.Document = target;
            Switched = true;
        }

        public void Restore()
        {
            var canvas = Instances.ActiveCanvas;
            if (canvas == null || _previous == null)
            {
                return;
            }

            canvas.Document = _previous;
        }
    }
}
