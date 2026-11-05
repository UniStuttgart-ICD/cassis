using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Cassis.Routing;

internal enum InstanceMatchKind
{
    Ok,
    MissingHeader,
    UnexpandedHeader,
    NoFile,
    TwoRhinos,
    TwoFiles,
}

internal sealed class InstanceSnapshot
{
    public int Pid { get; set; }

    public int Port { get; set; }

    public string? ActivePath { get; set; }

    public List<string> Paths { get; set; } = new();

    public DateTime Utc { get; set; }
}

internal sealed class InstanceMatchResult
{
    public InstanceMatchKind Kind { get; set; }

    public int Pid { get; set; }

    public int Port { get; set; }

    public string? DocumentPath { get; set; }

    public string Detail { get; set; } = string.Empty;
}

/// <summary>
/// Picks the Rhino whose saved Grasshopper file sits inside the project folder.
/// </summary>
internal static class InstanceMatcher
{
    internal const string HeaderName = "X-Cassis-Root";
    internal const string UnexpandedHeader = "${workspaceFolder}";
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    public static InstanceMatchResult Match(string? header, IReadOnlyList<InstanceSnapshot> instances)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Fail(InstanceMatchKind.MissingHeader, "X-Cassis-Root is missing. Set it to the project folder.");
        }

        var root = header.Trim();
        if (root.IndexOf("${", StringComparison.Ordinal) >= 0)
        {
            return Fail(
                InstanceMatchKind.UnexpandedHeader,
                "X-Cassis-Root is still ${workspaceFolder}. The client did not expand the project folder.");
        }

        var hits = new List<(InstanceSnapshot Snapshot, string Path)>();
        foreach (var snapshot in instances)
        {
            foreach (var path in snapshot.Paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (IsInside(path, root))
                {
                    hits.Add((snapshot, path));
                }
            }
        }

        var byPid = hits.GroupBy(hit => hit.Snapshot.Pid).ToList();
        if (byPid.Count == 0)
        {
            return Fail(
                InstanceMatchKind.NoFile,
                $"No open Grasshopper file is inside {root}. Save the file into that folder.");
        }

        if (byPid.Count > 1)
        {
            var pids = string.Join(", ", byPid.Select(group => group.Key.ToString(CultureInfo.InvariantCulture)));
            return Fail(
                InstanceMatchKind.TwoRhinos,
                $"More than one Rhino has a file inside {root}. Pids: {pids}.");
        }

        var group = byPid[0];
        var files = group.Select(hit => hit.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var instance = group.First().Snapshot;
        if (files.Count == 1)
        {
            return Ok(instance, files[0]);
        }

        var active = instance.ActivePath;
        if (!string.IsNullOrWhiteSpace(active)
            && files.Any(path => PathsEqual(path, active)))
        {
            return Ok(instance, active!);
        }

        return Fail(
            InstanceMatchKind.TwoFiles,
            $"More than one Grasshopper file is inside {root}: {string.Join(", ", files)}.");
    }

    internal static string Normalize(string path)
    {
        var text = path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (text.Length > 1)
        {
            text = text.TrimEnd(Path.DirectorySeparatorChar);
        }

        return text;
    }

    private static bool IsInside(string filePath, string root)
    {
        var file = Normalize(filePath);
        var folder = Normalize(root);
        var prefix = folder + Path.DirectorySeparatorChar;
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static InstanceMatchResult Ok(InstanceSnapshot instance, string path)
    {
        return new InstanceMatchResult
        {
            Kind = InstanceMatchKind.Ok,
            Pid = instance.Pid,
            Port = instance.Port,
            DocumentPath = path,
            Detail = path,
        };
    }

    private static InstanceMatchResult Fail(InstanceMatchKind kind, string detail)
    {
        return new InstanceMatchResult { Kind = kind, Detail = detail };
    }
}

internal static class InstanceFiles
{
    public static void Write(string settingsDirectory, InstanceSnapshot snapshot)
    {
        var directory = InstancesDirectory(settingsDirectory);
        Directory.CreateDirectory(directory);
        var path = FilePath(settingsDirectory, snapshot.Pid);
        var json = JsonSerializer.Serialize(snapshot);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, Encoding.UTF8);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(temporary, path);
    }

    public static void Delete(string settingsDirectory, int pid)
    {
        var path = FilePath(settingsDirectory, pid);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static IReadOnlyList<InstanceSnapshot> ReadLive(
        string settingsDirectory,
        DateTime utcNow,
        Func<int, bool> isAlive,
        TimeSpan staleAfter)
    {
        var directory = InstancesDirectory(settingsDirectory);
        if (!Directory.Exists(directory))
        {
            return Array.Empty<InstanceSnapshot>();
        }

        var live = new List<InstanceSnapshot>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            InstanceSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<InstanceSnapshot>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                continue;
            }

            if (snapshot == null || snapshot.Pid <= 0 || snapshot.Port <= 0)
            {
                continue;
            }

            if (utcNow - AsUtc(snapshot.Utc) > staleAfter)
            {
                continue;
            }

            if (!isAlive(snapshot.Pid))
            {
                continue;
            }

            live.Add(snapshot);
        }

        return live;
    }

    private static DateTime AsUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string InstancesDirectory(string settingsDirectory)
    {
        return Path.Combine(settingsDirectory, "instances");
    }

    private static string FilePath(string settingsDirectory, int pid)
    {
        return Path.Combine(InstancesDirectory(settingsDirectory), pid.ToString(CultureInfo.InvariantCulture) + ".json");
    }
}

internal static class FrontDoorLock
{
    public static bool TryAcquire(string settingsDirectory, int pid, Func<int, bool> isAlive)
    {
        Directory.CreateDirectory(settingsDirectory);
        var path = LockPath(settingsDirectory);
        try
        {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128, leaveOpen: true);
            var text = reader.ReadToEnd();
            var owner = 0;
            if (!string.IsNullOrWhiteSpace(text))
            {
                int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out owner);
            }

            if (owner != 0 && owner != pid && isAlive(owner))
            {
                return false;
            }

            stream.SetLength(0);
            stream.Position = 0;
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 128, leaveOpen: true);
            writer.Write(pid.ToString(CultureInfo.InvariantCulture));
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void Release(string settingsDirectory, int pid)
    {
        var path = LockPath(settingsDirectory);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 128, leaveOpen: true);
            var text = reader.ReadToEnd();
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var owner) || owner != pid)
            {
                return;
            }

            stream.SetLength(0);
        }
        catch (IOException)
        {
            // The next heartbeat takes the lock when this pid is dead.
        }
    }

    private static string LockPath(string settingsDirectory)
    {
        return Path.Combine(settingsDirectory, "front-door.lock");
    }
}
