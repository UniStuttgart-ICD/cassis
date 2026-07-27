using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Rhino;
using Timer = System.Threading.Timer;

namespace Cassis.Services;

/// <summary>
/// Tracks viewport capture files and cleans them up after a retention period to prevent disk buildup.
/// </summary>
public static class ViewportCaptureCleanupService
{
    private static readonly ConcurrentDictionary<string, DateTime> _trackedFiles = new ConcurrentDictionary<string, DateTime>();
    private static readonly object _timerLock = new object();
    private static Timer? _cleanupTimer;

    private const int DefaultRetentionSeconds = 60;
    private const int CleanupIntervalSeconds = 30;

    /// <summary>
    /// Registers a file to be deleted automatically after the provided retention window.
    /// </summary>
    public static void RegisterForCleanup(string? filePath, int retentionSeconds = DefaultRetentionSeconds)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var expiration = DateTime.UtcNow.AddSeconds(retentionSeconds);
        _trackedFiles[filePath] = expiration;
        RhinoApp.WriteLine($"[ViewportCleanupService] Tracking {Path.GetFileName(filePath)} until {expiration:HH:mm:ss}");
        EnsureTimer();
    }

    /// <summary>
    /// Returns the expiration timestamp for a tracked file if available.
    /// </summary>
    public static DateTime? GetExpirationTime(string filePath)
    {
        return _trackedFiles.TryGetValue(filePath, out var expiration) ? expiration : null;
    }

    /// <summary>
    /// Stops tracking the provided file without deleting it.
    /// </summary>
    public static void UnregisterFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        _trackedFiles.TryRemove(filePath, out _);
    }

    /// <summary>
    /// Number of files currently scheduled for cleanup.
    /// </summary>
    public static int TrackedFileCount => _trackedFiles.Count;

    /// <summary>
    /// Disposes the cleanup timer and immediately deletes any expired files.
    /// </summary>
    public static void Shutdown()
    {
        lock (_timerLock)
        {
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;
        }

        CleanupExpiredFiles(null);
    }

    private static void EnsureTimer()
    {
        lock (_timerLock)
        {
            if (_cleanupTimer != null)
            {
                return;
            }

            _cleanupTimer = new Timer(
                CleanupExpiredFiles,
                state: null,
                dueTime: TimeSpan.FromSeconds(CleanupIntervalSeconds),
                period: TimeSpan.FromSeconds(CleanupIntervalSeconds));
        }
    }

    private static void CleanupExpiredFiles(object? _)
    {
        var now = DateTime.UtcNow;
        var expired = new List<string>();

        foreach (var kvp in _trackedFiles)
        {
            if (now >= kvp.Value)
            {
                expired.Add(kvp.Key);
            }
        }

        foreach (var filePath in expired)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    File.Delete(filePath);
                    RhinoApp.WriteLine($"[ViewportCleanupService] Deleted expired capture {Path.GetFileName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[ViewportCleanupService] Failed to delete {filePath}: {ex.Message}");
            }
            finally
            {
                DateTime removed;
                _trackedFiles.TryRemove(filePath, out removed);
            }
        }
    }
}
