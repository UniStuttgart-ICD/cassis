using System;
using System.Threading;
using System.Threading.Tasks;
using Rhino;

namespace GrasshopperMCP.Utilities;

/// <summary>
/// Marshals Grasshopper/Rhino work onto the UI thread with deadlock avoidance and timeouts.
/// </summary>
public static class UiThreadHelper
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim UiGate = new(1, 1);

    /// <summary>
    /// Executes the provided function on the UI thread and returns its result.
    /// </summary>
    public static async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await UiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DispatchAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UiGate.Release();
        }
    }

    /// <summary>
    /// Executes the provided asynchronous function on the UI thread and returns its result.
    /// </summary>
    public static async Task<T> InvokeAsync<T>(Func<Task<T>> asyncAction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await UiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!RhinoApp.InvokeRequired)
            {
                return await asyncAction().ConfigureAwait(true);
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = asyncAction().ConfigureAwait(true).GetAwaiter().GetResult();
                    tcs.SetResult(result);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));

            using var timeoutCts = new CancellationTokenSource(DispatchTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                return await AwaitWithCancellation(tcs.Task, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                RhinoApp.WriteLine(
                    $"[MCP WARN] UI dispatch timed out after {DispatchTimeout.TotalSeconds:F0}s (async). " +
                    "Rhino may be blocked by a modal dialog or a long solve.");
                throw new TimeoutException($"UI async dispatch timed out after {DispatchTimeout.TotalSeconds:F0}s.");
            }
        }
        finally
        {
            UiGate.Release();
        }
    }

    /// <summary>
    /// Executes the provided action on the UI thread.
    /// </summary>
    public static async Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await UiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DispatchAsync(
                () =>
                {
                    action();
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UiGate.Release();
        }
    }

    /// <summary>
    /// Writes a message to the Rhino console, switching to the UI thread if needed.
    /// </summary>
    public static void LogToConsole(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            if (RhinoApp.InvokeRequired)
            {
                RhinoApp.InvokeOnUiThread((Action)(() => RhinoApp.WriteLine(message)));
            }
            else
            {
                RhinoApp.WriteLine(message);
            }
        }
        catch
        {
            RhinoApp.WriteLine(message);
        }
    }

    private static async Task<T> DispatchAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        if (!RhinoApp.InvokeRequired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationName = action.Method?.Name ?? "anonymous";

        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.SetResult(action());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }));

        using var timeoutCts = new CancellationTokenSource(DispatchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await AwaitWithCancellation(tcs.Task, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            RhinoApp.WriteLine(
                $"[MCP WARN] UI dispatch timed out after {DispatchTimeout.TotalSeconds:F0}s ({operationName}). " +
                "Rhino may be blocked by a modal dialog or a long solve.");
            throw new TimeoutException(
                $"UI dispatch timed out after {DispatchTimeout.TotalSeconds:F0}s for '{operationName}'.");
        }
    }

    private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
#else
        var cancellationTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancellationTask.TrySetCanceled(cancellationToken)))
        {
            var completedTask = await Task.WhenAny(task, cancellationTask.Task).ConfigureAwait(false);
            if (completedTask != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
#endif
    }
}
