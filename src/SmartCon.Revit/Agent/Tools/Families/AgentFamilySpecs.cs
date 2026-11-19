using Autodesk.Revit.DB;
using SmartCon.Core.Services.Agent;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Maps user-facing spec / group names to Revit <see cref="ForgeTypeId"/> values
/// and back. Family tools are compiled for Revit 2023+ only, so the ForgeTypeId
/// API (introduced in 2022) is used unconditionally.
/// </summary>
internal static class AgentFamilySpecs
{
    internal static bool TryResolveSpec(
        string? name,
        out ForgeTypeId? spec,
        out AgentFamilyValueKind kind,
        out string? message)
    {
        spec = null;
        kind = AgentFamilyValueKind.Number;
        message = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            message = "storageType is required: length, number, text, yesno, integer, " +
                      "angle, area, volume or material.";
            return false;
        }

        var normalized = name!.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "length":
            case "длина":
            case "mm":
            case "мм":
                spec = SpecTypeId.Length;
                kind = AgentFamilyValueKind.Length;
                return true;
            case "number":
            case "число":
            case "числовой":
            case "число2":
                spec = SpecTypeId.Number;
                kind = AgentFamilyValueKind.Number;
                return true;
            case "text":
            case "string":
            case "текст":
            case "строка":
                spec = SpecTypeId.String.Text;
                kind = AgentFamilyValueKind.Text;
                return true;
            case "yesno":
            case "yes/no":
            case "boolean":
            case "bool":
            case "данет":
            case "да/нет":
            case "логический":
                spec = SpecTypeId.Boolean.YesNo;
                kind = AgentFamilyValueKind.YesNo;
                return true;
            case "integer":
            case "int":
            case "целое":
            case "целочисленный":
                spec = SpecTypeId.Int.Integer;
                kind = AgentFamilyValueKind.WholeNumber;
                return true;
            case "angle":
            case "угол":
                spec = SpecTypeId.Angle;
                kind = AgentFamilyValueKind.Angle;
                return true;
            case "area":
            case "площадь":
                spec = SpecTypeId.Area;
                kind = AgentFamilyValueKind.Area;
                return true;
            case "volume":
            case "объем":
            case "объём":
                spec = SpecTypeId.Volume;
                kind = AgentFamilyValueKind.Volume;
                return true;
            case "material":
            case "материал":
                spec = SpecTypeId.Reference.Material;
                kind = AgentFamilyValueKind.ElementId;
                return true;
            case "familytype":
            case "тип семейства":
                message = "A nested family type parameter requires a family category; " +
                          "create it in the Family Editor instead.";
                return false;
            default:
                message = "Unknown storageType '" + name + "'. Use length, number, text, " +
                          "yesno, integer, angle, area, volume or material.";
                return false;
        }
    }

    internal static AgentFamilyValueKind KindOf(Definition definition, StorageType storageType)
    {
        ForgeTypeId? spec = null;
        try
        {
            spec = definition.GetDataType();
        }
        catch (Exception)
        {
            // Older or built-in definitions may not expose a data type; fall back
            // to the storage type below.
        }

        if (spec is not null)
        {
            if (SpecTypeId.Length.Equals(spec))
                return AgentFamilyValueKind.Length;
            if (SpecTypeId.Angle.Equals(spec))
                return AgentFamilyValueKind.Angle;
            if (SpecTypeId.Area.Equals(spec))
                return AgentFamilyValueKind.Area;
            if (SpecTypeId.Volume.Equals(spec))
                return AgentFamilyValueKind.Volume;
            if (SpecTypeId.Number.Equals(spec))
                return AgentFamilyValueKind.Number;
            if (SpecTypeId.Boolean.YesNo.Equals(spec))
                return AgentFamilyValueKind.YesNo;
            if (SpecTypeId.Int.Integer.Equals(spec))
                return AgentFamilyValueKind.WholeNumber;
            if (SpecTypeId.String.Text.Equals(spec))
                return AgentFamilyValueKind.Text;
            if (SpecTypeId.Reference.Material.Equals(spec))
                return AgentFamilyValueKind.ElementId;
        }

        // Unknown measurable or reference specs must not be treated as a
        // dimensionless number: that would silently write internal units.
        return storageType switch
        {
            StorageType.Integer => AgentFamilyValueKind.WholeNumber,
            StorageType.String => AgentFamilyValueKind.Text,
            _ => AgentFamilyValueKind.Unsupported
        };
    }

    /// <summary>
    /// True when both definitions describe the same data type. Unlike the coarse
    /// <see cref="KindOf"/> buckets this distinguishes pressure from mass, etc.
    /// </summary>
    internal static bool SameSpec(Definition left, Definition right)
    {
        try
        {
            return left.GetDataType().Equals(right.GetDataType());
        }
        catch (Exception)
        {
            return KindOf(left, StorageType.Double) == KindOf(right, StorageType.Double);
        }
    }

    internal static bool TryResolveGroup(
        string? name,
        out ForgeTypeId? group,
        out string? message)
    {
        group = null;
        message = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            group = GroupTypeId.Data;
            return true;
        }

        var normalized = name!.Trim().ToLowerInvariant().Replace(" ", string.Empty);
        switch (normalized)
        {
            case "pg_length":
            case "length":
            case "dimensions":
            case "размеры":
            case "размер":
                group = GroupTypeId.Length;
                return true;
            case "pg_identity_data":
            case "identitydata":
            case "identity":
            case "идентификация":
            case "идентификационныеданные":
                group = GroupTypeId.IdentityData;
                return true;
            case "pg_data":
            case "data":
            case "данные":
                group = GroupTypeId.Data;
                return true;
            case "pg_general":
            case "general":
            case "общие":
            case "общий":
                group = GroupTypeId.General;
                return true;
            case "pg_geometry":
            case "geometry":
            case "геометрия":
                group = GroupTypeId.Geometry;
                return true;
            case "pg_constraints":
            case "constraints":
            case "ограничения":
                group = GroupTypeId.Constraints;
                return true;
            case "pg_text":
            case "text":
            case "текст":
                group = GroupTypeId.Text;
                return true;
            case "pg_materials":
            case "materials":
            case "материалы":
            case "материалыиотделка":
                group = GroupTypeId.Materials;
                return true;
            case "pg_visibility":
            case "visibility":
            case "видимость":
                group = GroupTypeId.Visibility;
                return true;
            case "pg_graphics":
            case "graphics":
            case "графика":
                group = GroupTypeId.Graphics;
                return true;
            case "pg_construction":
            case "construction":
            case "строительство":
                group = GroupTypeId.Construction;
                return true;
            case "pg_phasing":
            case "phasing":
            case "стадии":
                group = GroupTypeId.Phasing;
                return true;
            case "pg_title":
            case "title":
            case "заголовок":
                group = GroupTypeId.Title;
                return true;
            case "pg_electrical":
            case "electrical":
            case "электрика":
                group = GroupTypeId.Electrical;
                return true;
            case "pg_mechanical":
            case "mechanical":
            case "механика":
                group = GroupTypeId.Mechanical;
                return true;
            case "pg_plumbing":
            case "plumbing":
            case "сантехника":
                group = GroupTypeId.Plumbing;
                return true;
            default:
                message = "Unknown parameter group '" + name + "'. Use PG_LENGTH, PG_DATA, " +
                          "PG_IDENTITY_DATA, PG_GENERAL, PG_GEOMETRY, PG_CONSTRAINTS, " +
                          "PG_TEXT, PG_MATERIALS, PG_VISIBILITY, PG_GRAPHICS, " +
                          "PG_CONSTRUCTION (or the localized name).";
                return false;
        }
    }

    internal static string GroupName(Definition definition)
    {
        try
        {
            var group = definition.GetGroupTypeId();
            return GroupNameOf(group);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    internal static string GroupNameOf(ForgeTypeId group)
    {
        if (GroupTypeId.Length.Equals(group)) return "PG_LENGTH";
        if (GroupTypeId.IdentityData.Equals(group)) return "PG_IDENTITY_DATA";
        if (GroupTypeId.Data.Equals(group)) return "PG_DATA";
        if (GroupTypeId.General.Equals(group)) return "PG_GENERAL";
        if (GroupTypeId.Geometry.Equals(group)) return "PG_GEOMETRY";
        if (GroupTypeId.Constraints.Equals(group)) return "PG_CONSTRAINTS";
        if (GroupTypeId.Text.Equals(group)) return "PG_TEXT";
        if (GroupTypeId.Materials.Equals(group)) return "PG_MATERIALS";
        if (GroupTypeId.Visibility.Equals(group)) return "PG_VISIBILITY";
        if (GroupTypeId.Graphics.Equals(group)) return "PG_GRAPHICS";
        if (GroupTypeId.Construction.Equals(group)) return "PG_CONSTRUCTION";
        if (GroupTypeId.Phasing.Equals(group)) return "PG_PHASING";
        if (GroupTypeId.Title.Equals(group)) return "PG_TITLE";
        if (GroupTypeId.Electrical.Equals(group)) return "PG_ELECTRICAL";
        if (GroupTypeId.Mechanical.Equals(group)) return "PG_MECHANICAL";
        if (GroupTypeId.Plumbing.Equals(group)) return "PG_PLUMBING";
        return group.TypeId ?? "unknown";
    }

    internal static string SpecName(Definition definition)
    {
        try
        {
            var spec = definition.GetDataType();
            return SpecNameOf(spec);
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    internal static string SpecNameOf(ForgeTypeId spec)
    {
        if (SpecTypeId.Length.Equals(spec)) return "length";
        if (SpecTypeId.Angle.Equals(spec)) return "angle";
        if (SpecTypeId.Area.Equals(spec)) return "area";
        if (SpecTypeId.Volume.Equals(spec)) return "volume";
        if (SpecTypeId.Number.Equals(spec)) return "number";
        if (SpecTypeId.Boolean.YesNo.Equals(spec)) return "yesno";
        if (SpecTypeId.Int.Integer.Equals(spec)) return "integer";
        if (SpecTypeId.String.Text.Equals(spec)) return "text";
        if (SpecTypeId.Reference.Material.Equals(spec)) return "material";
        return spec.TypeId ?? "unknown";
    }

    internal static bool IsRevitArgumentException(Exception ex) =>
        ex is Autodesk.Revit.Exceptions.ArgumentException;

    internal static bool IsRevitInvalidOperation(Exception ex) =>
        ex is Autodesk.Revit.Exceptions.InvalidOperationException;

    internal static string StorageTypeName(StorageType storageType) => storageType switch
    {
        StorageType.Double => "double",
        StorageType.Integer => "integer",
        StorageType.String => "string",
        StorageType.ElementId => "elementId",
        _ => "none"
    };

    internal static IReadOnlyList<string> ExtractLookupTables(string? formula) =>
        AgentFamilyText.ExtractLookupTables(formula);

    internal static string NormalizeForMatch(string? value) =>
        AgentFamilyText.NormalizeForMatch(value);

    internal static bool ContainsNormalized(string? haystack, string needle) =>
        AgentFamilyText.ContainsNormalized(haystack, needle);

    internal static string SanitizeFileName(string name) =>
        AgentFamilyText.SanitizeFileName(name);
}