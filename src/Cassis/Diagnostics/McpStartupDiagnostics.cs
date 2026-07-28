using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Cassis.Diagnostics;

internal static class McpStartupDiagnostics
{
    internal readonly record struct McpStartupReportResult(bool Written, string Detail);

    private static readonly string[] InterestingAssemblyPrefixes =
    {
        "Cassis",
        "Grasshopper",
        "GH_IO",
        "Microsoft.Extensions.",
        "ModelContextProtocol",
        "Rhino",
        "System.IO.Pipelines",
        "System.Net.ServerSentEvents",
        "System.Text.Encodings.Web",
        "System.Text.Json",
    };

    private static readonly string[] ExpectedPluginFiles =
    {
        "Cassis.gha",
        "Microsoft.Extensions.AI.Abstractions.dll",
        "Microsoft.Extensions.DependencyInjection.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "ModelContextProtocol.dll",
        "ModelContextProtocol.Core.dll",
        "ModelContextProtocol.HttpListener.dll",
        "System.Text.Json.dll",
    };

    public static McpStartupReportResult WriteReport(Exception exception, string phase, string? prefix)
    {
        var fileName = $"cassis_mcp_report_{DateTime.UtcNow:yyyyMMddTHHmmssfff}_{Guid.NewGuid():N}.txt";
        var path = Path.Combine(Path.GetTempPath(), fileName);

        try
        {
            var report = RedactUserProfile(BuildReport(exception, phase, prefix));
            File.WriteAllText(path, report, Encoding.UTF8);
            return new McpStartupReportResult(true, path);
        }
        catch (Exception writeException)
        {
            return new McpStartupReportResult(
                false,
                $"Failed to write startup report to {path}: {writeException}");
        }
    }

    private static string RedactUserProfile(string report)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? report
            : Regex.Replace(
                report,
                Regex.Escape(userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "%USERPROFILE%",
                RegexOptions.IgnoreCase);
    }

