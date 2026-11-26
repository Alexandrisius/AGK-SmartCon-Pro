using System.Globalization;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed partial class SetParametersJobAction : RevitJobActionBase
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
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules) ?? ValidateFlagShape(specJson);
        if (keyError is not null)
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);

        var spec = Parse<SetParametersSpec>(specJson);
        var specError = ValidateSpec(spec);
        if (specError is not null)
            return ExpansionFail(JobErrorCodes.InvalidSpec, specError);

        var warnings = new List<string>();
        var units = new List<JobUnit>();
        var scope = string.Equals(spec!.Scope, "type", StringComparison.OrdinalIgnoreCase) ? "type" : "instance";
        var parameterKey = ParameterKey(spec);
        var resolveName = string.IsNullOrWhiteSpace(spec.BuiltInParameter)
            ? spec.Parameter
            : spec.BuiltInParameter;
        var allowUnverified = spec.AllowUnverifiedClassification ?? false;

        if (spec.Values is { Count: > 0 })
        {
            // values[] form: several {select, value} groups in ONE job - one
            // preview and one apply instead of 2N calls (S.47, #532).
            for (var index = 0; index < spec.Values.Count; index++)
            {
                var entry = spec.Values[index];
                var remaining = MaxUnits - units.Count;
                if (remaining <= 0)
                {
                    warnings.Add("values[" + index + "] not expanded: the job already holds " + units.Count +
                                 " units (cap " + MaxUnits + "); put the rest in another job");
                    break;
                }

                var entryError = ExpandSelection(
                    context, spec, entry.Select!, entry.Value ?? string.Empty,
                    parameterKey, resolveName, scope, allowUnverified,
                    remaining, index, warnings, units);
                if (entryError is not null)
                    return ExpansionFail(entryError.Value.Code, "values[" + index + "]: " + entryError.Value.Detail);
            }
        }
        else
        {
            var singleError = ExpandSelection(
                context, spec, spec.Select!, spec.Value ?? string.Empty,
                parameterKey, resolveName, scope, allowUnverified,
                MaxUnits, null, warnings, units);
            if (singleError is not null)
                return ExpansionFail(singleError.Value.Code, singleError.Value.Detail);
        }

        if (units.Count == 0)
            return ExpansionFail(JobErrorCodes.SelectorEmpty,
                "The selector matched no elements; check the ids, level ids, category names and the " +
                "system/condition filters");
        if (string.Equals(scope, "type", StringComparison.Ordinal))
            warnings.Add("scope=type: each write changes the type element, so every instance of that type changes");
        if (allowUnverified)
            warnings.Add("allowUnverifiedClassification=true: an Assembly Code is written even when no row " +
                         "of the classification table loaded in the document backs it");
        return new JobExpansion { Units = units, Warnings = warnings };
    }

    /// <summary>
    /// One {select, value} pair of the spec (the classic form has exactly one;
    /// values[] has several). Appends units, reports per-pair problems through
    /// warnings; a broken selector returns a typed error for the whole expansion.
    /// </summary>
    private (string Code, string Detail)? ExpandSelection(
        JobActionContext context,
        SetParametersSpec spec,
        ParametersSelectSpec select,
        string value,
        string parameterKey,
        string? resolveName,
        string scope,
        bool allowUnverified,
        int cap,
        int? valuesIndex,
        List<string> warnings,
        List<JobUnit> units)
    {
        var where = valuesIndex.HasValue ? "values[" + valuesIndex.Value + "].select" : "select";
        if (!TryResolveCategories(context.Document, select, out var categoryIds, out var categoryError))
            return (JobErrorCodes.InvalidSpec, where + ": " + categoryError!);
        HashSet<long>? systemMemberIds = null;
        if (!string.IsNullOrWhiteSpace(select.SystemName))
        {
            if (!TrySelectSystemMembers(
                    context.Document, select, context.CancellationToken, out systemMemberIds, out var systemError))
                return ("system_not_found", where + ": " + systemError!);
        }

        var limit = select.Limit ?? int.MaxValue;
        var pairCap = Math.Min(limit, cap);
        var elements = Select(context, select, categoryIds, systemMemberIds, pairCap + 1, warnings);
        if (select.IncludeSubComponents == true)
            ExpandWithSubComponents(context.Document, elements, pairCap + 1, warnings, context.CancellationToken);
        if (elements.Count > pairCap)
        {
            if (limit > cap)
                return (JobErrorCodes.LimitExceeded,
                    where + " matches more than " + cap + " elements; narrow it or set limit");
            elements.RemoveRange(limit, elements.Count - limit);
            warnings.Add(where + ".limit truncated the selection to " + limit + " element(s)");
        }

        if (elements.Count == 0)
            return (JobErrorCodes.SelectorEmpty,
                where + " matched no elements; check the ids, level ids, category names and the " +
                "system/condition filters");

        elements.Sort((left, right) => left.Id.GetValue().CompareTo(right.Id.GetValue()));
        var missing = 0;
        foreach (var element in elements)
        {
            var resolution = AgentParameterWriteHelper.Resolve(
                context.Document, element, resolveName, spec.SharedParameterGuid, scope);
            if (!resolution.Ok)
                missing++;
            units.Add(new JobUnit
            {
                // The values[] form may aim several values at one element: the
                // entry index keeps the unit keys distinct.
                UnitKey = element.UniqueId + "|" + parameterKey + "|" + scope +
                          (valuesIndex.HasValue ? "|v" + valuesIndex.Value : string.Empty),
                Label = scope + " " + (resolution.Parameter?.Definition?.Name ?? parameterKey) + " = " +
                        TargetText(value, spec.Unit),
                ArgsJson = Json(new SetParameterUnitArgs
                {
                    ElementId = element.Id.GetValue(),
                    UniqueId = element.UniqueId,
                    OwnerUniqueId = resolution.Ok && resolution.IsTypeScope
                        ? resolution.Parameter!.Element?.UniqueId
                        : null,
                    Parameter = spec.Parameter,
                    SharedParameterGuid = spec.SharedParameterGuid,
                    BuiltInParameter = spec.BuiltInParameter,
                    Scope = scope,
                    Value = value,
                    Unit = spec.Unit,
                    ErrorCode = resolution.Ok ? null : resolution.ErrorCode,
                    Error = resolution.Ok ? null : resolution.Reason + " " + ResolutionHint,
                    AllowUnverifiedClassification = allowUnverified
                })
            });
        }

        if (missing > 0)
            warnings.Add((valuesIndex.HasValue ? "values[" + valuesIndex.Value + "]: " : string.Empty) +
                         missing + " selected element(s) do not resolve parameter '" + parameterKey +
                         "' for scope " + scope + "; those units fail in the preview");
        if (valuesIndex.HasValue)
            warnings.Add("values[" + valuesIndex.Value + "]: " + elements.Count +
                         " element(s) = '" + TargetText(value, spec.Unit) + "'");
        return null;
    }

    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        if (!TryPrepare(context, unit, out var args, out var parameter, out var code, out var detail))
            return Fail(code, detail);
        var classification = CheckClassification(context, parameter, args);
        if (!classification.Ok)
            return Fail(classification.ErrorCode!, classification.Reason!);
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
                    " → " + TargetText(args.Value, args.Unit),
            Warnings = classification.Unverified
                ? new[] { UnverifiedWarning(classification, args.Value) }
                : Array.Empty<string>()
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

        // The apply slices get their own context: the code is checked against the
        // table as it is now, never against the one the preview saw.
        var classification = CheckClassification(context, parameter, args);
        if (!classification.Ok)
            return ApplyFail(classification.ErrorCode!, classification.Reason! + " " + Hint("run the preview again"));

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
                return TryNormalizeDouble(text, args.Unit, name, parameter, out expectedRaw, out reason);
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
        Parameter parameter,
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
        var normalized = unit!.Trim().ToLowerInvariant();
        var unitType = normalized switch
        {
            "mm" or "millimeter" or "millimeters" => UnitTypeId.Millimeters,
            "cm" => UnitTypeId.Centimeters,
            "m" => UnitTypeId.Meters,
            "ft" or "foot" or "feet" => UnitTypeId.Feet,
            "in" or "inch" or "inches" => UnitTypeId.Inches,
            "m2" or "sqm" => UnitTypeId.SquareMeters,
            "m3" => UnitTypeId.CubicMeters,
            _ => null
        };
        if (unitType is null && normalized is not ("raw" or "internal"))
        {
            reason = "Unit '" + unit + "' is not supported for parameter '" + name + "' " +
                     Hint("pass mm, cm, m, ft, in, m2, m3 or raw");
            return false;
        }

        // A unit of the wrong dimension silently wrote a meaningless value (m2 for a
        // length); validate against the parameter spec before converting.
        if (unitType is not null)
        {
            ForgeTypeId? spec = null;
            try
            {
                spec = parameter.Definition?.GetDataType();
            }
            catch (Exception)
            {
                // No spec: the dimension cannot be checked.
            }

            if (spec is not null && !UnitUtils.IsMeasurableSpec(spec))
            {
                reason = "Parameter '" + name + "' is dimensionless; omit unit or pass unit=\"raw\".";
                return false;
            }

            if (spec is not null && !UnitUtils.IsValidUnit(spec, unitType))
            {
                reason = "Unit '" + unit + "' does not match parameter '" + name +
                         "' (spec " + spec.TypeId + ").";
                return false;
            }
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

}
