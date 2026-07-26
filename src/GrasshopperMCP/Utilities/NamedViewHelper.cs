using System;
using System.Collections.Generic;
using GrasshopperMCP.Models;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;

namespace GrasshopperMCP.Utilities;

public enum NamedViewAction
{
    List,
    Restore,
    Add,
    Rename,
    Delete,
}

/// <summary>
/// Rhino named view CRUD helpers for the Manage_NamedViews MCP tool.
/// </summary>
public static class NamedViewHelper
{
    public static NamedViewAction ParseAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("Required parameter 'action' is missing or empty");
        }

        return action.Trim().ToLowerInvariant() switch
        {
            "list" => NamedViewAction.List,
            "restore" => NamedViewAction.Restore,
            "add" => NamedViewAction.Add,
            "rename" => NamedViewAction.Rename,
            "delete" => NamedViewAction.Delete,
            _ => throw new ArgumentException(
                $"Unknown action '{action}'. Use list, restore, add, rename, or delete."),
        };
    }

    public static void RequireMutationConfirm(bool? confirm)
    {
        if (confirm != true)
        {
            throw new ArgumentException("Mutating actions require confirm: true");
        }
    }

    public static void RequireLookup(string? name, int? index)
    {
        if (!index.HasValue && string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Provide name or index");
        }
    }

    public static int ResolveIndex(NamedViewTable table, string? name, int? index)
    {
        if (index.HasValue)
        {
            if (index.Value < 0 || index.Value >= table.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    index.Value,
                    $"Index must be between 0 and {Math.Max(0, table.Count - 1)}.");
            }

            return index.Value;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var found = table.FindByName(name);
            if (found < 0)
            {
                throw new ArgumentException($"Named view '{name}' not found");
            }

            return found;
        }

        throw new ArgumentException("Provide name or index");
    }

    public static object Execute(
        RhinoDoc doc,
        string action,
        string? name,
        string? newName,
        int? index,
        string? viewportName,
        bool? confirm)
    {
        var parsed = ParseAction(action);
        var table = doc.NamedViews;

        return parsed switch
        {
            NamedViewAction.List => BuildListResponse(parsed),
            NamedViewAction.Restore => Restore(doc, table, parsed, name, index, viewportName),
            NamedViewAction.Add => Add(doc, table, parsed, name, viewportName, confirm),
            NamedViewAction.Rename => Rename(table, parsed, name, newName, index, confirm),
            NamedViewAction.Delete => Delete(table, parsed, name, index, confirm),
            _ => throw new ArgumentException($"Unsupported action '{action}'."),
        };

        object BuildListResponse(NamedViewAction act)
        {
            var list = BuildList(table);
            return new
            {
                action = act.ToString().ToLowerInvariant(),
                namedViews = list.NamedViews,
                totalCount = list.TotalCount,
            };
        }
    }

    public static NamedViewListResult BuildList(NamedViewTable table)
    {
        var namedViews = new List<NamedViewInfo>(table.Count);
        for (var i = 0; i < table.Count; i++)
        {
            var viewInfo = table[i];
            var viewport = viewInfo.Viewport;
            namedViews.Add(new NamedViewInfo
            {
                Index = i,
                Name = viewInfo.Name ?? string.Empty,
                NamedViewId = viewInfo.NamedViewId.ToString(),
                CameraLocation = viewport == null ? null : ToVec3(viewport.CameraLocation),
                CameraDirection = viewport == null ? null : ToVec3(viewport.CameraDirection),
            });
        }

        return new NamedViewListResult
        {
            NamedViews = namedViews,
            TotalCount = namedViews.Count,
        };
    }

    private static object Restore(
        RhinoDoc doc,
        NamedViewTable table,
        NamedViewAction parsed,
        string? name,
        int? index,
        string? viewportName)
    {
        RequireLookup(name, index);
        var resolvedIndex = ResolveIndex(table, name, index);
        var view = ResolveViewport(doc, viewportName)
                   ?? throw new ArgumentException(
                       string.IsNullOrWhiteSpace(viewportName)
                           ? "Unable to locate active viewport"
                           : $"Unable to locate viewport '{viewportName}'");

        var restored = table.Restore(resolvedIndex, view.ActiveViewport);
        if (!restored)
        {
            throw new InvalidOperationException($"Failed to restore named view at index {resolvedIndex}");
        }

        view.Redraw();
        var restoredName = table[resolvedIndex].Name ?? string.Empty;

        return new
        {
            action = parsed.ToString().ToLowerInvariant(),
            success = true,
            name = restoredName,
            index = resolvedIndex,
            viewportName = view.MainViewport?.Name ?? string.Empty,
        };
    }

    private static object Add(
        RhinoDoc doc,
        NamedViewTable table,
        NamedViewAction parsed,
        string? name,
        string? viewportName,
        bool? confirm)
    {
        RequireMutationConfirm(confirm);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Required parameter 'name' is missing or empty");
        }

        var view = ResolveViewport(doc, viewportName)
                   ?? throw new ArgumentException(
                       string.IsNullOrWhiteSpace(viewportName)
                           ? "Unable to locate active viewport"
                           : $"Unable to locate viewport '{viewportName}'");

        var replacedExisting = table.FindByName(name) >= 0;
        table.Add(name, view.ActiveViewport.Id);
        var resolvedIndex = table.FindByName(name);
        if (resolvedIndex < 0)
        {
            throw new InvalidOperationException($"Failed to add named view '{name}'");
        }

        var list = BuildList(table);
        return new
        {
            action = parsed.ToString().ToLowerInvariant(),
            success = true,
            name,
            index = resolvedIndex,
            replacedExisting,
            namedViews = list.NamedViews,
            totalCount = list.TotalCount,
        };
    }

    private static object Rename(
        NamedViewTable table,
        NamedViewAction parsed,
        string? name,
        string? newName,
        int? index,
        bool? confirm)
    {
        RequireMutationConfirm(confirm);
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("Required parameter 'newName' is missing or empty");
        }

        RequireLookup(name, index);
        var resolvedIndex = ResolveIndex(table, name, index);
        var oldName = table[resolvedIndex].Name ?? string.Empty;

        var renamed = table.Rename(resolvedIndex, newName);
        if (!renamed)
        {
            throw new InvalidOperationException($"Failed to rename named view '{oldName}' to '{newName}'");
        }

        var updatedIndex = table.FindByName(newName);
        var list = BuildList(table);
        return new
        {
            action = parsed.ToString().ToLowerInvariant(),
            success = true,
            oldName,
            newName,
            index = updatedIndex >= 0 ? updatedIndex : resolvedIndex,
            namedViews = list.NamedViews,
            totalCount = list.TotalCount,
        };
    }

    private static object Delete(
        NamedViewTable table,
        NamedViewAction parsed,
        string? name,
        int? index,
        bool? confirm)
    {
        RequireMutationConfirm(confirm);
        RequireLookup(name, index);
        var resolvedIndex = ResolveIndex(table, name, index);
        var deletedName = table[resolvedIndex].Name ?? string.Empty;

        var deleted = table.Delete(resolvedIndex);
        if (!deleted)
        {
            throw new InvalidOperationException($"Failed to delete named view '{deletedName}'");
        }

        var list = BuildList(table);
        return new
        {
            action = parsed.ToString().ToLowerInvariant(),
            success = true,
            name = deletedName,
            index = resolvedIndex,
            namedViews = list.NamedViews,
            totalCount = list.TotalCount,
        };
    }

    private static RhinoView? ResolveViewport(RhinoDoc doc, string? viewportName)
    {
        if (string.IsNullOrWhiteSpace(viewportName))
        {
            return doc.Views.ActiveView ?? doc.Views.FirstOrDefault();
        }

        return doc.Views.FirstOrDefault(view =>
            string.Equals(view.MainViewport?.Name, viewportName, StringComparison.OrdinalIgnoreCase));
    }

    private static Vec3? ToVec3(Point3d point)
    {
        if (!point.IsValid)
        {
            return null;
        }

        return new Vec3 { X = point.X, Y = point.Y, Z = point.Z };
    }

    private static Vec3? ToVec3(Vector3d vector)
    {
        if (!vector.IsValid || vector.IsZero)
        {
            return null;
        }

        return new Vec3 { X = vector.X, Y = vector.Y, Z = vector.Z };
    }
}