    private static string BuildReport(Exception exception, string phase, string? prefix)
    {
        var sb = new StringBuilder();
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var pluginDirectory = string.IsNullOrWhiteSpace(assemblyLocation)
            ? string.Empty
            : Path.GetDirectoryName(assemblyLocation) ?? string.Empty;

        AppendHeader(sb, "Cassis MCP Startup Report");
        AppendLine(sb, "UTC time", DateTime.UtcNow.ToString("O"));
        AppendLine(sb, "Local time", DateTime.Now.ToString("O"));
        AppendLine(sb, "Phase", phase);
        AppendLine(sb, "Prefix", prefix ?? "<none>");
        AppendLine(sb, "Process", Process.GetCurrentProcess().ProcessName);
        AppendLine(sb, ".NET", RuntimeInformation.FrameworkDescription);
        AppendLine(sb, "OS", RuntimeInformation.OSDescription);
        AppendLine(sb, "Process architecture", RuntimeInformation.ProcessArchitecture.ToString());
        AppendLine(sb, "Plugin assembly", assemblyLocation);
        AppendLine(sb, "Plugin directory", pluginDirectory);
        AppendLine(sb, "Current directory", Environment.CurrentDirectory);
        AppendLine(sb, "APPDATA", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        AppendLine(sb, "LOCALAPPDATA", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        AppendLine(sb, "TEMP", Path.GetTempPath());

        AppendHeader(sb, "Exception");
        AppendLine(sb, "Type", exception.GetType().FullName ?? exception.GetType().Name);
        if (exception is TypeLoadException typeLoadException)
        {
            AppendLine(sb, "Type name", typeLoadException.TypeName ?? "<unknown>");
        }

        sb.AppendLine(exception.ToString());
        AppendLoaderExceptions(sb, exception);

        AppendPortState(sb, prefix);
        AppendLoadedAssemblies(sb);
        AppendPluginDirectory(sb, pluginDirectory);

        return sb.ToString();
    }

    private static void AppendPortState(StringBuilder sb, string? prefix)
    {
        AppendHeader(sb, "Port State");
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            sb.AppendLine("No valid port found in prefix.");
            return;
        }

        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            var listeners = properties.GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == uri.Port)
                .Select(endpoint => endpoint.ToString())
                .ToArray();
            var connections = properties.GetActiveTcpConnections()
                .Where(connection => connection.LocalEndPoint.Port == uri.Port || connection.RemoteEndPoint.Port == uri.Port)
                .Select(connection => $"{connection.LocalEndPoint} -> {connection.RemoteEndPoint} ({connection.State})")
                .ToArray();

            AppendLine(sb, "Port", uri.Port.ToString());
            AppendLine(sb, "Listeners", listeners.Length == 0 ? "<none>" : string.Join(", ", listeners));
            AppendLine(sb, "Connections", connections.Length == 0 ? "<none>" : string.Join(", ", connections));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Could not inspect port state: {ex}");
        }
    }

    private static void AppendLoadedAssemblies(StringBuilder sb)
    {
        AppendHeader(sb, "Loaded Assemblies");

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => !assembly.IsDynamic)
                     .Where(IsInterestingAssembly)
                     .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase))
        {
            AppendAssemblyLine(sb, assembly.GetName(), GetAssemblyLocation(assembly));
        }
    }

    private static void AppendPluginDirectory(StringBuilder sb, string pluginDirectory)
    {
        AppendHeader(sb, "Plugin Directory Files");

        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            sb.AppendLine("Plugin directory not found.");
            return;
        }

        foreach (var expectedFile in ExpectedPluginFiles)
        {
            var path = Path.Combine(pluginDirectory, expectedFile);
            AppendLine(sb, expectedFile, File.Exists(path) ? "present" : "missing");
        }

        sb.AppendLine();

        foreach (var path in Directory.EnumerateFiles(pluginDirectory)
                     .Where(path => IsAssemblyFile(path))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            try
            {
                AppendAssemblyLine(sb, AssemblyName.GetAssemblyName(path), path, file.Length, file.LastWriteTime);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{file.Name} | {file.Length} bytes | {file.LastWriteTime:O} | not a managed assembly: {ex.Message}");
            }
        }
    }

    private static void AppendLoaderExceptions(StringBuilder sb, Exception exception)
    {
        if (exception is ReflectionTypeLoadException reflectionException)
        {
            AppendHeader(sb, "Loader Exceptions");
            foreach (var loaderException in reflectionException.LoaderExceptions.Where(e => e != null))
            {
                sb.AppendLine(loaderException!.ToString());
                sb.AppendLine();
            }
        }

        if (exception.InnerException != null)
        {
            AppendLoaderExceptions(sb, exception.InnerException);
        }
    }

    private static bool IsInterestingAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? string.Empty;
        return InterestingAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAssemblyFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gha", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAssemblyLocation(Assembly assembly)
    {
        try
        {
            return string.IsNullOrWhiteSpace(assembly.Location) ? "<no location>" : assembly.Location;
        }
        catch (Exception ex)
        {
            return $"<location unavailable: {ex.Message}>";
        }
    }

    private static void AppendHeader(StringBuilder sb, string header)
    {
        sb.AppendLine();
        sb.AppendLine($"## {header}");
    }

    private static void AppendLine(StringBuilder sb, string name, string value)
    {
        sb.AppendLine($"{name}: {value}");
    }

    private static void AppendAssemblyLine(StringBuilder sb, AssemblyName assemblyName, string location, long? size = null, DateTime? modified = null)
    {
        var sizeText = size.HasValue ? $" | {size.Value} bytes" : string.Empty;
        var modifiedText = modified.HasValue ? $" | {modified.Value:O}" : string.Empty;
        sb.AppendLine($"{assemblyName.Name} | {assemblyName.Version}{sizeText}{modifiedText} | {location}");
    }
}
