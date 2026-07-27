using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Rhino;

namespace Cassis.Services;

/// <summary>
/// Implementation of IGrasshopperUIService that provides safe UI thread operations.
/// </summary>
public class GrasshopperUIService : IGrasshopperUIService
{
    private readonly ILogger<GrasshopperUIService> _logger;
    private static readonly TimeSpan UiThreadDispatchTimeout = TimeSpan.FromSeconds(30);

    public GrasshopperUIService(ILogger<GrasshopperUIService> logger)
    {
        _logger = logger;
    }

    public async Task<T> InvokeOnUIThreadAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check if we're already on the UI thread
        if (RhinoApp.InvokeRequired == false)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing operation on UI thread (direct)");
                throw;
            }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationName = operation.Method?.Name ?? "anonymous_func";
        _logger.LogDebug("UI dispatch queued: {OperationName}", operationName);

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                _logger.LogDebug("UI dispatch started: {OperationName}", operationName);
                cancellationToken.ThrowIfCancellationRequested();
                var result = operation();
                tcs.SetResult(result);
                _logger.LogDebug("UI dispatch completed: {OperationName}", operationName);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
                _logger.LogInformation("UI dispatch cancelled while running: {OperationName}", operationName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing operation on UI thread");
                tcs.TrySetException(ex);
            }
        });

        return await AwaitWithCancellationAndTimeout(tcs.Task, operationName, cancellationToken).ConfigureAwait(false);
    }

    public async Task InvokeOnUIThreadAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check if we're already on the UI thread
        if (RhinoApp.InvokeRequired == false)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing action on UI thread (direct)");
                throw;
            }
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionName = action.Method?.Name ?? "anonymous_action";
        _logger.LogDebug("UI dispatch queued: {ActionName}", actionName);

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                _logger.LogDebug("UI dispatch started: {ActionName}", actionName);
                cancellationToken.ThrowIfCancellationRequested();
                action();
                tcs.SetResult(true);
                _logger.LogDebug("UI dispatch completed: {ActionName}", actionName);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
                _logger.LogInformation("UI dispatch cancelled while running: {ActionName}", actionName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing action on UI thread");
                tcs.TrySetException(ex);
            }
        });

        await AwaitWithCancellationAndTimeout(tcs.Task, actionName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> SafeInvokeOnUIThreadAsync<TResult, TError>(
        Func<TResult> operation,
        Func<Exception, TResult> errorHandler,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check if we're already on the UI thread
        if (RhinoApp.InvokeRequired == false)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in safe UI thread operation (direct), handling gracefully");
                try
                {
                    return errorHandler(ex);
                }
                catch (Exception errorHandlerEx)
                {
                    _logger.LogError(errorHandlerEx, "Error in error handler for UI thread operation (direct)");
                    throw;
                }
            }
        }

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationName = operation.Method?.Name ?? "anonymous_safe_func";
        _logger.LogDebug("UI dispatch queued: {OperationName}", operationName);

        RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                _logger.LogDebug("UI dispatch started: {OperationName}", operationName);
                cancellationToken.ThrowIfCancellationRequested();
                var result = operation();
                tcs.SetResult(result);
                _logger.LogDebug("UI dispatch completed: {OperationName}", operationName);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
                _logger.LogInformation("UI dispatch cancelled while running: {OperationName}", operationName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in safe UI thread operation, handling gracefully");
                try
                {
                    var errorResult = errorHandler(ex);
                    tcs.SetResult(errorResult);
                }
                catch (Exception errorHandlerEx)
                {
                    _logger.LogError(errorHandlerEx, "Error in error handler for UI thread operation");
                    tcs.TrySetException(errorHandlerEx);
                }
            }
        });

        return await AwaitWithCancellationAndTimeout(tcs.Task, operationName, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> AwaitWithCancellationAndTimeout<T>(Task<T> task, string operationName, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(UiThreadDispatchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await AwaitWithCancellation(task, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("UI dispatch timed out after {TimeoutSeconds}s: {OperationName}", UiThreadDispatchTimeout.TotalSeconds, operationName);
            throw new TimeoutException($"UI dispatch timed out after {UiThreadDispatchTimeout.TotalSeconds:F0}s for '{operationName}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("UI dispatch cancelled before completion: {OperationName}", operationName);
            throw;
        }
    }

    private async Task AwaitWithCancellationAndTimeout(Task task, string operationName, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(UiThreadDispatchTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await AwaitWithCancellation(task, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("UI dispatch timed out after {TimeoutSeconds}s: {OperationName}", UiThreadDispatchTimeout.TotalSeconds, operationName);
            throw new TimeoutException($"UI dispatch timed out after {UiThreadDispatchTimeout.TotalSeconds:F0}s for '{operationName}'.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("UI dispatch cancelled before completion: {OperationName}", operationName);
            throw;
        }
    }

    private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
    {
#if NET6_0_OR_GREATER
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
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

        await task.ConfigureAwait(false);
#endif
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