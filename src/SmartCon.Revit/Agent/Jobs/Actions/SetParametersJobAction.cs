using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed class SetParametersJobAction : RevitJobActionBase
{
    private const string ParameterNotFound = "parameter_not_found";
    private const string ReadOnlyParameter = "readonly_parameter";
    private const string InvalidValue = "invalid_value";
    private const string ResolutionHint = "[Action: check the parameter name/GUID and the scope, then run the preview again]";

    public override string Name => "set_parameters";
    public override JobActionScope Scope => JobActionScope.ProjectInstance;
    public override int MaxUnits => 5000;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var spec = Parse<SetParametersSpec>(specJson);
        var specError = ValidateSpec(spec);
        if (specError is not null)
            return ExpansionFail(JobErrorCodes.InvalidSpec, specError);
        var select = spec!.Select!;
        if (!TryResolveCategories(context.Document, select, out var categoryIds, out var categoryError))
            return ExpansionFail(JobErrorCodes.InvalidSpec, categoryError!);
        var limit = select.Limit ?? int.MaxValue;
        var cap = Math.Min(limit, MaxUnits);
        var warnings = new List<string>();
        var elements = Select(context, select, categoryIds, cap + 1, warnings);
        if (elements.Count > cap)
        {
            if (limit > MaxUnits)
                return ExpansionFail(JobErrorCodes.LimitExceeded, "The selector matches more than " + MaxUnits +
                    " elements; narrow select.* or set limit");
            elements.RemoveRange(limit, elements.Count - limit);
            warnings.Add("select.limit truncated the selection to " + limit + " element(s)");
        }
        if (elements.Count == 0)
            return ExpansionFail(JobErrorCodes.SelectorEmpty,
                "The selector matched no elements; check the ids, level ids and category names");
        elements.Sort((left, right) => left.Id.GetValue().CompareTo(right.Id.GetValue()));
        var scope = string.Equals(spec.Scope, "type", StringComparison.OrdinalIgnoreCase) ? "type" : "instance";
        var parameterKey = ParameterKey(spec);
        var resolveName = string.IsNullOrWhiteSpace(spec.BuiltInParameter)
            ? spec.Parameter
            : spec.BuiltInParameter;
        var units = new List<JobUnit>(elements.Count);
        var missing = 0;
        foreach (var element in elements)
        {
            var resolution = AgentParameterWriteHelper.Resolve(
                context.Document, element, resolveName, spec.Guid, scope);
            if (!resolution.Ok)
                missing++;
            units.Add(new JobUnit
            {
                UnitKey = element.UniqueId + "|" + parameterKey + "|" + scope,
                Label = scope + " " + (resolution.Parameter?.Definition?.Name ?? parameterKey) + " = " +
                        TargetText(spec.Value, spec.Unit),
                ArgsJson = Json(new SetParameterUnitArgs
                {
                    ElementId = element.Id.GetValue(),
                    UniqueId = element.UniqueId,
                    OwnerUniqueId = resolution.Ok && resolution.IsTypeScope
                        ? resolution.Parameter!.Element?.UniqueId
                        : null,
                    Parameter = spec.Parameter,
                    Guid = spec.Guid,
                    BuiltInParameter = spec.BuiltInParameter,
                    Scope = scope,
                    Value = spec.Value,
                    Unit = spec.Unit,
                    ErrorCode = resolution.Ok ? null : resolution.ErrorCode,
                    Error = resolution.Ok ? null : resolution.Reason + " " + ResolutionHint
                })
            });
        }
        if (missing > 0)
            warnings.Add(missing + " selected element(s) do not resolve parameter '" + parameterKey +
                         "' for scope " + scope + "; those units fail in the preview");
        if (string.Equals(scope, "type", StringComparison.Ordinal) && units.Count > missing)
            warnings.Add("scope=type: each write changes the type element, so every instance of that type changes");
        return new JobExpansion { Units = units, Warnings = warnings };
    }

    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return Fail(code, detail);
        var name = NameOf(parameter, args);
        if (parameter.IsReadOnly)
            return Fail(ReadOnlyParameter, "Parameter '" + name + "' is read-only " + Hint("choose a writable parameter"));
        if (!TryNormalizeTarget(parameter, args, out var expectedRaw, out var invalidReason))
            return Fail(InvalidValue, invalidReason);
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        return new JobUnitPreview
        {
            Skipped = RawMatches(parameter.StorageType, currentRaw, expectedRaw),
            ExpectedJson = Value(expectedRaw),
            ActualJson = Value(currentRaw),
            UndoJson = Value(currentRaw),
            Label = name + ": " + AgentParameterWriteHelper.Describe(parameter) +
                    " → " + TargetText(args.Value, args.Unit)
        };
    }

    public override JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return ApplyFail(code, detail);
        var expectedRaw = ReadValue(preview.ExpectedJson);
        if (expectedRaw is null)
        {
            if (!TryNormalizeTarget(parameter, args, out var normalized, out var invalidReason))
                return ApplyFail(InvalidValue, invalidReason);
            expectedRaw = normalized;
        }
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        if (RawMatches(parameter.StorageType, currentRaw, expectedRaw))
        {
            return new JobUnitApply
            {
                Success = true,
                Skipped = true,
                ExpectedJson = Value(expectedRaw),
                UndoJson = string.IsNullOrWhiteSpace(preview.UndoJson) ? Value(currentRaw) : preview.UndoJson
            };
        }

        var previewActual = ReadValue(preview.ActualJson);
        if (previewActual is not null && !RawMatches(parameter.StorageType, currentRaw, previewActual))
        {
            return ApplyFail(
                JobErrorCodes.PreviewStale,
                JobErrors.PreviewStale(unit.UnitKey).ToMessage() +
                " (the parameter now reads '" + currentRaw + "')");
        }

        var undoJson = string.IsNullOrWhiteSpace(preview.UndoJson) ? Value(currentRaw) : preview.UndoJson!;
        persistUndo?.Invoke(undoJson);
        try
        {
            if (!AgentParameterWriteHelper.TrySet(
                    parameter, args.Value, args.Unit, out var errorCode, out var reason))
            {
                return ApplyFail(errorCode, reason + " " + Hint("fix the value/unit and run the preview again"));
            }
        }
        catch (Exception ex)
        {
            return ApplyFail("set_failed", "Writing parameter '" + NameOf(parameter, args) + "' failed: " +
                ex.GetType().Name + ": " + ex.Message + " " + Hint("check that the parameter accepts this value"));
        }
        return new JobUnitApply { Success = true, ExpectedJson = Value(expectedRaw), UndoJson = undoJson };
    }

    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return VerifyFail(code, detail, detail);
        var expectedRaw = ReadValue(preview.ExpectedJson);
        if (expectedRaw is null)
            return VerifyFail(JobErrorCodes.InvalidSpec, "The preview has no expected value",
                "The preview has no expected value " + Hint("run the preview again"));
        var actualRaw = AgentParameterWriteHelper.RawValue(parameter);
        var verified = RawMatches(parameter.StorageType, actualRaw, expectedRaw);
        return new JobUnitVerification
        {
            Verified = verified,
            ActualJson = Value(actualRaw),
            MismatchReason = verified
                ? null
                : "Parameter '" + NameOf(parameter, args) + "' holds '" + actualRaw + "', the preview expected '" +
                  expectedRaw + "' " + Hint("run get_job_report, then rollback or retry with a fresh preview")
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var target = ReadValue(undoJson);
        if (target is null)
            return UndoFail(JobErrorCodes.RollbackUnavailable, "No recorded previous value " +
                Hint("restore this value from a backup or manually"));
        if (!TryPrepare(context, unit, out var args, out var parameter, out _, out var detail))
            return UndoFail(JobErrorCodes.RollbackFailed, detail + " " + Hint("restore this value manually"));
        var currentRaw = AgentParameterWriteHelper.RawValue(parameter);
        if (RawMatches(parameter.StorageType, currentRaw, target))
            return new JobUnitUndo { Success = true, Skipped = true, ActualJson = Value(currentRaw) };
        if (dryRun)
            return new JobUnitUndo { Success = true, ActualJson = Value(currentRaw) };
        try
        {
            if (!AgentParameterWriteHelper.TrySet(parameter, target, "raw", out _, out var reason))
            {
                return UndoFail(JobErrorCodes.RollbackFailed, "Restoring '" + target + "' on parameter '" +
                    NameOf(parameter, args) + "' failed: " + reason + " " + Hint("restore this value manually"));
            }
        }
        catch (Exception ex)
        {
            return UndoFail(JobErrorCodes.RollbackFailed, "Restoring '" + target + "' failed: " +
                ex.GetType().Name + ": " + ex.Message + " " + Hint("restore this value manually"));
        }
        return new JobUnitUndo { Success = true, ActualJson = Value(target) };
    }

    private static bool TryPrepare(
        JobActionContext context,
        JobUnit unit,
        [NotNullWhen(true)] out SetParameterUnitArgs? args,
        [NotNullWhen(true)] out Parameter? parameter,
        out string errorCode,
        out string errorDetail)
    {
        args = Parse<SetParameterUnitArgs>(unit.ArgsJson);
        parameter = null;
        errorCode = JobErrorCodes.InvalidSpec;
        errorDetail = "Unit arguments are unreadable " + Hint("run the preview again");
        if (args is null)
            return false;
        if (!string.IsNullOrWhiteSpace(args.Error))
        {
            errorCode = args.ErrorCode ?? ParameterNotFound;
            errorDetail = args.Error!;
            return false;
        }
        var uniqueId = string.IsNullOrWhiteSpace(args.OwnerUniqueId) ? args.UniqueId : args.OwnerUniqueId;
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            errorDetail = "The unit has no element UniqueId " + Hint("run the preview again");
            return false;
        }
        if (!TryFindByUniqueId<Element>(context.Document, uniqueId!, out var element, out var findError))
        {
            errorCode = findError!.Code;
            errorDetail = findError.ToMessage();
            return false;
        }
        var resolution = AgentParameterWriteHelper.Resolve(
            context.Document, element!,
            string.IsNullOrWhiteSpace(args.BuiltInParameter) ? args.Parameter : args.BuiltInParameter,
            args.Guid, args.Scope);
        if (!resolution.Ok)
        {
            errorCode = resolution.ErrorCode ?? ParameterNotFound;
            errorDetail = (resolution.Reason ?? "The parameter could not be resolved") + " " + ResolutionHint;
            return false;
        }
        parameter = resolution.Parameter!;
        errorCode = errorDetail = string.Empty;
        return true;
    }

    // Preview must not touch the model, so the post-write raw value is computed with the
    // same unit table AgentParameterWriteHelper.TrySet uses; Verify compares RawValue to it.
    private static bool TryNormalizeTarget(
        Parameter parameter,
        SetParameterUnitArgs args,
        out string expectedRaw,
        out string reason)
    {
        expectedRaw = string.Empty;
        reason = string.Empty;
        var text = args.Value ?? string.Empty;
        var name = parameter.Definition?.Name ?? "?";
        switch (parameter.StorageType)
        {
            case StorageType.String:
                expectedRaw = text;
                return true;
            case StorageType.Integer:
                if (bool.TryParse(text, out var flag))
                    text = flag ? "1" : "0";
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                {
                    reason = "'" + text + "' is not a valid integer for parameter '" + name + "' " +
                             Hint("pass an integer or true/false");
                    return false;
                }
                expectedRaw = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            case StorageType.Double:
                return TryNormalizeDouble(text, args.Unit, name, out expectedRaw, out reason);
            case StorageType.ElementId:
                if (string.IsNullOrWhiteSpace(text) ||
                    string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
                {
                    expectedRaw = "-1";
                    return true;
                }
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    reason = "'" + text + "' is not a valid element id for parameter '" + name + "' " +
                             Hint("pass a numeric element id, none, or an empty string");
                    return false;
                }
                expectedRaw = id.ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                reason = "Parameter '" + name + "' has storage type " + parameter.StorageType + " and cannot be written " +
                         Hint("choose a writable parameter type");
                return false;
        }
    }

    private static bool TryNormalizeDouble(
        string text,
        string? unit,
        string name,
        out string expectedRaw,
        out string reason)
    {
        expectedRaw = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(unit))
        {
            reason = "Numeric value '" + text + "' for parameter '" + name + "' needs an explicit unit " +
                     Hint("pass mm, cm, m, ft, m2, m3, or raw for a dimensionless parameter");
            return false;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            reason = "'" + text + "' is not a valid number for parameter '" + name + "' " + Hint("pass a number");
            return false;
        }
        var normalized = unit.Trim().ToLowerInvariant();
        var unitType = normalized switch
        {
            "mm" or "millimeter" or "millimeters" => UnitTypeId.Millimeters,
            "cm" => UnitTypeId.Centimeters,
            "m" => UnitTypeId.Meters,
            "m2" or "sqm" => UnitTypeId.SquareMeters,
            "m3" => UnitTypeId.CubicMeters,
            _ => null
        };
        if (unitType is null && normalized is not ("ft" or "raw" or "internal"))
        {
            reason = "Unit '" + unit + "' is not supported for parameter '" + name + "' " +
                     Hint("pass mm, cm, m, ft, m2, m3 or raw");
            return false;
        }
        var raw = unitType is null ? number : UnitUtils.ConvertToInternalUnits(number, unitType);
        expectedRaw = raw.ToString("R", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool RawMatches(StorageType storageType, string actual, string? expected)
    {
        if (expected is null)
            return false;
        if (storageType != StorageType.Double)
            return string.Equals(actual, expected, StringComparison.Ordinal);
        if (!double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var actualNumber) ||
            !double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var expectedNumber))
            return false;
        return !double.IsNaN(actualNumber) && !double.IsNaN(expectedNumber) &&
               Math.Abs(actualNumber - expectedNumber) <= Math.Max(1e-12, Math.Abs(expectedNumber) * 1e-9);
    }

    private static List<Element> Select(
        JobActionContext context,
        ParametersSelectSpec select,
        List<ElementId> categoryIds,
        int maxCount,
        List<string> warnings)
    {
        var document = context.Document;
        var levels = new HashSet<long>(select.LevelIds ?? new List<long>());
        var categories = new HashSet<long>(categoryIds.ConvertAll(id => id.GetValue()));
        var result = new List<Element>();
        if (select.ElementIds is { Count: > 0 })
        {
            var skipped = 0;
            foreach (var id in new HashSet<long>(select.ElementIds))
            {
                var element = document.GetElement(ElementIdCompat.Create(id));
                if (element is null || element is ElementType ||
                    !Matches(document, element, select, levels, categories))
                {
                    skipped++;
                    continue;
                }
                result.Add(element);
                if (result.Count >= maxCount)
                    break;
            }
            if (skipped > 0)
                warnings.Add(skipped + " explicit element id(s) are missing or point to a type; skipped");
            return result;
        }
        FilteredElementCollector collector = new FilteredElementCollector(document);
        collector = collector.WhereElementIsNotElementType();
        if (categories.Count > 0)
            collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds));
        if (!string.IsNullOrWhiteSpace(select.FamilyName))
            collector = collector.OfClass(typeof(FamilyInstance));
        var scanned = 0;
        foreach (var element in collector)
        {
            if (++scanned % 500 == 0)
                context.CancellationToken.ThrowIfCancellationRequested();
            if (!Matches(document, element, select, levels, categories))
                continue;
            result.Add(element);
            if (result.Count >= maxCount)
                break;
        }
        return result;
    }

    private static bool Matches(
        Document document,
        Element element,
        ParametersSelectSpec select,
        HashSet<long> levelIds,
        HashSet<long> categoryIds)
    {
        var categoryId = element.Category?.Id;
        if (categoryIds.Count > 0 && (categoryId is null || !categoryIds.Contains(categoryId.GetValue())))
            return false;
        if (levelIds.Count > 0 && !MatchesLevel(element, levelIds))
            return false;
        var (familyName, typeName) = ReadFamilyAndType(document, element);
        if (!string.IsNullOrWhiteSpace(select.FamilyName) &&
            !string.Equals(familyName, select.FamilyName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(select.TypeName) &&
            !string.Equals(typeName, select.TypeName, StringComparison.OrdinalIgnoreCase))
            return false;
        return string.IsNullOrWhiteSpace(select.NameContains) ||
               (element.Name ?? string.Empty).IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) >= 0 ||
               (typeName ?? string.Empty).IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool MatchesLevel(Element element, HashSet<long> levelIds)
    {
        var levelId = element.LevelId;
        if (levelId == ElementId.InvalidElementId)
        {
            var parameter = element.get_Parameter(BuiltInParameter.LEVEL_PARAM);
            if (parameter is { StorageType: StorageType.ElementId } && parameter.HasValue)
                levelId = parameter.AsElementId();
        }
        return levelId != ElementId.InvalidElementId && levelIds.Contains(levelId.GetValue());
    }

    private static (string? Family, string? Type) ReadFamilyAndType(Document document, Element element)
    {
        if (element is FamilyInstance instance)
            return (instance.Symbol?.Family?.Name, instance.Symbol?.Name);
        var typeId = element.GetTypeId();
        return typeId == ElementId.InvalidElementId
            ? (null, null)
            : (null, document.GetElement(typeId)?.Name);
    }

    private static bool TryResolveCategories(
        Document document,
        ParametersSelectSpec select,
        out List<ElementId> categoryIds,
        out string? error)
    {
        var resolved = new List<ElementId>();
        categoryIds = resolved;
        error = null;
        var seen = new HashSet<long>();
        var unknown = new List<string>();
        void Add(ElementId id)
        {
            if (seen.Add(id.GetValue()))
                resolved.Add(id);
        }
        try
        {
            foreach (var id in select.CategoryIds ?? new List<long>())
                Add(ElementIdCompat.Create(id));
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        foreach (var name in select.CategoryNames ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (Enum.TryParse<BuiltInCategory>(name, true, out var builtIn))
            {
                Add(new ElementId(builtIn));
                continue;
            }
            var match = document.Settings.Categories
                .Cast<Category>()
                .FirstOrDefault(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                unknown.Add(name);
            else
                Add(match.Id);
        }
        if (unknown.Count == 0)
            return true;
        error = "Unknown category name(s): " + string.Join(", ", unknown) + " " +
                Hint("pass a BuiltInCategory id (OST_...) or an exact category name from the model");
        return false;
    }

    private static string? ValidateSpec(SetParametersSpec? spec)
    {
        if (spec?.Select is null)
            return "set_parameters requires {\"select\": {...}, \"value\": \"...\"}";
        var select = spec.Select;
        if (select.ElementIds is not { Count: > 0 } && select.CategoryIds is not { Count: > 0 } &&
            select.CategoryNames is not { Count: > 0 } && select.LevelIds is not { Count: > 0 } &&
            string.IsNullOrWhiteSpace(select.FamilyName) && string.IsNullOrWhiteSpace(select.TypeName) &&
            string.IsNullOrWhiteSpace(select.NameContains))
        {
            return "select must contain at least one of elementIds, categoryIds, categoryNames, levelIds, " +
                   "familyName, typeName or nameContains; an empty selector is refused";
        }
        if (select.Limit is < 1)
            return "select.limit must be a positive number";
        if (spec.Value is null)
            return "set_parameters requires a string 'value'; pass \"\" to clear a text parameter";
        if (string.IsNullOrWhiteSpace(spec.Parameter) && string.IsNullOrWhiteSpace(spec.Guid) &&
            string.IsNullOrWhiteSpace(spec.BuiltInParameter))
            return "set_parameters requires 'parameter', 'guid' or 'builtInParameter'";
        if (!string.IsNullOrWhiteSpace(spec.Guid) && !Guid.TryParse(spec.Guid, out _))
            return "'" + spec.Guid + "' is not a valid shared-parameter GUID";
        if (!string.IsNullOrWhiteSpace(spec.Scope) &&
            !string.Equals(spec.Scope, "instance", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(spec.Scope, "type", StringComparison.OrdinalIgnoreCase))
            return "scope must be \"instance\" or \"type\", not '" + spec.Scope + "'";
        return null;
    }

    private static string ParameterKey(SetParametersSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.Guid) && Guid.TryParse(spec.Guid, out var guid))
            return guid.ToString("D");
        if (!string.IsNullOrWhiteSpace(spec.BuiltInParameter))
            return spec.BuiltInParameter!.Trim();
        return (spec.Parameter ?? string.Empty).Trim();
    }

    private static string NameOf(Parameter parameter, SetParameterUnitArgs args) =>
        parameter.Definition?.Name ?? args.Parameter ?? args.BuiltInParameter ?? args.Guid ?? "?";
    private static string TargetText(string? value, string? unit) =>
        string.IsNullOrWhiteSpace(unit) ? value ?? string.Empty : (value ?? string.Empty) + " " + unit;
    private static string Hint(string action) => "[Action: " + action + "]";

    private static JobUnitApply ApplyFail(string code, string detail) =>
        new() { ErrorCode = code, ErrorDetail = detail };
    private static JobUnitVerification VerifyFail(string code, string detail, string reason) =>
        new() { Verified = false, ErrorCode = code, ErrorDetail = detail, MismatchReason = reason };
    private static JobUnitUndo UndoFail(string code, string detail) =>
        new() { ErrorCode = code, ErrorDetail = detail };

    public sealed class SetParametersSpec
    {
        public ParametersSelectSpec? Select { get; set; }
        public string? Parameter { get; set; }
        public string? Guid { get; set; }
        public string? BuiltInParameter { get; set; }
        public string? Scope { get; set; }
        public string? Value { get; set; }
        public string? Unit { get; set; }
    }

    public sealed class ParametersSelectSpec
    {
        public List<long>? ElementIds { get; set; }
        public List<long>? CategoryIds { get; set; }
        public List<string>? CategoryNames { get; set; }
        public List<long>? LevelIds { get; set; }
        public string? FamilyName { get; set; }
        public string? TypeName { get; set; }
        public string? NameContains { get; set; }
        public int? Limit { get; set; }
    }

    public sealed class SetParameterUnitArgs
    {
        public long ElementId { get; set; }
        public string? UniqueId { get; set; }
        public string? OwnerUniqueId { get; set; }
        public string? Parameter { get; set; }
        public string? Guid { get; set; }
        public string? BuiltInParameter { get; set; }
        public string? Scope { get; set; }
        public string? Value { get; set; }
        public string? Unit { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }
    }
}
