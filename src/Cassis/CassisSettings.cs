using System;
using System.IO;
using Rhino;

namespace Cassis;

/// <summary>
/// User preferences for Cassis (persisted under Application Support / AppData).
/// </summary>
public static class CassisSettings
{
    private const string FileName = "settings.json";
    private static readonly object Gate = new();
    private static bool _loaded;
    private static bool _autoStart = true;

    /// <summary>
    /// When true, MCP starts as soon as Grasshopper loads the Cassis plugin.
    /// Turning this off does not stop a running server; it only skips the next GH load auto-start.
    /// </summary>
    public static bool AutoStart
    {
        get
        {
            EnsureLoaded();
            lock (Gate)
            {
                return _autoStart;
            }
        }
        set
        {
            EnsureLoaded();
            lock (Gate)
            {
                if (_autoStart == value)
                {
                    return;
                }

                _autoStart = value;
            }

            Save();
        }
    }

    public static string SettingsDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "Cassis");
        }
    }

    public static string SettingsPath => Path.Combine(SettingsDirectory, FileName);

    internal static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return;
                }

                var text = File.ReadAllText(SettingsPath);
                _autoStart = ParseAutoStart(text, defaultValue: true);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"[Cassis WARN] Could not load settings: {ex.Message}");
            }
        }
    }

    internal static void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            bool autoStart;
            lock (Gate)
            {
                autoStart = _autoStart;
            }

            var json = autoStart
                ? "{\"autoStart\":true}\n"
                : "{\"autoStart\":false}\n";
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"[Cassis WARN] Could not save settings: {ex.Message}");
        }
    }

    /// <summary>Minimal parser so early load does not depend on System.Text.Json.</summary>
    internal static bool ParseAutoStart(string json, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultValue;
        }

        var key = "\"autoStart\"";
        var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return defaultValue;
        }

        var colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0)
        {
            return defaultValue;
        }

        var slice = json.Substring(colon + 1).TrimStart();
        if (slice.StartsWith("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (slice.StartsWith("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return defaultValue;
    }
}
