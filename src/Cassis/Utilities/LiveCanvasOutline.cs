using System.Security.Cryptography;
using Cassis.Tools;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace Cassis.Utilities;

// Reads a live document into the CanvasOutline model. Ids, ports and pivots follow the
// GhJSON export (ghjson-dotnet CanvasReader), so ghsh and Cassis print the same refs.
internal static class LiveCanvasOutline
{
    public static OutlineCanvas Read(GH_Document doc)
    {
        var objects = doc.Objects.ToList();
        var nonGroups = objects.Where(obj => obj is not GH_Group).ToList();
        var ids = new Dictionary<Guid, int>();
        var next = 1;
        foreach (var obj in nonGroups)
            ids[obj.InstanceGuid] = next++;

        var components = nonGroups.Select(obj => new OutlineComponent
        {
            Id = ids[obj.InstanceGuid],
            Name = obj.Name ?? "",
            NickName = obj.NickName ?? "",
            InstanceGuid = obj.InstanceGuid.ToString(),
            Pivot = obj.Attributes is { } attributes
                ? $"{Round(attributes.Pivot.X)},{Round(attributes.Pivot.Y)}"
                : null,
            ScriptLanguage = ScriptLanguage(obj),
            ScriptCode = ScriptLanguage(obj) is null ? "" : ScriptTools.TryExtractScript(obj) ?? "",
            ScribbleText = (obj as GH_Scribble)?.Text,
            InputNames = (obj as IGH_Component)?.Params.Input.Select(p => p.Name ?? "").ToArray()
                ?? Array.Empty<string>(),
            // GhJSON leaves out the reserved script output "out"; so does the outline.
            OutputNames = obj is IGH_Component outputs
                ? outputs.Params.Output
                    .Where((_, index) => ScriptLanguage(obj) is null || !ScriptTools.IsReservedScriptOutput(outputs, index))
                    .Select(p => p.Name ?? "")
                    .ToArray()
                : Array.Empty<string>()
        }).ToList();

        var groups = objects.OfType<GH_Group>().Select(group => new OutlineGroup
        {
            Ref = (next++).ToString(),
            InstanceGuid = group.InstanceGuid.ToString(),
            Name = group.NickName ?? "",
            Members = group.ObjectIDs
                .Where(ids.ContainsKey)
                .Select(id => ids[id])
                .ToArray()
        }).ToList();

        return new OutlineCanvas
        {
            Title = doc.IsFilePathDefined
                ? Path.GetFileNameWithoutExtension(doc.FilePath)
                : doc.DisplayName,
            Components = components,
            Groups = groups,
            Wires = Wires(nonGroups, ids)
        };
    }

    // SHA-256 of the document as Grasshopper would save it; ghsh uses the same content hash.
    public static string Revision(GH_Document doc)
    {
        var archive = new GH_Archive();
        archive.AppendObject(doc, "Definition");
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(archive.Serialize_Binary()))
            .Replace("-", "")
            .ToLowerInvariant();
    }

    private static List<OutlineWire> Wires(List<IGH_DocumentObject> objects, Dictionary<Guid, int> ids)
    {
        var wires = new List<OutlineWire>();
        var seen = new HashSet<string>();
        foreach (var obj in objects)
        {
            IList<IGH_Param>? outputs = obj switch
            {
                IGH_Component component => component.Params.Output,
                IGH_Param param => new List<IGH_Param> { param },
                _ => null
            };
            if (outputs is null || !ids.TryGetValue(obj.InstanceGuid, out var fromId))
                continue;

            for (var fromIndex = 0; fromIndex < outputs.Count; fromIndex++)
            {
                foreach (var recipient in outputs[fromIndex].Recipients)
                {
                    var owner = recipient.Attributes?.GetTopLevel?.DocObject;
                    if (owner is null || !ids.TryGetValue(owner.InstanceGuid, out var toId))
                        continue;
                    var toIndex = owner is IGH_Component target
                        ? target.Params.Input.IndexOf(recipient)
                        : 0;
                    if (!seen.Add($"{fromId}:{fromIndex}-{toId}:{toIndex}"))
                        continue;
                    wires.Add(new OutlineWire
                    {
                        FromId = fromId,
                        ToId = toId,
                        FromPort = fromIndex.ToString(),
                        ToPort = toIndex.ToString()
                    });
                }
            }
        }
        return wires;
    }

    // Language names match the GhJSON script extensions (gh.csharp, gh.python, ...).
    private static string? ScriptLanguage(IGH_DocumentObject obj)
    {
        var typeName = obj.GetType().FullName ?? "";
        return ScriptComponentKind.GetLanguage(obj) switch
        {
            "C#" => "csharp",
            "VB" => "vbscript",
            "Python" => typeName.IndexOf("IronPython", StringComparison.Ordinal) >= 0 ||
                        typeName.IndexOf("ZuiPython", StringComparison.Ordinal) >= 0
                ? "ironpython"
                : "python",
            _ => null
        };
    }

    private static int Round(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
