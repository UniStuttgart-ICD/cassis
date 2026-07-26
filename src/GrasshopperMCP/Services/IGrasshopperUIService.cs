using System;
using System.Threading;
using System.Threading.Tasks;

namespace GrasshopperMCP.Services;

/// <summary>
/// Service for managing UI thread operations in Grasshopper.
/// Provides a testable abstraction over RhinoApp.InvokeOnUiThread.
/// </summary>
public interface IGrasshopperUIService
{
    /// <summary>
    /// Executes an operation on the UI thread and returns a result.
    /// </summary>
    /// <typeparam name="T">The type of result returned by the operation</typeparam>
    /// <param name="operation">The operation to execute on the UI thread</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation with its result</returns>
    Task<T> InvokeOnUIThreadAsync<T>(Func<T> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an action on the UI thread without returning a result.
    /// </summary>
    /// <param name="action">The action to execute on the UI thread</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    Task InvokeOnUIThreadAsync(Action action, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation on the UI thread that may throw exceptions, 
    /// and handles them by returning a result wrapper.
    /// </summary>
    /// <typeparam name="TResult">The type of successful result</typeparam>
    /// <typeparam name="TError">The type of error result</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="errorHandler">Function to create error result from exception</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Either the successful result or an error result</returns>
    Task<TResult> SafeInvokeOnUIThreadAsync<TResult, TError>(
        Func<TResult> operation,
        Func<Exception, TResult> errorHandler,
        CancellationToken cancellationToken = default);
}