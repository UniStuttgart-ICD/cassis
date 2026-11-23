using System.ComponentModel;
using Cassis.Extensions;
using Cassis.Utilities;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cassis.Tools;

/// <summary>
/// The canvas as a book: groups are chapters. Read the outline, then one chapter.
/// </summary>
[McpServerToolType]
public static class OutlineTools
{
    [McpServerTool(Name = "Canvas_Outline")]
    [Description(
        "Start here to understand a canvas. Returns it as a book: groups are chapters with " +
        "subchapters, script nicknames, wire flow between chapters (\"from>toxCount\"), and gaps " +
        "(unnamed groups, default script names, ungrouped components). Titles with derived=true " +
        "were made up by Cassis. No script source. Then call Canvas_Chapter for one chapter. " +
        "revision changes whenever the canvas changes; refs are valid for that revision. " +
        "To re-check later, pass that revision as knownRevision: an unchanged canvas returns " +
        "only {unchanged:true, revision}.")]
    public static async Task<CallToolResult> Outline(
        [Description("revision of your last Canvas_Outline answer; omit for a first read")]
        string? knownRevision = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
            await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = ActiveDocument();
                return Reply(
                    CanvasOutline.Outline(LiveCanvasOutline.Read(doc)),
                    LiveCanvasOutline.Revision(doc),
                    knownRevision);
            }), "Canvas_Outline");
    }

    [McpServerTool(Name = "Canvas_Chapter")]
    [Description(
        "Returns one chapter of Canvas_Outline: its components with ids, GUIDs and pivots, script " +
        "summaries (language, lines, header comment; no source), wires inside the chapter and " +
        "boundary wires to other chapters, written \"fromId.port>toId.port\". To re-check a " +
        "chapter you already read, pass its revision as knownRevision.")]
    public static async Task<CallToolResult> Chapter(
        [Description("Chapter ref from Canvas_Outline, a group InstanceGuid, or 'ungrouped'")]
        string group,
        [Description("revision of your last answer for this same chapter; omit for a first read")]
        string? knownRevision = null)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            McpExtensions.ValidateRequired((nameof(group), group));
            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = ActiveDocument();
                Dictionary<string, object?> chapter;
                try
                {
                    chapter = CanvasOutline.Chapter(LiveCanvasOutline.Read(doc), group);
                }
                catch (OutlineGroupNotFoundException exception)
                {
                    throw new ArgumentException(exception.Message);
                }
                return Reply(chapter, LiveCanvasOutline.Revision(doc), knownRevision);
            });
        }, "Canvas_Chapter");
    }

    // The answer is always built, so a bad ref still fails; only the reply is shortened.
    internal static Dictionary<string, object?> Reply(
        Dictionary<string, object?> answer,
        string revision,
        string? knownRevision)
    {
        if (string.Equals(knownRevision?.Trim(), revision, StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object?> { ["unchanged"] = true, ["revision"] = revision };
        answer["revision"] = revision;
        return answer;
    }

    [McpServerTool(Name = "Rename_Objects")]
    [Description(
        "Renames groups and components in one call. A group name is a chapter title; a script " +
        "nickname says what the script does. All entries are checked first; if any write fails, " +
        "every earlier name is restored.")]
    public static async Task<CallToolResult> RenameObjects(
        [Description("InstanceGuid -> new one-line name, e.g. {\"94953358-f91e-4f41-b73b-263888bd5e5c\": \"HoloLens model tracking\"}")]
        Dictionary<string, string> names)
    {
        return await McpExtensions.SafeExecuteAsync(async () =>
        {
            if (names is null || names.Count == 0)
                throw new ArgumentException("names must contain at least one InstanceGuid and name.");

            return await UiThreadHelper.InvokeAsync<object>(() =>
            {
                var doc = ActiveDocument();
                var targets = new List<(IGH_DocumentObject Obj, string Name, string Before)>();
                foreach (var pair in names)
                {
                    if (!Guid.TryParse(pair.Key, out var guid))
                        throw new ArgumentException($"'{pair.Key}' is not an InstanceGuid.");
                    if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        throw new ArgumentException($"The name for '{pair.Key}' must be one non-empty line.");
                    var obj = doc.FindObject(guid, true)
                        ?? throw new ArgumentException($"No top-level object has InstanceGuid '{pair.Key}'.");
                    targets.Add((obj, pair.Value, obj.NickName ?? ""));
                }

                var written = new List<(IGH_DocumentObject Obj, string Before)>();
                try
                {
                    foreach (var target in targets)
                    {
                        written.Add((target.Obj, target.Before));
                        target.Obj.NickName = target.Name;
                        // The name changes the drawn width; stale layout keeps the old box.
                        target.Obj.Attributes?.ExpireLayout();
                    }
                    foreach (var target in targets)
                    {
                        if (target.Obj.NickName != target.Name)
                            throw new InvalidOperationException($"Readback for '{target.Obj.InstanceGuid}' did not match the new name.");
                    }
                }
                catch
                {
                    foreach (var (obj, before) in written)
                    {
                        obj.NickName = before;
                        obj.Attributes?.ExpireLayout();
                    }
                    throw;
                }

                Instances.ActiveCanvas?.Refresh();
                return new
                {
                    success = true,
                    renamed = targets.Select(target => new
                    {
                        id = target.Obj.InstanceGuid,
                        kind = target.Obj is GH_Group ? "group" : "component",
                        before = target.Before,
                        after = target.Name,
                        changed = target.Before != target.Name
                    }).ToList()
                };
            });
        }, "Rename_Objects");
    }

    private static GH_Document ActiveDocument() =>
        Instances.ActiveCanvas?.Document
        ?? throw new InvalidOperationException("No active Grasshopper document.");
}
