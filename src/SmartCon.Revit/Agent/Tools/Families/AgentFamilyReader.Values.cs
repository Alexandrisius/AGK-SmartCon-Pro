using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Value and report helpers of <see cref="AgentFamilyReader"/>: display values
/// with units, parameter classification (built-in, formula-driven) and the
/// <see cref="FamilyParameterReport"/> assembly. All methods are Revit-thread
/// only (I-05).
/// </summary>
internal static partial class AgentFamilyReader
{
    internal static string? DisplayValue(
        Document? doc,
        FamilyType? type,
        FamilyParameter parameter)
    {
        var value = DisplayValue(type, parameter);
        if (doc is null || value is null || parameter.StorageType != StorageType.Double)
            return value;

        var unit = UnitOf(doc, parameter);
        if (unit is null || value.IndexOf(unit, StringComparison.OrdinalIgnoreCase) >= 0)
            return value;

        return value + " " + unit;
    }

    internal static string? UnitOf(Document? doc, FamilyParameter parameter)
    {
        if (doc is null || parameter.StorageType != StorageType.Double)
            return null;

        try
        {
            var spec = parameter.Definition.GetDataType();
            var format = doc.GetUnits().GetFormatOptions(spec);
            var unit = format?.GetUnitTypeId();
            if (unit is null)
                return null;

            var label = LabelUtils.GetLabelForUnit(unit);
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool HasValue(FamilyType? type, FamilyParameter parameter)
    {
        try
        {
            return type?.HasValue(parameter) ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True for a Revit built-in family parameter (Comments, Mark, …).
    /// <see cref="InternalDefinition.BuiltInParameter"/> is
    /// <see cref="BuiltInParameter.INVALID"/> for every custom family, shared or
    /// global parameter, so it is the API's own discriminator.
    /// </summary>
    internal static bool IsBuiltIn(FamilyParameter parameter)
    {
        try
        {
            return parameter.Definition is InternalDefinition internalDefinition
                   && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the value is driven by a formula. <see cref="FamilyParameter.IsDeterminedByFormula"/>
    /// is not reliable for every parameter kind (a parameter with formula text can
    /// report false), so the formula text is checked as well — a wrong "false" makes
    /// set_family_parameter_values plan a silent no-op write.
    /// </summary>
    internal static bool IsFormulaDriven(FamilyParameter parameter)
    {
        try
        {
            return parameter.IsDeterminedByFormula
                   || !string.IsNullOrWhiteSpace(parameter.Formula);
        }
        catch (Exception)
        {
            return !string.IsNullOrWhiteSpace(parameter.Formula);
        }
    }

    internal static FamilyParameterReport BuildParameterReport(
        FamilyParameter parameter,
        IReadOnlyList<FamilyType> types,
        bool includeValues,
        int maxValues,
        Document? doc = null)
    {
        var formulaDriven = IsFormulaDriven(parameter);
        var report = new FamilyParameterReport
        {
            Id = parameter.Id.ToString(),
            Name = parameter.Definition.Name,
            Scope = parameter.IsInstance ? "instance" : "type",
            IsInstance = parameter.IsInstance,
            IsShared = parameter.IsShared,
            IsBuiltIn = IsBuiltIn(parameter),
            Guid = TryGetGuid(parameter),
            StorageType = AgentFamilySpecs.StorageTypeName(parameter.StorageType),
            Spec = AgentFamilySpecs.SpecName(parameter.Definition),
            Group = AgentFamilySpecs.GroupName(parameter.Definition),
            Formula = parameter.Formula,
            IsDeterminedByFormula = formulaDriven,
            LookupTables = LookupTables(parameter),
            CanSetValue = !formulaDriven && !parameter.IsReadOnly,
            CanSetFormula = parameter.CanAssignFormula,
            IsReporting = parameter.IsReporting,
            IsReadOnly = parameter.IsReadOnly
        };

        if (includeValues)
        {
            var values = new List<FamilyTypeValueReport>();
            var take = Math.Min(types.Count, Math.Max(0, maxValues));
            for (var i = 0; i < take; i++)
            {
                var type = types[i];
                var hasValue = HasValue(type, parameter);
                values.Add(new FamilyTypeValueReport
                {
                    TypeName = type.Name,
                    Value = hasValue
                        ? FamilyValueSnapshot.ReadFamilyType(type, parameter).ToRaw()
                        : null,
                    DisplayValue = hasValue ? DisplayValue(doc, type, parameter) : null,
                    Unit = UnitOf(doc, parameter),
                    HasValue = hasValue
                });
            }

            report.ValuesByType = values;
            report.ValuesTruncated = types.Count > take;
        }

        return report;
    }

    private static List<string>? LookupTables(FamilyParameter parameter)
    {
        var tables = AgentFamilySpecs.ExtractLookupTables(parameter.Formula);
        return tables.Count == 0 ? null : new List<string>(tables);
    }

    private static string? TryGetGuid(FamilyParameter parameter)
    {
        try
        {
            return parameter.IsShared && parameter.GUID != Guid.Empty
                ? parameter.GUID.ToString("D")
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that every size_lookup table referenced by the formula exists in the
    /// family. Returns false with the missing table names when it does not.
    /// </summary>
    internal static bool TryFindMissingLookupTables(
        Document familyDoc,
        string? formula,
        out IReadOnlyList<string> missing)
    {
        missing = Array.Empty<string>();
        var tables = AgentFamilySpecs.ExtractLookupTables(formula);
        if (tables.Count == 0)
            return true;

        var manager = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc,
            familyDoc.OwnerFamily.Id);
        var absent = new List<string>();
        foreach (var table in tables)
        {
            if (manager is null || !manager.HasSizeTable(table))
                absent.Add(table);
        }

        missing = absent;
        return absent.Count == 0;
    }
}