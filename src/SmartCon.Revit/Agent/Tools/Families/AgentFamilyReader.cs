using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Read helpers over a family document. All methods are Revit-thread only and
/// never keep Element/Parameter references between calls (I-05).
/// </summary>
internal static class AgentFamilyReader
{
    internal static IReadOnlyList<RevitFamily> LoadableFamilies(Document doc)
    {
        var families = new List<RevitFamily>();
        var collector = new FilteredElementCollector(doc).OfClass(typeof(RevitFamily));
        foreach (var element in collector)
        {
            if (element is RevitFamily family)
                families.Add(family);
        }

        families.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
        return families;
    }

    internal static RevitFamily? ResolveFamily(
        Document doc,
        long? familyId,
        string? familyName,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        if (familyId.HasValue && familyId.Value > 0)
        {
            var element = doc.GetElement(
                SmartCon.Core.Compatibility.ElementIdCompat.Create(familyId.Value));
            if (element is RevitFamily familyById)
            {
                if (!string.IsNullOrWhiteSpace(familyName)
                    && !string.Equals(
                        familyById.Name,
                        familyName!.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    errorCode = "invalid_value";
                    message = "familyId " + familyId.Value + " is family '" +
                              familyById.Name + "', which does not match familyName '" +
                              familyName + "'.";
                    return null;
                }

                return familyById;
            }

            errorCode = "family_not_found";
            message = "Family element " + familyId.Value + " does not exist in the project. " +
                      "Revit may assign a new familyId after a family edit (load-back): call " +
                      "list_families again, or pass familyName instead of familyId.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(familyName))
        {
            errorCode = "invalid_arguments";
            message = "Pass familyId or familyName.";
            return null;
        }

        var matches = new List<RevitFamily>();
        foreach (var family in LoadableFamilies(doc))
        {
            if (string.Equals(family.Name, familyName!.Trim(), StringComparison.OrdinalIgnoreCase))
                matches.Add(family);
        }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
        {
            errorCode = "ambiguous_family";
            message = "Several families are named '" + familyName + "'; pass familyId instead.";
            return null;
        }

        errorCode = "family_not_found";
        message = "No loaded family named '" + familyName + "'. Use list_families to see " +
                  "the loaded families.";
        return null;
    }

    internal static IReadOnlyList<FamilyType> GetTypes(RevitFamilyManager manager)
    {
        var types = new List<FamilyType>();
        foreach (FamilyType type in manager.Types)
            types.Add(type);
        return types;
    }

    /// <summary>
    /// Opens a family document for read-only inspection. The caller must close it
    /// with <c>Close(false)</c> in a finally block (H1d lifecycle rule).
    /// </summary>
    internal static bool TryOpenFamilyDocument(
        Document projectDoc,
        RevitFamily family,
        out Document? familyDoc,
        out string? errorCode,
        out string? message)
    {
        familyDoc = null;
        errorCode = null;
        message = null;

        if (projectDoc.IsModifiable)
        {
            errorCode = "permission_denied";
            message = "The project document is modified by another transaction. Close it and retry.";
            return false;
        }

        if (projectDoc.IsReadOnly)
        {
            errorCode = "permission_denied";
            message = "The project document is read-only.";
            return false;
        }

        if (family.IsInPlace)
        {
            errorCode = "in_place_not_supported";
            message = "The family '" + family.Name + "' is an in-place family and has no " +
                      "family document.";
            return false;
        }

        if (!family.IsEditable)
        {
            errorCode = "family_not_editable";
            message = "The family '" + family.Name + "' is not editable.";
            return false;
        }

        try
        {
            familyDoc = projectDoc.EditFamily(family);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
        {
            errorCode = "family_open_conflict";
            message = "Revit refused to open the family document: " + ex.Message +
                      " Close the Family Editor and retry.";
            return false;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            errorCode = "family_not_editable";
            message = "Revit refused to open the family document: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            errorCode = "family_open_failed";
            message = "The family document could not be opened: " + ex.GetType().Name + ": " +
                      ex.Message;
            return false;
        }

        if (familyDoc is null)
        {
            errorCode = "family_open_failed";
            message = "Revit did not return a family document for '" + family.Name + "'.";
            return false;
        }

        return true;
    }

    internal static bool? ParseScope(string? scope, out string? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(scope))
            return null;

        switch (scope!.Trim().ToLowerInvariant())
        {
            case "instance":
            case "экземпляр":
            case "параметр экземпляра":
                return true;
            case "type":
            case "typ":
            case "тип":
            case "параметр типа":
                return false;
            default:
                message = "scope must be 'instance' or 'type'.";
                return null;
        }
    }

    internal static FamilyParameter? FindParameter(
        RevitFamilyManager manager,
        string? name,
        string? guid,
        string? scope,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        bool? wantInstance = null;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            wantInstance = ParseScope(scope, out var scopeMessage);
            if (wantInstance is null)
            {
                errorCode = "invalid_value";
                message = scopeMessage;
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(guid))
        {
            if (!Guid.TryParse(guid, out var parsedGuid))
            {
                errorCode = "invalid_value";
                message = "'" + guid + "' is not a valid parameter GUID.";
                return null;
            }

            var byGuid = manager.get_Parameter(parsedGuid);
            if (byGuid is null)
            {
                errorCode = "parameter_not_found";
                message = "No shared parameter with GUID " + guid + " in this family.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(name)
                && !string.Equals(byGuid.Definition.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "parameter_not_found";
                message = "The shared parameter with GUID " + guid + " is named '" +
                          byGuid.Definition.Name + "', not '" + name + "'.";
                return null;
            }

            if (wantInstance.HasValue && byGuid.IsInstance != wantInstance.Value)
            {
                errorCode = "scope_mismatch";
                message = "Parameter '" + byGuid.Definition.Name + "' is a " +
                          (byGuid.IsInstance ? "instance" : "type") + " parameter, but scope='" +
                          scope + "' was requested.";
                return null;
            }

            return byGuid;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errorCode = "invalid_arguments";
            message = "Pass parameter name or guid.";
            return null;
        }

        var byName = new List<FamilyParameter>();
        var matches = new List<FamilyParameter>();
        foreach (var parameter in manager.GetParameters())
        {
            if (!string.Equals(parameter.Definition.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            byName.Add(parameter);
            if (wantInstance.HasValue && parameter.IsInstance != wantInstance.Value)
                continue;
            matches.Add(parameter);
        }

        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
        {
            errorCode = "ambiguous_parameter";
            message = "Several parameters are named '" + name + "' (instance and type). " +
                      "Pass scope='instance' or scope='type'.";
            return null;
        }

        if (byName.Count > 0 && wantInstance.HasValue)
        {
            var actual = byName[0].IsInstance ? "instance" : "type";
            errorCode = "scope_mismatch";
            message = "The parameter '" + byName[0].Definition.Name + "' is a " + actual +
                      " parameter, but scope='" + scope + "' was requested.";
            return null;
        }

        errorCode = "parameter_not_found";
        message = "No parameter named '" + name + "' in this family. Use get_family_parameters " +
                  "to list the family parameters.";
        return null;
    }

    internal static FamilyType? ResolveType(
        RevitFamilyManager manager,
        string? typeName,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;
        var types = GetTypes(manager);

        if (types.Count == 0)
        {
            errorCode = "empty_family";
            message = "The family has no types. Create a type in the Family Editor before " +
                      "setting values or formulas.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            var current = manager.CurrentType;
            if (current is not null)
                return current;

            errorCode = "empty_family";
            message = "The family has no current type.";
            return null;
        }

        foreach (var type in types)
        {
            if (string.Equals(type.Name, typeName!.Trim(), StringComparison.OrdinalIgnoreCase))
                return type;
        }

        errorCode = "type_not_found";
        var known = new List<string>();
        for (var i = 0; i < types.Count && i < 20; i++)
            known.Add(types[i].Name);
        message = "No type named '" + typeName + "' in this family. Known types: " +
                  (known.Count == 0 ? "(none)" : string.Join(", ", known)) +
                  (types.Count > known.Count ? ", …" : string.Empty) + ".";
        return null;
    }

    internal static string? DisplayValue(FamilyType? type, FamilyParameter parameter)
    {
        if (type is null)
            return null;

        try
        {
            var value = type.AsValueString(parameter);
            if (value is not null)
                return value;
        }
        catch (Exception)
        {
            // Fall through to the raw storage value below.
        }

        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return type.AsString(parameter);
                case StorageType.Integer:
                    return type.AsInteger(parameter)?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.Double:
                    return type.AsDouble(parameter)?.ToString(
                        "R",
                        System.Globalization.CultureInfo.InvariantCulture);
                case StorageType.ElementId:
                    return type.AsElementId(parameter)?.GetValue().ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return null;
            }
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

    internal static FamilyParameterReport BuildParameterReport(
        FamilyParameter parameter,
        IReadOnlyList<FamilyType> types,
        bool includeValues,
        int maxValues)
    {
        var report = new FamilyParameterReport
        {
            Name = parameter.Definition.Name,
            Scope = parameter.IsInstance ? "instance" : "type",
            IsInstance = parameter.IsInstance,
            IsShared = parameter.IsShared,
            Guid = TryGetGuid(parameter),
            StorageType = AgentFamilySpecs.StorageTypeName(parameter.StorageType),
            Spec = AgentFamilySpecs.SpecName(parameter.Definition),
            Group = AgentFamilySpecs.GroupName(parameter.Definition),
            Formula = parameter.Formula,
            IsDeterminedByFormula = parameter.IsDeterminedByFormula,
            LookupTables = LookupTables(parameter),
            CanSetValue = !parameter.IsDeterminedByFormula && !parameter.IsReadOnly,
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
                values.Add(new FamilyTypeValueReport
                {
                    TypeName = type.Name,
                    Value = DisplayValue(type, parameter),
                    HasValue = HasValue(type, parameter)
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